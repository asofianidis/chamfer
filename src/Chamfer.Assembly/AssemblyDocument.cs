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
}

/// <summary>
/// An assembly: components placed in space and the mates between them.
/// Parts are loaded through <see cref="PartLoader"/> and cached; their bodies
/// are rebuilt once and shared by every instance.
/// </summary>
public sealed class AssemblyDocument
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

    public AssemblyDocument()
    {
        PartLoader = path => Toolbox.IsToolboxPath(path) ? Toolbox.Create(path) : PartDocument.Load(File.ReadAllText(Resolve(path)));
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
            part = PartLoader(component.Path);
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

    /// <summary>Forgets cached parts so they're reloaded (after a part file changed).</summary>
    public void ReloadParts()
    {
        _parts.Clear();
        _placed.Clear();
    }

    public Component? Find(string id) => Components.Find(c => c.Id == id);

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
        mate.Aligned = aligned ?? MateSolver.ClosestAlignment(this, mate);
        if (type == MateType.Lock)
        {
            var ta = Find(a.ComponentId)!.Transform;
            var tb = Find(b.ComponentId)!.Transform;
            mate.Relative = Inverse(ta) * tb;
        }

        Mates.Add(mate);
        return mate;
    }

    /// <summary>Deletes a component and the mates that reference it.</summary>
    public void RemoveComponent(Component c)
    {
        ArgumentNullException.ThrowIfNull(c);
        Components.Remove(c);
        Mates.RemoveAll(m => m.A.ComponentId == c.Id || m.B.ComponentId == c.Id);
    }

    public MateSolveResult Solve() => MateSolver.Solve(this);

    /// <summary>A component's bodies placed in assembly space (cached per placement).</summary>
    public IEnumerable<(Body Body, Shape Placed)> PlacedBodies(Component c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var part = PartOf(c);
        for (var i = 0; i < part.Bodies.Count; i++)
        {
            var body = part.Bodies[i];
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
    }

    public string Save() => JsonSerializer.Serialize(new AssemblyDto
    {
        Version = FormatVersion,
        NextId = _nextId,
        Components = Components,
        Mates = Mates,
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
            return mine;
        }).ToList();
        Mates.Clear();
        Mates.AddRange(mates);
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
        doc.Components.AddRange(dto.Components);
        doc.Mates.AddRange(dto.Mates);
        doc._nextId = Math.Max(dto.NextId, doc.Components.Count + doc.Mates.Count + 1);
        return doc;
    }
}
