using Chamfer.Document.Features;
using Chamfer.Sketching;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Document.Tests;

public class DxfImportTests
{
    private static string Dxf(string units, params string[] entities) =>
        string.Join("\n", ["0", "SECTION", "2", "HEADER", "9", "$INSUNITS", "70", units, "0", "ENDSEC",
            "0", "SECTION", "2", "ENTITIES", .. entities.SelectMany(e => e.Split(' ')), "0", "ENDSEC", "0", "EOF"]);

    [Fact]
    public void BulgedPolylineSlotWithHoleExtrudes()
    {
        var text = Dxf("4",
            "0 LWPOLYLINE 8 0 90 4 70 1 10 0 20 0 10 40 20 0 42 1 10 40 20 20 10 0 20 20 42 1",
            "0 CIRCLE 8 0 10 20 20 10 40 5",
            "0 TEXT 8 0 1 ignored");
        var sketch = new Sketch();
        var result = DxfImport.ImportText(sketch, text);
        Assert.Equal(2, result.Lines);
        Assert.Equal(2, result.Arcs);
        Assert.Equal(1, result.Circles);
        Assert.Equal(1, result.Skipped);

        var arc = sketch.Entities.OfType<SketchArc>().First();
        Assert.Equal(10, arc.Radius, 1e-9);

        var doc = new PartDocument();
        var sf = doc.Add(new SketchFeature { Sketch = sketch });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var expected = ((40 * 20) + (Math.PI * 100) - (Math.PI * 25)) * 10;
        Assert.Equal(expected, doc.MassProperties().Volume, 1e-3);
    }

    [Fact]
    public void ArcsQuarterBulgesAndInches()
    {
        var sketch = new Sketch();
        var quarter = Math.Tan(Math.PI / 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
        DxfImport.ImportText(sketch, Dxf("4", $"0 LWPOLYLINE 90 2 70 0 10 10 20 0 42 {quarter} 10 0 20 10"));
        var arc = Assert.Single(sketch.Entities.OfType<SketchArc>());
        Assert.Equal(0, arc.Center.X, 1e-9);
        Assert.Equal(0, arc.Center.Y, 1e-9);

        sketch = new Sketch();
        var r = DxfImport.ImportText(sketch, Dxf("1", "0 CIRCLE 10 1 20 2 40 0.5", "0 ARC 10 0 20 0 40 1 50 0 51 90", "0 LINE 10 0 20 0 11 2 21 0"));
        Assert.Equal(25.4, r.Scale);
        var circle = Assert.Single(sketch.Entities.OfType<SketchCircle>());
        Assert.Equal(12.7, circle.Radius, 1e-9);
        Assert.Equal(50.8, circle.Center.Y, 1e-9);
        var a = Assert.Single(sketch.Entities.OfType<SketchArc>());
        Assert.Equal(25.4, a.End.Y, 1e-9);
        Assert.Equal(50.8, Assert.Single(sketch.Entities.OfType<SketchLine>()).Length, 1e-9);
    }
}
