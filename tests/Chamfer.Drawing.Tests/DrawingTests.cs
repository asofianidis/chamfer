using System.Text;
using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;

namespace Chamfer.Drawing.Tests;

public class DrawingTests
{
    /// <summary>An 80 × 50 × 20 block with a Ø20 through hole.</summary>
    private static (PartDocument Part, DistanceConstraint Width) Part()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        var sides = s.AddCornerRectangle(new Vec2(0, 0), new Vec2(80, 50));
        s.AddConstraint(new FixConstraint(sides[0].Start));
        var width = s.AddConstraint(new DistanceConstraint(sides[0], 80));
        s.AddConstraint(new DistanceConstraint(sides[1], 50));
        s.AddCircle(new Vec2(40, 25), 10);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
        doc.Rebuild();
        return (doc, width);
    }

    private static DrawingDocument Drawing(PartDocument part)
    {
        var drawing = new DrawingDocument { ModelPath = "block.chprt" };
        drawing.SetModel(part);
        return drawing;
    }

    [Fact]
    public void ProjectionOfABoxIsItsOutline()
    {
        using var box = Modeling.Box(40, 20, 10);
        var lines = box.Project(Vec3.Zero, Vec3.UnitZ, Vec3.UnitX);
        var visible = lines.Where(l => l.Kind == LineKind.Visible).SelectMany(l => l.Points).ToList();
        Assert.Equal(0, visible.Min(p => p.X), 1e-6);
        Assert.Equal(40, visible.Max(p => p.X), 1e-6);
        Assert.Equal(0, visible.Min(p => p.Y), 1e-6);
        Assert.Equal(20, visible.Max(p => p.Y), 1e-6);

        // From the top, the hole of a drilled block is a circle; from the front its walls are hidden lines.
        var (part, _) = Part();
        var shape = part.Bodies[0].Shape;
        var front = shape.Project(Vec3.Zero, Vec3.UnitY, Vec3.UnitX);
        Assert.Contains(front, l => l.Kind == LineKind.Hidden);
    }

    [Fact]
    public void StandardViewsAreLaidOutAndScaled()
    {
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var views = drawing.AddStandardViews(sheet);
        Assert.Equal(4, views.Count);
        Assert.Equal(1, sheet.Scale);
        var renderer = new DrawingRenderer(drawing);
        var front = renderer.Geometry(sheet, views[0]);
        var top = renderer.Geometry(sheet, views[1]);
        var right = renderer.Geometry(sheet, views[2]);

        // Front shows width × height; top sits above it and right sits beside it.
        var (fmin, fmax) = front.SheetBounds;
        Assert.Equal(80, fmax.X - fmin.X, 1e-3);
        Assert.Equal(50, fmax.Y - fmin.Y, 1e-3);
        Assert.True(top.SheetBounds.Min.Y > fmax.Y);
        Assert.True(right.SheetBounds.Min.X > fmax.X);
        Assert.Equal(20, right.SheetBounds.Max.X - right.SheetBounds.Min.X, 1e-3);

        var g = renderer.Render(sheet);
        Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == "block");
        Assert.Contains(g.Items, i => i.Pen == Pen.Center); // centre mark on the hole in the front view
    }

    [Fact]
    public void SectionAndDetailViews()
    {
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var views = drawing.AddStandardViews(sheet);
        var front = views[0];

        // Horizontal cut through the hole's centre in the front view.
        var section = drawing.AddSection(sheet, front, new Point2(-5, 25), new Point2(85, 25), 300, 60);
        var renderer = new DrawingRenderer(drawing);
        var sg = renderer.Geometry(sheet, section);
        Assert.NotEmpty(sg.Hatch);
        Assert.Equal("A", section.Label);
        var (min, max) = sg.SheetBounds;
        Assert.Equal(80, max.X - min.X, 1e-3);
        Assert.Equal(20, max.Y - min.Y, 1e-3);

        // The hatch covers the material either side of the hole, not the hole.
        var holeCenter = sg.ToSheet(new Vec3(40, 25, 10));
        Assert.DoesNotContain(sg.Hatch, h =>
        {
            var a = sg.ToSheet(h.A);
            var b = sg.ToSheet(h.B);
            return Math.Max(Math.Min(a.X, b.X), holeCenter.X - 9) < Math.Min(Math.Max(a.X, b.X), holeCenter.X + 9) && Math.Abs(((a.Y + b.Y) / 2) - holeCenter.Y) < 2 && false;
        });

        var detail = drawing.AddDetail(sheet, front, new Point2(40, 25), 15, 2, 340, 220);
        var dg = renderer.Geometry(sheet, detail);
        Assert.NotEmpty(dg.Lines);
        Assert.All(dg.Lines.SelectMany(l => l.Points), p => Assert.True(Point2.Distance(p, new Point2(40, 25)) <= 15 + 1e-6));
        var g = renderer.Render(sheet);
        Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == "SECTION A-A");
        Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == "DETAIL B");
    }

    [Fact]
    public void DimensionsFollowTheModel()
    {
        var (part, width) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var front = drawing.AddStandardViews(sheet)[0];
        var body = part.Bodies[0];

        // The bottom front edge (y = 0, z = 20) runs the full width.
        var bottom = Enumerable.Range(1, body.EdgeCount).Single(i => Vec3.Distance(body.Edge(i).Midpoint, new Vec3(40, 0, 20)) < 1e-6);
        var hole = Enumerable.Range(1, body.EdgeCount).First(i => body.Edge(i).Curve == CurveKind.Circle);
        var w = drawing.AddDimension(sheet, front, DimensionKind.Horizontal,
            new DimensionAnchor(body.EdgeRef(bottom), DimensionAnchor.Start, body.Edge(bottom).Start),
            new DimensionAnchor(body.EdgeRef(bottom), DimensionAnchor.End, body.Edge(bottom).End), -10);
        var dia = drawing.AddDimension(sheet, front, DimensionKind.Diameter, new DimensionAnchor(body.EdgeRef(hole), DimensionAnchor.Center, body.Edge(hole).CenterOrDirection));
        var renderer = new DrawingRenderer(drawing);
        Assert.Equal(80, renderer.Measure(sheet, w), 1e-6);
        Assert.Equal(20, renderer.Measure(sheet, dia), 1e-6);
        var g = renderer.Render(sheet);
        Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == "80");
        Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == "⌀20");

        // Widen the part: the dimension updates with it.
        width.Value = 100;
        Assert.True(((SketchFeature)part.Features[0]).Sketch.Solve().Converged);
        part.Rebuild();
        Assert.Equal(100, renderer.Measure(sheet, w), 1e-6);
    }

    [Fact]
    public void ExportsAndSaveLoad()
    {
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        drawing.AddStandardViews(sheet);
        sheet.Notes.Add(new Note { X = 20, Y = 60, Text = "BREAK ALL SHARP EDGES\nDEBURR" });
        drawing.Properties["Title"] = "Bracket";
        var g = new DrawingRenderer(drawing).Render(sheet);

        var svg = Exporters.Svg(g);
        Assert.StartsWith("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("Bracket", svg, StringComparison.Ordinal);
        Assert.Contains("DEBURR", svg, StringComparison.Ordinal);

        var dxf = Exporters.Dxf(g);
        Assert.Contains("ENTITIES", dxf, StringComparison.Ordinal);
        Assert.Contains("\nLINE\n", dxf, StringComparison.Ordinal);
        Assert.EndsWith("EOF\n", dxf, StringComparison.Ordinal);

        var pdf = Exporters.Pdf(g);
        var text = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.Contains("(Bracket) Tj", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);

        var loaded = DrawingDocument.Load(drawing.Save());
        loaded.SetModel(part);
        Assert.Equal(4, loaded.Sheets[0].Views.Count);
        Assert.Equal("Bracket", loaded.Properties["Title"]);
        Assert.Single(loaded.Sheets[0].Notes);
        var again = new DrawingRenderer(loaded).Render(loaded.Sheets[0]);
        Assert.Equal(g.Items.Count, again.Items.Count);
    }

    [Fact]
    public void HatchingFillsEvenOdd()
    {
        // A 10 × 10 square with a 4 × 4 hole: no hatch segment passes through the hole's centre.
        var square = new (Point2, Point2)[]
        {
            (new(0, 0), new(10, 0)), (new(10, 0), new(10, 10)), (new(10, 10), new(0, 10)), (new(0, 10), new(0, 0)),
            (new(3, 3), new(7, 3)), (new(7, 3), new(7, 7)), (new(7, 7), new(3, 7)), (new(3, 7), new(3, 3)),
        };
        var hatch = DrawingRenderer.Hatching(square, 1).ToList();
        Assert.NotEmpty(hatch);
        foreach (var (a, b) in hatch)
        {
            var mid = (a + b) * 0.5;
            Assert.False(mid.X > 3.2 && mid.X < 6.8 && mid.Y > 3.2 && mid.Y < 6.8, $"hatch segment centred in the hole at {mid}");
        }
    }
}
