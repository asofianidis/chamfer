using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class CustomPropertyTests
{
    [Fact]
    public void PropertiesEvaluateTokensAndRoundTrip()
    {
        var doc = new PartDocument();
        doc.Variables.Add(new GlobalVariable("Width", "40"));
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 10));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();

        PropertyEvaluator.Set(doc, "PartNumber", "BR-{Width}");
        PropertyEvaluator.Set(doc, "Description", "Bar {Width} mm, {Material}");
        PropertyEvaluator.Set(doc, "Weight", "{Mass} kg");
        PropertyEvaluator.Set(doc, "Label", "{PartNumber} / {Unknown}");
        Assert.Equal("BR-40", PropertyEvaluator.Get(doc, "partnumber"));
        Assert.Equal("Bar 40 mm, Plain Carbon Steel", PropertyEvaluator.Get(doc, "Description"));
        Assert.Equal($"{(4000 * 7800e-9).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} kg", PropertyEvaluator.Get(doc, "Weight"));
        Assert.Equal("BR-40 / {Unknown}", PropertyEvaluator.Get(doc, "Label"));
        Assert.Null(PropertyEvaluator.Get(doc, "Missing"));

        PropertyEvaluator.Set(doc, "Label", string.Empty);
        Assert.Equal(3, doc.Properties.Count);

        var loaded = PartDocument.Load(doc.Save());
        loaded.Rebuild();
        Assert.Equal("BR-40", PropertyEvaluator.Get(loaded, "PartNumber"));
        Assert.Equal(3, loaded.Properties.Count);
    }
}
