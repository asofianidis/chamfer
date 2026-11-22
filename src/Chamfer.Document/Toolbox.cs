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

    /// <summary>Deep groove ball bearing (ISO 15), sized by its designation, e.g. "6204".</summary>
    Bearing,
}

/// <summary>A standard fastener specification, e.g. ISO 4762 M6 × 20.</summary>
public sealed record FastenerSpec(FastenerKind Kind, string Size, double Length = 0)
{
    /// <summary>Inch (UNC) sizes such as "1/4-20" or "#10-24" follow the ASME standards.</summary>
    public bool IsInch => Size.Contains('-', StringComparison.Ordinal);

    public string Standard => (Kind, IsInch) switch
    {
        (FastenerKind.Bearing, _) => "ISO 15",
        (FastenerKind.SocketHeadCapScrew, false) => "ISO 4762",
        (FastenerKind.HexBolt, false) => "ISO 4017",
        (FastenerKind.HexNut, false) => "ISO 4032",
        (FastenerKind.Washer, false) => "ISO 7089",
        (FastenerKind.SocketHeadCapScrew, true) => "ASME B18.3",
        (FastenerKind.HexBolt, true) => "ASME B18.2.1",
        (FastenerKind.HexNut, true) => "ASME B18.2.2",
        _ => "ASME B18.22.1",
    };

    public string Description => Kind is FastenerKind.SocketHeadCapScrew or FastenerKind.HexBolt
        ? IsInch
            ? string.Create(CultureInfo.InvariantCulture, $"{Standard} {Size} x {Length / 25.4:0.###}in")
            : string.Create(CultureInfo.InvariantCulture, $"{Standard} {Size}x{Length:0.#}")
        : Kind == FastenerKind.Bearing ? $"Bearing {Size}" : $"{Standard} {Size}";

    /// <summary>The virtual path assemblies use for this part, e.g. "toolbox:ISO4762 M6x20" (lengths in mm).</summary>
    public string Path => "toolbox:" + (Kind == FastenerKind.Bearing ? $"Bearing {Size}"
        : Standard.Replace("ISO ", "ISO", StringComparison.Ordinal).Replace("ASME ", string.Empty, StringComparison.Ordinal) + " " + Size +
          (Kind is FastenerKind.SocketHeadCapScrew or FastenerKind.HexBolt ? string.Create(CultureInfo.InvariantCulture, $"x{Length:0.###}") : string.Empty));

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
            "ISO4762" or "B18.3" => FastenerKind.SocketHeadCapScrew,
            "ISO4017" or "B18.2.1" => FastenerKind.HexBolt,
            "ISO4032" or "B18.2.2" => FastenerKind.HexNut,
            "ISO7089" or "B18.22.1" => FastenerKind.Washer,
            "Bearing" => FastenerKind.Bearing,
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

        // ASME inch sizes (converted to mm): B18.3 socket head (A, H, J, T), B18.2.1/.2 hex (F, nut H, bolt H), SAE washer (ID, OD, T).
        ["#10-24"] = Inch(0.190, 0.312, 0.190, 0.156, 0.112, 0.375, 0.130, 0.140, 0.219, 0.500, 0.049),
        ["1/4-20"] = Inch(0.250, 0.375, 0.250, 0.188, 0.125, 0.438, 0.219, 0.163, 0.281, 0.625, 0.065),
        ["5/16-18"] = Inch(0.3125, 0.469, 0.312, 0.250, 0.156, 0.500, 0.266, 0.211, 0.344, 0.688, 0.065),
        ["3/8-16"] = Inch(0.375, 0.5625, 0.375, 0.312, 0.188, 0.5625, 0.328, 0.243, 0.406, 0.812, 0.065),
        ["1/2-13"] = Inch(0.500, 0.750, 0.500, 0.375, 0.250, 0.750, 0.438, 0.323, 0.531, 1.062, 0.095),
    };

    private static (double, double, double, double, double, double, double, double, double, double, double) Inch(
        double d, double dk, double k, double s, double t, double hexS, double nutM, double boltK, double w1, double w2, double wh) =>
        (d * 25.4, dk * 25.4, k * 25.4, s * 25.4, t * 25.4, hexS * 25.4, nutM * 25.4, boltK * 25.4, w1 * 25.4, w2 * 25.4, wh * 25.4);

    /// <summary>Deep groove ball bearings: bore d, outside diameter D, width B (mm).</summary>
    private static readonly Dictionary<string, (double D, double Outer, double Width)> Bearings = new()
    {
        ["608"] = (8, 22, 7),
        ["6000"] = (10, 26, 8),
        ["6001"] = (12, 28, 8),
        ["6002"] = (15, 32, 9),
        ["6003"] = (17, 35, 10),
        ["6004"] = (20, 42, 12),
        ["6200"] = (10, 30, 9),
        ["6201"] = (12, 32, 10),
        ["6202"] = (15, 35, 11),
        ["6203"] = (17, 40, 12),
        ["6204"] = (20, 47, 14),
        ["6205"] = (25, 52, 15),
        ["6206"] = (30, 62, 16),
    };

    public static IReadOnlyList<string> SizeNames { get; } = [.. Sizes.Keys];

    public static IReadOnlyList<string> BearingNames { get; } = [.. Bearings.Keys];

    public static IReadOnlyList<double> StandardLengths { get; } = [6, 8, 10, 12, 16, 20, 25, 30, 35, 40, 45, 50, 60, 70, 80, 90, 100];

    /// <summary>Inch screw lengths, in mm: 1/2, 3/4, 1, 1-1/4, 1-1/2, 2, 2-1/2 and 3 in.</summary>
    public static IReadOnlyList<double> InchLengths { get; } = [12.7, 19.05, 25.4, 31.75, 38.1, 50.8, 63.5, 76.2];

    /// <summary>The bore, outside diameter and width of a bearing designation.</summary>
    public static (double Bore, double Outer, double Width)? Bearing(string designation) =>
        Bearings.TryGetValue(designation, out var b) ? b : null;

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
        if (spec.Kind == FastenerKind.Bearing)
        {
            return BearingPart(spec.Size);
        }

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

    /// <summary>
    /// A bearing as one ring of its bore, outside diameter and width, sitting on the XY plane, with
    /// shallow grooves on both faces between the inner and outer rings.
    /// </summary>
    private static PartDocument BearingPart(string designation)
    {
        if (!Bearings.TryGetValue(designation, out var b))
        {
            throw new ArgumentException($"Unknown bearing {designation}.", nameof(designation));
        }

        var doc = new PartDocument { Material = Material.Steel };
        Disc(doc, b.Outer / 2, b.Width, reverse: false, "Rings");
        Hole(doc, b.D / 2, b.Width);
        var gapInner = (b.D / 2) + ((b.Outer - b.D) * 0.3);
        var gapOuter = (b.D / 2) + ((b.Outer - b.D) * 0.4);
        foreach (var (plane, name) in new[] { (PlaneRef.Front, "Front gap"), (null, "Back gap") })
        {
            var s = new Sketch();
            s.AddCircle(Vec2.Zero, gapOuter);
            s.AddCircle(Vec2.Zero, gapInner);
            var at = plane ?? new PlaneRef(doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Front, Offset = b.Width, Name = "Back face" }).Id);
            var sf = doc.Add(new SketchFeature { Plane = at, Sketch = s, Name = $"{name} sketch" });
            doc.Add(new ExtrudeFeature { SketchId = sf.Id, IsCut = true, Depth = 0.4, Reverse = plane is not null, Name = name });
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
