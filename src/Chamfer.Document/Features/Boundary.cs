using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>How a boundary feature leaves its first profile or arrives at its last.</summary>
public enum BoundaryTangency
{
    /// <summary>Whatever the smooth interpolation through the profiles gives.</summary>
    None,

    /// <summary>Perpendicular to the profile's sketch plane.</summary>
    NormalToProfile,
}

/// <summary>
/// A boundary boss, cut or surface: a smooth shape through profile sketches in order, with control
/// over how it leaves the first profile and meets the last (normal to the profile, with a tangent length).
/// </summary>
public sealed class BoundaryFeature : Feature
{
    public override string DefaultName => IsSurface ? "Boundary-Surface" : IsCut ? "Cut-Boundary" : "Boundary";

    public List<string> ProfileSketchIds { get; set; } = [];

    public bool IsCut { get; set; }

    /// <summary>Make an open surface through the profiles' outlines instead of a solid.</summary>
    public bool IsSurface { get; set; }

    public bool Merge { get; set; } = true;

    public BoundaryTangency StartTangency { get; set; } = BoundaryTangency.NormalToProfile;

    public BoundaryTangency EndTangency { get; set; } = BoundaryTangency.NormalToProfile;

    /// <summary>How strongly the tangency pulls (1 is the default; larger runs straighter for longer).</summary>
    public double StartTangentLength { get; set; } = 1;

    public double EndTangentLength { get; set; } = 1;

    public override IEnumerable<string> Dependencies => ProfileSketchIds;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ProfileSketchIds.Count < 2)
        {
            throw new FeatureException("Select at least two profile sketches.");
        }

        if (StartTangentLength <= 0 || EndTangentLength <= 0)
        {
            throw new FeatureException("Tangent lengths must be greater than zero.");
        }

        var owned = new List<Shape>();
        try
        {
            var profiles = new List<(Shape Face, Vec3 Normal, Vec3 Center)>();
            Dictionary<int, int>? firstMap = null;
            var key = 0;
            foreach (var id in ProfileSketchIds)
            {
                var sketch = ctx.Sketch(id);
                var region = ProfileShapes.Regions(sketch).OrderByDescending(r => Math.Abs(r.Area)).FirstOrDefault()
                    ?? throw new FeatureException("Each profile sketch needs a closed outline.");
                var face = ProfileShapes.Face(sketch.Plane, region with { Holes = [] }, out var map);
                owned.Add(face);
                profiles.Add((face, sketch.Plane.Normal, face.Bounds.Center));
                if (firstMap is null)
                {
                    firstMap = map;
                    key = region.Key;
                }
            }

            // Guide sections just off the end profiles, along their normals, make the smooth
            // interpolation leave and arrive perpendicular to them.
            var sections = profiles.Select(p => p.Face).ToList();
            if (StartTangency == BoundaryTangency.NormalToProfile)
            {
                sections.Insert(1, Offset(profiles[0], profiles[1].Center, StartTangentLength, owned));
            }

            if (EndTangency == BoundaryTangency.NormalToProfile)
            {
                sections.Insert(sections.Count - 1, Offset(profiles[^1], profiles[^2].Center, EndTangentLength, owned));
            }

            using var op = SweepFeature.Run(() => Modeling.Loft(sections, solid: !IsSurface, ruled: false),
                "The boundary can't be built. Profiles should be similar shapes, in order, and not intersect; try a shorter tangent length.",
                requireSolid: !IsSurface);
            if (IsSurface)
            {
                Surfaces.AddBody(ctx, op.Result, Id);
                return;
            }

            var solid = op.Result;
            var names = ProfileShapes.NameSweep(op, solid, sections[0], firstMap!, Id, "boundary", key);
            AdvancedShapes.Apply(ctx, Id, [(solid, names)], IsCut, Merge);
        }
        finally
        {
            foreach (var s in owned)
            {
                s.Dispose();
            }
        }
    }

    /// <summary>A copy of a profile moved along its normal, towards its neighbour, by a fraction of the gap.</summary>
    internal static Shape Offset((Shape Face, Vec3 Normal, Vec3 Center) profile, Vec3 neighbour, double length, List<Shape> owned)
    {
        var gap = neighbour - profile.Center;
        var along = Vec3.Dot(gap, profile.Normal);
        if (Math.Abs(along) < 1e-6)
        {
            throw new FeatureException("A profile with a normal tangency can't have its neighbour in its own plane.");
        }

        // A guide closer than the next profile, so the shape stays ordered.
        var distance = Math.Min(0.2 * length, 0.45) * along;
        using var op = Modeling.Transform(profile.Face, Transform3.Translation(profile.Normal * distance));
        var moved = op.Result;
        owned.Add(moved);
        return moved;
    }
}
