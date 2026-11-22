using System.Globalization;
using System.Text;
using Chamfer.Geometry;

namespace Chamfer.Drawing;

/// <summary>Writes sheet graphics as SVG, DXF (R12) or PDF. Units are millimetres.</summary>
public static class Exporters
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string F(double v) => v.ToString("0.###", Inv);

    /// <summary>Approximate text width for a proportional sans-serif font.</summary>
    public static double TextWidth(string text, double height) => text.Length * height * 0.55;

    // ------------------------------------------------------------------ SVG

    public static string Svg(SheetGraphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        var sb = new StringBuilder();
        sb.Append(Inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{F(g.Width)}mm\" height=\"{F(g.Height)}mm\" viewBox=\"0 0 {F(g.Width)} {F(g.Height)}\">\n");
        sb.Append(Inv, $"<rect width=\"{F(g.Width)}\" height=\"{F(g.Height)}\" fill=\"white\"/>\n");
        double Y(double y) => g.Height - y;
        string Color(Primitive p) => g.Layers.TryGetValue(p, out var layer) && g.LayerColors.TryGetValue(layer, out var c) && c != 0
            ? $"#{c:X6}"
            : "black";
        foreach (var item in g.Items)
        {
            switch (item)
            {
                case PolylinePrimitive p when p.Points.Count > 1:
                    var dash = SheetGraphics.Dashes(p.Pen);
                    var points = string.Join(' ', p.Points.Select(q => $"{F(q.X)},{F(Y(q.Y))}"));
                    sb.Append(Inv, $"<{(p.Closed ? "polygon" : "polyline")} points=\"{points}\" fill=\"none\" stroke=\"{Color(p)}\" stroke-width=\"{F(SheetGraphics.PenWidth(p.Pen))}\" stroke-linecap=\"round\" stroke-linejoin=\"round\"");
                    if (dash.Length > 0)
                    {
                        sb.Append(Inv, $" stroke-dasharray=\"{string.Join(',', dash.Select(F))}\"");
                    }

                    sb.Append("/>\n");
                    break;
                case ArrowPrimitive a:
                    var (l, r) = ArrowWings(a);
                    sb.Append(Inv, $"<polygon points=\"{F(a.Tip.X)},{F(Y(a.Tip.Y))} {F(l.X)},{F(Y(l.Y))} {F(r.X)},{F(Y(r.Y))}\" fill=\"{Color(a)}\"/>\n");
                    break;
                case TextPrimitive t:
                    var anchor = t.Anchor switch { TextAnchor.Center => "middle", TextAnchor.Right => "end", _ => "start" };
                    var esc = System.Security.SecurityElement.Escape(t.Text);
                    sb.Append(Inv, $"<text fill=\"{Color(t)}\" x=\"{F(t.At.X)}\" y=\"{F(Y(t.At.Y))}\" font-family=\"Arial, Helvetica, sans-serif\" font-size=\"{F(t.Height * 1.4)}\" text-anchor=\"{anchor}\"");
                    if (Math.Abs(t.Angle) > 1e-9)
                    {
                        sb.Append(Inv, $" transform=\"rotate({F(-t.Angle)} {F(t.At.X)} {F(Y(t.At.Y))})\"");
                    }

                    sb.Append(Inv, $">{esc}</text>\n");
                    break;
            }
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    /// <summary>A layer name DXF accepts: letters, digits, '_' and '-'.</summary>
    private static string DxfName(string name)
    {
        var clean = new string([.. name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_')]);
        return clean.Length == 0 ? "LAYER" : clean;
    }

    public static (Point2 Left, Point2 Right) ArrowWings(ArrowPrimitive a)
    {
        ArgumentNullException.ThrowIfNull(a);
        var back = a.Tip - (a.Direction * a.Length);
        var side = a.Direction.Perp() * (a.Length / 3.5);
        return (back + side, back - side);
    }

    // ------------------------------------------------------------------ DXF

    public static string Dxf(SheetGraphics g)
    {
        ArgumentNullException.ThrowIfNull(g);
        var sb = new StringBuilder();
        void Pair(int code, string value) => sb.Append(Inv, $"{code}\n{value}\n");
        Pair(0, "SECTION");
        Pair(2, "HEADER");
        Pair(9, "$INSUNITS");
        Pair(70, "4");
        Pair(0, "ENDSEC");
        Pair(0, "SECTION");
        Pair(2, "ENTITIES");
        string LayerOf(Primitive p, string fallback) => g.Layers.TryGetValue(p, out var l) ? DxfName(l) : fallback;
        void Color(Primitive p)
        {
            if (g.Layers.TryGetValue(p, out var l) && g.LayerColors.TryGetValue(l, out var c) && c != 0)
            {
                Pair(420, ((int)c).ToString(Inv)); // true colour
            }
        }

        void Line(string layer, Point2 a, Point2 b, Primitive owner)
        {
            Pair(0, "LINE");
            Pair(8, layer);
            Color(owner);
            Pair(10, F(a.X));
            Pair(20, F(a.Y));
            Pair(30, "0");
            Pair(11, F(b.X));
            Pair(21, F(b.Y));
            Pair(31, "0");
        }

        foreach (var item in g.Items)
        {
            switch (item)
            {
                case PolylinePrimitive p:
                    var layer = LayerOf(p, p.Pen.ToString().ToUpperInvariant());
                    for (var i = 0; i + 1 < p.Points.Count; i++)
                    {
                        Line(layer, p.Points[i], p.Points[i + 1], p);
                    }

                    if (p.Closed && p.Points.Count > 2)
                    {
                        Line(layer, p.Points[^1], p.Points[0], p);
                    }

                    break;
                case ArrowPrimitive a:
                    var (l, r) = ArrowWings(a);
                    Pair(0, "SOLID");
                    Pair(8, LayerOf(a, "THIN"));
                    Color(a);
                    foreach (var (code, p) in new[] { (10, a.Tip), (11, l), (12, r), (13, r) })
                    {
                        Pair(code, F(p.X));
                        Pair(code + 10, F(p.Y));
                        Pair(code + 20, "0");
                    }

                    break;
                case TextPrimitive t:
                    Pair(0, "TEXT");
                    Pair(8, LayerOf(t, "TEXT"));
                    Color(t);
                    Pair(10, F(t.At.X));
                    Pair(20, F(t.At.Y));
                    Pair(30, "0");
                    Pair(40, F(t.Height));
                    Pair(1, t.Text.Replace("⌀", "%%c", StringComparison.Ordinal).Replace("°", "%%d", StringComparison.Ordinal).Replace("±", "%%p", StringComparison.Ordinal));
                    Pair(50, F(t.Angle));
                    if (t.Anchor != TextAnchor.Left)
                    {
                        Pair(72, t.Anchor == TextAnchor.Center ? "1" : "2");
                        Pair(11, F(t.At.X));
                        Pair(21, F(t.At.Y));
                        Pair(31, "0");
                    }

                    break;
            }
        }

        Pair(0, "ENDSEC");
        Pair(0, "EOF");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ PDF

    /// <summary>A single-page vector PDF of the sheet (Helvetica text, WinAnsi encoding).</summary>
    public static byte[] Pdf(SheetGraphics g) => Pdf([g]);

    /// <summary>A multi-page vector PDF, one page per sheet.</summary>
    public static byte[] Pdf(IReadOnlyList<SheetGraphics> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        const double pt = 72 / 25.4;
        var objects = new List<string>();
        var streams = new List<byte[]?>();
        int AddObject(string body, byte[]? stream = null)
        {
            objects.Add(body);
            streams.Add(stream);
            return objects.Count;
        }

        var font = AddObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var pagesId = objects.Count + 1 + (2 * sheets.Count) + 1;
        var pageIds = new List<int>();
        foreach (var g in sheets)
        {
            var content = PdfContent(g, pt);
            var contentId = AddObject($"<< /Length {content.Length} >>", content);
            pageIds.Add(AddObject($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {F(g.Width * pt)} {F(g.Height * pt)}] /Contents {contentId} 0 R /Resources << /Font << /F1 {font} 0 R >> >> >>"));
        }

        var catalog = AddObject($"<< /Type /Catalog /Pages {pagesId} 0 R >>");
        var pages = AddObject($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(i => $"{i} 0 R"))}] /Count {pageIds.Count} >>");
        if (pages != pagesId)
        {
            throw new InvalidOperationException("PDF object numbering mismatch.");
        }

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new long[objects.Count];
        for (var i = 0; i < objects.Count; i++)
        {
            offsets[i] = ms.Position;
            W($"{i + 1} 0 obj\n{objects[i]}\n");
            if (streams[i] is { } data)
            {
                W("stream\n");
                ms.Write(data);
                W("\nendstream\n");
            }

            W("endobj\n");
        }

        var xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets)
        {
            W($"{o:0000000000} 00000 n \n");
        }

        W($"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] PdfContent(SheetGraphics g, double pt)
    {
        var sb = new StringBuilder("1 J 1 j 0 G 0 g\n");
        string P(Point2 p) => $"{F(p.X * pt)} {F(p.Y * pt)}";
        foreach (var item in g.Items)
        {
            switch (item)
            {
                case PolylinePrimitive p when p.Points.Count > 1:
                    var dash = SheetGraphics.Dashes(p.Pen);
                    sb.Append(Inv, $"{F(SheetGraphics.PenWidth(p.Pen) * pt)} w [{string.Join(' ', dash.Select(d => F(d * pt)))}] 0 d\n");
                    sb.Append(Inv, $"{P(p.Points[0])} m\n");
                    foreach (var q in p.Points.Skip(1))
                    {
                        sb.Append(Inv, $"{P(q)} l\n");
                    }

                    sb.Append(p.Closed ? "s\n" : "S\n");
                    break;
                case ArrowPrimitive a:
                    var (l, r) = ArrowWings(a);
                    sb.Append(Inv, $"{P(a.Tip)} m {P(l)} l {P(r)} l f\n");
                    break;
                case TextPrimitive t:
                    var size = t.Height * 1.4 * pt;
                    var width = TextWidth(t.Text, t.Height) * pt;
                    var shift = t.Anchor switch { TextAnchor.Center => -width / 2, TextAnchor.Right => -width, _ => 0 };
                    var rad = t.Angle * Math.PI / 180;
                    var (c, s) = (Math.Cos(rad), Math.Sin(rad));
                    var x = (t.At.X * pt) + (shift * c);
                    var y = (t.At.Y * pt) + (shift * s);
                    var text = t.Text.Replace("⌀", "Ø", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
                    sb.Append(Inv, $"BT /F1 {F(size)} Tf {F(c)} {F(s)} {F(-s)} {F(c)} {F(x)} {F(y)} Tm ({text}) Tj ET\n");
                    break;
            }
        }

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
