using System.Globalization;
using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;
using Chamfer.Sketching.Entities;

namespace Chamfer.Api;

/// <summary>
/// The scripting API: a small, stable surface over a part for macros and
/// plugins. Every method adds ordinary features to the part's history, so a
/// scripted part can be edited by hand afterwards.
/// </summary>
/// <example>
/// <code>
/// var s = Api.NewSketch("front").Rectangle(0, 0, 80, 40).Circle(40, 20, 8).Done();
/// Api.Extrude(s, 10);
/// Api.FilletEdges(e => e.Curve == CurveKind.Line &amp;&amp; Math.Abs(e.CenterOrDirection.Z) > 0.99, 3);
/// Print(Api.Volume);
/// </code>
/// </example>
public sealed class ChamferApi(PartDocument part)
{
    private readonly List<string> _output = [];

    public PartDocument Part { get; } = part ?? throw new ArgumentNullException(nameof(part));

    /// <summary>Rebuild after every added feature (on by default).</summary>
    public bool AutoRebuild { get; set; } = true;

    /// <summary>Text written by <see cref="Print"/>.</summary>
    public IReadOnlyList<string> Output => _output;

    public void Print(object? value) => _output.Add(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null");

    // --------------------------------------------------------------- sketches

    /// <summary>Starts a sketch on "front", "top", "right", or the id of a reference plane.</summary>
    public SketchBuilder NewSketch(string plane = "front") => new(this, new PlaneRef(plane));

    /// <summary>Starts a sketch on a planar face of the part, picked by a predicate.</summary>
    public SketchBuilder NewSketchOnFace(Func<FaceInfo, bool> where)
    {
        ArgumentNullException.ThrowIfNull(where);
        var (body, index) = FindFace(where);
        return new SketchBuilder(this, new PlaneRef(Face: body.FaceRef(index)));
    }

    // --------------------------------------------------------------- features

    public T Add<T>(T feature)
        where T : Feature
    {
        Part.Add(feature);
        if (AutoRebuild)
        {
            Part.Rebuild();
        }

        return feature;
    }

    /// <summary>Adds a feature from its JSON form (what the macro recorder writes).</summary>
    public Feature AddFeatureJson(string json) => Add(PartDocument.DeserializeFeature(json));

    public ExtrudeFeature Extrude(SketchFeature sketch, double depth, bool cut = false, bool throughAll = false, bool midPlane = false)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        return Add(new ExtrudeFeature
        {
            SketchId = sketch.Id,
            Depth = depth,
            IsCut = cut,
            EndCondition = throughAll ? EndCondition.ThroughAll : midPlane ? EndCondition.MidPlane : EndCondition.Blind,
        });
    }

    public RevolveFeature Revolve(SketchFeature sketch, double angle = 360, bool cut = false)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        return Add(new RevolveFeature { SketchId = sketch.Id, Angle = angle, IsCut = cut });
    }

    public FilletFeature FilletEdges(Func<EdgeInfo, bool> where, double radius) =>
        Add(new FilletFeature { Edges = EdgeRefs(where), Radius = radius });

    public ChamferFeature ChamferEdges(Func<EdgeInfo, bool> where, double distance) =>
        Add(new ChamferFeature { Edges = EdgeRefs(where), Distance = distance });

    public ShellFeature Shell(Func<FaceInfo, bool> openFaces, double thickness) =>
        Add(new ShellFeature { FacesToRemove = FaceRefs(openFaces), Thickness = thickness });

    public LinearPatternFeature LinearPattern(Feature seed, string axis, double spacing, int count)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return Add(new LinearPatternFeature { Seeds = [seed.Id], Direction = new AxisRef(StandardAxis: axis), Spacing = spacing, Count = count });
    }

    public CircularPatternFeature CircularPattern(Feature seed, int count, string axis = "z", double totalAngle = 360)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return Add(new CircularPatternFeature { Seeds = [seed.Id], Axis = new AxisRef(StandardAxis: axis), Count = count, TotalAngle = totalAngle });
    }

    public ReferencePlaneFeature OffsetPlane(string basePlane, double offset) =>
        Add(new ReferencePlaneFeature { Base = new PlaneRef(basePlane), Offset = offset });

    /// <summary>Defines or changes a global variable (expressions can use other variables).</summary>
    public void Variable(string name, string expression)
    {
        Part.SetVariable(name, expression);
        if (AutoRebuild)
        {
            Part.Rebuild();
        }
    }

    /// <summary>Links a feature parameter to an expression, e.g. Link(extrude, "Depth", "Thickness * 2").</summary>
    public void Link(Feature feature, string property, string expression)
    {
        ArgumentNullException.ThrowIfNull(feature);
        (feature.Links ??= [])[property] = expression;
        if (AutoRebuild)
        {
            Part.Rebuild();
        }
    }

    // ----------------------------------------------------------------- queries

    public void Rebuild() => Part.Rebuild();

    public double Volume => Part.MassProperties().Volume;

    public double Mass => Part.MassProperties().Mass;

    public Vec3 CenterOfMass => Part.MassProperties().CenterOfMass;

    public Box3 Bounds => Part.Bodies.Aggregate(Box3.Empty, (b, x) => b.Union(x.Shape.Bounds));

    /// <summary>Features that failed or warned in the last rebuild, as "Name: message".</summary>
    public IReadOnlyList<string> Problems =>
        [.. Part.Features.Where(f => Part.Results.TryGetValue(f.Id, out var r) && r.Status is FeatureStatus.Error or FeatureStatus.Warning)
            .Select(f => $"{f.Name}: {Part.Results[f.Id].Message}")];

    public IEnumerable<FaceInfo> Faces => Part.Bodies.SelectMany(b => Enumerable.Range(1, b.FaceCount).Select(b.Face));

    public IEnumerable<EdgeInfo> Edges => Part.Bodies.SelectMany(b => Enumerable.Range(1, b.EdgeCount).Select(b.Edge));

    public void Export(string path) => Part.Export(path);

    public void SetMaterial(string name) =>
        Part.Material = Material.Library.FirstOrDefault(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No material matching '{name}'.", nameof(name));

    // ----------------------------------------------------------------- helpers

    private List<TopoRef> EdgeRefs(Func<EdgeInfo, bool> where)
    {
        ArgumentNullException.ThrowIfNull(where);
        var refs = Part.Bodies.SelectMany(b => Enumerable.Range(1, b.EdgeCount).Where(i => where(b.Edge(i))).Select(b.EdgeRef)).ToList();
        return refs.Count > 0 ? refs : throw new InvalidOperationException("No edges match.");
    }

    private List<TopoRef> FaceRefs(Func<FaceInfo, bool> where)
    {
        ArgumentNullException.ThrowIfNull(where);
        return [.. Part.Bodies.SelectMany(b => Enumerable.Range(1, b.FaceCount).Where(i => where(b.Face(i))).Select(b.FaceRef))];
    }

    private (Body Body, int Index) FindFace(Func<FaceInfo, bool> where)
    {
        foreach (var b in Part.Bodies)
        {
            for (var i = 1; i <= b.FaceCount; i++)
            {
                if (where(b.Face(i)) && b.Shape.FacePlane(i) is not null)
                {
                    return (b, i);
                }
            }
        }

        throw new InvalidOperationException("No planar face matches.");
    }
}

/// <summary>Builds a sketch fluently; <see cref="Done"/> adds it to the part.</summary>
public sealed class SketchBuilder
{
    private readonly ChamferApi _api;
    private readonly PlaneRef _plane;

    internal SketchBuilder(ChamferApi api, PlaneRef plane)
    {
        _api = api;
        _plane = plane;
    }

    /// <summary>The sketch being built, for direct use of the sketching API (relations, dimensions).</summary>
    public Sketch Sketch { get; } = new();

    public SketchBuilder Line(double x0, double y0, double x1, double y1)
    {
        Sketch.AddLine(new Vec2(x0, y0), new Vec2(x1, y1));
        return this;
    }

    public SketchBuilder Centerline(double x0, double y0, double x1, double y1)
    {
        Sketch.AddLine(new Vec2(x0, y0), new Vec2(x1, y1), construction: true);
        return this;
    }

    public SketchBuilder Polyline(bool closed, params double[] xy)
    {
        ArgumentNullException.ThrowIfNull(xy);
        Sketch.AddPolyline([.. Enumerable.Range(0, xy.Length / 2).Select(i => new Vec2(xy[2 * i], xy[(2 * i) + 1]))], closed);
        return this;
    }

    public SketchBuilder Rectangle(double x0, double y0, double x1, double y1)
    {
        Sketch.AddCornerRectangle(new Vec2(x0, y0), new Vec2(x1, y1));
        return this;
    }

    public SketchBuilder CenterRectangle(double cx, double cy, double width, double height)
    {
        Sketch.AddCenterRectangle(new Vec2(cx, cy), new Vec2(cx + (width / 2), cy + (height / 2)));
        return this;
    }

    public SketchBuilder Circle(double cx, double cy, double radius)
    {
        Sketch.AddCircle(new Vec2(cx, cy), radius);
        return this;
    }

    /// <summary>Counter-clockwise arc around (cx, cy) from (x0, y0) to (x1, y1).</summary>
    public SketchBuilder Arc(double cx, double cy, double x0, double y0, double x1, double y1)
    {
        Sketch.AddArc(new Vec2(cx, cy), new Vec2(x0, y0), new Vec2(x1, y1));
        return this;
    }

    public SketchBuilder Polygon(double cx, double cy, double radius, int sides)
    {
        Sketch.AddPolygon(new Vec2(cx, cy), new Vec2(cx + radius, cy), sides);
        return this;
    }

    public SketchBuilder Slot(double x0, double y0, double x1, double y1, double radius)
    {
        Sketch.AddSlot(new Vec2(x0, y0), new Vec2(x1, y1), radius);
        return this;
    }

    public SketchBuilder Point(double x, double y)
    {
        Sketch.AddPoint(new Vec2(x, y));
        return this;
    }

    /// <summary>All non-construction lines in the sketch so far.</summary>
    public IEnumerable<SketchLine> Lines => Sketch.Entities.OfType<SketchLine>();

    public SketchFeature Done() => _api.Add(new SketchFeature { Plane = _plane, Sketch = Sketch });
}
