using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;

namespace Chamfer.Assembly.Tests;

public class AssemblyTests
{
    private static PartDocument Block(double w, double h, double d, double holeRadius = 0)
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(w, h));
        if (holeRadius > 0)
        {
            s.AddCircle(new Vec2(w / 2, h / 2), holeRadius);
        }

        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = d });
        doc.Rebuild();
        return doc;
    }

    private static PartDocument Pin(double r, double length)
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCircle(Vec2.Zero, r);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = length });
        doc.Rebuild();
        return doc;
    }

    private static TopoRef FaceWhere(PartDocument part, Func<FaceInfo, bool> pick)
    {
        var body = part.Bodies[0];
        return body.FaceRef(Enumerable.Range(1, body.FaceCount).First(i => pick(body.Face(i))));
    }

    private static Vec3 Origin(Component c) => new(c.Transform.Tx, c.Transform.Ty, c.Transform.Tz);

    [Fact]
    public void StackBlocksWithCoincidentMates()
    {
        var asm = new AssemblyDocument();
        var baseBlock = Block(40, 20, 10);
        var topBlock = Block(20, 20, 10);
        asm.RegisterPart("base.chprt", baseBlock);
        asm.RegisterPart("top.chprt", topBlock);
        var a = asm.AddComponent("base.chprt");
        var b = asm.AddComponent("top.chprt", Transform3.Translation(new Vec3(50, 30, 25)) * Transform3.Rotation(Vec3.Zero, Vec3.UnitZ, 0.3));
        Assert.True(a.Fixed);
        Assert.False(b.Fixed);

        // B's bottom (-Z) on A's top (+Z); B's -X side flush with A's -X side; B's -Y side flush with A's -Y side.
        asm.AddMate(MateType.Coincident, new MateRef(a.Id, FaceWhere(baseBlock, f => f.Normal.Z > 0.99)), new MateRef(b.Id, FaceWhere(topBlock, f => f.Normal.Z < -0.99)));
        asm.AddMate(MateType.Coincident, new MateRef(a.Id, FaceWhere(baseBlock, f => f.Normal.X < -0.99)), new MateRef(b.Id, FaceWhere(topBlock, f => f.Normal.X < -0.99)), aligned: true);
        asm.AddMate(MateType.Coincident, new MateRef(a.Id, FaceWhere(baseBlock, f => f.Normal.Y < -0.99)), new MateRef(b.Id, FaceWhere(topBlock, f => f.Normal.Y < -0.99)), aligned: true);
        var result = asm.Solve();
        Assert.True(result.Converged, $"residual {result.Residual}");
        Assert.Equal(0, result.DegreesOfFreedom);
        Assert.Empty(result.FailedMates);
        Assert.True(Vec3.Distance(new Vec3(0, 0, 10), Origin(b)) < 1e-6, $"B at {Origin(b)}");

        // Nothing overlaps; moving B down into A creates an interference.
        Assert.Empty(AssemblyTools.FindInterferences(asm));
        b.Transform = Transform3.Translation(new Vec3(0, 0, 5));
        var hit = Assert.Single(AssemblyTools.FindInterferences(asm));
        Assert.Equal(20 * 20 * 5, hit.Volume, 1e-6);
    }

    [Fact]
    public void PinInHoleLeavesRotationFree()
    {
        var asm = new AssemblyDocument();
        var plate = Block(40, 40, 10, holeRadius: 5);
        var pin = Pin(5, 30);
        asm.RegisterPart("plate.chprt", plate);
        asm.RegisterPart("pin.chprt", pin);
        var p = asm.AddComponent("plate.chprt");
        var q = asm.AddComponent("pin.chprt", Transform3.Translation(new Vec3(-30, 10, 40)) * Transform3.Rotation(Vec3.Zero, Vec3.UnitX, 0.2));

        asm.AddMate(MateType.Concentric,
            new MateRef(p.Id, FaceWhere(plate, f => f.Surface == SurfaceKind.Cylinder)),
            new MateRef(q.Id, FaceWhere(pin, f => f.Surface == SurfaceKind.Cylinder)));
        var r1 = asm.Solve();
        Assert.True(r1.Converged);
        Assert.Equal(2, r1.DegreesOfFreedom); // slide along and turn about the axis

        // Pin's bottom flush with the plate's bottom: only rotation remains.
        asm.AddMate(MateType.Coincident,
            new MateRef(p.Id, FaceWhere(plate, f => f.Normal.Z < -0.99)),
            new MateRef(q.Id, FaceWhere(pin, f => f.Normal.Z < -0.99)), aligned: true);
        var r2 = asm.Solve();
        Assert.True(r2.Converged);
        Assert.Equal(1, r2.DegreesOfFreedom);
        var axisPoint = q.Transform.Apply(Vec3.Zero);
        Assert.Equal(20, axisPoint.X, 1e-6);
        Assert.Equal(20, axisPoint.Y, 1e-6);
        Assert.Equal(0, axisPoint.Z, 1e-6);
        Assert.Equal(1, Math.Abs(q.Transform.ApplyVector(Vec3.UnitZ).Z), 1e-9);
    }

    [Fact]
    public void DistanceAngleAndLockMates()
    {
        var asm = new AssemblyDocument();
        var block = Block(20, 20, 20);
        asm.RegisterPart("block.chprt", block);
        var a = asm.AddComponent("block.chprt");
        var b = asm.AddComponent("block.chprt", Transform3.Translation(new Vec3(30, 0, 0)));
        var top = FaceWhere(block, f => f.Normal.Z > 0.99);
        var bottom = FaceWhere(block, f => f.Normal.Z < -0.99);

        var distance = asm.AddMate(MateType.Distance, new MateRef(a.Id, top), new MateRef(b.Id, bottom), 15);
        Assert.True(asm.Solve().Converged);
        Assert.Equal(35, b.Transform.Tz, 1e-6);

        distance.Value = 5;
        Assert.True(asm.Solve().Converged);
        Assert.Equal(25, b.Transform.Tz, 1e-6);

        // Lock a third block to the second, then move the second: the third follows.
        var c = asm.AddComponent("block.chprt", Transform3.Translation(new Vec3(30, 30, 25)));
        asm.AddMate(MateType.Lock, new MateRef(b.Id, PlaneId: "front"), new MateRef(c.Id, PlaneId: "front"));
        distance.Value = 50;
        Assert.True(asm.Solve().Converged);
        Assert.Equal(70, b.Transform.Tz, 1e-6);
        Assert.Equal(70, c.Transform.Tz, 1e-6);
        Assert.Equal(30, c.Transform.Ty, 1e-6);

        // Angle between two side faces.
        var d = asm.AddComponent("block.chprt", Transform3.Translation(new Vec3(-60, 0, 0)));
        asm.AddMate(MateType.Coincident, new MateRef(a.Id, bottom), new MateRef(d.Id, bottom), aligned: true);
        asm.AddMate(MateType.Angle, new MateRef(a.Id, FaceWhere(block, f => f.Normal.X > 0.99)), new MateRef(d.Id, FaceWhere(block, f => f.Normal.X > 0.99)), 30, aligned: true);
        var result = asm.Solve();
        Assert.True(result.Converged);
        var dx = d.Transform.ApplyVector(Vec3.UnitX);
        Assert.Equal(Math.Cos(Math.PI / 6), dx.X, 1e-6);
    }

    [Fact]
    public void ConflictingMatesAreReported()
    {
        var asm = new AssemblyDocument();
        var block = Block(20, 20, 20);
        asm.RegisterPart("block.chprt", block);
        var a = asm.AddComponent("block.chprt");
        var b = asm.AddComponent("block.chprt", Transform3.Translation(new Vec3(0, 0, 30)));
        var top = FaceWhere(block, f => f.Normal.Z > 0.99);
        var bottom = FaceWhere(block, f => f.Normal.Z < -0.99);
        asm.AddMate(MateType.Distance, new MateRef(a.Id, top), new MateRef(b.Id, bottom), 5);
        asm.AddMate(MateType.Distance, new MateRef(a.Id, top), new MateRef(b.Id, bottom), 15);
        var result = asm.Solve();
        Assert.False(result.Converged);
        Assert.NotEmpty(result.FailedMates);
    }

    [Fact]
    public void BillOfMaterialsExplodeAndSaveLoad()
    {
        var asm = new AssemblyDocument();
        var block = Block(20, 20, 20);
        var pin = Pin(2, 10);
        asm.RegisterPart("block.chprt", block);
        asm.RegisterPart("pin.chprt", pin);
        asm.AddComponent("block.chprt");
        asm.AddComponent("pin.chprt", Transform3.Translation(new Vec3(5, 5, 20)));
        asm.AddComponent("pin.chprt", Transform3.Translation(new Vec3(15, 15, 20)));
        var bom = AssemblyTools.BillOfMaterials(asm);
        Assert.Equal(2, bom.Count);
        Assert.Equal(2, bom.Single(l => l.PartName == "pin").Quantity);
        Assert.Equal(8000 * 7800e-9, bom.Single(l => l.PartName == "block").MassEach, 1e-9);

        AssemblyTools.AutoExplode(asm, 1);
        Assert.Equal(Vec3.Zero, asm.Components[0].ExplodeOffset);
        Assert.True(asm.Components[1].ExplodeOffset.LengthSquared > 0);

        var json = asm.Save();
        var loaded = AssemblyDocument.Load(json);
        loaded.RegisterPart("block.chprt", block);
        loaded.RegisterPart("pin.chprt", pin);
        Assert.Equal(3, loaded.Components.Count);
        Assert.Equal(asm.Components[2].Transform, loaded.Components[2].Transform);
        Assert.Equal("pin<2>", loaded.Components[2].Name);
    }

    [Fact]
    public void PartsLoadFromDiskRelativeToTheAssembly()
    {
        var dir = Directory.CreateTempSubdirectory("chamfer-asm-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "block.chprt"), Block(10, 10, 10).Save());
            var asm = new AssemblyDocument { Directory = dir.FullName };
            var c = asm.AddComponent("block.chprt");
            Assert.Equal(1000, asm.PartOf(c).MassProperties().Volume, 1e-6);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
