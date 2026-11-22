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

public class ToolingTests
{
    [Fact]
    public void CoreAndCavityFromAFlatParting()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCircle(Vec2.Zero, 10);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
        doc.Rebuild();
        var part = doc.MassProperties().Volume;

        var split = (ToolingSplitFeature)doc.Add(new ToolingSplitFeature { PartingHeight = 10, Margin = 10 });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[split.Id].Status);
        Assert.Equal(3, doc.Bodies.Count);
        var core = doc.Bodies.Single(b => b.Name == "Core");
        var cavity = doc.Bodies.Single(b => b.Name == "Cavity");
        var block = 40.0 * 40 * 40;
        Assert.Equal((block - part) / 2, core.Shape.MassProperties.Volume, 1e-3);
        Assert.Equal((block - part) / 2, cavity.Shape.MassProperties.Volume, 1e-3);
        Assert.Equal(10, core.Shape.Bounds.Max.Z, 1e-6);
        Assert.Equal(10, cavity.Shape.Bounds.Min.Z, 1e-6);

        // 2 % shrinkage enlarges the impression by 1.02³.
        split.Shrinkage = 1.02;
        doc.Rebuild();
        var tools = doc.Bodies.Where(b => b.Name is "Core" or "Cavity").Sum(b => b.Shape.MassProperties.Volume);
        Assert.Equal(block - (part * Math.Pow(1.02, 3)), tools, 1e-2);

        // A box's parting line for a +Z pull is its top edge loop (zero-draft walls).
        var box = new PartDocument();
        var bs = new Sketch();
        bs.AddCornerRectangle(new Vec2(0, 0), new Vec2(20, 20));
        var bf = box.Add(new SketchFeature { Sketch = bs });
        box.Add(new ExtrudeFeature { SketchId = bf.Id, Depth = 20 });
        box.Rebuild();
        var edges = PartingLines.Find(box.Bodies[0], Vec3.UnitZ);
        Assert.Equal(4, edges.Count);
        Assert.All(edges, e => Assert.Equal(20, box.Bodies[0].Edge(e).Midpoint.Z, 1e-9));
    }
}
