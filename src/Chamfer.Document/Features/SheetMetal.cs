using Chamfer.Document.Profiles;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Operations;
using Chamfer.Sketching.Serialization;
using SkVec2 = Chamfer.Sketching.Vec2;

namespace Chamfer.Document.Features;

/// <summary>
/// Sheet metal parameters and the flat pattern built up by sheet metal
/// features. Flat regions and bend lines are in <see cref="FlatPlane"/>
/// coordinates; the flat solid is those regions extruded by <see cref="FlatExtrusion"/>.
/// </summary>
public sealed record SheetMetalState(
    double Thickness,
    double BendRadius,
    double KFactor,
    Plane3 FlatPlane,
    Vec3 FlatExtrusion,
    IReadOnlyList<IReadOnlyList<Point2>> Regions,
    IReadOnlyList<IReadOnlyList<Point2>> Holes,
    IReadOnlyList<(Point2 A, Point2 B)> BendLines,
    bool AcceptsFlanges)
{
    /// <summary>For a flat base plate: the feature whose tool solid is the exact flat base (regions then start with the flaps).</summary>
    public string? BaseToolId { get; init; }

    /// <summary>How many of <see cref="Regions"/> belong to the base (the rest are flange flaps).</summary>
    public int BaseRegionCount { get; init; }

    /// <summary>Length of the neutral fibre through a bend of <paramref name="angle"/> radians.</summary>
    public double BendAllowance(double angle) => angle * (BendRadius + (KFactor * Thickness));
}

/// <summary>
/// The first sheet metal feature. A closed sketch makes a flat plate; an open
/// sketch makes a bent sheet of <see cref="Depth"/> with rounded bends of
/// <see cref="BendRadius"/> (inside radius) at every corner.
/// </summary>
public sealed class BaseFlangeFeature : Feature
{
    public override string DefaultName => "Base-Flange";

    public string SketchId { get; set; } = string.Empty;

    public double Thickness { get; set; } = 1;

    /// <summary>Inside bend radius.</summary>
    public double BendRadius { get; set; } = 1;

    /// <summary>Neutral-axis position as a fraction of the thickness from the inside (0–0.5).</summary>
    public double KFactor { get; set; } = 0.44;

    /// <summary>Width of a sheet bent from an open profile.</summary>
    public double Depth { get; set; } = 20;

    /// <summary>Put the thickness on the other side of the sketch.</summary>
    public bool Reverse { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Thickness <= 0 || BendRadius < 0 || KFactor < 0 || KFactor > 1)
        {
            throw new FeatureException("Thickness must be positive, the bend radius not negative and the K-factor between 0 and 1.");
        }

        var sketch = ctx.Sketch(SketchId);
        var plane = sketch.Plane;
        var dir = plane.Normal * (Reverse ? -1 : 1);
        var closed = ProfileBuilder.Build(sketch.Sketch).Regions;
        if (closed.Count > 0)
        {
            Plate(ctx, sketch, closed, dir);
        }
        else
        {
            Bent(ctx, sketch, dir);
        }
    }

    private void Plate(RebuildContext ctx, PlacedSketch sketch, IReadOnlyList<ProfileRegion> regions, Vec3 dir)
    {
        var pieces = new List<(Shape, string[])>();
        foreach (var region in regions)
        {
            using var face = ProfileShapes.Face(sketch.Plane, region, out var map);
            using var op = Modeling.Extrude(face, dir * Thickness);
            var solid = op.Result;
            pieces.Add((solid, ProfileShapes.NameSweep(op, solid, face, map, Id, "side", region.Key)));
        }

        var (tool, names) = ProfileShapes.Combine(pieces);
        ctx.Tools[Id] = new FeatureTool(tool, names, IsCut: false);
        ctx.AddMaterial(tool, names, merge: true);

        static IReadOnlyList<Point2> Poly(ProfileLoop loop) => [.. loop.Polygon.Select(p => new Point2(p.X, p.Y))];
        ctx.SheetMetal = new SheetMetalState(Thickness, BendRadius, KFactor, sketch.Plane, dir * Thickness,
            [.. regions.Select(r => Poly(r.Outer))], [.. regions.SelectMany(r => r.Holes).Select(Poly)], [], AcceptsFlanges: true) { BaseToolId = Id, BaseRegionCount = regions.Count };
    }

    private void Bent(RebuildContext ctx, PlacedSketch sketch, Vec3 dir)
    {
        var copy = SketchSerializer.Deserialize(SketchSerializer.Serialize(sketch.Sketch));
        foreach (var c in copy.Constraints.ToList())
        {
            copy.RemoveConstraint(c);
        }

        var chain = ProfileBuilder.Chain(copy) ?? throw new FeatureException("A base flange needs a closed profile, or one open chain of lines.");
        if (chain.Closed || chain.Segments.Any(s => s.Kind != EntityKind.Line))
        {
            throw new FeatureException("An open base flange profile must be lines only.");
        }

        // The sheet lies to the left of the chain (right when reversed). At each corner the
        // inside of the bend gets the bend radius, so the chain's own radius depends on the turn.
        var side = Reverse ? -1.0 : 1.0;
        var segs = chain.Segments;
        var straights = segs.Select(s => SkVec2.Distance(s.Start, s.End)).ToList();
        var bends = new List<double>();
        for (var i = 0; i + 1 < segs.Count; i++)
        {
            var a = (segs[i].End - segs[i].Start).Normalized();
            var b = (segs[i + 1].End - segs[i + 1].Start).Normalized();
            var turn = Math.Atan2(SkVec2.Cross(a, b), SkVec2.Dot(a, b));
            if (Math.Abs(turn) < 1e-9)
            {
                bends.Add(0);
                continue;
            }

            var insideIsSheet = Math.Sign(turn) == Math.Sign(side);
            var radius = insideIsSheet ? BendRadius + Thickness : BendRadius;
            var setback = radius * Math.Tan(Math.Abs(turn) / 2);
            straights[i] -= setback;
            straights[i + 1] -= setback;
            bends.Add(Math.Abs(turn));
            if (radius > 1e-9 && SketchOperations.FindCorner(copy, segs[i].End, 1e-4) is { } corner)
            {
                try
                {
                    SketchOperations.Fillet(copy, corner.A, corner.B, radius);
                }
                catch (ArgumentException ex)
                {
                    throw new FeatureException("A segment is too short for the bend radius.", ex);
                }
            }
        }

        if (straights.Any(l => l <= 0))
        {
            throw new FeatureException("A segment is too short for the bend radius.");
        }

        foreach (var c in copy.Constraints.ToList())
        {
            copy.RemoveConstraint(c);
        }

        var bentChain = ProfileBuilder.Chain(copy)!;
        var curves = bentChain.Value.Segments.Select(s => copy.FindEntity(s.EntityId)!).ToList();
        try
        {
            var offsets = SketchOperations.Offset(copy, curves, Thickness, reverse: Reverse);
            var first = bentChain.Value.Segments[0];
            var last = bentChain.Value.Segments[^1];
            var ends = offsets.SelectMany(e => e.Points).Where(p => p.Owner is SketchLine).Select(p => p.Position).ToList();
            copy.AddLine(first.Start, ends.OrderBy(p => SkVec2.Distance(p, first.Start)).First());
            copy.AddLine(last.End, ends.OrderBy(p => SkVec2.Distance(p, last.End)).First());
        }
        catch (ArgumentException ex)
        {
            throw new FeatureException(ex.Message, ex);
        }

        foreach (var c in copy.Constraints.ToList())
        {
            copy.RemoveConstraint(c);
        }

        var regions = ProfileBuilder.Build(copy).Regions;
        if (regions.Count == 0)
        {
            throw new FeatureException("The bent sheet couldn't be formed.");
        }

        var placed = new PlacedSketch(sketch.Plane, copy);
        var pieces = new List<(Shape, string[])>();
        foreach (var region in regions)
        {
            using var face = ProfileShapes.Face(placed.Plane, region, out var map);
            using var op = Modeling.Extrude(face, dir * Depth);
            var solid = op.Result;
            pieces.Add((solid, ProfileShapes.NameSweep(op, solid, face, map, Id, "side", region.Key)));
        }

        var (tool, names) = ProfileShapes.Combine(pieces);
        ctx.Tools[Id] = new FeatureTool(tool, names, IsCut: false);
        ctx.AddMaterial(tool, names, merge: true);

        // Flat pattern: the straights and bend allowances laid end to end, Depth wide, in the
        // plane of the first segment.
        var s0 = sketch.Plane.ToWorld(segs[0].Start.X, segs[0].Start.Y);
        var s1 = sketch.Plane.ToWorld(segs[0].End.X, segs[0].End.Y);
        var x = (s1 - s0).Normalized();
        var flat = new Plane3(s0, Vec3.Cross(x, dir).Normalized() * side, x);
        var bendLines = new List<(Point2, Point2)>();
        double u = 0;
        var flatY = Vec3.Dot(dir, flat.YDirection) >= 0 ? 1.0 : -1.0;
        var thicknessDir = Vec3.Cross(sketch.Plane.Normal, x).Normalized() * side;
        var state = new SheetMetalState(Thickness, BendRadius, KFactor, flat, thicknessDir * Thickness, [], [], [], AcceptsFlanges: false);
        for (var i = 0; i < straights.Count; i++)
        {
            u += straights[i];
            if (i < bends.Count && bends[i] > 0)
            {
                var ba = state.BendAllowance(bends[i]);
                bendLines.Add((new Point2(u + (ba / 2), 0), new Point2(u + (ba / 2), Depth * flatY)));
                u += ba;
            }
        }

        IReadOnlyList<Point2> rect = [new(0, 0), new(u, 0), new(u, Depth * flatY), new(0, Depth * flatY)];
        ctx.SheetMetal = state with { Regions = [rect], BendLines = bendLines };
    }
}

/// <summary>
/// A flange bent up from a straight edge of the sheet: a bend of the sheet's
/// radius through <see cref="Angle"/>, then a straight leg of <see cref="Length"/>.
/// </summary>
public sealed class EdgeFlangeFeature : Feature
{
    public override string DefaultName => "Edge-Flange";

    public List<TopoRef> Edges { get; set; } = [];

    /// <summary>Length of the straight leg after the bend.</summary>
    public double Length { get; set; } = 10;

    /// <summary>Bend angle in degrees.</summary>
    public double Angle { get; set; } = 90;

    /// <summary>Bend towards the other face of the sheet.</summary>
    public bool Reverse { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sm = ctx.SheetMetal ?? throw new FeatureException("Add a base flange first.");
        if (Edges.Count == 0)
        {
            throw new FeatureException("Select the edges to put flanges on.");
        }

        if (Length <= 0 || Angle <= 0 || Angle >= 180)
        {
            throw new FeatureException("The length must be positive and the angle between 0 and 180 degrees.");
        }

        var a = Angle * Math.PI / 180;
        var regions = new List<IReadOnlyList<Point2>>(sm.Regions);
        var bendLines = new List<(Point2, Point2)>(sm.BendLines);

        // Resolve every edge first: adding a flange renames the faces around the others.
        var targets = Targets(ctx, Edges);

        foreach (var target in targets)
        {
            Bend(ctx, sm, target, a, sm.BendRadius, Length, Reverse, Id, regions, bendLines);
        }

        ctx.SheetMetal = sm with { Regions = regions, BendLines = bendLines };
    }

    /// <summary>Resolves flange edges to the edge and its sheet and side faces.</summary>
    internal static List<(EdgeInfo Info, FaceInfo Sheet, FaceInfo Side)> Targets(RebuildContext ctx, IEnumerable<TopoRef> edges) =>
        [.. edges.Select(e =>
        {
            var (body, index) = ctx.ResolveEdge(e);
            var info = body.Edge(index);
            if (info.Curve != CurveKind.Line)
            {
                throw new FeatureException("Flanges need straight edges.");
            }

            var faces = body.Shape.FacesOfEdge(index).Select(body.Face).Where(f => f.Surface == SurfaceKind.Plane).OrderByDescending(f => f.Area).ToList();
            if (faces.Count < 2)
            {
                throw new FeatureException("Pick an edge between the sheet's face and its thickness.");
            }

            return (info, faces[0], faces[1]);
        })];

    /// <summary>
    /// Adds a bent flange of <paramref name="length"/> at a <paramref name="radius"/> through
    /// <paramref name="a"/> radians on one edge, and its flap to the flat pattern.
    /// </summary>
    internal static void Bend(RebuildContext ctx, SheetMetalState sm, (EdgeInfo Info, FaceInfo Sheet, FaceInfo Side) target,
        double a, double radius, double length, bool reverse, string id,
        List<IReadOnlyList<Point2>> regions, List<(Point2, Point2)> bendLines)
    {
        var (info, sheetFace, sideFace) = target;
        var t = sm.Thickness;
        var r = radius;
        var d = (info.End - info.Start).Normalized();
        var u = sideFace.Normal.Normalized();
        var v = sheetFace.Normal.Normalized();
        var sigma = reverse ? -1.0 : 1.0;
        var w = v * sigma;
        var b0 = reverse ? t : 0;
        var c = new Point2(0, b0 + r);
        var radial = new Point2(Math.Sin(a), -Math.Cos(a));
        var tangent = new Point2(Math.Cos(a), Math.Sin(a));
        var pi0 = new Point2(0, b0);
        var pi1 = c + (radial * r);
        var po0 = new Point2(0, b0 - t);
        var po1 = c + (radial * (r + t));
        var pi2 = pi1 + (tangent * length);
        var po2 = po1 + (tangent * length);

        var segments = new List<ProfileSegment>();
        if (r > 1e-9)
        {
            segments.Add(ProfileSegment.Arc(c.X, c.Y, pi0.X, pi0.Y, pi1.X, pi1.Y, ccw: true));
        }

        segments.Add(ProfileSegment.Line(pi1.X, pi1.Y, pi2.X, pi2.Y));
        segments.Add(ProfileSegment.Line(pi2.X, pi2.Y, po2.X, po2.Y));
        segments.Add(ProfileSegment.Line(po2.X, po2.Y, po1.X, po1.Y));
        segments.Add(ProfileSegment.Arc(c.X, c.Y, po1.X, po1.Y, po0.X, po0.Y, ccw: false));
        segments.Add(ProfileSegment.Line(po0.X, po0.Y, pi0.X, pi0.Y));

        // The edge is the profile's origin; the sheet spans w in [-t, 0], or [0, t] when bending the other way.
        var frame = new Plane3(info.Start, Vec3.Cross(u, w), u);
        using var face = Modeling.Face(frame, [segments]);
        using var op = Modeling.Extrude(face, d * info.Length);
        var tool = op.Result;
        var names = Naming.Finish(tool, new string?[tool.FaceCount], id);
        ctx.AddMaterial(tool, names, merge: true);

        // Flat pattern: a flap of bend allowance plus leg, beyond the edge in the flat plane.
        if (sm.AcceptsFlanges && Math.Abs(Math.Abs(Vec3.Dot(v, sm.FlatPlane.Normal)) - 1) < 1e-6)
        {
            var (e0x, e0y) = sm.FlatPlane.ToLocal(info.Start);
            var (e1x, e1y) = sm.FlatPlane.ToLocal(info.End);
            var out2 = new Point2(Vec3.Dot(u, sm.FlatPlane.XDirection), Vec3.Dot(u, sm.FlatPlane.YDirection)).Normalized();
            var ba = a * (r + (sm.KFactor * t));
            var e0 = new Point2(e0x, e0y);
            var e1 = new Point2(e1x, e1y);
            regions.Add([e0, e1, e1 + (out2 * (ba + length)), e0 + (out2 * (ba + length))]);
            bendLines.Add((e0 + (out2 * (ba / 2)), e1 + (out2 * (ba / 2))));
        }
        else
        {
            ctx.Warn("This flange isn't on the base face, so it's left out of the flat pattern.");
        }
    }
}

/// <summary>
/// Folds an edge back on itself through 180°: a closed hem (the leg lies on the sheet with a tiny gap)
/// or an open one with a set gap.
/// </summary>
public sealed class HemFeature : Feature
{
    public override string DefaultName => "Hem";

    public List<TopoRef> Edges { get; set; } = [];

    /// <summary>Length of the folded-back leg.</summary>
    public double Length { get; set; } = 6;

    /// <summary>Gap between the sheet and the leg (0 makes a closed hem).</summary>
    public double Gap { get; set; }

    public bool Reverse { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sm = ctx.SheetMetal ?? throw new FeatureException("Add a base flange first.");
        if (Edges.Count == 0)
        {
            throw new FeatureException("Select the edges to hem.");
        }

        if (Length <= 0 || Gap < 0)
        {
            throw new FeatureException("The length must be positive and the gap not negative.");
        }

        // A closed hem keeps a hair of clearance so the leg and sheet stay separate solids faces.
        var radius = Math.Max(Gap, sm.Thickness * 0.02) / 2;
        var regions = new List<IReadOnlyList<Point2>>(sm.Regions);
        var bendLines = new List<(Point2, Point2)>(sm.BendLines);
        foreach (var target in EdgeFlangeFeature.Targets(ctx, Edges))
        {
            EdgeFlangeFeature.Bend(ctx, sm, target, Math.PI, radius, Length, Reverse, Id, regions, bendLines);
        }

        ctx.SheetMetal = sm with { Regions = regions, BendLines = bendLines };
    }
}

/// <summary>Adds flat material of the sheet's thickness from a closed sketch on the sheet face (a tab).</summary>
public sealed class TabFeature : Feature
{
    public override string DefaultName => "Tab";

    public string SketchId { get; set; } = string.Empty;

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sm = ctx.SheetMetal ?? throw new FeatureException("Add a base flange first.");
        var sketch = ctx.Sketch(SketchId);
        var plane = sketch.Plane;
        if (Math.Abs(Math.Abs(Vec3.Dot(plane.Normal, sm.FlatPlane.Normal)) - 1) > 1e-6)
        {
            throw new FeatureException("Sketch the tab on a face of the base sheet.");
        }

        // Extrude into the sheet: from the flat plane along the thickness, or back from the opposite face.
        var offset = sm.FlatPlane.SignedDistance(plane.Origin);
        var far = Vec3.Dot(sm.FlatExtrusion, sm.FlatPlane.Normal);
        var dir = Math.Abs(offset) < 1e-6 ? sm.FlatExtrusion
            : Math.Abs(offset - far) < 1e-6 ? -sm.FlatExtrusion
            : throw new FeatureException("Sketch the tab on a face of the base sheet.");

        var regions = new List<IReadOnlyList<Point2>>(sm.Regions);
        foreach (var region in ProfileShapes.Regions(sketch))
        {
            using var face = ProfileShapes.Face(plane, region, out var entityOfEdge);
            using var op = Modeling.Extrude(face, dir);
            var solid = op.Result;
            var names = ProfileShapes.NameSweep(op, solid, face, entityOfEdge, Id, "side", region.Key);
            ctx.AddMaterial(solid, names, merge: true);

            // The outline in flat-plane coordinates (arcs sampled).
            var outline = region.Outer.Segments.SelectMany(seg => seg.Sample()).Select(p =>
            {
                var (x, y) = sm.FlatPlane.ToLocal(plane.ToWorld(p.X, p.Y));
                return new Point2(x, y);
            }).ToList();
            regions.Add(outline);
        }

        ctx.SheetMetal = sm with { Regions = regions };
    }
}

/// <summary>
/// Bends a flat sheet along a sketched line: one side stays, the other folds through the angle
/// around a bend of the sheet's radius. The flat pattern keeps its outline and gains a bend line.
/// </summary>
public sealed class SketchedBendFeature : Feature
{
    public override string DefaultName => "Sketched Bend";

    /// <summary>A sketch on the sheet's face with one straight line: the bend line.</summary>
    public string SketchId { get; set; } = string.Empty;

    /// <summary>Bend angle in degrees.</summary>
    public double Angle { get; set; } = 90;

    /// <summary>Fold the other side of the line.</summary>
    public bool FlipSide { get; set; }

    /// <summary>Fold toward the sheet's other face.</summary>
    public bool Reverse { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sm = ctx.SheetMetal ?? throw new FeatureException("Add a base flange first.");
        if (Angle <= 0 || Angle >= 180)
        {
            throw new FeatureException("The bend angle must be between 0 and 180 degrees.");
        }

        var sketch = ctx.Sketch(SketchId);
        var line = sketch.Sketch.Entities.OfType<SketchLine>().FirstOrDefault(l => !l.IsConstruction && l.Length > 1e-9)
            ?? throw new FeatureException("Sketch one straight line where the sheet should bend.");
        var body = ctx.Bodies.FirstOrDefault(b => !b.IsSurface) ?? throw new FeatureException("There is no sheet to bend.");

        // Frame: n = sheet thickness direction (bottom face to top), d along the line, u toward the moving side,
        // w the side the sheet folds to.
        var t = sm.Thickness;
        var r = sm.BendRadius;
        var a = Angle * Math.PI / 180;
        var n = sm.FlatExtrusion.Normalized();
        var bottom = sm.FlatPlane.Origin;
        var l0 = sketch.Plane.ToWorld(line.Start.X, line.Start.Y);
        var l1 = sketch.Plane.ToWorld(line.End.X, line.End.Y);
        var d = (l1 - l0).Normalized();
        var u = Vec3.Cross(n, d).Normalized() * (FlipSide ? -1 : 1);
        var w = Reverse ? -n : n;
        var p0 = l0 - (n * Vec3.Dot(l0 - bottom, n)); // the line, dropped onto the bottom face
        var ba = sm.BendAllowance(a);

        // The face on the outside of the bend, where the bend starts, and the bend axis r + t inside it.
        var outer = Reverse ? p0 + (n * t) : p0;
        var start = outer - (u * (ba / 2));
        var axis = start + (w * (r + t));
        var axisDir = Vec3.Cross(u, w).Normalized();

        var size = (body.Shape.Bounds.Diagonal * 2) + 10;
        using var fixedPart = Side(body.Shape, p0 - (u * (ba / 2)), -u, size);
        using var movingPart = Side(body.Shape, p0 + (u * (ba / 2)), u, size);

        // Where the sheet crosses the bend start: intervals along the line, from the section's bottom edges.
        using var section = Modeling.Section(body.Shape, start, u);
        var intervals = new List<(double From, double To)>();
        for (var i = 1; i <= section.EdgeCount; i++)
        {
            var e = section.Edge(i);
            if (Math.Abs(Vec3.Dot((e.End - e.Start).Normalized(), d)) < 0.999 || Math.Abs(Vec3.Dot(e.Start - bottom, n)) > 1e-6)
            {
                continue;
            }

            var s0 = Vec3.Dot(e.Start - start, d);
            var s1 = Vec3.Dot(e.End - start, d);
            intervals.Add((Math.Min(s0, s1), Math.Max(s0, s1)));
        }

        if (intervals.Count == 0)
        {
            throw new FeatureException("The bend line must cross the sheet.");
        }

        // The bend zone: the cross-section (outer face to inner face, along w) revolved about the axis.
        var pieces = new List<Shape> { fixedPart };
        try
        {
            foreach (var (from, to) in intervals)
            {
                var frame = new Plane3(outer - (u * (ba / 2)) + (d * from), -u, d);
                var sign = Vec3.Dot(frame.YDirection, w) > 0 ? 1 : -1;
                var len = to - from;
                List<ProfileSegment> rect =
                [
                    ProfileSegment.Line(0, 0, len, 0),
                    ProfileSegment.Line(len, 0, len, sign * t),
                    ProfileSegment.Line(len, sign * t, 0, sign * t),
                    ProfileSegment.Line(0, sign * t, 0, 0),
                ];
                using var face = Modeling.Face(frame, [rect]);
                using var rev = Modeling.Revolve(face, axis, axisDir, a);
                pieces.Add(rev.Result);
            }

            using var moved = Modeling.Transform(movingPart, Transform3.Rotation(axis, axisDir, a) * Transform3.Translation(-u * ba));
            pieces.Add(moved.Result);

            var result = pieces[0];
            foreach (var piece in pieces.Skip(1))
            {
                using var fuse = Modeling.Boolean(result, piece, BooleanKind.Fuse);
                result = fuse.Result;
            }

            var names = Naming.Finish(result, new string?[result.FaceCount], Id);
            ctx.Bodies[ctx.Bodies.IndexOf(body)] = new Body(result, names, body.Name, body.Color);
        }
        finally
        {
            foreach (var p in pieces.Skip(1))
            {
                p.Dispose();
            }
        }

        // Flat pattern: same outline, plus a bend line where the sheet crosses the sketched line.
        var bendLines = new List<(Point2, Point2)>(sm.BendLines);
        foreach (var (from, to) in intervals)
        {
            var (ax, ay) = sm.FlatPlane.ToLocal(p0 + (d * (from + Vec3.Dot(start - p0, d))));
            var (bx, by) = sm.FlatPlane.ToLocal(p0 + (d * (to + Vec3.Dot(start - p0, d))));
            bendLines.Add((new Point2(ax, ay), new Point2(bx, by)));
        }

        ctx.SheetMetal = sm with { BendLines = bendLines };
    }

    /// <summary>The part of a shape on one side of a plane.</summary>
    private static Shape Side(Shape shape, Vec3 origin, Vec3 keep, double size)
    {
        using var tool = Modeling.PlaneFace(new Plane3(origin, keep, keep.AnyPerpendicular()), size);
        using var split = Modeling.Split(shape, tool);
        var whole = split.Result;
        var kept = new List<Shape>();
        for (var i = 1; i <= whole.SolidCount; i++)
        {
            var piece = whole.Sub(TopoKind.Solid, i);
            if (Vec3.Dot(piece.MassProperties.CenterOfMass - origin, keep) > 0)
            {
                kept.Add(piece);
            }
            else
            {
                piece.Dispose();
            }
        }

        return kept.Count switch
        {
            0 => throw new FeatureException("The bend line must cross the sheet."),
            1 => kept[0],
            _ => Modeling.Compound(kept),
        };
    }
}

/// <summary>
/// A forming tool: round dimples pressed into the sheet at each sketch point (a spherical bump on
/// the sketch face, hollow behind). Formed features don't flatten; the flat pattern ignores them.
/// </summary>
public sealed class DimpleFeature : Feature
{
    public override string DefaultName => "Dimple";

    /// <summary>A sketch on a sheet face; each sketch point is a dimple centre.</summary>
    public string SketchId { get; set; } = string.Empty;

    public double Diameter { get; set; } = 10;

    public double Height { get; set; } = 2;

    /// <summary>Press toward the other side of the sheet.</summary>
    public bool Reverse { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sm = ctx.SheetMetal ?? throw new FeatureException("Add a base flange first.");
        if (Diameter <= 0 || Height <= 0 || Height > Diameter / 2)
        {
            throw new FeatureException("The diameter must be positive and the height at most half of it.");
        }

        var sketch = ctx.Sketch(SketchId);
        var points = sketch.Sketch.Entities.OfType<SketchPoint>().Where(p => p.Owner is null && !ReferenceEquals(p, sketch.Sketch.Origin)).ToList();
        if (points.Count == 0)
        {
            throw new FeatureException("Put sketch points where the dimples go.");
        }

        // The sheet's two faces along its thickness direction; the bump rises from the sketch face.
        var n = sm.FlatExtrusion.Normalized();
        var t = sm.Thickness;
        var bottom = sm.FlatPlane.Origin;
        var r = Diameter / 2;
        var radius = ((r * r) + (Height * Height)) / (2 * Height);
        foreach (var p in points)
        {
            var at = sketch.Plane.ToWorld(p.X, p.Y);
            var onTop = Math.Abs(Vec3.Dot(at - bottom, n) - t) < Math.Abs(Vec3.Dot(at - bottom, n));
            var up = (onTop ? n : -n) * (Reverse ? -1 : 1);

            // Faces the bump grows from (front) and the cavity starts at (back).
            var frontLevel = Reverse ? !onTop : onTop;
            var center2 = at - (n * Vec3.Dot(at - bottom, n));
            var front = center2 + (frontLevel ? n * t : Vec3.Zero);
            var back = center2 + (frontLevel ? Vec3.Zero : n * t);
            using var outer = Cap(front, up, r, Height, radius);
            using var inner = Cap(back, up, r, Height, radius);
            var outerNames = Naming.Finish(outer, new string?[outer.FaceCount], Id);
            ctx.AddMaterial(outer, outerNames, merge: true);
            var innerNames = Naming.Finish(inner, new string?[inner.FaceCount], Id + ":in");
            ctx.RemoveMaterial(inner, innerNames);
        }
    }

    /// <summary>A spherical cap of base radius r and height h on a plane through <paramref name="at"/>, rising along <paramref name="up"/>.</summary>
    private static Shape Cap(Vec3 at, Vec3 up, double r, double h, double sphereRadius)
    {
        using var sphere = Modeling.Sphere(sphereRadius);
        using var placed = Modeling.Transform(sphere, Transform3.Translation(at + (up * (h - sphereRadius))));
        var plane = new Plane3(at, up, up.AnyPerpendicular());
        using var disc = Modeling.Face(plane, [[ProfileSegment.Circle(0, 0, r)]]);
        using var prism = Modeling.Extrude(disc, up * h);
        using var cap = Modeling.Boolean(placed.Result, prism.Result, BooleanKind.Common);
        return cap.Result;
    }
}

/// <summary>Builds the flat pattern of a sheet metal part.</summary>
public static class FlatPattern
{
    /// <summary>The flat blank as a solid (regions fused, extruded by the thickness, base cuts applied).</summary>
    public static Shape Solid(PartDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var sm = doc.State.SheetMetal ?? throw new InvalidOperationException("The part has no sheet metal.");
        Shape? flat = null;
        var baseTool = sm.BaseToolId is { } id && doc.State.Tools.TryGetValue(id, out var t) ? t.Shape : null;
        if (baseTool is not null)
        {
            using var copy = Modeling.Transform(baseTool, Transform3.Identity);
            flat = copy.Result;
        }

        // With an exact base, the base's own outlines are already in it; only the flaps remain.
        foreach (var region in sm.Regions.Skip(baseTool is null ? 0 : sm.BaseRegionCount))
        {
            var loops = new List<IReadOnlyList<ProfileSegment>> { Loop(region) };
            using var face = Modeling.Face(sm.FlatPlane, loops);
            using var op = Modeling.Extrude(face, sm.FlatExtrusion);
            var piece = op.Result;
            if (flat is null)
            {
                flat = piece;
            }
            else
            {
                using var fuse = Modeling.Boolean(flat, piece, BooleanKind.Fuse);
                flat = fuse.Result;
            }
        }

        if (flat is null)
        {
            throw new InvalidOperationException("The flat pattern is empty.");
        }

        foreach (var hole in baseTool is null ? sm.Holes : [])
        {
            using var face = Modeling.Face(sm.FlatPlane, [Loop(hole)]);
            using var op = Modeling.Extrude(face, sm.FlatExtrusion);
            using var cut = Modeling.Boolean(flat, op.Result, BooleanKind.Cut);
            flat = cut.Result;
        }

        // Cuts made after the flange go through the flat region they cut in the folded part.
        foreach (var f in doc.Features)
        {
            if (f is not BaseFlangeFeature && doc.State.Tools.TryGetValue(f.Id, out var tool) && tool.IsCut)
            {
                using var cut = Modeling.Boolean(flat, tool.Shape, BooleanKind.Cut);
                flat = cut.Result;
            }
        }

        return flat;
    }

    /// <summary>Total flat size (bounding box in the flat plane).</summary>
    public static (double Width, double Height) Size(PartDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var sm = doc.State.SheetMetal ?? throw new InvalidOperationException("The part has no sheet metal.");
        var pts = sm.Regions.SelectMany(r => r).ToList();
        return (pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));
    }

    private static List<ProfileSegment> Loop(IReadOnlyList<Point2> poly)
    {
        var segs = new List<ProfileSegment>();
        for (var i = 0; i < poly.Count; i++)
        {
            var p = poly[i];
            var q = poly[(i + 1) % poly.Count];
            if (Point2.Distance(p, q) > 1e-9)
            {
                segs.Add(ProfileSegment.Line(p.X, p.Y, q.X, q.Y));
            }
        }

        return segs;
    }
}
