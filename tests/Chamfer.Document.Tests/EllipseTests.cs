using Chamfer.Document.Features;
using Chamfer.Sketching;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Document.Tests;

public class EllipseTests
{
    private static double ExtrudedVolume(Sketch s)
    {
        var doc = new PartDocument();
        var sf = doc.Add(new SketchFeature { Sketch = s });
        var ex = doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 5 });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[ex.Id].Status);
        return doc.MassProperties().Volume;
    }

    [Fact]
    public void EllipsesExtrudeAndRoundTrip()
    {
        var s = new Sketch();
        s.AddEllipse(Vec2.Zero, new Vec2(30, 0), 10);
        Assert.Equal(Math.PI * 30 * 10 * 5, ExtrudedVolume(s), 1e-3);

        // Rotated, with a round hole inside it.
        var r = new Sketch();
        r.AddEllipse(new Vec2(5, 5), new Vec2(5, 25), 8);
        r.AddCircle(new Vec2(5, 5), 3);
        Assert.Equal(Math.PI * ((20 * 8) - 9) * 5, ExtrudedVolume(r), 1e-3);

        // A minor axis longer than the "major" one still works (the axes swap).
        var w = new Sketch();
        w.AddEllipse(Vec2.Zero, new Vec2(6, 0), 15);
        Assert.Equal(Math.PI * 6 * 15 * 5, ExtrudedVolume(w), 1e-3);

        var back = SketchSerializer.Deserialize(SketchSerializer.Serialize(r));
        var el = Assert.Single(back.Entities.OfType<SketchEllipse>());
        Assert.Equal(20, el.MajorRadius, 1e-12);
        Assert.Equal(8, el.MinorRadius, 1e-12);
        Assert.Equal(Math.PI / 2, el.Angle, 1e-12);
        var top = el.ClosestPoint(new Vec2(5, 40));
        Assert.Equal(5, top.X, 1e-9);
        Assert.Equal(25, top.Y, 1e-9);
    }
}
