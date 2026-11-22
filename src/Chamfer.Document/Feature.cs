using System.Text.Json.Serialization;
using Chamfer.Document.Features;

namespace Chamfer.Document;

public enum FeatureStatus
{
    Ok,
    Warning,
    Error,
    Suppressed,
    RolledBack,
}

/// <summary>A feature failed to build. The message is shown to the user.</summary>
public sealed class FeatureException : Exception
{
    public FeatureException()
    {
    }

    public FeatureException(string message)
        : base(message)
    {
    }

    public FeatureException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// One step of a part's history. Features are plain parameter objects; they
/// build geometry only through <see cref="Execute"/>, so a document can be
/// rebuilt from its features alone.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "feature")]
[JsonDerivedType(typeof(SketchFeature), "sketch")]
[JsonDerivedType(typeof(ExtrudeFeature), "extrude")]
[JsonDerivedType(typeof(RevolveFeature), "revolve")]
[JsonDerivedType(typeof(FilletFeature), "fillet")]
[JsonDerivedType(typeof(ChamferFeature), "chamfer")]
[JsonDerivedType(typeof(ShellFeature), "shell")]
[JsonDerivedType(typeof(DraftFeature), "draft")]
[JsonDerivedType(typeof(HoleFeature), "hole")]
[JsonDerivedType(typeof(ReferencePlaneFeature), "plane")]
[JsonDerivedType(typeof(LinearPatternFeature), "linearPattern")]
[JsonDerivedType(typeof(CircularPatternFeature), "circularPattern")]
[JsonDerivedType(typeof(MirrorFeature), "mirror")]
[JsonDerivedType(typeof(ImportFeature), "import")]
[JsonDerivedType(typeof(SweepFeature), "sweep")]
[JsonDerivedType(typeof(LoftFeature), "loft")]
[JsonDerivedType(typeof(RibFeature), "rib")]
[JsonDerivedType(typeof(CombineFeature), "combine")]
[JsonDerivedType(typeof(SplitBodyFeature), "splitBody")]
[JsonDerivedType(typeof(DeleteBodyFeature), "deleteBody")]
[JsonDerivedType(typeof(MoveCopyBodyFeature), "moveBody")]
[JsonDerivedType(typeof(AppearanceFeature), "appearance")]
[JsonDerivedType(typeof(SurfaceExtrudeFeature), "surfaceExtrude")]
[JsonDerivedType(typeof(SurfaceRevolveFeature), "surfaceRevolve")]
[JsonDerivedType(typeof(PlanarSurfaceFeature), "planarSurface")]
[JsonDerivedType(typeof(FilledSurfaceFeature), "fillSurface")]
[JsonDerivedType(typeof(OffsetSurfaceFeature), "offsetSurface")]
[JsonDerivedType(typeof(KnitFeature), "knit")]
[JsonDerivedType(typeof(ThickenFeature), "thicken")]
public abstract class Feature
{
    /// <summary>Stable identifier used by references and persistent names. Never shown to users.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name in the feature tree, e.g. "Boss-Extrude1".</summary>
    public string Name { get; set; } = string.Empty;

    public bool IsSuppressed { get; set; }

    /// <summary>
    /// Parameters driven by equations: property name → expression over the
    /// document's global variables (e.g. "Depth" → "Thickness * 2").
    /// </summary>
    public Dictionary<string, string>? Links { get; set; }

    /// <summary>Base for display names ("Boss-Extrude", "Fillet", ...).</summary>
    [JsonIgnore]
    public abstract string DefaultName { get; }

    /// <summary>Ids of earlier features this one reads (sketches, seeds, planes).</summary>
    [JsonIgnore]
    public virtual IEnumerable<string> Dependencies => [];

    /// <summary>Builds the feature's geometry into the context. Throws <see cref="FeatureException"/> on failure.</summary>
    public abstract void Execute(RebuildContext ctx);
}
