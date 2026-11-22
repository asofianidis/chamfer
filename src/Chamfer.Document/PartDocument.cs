using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chamfer.Document.Features;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document;

/// <summary>A material: density in kg/m³ and, for simulation, elastic modulus and yield strength in MPa and thermal conductivity.</summary>
public sealed record Material(string Name, double Density)
{
    /// <summary>Young's modulus, MPa.</summary>
    public double YoungModulus { get; init; } = 200000;

    public double PoissonRatio { get; init; } = 0.3;

    /// <summary>Yield strength, MPa.</summary>
    public double YieldStrength { get; init; } = 250;

    /// <summary>Thermal conductivity, W/(m·K).</summary>
    public double ThermalConductivity { get; init; } = 45;

    public static Material Steel { get; } = new("Plain Carbon Steel", 7800) { YoungModulus = 210000, PoissonRatio = 0.28, YieldStrength = 220, ThermalConductivity = 45 };
    public static Material Aluminum { get; } = new("6061 Alloy", 2700) { YoungModulus = 69000, PoissonRatio = 0.33, YieldStrength = 275, ThermalConductivity = 167 };
    public static Material Abs { get; } = new("ABS", 1020) { YoungModulus = 2000, PoissonRatio = 0.39, YieldStrength = 40, ThermalConductivity = 0.17 };
    public static Material Brass { get; } = new("Brass", 8500) { YoungModulus = 100000, PoissonRatio = 0.33, YieldStrength = 200, ThermalConductivity = 109 };
    public static Material Titanium { get; } = new("Ti-6Al-4V", 4430) { YoungModulus = 114000, PoissonRatio = 0.34, YieldStrength = 880, ThermalConductivity = 6.7 };
    public static Material Wood { get; } = new("Oak", 750) { YoungModulus = 11000, PoissonRatio = 0.3, YieldStrength = 40, ThermalConductivity = 0.17 };

    public static IReadOnlyList<Material> Library { get; } = [Steel, Aluminum, Abs, Brass, Titanium, Wood];
}

/// <summary>Per-feature outcome of the latest rebuild.</summary>
public sealed record FeatureResult(FeatureStatus Status, string? Message);

/// <summary>Mass properties of the whole part in SI-friendly units.</summary>
/// <param name="Volume">mm³.</param>
/// <param name="SurfaceArea">mm².</param>
/// <param name="Mass">kg.</param>
/// <param name="CenterOfMass">mm.</param>
/// <param name="Inertia">Inertia tensor about the centre of mass in kg·mm², row-major 3×3 (off-diagonals are -∫xy dm etc.).</param>
public sealed record PartMassProperties(double Volume, double SurfaceArea, double Mass, Geometry.Vec3 CenterOfMass, double[] Inertia)
{
    public double Ixx => Inertia[0];
    public double Iyy => Inertia[4];
    public double Izz => Inertia[8];
    public double Ixy => Inertia[1];
    public double Ixz => Inertia[2];
    public double Iyz => Inertia[5];
}

/// <summary>
/// A part: an ordered feature history and the bodies it builds. Rebuilds are
/// incremental: the model state after every feature is cached together with a
/// fingerprint of the features that produced it, so an edit only re-executes
/// the features from the first changed one onward.
/// </summary>
public sealed class PartDocument
{
    public const int FormatVersion = 1;
    public const string FileExtension = "chprt";

    private readonly List<(string Fingerprint, RebuildState State)> _cache = [];
    private int _nextId = 1;

    public List<Feature> Features { get; } = [];

    public Material Material { get; set; } = Material.Steel;

    /// <summary>Custom properties (Description, PartNumber, ...) shown in BOMs and title blocks.</summary>
    public List<CustomProperty> Properties { get; } = [];

    /// <summary>Mate references used to mate this part automatically when it's inserted into an assembly.</summary>
    public List<MateReference> MateReferences { get; } = [];

    /// <summary>The length unit the part is shown and edited in (geometry is always millimetres).</summary>
    public Geometry.LengthUnit Units { get; set; } = Geometry.LengthUnit.Millimeter;

    /// <summary>Global variables, usable in equations for feature parameters and sketch dimensions.</summary>
    public List<GlobalVariable> Variables { get; } = [];

    /// <summary>Configurations. There is always at least one; <see cref="ActiveConfiguration"/> names the one that is built.</summary>
    public List<Configuration> Configurations { get; } = [new Configuration()];

    public string ActiveConfiguration { get; set; } = "Default";

    public Configuration Active => Configurations.Find(c => c.Name == ActiveConfiguration) ?? Configurations[0];

    /// <summary>Whether a feature is suppressed in the active configuration.</summary>
    public bool IsSuppressed(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return Active.Suppression.TryGetValue(feature.Id, out var s) ? s : feature.IsSuppressed;
    }

    /// <summary>Sets suppression for the active configuration only (or the feature itself when there is one configuration).</summary>
    public void SetSuppressed(Feature feature, bool suppressed)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (Configurations.Count == 1)
        {
            feature.IsSuppressed = suppressed;
            Active.Suppression.Remove(feature.Id);
        }
        else
        {
            Active.Suppression[feature.Id] = suppressed;
        }
    }

    /// <summary>The document's variables with the active configuration's overrides.</summary>
    public IReadOnlyList<GlobalVariable> EffectiveVariables
    {
        get
        {
            var overrides = Active.Variables;
            var list = Variables.Select(v => overrides.TryGetValue(v.Name, out var e) ? v with { Expression = e } : v).ToList();
            list.AddRange(overrides.Where(kv => !Variables.Exists(v => v.Name == kv.Key)).Select(kv => new GlobalVariable(kv.Key, kv.Value)));
            return list;
        }
    }

    /// <summary>Values of the global variables after the last rebuild.</summary>
    public IReadOnlyDictionary<string, double> VariableValues { get; private set; } = new Dictionary<string, double>();

    /// <summary>Variables that couldn't be evaluated, with the reason.</summary>
    public IReadOnlyDictionary<string, string> VariableErrors { get; private set; } = new Dictionary<string, string>();

    /// <summary>Features at or after this index are rolled back (not built). Null = all built.</summary>
    public int? RollbackIndex { get; set; }

    /// <summary>State after the last rebuild.</summary>
    public RebuildState State { get; private set; } = RebuildState.Initial;

    public IReadOnlyList<Body> Bodies => State.Bodies;

    public IReadOnlyDictionary<string, FeatureResult> Results => _results;

    private readonly Dictionary<string, FeatureResult> _results = [];

    public Feature? Find(string id) => Features.Find(f => f.Id == id);

    /// <summary>Gives a feature an id and a numbered display name, then appends it (or inserts it at the rollback bar).</summary>
    public T Add<T>(T feature)
        where T : Feature
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (string.IsNullOrEmpty(feature.Id) || Features.Exists(f => f.Id == feature.Id))
        {
            feature.Id = $"f{_nextId++}";
        }
        else if (feature.Id.StartsWith('f') && int.TryParse(feature.Id.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            _nextId = Math.Max(_nextId, n + 1);
        }

        if (string.IsNullOrEmpty(feature.Name))
        {
            feature.Name = NextName(feature.DefaultName);
        }

        if (RollbackIndex is { } at && at < Features.Count)
        {
            Features.Insert(at, feature);
            RollbackIndex = at + 1;
        }
        else
        {
            Features.Add(feature);
        }

        return feature;
    }

    public string NextName(string baseName)
    {
        for (var n = 1; ; n++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"{baseName}{n}");
            if (!Features.Exists(f => f.Name == name))
            {
                return name;
            }
        }
    }

    /// <summary>Features that read <paramref name="id"/>, directly or through others.</summary>
    public IReadOnlyList<Feature> Dependents(string id)
    {
        var result = new List<Feature>();
        var ids = new HashSet<string> { id };
        foreach (var f in Features)
        {
            if (f.Dependencies.Any(ids.Contains))
            {
                result.Add(f);
                ids.Add(f.Id);
            }
        }

        return result;
    }

    /// <summary>Deletes a feature and everything that depends on it.</summary>
    public IReadOnlyList<Feature> Delete(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var removed = Dependents(feature.Id).Prepend(feature).ToList();
        foreach (var f in removed)
        {
            var i = Features.IndexOf(f);
            if (RollbackIndex is { } r && i < r)
            {
                RollbackIndex = r - 1;
            }

            Features.Remove(f);
        }

        return removed;
    }

    /// <summary>Re-executes features as needed and updates <see cref="State"/> and <see cref="Results"/>.</summary>
    public void Rebuild()
    {
        var end = Math.Min(RollbackIndex ?? Features.Count, Features.Count);
        var state = RebuildState.Initial;
        var reuse = true;
        VariableValues = Equations.Evaluate(EffectiveVariables, out var variableErrors);
        VariableErrors = variableErrors;
        var fingerprint = string.Join(";", VariableValues.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value:R}"));
        var linkErrors = new Dictionary<string, string>();
        for (var i = 0; i < Features.Count; i++)
        {
            var f = Features[i];
            if (i >= end)
            {
                _results[f.Id] = new FeatureResult(FeatureStatus.RolledBack, null);
                continue;
            }

            try
            {
                Equations.Apply(f, VariableValues);
            }
            catch (FeatureException ex)
            {
                linkErrors[f.Id] = ex.Message;
            }

            fingerprint = Fingerprint(f, fingerprint, IsSuppressed(f));
            if (reuse && i < _cache.Count && _cache[i].Fingerprint == fingerprint)
            {
                state = _cache[i].State;
                continue;
            }

            reuse = false;
            if (i < _cache.Count)
            {
                _cache.RemoveRange(i, _cache.Count - i);
            }

            if (IsSuppressed(f))
            {
                _results[f.Id] = new FeatureResult(FeatureStatus.Suppressed, null);
            }
            else if (linkErrors.TryGetValue(f.Id, out var linkError))
            {
                _results[f.Id] = new FeatureResult(FeatureStatus.Error, linkError);
            }
            else
            {
                var ctx = new RebuildContext(this, state, f);
                try
                {
                    f.Execute(ctx);
                    state = ctx.Capture();
                    _results[f.Id] = ctx.Warnings.Count > 0
                        ? new FeatureResult(FeatureStatus.Warning, string.Join(" ", ctx.Warnings))
                        : new FeatureResult(FeatureStatus.Ok, null);
                }
                catch (Exception ex) when (ex is FeatureException or KernelException)
                {
                    _results[f.Id] = new FeatureResult(FeatureStatus.Error, ex is KernelException ? $"The geometry kernel couldn't build this feature ({ex.Message})." : ex.Message);
                }
            }

            _cache.Add((fingerprint, state));
        }

        foreach (var stale in _results.Keys.Where(k => Find(k) is null).ToList())
        {
            _results.Remove(stale);
        }

        if (_cache.Count > end)
        {
            _cache.RemoveRange(end, _cache.Count - end);
        }

        State = state;
    }

    /// <summary>Adds or replaces a global variable.</summary>
    public void SetVariable(string name, string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(expression);
        var i = Variables.FindIndex(v => v.Name == name);
        var variable = new GlobalVariable(name, expression);
        if (i >= 0)
        {
            Variables[i] = variable;
        }
        else
        {
            Variables.Add(variable);
        }
    }

    /// <summary>The model as it was just before a feature (from the rebuild cache).</summary>
    public RebuildState StateBefore(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var i = Features.IndexOf(feature);
        if (i <= 0)
        {
            return RebuildState.Initial;
        }

        return i - 1 < _cache.Count ? _cache[i - 1].State : State;
    }

    /// <summary>Drops cached states so the next rebuild re-executes everything.</summary>
    public void InvalidateAll() => _cache.Clear();

    /// <summary>Mass properties of all bodies with the document's material.</summary>
    public PartMassProperties MassProperties()
    {
        double volume = 0, area = 0;
        var moment = Geometry.Vec3.Zero;
        var parts = new List<(MassProperties M, Geometry.Vec3 C)>();
        foreach (var b in Bodies)
        {
            var m = b.Shape.MassProperties;
            volume += m.Volume;
            area += m.SurfaceArea;
            moment += m.CenterOfMass * m.Volume;
            parts.Add((m, m.CenterOfMass));
        }

        var density = Material.Density * 1e-9; // kg/mm³
        var mass = volume * density;
        var com = volume > 0 ? moment / volume : Geometry.Vec3.Zero;

        // Combine body tensors about the overall centre of mass (parallel-axis theorem).
        var inertia = new double[9];
        foreach (var (m, c) in parts)
        {
            var bodyMass = m.Volume * density;
            var d = c - com;
            double[] own = [m.Ixx, m.Ixy, m.Ixz, m.Ixy, m.Iyy, m.Iyz, m.Ixz, m.Iyz, m.Izz];
            double[] dv = [d.X, d.Y, d.Z];
            var d2 = d.LengthSquared;
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    inertia[(3 * i) + j] += (own[(3 * i) + j] * density) + (bodyMass * (((i == j) ? d2 : 0) - (dv[i] * dv[j])));
                }
            }
        }

        return new PartMassProperties(volume, area, mass, com, inertia);
    }

    /// <summary>
    /// Moves a feature up (negative) or down (positive) in the history, unless
    /// that would put it before something it uses or after something that uses it.
    /// </summary>
    public bool Move(Feature feature, int delta)
    {
        ArgumentNullException.ThrowIfNull(feature);
        var from = Features.IndexOf(feature);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Features.Count || delta == 0)
        {
            return false;
        }

        var lo = Math.Min(from, to);
        var hi = Math.Max(from, to);
        var between = Features.GetRange(lo, hi - lo + 1).Where(f => f != feature).ToList();
        if (delta < 0 && between.Any(f => feature.Dependencies.Contains(f.Id)))
        {
            return false;
        }

        if (delta > 0 && between.Any(f => f.Dependencies.Contains(feature.Id)))
        {
            return false;
        }

        Features.RemoveAt(from);
        Features.Insert(to, feature);
        return true;
    }

    private static string Fingerprint(Feature f, string previous, bool suppressed) =>
        $"{previous.GetHashCode(StringComparison.Ordinal):x8}:{suppressed}:{JsonSerializer.Serialize(f, Options)}";

    // ------------------------------------------------------------ persistence

    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed class PartDto
    {
        public int Version { get; set; }
        public string Format { get; set; } = "chamfer-part";
        public Material? Material { get; set; }
        public int? RollbackIndex { get; set; }
        public int NextId { get; set; }
        public List<GlobalVariable>? Variables { get; set; }
        public List<Configuration>? Configurations { get; set; }
        public string? ActiveConfiguration { get; set; }
        public List<CustomProperty>? Properties { get; set; }
        public List<MateReference>? MateReferences { get; set; }
        public Geometry.LengthUnit? Units { get; set; }
        public List<Feature> Features { get; set; } = [];
    }

    public string Save() => JsonSerializer.Serialize(new PartDto
    {
        Version = FormatVersion,
        Material = Material,
        RollbackIndex = RollbackIndex,
        NextId = _nextId,
        Variables = Variables.Count > 0 ? Variables : null,
        Configurations = Configurations.Count > 1 || Configurations[0].Variables.Count > 0 || Configurations[0].Suppression.Count > 0 || Configurations[0].Name != "Default" ? Configurations : null,
        ActiveConfiguration = ActiveConfiguration == "Default" ? null : ActiveConfiguration,
        Properties = Properties.Count > 0 ? Properties : null,
        MateReferences = MateReferences.Count > 0 ? MateReferences : null,
        Units = Units == Geometry.LengthUnit.Millimeter ? null : Units,
        Features = Features,
    }, Options);

    public static PartDocument Load(string json)
    {
        var dto = JsonSerializer.Deserialize<PartDto>(json, Options) ?? throw new JsonException("Empty part document.");
        if (dto.Version > FormatVersion)
        {
            throw new NotSupportedException($"This part was saved by a newer version of Chamfer (format {dto.Version}).");
        }

        var doc = new PartDocument
        {
            Material = dto.Material ?? Material.Steel,
            RollbackIndex = dto.RollbackIndex,
        };
        doc.Features.AddRange(dto.Features);
        doc.Variables.AddRange(dto.Variables ?? []);
        doc.Properties.AddRange(dto.Properties ?? []);
        doc.MateReferences.AddRange(dto.MateReferences ?? []);
        doc.Units = dto.Units ?? Geometry.LengthUnit.Millimeter;
        if (dto.Configurations is { Count: > 0 } configs)
        {
            doc.Configurations.Clear();
            doc.Configurations.AddRange(configs);
        }

        doc.ActiveConfiguration = dto.ActiveConfiguration ?? doc.Configurations[0].Name;
        doc._nextId = Math.Max(dto.NextId, doc.Features.Count + 1);
        return doc;
    }

    /// <summary>Writes all bodies to a STEP, IGES, STL or BRep file, chosen by extension.</summary>
    public void Export(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Bodies.Count == 0)
        {
            throw new InvalidOperationException("There is nothing to export.");
        }

        var ext = Path.GetExtension(path).ToUpperInvariant();
        if (ext is ".OBJ" or ".3MF")
        {
            var meshes = Bodies.Select(b => (b.Name, b.Shape.Tessellate(0.02, 0.2))).ToList();
            if (ext == ".OBJ")
            {
                File.WriteAllText(path, MeshExport.Obj(meshes));
            }
            else
            {
                File.WriteAllBytes(path, MeshExport.ThreeMf(meshes));
            }

            return;
        }

        using var all = Modeling.Compound([.. Bodies.Select(b => b.Shape)]);
        switch (ext)
        {
            case ".STEP" or ".STP": all.WriteStep(path); break;
            case ".IGES" or ".IGS": all.WriteIges(path); break;
            case ".STL": all.WriteStl(path, 0.02); break;
            case ".BREP" or ".BRP": all.WriteBrep(path); break;
            default: throw new NotSupportedException($"Can't export {ext} files.");
        }
    }

    /// <summary>One feature as JSON (the form macros record).</summary>
    public static string SerializeFeature(Feature feature) => JsonSerializer.Serialize(feature, Options).Replace("\r\n", "\n", StringComparison.Ordinal);

    public static Feature DeserializeFeature(string json) =>
        JsonSerializer.Deserialize<Feature>(json, Options) ?? throw new JsonException("Empty feature.");

    /// <summary>Deep copy through the file format, e.g. for undo snapshots or background rebuilds.</summary>
    public PartDocument Clone() => Load(Save());
}
