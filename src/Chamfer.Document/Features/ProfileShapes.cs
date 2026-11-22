using Chamfer.Document.Profiles;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching.Entities;

namespace Chamfer.Document.Features;

/// <summary>Turns sketch profile regions into kernel faces and names the solids swept from them.</summary>
internal static class ProfileShapes
{
    public static IReadOnlyList<ProfileRegion> Regions(PlacedSketch sketch)
    {
        var result = ProfileBuilder.Build(sketch.Sketch);
        return result.Regions.Count == 0
            ? throw new FeatureException("The sketch has no closed contours.")
            : result.Regions;
    }

    public static ProfileSegment ToKernel(LoopSegment s) => s.Kind switch
    {
        EntityKind.Line => ProfileSegment.Line(s.Start.X, s.Start.Y, s.End.X, s.End.Y),
        EntityKind.Arc => ProfileSegment.Arc(s.Center.X, s.Center.Y, s.Start.X, s.Start.Y, s.End.X, s.End.Y, s.Ccw),
        EntityKind.Circle => ProfileSegment.Circle(s.Center.X, s.Center.Y, s.Radius),
        _ => throw new FeatureException($"Unsupported profile curve {s.Kind}."),
    };

    /// <summary>A planar face for a region, plus the sketch entity id of each of its edges (by 1-based edge index).</summary>
    public static Shape Face(Plane3 plane, ProfileRegion region, out Dictionary<int, int> entityOfEdge)
    {
        var loops = new List<IReadOnlyList<LoopSegment>> { region.Outer.Segments };
        loops.AddRange(region.Holes.Select(h => h.Segments));
        var face = Modeling.Face(plane, [.. loops.Select(l => (IReadOnlyList<ProfileSegment>)[.. l.Select(ToKernel)])], out var map);
        entityOfEdge = [];
        var segs = loops.SelectMany(l => l).ToList();
        for (var i = 0; i < segs.Count; i++)
        {
            entityOfEdge.TryAdd(map[i], segs[i].EntityId);
        }

        return face;
    }

    /// <summary>
    /// Names the faces of a solid swept from a profile face: side faces after
    /// the sketch entity that generated them, caps after the region.
    /// </summary>
    public static string[] NameSweep(Operation op, Shape solid, Shape face, Dictionary<int, int> entityOfEdge, string featureId, string sideTag, int regionKey)
    {
        var names = new string?[solid.FaceCount];
        foreach (var (edge, entity) in entityOfEdge)
        {
            foreach (var f in op.Generated(face, TopoKind.Edge, edge, TopoKind.Face))
            {
                names[f - 1] ??= $"{featureId}:{sideTag}:{entity}";
            }
        }

        foreach (var f in op.StartCaps)
        {
            names[f - 1] ??= $"{featureId}:start:{regionKey}";
        }

        foreach (var f in op.EndCaps)
        {
            names[f - 1] ??= $"{featureId}:end:{regionKey}";
        }

        return Naming.Finish(solid, names, featureId);
    }

    /// <summary>Combines per-region solids into one tool shape with concatenated face names.</summary>
    public static (Shape Shape, string[] Names) Combine(List<(Shape Shape, string[] Names)> pieces)
    {
        if (pieces.Count == 1)
        {
            return pieces[0];
        }

        var compound = Modeling.Compound([.. pieces.Select(p => p.Shape)]);
        var names = new string[compound.FaceCount];
        foreach (var (shape, pieceNames) in pieces)
        {
            for (var i = 1; i <= shape.FaceCount; i++)
            {
                using var sub = shape.Sub(TopoKind.Face, i);
                var index = compound.IndexOf(TopoKind.Face, sub);
                if (index > 0)
                {
                    names[index - 1] = pieceNames[i - 1];
                }
            }
        }

        return (compound, names);
    }
}
