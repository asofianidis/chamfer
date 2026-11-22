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
    public void ProjectedAuxiliaryCropAndBreakViews()
    {
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var front = drawing.Add(sheet, ViewKind.Front, 100, 100);
        var renderer = new DrawingRenderer(drawing);
        static (double W, double H) Size(ViewGeometry g) => (g.SheetBounds.Max.X - g.SheetBounds.Min.X, g.SheetBounds.Max.Y - g.SheetBounds.Min.Y);

        // Projected to the right: depth by height, like the standard right view.
        var right = drawing.AddProjected(sheet, front, ViewSide.Right, 200, 100);
        Assert.Equal((20, 50), Size(renderer.Geometry(sheet, right)), new SizeComparer());
        var above = drawing.AddProjected(sheet, front, ViewSide.Above, 100, 200);
        Assert.Equal((80, 20), Size(renderer.Geometry(sheet, above)), new SizeComparer());

        // Auxiliary, square to the front view's bottom edge: looking up from below.
        var aux = drawing.AddAuxiliary(sheet, front, new Point2(0, 0), new Point2(80, 0), 100, 20);
        var ag = renderer.Geometry(sheet, aux);
        Assert.Equal((80, 20), Size(ag), new SizeComparer());
        Assert.Equal(-1, Vec3.Dot(ag.Toward, Vec3.UnitY), 1e-9);

        // Cropped to the lower-left quarter.
        var cropped = drawing.Add(sheet, ViewKind.Front, 300, 100);
        cropped.Crop = [0, 0, 40, 25];
        Assert.Equal((40, 25), Size(renderer.Geometry(sheet, cropped)), new SizeComparer());

        // Broken: x from 20 to 60 left out and closed up to a 4 mm gap; break lines reach 2 mm past the part.
        var broken = drawing.Add(sheet, ViewKind.Front, 300, 250);
        broken.Break = [20, 60];
        var bg = renderer.Geometry(sheet, broken);
        Assert.Equal((44, 54), Size(bg), new SizeComparer());
        Assert.False(bg.IsShown(new Vec3(40, 25, 0)));
        Assert.Equal(44, bg.ToSheet(new Vec3(80, 0, 0)).X - bg.ToSheet(new Vec3(0, 0, 0)).X, 1e-6);

        var g = renderer.Render(sheet);
        Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == "VIEW A");
        Assert.Equal(ViewSide.Right, DrawingDocument.Load(drawing.Save()).Sheets[0].Views.Single(v => v.Kind == ViewKind.Projected && v.Side == ViewSide.Right).Side);
    }

    private sealed class SizeComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 1e-3 && Math.Abs(a.Item2 - b.Item2) < 1e-3;

        public int GetHashCode((double, double) obj) => 0;
    }

    [Fact]
    public void BaselineChainAndOrdinateDimensions()
    {
        // A plate 80 x 50 with the hole at (40, 25) and another at (65, 25).
        var (part, _) = Part();
        var holes = new Sketch();
        holes.AddCircle(new Vec2(65, 25), 4);
        var hs = part.Add(new SketchFeature { Sketch = holes });
        part.Add(new ExtrudeFeature { SketchId = hs.Id, IsCut = true, EndCondition = EndCondition.ThroughAll, Reverse = true });
        part.Rebuild();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var front = drawing.Add(sheet, ViewKind.Front, 150, 150);
        var renderer = new DrawingRenderer(drawing);
        var vg = renderer.Geometry(sheet, front);

        double[] Values() => [.. sheet.Dimensions.Select(x => Math.Round(renderer.Measure(sheet, x), 6))];

        // Baseline: 40, 65 and 80 from the left edge, stacked further out each time.
        var baseline = DimensionSets.Add(drawing, sheet, front, vg, DimensionSetKind.Baseline, vertical: false);
        Assert.Equal([40, 65, 80], Values());
        Assert.True(baseline[2].Offset < baseline[0].Offset);

        // Chain: 40, then 25, then 15, in a row.
        sheet.Dimensions.Clear();
        DimensionSets.Add(drawing, sheet, front, vg, DimensionSetKind.Chain, vertical: false);
        Assert.Equal([40, 25, 15], Values());

        // Ordinate: zero at the datum, then each distance from it; drawn as text at leader ends.
        sheet.Dimensions.Clear();
        DimensionSets.Add(drawing, sheet, front, vg, DimensionSetKind.Ordinate, vertical: false);
        Assert.Equal([0, 40, 65, 80], Values());
        var g = renderer.Render(sheet);
        foreach (var text in new[] { "0", "40", "65", "80" })
        {
            Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == text);
        }

        // Vertically: the holes share a height, so it's one stop at 25 and the top at 50.
        sheet.Dimensions.Clear();
        DimensionSets.Add(drawing, sheet, front, vg, DimensionSetKind.Baseline, vertical: true);
        Assert.Equal([25, 50], Values());
    }

    [Fact]
    public void IsoFitsGiveStandardLimits()
    {
        (double, double)? Mm(double upper, double lower) => (upper / 1000, lower / 1000);
        void Check(string zone, double size, double upper, double lower)
        {
            var d = Fits.Deviations(size, zone);
            Assert.NotNull(d);
            Assert.Equal(Mm(upper, lower)!.Value.Item1, d.Value.Upper, 1e-9);
            Assert.Equal(Mm(upper, lower)!.Value.Item2, d.Value.Lower, 1e-9);
        }

        // ISO 286-2 values (micrometres) for a 10 mm and a 50 mm size.
        Check("H7", 10, 15, 0);
        Check("g6", 10, -5, -14);
        Check("k6", 10, 10, 1);
        Check("p6", 10, 24, 15);
        Check("N7", 10, -4, -19);
        Check("K7", 10, 5, -10);
        Check("P7", 10, -9, -24);
        Check("js6", 10, 4.5, -4.5);
        Check("H8", 50, 39, 0);
        Check("f7", 50, -25, -50);
        Check("F8", 50, 64, 25);
        Assert.Null(Fits.Deviations(10, "Q7"));
        Assert.Null(Fits.Deviations(600, "H7"));
        Assert.Equal("+0.015/0  −0.005/−0.014", Fits.LimitsText(10, "H7/g6"));

        // On a drawing: the diameter of the 20 mm hole reads "⌀20 H7 (+0.021/0)".
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var front = drawing.Add(sheet, ViewKind.Front, 150, 150);
        var body = part.Bodies[0];
        var hole = Enumerable.Range(1, body.EdgeCount).First(i => body.Edge(i).Curve == CurveKind.Circle);
        var dim = drawing.AddDimension(sheet, front, DimensionKind.Diameter, new DimensionAnchor(body.EdgeRef(hole), DimensionAnchor.Center, body.Edge(hole).CenterOrDirection));
        dim.Fit = "H7";
        dim.ShowLimits = true;
        var g = new DrawingRenderer(drawing).Render(sheet);
        Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == "⌀20 H7 (+0.021/0)");
    }

    [Fact]
    public void CenterlinesAndCosmeticThreads()
    {
        // A plate with a tapped M8 hole from the top face.
        var (part, _) = Part();
        var body = part.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        var (u, v) = body.Shape.FacePlane(top)!.Value.ToLocal(new Vec3(15, 15, 20));
        var pts = new Sketch();
        pts.AddPoint(new Vec2(u, v));
        var ps = part.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = pts });
        part.Add(new HoleFeature { SketchId = ps.Id, Type = HoleType.Tapped, Size = "M8", Diameter = 6.8, ThreadDiameter = 8, Depth = 12 });
        part.Rebuild();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var front = drawing.Add(sheet, ViewKind.Front, 120, 150);
        var below = drawing.AddProjected(sheet, front, ViewSide.Below, 120, 60);
        var renderer = new DrawingRenderer(drawing);
        var g = renderer.Render(sheet);
        List<PolylinePrimitive> Owned(DrawingView view, Pen pen) =>
            [.. g.Items.OfType<PolylinePrimitive>().Where(p => p.Pen == pen && g.Owners.TryGetValue(p, out var o) && o == view.Id)];

        // End-on (front, looking down the holes): a thin three-quarter circle of the M8 thread.
        var arc = Assert.Single(Owned(front, Pen.Thin), p => p.Points.Count > 20);
        var fg = renderer.Geometry(sheet, front);
        var center = fg.ToSheet(new Vec3(15, 15, 0));
        Assert.All(arc.Points, p => Assert.Equal(4 * fg.Scale, Point2.Distance(p, center), 1e-6));

        // Side-on (below): centrelines along both holes, and the thread's two thin lines.
        Assert.Equal(2, Owned(below, Pen.Center).Count);
        Assert.Equal(2, Owned(below, Pen.Thin).Count(p => p.Points.Count == 2));
    }

    [Fact]
    public void WeldSymbolsAndDatumTargets()
    {
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        sheet.Welds.Add(new WeldSymbol { Id = "w1", Kind = WeldKind.Fillet, Size = "6", Length = "50", AnchorX = 100, AnchorY = 100, X = 120, Y = 115, AllAround = true, FieldWeld = true });
        sheet.Welds.Add(new WeldSymbol { Id = "w2", Kind = WeldKind.VGroove, Size = "8", BothSides = true, AnchorX = 200, AnchorY = 100, X = 180, Y = 115 });
        sheet.DatumTargets.Add(new DatumTarget { Id = "t1", Label = "A1", Area = "⌀6", AnchorX = 150, AnchorY = 60, X = 165, Y = 75 });
        var g = new DrawingRenderer(drawing).Render(sheet);
        List<Primitive> Of(string id) => [.. g.Items.Where(i => g.Owners.TryGetValue(i, out var o) && o == id)];

        Assert.Contains(Of("w1").OfType<TextPrimitive>(), t => t.Text == "6");
        Assert.Contains(Of("w1").OfType<TextPrimitive>(), t => t.Text == "50");
        Assert.Equal(2, Of("w2").OfType<TextPrimitive>().Count(t => t.Text == "8")); // both sides
        Assert.Contains(Of("t1").OfType<TextPrimitive>(), t => t.Text == "A1");
        Assert.Contains(Of("t1").OfType<TextPrimitive>(), t => t.Text == "⌀6");

        var reloaded = DrawingDocument.Load(drawing.Save());
        Assert.Equal(2, reloaded.Sheets[0].Welds.Count);
        Assert.True(reloaded.Sheets[0].Welds[0].FieldWeld);
        Assert.Equal("A1", reloaded.Sheets[0].DatumTargets.Single().Label);
    }

    [Fact]
    public void ProjectedViewsFollowTheProjectionAngle()
    {
        // An L-shaped part: its left and right views differ.
        var part = new PartDocument();
        var l = new Sketch();
        l.AddPolyline([new Vec2(0, 0), new Vec2(40, 0), new Vec2(40, 10), new Vec2(10, 10), new Vec2(10, 30), new Vec2(0, 30)], closed: true);
        var sf = part.Add(new SketchFeature { Sketch = l });
        part.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
        part.Rebuild();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var front = drawing.Add(sheet, ViewKind.Front, 100, 100);
        var placedRight = drawing.AddProjected(sheet, front, ViewSide.Right, 200, 100);

        drawing.ApplyStandard(DraftingStandard.Jis); // third angle: the right view looks from the right
        Assert.False(drawing.FirstAngle);
        var third = new DrawingRenderer(drawing).Geometry(sheet, placedRight);
        Assert.Equal(1, Vec3.Dot(third.Toward, Vec3.UnitX), 1e-9);

        drawing.ApplyStandard(DraftingStandard.Din); // first angle: the view on the right looks from the left
        Assert.True(drawing.FirstAngle);
        var first = new DrawingRenderer(drawing).Geometry(sheet, placedRight);
        Assert.Equal(-1, Vec3.Dot(first.Toward, Vec3.UnitX), 1e-9);
        Assert.Contains(new DrawingRenderer(drawing).Render(sheet).Items.OfType<TextPrimitive>(), t => t.Text == "FIRST ANGLE PROJECTION");
    }

    [Fact]
    public void CosmeticThreadsOnAShaft()
    {
        // A 10 mm shaft, 30 long along Z, threaded M10 for its first 15 mm.
        var part = new PartDocument();
        var s = new Sketch();
        s.AddCircle(Vec2.Zero, 5);
        var sf = part.Add(new SketchFeature { Sketch = s });
        part.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 30 });
        part.Rebuild();
        var body = part.Bodies[0];
        var side = body.FaceRef(Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Surface == SurfaceKind.Cylinder));
        var thread = part.Add(new CosmeticThreadFeature { Face = side, Designation = "M10x1.5", Diameter = 8.376, Length = 15 });
        part.Rebuild();
        Assert.True(part.Results[thread.Id].Status == FeatureStatus.Ok, part.Results[thread.Id].Message);
        var t = thread.Resolve(part.Bodies);
        Assert.False(t.Internal);
        Assert.Equal(15, t.To - t.From, 1e-6);

        // Side-on, two thin lines at the root diameter, 15 long.
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var top = drawing.Add(sheet, ViewKind.Top, 100, 100);
        var renderer = new DrawingRenderer(drawing);
        var g = renderer.Render(sheet);
        var lines = g.Items.OfType<PolylinePrimitive>().Where(p => p.Pen == Pen.Thin && p.Points.Count == 2 && g.Owners.TryGetValue(p, out var o) && o == top.Id).ToList();
        Assert.Equal(2, lines.Count);
        var scale = renderer.Geometry(sheet, top).Scale;
        Assert.All(lines, l => Assert.Equal(15 * scale, Point2.Distance(l.Points[0], l.Points[1]), 1e-6));
        Assert.Equal(8.376 * scale, Math.Abs(lines[0].Points[0].X - lines[1].Points[0].X), 1e-6);

        // A thread root bigger than the shaft is refused.
        thread.Diameter = 12;
        part.Rebuild();
        Assert.Equal(FeatureStatus.Error, part.Results[thread.Id].Status);
    }

    [Fact]
    public void GeneralTablesHoldAnyText()
    {
        var rows = GeneralTable.Parse("PART | QTY | FINISH\nBracket | 2 | Anodised\n\nPlate | 1");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Plate", "1"], rows[2]);
        var table = new GeneralTable { Id = "t1", X = 20, Y = 200, Rows = rows };
        Assert.Equal(rows, GeneralTable.Parse(table.Format()));

        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        sheet.Tables.Add(table);
        var g = new DrawingRenderer(drawing).Render(sheet);
        foreach (var text in new[] { "PART", "QTY", "FINISH", "Bracket", "Anodised", "Plate" })
        {
            Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == text && g.Owners.TryGetValue(t, out var o) && o == "t1");
        }

        Assert.Equal(rows, DrawingDocument.Load(drawing.Save()).Sheets[0].Tables.Single().Rows);
    }

    [Fact]
    public void BrokenOutSectionShowsTheInside()
    {
        // The plate (80 x 50 x 20, a 20 mm hole through it, y 15 to 35) seen along Y from y = 50; break out
        // a window 20 deep over the hole, down past its near side.
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var top = drawing.Add(sheet, ViewKind.Top, 100, 100);
        var renderer = new DrawingRenderer(drawing);
        var plain = renderer.Geometry(sheet, top);
        Assert.Empty(plain.Hatch);
        Assert.DoesNotContain(plain.Lines, l => l.Kind == LineKind.Visible && l.Points.All(p => p.X > 25 && p.X < 55));

        // View coordinates of the top view: x is model X, y is -Z (the view looks down).
        top.BrokenOut = [20, -25, 60, -25, 60, 5, 20, 5];
        top.BrokenOutDepth = 20;
        var broken = renderer.Geometry(sheet, top);
        Assert.NotEmpty(broken.Hatch); // the floor of the cut
        Assert.Contains(broken.Lines, l => l.Kind == LineKind.Visible && l.Points.All(p => p.X > 25 && p.X < 55)); // the hole's walls now in sight
        Assert.Equal(20, DrawingDocument.Load(drawing.Save()).Sheets[0].Views.Single().BrokenOutDepth);
    }

    [Fact]
    public void DimensionDecimalPlaces()
    {
        Assert.Equal("25", Units.Format(25, LengthUnit.Millimeter));
        Assert.Equal("25.00", Units.Format(25, LengthUnit.Millimeter, decimals: 2));
        Assert.Equal("1.000", Units.Format(25.4, LengthUnit.Inch, decimals: 3));

        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        var front = drawing.Add(sheet, ViewKind.Front, 150, 150);
        var body = part.Bodies[0];
        var hole = Enumerable.Range(1, body.EdgeCount).First(i => body.Edge(i).Curve == CurveKind.Circle);
        drawing.AddDimension(sheet, front, DimensionKind.Diameter, new DimensionAnchor(body.EdgeRef(hole), DimensionAnchor.Center, body.Edge(hole).CenterOrDirection));
        drawing.Decimals = 2;
        var g = new DrawingRenderer(drawing).Render(sheet);
        Assert.Contains(g.Items.OfType<TextPrimitive>(), t => t.Text == "⌀20.00");
        Assert.Equal(2, DrawingDocument.Load(drawing.Save()).Decimals);
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

    [Fact]
    public void FlatPatternDxfHasOutlineAndBendLine()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 50));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new BaseFlangeFeature { SketchId = sf.Id, Thickness = 2, BendRadius = 1, KFactor = 0.5 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var edge = Enumerable.Range(1, body.EdgeCount).Single(i => Vec3.Distance(body.Edge(i).Midpoint, new Vec3(50, 50, 2)) < 1e-6);
        doc.Add(new EdgeFlangeFeature { Edges = [body.EdgeRef(edge)], Length = 20 });
        doc.Rebuild();

        var g = FlatPatternExport.Graphics(doc);
        Assert.Equal(100 + 20, g.Width, 1e-6);
        Assert.Equal(50 + Math.PI + 20 + 20, g.Height, 1e-6);
        Assert.Single(g.Items.OfType<PolylinePrimitive>(), p => p.Pen == Pen.Center);
        var dxf = Exporters.Dxf(g);
        Assert.Contains("CENTER", dxf, StringComparison.Ordinal);
    }

    [Fact]
    public void AssemblyDrawingWithBomAndBalloons()
    {
        var asm = new Chamfer.Assembly.AssemblyDocument();
        var (plate, _) = Part();
        asm.RegisterPart("plate.chprt", plate);
        asm.AddComponent("plate.chprt");
        asm.AddComponent("toolbox:ISO4762 M10x30", Transform3.Translation(new Vec3(40, 25, 20)));
        asm.AddComponent("toolbox:ISO7089 M10", Transform3.Translation(new Vec3(40, 25, 20)));
        asm.AddComponent("toolbox:ISO7089 M10", Transform3.Translation(new Vec3(40, 25, -2)));

        var drawing = new DrawingDocument { ModelPath = "bracket.chasm" };
        drawing.SetAssembly(asm);
        Assert.True(drawing.IsAssembly);
        var sheet = drawing.Sheets[0];
        var views = drawing.AddStandardViews(sheet);
        var renderer = new DrawingRenderer(drawing);
        sheet.Bom = new BomTable { X = 30, Y = 280 };
        var balloons = drawing.AutoBalloon(sheet, views[^1], renderer);
        Assert.Equal(3, balloons.Count); // plate, screw, washer (two washers share an item)

        var numbers = renderer.ItemNumbers();
        Assert.Equal(numbers[asm.Components[2].Id], numbers[asm.Components[3].Id]);
        var g = renderer.Render(sheet);
        var texts = g.Items.OfType<TextPrimitive>().Select(t => t.Text).ToList();
        Assert.Contains("PART NUMBER", texts);
        Assert.Contains("ISO 4762 M10x30", texts);
        Assert.Contains("ISO 7089 M10", texts);
        Assert.Contains("2", texts); // washer quantity
        Assert.Contains("MATERIAL: See BOM", texts);
    }

    [Fact]
    public void GeometricTolerancesDatumsAndFinishes()
    {
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        drawing.AddStandardViews(sheet);
        var t = new GeometricTolerance
        {
            Id = drawing.NextId("gdt"), Characteristic = GeometricCharacteristic.Position, Tolerance = "0.05", Diameter = true,
            Condition = MaterialCondition.Maximum, Datums = ["A", "B"], X = 100, Y = 200, LeaderX = 90, LeaderY = 180,
        };
        sheet.Tolerances.Add(t);
        sheet.Datums.Add(new DatumFeature { Id = drawing.NextId("datum"), Label = "A", X = 60, Y = 150, AnchorX = 60, AnchorY = 165 });
        sheet.Finishes.Add(new SurfaceFinish { Id = drawing.NextId("sf"), Roughness = "Ra 1.6", X = 150, Y = 180 });

        // Frame: symbol cell, tolerance cell, one cell per datum.
        var (cells, width) = AnnotationGraphics.Layout(t);
        Assert.Equal(5, cells.Length);
        Assert.True(width > 4 * AnnotationGraphics.FrameHeight);
        Assert.Equal("Position | Ø0.05 (M) | A | B", AnnotationGraphics.Describe(t));

        var g = new DrawingRenderer(drawing).Render(sheet);
        foreach (var id in new[] { t.Id, sheet.Datums[0].Id, sheet.Finishes[0].Id })
        {
            Assert.Contains(g.Owners.Values, o => o == id);
        }

        Assert.Contains(g.Items.OfType<TextPrimitive>(), x => x.Text == "0.05");
        Assert.Contains(g.Items.OfType<TextPrimitive>(), x => x.Text == "Ra 1.6");
        Assert.Contains("0.05", Exporters.Svg(g), StringComparison.Ordinal);

        // Every characteristic draws something.
        foreach (var kind in Enum.GetValues<GeometricCharacteristic>())
        {
            var sg = new SheetGraphics(100, 100);
            AnnotationGraphics.Symbol(sg, kind, new Point2(50, 50), 4);
            Assert.NotEmpty(sg.Items);
        }

        var loaded = DrawingDocument.Load(drawing.Save());
        var lt = Assert.Single(loaded.Sheets[0].Tolerances);
        Assert.Equal(["A", "B"], lt.Datums);
        Assert.Equal(MaterialCondition.Maximum, lt.Condition);
        Assert.Equal("A", Assert.Single(loaded.Sheets[0].Datums).Label);
        Assert.Equal("Ra 1.6", Assert.Single(loaded.Sheets[0].Finishes).Roughness);
    }

    [Fact]
    public void HoleTableAndRevisionTable()
    {
        // A 100 × 60 × 10 plate with Ø6 holes at (20, 15) and (50, 15), Ø10 at (80, 40), and a Ø8 boss on top.
        var part = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 60));
        s.AddCircle(new Vec2(20, 15), 3);
        s.AddCircle(new Vec2(50, 15), 3);
        s.AddCircle(new Vec2(80, 40), 5);
        var sf = part.Add(new SketchFeature { Sketch = s });
        part.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        part.Rebuild();
        var body = part.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        var (u, v) = body.Shape.FacePlane(top)!.Value.ToLocal(new Vec3(30, 45, 10));
        var boss = new Sketch();
        boss.AddCircle(new Vec2(u, v), 4);
        var bf = part.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = boss });
        part.Add(new ExtrudeFeature { SketchId = bf.Id, Depth = 5 });
        part.Rebuild();

        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        drawing.AddStandardViews(sheet);
        var front = sheet.Views[0];
        var renderer = new DrawingRenderer(drawing);
        var holes = DrawingTables.Holes(drawing.Bodies, renderer.Geometry(sheet, front));
        Assert.Equal(3, holes.Count);
        Assert.Equal(["A1", "A2", "A3"], holes.Select(h => h.Tag));
        Assert.Equal((80, 40, 10), (Math.Round(holes[0].X, 6), Math.Round(holes[0].Y, 6), Math.Round(holes[0].Diameter, 6)));
        Assert.Equal((20, 15, 6), (Math.Round(holes[1].X, 6), Math.Round(holes[1].Y, 6), Math.Round(holes[1].Diameter, 6)));
        Assert.Equal((50, 15, 6), (Math.Round(holes[2].X, 6), Math.Round(holes[2].Y, 6), Math.Round(holes[2].Diameter, 6)));

        sheet.HoleTables.Add(new HoleTable { Id = drawing.NextId("ht"), ViewId = front.Id, X = 20, Y = 280 });
        sheet.Revisions = new RevisionTable { X = 250, Y = 280 };
        sheet.Revisions.Rows.Add(new Revision { Rev = sheet.Revisions.NextRev(), Description = "First release", Date = "2026-11-22" });
        Assert.Equal("B", sheet.Revisions.NextRev());
        var g = renderer.Render(sheet);
        var texts = g.Items.OfType<TextPrimitive>().Select(t => t.Text).ToList();
        Assert.Contains("X LOC", texts);
        Assert.Contains("Ø10", texts);
        Assert.Equal(2, texts.Count(t => t == "A1")); // tag by the hole and in the table
        Assert.Contains("First release", texts);

        var loaded = DrawingDocument.Load(drawing.Save());
        Assert.Single(loaded.Sheets[0].HoleTables);
        Assert.Equal("First release", loaded.Sheets[0].Revisions!.Rows[0].Description);
        var many = new RevisionTable();
        for (var i = 0; i < 27; i++)
        {
            many.Rows.Add(new Revision { Rev = many.NextRev() });
        }

        Assert.Equal("Z", many.Rows[25].Rev);
        Assert.Equal("AA", many.Rows[26].Rev);
    }

    [Fact]
    public void LayersAndDraftingStandards()
    {
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        drawing.AddStandardViews(sheet);
        drawing.Layers.Add(new DrawingLayer { Name = "Notes", Color = 0xFF0000 });
        sheet.Notes.Add(new Note { X = 20, Y = 60, Text = "DEBURR", Layer = "Notes" });
        var front = sheet.Views[0];
        var renderer = new DrawingRenderer(drawing);

        // Notes on the red layer: DXF layer and true colour, red in SVG.
        var g = renderer.Render(sheet);
        var text = g.Items.OfType<TextPrimitive>().Single(t => t.Text == "DEBURR");
        Assert.Equal("Notes", g.Layers[text]);
        var dxf = Exporters.Dxf(g);
        Assert.Contains("\n8\nNOTES\n420\n16711680\n", dxf, StringComparison.Ordinal);
        Assert.Contains("fill=\"#FF0000\"", Exporters.Svg(g), StringComparison.Ordinal);

        // A hidden layer isn't drawn.
        drawing.Layers[0].Visible = false;
        Assert.DoesNotContain(renderer.Render(sheet).Items.OfType<TextPrimitive>(), t => t.Text == "DEBURR");

        // ANSI: third-angle projection and horizontal text on vertical dimensions.
        var body = part.Bodies[0];
        var vg = renderer.Geometry(sheet, front);
        var left = Enumerable.Range(1, body.EdgeCount).First(i => body.Edge(i).Curve == Kernel.Occt.CurveKind.Line &&
            Math.Abs(Math.Abs(Vec3.Dot((body.Edge(i).End - body.Edge(i).Start).Normalized(), vg.YAxis)) - 1) < 1e-6);
        sheet.Dimensions.Add(new DrawingDimension
        {
            Id = drawing.NextId("dim"), ViewId = front.Id, Kind = DimensionKind.Vertical,
            A = new DimensionAnchor(body.EdgeRef(left), DimensionAnchor.Start, Vec3.Zero),
            B = new DimensionAnchor(body.EdgeRef(left), DimensionAnchor.End, Vec3.Zero),
        });
        drawing.ApplyStandard(DraftingStandard.Iso);
        Assert.True(drawing.FirstAngle);
        var iso = renderer.Render(sheet).Items.OfType<TextPrimitive>().Single(t => t.Text == "50");
        Assert.Equal(90, Math.Abs(iso.Angle), 1e-6);
        drawing.ApplyStandard(DraftingStandard.Ansi);
        Assert.False(drawing.FirstAngle);
        var ansi = renderer.Render(sheet).Items.OfType<TextPrimitive>().Single(t => t.Text == "50");
        Assert.Equal(0, ansi.Angle, 1e-9);

        var loaded = DrawingDocument.Load(drawing.Save());
        Assert.Equal(DraftingStandard.Ansi, loaded.Standard);
        Assert.False(Assert.Single(loaded.Layers).Visible);
        Assert.Equal("Notes", loaded.Sheets[0].Notes[0].Layer);
    }

    private static IEnumerable<string> Mutations(string text, int count, int seed)
    {
        var rng = new Random(seed);
        string[] junk = ["", "null", "-1", "1e308", "NaN", "\"\"", "[]", "{}", "0", "true"];
        for (var n = 0; n < count; n++)
        {
            var sb = new System.Text.StringBuilder(text);
            for (var k = 1 + rng.Next(4); k > 0 && sb.Length > 0; k--)
            {
                var at = rng.Next(sb.Length);
                switch (rng.Next(4))
                {
                    case 0:
                        sb.Remove(at, Math.Min(sb.Length - at, 1 + rng.Next(8)));
                        break;
                    case 1:
                        sb.Insert(at, (char)rng.Next(32, 127));
                        break;
                    case 2:
                        var s = sb.ToString();
                        var digit = s.IndexOfAny("0123456789".ToCharArray(), at);
                        if (digit >= 0)
                        {
                            var end = digit;
                            while (end < s.Length && (char.IsDigit(s[end]) || s[end] is '.' or 'e' or '-'))
                            {
                                end++;
                            }

                            sb.Remove(digit, end - digit).Insert(digit, junk[rng.Next(junk.Length)]);
                        }

                        break;
                    default:
                        sb[at] = (char)rng.Next(32, 127);
                        break;
                }
            }

            yield return sb.ToString();
        }
    }

    private static bool Expected(Exception ex) => ex is System.Text.Json.JsonException or NotSupportedException or FormatException;

    [Fact]
    public void MutatedDrawingsLoadOrFailCleanly()
    {
        var (part, _) = Part();
        var drawing = Drawing(part);
        var sheet = drawing.Sheets[0];
        drawing.AddStandardViews(sheet);
        sheet.Notes.Add(new Note { X = 20, Y = 60, Text = "NOTE" });
        sheet.Tolerances.Add(new GeometricTolerance { Id = "gdt1", Datums = ["A"], X = 100, Y = 100, LeaderX = 90, LeaderY = 90 });
        sheet.HoleTables.Add(new HoleTable { Id = "ht1", ViewId = sheet.Views[0].Id, X = 300, Y = 150 });
        sheet.Revisions = new RevisionTable { X = 250, Y = 280, Rows = [new Revision { Description = "First" }] };
        var json = drawing.Save();
        var failures = new List<string>();
        foreach (var text in Mutations(json, 150, 17))
        {
            try
            {
                var loaded = DrawingDocument.Load(text);
                loaded.SetModel(part);
                var renderer = new DrawingRenderer(loaded);
                foreach (var sh in loaded.Sheets)
                {
                    renderer.Render(sh);
                }
            }
            catch (Exception ex) when (Expected(ex))
            {
            }
            catch (Exception ex)
            {
                failures.Add($"{ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct().Take(10)));
    }

    [Fact]
    public void VaultLocksRevisionsHistoryAndWhereUsed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"chamfer-vault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (part, _) = Part();
            var partPath = Path.Combine(root, "bracket.chprt");
            File.WriteAllText(partPath, part.Save());
            var asm = new Chamfer.Assembly.AssemblyDocument { Directory = root };
            asm.AddComponent("bracket.chprt");
            File.WriteAllText(Path.Combine(root, "frame.chasm"), asm.Save());
            File.WriteAllText(Path.Combine(root, "bracket.chdrw"), new DrawingDocument { ModelPath = "bracket.chprt" }.Save());
            File.WriteAllText(Path.Combine(root, "other.chprt"), new PartDocument().Save());

            var vault = new Vault(root);
            vault.CheckOut(partPath, "alice");
            Assert.Equal("alice", vault.LockOf(partPath)!.User);
            Assert.Throws<InvalidOperationException>(() => vault.CheckOut(partPath, "bob"));
            var a = vault.CheckIn(partPath, "alice", "First release");
            Assert.Equal("A", a.Rev);
            Assert.Null(vault.LockOf(partPath));

            vault.CheckOut(partPath, "bob");
            Vault.StampRevision(part, "B");
            File.WriteAllText(partPath, part.Save());
            var b = vault.CheckIn(partPath, "bob", "Thicker flange");
            Assert.Equal("B", b.Rev);
            Assert.Equal(["A", "B"], vault.History(partPath).Select(r => r.Rev));
            Assert.Equal("B", vault.RevisionOf(partPath));

            vault.Restore(partPath, "A");
            Assert.Null(PropertyEvaluator.Get(PartDocument.Load(File.ReadAllText(partPath)), "Revision"));

            var used = vault.WhereUsed(partPath);
            Assert.Equal(["bracket.chdrw", "frame.chasm"], used.Order(StringComparer.Ordinal));
            Assert.Empty(vault.WhereUsed(Path.Combine(root, "other.chprt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
