using System.Text.Json;
using System.Text.Json.Serialization;
using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Assembly;

/// <summary>An instance of a part placed in an assembly.</summary>
public sealed class Component
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name, e.g. "Bracket&lt;1&gt;".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The part file, relative to the assembly's folder when saved.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Part configuration to use; null uses the part's active one.</summary>
    public string? Configuration { get; set; }

    /// <summary>Placement of the part in assembly space.</summary>
    public Transform3 Transform { get; set; } = Transform3.Identity;

    /// <summary>Fixed components never move when mates are solved.</summary>
    public bool Fixed { get; set; }

    public bool Suppressed { get; set; }

    public bool Hidden { get; set; }

    /// <summary>Extra translation applied in the exploded view.</summary>
    public Vec3 ExplodeOffset { get; set; }
}

public enum MateType
{
    Coincident,
    Parallel,
    Perpendicular,
    Distance,
    Angle,
    Concentric,
    Tangent,
    Lock,

    /// <summary>A and B mirror each other about plane C.</summary>
    Symmetric,

    /// <summary>The faces A and B (a tab) are centred between the faces C and D (a slot).</summary>
    Width,

    /// <summary>Distance between A and B kept between Value and Value2.</summary>
    LimitDistance,

    /// <summary>Angle between A and B kept between Value and Value2 degrees.</summary>
    LimitAngle,

    /// <summary>Axes A and B turn together at the ratio Value (A turns Value times for each turn of B, oppositely).</summary>
    Gear,

    /// <summary>The rack (edge A) moves Value mm per radian the pinion (axis B) turns.</summary>
    RackPinion,

    /// <summary>B advances Value mm along axis A per revolution about it.</summary>
    Screw,

    /// <summary>
    /// Internal (motion studies): B turns Value radians about its axis A (or, when Aligned,
    /// moves Value mm along it) from InitialB. Not offered as a user mate.
    /// </summary>
    Drive,

    /// <summary>Axes A and B concentric and, when given, faces C and D touching: a hinge with one rotation left.</summary>
    Hinge,

    /// <summary>The pin axis A stays centred between the slot faces B and C, free to slide along the slot.</summary>
    Slot,

    /// <summary>Shaft B follows shaft A's turning through a Cardan joint: tan θB = tan θA · cos β.</summary>
    UniversalJoint,

    /// <summary>B moves along its direction Value times as far as A moves along its own.</summary>
    LinearCoupler,

    /// <summary>B's point (or axis origin) stays on the curve of edge A.</summary>
    Path,

    /// <summary>The follower B (a cylinder) stays touching the cam profile, edge A, seen along B's axis.</summary>
    Cam,
}

/// <summary>A named set of hidden components (a display state).</summary>
public sealed class DisplayState
{
    public string Name { get; set; } = "Display State";

    public List<string> HiddenComponents { get; set; } = [];
}

/// <summary>Geometry on a component a mate refers to: a face, an edge, or a reference plane of the part.</summary>
public sealed record MateRef(string ComponentId, TopoRef? Face = null, TopoRef? Edge = null, string? PlaneId = null)
{
    public override string ToString() => Face?.Name ?? Edge?.Name ?? PlaneId ?? "?";
}

/// <summary>A geometric relation between two components.</summary>
public sealed class Mate
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public MateType Type { get; set; }

    public MateRef A { get; set; } = new(string.Empty);

    public MateRef B { get; set; } = new(string.Empty);

    /// <summary>Distance (mm) for distance mates, degrees for angle mates.</summary>
    public double Value { get; set; }

    /// <summary>
    /// Alignment: false (anti-aligned) makes planar faces face each other and
    /// axes point opposite ways; true points them the same way. For distance
    /// and tangent mates it picks the side.
    /// </summary>
    public bool Aligned { get; set; }

    public bool Suppressed { get; set; }

    /// <summary>For lock mates: B's placement relative to A when the mate was made.</summary>
    public Transform3? Relative { get; set; }

    /// <summary>Third reference: the symmetry plane, or the first face of a width mate's slot.</summary>
    public MateRef? C { get; set; }

    /// <summary>Fourth reference: the second face of a width mate's slot.</summary>
    public MateRef? D { get; set; }

    /// <summary>Upper bound for limit mates.</summary>
    public double Value2 { get; set; }

    /// <summary>For mechanical mates: where A and B were when the mate was made (motion is measured from there).</summary>
    public Transform3? InitialA { get; set; }

    public Transform3? InitialB { get; set; }
}

/// <summary>
/// An assembly: components placed in space and the mates between them.
/// Parts are loaded through <see cref="PartLoader"/> and cached; their bodies
/// are rebuilt once and shared by every instance.
/// </summary>
public sealed partial class AssemblyDocument
{
    public const int FormatVersion = 1;
    public const string FileExtension = "chasm";

    private readonly Dictionary<string, PartDocument> _parts = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Id, Transform3 T), Shape> _placed = [];
    private int _nextId = 1;

    public List<Component> Components { get; } = [];

    public List<Mate> Mates { get; } = [];

    /// <summary>Where the assembly file lives; part paths are resolved relative to it.</summary>
    public string? Directory { get; set; }

    /// <summary>Loads a part by path. Defaults to reading <c>.chprt</c> files from disk.</summary>
    public Func<string, PartDocument> PartLoader { get; set; }

    /// <summary>Loads a subassembly by path. Defaults to reading <c>.chasm</c> files from disk.</summary>
    public Func<string, AssemblyDocument> AssemblyLoader { get; set; }

    private readonly Dictionary<string, AssemblyDocument> _subAssemblies = [];

    /// <summary>Subassemblies being loaded on this thread, to refuse one that contains itself.</summary>
    [ThreadStatic]
    private static HashSet<string>? _loading;

    public AssemblyDocument()
    {
        PartLoader = path => Toolbox.IsToolboxPath(path) ? Toolbox.Create(path) : PartDocument.Load(File.ReadAllText(Resolve(path)));
        AssemblyLoader = path =>
        {
            var full = Resolve(path);
            return Load(File.ReadAllText(full), System.IO.Path.GetDirectoryName(full));
        };
    }

    /// <summary>Registers an in-memory subassembly under a path (for new subassemblies and tests).</summary>
    public void RegisterSubAssembly(string path, AssemblyDocument sub)
    {
        ArgumentNullException.ThrowIfNull(sub);
        _subAssemblies[path] = sub;
        _parts.Remove(path);
    }

    /// <summary>The subassembly a component instances, or null when it's a part.</summary>
    public AssemblyDocument? SubAssemblyOf(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!SubAssemblies.IsAssemblyPath(component.Path))
        {
            return null;
        }

        PartOf(component);
        return _subAssemblies.GetValueOrDefault(component.Path);
    }

    private PartDocument LoadSubAssembly(string path)
    {
        var key = Resolve(path);
        _loading ??= [];
        if (!_loading.Add(key))
        {
            throw new InvalidOperationException($"The assembly {System.IO.Path.GetFileName(path)} contains itself.");
        }

        try
        {
            if (!_subAssemblies.TryGetValue(path, out var sub))
            {
                sub = AssemblyLoader(path);
                _subAssemblies[path] = sub;
            }

            return SubAssemblies.StandIn(sub);
        }
        finally
        {
            _loading.Remove(key);
        }
    }

    public string Resolve(string path) =>
        System.IO.Path.IsPathRooted(path) || Directory is null ? path : System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory, path));

    /// <summary>Registers an in-memory part under a path (for new parts and tests).</summary>
    public void RegisterPart(string path, PartDocument part)
    {
        ArgumentNullException.ThrowIfNull(part);
        _parts[path] = part;
        part.Rebuild();
    }

    /// <summary>The rebuilt part a component instances, in its configuration.</summary>
    public PartDocument PartOf(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!_parts.TryGetValue(component.Path, out var part))
        {
            part = SubAssemblies.IsAssemblyPath(component.Path) ? LoadSubAssembly(component.Path) : PartLoader(component.Path);
            _parts[component.Path] = part;
        }

        var config = component.Configuration ?? part.ActiveConfiguration;
        if (part.ActiveConfiguration != config && part.Configurations.Exists(c => c.Name == config))
        {
            part.ActiveConfiguration = config;
            part.Rebuild();
        }
        else if (part.State == RebuildState.Initial && part.Features.Count > 0)
        {
            part.Rebuild();
        }

        return part;
    }

    /// <summary>Rebuilds one component path's part or subassembly stand-in on next use (after it was edited).</summary>
    public void Refresh(string path)
    {
        _parts.Remove(path);
        _placed.Clear();
    }

    /// <summary>Forgets cached parts so they're reloaded (after a part file changed).</summary>
    public void ReloadParts()
    {
        _parts.Clear();
        _placed.Clear();
        _subAssemblies.Clear();
    }

    public Component? Find(string id) => Components.Find(c => c.Id == id);

    /// <summary>Saved display states (which components are hidden).</summary>
    public List<DisplayState> DisplayStates { get; } = [];

    /// <summary>The display state last applied or saved, if any.</summary>
    public string? ActiveDisplayState { get; set; }

    /// <summary>Saves which components are hidden now under a name (replacing a state of that name).</summary>
    public DisplayState SaveDisplayState(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var state = DisplayStates.Find(s => s.Name == name.Trim()) ?? new DisplayState { Name = name.Trim() };
        state.HiddenComponents = [.. Components.Where(c => c.Hidden).Select(c => c.Id)];
        if (!DisplayStates.Contains(state))
        {
            DisplayStates.Add(state);
        }

        ActiveDisplayState = state.Name;
        return state;
    }

    /// <summary>Shows and hides components as a display state says.</summary>
    public void ApplyDisplayState(DisplayState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        foreach (var c in Components)
        {
            c.Hidden = state.HiddenComponents.Contains(c.Id);
        }

        ActiveDisplayState = state.Name;
    }

    public Component AddComponent(string path, Transform3? placement = null, string? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var baseName = Toolbox.IsToolboxPath(path) ? path["toolbox:".Length..] : System.IO.Path.GetFileNameWithoutExtension(path);
        var instance = Components.Count(c => c.Path == path) + 1;
        var c = new Component
        {
            Id = $"c{_nextId++}",
            Name = $"{baseName}<{instance}>",
            Path = path,
            Configuration = configuration,
            Transform = placement ?? Transform3.Identity,
            Fixed = Components.Count == 0,
        };
        Components.Add(c);
        return c;
    }

    public Mate AddMate(MateType type, MateRef a, MateRef b, double value = 0, bool? aligned = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var mate = new Mate
        {
            Id = $"m{_nextId++}",
            Name = $"{type}{Mates.Count(m => m.Type == type) + 1}",
            Type = type,
            A = a,
            B = b,
            Value = value,
        };

        // Pick the alignment closest to how the parts sit now, as other CAD tools do.
        mate.Aligned = aligned ?? (type is MateType.Gear or MateType.RackPinion or MateType.Screw or MateType.Symmetric or MateType.Width
            or MateType.Slot or MateType.UniversalJoint or MateType.LinearCoupler or MateType.Path or MateType.Cam
            ? false
            : MateSolver.ClosestAlignment(this, mate));
        if (type == MateType.Lock)
        {
            var ta = Find(a.ComponentId)!.Transform;
            var tb = Find(b.ComponentId)!.Transform;
            mate.Relative = Inverse(ta) * tb;
        }

        if (type is MateType.Gear or MateType.RackPinion or MateType.Screw or MateType.UniversalJoint or MateType.LinearCoupler)
        {
            mate.InitialA = Find(a.ComponentId)!.Transform;
            mate.InitialB = Find(b.ComponentId)!.Transform;
        }

        Mates.Add(mate);
        return mate;
    }

    /// <summary>
    /// Copies a component along a direction: <paramref name="count"/> instances including the seed,
    /// each locked to the seed so they move with it.
    /// </summary>
    public IReadOnlyList<Component> LinearPattern(Component seed, Vec3 direction, double spacing, int count)
    {
        ArgumentNullException.ThrowIfNull(seed);
        var step = direction.Normalized() * spacing;
        return Pattern(seed, count, i => Transform3.Translation(step * i));
    }

    /// <summary>Copies a component around an axis, evenly over 360°, locked to the seed.</summary>
    public IReadOnlyList<Component> CircularPattern(Component seed, Vec3 axisOrigin, Vec3 axisDirection, int count)
    {
        ArgumentNullException.ThrowIfNull(seed);
        return Pattern(seed, count, i => Transform3.Rotation(axisOrigin, axisDirection, 2 * Math.PI * i / count));
    }

    private List<Component> Pattern(Component seed, int count, Func<int, Transform3> placement)
    {
        var made = new List<Component>();
        for (var i = 1; i < count; i++)
        {
            var c = AddComponent(seed.Path, placement(i) * seed.Transform, seed.Configuration);
            c.Fixed = false;
            AddMate(MateType.Lock, new MateRef(seed.Id, PlaneId: "front"), new MateRef(c.Id, PlaneId: "front"));
            made.Add(c);
        }

        return made;
    }

    /// <summary>
    /// Mirrors a component across a plane. With <paramref name="oppositeHand"/>, a mirrored copy of
    /// its part is made (registered under <paramref name="mirroredPath"/>); without, the same part is
    /// placed turned over, which is right for parts that are symmetric themselves.
    /// </summary>
    public Component MirrorComponent(Component seed, Vec3 planeOrigin, Vec3 planeNormal, bool oppositeHand, string mirroredPath)
    {
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(mirroredPath);
        if (planeNormal.LengthSquared < 1e-18)
        {
            throw new ArgumentException("The mirror plane needs a direction.", nameof(planeNormal));
        }

        // Mirror · placement · local reflection is a rigid placement; the local reflection goes into
        // the part's geometry (opposite hand) or is left out (a symmetric part, turned over). It's
        // taken across the part's own middle, square to the local axis nearest the mirror normal,
        // so a part symmetric about that plane lands exactly on its mirror image.
        var source = PartOf(seed);
        var n = AssemblyDocument.Inverse(seed.Transform).ApplyVector(planeNormal.Normalized());
        var axis = Math.Abs(n.X) >= Math.Abs(n.Y) && Math.Abs(n.X) >= Math.Abs(n.Z) ? Vec3.UnitX : Math.Abs(n.Y) >= Math.Abs(n.Z) ? Vec3.UnitY : Vec3.UnitZ;
        var box = source.Bodies.Aggregate(Box3.Empty, (b, x) => b.Union(x.Shape.Bounds));
        var middle = box.IsEmpty ? Vec3.Zero : box.Center;
        var local = Transform3.Mirror(middle, axis);
        var placement = Transform3.Mirror(planeOrigin, planeNormal.Normalized()) * seed.Transform * local;
        var path = seed.Path;
        if (oppositeHand)
        {
            var shapes = new List<Shape>();
            try
            {
                foreach (var body in source.Bodies.Where(b => !b.IsSurface))
                {
                    using var op = Modeling.Mirror(body.Shape, middle, axis);
                    shapes.Add(op.Result);
                }

                if (shapes.Count == 0)
                {
                    throw new InvalidOperationException("The component has no solid bodies to mirror.");
                }

                using var all = shapes.Count == 1 ? null : Modeling.Compound(shapes);
                var mirrored = new PartDocument { Material = source.Material };
                mirrored.Add(new Chamfer.Document.Features.ImportFeature { SourceName = $"{System.IO.Path.GetFileNameWithoutExtension(seed.Path)} (mirrored)", Brep = (all ?? shapes[0]).ToBrep() });
                RegisterPart(mirroredPath, mirrored);
                path = mirroredPath;
            }
            finally
            {
                foreach (var s in shapes)
                {
                    s.Dispose();
                }
            }
        }

        var copy = AddComponent(path, placement, oppositeHand ? null : seed.Configuration);
        copy.Fixed = false;
        return copy;
    }

    /// <summary>Deletes a component and the mates that reference it.</summary>
    public void RemoveComponent(Component c)
    {
        ArgumentNullException.ThrowIfNull(c);
        Components.Remove(c);
        Mates.RemoveAll(m => m.A.ComponentId == c.Id || m.B.ComponentId == c.Id || m.C?.ComponentId == c.Id || m.D?.ComponentId == c.Id);
    }

    public MateSolveResult Solve() => MateSolver.Solve(this);

    /// <summary>A component's bodies placed in assembly space (cached per placement).</summary>
    public IEnumerable<(Body Body, Shape Placed)> PlacedBodies(Component c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var bodies = BodiesOf(c);
        for (var i = 0; i < bodies.Count; i++)
        {
            var body = bodies[i];
            var key = ($"{c.Id}/{i}/{body.GetHashCode()}", c.Transform);
            if (!_placed.TryGetValue(key, out var shape))
            {
                using var op = Modeling.Transform(body.Shape, c.Transform);
                shape = op.Result;
                _placed[key] = shape;
            }

            yield return (body, shape);
        }
    }

    public static Transform3 Inverse(Transform3 t)
    {
        // Rigid transforms: the inverse rotation is the transpose.
        var r = new Transform3(t.M00, t.M10, t.M20, 0, t.M01, t.M11, t.M21, 0, t.M02, t.M12, t.M22, 0);
        var p = r.Apply(new Vec3(t.Tx, t.Ty, t.Tz));
        return r with { Tx = -p.X, Ty = -p.Y, Tz = -p.Z };
    }

    // ---------------------------------------------------------- persistence

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class AssemblyDto
    {
        public int Version { get; set; }
        public string Format { get; set; } = "chamfer-assembly";
        public int NextId { get; set; }
        public List<Component> Components { get; set; } = [];
        public List<Mate> Mates { get; set; } = [];
        public List<AssemblyCut>? Features { get; set; }
        public List<DisplayState>? DisplayStates { get; set; }
        public string? ActiveDisplayState { get; set; }
    }

    public string Save() => JsonSerializer.Serialize(new AssemblyDto
    {
        Version = FormatVersion,
        NextId = _nextId,
        Components = Components,
        Mates = Mates,
        Features = Features.Count > 0 ? Features : null,
        DisplayStates = DisplayStates.Count > 0 ? DisplayStates : null,
        ActiveDisplayState = ActiveDisplayState,
    }, Options);

    /// <summary>
    /// Makes the assembly match <paramref name="json"/> (for undo and previews), keeping loaded parts
    /// and updating existing component and mate objects in place so references to them stay valid.
    /// </summary>
    public void RestoreFrom(string json)
    {
        var other = Load(json, Directory);
        var components = other.Components.Select(c =>
        {
            if (Find(c.Id) is not { } mine)
            {
                return c;
            }

            mine.Name = c.Name;
            mine.Path = c.Path;
            mine.Configuration = c.Configuration;
            mine.Transform = c.Transform;
            mine.Fixed = c.Fixed;
            mine.Suppressed = c.Suppressed;
            mine.Hidden = c.Hidden;
            mine.ExplodeOffset = c.ExplodeOffset;
            return mine;
        }).ToList();
        Components.Clear();
        Components.AddRange(components);

        var mates = other.Mates.Select(m =>
        {
            if (Mates.Find(x => x.Id == m.Id) is not { } mine)
            {
                return m;
            }

            mine.Name = m.Name;
            mine.Type = m.Type;
            mine.A = m.A;
            mine.B = m.B;
            mine.Value = m.Value;
            mine.Aligned = m.Aligned;
            mine.Suppressed = m.Suppressed;
            mine.Relative = m.Relative;
            mine.C = m.C;
            mine.D = m.D;
            mine.Value2 = m.Value2;
            mine.InitialA = m.InitialA;
            mine.InitialB = m.InitialB;
            return mine;
        }).ToList();
        Mates.Clear();
        Mates.AddRange(mates);
        Features.Clear();
        Features.AddRange(other.Features);
        DisplayStates.Clear();
        DisplayStates.AddRange(other.DisplayStates);
        ActiveDisplayState = other.ActiveDisplayState;
        _nextId = Math.Max(_nextId, other._nextId);
    }

    public static AssemblyDocument Load(string json, string? directory = null)
    {
        var dto = JsonSerializer.Deserialize<AssemblyDto>(json, Options) ?? throw new JsonException("Empty assembly document.");
        if (dto.Version > FormatVersion)
        {
            throw new NotSupportedException($"This assembly was saved by a newer version of Chamfer (format {dto.Version}).");
        }

        var doc = new AssemblyDocument { Directory = directory };
        if (dto.Components.Find(c => !c.Transform.IsRigid || c.Path is null || c.Id is null) is { } bad)
        {
            throw new JsonException($"Component {bad.Name} has an invalid placement or path.");
        }

        doc.Components.AddRange(dto.Components);
        doc.Mates.AddRange(dto.Mates);
        doc.Features.AddRange(dto.Features ?? []);
        doc.DisplayStates.AddRange(dto.DisplayStates ?? []);
        doc.ActiveDisplayState = dto.ActiveDisplayState;
        doc._nextId = Math.Max(dto.NextId, doc.Components.Count + doc.Mates.Count + doc.Features.Count + 1);
        return doc;
    }
}
