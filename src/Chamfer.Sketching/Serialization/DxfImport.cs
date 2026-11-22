using System.Globalization;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.Sketching.Serialization;

/// <summary>What a DXF import added to a sketch.</summary>
public sealed record DxfImportResult(int Lines, int Arcs, int Circles, int Points, int Skipped, double Scale)
{
    public int Total => Lines + Arcs + Circles + Points;
}

/// <summary>
/// Reads 2D geometry from an ASCII DXF file into a sketch: lines, circles, arcs,
/// light-weight and classic polylines (with bulge arcs), points and splines
/// (through their fit or control points). Units come from <c>$INSUNITS</c> and
/// are converted to millimetres. Endpoints that meet get coincident relations.
/// </summary>
public static class DxfImport
{
    public static DxfImportResult Import(Sketch sketch, string path, Vec2 offset = default)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        return ImportText(sketch, File.ReadAllText(path), offset);
    }

    public static DxfImportResult ImportText(Sketch sketch, string text, Vec2 offset = default)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(text);
        var pairs = Pairs(text);
        var scale = UnitScale(pairs);
        var counts = new int[5]; // lines, arcs, circles, points, skipped
        var ends = new List<SketchPoint>();
        Vec2 P(double x, double y) => new Vec2((x * scale) + offset.X, (y * scale) + offset.Y);

        void Line(Vec2 a, Vec2 b)
        {
            if (Vec2.Distance(a, b) < 1e-9)
            {
                return;
            }

            var l = sketch.AddLine(a, b);
            ends.Add(l.Start);
            ends.Add(l.End);
            counts[0]++;
        }

        void Arc(Vec2 c, double r, double startDeg, double endDeg)
        {
            var s = startDeg * Math.PI / 180;
            var e = endDeg * Math.PI / 180;
            var arc = sketch.AddArc(c, c + new Vec2(r * Math.Cos(s), r * Math.Sin(s)), c + new Vec2(r * Math.Cos(e), r * Math.Sin(e)));
            ends.Add(arc.Start);
            ends.Add(arc.End);
            counts[1]++;
        }

        // A polyline segment from a to b with a bulge (tan of a quarter of the included angle, positive = CCW).
        void Segment(Vec2 a, Vec2 b, double bulge)
        {
            if (Math.Abs(bulge) < 1e-12)
            {
                Line(a, b);
                return;
            }

            var chord = Vec2.Distance(a, b);
            if (chord < 1e-9)
            {
                return;
            }

            var theta = 4 * Math.Atan(bulge);
            var r = chord / (2 * Math.Sin(Math.Abs(theta) / 2));
            var mid = (a + b) * 0.5;
            var dir = (b - a) / chord;
            var normal = new Vec2(-dir.Y, dir.X);
            var h = Math.Sqrt(Math.Max(0, (r * r) - (chord * chord / 4)));

            // For a CCW bulge under a half turn the centre is to the left of the chord.
            var side = Math.Sign(bulge) * (Math.Abs(theta) < Math.PI ? 1 : -1);
            var c = mid + (normal * (h * side));
            var (from, to) = bulge > 0 ? (a, b) : (b, a);
            var arc = sketch.AddArc(c, from, to);
            ends.Add(arc.Start);
            ends.Add(arc.End);
            counts[1]++;
        }

        void Polyline(List<(Vec2 P, double Bulge)> verts, bool closed)
        {
            for (var i = 0; i + 1 < verts.Count; i++)
            {
                Segment(verts[i].P, verts[i + 1].P, verts[i].Bulge);
            }

            if (closed && verts.Count > 2)
            {
                Segment(verts[^1].P, verts[0].P, verts[^1].Bulge);
            }
        }

        var i = 0;

        // Only the ENTITIES section (and BLOCKS are ignored).
        while (i < pairs.Count && !(pairs[i].Code == 2 && pairs[i].Value == "ENTITIES"))
        {
            i++;
        }

        while (i < pairs.Count)
        {
            if (pairs[i].Code != 0)
            {
                i++;
                continue;
            }

            var type = pairs[i].Value;
            if (type is "ENDSEC" or "EOF")
            {
                break;
            }

            var start = ++i;
            while (i < pairs.Count && pairs[i].Code != 0)
            {
                i++;
            }

            var e = pairs.GetRange(start, i - start);
            double D(int code, double fallback = 0) => e.FirstOrDefault(p => p.Code == code) is { Value: { } v } && Num(v) is { } n ? n : fallback;
            switch (type)
            {
                case "LINE":
                    Line(P(D(10), D(20)), P(D(11), D(21)));
                    break;
                case "CIRCLE":
                    sketch.AddCircle(P(D(10), D(20)), D(40) * scale);
                    counts[2]++;
                    break;
                case "ARC":
                    Arc(P(D(10), D(20)), D(40) * scale, D(50), D(51));
                    break;
                case "POINT":
                    sketch.AddPoint(P(D(10), D(20)));
                    counts[3]++;
                    break;
                case "LWPOLYLINE":
                {
                    var verts = new List<(Vec2, double)>();
                    double? x = null;
                    foreach (var (code, value) in e)
                    {
                        if (code == 10)
                        {
                            x = Num(value);
                        }
                        else if (code == 20 && x is { } vx)
                        {
                            verts.Add((P(vx, Num(value) ?? 0), 0));
                            x = null;
                        }
                        else if (code == 42 && verts.Count > 0)
                        {
                            verts[^1] = (verts[^1].Item1, Num(value) ?? 0);
                        }
                    }

                    Polyline(verts, ((int)D(70) & 1) != 0);
                    break;
                }

                case "POLYLINE":
                {
                    var closed = ((int)D(70) & 1) != 0;
                    var verts = new List<(Vec2, double)>();
                    while (i < pairs.Count && pairs[i].Code == 0 && pairs[i].Value == "VERTEX")
                    {
                        var vs = ++i;
                        while (i < pairs.Count && pairs[i].Code != 0)
                        {
                            i++;
                        }

                        var v = pairs.GetRange(vs, i - vs);
                        double V(int code) => v.FirstOrDefault(p => p.Code == code) is { Value: { } s } && Num(s) is { } n ? n : 0;
                        verts.Add((P(V(10), V(20)), V(42)));
                    }

                    if (i < pairs.Count && pairs[i].Value == "SEQEND")
                    {
                        i++;
                        while (i < pairs.Count && pairs[i].Code != 0)
                        {
                            i++;
                        }
                    }

                    Polyline(verts, closed);
                    break;
                }

                case "SPLINE":
                {
                    // Approximate through fit points when present, else the control polygon.
                    var fit = Points(e, 11, 21);
                    var pts = fit.Count >= 2 ? fit : Points(e, 10, 20);
                    for (var k = 0; k + 1 < pts.Count; k++)
                    {
                        Line(P(pts[k].X, pts[k].Y), P(pts[k + 1].X, pts[k + 1].Y));
                    }

                    if (((int)D(70) & 1) != 0 && pts.Count > 2)
                    {
                        Line(P(pts[^1].X, pts[^1].Y), P(pts[0].X, pts[0].Y));
                    }

                    break;
                }

                default:
                    counts[4]++;
                    break;
            }
        }

        Connect(sketch, ends);
        return new DxfImportResult(counts[0], counts[1], counts[2], counts[3], counts[4], scale);
    }

    /// <summary>Adds coincident relations between endpoints that touch (and snaps them together).</summary>
    private static void Connect(Sketch sketch, List<SketchPoint> ends)
    {
        var tol = 1e-6 * Math.Max(1, ends.Count == 0 ? 1 : ends.Max(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y))));
        var joined = new bool[ends.Count];
        for (var a = 0; a < ends.Count; a++)
        {
            if (joined[a])
            {
                continue;
            }

            for (var b = a + 1; b < ends.Count; b++)
            {
                if (joined[b] || ReferenceEquals(ends[a].Owner, ends[b].Owner))
                {
                    continue;
                }

                if (Vec2.Distance(ends[a].Position, ends[b].Position) < tol)
                {
                    ends[b].Position = ends[a].Position;
                    sketch.AddConstraint(new CoincidentConstraint(ends[a], ends[b]));
                    joined[b] = true;
                }
            }
        }
    }

    private static List<(double X, double Y)> Points(List<(int Code, string Value)> e, int xCode, int yCode)
    {
        var list = new List<(double, double)>();
        double? x = null;
        foreach (var (code, value) in e)
        {
            if (code == xCode)
            {
                x = Num(value);
            }
            else if (code == yCode && x is { } vx)
            {
                list.Add((vx, Num(value) ?? 0));
                x = null;
            }
        }

        return list;
    }

    /// <summary>Millimetres per drawing unit from <c>$INSUNITS</c> (unitless drawings are taken as mm).</summary>
    private static double UnitScale(List<(int Code, string Value)> pairs)
    {
        var at = pairs.FindIndex(p => p.Code == 9 && p.Value == "$INSUNITS");
        var units = at >= 0 && at + 1 < pairs.Count && int.TryParse(pairs[at + 1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 4;
        return units switch
        {
            1 => 25.4,          // inches
            2 => 304.8,         // feet
            5 => 10,            // centimetres
            6 => 1000,          // metres
            8 => 25.4e-6,       // microinches
            9 => 0.0254,        // mils
            10 => 914.4,        // yards
            13 => 1e-3,         // microns
            14 => 100,          // decimetres
            _ => 1,             // millimetres or unitless
        };
    }

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static List<(int Code, string Value)> Pairs(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var pairs = new List<(int, string)>(lines.Length / 2);
        for (var k = 0; k + 1 < lines.Length; k += 2)
        {
            if (int.TryParse(lines[k].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            {
                pairs.Add((code, lines[k + 1].Trim()));
            }
        }

        return pairs;
    }
}
