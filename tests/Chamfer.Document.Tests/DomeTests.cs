using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class DomeTests
{
    private static (PartDocument Doc, TopoRef Top) Extruded(Action<Sketch> draw)
    {
        var doc = new PartDocument();
        var s = new Sketch();
        draw(s);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var b = doc.Bodies[0];
        return (doc, b.FaceRef(Enumerable.Range(1, b.FaceCount).Single(i => b.Face(i).Normal.Z > 0.99)));
    }

    [Fact]
    public void DomeOnRoundAndSquareFaces()
    {
        var (doc, top) = Extruded(s => s.AddCircle(Vec2.Zero, 20));
        var cylinder = doc.MassProperties().Volume;
        var dome = (DomeFeature)doc.Add(new DomeFeature { Face = top, Height = 5 });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[dome.Id].Status);

        // Spherical cap: R = (c² + h²) / 2h = 42.5; V = π h² (3R − h) / 3.
        var cap = Math.PI * 25 * ((3 * 42.5) - 5) / 3;
        Assert.Equal(cylinder + cap, doc.MassProperties().Volume, 1);
        Assert.Equal(15, doc.Bodies[0].Shape.Bounds.Max.Z, 1e-6);
        Assert.True(doc.Bodies[0].Shape.IsValid);

        dome.Reverse = true;
        doc.Rebuild();
        Assert.Equal(cylinder - cap, doc.MassProperties().Volume, 1);
        Assert.Equal(10, doc.Bodies[0].Shape.Bounds.Max.Z, 1e-6);

        // A square face gets a dome clipped to its outline.
        var (sq, sqTop) = Extruded(s => s.AddCenterRectangle(Vec2.Zero, new Vec2(20, 20)));
        var block = sq.MassProperties().Volume;
        sq.Add(new DomeFeature { Face = sqTop, Height = 4 });
        sq.Rebuild();
        Assert.True(sq.MassProperties().Volume > block);
        Assert.Equal(14, sq.Bodies[0].Shape.Bounds.Max.Z, 1e-6);
        Assert.Single(sq.Bodies);
    }
}
