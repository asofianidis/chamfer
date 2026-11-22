using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class Sketch3DTests
{
    [Fact]
    public void ParsesAbsoluteAndRelativePoints()
    {
        var pts = Sketch3DFeature.Parse("0,0,0 | @0,0,50; 40 0 50 \n @0,30,0");
        Assert.Equal([new Point3D(0, 0, 0), new Point3D(0, 0, 50), new Point3D(40, 0, 50), new Point3D(40, 30, 50)], pts);
        Assert.Equal("0,0,0 | 0,0,50 | 40,0,50 | 40,30,50", Sketch3DFeature.Format(pts));
        Assert.Throws<FormatException>(() => Sketch3DFeature.Parse("1,2"));
    }

    [Fact]
    public void SweepAlongA3DPath()
    {
        var doc = new PartDocument();
        var path = doc.Add(new Sketch3DFeature { Points = Sketch3DFeature.Parse("0,0,0 | @0,0,50 | @40,0,0 | @0,30,0") });
        var s = new Sketch();
        s.AddCircle(Vec2.Zero, 3);
        var profile = doc.Add(new SketchFeature { Sketch = s });
        var sweep = doc.Add(new SweepFeature { ProfileSketchId = profile.Id, PathSketchId = path.Id });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[sweep.Id].Status);
        Assert.True(doc.State.Curves.ContainsKey(path.Id));
        var expected = Math.PI * 9 * 120;
        Assert.InRange(doc.MassProperties().Volume, expected * 0.9, expected * 1.1);
        var box = doc.Bodies[0].Shape.Bounds;
        Assert.Equal(30, box.Max.Y, 1e-3); // ends square at y = 30
        Assert.Equal(50 + 3, box.Max.Z, 1e-3);

        // A spline through the same points also sweeps.
        ((Sketch3DFeature)path).Spline = true;
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[sweep.Id].Status);
        Assert.True(doc.Bodies[0].Shape.IsValid);

        var loaded = PartDocument.Load(doc.Save());
        loaded.Rebuild();
        Assert.Equal(doc.MassProperties().Volume, loaded.MassProperties().Volume, 1e-6);

        ((Sketch3DFeature)path).Points = [new Point3D(1, 1, 1)];
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Error, doc.Results[path.Id].Status);
    }
}
