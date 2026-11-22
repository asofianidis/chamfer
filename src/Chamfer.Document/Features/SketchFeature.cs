using System.Text.Json;
using System.Text.Json.Serialization;
using Chamfer.Sketching;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Document.Features;

/// <summary>A 2D sketch placed on a plane or planar face.</summary>
public sealed class SketchFeature : Feature
{
    public override string DefaultName => "Sketch";

    public PlaneRef Plane { get; set; } = PlaneRef.Front;

    [JsonIgnore]
    public Sketch Sketch { get; set; } = new();

    /// <summary>The sketch in its own JSON format, for the part file.</summary>
    public JsonElement SketchData
    {
        get => JsonElement.Parse(SketchSerializer.Serialize(Sketch));
        set => Sketch = SketchSerializer.Deserialize(value.GetRawText());
    }

    /// <summary>Sketch dimensions driven by equations: constraint id → expression (angles in degrees).</summary>
    public Dictionary<int, string>? DimensionLinks { get; set; }

    public override IEnumerable<string> Dependencies => Plane.PlaneId is { } id && id is not ("front" or "top" or "right") ? [id] : [];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Sketches[Id] = new PlacedSketch(ctx.ResolvePlane(Plane), Sketch);
    }
}
