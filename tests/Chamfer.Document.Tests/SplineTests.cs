using Chamfer.Document.Features;
using Chamfer.Sketching;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Document.Tests;

public class SplineTests
{
    private static (PartDocument Doc, Feature Extrude) Extrude(Sketch s)
    {
        var doc = new PartDocument();
        var sf = doc.Add(new SketchFeature { Sketch = s });
        var ex = doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 5 });
        doc.Rebuild();
        return (doc, ex);
    }

    [Fact]
    public void SplinesBoundProfilesAndRoundTrip()
    {
        // Three lines and a spline arching over the top.
        var s = new Sketch();
        s.AddLine(new Vec2(0, 20), new Vec2(0, 0));
        s.AddLine(new Vec2(0, 0), new Vec2(40, 0));
        s.AddLine(new Vec2(40, 0), new Vec2(40, 20));
        s.AddSpline([new Vec2(40, 20), new Vec2(20, 28), new Vec2(0, 20)]);
        var (doc, ex) = Extrude(s);
        Assert.Equal(FeatureStatus.Ok, doc.Results[ex.Id].Status);
        var body = Assert.Single(doc.Bodies);
        Assert.True(body.Shape.IsValid);
        var v = doc.MassProperties().Volume;
        Assert.InRange(v, (40 * 20 * 5) + 500, 40 * 28 * 5); // the arch adds area but stays under its peak
        Assert.Equal(28, body.Shape.Bounds.Max.Y, 0.5);

        // A spline that closes on itself is a loop on its own.
        var c = new Sketch();
        c.AddSpline([new Vec2(0, 0), new Vec2(20, -10), new Vec2(40, 0), new Vec2(20, 10), new Vec2(0, 0)]);
        var (closed, cex) = Extrude(c);
        Assert.Equal(FeatureStatus.Ok, closed.Results[cex.Id].Status);
        Assert.True(closed.MassProperties().Volume > 0);

        var back = SketchSerializer.Deserialize(SketchSerializer.Serialize(s));
        var sp = Assert.Single(back.Entities.OfType<SketchSpline>());
        Assert.Equal(3, sp.Points.Count);
        Assert.Equal(new Vec2(20, 28), sp.Points[1].Position);
        Assert.Equal(v, Extrude(back).Doc.MassProperties().Volume, 1e-6);
    }
}
