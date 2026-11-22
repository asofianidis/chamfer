using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;
using Chamfer.Sketching.Entities;

namespace Chamfer.Document;

/// <summary>A sketch placed in 3D.</summary>
public sealed record PlacedSketch(Plane3 Plane, Sketch Sketch);

/// <summary>
/// The solid a boss or cut feature added or removed, kept so patterns and
/// mirrors can repeat it.
/// </summary>
public sealed record FeatureTool(Shape Shape, string[] FaceNames, bool IsCut);

/// <summary>The model state between two features. Immutable once captured.</summary>
public sealed record RebuildState(
    IReadOnlyList<Body> Bodies,
    IReadOnlyDictionary<string, PlacedSketch> Sketches,
    IReadOnlyDictionary<string, Plane3> Planes,
    IReadOnlyDictionary<string, FeatureTool> Tools)
{
    /// <summary>Sheet metal parameters and flat-pattern data, once a base flange exists.</summary>
    public Features.SheetMetalState? SheetMetal { get; init; }

    /// <summary>3D curves (helices and the like) by feature id, usable as sweep paths.</summary>
    public IReadOnlyDictionary<string, Shape> Curves { get; init; } = new Dictionary<string, Shape>();

    /// <summary>Reference axes by feature id.</summary>
    public IReadOnlyDictionary<string, (Vec3 Origin, Vec3 Direction)> Axes { get; init; } = new Dictionary<string, (Vec3, Vec3)>();

    /// <summary>Reference points by feature id.</summary>
    public IReadOnlyDictionary<string, Vec3> Points { get; init; } = new Dictionary<string, Vec3>();

    /// <summary>Coordinate systems by feature id (local to model).</summary>
    public IReadOnlyDictionary<string, Transform3> Frames { get; init; } = new Dictionary<string, Transform3>();

    public static RebuildState Initial { get; } = new([], new Dictionary<string, PlacedSketch>(),
        new Dictionary<string, Plane3>
        {
            ["front"] = Plane3.Front,
            ["top"] = Plane3.Top,
            ["right"] = Plane3.Right,
        }, new Dictionary<string, FeatureTool>());
}

/// <summary>Mutable state a feature reads from and writes to while it executes.</summary>
public sealed class RebuildContext
{
    internal RebuildContext(PartDocument document, RebuildState state, Feature feature)
    {
        Document = document;
        Feature = feature;
        Bodies = [.. state.Bodies];
        Sketches = new Dictionary<string, PlacedSketch>(state.Sketches);
        Planes = new Dictionary<string, Plane3>(state.Planes);
        Tools = new Dictionary<string, FeatureTool>(state.Tools);
        SheetMetal = state.SheetMetal;
        Curves = new Dictionary<string, Shape>(state.Curves);
        Axes = new Dictionary<string, (Vec3, Vec3)>(state.Axes);
        Points = new Dictionary<string, Vec3>(state.Points);
        Frames = new Dictionary<string, Transform3>(state.Frames);
    }

    public PartDocument Document { get; }
    public Feature Feature { get; }
    public List<Body> Bodies { get; }
    public Dictionary<string, PlacedSketch> Sketches { get; }
    public Dictionary<string, Plane3> Planes { get; }
    public Dictionary<string, FeatureTool> Tools { get; }
    public List<string> Warnings { get; } = [];

    internal RebuildState Capture() => new([.. Bodies], new Dictionary<string, PlacedSketch>(Sketches),
        new Dictionary<string, Plane3>(Planes), new Dictionary<string, FeatureTool>(Tools)) {
        SheetMetal = SheetMetal,
        Curves = new Dictionary<string, Shape>(Curves),
        Axes = new Dictionary<string, (Vec3, Vec3)>(Axes),
        Points = new Dictionary<string, Vec3>(Points),
        Frames = new Dictionary<string, Transform3>(Frames),
    };

    /// <summary>Reference axes by feature id.</summary>
    public Dictionary<string, (Vec3 Origin, Vec3 Direction)> Axes { get; }

    /// <summary>Reference points by feature id.</summary>
    public Dictionary<string, Vec3> Points { get; }

    /// <summary>Coordinate systems by feature id.</summary>
    public Dictionary<string, Transform3> Frames { get; }

    /// <summary>A reference point by feature id.</summary>
    public Vec3 ResolvePoint(string id) =>
        Points.TryGetValue(id, out var p) ? p : throw new FeatureException($"Reference point '{id}' is missing.");

    /// <summary>3D curves by feature id.</summary>
    public Dictionary<string, Shape> Curves { get; }

    /// <summary>Sheet metal state; features replace it rather than mutate it.</summary>
    public Features.SheetMetalState? SheetMetal { get; set; }

    public void Warn(string message) => Warnings.Add(message);

    public PlacedSketch Sketch(string id) =>
        Sketches.TryGetValue(id, out var s) ? s : throw new FeatureException("The sketch this feature uses is missing or failed.");

    /// <summary>Bounding box of all bodies.</summary>
    public Box3 ModelBounds => Bodies.Aggregate(Box3.Empty, (b, body) => b.Union(body.Shape.Bounds));

    /// <summary>A length that reaches through the whole model from <paramref name="from"/>.</summary>
    public double ThroughAllLength(Vec3 from)
    {
        var box = ModelBounds;
        return box.IsEmpty ? 1000 : (2 * box.Diagonal) + (2 * Vec3.Distance(from, box.Center)) + 10;
    }

    public Plane3 ResolvePlane(PlaneRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.PlaneId is { } id)
        {
            return Planes.TryGetValue(id, out var p) ? p : throw new FeatureException($"Reference plane '{id}' is missing.");
        }

        if (r.Face is { } face)
        {
            var (body, index) = ResolveFace(face);
            return body.Shape.FacePlane(index) ?? throw new FeatureException("The selected face is not planar.");
        }

        throw new FeatureException("No plane selected.");
    }

    public (Body Body, int Index) ResolveFace(TopoRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        foreach (var b in Bodies)
        {
            if (b.HasFace(r.Name))
            {
                return (b, b.ResolveFace(r, out _));
            }
        }

        var nearest = Nearest(r, (b, k) => b.Face(k).Centroid, b => b.FaceCount);
        Warn($"Face reference '{r.Name}' was lost; used the nearest face instead.");
        return nearest;
    }

    public (Body Body, int Index) ResolveEdge(TopoRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        foreach (var b in Bodies)
        {
            if (b.HasEdge(r.Name))
            {
                return (b, b.ResolveEdge(r, out _));
            }
        }

        var nearest = Nearest(r, (b, k) => b.Edge(k).Midpoint, b => b.EdgeCount);
        Warn($"Edge reference '{r.Name}' was lost; used the nearest edge instead.");
        return nearest;
    }

    private (Body Body, int Index) Nearest(TopoRef r, Func<Body, int, Vec3> position, Func<Body, int> count)
    {
        (Body? Body, int Index, double D) best = (null, 0, double.PositiveInfinity);
        foreach (var b in Bodies)
        {
            for (var k = 1; k <= count(b); k++)
            {
                var d = Vec3.Distance(position(b, k), r.Hint);
                if (d < best.D)
                {
                    best = (b, k, d);
                }
            }
        }

        return best.Body is null ? throw new FeatureException($"Reference '{r.Name}' can't be found.") : (best.Body, best.Index);
    }

    /// <summary>Resolves an axis or direction to a point and unit direction.</summary>
    public (Vec3 Origin, Vec3 Direction) ResolveAxis(AxisRef r)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (r.AxisId is { } axisId)
        {
            return Axes.TryGetValue(axisId, out var axis) ? axis : throw new FeatureException($"Reference axis '{axisId}' is missing.");
        }

        if (r.StandardAxis is { } std)
        {
            return std switch
            {
                "x" => (Vec3.Zero, Vec3.UnitX),
                "y" => (Vec3.Zero, Vec3.UnitY),
                "z" => (Vec3.Zero, Vec3.UnitZ),
                _ => throw new FeatureException($"Unknown axis '{std}'."),
            };
        }

        if (r.Edge is { } edgeRef)
        {
            var (body, index) = ResolveEdge(edgeRef);
            var e = body.Edge(index);
            if (e.Curve == CurveKind.Line)
            {
                return (e.Start, (e.End - e.Start).Normalized());
            }

            if (e.Curve == CurveKind.Circle)
            {
                // Axis of a circular edge: normal of the circle's plane.
                var n = Vec3.Cross(e.Start - e.CenterOrDirection, e.Midpoint - e.CenterOrDirection);
                if (n.LengthSquared < 1e-18)
                {
                    n = body.Face(body.Shape.FacesOfEdge(index)[0]).AxisDirection;
                }

                return (e.CenterOrDirection, n.Normalized());
            }

            throw new FeatureException("The selected edge is neither straight nor circular.");
        }

        if (r.Face is { } faceRef)
        {
            var (body, index) = ResolveFace(faceRef);
            var f = body.Face(index);
            return f.Surface switch
            {
                SurfaceKind.Cylinder or SurfaceKind.Cone => (f.AxisOrigin, f.AxisDirection.Normalized()),
                SurfaceKind.Plane => (f.Centroid, f.Normal),
                _ => throw new FeatureException("The selected face has no axis."),
            };
        }

        if (r.PlaneNormal is { } planeRef)
        {
            var p = ResolvePlane(planeRef);
            return (p.Origin, p.Normal);
        }

        if (r.SketchId is { } sketchId)
        {
            var s = Sketch(sketchId);
            if (s.Sketch.FindEntity(r.SketchEntityId) is SketchLine line)
            {
                var a = s.Plane.ToWorld(line.Start.Position.X, line.Start.Position.Y);
                var b = s.Plane.ToWorld(line.End.Position.X, line.End.Position.Y);
                return (a, (b - a).Normalized());
            }

            throw new FeatureException("The axis line in the sketch is missing.");
        }

        throw new FeatureException("No axis selected.");
    }

    // ------------------------------------------------------------ combining

    /// <summary>Adds material: fuses the tool into the bodies it touches, or adds a new body.</summary>
    public void AddMaterial(Shape tool, string[] toolFaceNames, bool merge)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(toolFaceNames);
        var toolBox = tool.Bounds;
        var targets = merge ? Bodies.Where(b => Overlaps(b.Shape.Bounds, toolBox)).ToList() : [];
        if (targets.Count == 0)
        {
            Bodies.Add(new Body(tool, toolFaceNames, NewBodyName()));
            return;
        }

        var current = new Naming.Input(tool, toolFaceNames);
        foreach (var target in targets)
        {
            using var op = Modeling.Boolean(target.Shape, current.Shape, BooleanKind.Fuse);
            var names = Naming.Transfer(op, op.Result, [new Naming.Input(target.Shape, target.FaceNames, target.EdgeNames), current], Feature.Id);
            current = new Naming.Input(op.Result, names);
        }

        var first = targets[0];
        Bodies[Bodies.IndexOf(first)] = new Body(current.Shape, current.FaceNames, first.Name, first.Color);
        foreach (var other in targets.Skip(1))
        {
            Bodies.Remove(other);
        }
    }

    /// <summary>Removes material from every body the tool touches.</summary>
    public void RemoveMaterial(Shape tool, string[] toolFaceNames)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(toolFaceNames);
        var toolBox = tool.Bounds;
        var touched = false;
        for (var i = 0; i < Bodies.Count; i++)
        {
            var body = Bodies[i];
            if (!Overlaps(body.Shape.Bounds, toolBox))
            {
                continue;
            }

            using var op = Modeling.Boolean(body.Shape, tool, BooleanKind.Cut);
            var result = op.Result;
            if (result.SolidCount == 0)
            {
                Bodies.RemoveAt(i--);
                touched = true;
                continue;
            }

            var names = Naming.Transfer(op, result, [new Naming.Input(body.Shape, body.FaceNames, body.EdgeNames), new Naming.Input(tool, toolFaceNames)], Feature.Id);
            if (Math.Abs(result.MassProperties.Volume - body.Shape.MassProperties.Volume) > 1e-9)
            {
                touched = true;
            }

            Bodies[i] = new Body(result, names, body.Name, body.Color);
        }

        if (!touched)
        {
            Warn("The cut doesn't intersect the model.");
        }
    }

    /// <summary>Replaces a body with the result of an operation on it.</summary>
    public void Replace(Body body, Operation op, string generatedTag)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(op);
        var names = Naming.Transfer(op, op.Result, [new Naming.Input(body.Shape, body.FaceNames, body.EdgeNames)], Feature.Id, generatedTag);
        var i = Bodies.IndexOf(body);
        Bodies[i] = new Body(op.Result, names, body.Name, body.Color);
    }

    /// <summary>Finds a body by name, or the body that owns a face reference.</summary>
    public Body FindBody(string nameOrFace)
    {
        ArgumentNullException.ThrowIfNull(nameOrFace);
        return Bodies.Find(b => b.Name == nameOrFace) ?? Bodies.Find(b => b.HasFace(nameOrFace))
            ?? throw new FeatureException($"Body '{nameOrFace}' is missing.");
    }

    /// <summary>A body name not used yet.</summary>
    public string NewBodyName()
    {
        for (var n = Bodies.Count + 1; ; n++)
        {
            var name = $"Body{n}";
            if (!Bodies.Exists(b => b.Name == name))
            {
                return name;
            }
        }
    }

    private static bool Overlaps(Box3 a, Box3 b)
    {
        const double eps = 1e-6;
        return !a.IsEmpty && !b.IsEmpty &&
            a.Min.X <= b.Max.X + eps && b.Min.X <= a.Max.X + eps &&
            a.Min.Y <= b.Max.Y + eps && b.Min.Y <= a.Max.Y + eps &&
            a.Min.Z <= b.Max.Z + eps && b.Min.Z <= a.Max.Z + eps;
    }
}
