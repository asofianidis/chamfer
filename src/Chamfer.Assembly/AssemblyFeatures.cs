using System.Text.Json;
using System.Text.Json.Serialization;
using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Assembly;

/// <summary>
/// An assembly-level cut: a sketch on one of the assembly's origin planes, extruded and removed
/// from the chosen components in the assembly only (the part files don't change), like a
/// cut made after the parts are put together.
/// </summary>
public sealed class AssemblyCut
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Suppressed { get; set; }

    /// <summary>"front", "top" or "right": the assembly origin plane the sketch is on.</summary>
    public string PlaneId { get; set; } = "front";

    /// <summary>Moves the sketch plane along its normal.</summary>
    public double PlaneOffset { get; set; }

    [JsonIgnore]
    public Sketch Sketch { get; set; } = new();

    public JsonElement SketchData
    {
        get => JsonElement.Parse(SketchSerializer.Serialize(Sketch));
        set => Sketch = SketchSerializer.Deserialize(value.GetRawText());
    }

    public double Depth { get; set; } = 10;

    /// <summary>Cut through everything in both directions.</summary>
    public bool ThroughAll { get; set; }

    /// <summary>Cut toward the plane's back instead of along its normal.</summary>
    public bool Reverse { get; set; }

    /// <summary>Components to cut; empty cuts all of them.</summary>
    public List<string> Components { get; set; } = [];

    /// <summary>Holes: circles in the sketch get a counterbore or countersink at the sketch plane.</summary>
    public HoleType HoleStyle { get; set; } = HoleType.Simple;

    /// <summary>Counterbore or countersink diameter at the surface.</summary>
    public double CounterDiameter { get; set; } = 11;

    public double CounterboreDepth { get; set; } = 6;

    /// <summary>Included countersink angle, degrees.</summary>
    public double CountersinkAngle { get; set; } = 90;

    public Plane3 Plane => (PlaneId switch
    {
        "top" => Plane3.Top,
        "right" => Plane3.Right,
        _ => Plane3.Front,
    }).Offset(PlaneOffset);

    /// <summary>The cutting solid in assembly space.</summary>
    public Shape Tool(Box3 assemblyBounds)
    {
        var plane = Plane;
        Shape cut;
        if (ThroughAll)
        {
            var reach = assemblyBounds.IsEmpty ? 1000 : (assemblyBounds.Diagonal * 2) + 10;
            cut = SketchSolids.Extrude(plane, Sketch, -reach, reach);
        }
        else
        {
            cut = Reverse ? SketchSolids.Extrude(plane, Sketch, -Depth, 0) : SketchSolids.Extrude(plane, Sketch, 0, Depth);
        }

        if (HoleStyle is not (HoleType.Counterbore or HoleType.Countersink))
        {
            return cut;
        }

        // Each circle's counterbore or countersink, from the sketch plane in the cut's direction.
        var dir = plane.Normal * (Reverse ? -1 : 1);
        foreach (var circle in Sketch.Entities.OfType<Chamfer.Sketching.Entities.SketchCircle>().Where(c => !c.IsConstruction))
        {
            var center = plane.ToWorld(circle.Center.Position.X, circle.Center.Position.Y);
            var at = new Plane3(center, plane.Normal, plane.XDirection);
            var big = CounterDiameter / 2;
            if (big <= circle.Radius)
            {
                throw new InvalidOperationException("The counterbore or countersink must be wider than the hole.");
            }

            Shape extra;
            using (var top = Modeling.Face(at, [[ProfileSegment.Circle(0, 0, big)]]))
            {
                if (HoleStyle == HoleType.Counterbore)
                {
                    using var bore = Modeling.Extrude(top, dir * CounterboreDepth);
                    extra = bore.Result;
                }
                else
                {
                    var sink = (big - circle.Radius) / Math.Tan(CountersinkAngle * Math.PI / 360);
                    using var bottom = Modeling.Face(new Plane3(center + (dir * sink), plane.Normal, plane.XDirection), [[ProfileSegment.Circle(0, 0, circle.Radius)]]);
                    using var cone = Modeling.Loft([top, bottom], solid: true, ruled: true);
                    extra = cone.Result;
                }
            }

            using (extra)
            using (var fuse = Modeling.Boolean(cut, extra, BooleanKind.Fuse))
            {
                cut.Dispose();
                cut = fuse.Result;
            }
        }

        return cut;
    }

    /// <summary>A fingerprint of everything that changes the result.</summary>
    public string Fingerprint() =>
        $"{Suppressed}|{PlaneId}|{PlaneOffset}|{Depth}|{ThroughAll}|{Reverse}|{string.Join(',', Components)}|{HoleStyle}|{CounterDiameter}|{CounterboreDepth}|{CountersinkAngle}|" +
        $"{SketchSerializer.Serialize(Sketch).GetHashCode(StringComparison.Ordinal)}";
}

public sealed partial class AssemblyDocument
{
    private readonly Dictionary<string, (string Key, IReadOnlyList<Body> Bodies)> _cutBodies = [];

    /// <summary>Assembly features, applied in order.</summary>
    public List<AssemblyCut> Features { get; } = [];

    /// <summary>Adds an assembly cut with a fresh id and name.</summary>
    public AssemblyCut AddCut(AssemblyCut cut)
    {
        ArgumentNullException.ThrowIfNull(cut);
        cut.Id = $"cut{_nextId++}";
        if (cut.Name.Length == 0)
        {
            cut.Name = $"Cut-Extrude{Features.Count + 1}";
        }

        Features.Add(cut);
        return cut;
    }

    /// <summary>
    /// A component's bodies in its own coordinates, with assembly cuts applied. Without
    /// cuts these are the part's bodies.
    /// </summary>
    public IReadOnlyList<Body> BodiesOf(Component c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = PartOf(c);
        var cuts = Features.Where(f => !f.Suppressed && (f.Components.Count == 0 || f.Components.Contains(c.Id))).ToList();
        if (cuts.Count == 0)
        {
            return part.Bodies;
        }

        var bounds = AssemblyBounds();
        var key = $"{c.Transform}|{part.State.GetHashCode()}|{string.Join(";", cuts.Select(f => f.Fingerprint()))}|{bounds}";
        if (_cutBodies.TryGetValue(c.Id, out var cached) && cached.Key == key)
        {
            return cached.Bodies;
        }

        var inverse = Inverse(c.Transform);
        var bodies = part.Bodies.ToList();
        foreach (var cut in cuts)
        {
            Shape tool;
            try
            {
                tool = cut.Tool(bounds);
            }
            catch (Exception ex) when (ex is FeatureException or KernelException)
            {
                continue;
            }

            using (tool)
            {
                using var local = Modeling.Transform(tool, inverse);
                for (var i = 0; i < bodies.Count; i++)
                {
                    var body = bodies[i];
                    if (body.IsSurface || !body.Shape.Bounds.Intersects(local.Result.Bounds))
                    {
                        continue;
                    }

                    try
                    {
                        using var op = Modeling.Boolean(body.Shape, local.Result, BooleanKind.Cut);
                        var names = Naming.Transfer(op, op.Result, [new Naming.Input(body.Shape, body.FaceNames, body.EdgeNames)], cut.Id, "cut");
                        bodies[i] = new Body(op.Result, names, body.Name, body.Color);
                    }
                    catch (KernelException)
                    {
                        // Leave this body uncut rather than lose the component.
                    }
                }
            }
        }

        _cutBodies[c.Id] = (key, bodies);
        return bodies;
    }

    /// <summary>
    /// Mates a newly inserted component through its part's mate references: each reference is matched
    /// to another component whose part has a reference of the same name with faces not yet mated, and
    /// their entities are mated pairwise (concentric, or coincident with the faces touching).
    /// </summary>
    public IReadOnlyList<Mate> ApplyMateReferences(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        var added = new List<Mate>();
        foreach (var reference in PartOf(component).MateReferences.Where(r => r.Entities.Count > 0))
        {
            foreach (var other in Components.Where(c => c != component && !c.Suppressed))
            {
                var match = PartOf(other).MateReferences.Find(r => r.Name == reference.Name && r.Entities.Count == reference.Entities.Count);
                if (match is null || match.Entities.Any(e => Mates.Any(m => Uses(m, other.Id, e.Face))))
                {
                    continue;
                }

                for (var i = 0; i < reference.Entities.Count; i++)
                {
                    var mine = reference.Entities[i];
                    var theirs = match.Entities[i];
                    var type = mine.Kind == "concentric" ? MateType.Concentric : MateType.Coincident;
                    added.Add(AddMate(type, new MateRef(other.Id, theirs.Face), new MateRef(component.Id, mine.Face),
                        aligned: type == MateType.Coincident ? false : null));
                }

                break;
            }
        }

        if (added.Count > 0)
        {
            Solve();
        }

        return added;
    }

    private static bool Uses(Mate m, string componentId, TopoRef face) =>
        (m.A.ComponentId == componentId && m.A.Face == face) || (m.B.ComponentId == componentId && m.B.Face == face);

    /// <summary>A key that changes whenever any component's cut result could change.</summary>
    public string FeaturesFingerprint() => string.Join(";", Features.Select(f => f.Fingerprint()));

    private Box3 AssemblyBounds()
    {
        var box = Box3.Empty;
        foreach (var c in Components.Where(c => !c.Suppressed))
        {
            foreach (var b in PartOf(c).Bodies)
            {
                var bb = b.Shape.Bounds;
                for (var k = 0; k < 8; k++)
                {
                    var p = new Vec3((k & 1) == 0 ? bb.Min.X : bb.Max.X, (k & 2) == 0 ? bb.Min.Y : bb.Max.Y, (k & 4) == 0 ? bb.Min.Z : bb.Max.Z);
                    box = box.Include(c.Transform.Apply(p));
                }
            }
        }

        return box;
    }
}
