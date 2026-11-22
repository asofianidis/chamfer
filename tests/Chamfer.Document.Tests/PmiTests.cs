using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class PmiTests
{
    [Fact]
    public void SizesAndGroupedHoleCallouts()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        s.AddCircle(new Vec2(10, 10), 3);
        s.AddCircle(new Vec2(20, 10), 3);
        s.AddCircle(new Vec2(32, 10), 2);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var texts = Pmi.Build(doc).Select(a => a.Text).ToList();
        Assert.Contains("40", texts);
        Assert.Contains("20", texts);
        Assert.Contains("10", texts);
        Assert.Contains("2× Ø6 THRU", texts);
        Assert.Contains("Ø4 THRU", texts);
        Assert.Equal(5, texts.Count);

        var inches = Pmi.Build(doc, LengthUnit.Inch).Select(a => a.Text).ToList();
        Assert.Contains("1.5748", inches);
        Assert.Empty(Pmi.Build(new PartDocument()));
    }
}
