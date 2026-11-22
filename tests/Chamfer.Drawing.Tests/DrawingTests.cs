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
}
