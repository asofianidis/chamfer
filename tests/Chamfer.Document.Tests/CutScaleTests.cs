using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class CutScaleTests
{
    /// <summary>A 40 × 40 × 20 block from (x0, 0, 0).</summary>
    private static PartDocument Block(double x0 = 0)
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(x0, 0), new Vec2(x0 + 40, 40));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
        doc.Rebuild();
        return doc;
    }

    [Fact]
    public void ScaleAboutTheCentroid()
    {
        var doc = Block();
        var before = doc.MassProperties();
        var scale = doc.Add(new ScaleFeature { Factor = 1.5 });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[scale.Id].Status);
        var after = doc.MassProperties();
        Assert.Equal(before.Volume * 3.375, after.Volume, 1e-6);
        Assert.True(Vec3.Distance(before.CenterOfMass, after.CenterOfMass) < 1e-9);
        Assert.Equal(60, doc.Bodies[0].Shape.Bounds.Size.X, 1e-9);

        ((ScaleFeature)scale).AboutCentroid = false;
        ((ScaleFeature)scale).Factor = 0.5;
        doc.Rebuild();
        Assert.True(Vec3.Distance(new Vec3(10, 10, 5), doc.MassProperties().CenterOfMass) < 1e-9);
    }

    [Fact]
    public void CutWithPlaneAndSurface()
    {
        // The Right plane (x = 0, normal +X) through a block spanning x = -20..20.
        var doc = Block(-20);
        var cut = doc.Add(new CutWithSurfaceFeature { Plane = PlaneRef.Right });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[cut.Id].Status);
        Assert.Equal(40 * 40 * 20 / 2, doc.MassProperties().Volume, 1e-6);
        Assert.Equal(0, doc.Bodies[0].Shape.Bounds.Max.X, 1e-9);
        ((CutWithSurfaceFeature)cut).Flip = true;
        doc.Rebuild();
        Assert.Equal(0, doc.Bodies[0].Shape.Bounds.Min.X, 1e-9);

        // A surface body (an extruded line at y = 10) cuts the block; the two sides add up.
        var doc2 = Block();
        var line = new Sketch();
        line.AddLine(new Vec2(-10, 10), new Vec2(50, 10));
        var lf = doc2.Add(new SketchFeature { Sketch = line });
        doc2.Add(new SurfaceExtrudeFeature { SketchId = lf.Id, Depth = 60, MidPlane = true });
        doc2.Rebuild();
        var surface = doc2.Bodies.Single(b => b.IsSurface);
        var c2 = doc2.Add(new CutWithSurfaceFeature { Surface = surface.FaceRef(1) });
        doc2.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc2.Results[c2.Id].Status);
        var one = doc2.Bodies.Single(b => !b.IsSurface).Shape.MassProperties.Volume;
        ((CutWithSurfaceFeature)c2).Flip = true;
        doc2.Rebuild();
        var other = doc2.Bodies.Single(b => !b.IsSurface).Shape.MassProperties.Volume;
        Assert.Equal(40 * 40 * 20, one + other, 1e-6);
        Assert.Contains(new[] { one, other }, v => Math.Abs(v - (40 * 10 * 20)) < 1e-6);

        var loaded = PartDocument.Load(doc2.Save());
        loaded.Rebuild();
        Assert.Equal(other, loaded.Bodies.Single(b => !b.IsSurface).Shape.MassProperties.Volume, 1e-6);
    }
}
