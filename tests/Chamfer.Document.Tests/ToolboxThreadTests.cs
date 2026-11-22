using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class ToolboxThreadTests
{
    private static void AssertOk(PartDocument doc)
    {
        foreach (var f in doc.Features)
        {
            var r = doc.Results[f.Id];
            Assert.True(r.Status is FeatureStatus.Ok, $"{f.Name}: {r.Status} {r.Message}");
        }
    }

    [Fact]
    public void SpringFromHelixSweep()
    {
        var doc = new PartDocument();
        var c = new Sketch();
        c.AddCircle(Vec2.Zero, 10);
        var circle = doc.Add(new SketchFeature { Sketch = c, Plane = PlaneRef.Top });
        var helix = doc.Add(new HelixFeature { SketchId = circle.Id, Pitch = 6, Height = 30 });

        // Wire profile: a small circle on the Front plane at the helix start (x = 10).
        var p = new Sketch();
        p.AddCircle(new Vec2(10, 0), 1);
        var profile = doc.Add(new SketchFeature { Sketch = p, Plane = PlaneRef.Front });
        doc.Add(new SweepFeature { ProfileSketchId = profile.Id, PathSketchId = helix.Id });
        doc.Rebuild();
        AssertOk(doc);
        var turns = 30.0 / 6;
        var wireLength = turns * Math.Sqrt(Math.Pow(2 * Math.PI * 10, 2) + 36);
        Assert.Equal(Math.PI * wireLength, doc.MassProperties().Volume, Math.PI * wireLength * 0.02);
        Assert.Equal(30, doc.Bodies[0].Shape.Bounds.Size.Y, 2.5);
    }

    [Fact]
    public void ExternalAndInternalThreads()
    {
        var doc = new PartDocument();
        var c = new Sketch();
        c.AddCircle(Vec2.Zero, 5);
        var sf = doc.Add(new SketchFeature { Sketch = c });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var shaft = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Surface == SurfaceKind.Cylinder);
        var before = doc.MassProperties().Volume;
        doc.Add(new ThreadFeature { Face = body.FaceRef(shaft), Pitch = 1.5, Length = 12 });
        doc.Rebuild();
        AssertOk(doc);
        var after = doc.MassProperties().Volume;
        Assert.True(after < before, "the thread removes material");
        Assert.True(after > before * 0.7, "but not too much");
        Assert.True(doc.Bodies[0].Shape.IsValid);

        // Internal thread in a nut-like block.
        var nut = new PartDocument();
        var n = new Sketch();
        n.AddCenterRectangle(Vec2.Zero, new Vec2(10, 10));
        n.AddCircle(Vec2.Zero, 4);
        var nf = nut.Add(new SketchFeature { Sketch = n });
        nut.Add(new ExtrudeFeature { SketchId = nf.Id, Depth = 10 });
        nut.Rebuild();
        var nb = nut.Bodies[0];
        var bore = Enumerable.Range(1, nb.FaceCount).Single(i => nb.Face(i).Surface == SurfaceKind.Cylinder);
        var nutBefore = nut.MassProperties().Volume;
        nut.Add(new ThreadFeature { Face = nb.FaceRef(bore), Pitch = 1.25 });
        nut.Rebuild();
        AssertOk(nut);
        Assert.True(nut.MassProperties().Volume < nutBefore);
        Assert.True(nut.Bodies[0].Shape.Bounds.Size.X <= 20 + 1e-6, "an internal thread stays inside the block");
    }

    [Theory]
    [InlineData("toolbox:ISO4762 M6x20")]
    [InlineData("toolbox:ISO4017 M8x30")]
    [InlineData("toolbox:ISO4032 M10")]
    [InlineData("toolbox:ISO7089 M6")]
    public void ToolboxPartsBuild(string path)
    {
        var part = Toolbox.Create(path);
        AssertOk(part);
        Assert.Single(part.Bodies);
        Assert.True(part.Bodies[0].Shape.IsValid);
        var spec = FastenerSpec.Parse(path)!;
        Assert.Equal(path, spec.Path);
    }

    [Fact]
    public void SocketHeadScrewDimensions()
    {
        var part = Toolbox.Create(new FastenerSpec(FastenerKind.SocketHeadCapScrew, "M6", 20));
        var box = part.Bodies[0].Shape.Bounds;
        Assert.Equal(10, box.Size.X, 1e-6);    // head diameter
        Assert.Equal(-20, box.Min.Z, 1e-6);    // shank below the head
        Assert.Equal(6, box.Max.Z, 1e-6);      // head height
        var socketDepth = 3.0;
        var socketArea = 3 * Math.Sqrt(3) / 2 * Math.Pow(5 / Math.Sqrt(3), 2);
        var expected = (Math.PI * 25 * 6) + (Math.PI * 9 * 20) - (socketArea * socketDepth);
        Assert.Equal(expected, part.MassProperties().Volume, 1e-3);
        Assert.Equal("ISO 4762 M6x20", new FastenerSpec(FastenerKind.SocketHeadCapScrew, "M6", 20).Description);
    }
}
