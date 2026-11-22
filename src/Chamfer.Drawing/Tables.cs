using System.Globalization;
using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Drawing;

/// <summary>A table of the holes seen face-on in a view, measured from the view's lower-left corner.</summary>
public sealed class HoleTable
{
    public string Id { get; set; } = string.Empty;

    public string ViewId { get; set; } = string.Empty;

    /// <summary>Top-left corner of the table on the sheet (mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }
}

/// <summary>One revision: letter, what changed, when and who approved it.</summary>
public sealed class Revision
{
    public string Rev { get; set; } = "A";

    public string Description { get; set; } = string.Empty;

    public string Date { get; set; } = string.Empty;

    public string Approved { get; set; } = string.Empty;
}

/// <summary>The sheet's revision history table.</summary>
/// <summary>A general table: any text in rows and columns, the first row as its header.</summary>
public sealed class GeneralTable
{
    public string Id { get; set; } = string.Empty;

    public string? Layer { get; set; }

    /// <summary>Top-left corner of the table on the sheet (mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    /// <summary>The cells, row by row; the first row is drawn as the header.</summary>
    public List<List<string>> Rows { get; set; } = [];

    /// <summary>Reads rows from text: one row per line, cells separated by "|".</summary>
    public static List<List<string>> Parse(string text) =>
        [.. (text ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n')
            .Where(line => line.Trim().Length > 0)
            .Select(line => line.Split('|').Select(c => c.Trim()).ToList())];

    /// <summary>The rows as text (the inverse of <see cref="Parse"/>).</summary>
    public string Format() => string.Join('\n', Rows.Select(r => string.Join(" | ", r)));
}

public sealed class RevisionTable
{
    /// <summary>Top-left corner on the sheet (mm).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public List<Revision> Rows { get; set; } = [];

    /// <summary>The next revision letter (A, B, ... Z, AA, ...).</summary>
    public string NextRev()
    {
        var n = Rows.Count;
        var s = string.Empty;
        do
        {
            s = (char)('A' + (n % 26)) + s;
            n = (n / 26) - 1;
        }
        while (n >= 0);
        return s;
    }
}

/// <summary>A hole found in a view: its tag, position from the view's origin (model mm) and diameter.</summary>
public sealed record TableHole(string Tag, double X, double Y, double Diameter, Point2 SheetCenter, double SheetRadius);

public static class DrawingTables
{
    public const double RowHeight = 6;

    /// <summary>Holes seen face-on in a view, tagged A1, A2, ... in reading order.</summary>
    public static IReadOnlyList<TableHole> Holes(IEnumerable<Body> bodies, ViewGeometry vg)
    {
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(vg);
        var pts = vg.Lines.SelectMany(l => l.Points).ToList();
        if (pts.Count == 0)
        {
            return [];
        }

        var origin = new Point2(pts.Min(p => p.X), pts.Min(p => p.Y));
        var found = new List<(Point2 Uv, double D)>();
        foreach (var body in bodies.Where(b => !b.IsSurface))
        {
            for (var i = 1; i <= body.EdgeCount; i++)
            {
                var e = body.Edge(i);
                if (e.Curve != CurveKind.Circle || !e.IsClosed)
                {
                    continue;
                }

                // Only round holes (concave cylinders) whose axis points at the viewer.
                var cylIndex = body.Shape.FacesOfEdge(i).FirstOrDefault(f => body.Face(f).Surface == SurfaceKind.Cylinder);
                if (cylIndex == 0)
                {
                    continue;
                }

                var cyl = body.Face(cylIndex);
                if (Math.Abs(Math.Abs(Vec3.Dot(cyl.AxisDirection.Normalized(), vg.Toward)) - 1) > 1e-6 || !IsHole(body, cylIndex, e.CenterOrDirection, e.Radius))
                {
                    continue;
                }

                var uv = vg.ToView(e.CenterOrDirection);
                if (!found.Any(f => Point2.Distance(f.Uv, uv) < 1e-6 && Math.Abs(f.D - (2 * e.Radius)) < 1e-6))
                {
                    found.Add((uv, 2 * e.Radius));
                }
            }
        }

        var ordered = found.OrderByDescending(f => Math.Round(f.Uv.Y, 3)).ThenBy(f => f.Uv.X).ToList();
        return [.. ordered.Select((f, k) => new TableHole($"A{k + 1}", f.Uv.X - origin.X, f.Uv.Y - origin.Y, f.D, vg.ToSheet(f.Uv), f.D / 2 * vg.Scale))];
    }

    /// <summary>A hole's material is outside its cylinder: the face's outward normal points toward the axis.</summary>
    private static bool IsHole(Body body, int face, Vec3 center, double radius)
    {
        var axis = body.Face(face).AxisDirection.Normalized();
        var probe = center + (axis.AnyPerpendicular().Normalized() * (radius * 2));
        if (body.Shape.ProjectOnFace(face, probe) is not { } hit)
        {
            return false;
        }

        var radial = hit.Point - center;
        radial -= axis * Vec3.Dot(radial, axis);
        return Vec3.Dot(hit.Normal, radial) < 0;
    }

    public static void DrawHoleTable(SheetGraphics g, HoleTable table, IReadOnlyList<TableHole> holes)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(holes);
        var inv = CultureInfo.InvariantCulture;
        foreach (var h in holes)
        {
            var at = h.SheetCenter + new Point2(h.SheetRadius + 1, h.SheetRadius + 1);
            g.Text(at, h.Tag, 2.5, owner: table.Id);
        }

        Grid(g, table.Id, table.X, table.Y, [14, 24, 24, 24], ["TAG", "X LOC", "Y LOC", "SIZE"],
            [.. holes.Select(h => (IReadOnlyList<string>)[h.Tag, h.X.ToString("0.##", inv), h.Y.ToString("0.##", inv), "Ø" + h.Diameter.ToString("0.##", inv)])]);
    }

    public static void DrawRevisionTable(SheetGraphics g, RevisionTable table)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(table);
        g.Text(new Point2(table.X, table.Y + 2), "REVISIONS", 3, owner: "revisions");
        Grid(g, "revisions", table.X, table.Y, [12, 70, 24, 24], ["REV", "DESCRIPTION", "DATE", "APPROVED"],
            [.. table.Rows.Select(r => (IReadOnlyList<string>)[r.Rev, r.Description, r.Date, r.Approved])]);
    }

    /// <summary>A general table, its columns sized to their longest text.</summary>
    public static void DrawGeneralTable(SheetGraphics g, GeneralTable table)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(table);
        if (table.Rows.Count == 0)
        {
            return;
        }

        var columns = table.Rows.Max(r => r.Count);
        var widths = Enumerable.Range(0, columns)
            .Select(c => Math.Max(12, (table.Rows.Max(r => c < r.Count ? r[c].Length : 0) * 2.0) + 4))
            .ToArray();
        Grid(g, table.Id, table.X, table.Y, widths, [.. table.Rows[0]], [.. table.Rows.Skip(1).Select(r => (IReadOnlyList<string>)r)]);
    }

    /// <summary>A ruled table with a header row; text columns after the first are left-aligned.</summary>
    private static void Grid(SheetGraphics g, string owner, double x0, double y0, double[] widths, string[] header, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var total = widths.Sum();
        var y = y0;
        foreach (var (cells, bold) in rows.Select(r => (r, false)).Prepend((header, true)))
        {
            g.Polyline(Pen.Thin, [new(x0, y), new(x0 + total, y), new(x0 + total, y - RowHeight), new(x0, y - RowHeight)], closed: true, owner: owner);
            var x = x0;
            for (var c = 0; c < widths.Length; c++)
            {
                if (c > 0)
                {
                    g.Line(Pen.Thin, new Point2(x, y), new Point2(x, y - RowHeight), owner);
                }

                var text = c < cells.Count ? cells[c] : string.Empty;
                g.Text(new Point2(x + (widths[c] / 2), y - RowHeight + 1.7), text, bold ? 2.8 : 2.5, TextAnchor.Center, owner: owner);
                x += widths[c];
            }

            y -= RowHeight;
        }
    }
}
