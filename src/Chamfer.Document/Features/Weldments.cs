using System.Globalization;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching.Entities;

namespace Chamfer.Document.Features;

public enum ProfileKind
{
    SquareTube,
    RectangularTube,
    Angle,
    Channel,
    Beam,
    RoundTube,
    FlatBar,
}

/// <summary>A standard structural profile: H × W × T (and flange thickness for channels and beams), in mm.</summary>
public sealed record StructuralProfile(ProfileKind Kind, string Name, double H, double W, double T, double Tf = 0)
{
    public string Description => Kind switch
    {
        ProfileKind.Channel or ProfileKind.Beam => Name,
        ProfileKind.RoundTube => string.Create(CultureInfo.InvariantCulture, $"Tube Ø{H:0.#}x{T:0.#}"),
        ProfileKind.FlatBar => string.Create(CultureInfo.InvariantCulture, $"Flat {H:0.#}x{W:0.#}"),
        ProfileKind.Angle => string.Create(CultureInfo.InvariantCulture, $"Angle {H:0.#}x{W:0.#}x{T:0.#}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"Tube {H:0.#}x{W:0.#}x{T:0.#}"),
    };

    /// <summary>Closed loops (outer first) of the section, centred on the origin, in (across, up) coordinates.</summary>
    public IReadOnlyList<IReadOnlyList<ProfileSegment>> Loops()
    {
        static List<ProfileSegment> Poly(params (double X, double Y)[] p) =>
            [.. Enumerable.Range(0, p.Length).Select(i => ProfileSegment.Line(p[i].X, p[i].Y, p[(i + 1) % p.Length].X, p[(i + 1) % p.Length].Y))];
        static List<ProfileSegment> Rect(double w, double h) => Poly((-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2));
        var (w, h, t, tf) = (W, H, T, Tf);
        return Kind switch
        {
            ProfileKind.SquareTube or ProfileKind.RectangularTube => [Rect(w, h), Rect(w - (2 * t), h - (2 * t))],
            ProfileKind.FlatBar => [Rect(w, h)],
            ProfileKind.RoundTube => [[ProfileSegment.Circle(0, 0, h / 2)], [ProfileSegment.Circle(0, 0, (h / 2) - t)]],
            ProfileKind.Angle => [Poly((-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, (-h / 2) + t), ((-w / 2) + t, (-h / 2) + t), ((-w / 2) + t, h / 2), (-w / 2, h / 2))],
            ProfileKind.Channel => [Poly((-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, (-h / 2) + tf), ((-w / 2) + t, (-h / 2) + tf),
                ((-w / 2) + t, (h / 2) - tf), (w / 2, (h / 2) - tf), (w / 2, h / 2), (-w / 2, h / 2))],
            _ => [Poly((-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, (-h / 2) + tf), (t / 2, (-h / 2) + tf), (t / 2, (h / 2) - tf),
                (w / 2, (h / 2) - tf), (w / 2, h / 2), (-w / 2, h / 2), (-w / 2, (h / 2) - tf), (-t / 2, (h / 2) - tf),
                (-t / 2, (-h / 2) + tf), (-w / 2, (-h / 2) + tf))],
        };
    }

    /// <summary>Common ISO / EN sizes.</summary>
    public static IReadOnlyList<StructuralProfile> Library { get; } =
    [
        new(ProfileKind.SquareTube, "20x20x2", 20, 20, 2), new(ProfileKind.SquareTube, "25x25x2", 25, 25, 2),
        new(ProfileKind.SquareTube, "30x30x3", 30, 30, 3), new(ProfileKind.SquareTube, "40x40x3", 40, 40, 3),
        new(ProfileKind.SquareTube, "50x50x4", 50, 50, 4), new(ProfileKind.SquareTube, "80x80x5", 80, 80, 5),
        new(ProfileKind.RectangularTube, "40x20x2", 40, 20, 2), new(ProfileKind.RectangularTube, "60x40x3", 60, 40, 3),
        new(ProfileKind.RectangularTube, "100x50x5", 100, 50, 5),
        new(ProfileKind.Angle, "30x30x3", 30, 30, 3), new(ProfileKind.Angle, "40x40x4", 40, 40, 4), new(ProfileKind.Angle, "50x50x5", 50, 50, 5),
        new(ProfileKind.Channel, "UPN 80", 80, 45, 6, 8), new(ProfileKind.Channel, "UPN 100", 100, 50, 6, 8.5),
        new(ProfileKind.Beam, "IPE 80", 80, 46, 3.8, 5.2), new(ProfileKind.Beam, "IPE 100", 100, 55, 4.1, 5.7),
        new(ProfileKind.Beam, "IPE 160", 160, 82, 5, 7.4), new(ProfileKind.Beam, "IPE 200", 200, 100, 5.6, 8.5),
        new(ProfileKind.RoundTube, "33.7x3.2", 33.7, 33.7, 3.2), new(ProfileKind.RoundTube, "48.3x3.2", 48.3, 48.3, 3.2),
        new(ProfileKind.FlatBar, "40x8", 8, 40, 8), new(ProfileKind.FlatBar, "50x10", 10, 50, 10),
    ];
}

/// <summary>
/// Weldment structural members: one body per straight sketch line, with the
/// chosen profile centred on the line. Members that meet end to end are
/// mitred on the bisector plane.
/// </summary>
public sealed class StructuralMemberFeature : Feature
{
    public override string DefaultName => "Structural Member";

    public string SketchId { get; set; } = string.Empty;

    public ProfileKind Kind { get; set; } = ProfileKind.SquareTube;

    /// <summary>Profile size name from the library, e.g. "40x40x3" or "IPE 100".</summary>
    public string Size { get; set; } = "40x40x3";

    /// <summary>Rotates the profile about the member axis, degrees.</summary>
    public double Rotation { get; set; }

    public override IEnumerable<string> Dependencies => [SketchId];

    public StructuralProfile Profile =>
        StructuralProfile.Library.FirstOrDefault(p => p.Kind == Kind && p.Name == Size)
        ?? StructuralProfile.Library.First(p => p.Kind == Kind);

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var sketch = ctx.Sketch(SketchId);
        var lines = sketch.Sketch.Entities.OfType<SketchLine>().Where(l => !l.IsConstruction && l.Length > 1e-9).ToList();
        if (lines.Count == 0)
        {
            throw new FeatureException("Sketch the member centre lines first.");
        }

        var profile = Profile;
        var reach = Math.Sqrt((profile.H * profile.H) + (profile.W * profile.W));
        var segments = lines.Select(l => (A: sketch.Plane.ToWorld(l.Start.X, l.Start.Y), B: sketch.Plane.ToWorld(l.End.X, l.End.Y))).ToList();
        var rot = Rotation * Math.PI / 180;
        var index = 0;
        foreach (var (a, b) in segments)
        {
            index++;
            var dir = (b - a).Normalized();
            var across0 = Vec3.Cross(sketch.Plane.Normal, dir).Normalized();
            var up0 = sketch.Plane.Normal;
            var across = (across0 * Math.Cos(rot)) + (up0 * Math.Sin(rot));

            // Joints: other members sharing an end get a miter; free ends stay square.
            var startJoint = Partner(segments, a, (a, b));
            var endJoint = Partner(segments, b, (a, b));
            var start = startJoint is null ? a : a - (dir * reach);
            var end = endJoint is null ? b : b + (dir * reach);
            var plane = new Plane3(start, dir, across);
            using var face = Modeling.Face(plane, profile.Loops());
            var op = Modeling.Extrude(face, end - start);
            var member = op.Result;
            op.Dispose();
            if (startJoint is { } sj)
            {
                member = Miter(member, a, -dir, sj, (a + b) * 0.5);
            }

            if (endJoint is { } ej)
            {
                member = Miter(member, b, dir, ej, (a + b) * 0.5);
            }

            var names = Naming.Finish(member, new string?[member.FaceCount], $"{Id}:m{index}");
            ctx.Bodies.Add(new Body(member, names, $"{profile.Description} <{index}>"));
        }
    }

    /// <summary>The outward direction of the other member meeting at <paramref name="p"/>, if any.</summary>
    private static Vec3? Partner(List<(Vec3 A, Vec3 B)> segments, Vec3 p, (Vec3 A, Vec3 B) self)
    {
        foreach (var s in segments)
        {
            if (s == self)
            {
                continue;
            }

            if (Vec3.Distance(s.A, p) < 1e-6)
            {
                return (s.B - s.A).Normalized();
            }

            if (Vec3.Distance(s.B, p) < 1e-6)
            {
                return (s.A - s.B).Normalized();
            }
        }

        return null;
    }

    /// <summary>Cuts a member at the bisector plane of a corner, keeping the side its middle is on.</summary>
    private static Shape Miter(Shape member, Vec3 corner, Vec3 outward, Vec3 otherOutward, Vec3 middle)
    {
        var normal = (outward - otherOutward).Normalized();
        if (normal.LengthSquared < 0.5)
        {
            return member; // collinear: nothing to cut
        }

        using var tool = Modeling.PlaneFace(new Plane3(corner, normal, normal.AnyPerpendicular()), (member.Bounds.Diagonal * 2) + 10);
        using var split = Modeling.Split(member, tool);
        var whole = split.Result;
        Shape? keep = null;
        for (var i = 1; i <= whole.SolidCount; i++)
        {
            var piece = whole.Sub(TopoKind.Solid, i);
            if (Vec3.Dot(piece.MassProperties.CenterOfMass - corner, normal) * Vec3.Dot(middle - corner, normal) > 0)
            {
                keep = piece;
            }
            else
            {
                piece.Dispose();
            }
        }

        return keep ?? member;
    }
}

/// <summary>A weldment cut-list line: identical members grouped.</summary>
public sealed record CutListItem(int Item, string Description, double Length, int Quantity, double MassEach);

public static class CutList
{
    /// <summary>Groups structural member bodies by profile and length (to 0.1 mm).</summary>
    public static IReadOnlyList<CutListItem> Build(PartDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var members = doc.Bodies.Where(b => b.Name.Contains(" <", StringComparison.Ordinal)).Select(b =>
        {
            var name = b.Name[..b.Name.LastIndexOf(" <", StringComparison.Ordinal)];
            var mass = b.Shape.MassProperties;
            var length = LongestSide(b.Shape);
            return (Name: name, Length: Math.Round(length, 1), Mass: mass.Volume * doc.Material.Density * 1e-9);
        });
        return [.. members.GroupBy(m => (m.Name, m.Length)).Select((g, i) => new CutListItem(i + 1, g.Key.Name, g.Key.Length, g.Count(), g.First().Mass))];
    }

    private static double LongestSide(Shape s)
    {
        var size = s.Bounds.Size;
        return Math.Max(size.X, Math.Max(size.Y, size.Z));
    }
}
