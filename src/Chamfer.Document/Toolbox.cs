using System.Globalization;
using Chamfer.Document.Features;
using Chamfer.Sketching;

namespace Chamfer.Document;

public enum FastenerKind
{
    SocketHeadCapScrew,
    HexBolt,
    HexNut,
    Washer,
}

/// <summary>A standard fastener specification, e.g. ISO 4762 M6 × 20.</summary>
public sealed record FastenerSpec(FastenerKind Kind, string Size, double Length = 0)
{
    public string Standard => Kind switch
    {
        FastenerKind.SocketHeadCapScrew => "ISO 4762",
        FastenerKind.HexBolt => "ISO 4017",
        FastenerKind.HexNut => "ISO 4032",
        _ => "ISO 7089",
    };

    public string Description => Kind is FastenerKind.SocketHeadCapScrew or FastenerKind.HexBolt
        ? string.Create(CultureInfo.InvariantCulture, $"{Standard} {Size}x{Length:0.#}")
        : $"{Standard} {Size}";

    /// <summary>The virtual path assemblies use for this part, e.g. "toolbox:ISO4762 M6x20".</summary>
    public string Path => "toolbox:" + Description.Replace("ISO ", "ISO", StringComparison.Ordinal);

    public static FastenerSpec? Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var text = path.StartsWith("toolbox:", StringComparison.Ordinal) ? path[8..] : path;
        var parts = text.Split(' ', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        var kind = parts[0] switch
        {
            "ISO4762" => FastenerKind.SocketHeadCapScrew,
            "ISO4017" => FastenerKind.HexBolt,
            "ISO4032" => FastenerKind.HexNut,
            "ISO7089" => FastenerKind.Washer,
            _ => (FastenerKind?)null,
        };
        if (kind is null)
        {
            return null;
        }

        var sizeLen = parts[1].Split('x');
        var length = sizeLen.Length > 1 && double.TryParse(sizeLen[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var l) ? l : 0;
        return new FastenerSpec(kind.Value, sizeLen[0], length);
    }
}

/// <summary>
/// Standard hardware generated as ordinary parametric parts (sketches,
/// extrusions, revolves), so they can be inspected, edited and exported like
/// any other part. Dimensions follow the ISO standards; threads are shown at
/// the nominal diameter.
/// </summary>
public static class Toolbox
{
    /// <summary>Metric sizes with ISO 4762 head (dk, k, s, t), ISO 4032/4017 hex (s, nut m, bolt k) and ISO 7089 washer (d1, d2, h).</summary>
    private static readonly Dictionary<string, (double D, double Dk, double K, double S, double T, double HexS, double NutM, double BoltK, double W1, double W2, double Wh)> Sizes = new()
    {
        ["M3"] = (3, 5.5, 3, 2.5, 1.3, 5.5, 2.4, 2, 3.2, 7, 0.5),
        ["M4"] = (4, 7, 4, 3, 2, 7, 3.2, 2.8, 4.3, 9, 0.8),
        ["M5"] = (5, 8.5, 5, 4, 2.5, 8, 4.7, 3.5, 5.3, 10, 1),
        ["M6"] = (6, 10, 6, 5, 3, 10, 5.2, 4, 6.4, 12, 1.6),
        ["M8"] = (8, 13, 8, 6, 4, 13, 6.8, 5.3, 8.4, 16, 1.6),
        ["M10"] = (10, 16, 10, 8, 5, 16, 8.4, 6.4, 10.5, 20, 2),
        ["M12"] = (12, 18, 12, 10, 6, 18, 10.8, 7.5, 13, 24, 2.5),
        ["M16"] = (16, 24, 16, 14, 8, 24, 14.8, 10, 17, 30, 3),
        ["M20"] = (20, 30, 20, 17, 10, 30, 18, 12.5, 21, 37, 3),
    };

    public static IReadOnlyList<string> SizeNames { get; } = [.. Sizes.Keys];

    public static IReadOnlyList<double> StandardLengths { get; } = [6, 8, 10, 12, 16, 20, 25, 30, 35, 40, 45, 50, 60, 70, 80, 90, 100];

    public static bool IsToolboxPath(string path) => path.StartsWith("toolbox:", StringComparison.Ordinal);

    public static PartDocument Create(string path) =>
        Create(FastenerSpec.Parse(path) ?? throw new ArgumentException($"Unknown toolbox part '{path}'.", nameof(path)));

    /// <summary>
    /// Builds the part. Screws and bolts have the underside of the head at the
    /// origin with the shank along -Z; nuts and washers sit on the XY plane
    /// around the Z axis, so a concentric mate and a coincident mate place them.
    /// </summary>
    public static PartDocument Create(FastenerSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!Sizes.TryGetValue(spec.Size, out var z))
        {
            throw new ArgumentException($"Unknown size {spec.Size}.", nameof(spec));
        }

        var doc = new PartDocument { Material = Material.Steel };
        switch (spec.Kind)
        {
            case FastenerKind.SocketHeadCapScrew:
                Disc(doc, z.Dk / 2, z.K, reverse: false, "Head");
                Disc(doc, z.D / 2, spec.Length, reverse: true, "Shank");
                var socket = new Sketch();
                socket.AddPolygon(Vec2.Zero, new Vec2(z.S / Math.Sqrt(3), 0), 6);
                var top = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Front, Offset = z.K, Name = "Head top" });
                var sf = doc.Add(new SketchFeature { Plane = new PlaneRef(top.Id), Sketch = socket, Name = "Socket" });
                doc.Add(new ExtrudeFeature { SketchId = sf.Id, IsCut = true, Depth = z.T, Name = "Hex socket" });
                break;
            case FastenerKind.HexBolt:
                Hex(doc, z.HexS, z.BoltK, reverse: false, "Head");
                Disc(doc, z.D / 2, spec.Length, reverse: true, "Shank");
                break;
            case FastenerKind.HexNut:
                Hex(doc, z.HexS, z.NutM, reverse: false, "Body");
                Hole(doc, z.D / 2, z.NutM);
                break;
            case FastenerKind.Washer:
                Disc(doc, z.W2 / 2, z.Wh, reverse: false, "Washer");
                Hole(doc, z.W1 / 2, z.Wh);
                break;
        }

        doc.Rebuild();
        return doc;
    }

    private static void Disc(PartDocument doc, double radius, double height, bool reverse, string name)
    {
        var s = new Sketch();
        s.AddCircle(Vec2.Zero, radius);
        var sf = doc.Add(new SketchFeature { Sketch = s, Name = $"{name} sketch" });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = height, Reverse = reverse, Name = name });
    }

    private static void Hex(PartDocument doc, double acrossFlats, double height, bool reverse, string name)
    {
        var s = new Sketch();
        s.AddPolygon(Vec2.Zero, new Vec2(acrossFlats / Math.Sqrt(3), 0), 6);
        var sf = doc.Add(new SketchFeature { Sketch = s, Name = $"{name} sketch" });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = height, Reverse = reverse, Name = name });
    }

    private static void Hole(PartDocument doc, double radius, double height)
    {
        var s = new Sketch();
        s.AddCircle(Vec2.Zero, radius);
        var sf = doc.Add(new SketchFeature { Sketch = s, Name = "Bore sketch" });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, IsCut = true, Reverse = true, Depth = height, Name = "Bore" });
    }
}
