using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;

namespace Chamfer.Assembly.Tests;

public class AdvancedMateTests
{
    private static PartDocument Block(double w, double h, double d)
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(w, h));
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

    private static TopoRef Face(PartDocument part, Func<FaceInfo, bool> pick)
    {
        var b = part.Bodies[0];
        return b.FaceRef(Enumerable.Range(1, b.FaceCount).First(i => pick(b.Face(i))));
    }

    private static double TurnZ(Component c) => Math.Atan2(c.Transform.M10, c.Transform.M00);

    [Fact]
    public void GearMateCouplesRotations()
    {
        var asm = new AssemblyDocument();
        var plate = new PartDocument();
        var ps = new Sketch();
        ps.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 60));
        ps.AddCircle(new Vec2(30, 30), 4);
        ps.AddCircle(new Vec2(60, 30), 4);
        var psf = plate.Add(new SketchFeature { Sketch = ps });
        plate.Add(new ExtrudeFeature { SketchId = psf.Id, Depth = 10 });
        plate.Rebuild();
        var small = Pin(4, 20);
        var big = Pin(4, 20);
        asm.RegisterPart("plate.chprt", plate);
        asm.RegisterPart("small.chprt", small);
        asm.RegisterPart("big.chprt", big);
        var p = asm.AddComponent("plate.chprt");
        var a = asm.AddComponent("small.chprt", Transform3.Translation(new Vec3(30, 30, 0)));
        var b = asm.AddComponent("big.chprt", Transform3.Translation(new Vec3(60, 30, 0)));
        var body = plate.Bodies[0];
        var holes = Enumerable.Range(1, body.FaceCount).Where(i => body.Face(i).Surface == SurfaceKind.Cylinder)
            .OrderBy(i => body.Face(i).AxisOrigin.X).Select(body.FaceRef).ToList();
        var bottom = Face(plate, f => f.Normal.Z < -0.99);
        foreach (var (c, hole, part) in new[] { (a, holes[0], small), (b, holes[1], big) })
        {
            asm.AddMate(MateType.Concentric, new MateRef(p.Id, hole), new MateRef(c.Id, Face(part, f => f.Surface == SurfaceKind.Cylinder)));
            asm.AddMate(MateType.Coincident, new MateRef(p.Id, bottom), new MateRef(c.Id, Face(part, f => f.Normal.Z < -0.99)), aligned: true);
        }

        var free = asm.Solve();
        Assert.True(free.Converged, $"residual {free.Residual}");
        Assert.Equal(2, free.DegreesOfFreedom); // each pin spins

        asm.AddMate(MateType.Gear, new MateRef(a.Id, Face(small, f => f.Surface == SurfaceKind.Cylinder)), new MateRef(b.Id, Face(big, f => f.Surface == SurfaceKind.Cylinder)), 2);
        var coupled = asm.Solve();
        Assert.True(coupled.Converged);
        Assert.Equal(1, coupled.DegreesOfFreedom);

        // Turn one gear; after solving, the turns satisfy the ratio (A turns twice for each turn of B, oppositely).
        a.Transform = Transform3.Rotation(new Vec3(30, 30, 0), Vec3.UnitZ, 0.4) * a.Transform;
        Assert.True(asm.Solve().Converged);
        var ta = TurnZ(a);
        var tb = TurnZ(b);
        Assert.Equal(0, ta + (2 * tb), 1e-6);
        Assert.True(Math.Abs(ta) > 0.05);
    }

    [Fact]
    public void ScrewMateAdvancesWithRotation()
    {
        var asm = new AssemblyDocument();
        var bolt = Pin(5, 60);
        var nut = Pin(8, 6);
        asm.RegisterPart("bolt.chprt", bolt);
        asm.RegisterPart("nut.chprt", nut);
        var b = asm.AddComponent("bolt.chprt");
        var n = asm.AddComponent("nut.chprt", Transform3.Translation(new Vec3(0, 0, 20)));
        var boltSide = Face(bolt, f => f.Surface == SurfaceKind.Cylinder);
        var nutSide = Face(nut, f => f.Surface == SurfaceKind.Cylinder);
        asm.AddMate(MateType.Concentric, new MateRef(b.Id, boltSide), new MateRef(n.Id, nutSide));
        Assert.Equal(2, asm.Solve().DegreesOfFreedom);
        asm.AddMate(MateType.Screw, new MateRef(b.Id, boltSide), new MateRef(n.Id, nutSide), 1.5);
        var r = asm.Solve();
        Assert.True(r.Converged);
        Assert.Equal(1, r.DegreesOfFreedom);

        // Wind the nut a quarter turn: it advances a quarter pitch.
        n.Transform = Transform3.Rotation(Vec3.Zero, Vec3.UnitZ, Math.PI / 2) * n.Transform;
        Assert.True(asm.Solve().Converged);
        var turn = TurnZ(n);
        Assert.Equal(turn / (2 * Math.PI) * 1.5, n.Transform.Tz - 20, 1e-6);
    }

    [Fact]
    public void SymmetricWidthAndLimitMates()
    {
        var asm = new AssemblyDocument();
        var baseBlock = Block(100, 20, 10);
        var cube = Block(10, 10, 10);
        asm.RegisterPart("base.chprt", baseBlock);
        asm.RegisterPart("cube.chprt", cube);
        var p = asm.AddComponent("base.chprt");
        var c1 = asm.AddComponent("cube.chprt", Transform3.Translation(new Vec3(10, 0, 10)));
        var c2 = asm.AddComponent("cube.chprt", Transform3.Translation(new Vec3(70, 0, 10)));
        var top = Face(baseBlock, f => f.Normal.Z > 0.99);
        var bottom = Face(cube, f => f.Normal.Z < -0.99);
        asm.AddMate(MateType.Coincident, new MateRef(p.Id, top), new MateRef(c1.Id, bottom));
        asm.AddMate(MateType.Coincident, new MateRef(p.Id, top), new MateRef(c2.Id, bottom));
        var baseFront = Face(baseBlock, f => f.Normal.Y < -0.99);
        var cubeFront = Face(cube, f => f.Normal.Y < -0.99);
        asm.AddMate(MateType.Parallel, new MateRef(p.Id, baseFront), new MateRef(c1.Id, cubeFront), aligned: true);
        asm.AddMate(MateType.Parallel, new MateRef(p.Id, baseFront), new MateRef(c2.Id, cubeFront), aligned: true);

        // c1's -X face and c2's +X face mirror each other about the base's -X face (x = 0).
        asm.Components[0].Fixed = true;
        asm.AddMate(MateType.Symmetric, new MateRef(c1.Id, Face(cube, f => f.Normal.X < -0.99)), new MateRef(c2.Id, Face(cube, f => f.Normal.X > 0.99)))
            .C = new MateRef(p.Id, Face(baseBlock, f => f.Normal.X < -0.99));
        var r = asm.Solve();
        Assert.True(r.Converged, $"first residual {r.Residual} c1 {c1.Transform} c2 {c2.Transform} failed {string.Join(",", r.FailedMates.Select(m => m.Type))}");
        Assert.Equal(-(c2.Transform.Tx + 10), c1.Transform.Tx, 1e-6);

        // Limit: the gap between the cubes' facing sides stays between 5 and 10.
        var limit = asm.AddMate(MateType.LimitDistance, new MateRef(c1.Id, Face(cube, f => f.Normal.X > 0.99)), new MateRef(c2.Id, Face(cube, f => f.Normal.X < -0.99)), 5);
        limit.Value2 = 10;
        r = asm.Solve();
        Assert.True(r.Converged, $"residual {r.Residual} c1 {c1.Transform} c2 {c2.Transform} failed {string.Join(",", r.FailedMates.Select(m => m.Type))}");
        var gap = c2.Transform.Tx - (c1.Transform.Tx + 10);
        Assert.InRange(gap, 5 - 1e-6, 10 + 1e-6);
    }

    [Fact]
    public void WidthMateCentresATab()
    {
        var asm = new AssemblyDocument();
        var slotPart = Block(60, 20, 10);
        var tab = Block(10, 10, 10);
        asm.RegisterPart("slot.chprt", slotPart);
        asm.RegisterPart("tab.chprt", tab);
        var s = asm.AddComponent("slot.chprt");
        var t = asm.AddComponent("tab.chprt", Transform3.Translation(new Vec3(5, 3, 12)));
        asm.AddMate(MateType.Width,
            new MateRef(t.Id, Face(tab, f => f.Normal.X < -0.99)), new MateRef(t.Id, Face(tab, f => f.Normal.X > 0.99)))
            .C = new MateRef(s.Id, Face(slotPart, f => f.Normal.X < -0.99));
        asm.Mates[^1].D = new MateRef(s.Id, Face(slotPart, f => f.Normal.X > 0.99));
        Assert.True(asm.Solve().Converged);
        Assert.Equal(25, t.Transform.Tx, 1e-6); // tab 10 wide centred in 60
    }

    [Fact]
    public void ComponentPatternsFollowTheSeed()
    {
        var asm = new AssemblyDocument();
        var cube = Block(10, 10, 10);
        asm.RegisterPart("cube.chprt", cube);
        asm.AddComponent("cube.chprt");
        var seed = asm.AddComponent("cube.chprt", Transform3.Translation(new Vec3(0, 20, 0)));
        var copies = asm.LinearPattern(seed, Vec3.UnitX, 15, 4);
        Assert.Equal(3, copies.Count);
        Assert.Equal(45, copies[^1].Transform.Tx, 1e-9);
        // Dragging the seed holds it in place while the copies follow.
        seed.Transform = Transform3.Translation(new Vec3(0, 50, 0));
        seed.Fixed = true;
        Assert.True(asm.Solve().Converged);
        Assert.Equal(50, copies[^1].Transform.Ty, 1e-6);
        Assert.Equal(45, copies[^1].Transform.Tx, 1e-6);

        var ring = asm.CircularPattern(seed, Vec3.Zero, Vec3.UnitZ, 6);
        Assert.Equal(5, ring.Count);
        Assert.Equal(10, asm.Components.Count);
        Assert.Equal(-50, ring[2].Transform.Ty, 1e-6); // half-way round
    }

    [Fact]
    public void MotorDrivesAGearPair()
    {
        var asm = new AssemblyDocument();
        var plate = new PartDocument();
        var ps = new Sketch();
        ps.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 60));
        ps.AddCircle(new Vec2(30, 30), 4);
        ps.AddCircle(new Vec2(60, 30), 4);
        var psf = plate.Add(new SketchFeature { Sketch = ps });
        plate.Add(new ExtrudeFeature { SketchId = psf.Id, Depth = 10 });
        plate.Rebuild();
        var pin = Pin(4, 20);
        asm.RegisterPart("plate.chprt", plate);
        asm.RegisterPart("pin.chprt", pin);
        var p = asm.AddComponent("plate.chprt");
        var a = asm.AddComponent("pin.chprt", Transform3.Translation(new Vec3(30, 30, 0)));
        var b = asm.AddComponent("pin.chprt", Transform3.Translation(new Vec3(60, 30, 0)));
        var body = plate.Bodies[0];
        var holes = Enumerable.Range(1, body.FaceCount).Where(i => body.Face(i).Surface == SurfaceKind.Cylinder)
            .OrderBy(i => body.Face(i).AxisOrigin.X).Select(body.FaceRef).ToList();
        var side = Face(pin, f => f.Surface == SurfaceKind.Cylinder);
        foreach (var (c, hole) in new[] { (a, holes[0]), (b, holes[1]) })
        {
            asm.AddMate(MateType.Concentric, new MateRef(p.Id, hole), new MateRef(c.Id, side));
            asm.AddMate(MateType.Coincident, new MateRef(p.Id, Face(plate, f => f.Normal.Z < -0.99)), new MateRef(c.Id, Face(pin, f => f.Normal.Z < -0.99)), aligned: true);
        }

        asm.AddMate(MateType.Gear, new MateRef(a.Id, side), new MateRef(b.Id, side), 2);
        var start = a.Transform;
        var study = new MotionStudy { Duration = 0.25, FramesPerSecond = 24 };
        study.Motors.Add(new Motor { Reference = new MateRef(a.Id, side), Speed = 60 });
        var result = study.Run(asm, TestContext.Current.CancellationToken);
        Assert.Equal(7, result.Frames.Count);
        Assert.True(result.AllSolved);
        Assert.Equal(start, a.Transform); // the assembly is put back
        Assert.DoesNotContain(asm.Mates, m => m.Type == MateType.Drive);

        MotionStudy.Apply(asm, result.Frames[^1]);
        Assert.Equal(Math.PI / 2, Math.Abs(TurnZ(a)), 1e-6);
        Assert.Equal(0, TurnZ(a) + (2 * TurnZ(b)), 1e-6);
        Assert.Equal(30, a.Transform.Apply(Vec3.Zero).X, 1e-6); // still in its hole
    }

    [Fact]
    public void MotorWindsANutAlongAScrewAndCollisionsAreFound()
    {
        var asm = new AssemblyDocument();
        var bolt = Pin(5, 60);
        var nut = Pin(8, 6);
        asm.RegisterPart("bolt.chprt", bolt);
        asm.RegisterPart("nut.chprt", nut);
        var b = asm.AddComponent("bolt.chprt");
        var n = asm.AddComponent("nut.chprt", Transform3.Translation(new Vec3(0, 0, 20)));
        var boltSide = Face(bolt, f => f.Surface == SurfaceKind.Cylinder);
        var nutSide = Face(nut, f => f.Surface == SurfaceKind.Cylinder);
        asm.AddMate(MateType.Concentric, new MateRef(b.Id, boltSide), new MateRef(n.Id, nutSide));
        asm.AddMate(MateType.Screw, new MateRef(b.Id, boltSide), new MateRef(n.Id, nutSide), 1.5);
        var study = new MotionStudy { Duration = 1, FramesPerSecond = 12 };
        study.Motors.Add(new Motor { Reference = new MateRef(n.Id, nutSide), Speed = 120 }); // two turns
        var result = study.Run(asm, TestContext.Current.CancellationToken);
        Assert.True(result.AllSolved);
        Assert.Equal(3, Math.Abs(result.Frames[^1].Placements[n.Id].Tz - 20), 1e-6); // two turns of 1.5 mm pitch

        // A block sitting in the path of a linear motor: the collision is reported.
        var block = Block(20, 20, 10);
        asm.RegisterPart("block.chprt", block);
        var stop = asm.AddComponent("block.chprt", Transform3.Translation(new Vec3(30, 40, 0)));
        stop.Fixed = true;
        var slider = asm.AddComponent("block.chprt", Transform3.Translation(new Vec3(-30, 40, 0)));
        var run = new MotionStudy { Duration = 1, FramesPerSecond = 10, DetectCollisions = true };
        run.Motors.Add(new Motor { Kind = MotorKind.Linear, Reference = new MateRef(slider.Id, Face(block, f => f.Normal.X > 0.99)), Speed = 50 });
        var hit = run.Run(asm, TestContext.Current.CancellationToken);
        Assert.NotNull(hit.FirstCollisionTime);
        Assert.InRange(hit.FirstCollisionTime!.Value, 0.7, 0.9); // 40 mm gap at 50 mm/s
        Assert.Contains(stop.Name, hit.CollidingPair!, StringComparison.Ordinal);
    }

    [Fact]
    public void BomUsesPropertiesAndPackAndGoCopiesEverything()
    {
        var root = Path.Combine(Path.GetTempPath(), $"chamfer-pg-{Guid.NewGuid():N}");
        try
        {
            // Two parts with the same file name in different folders, plus a toolbox screw.
            Directory.CreateDirectory(Path.Combine(root, "src", "a"));
            Directory.CreateDirectory(Path.Combine(root, "src", "b"));
            var plate = Block(60, 40, 10);
            PropertyEvaluator.Set(plate, PropertyEvaluator.PartNumber, "PL-001");
            PropertyEvaluator.Set(plate, PropertyEvaluator.Description, "Base plate");
            File.WriteAllText(Path.Combine(root, "src", "a", "part.chprt"), plate.Save());
            File.WriteAllText(Path.Combine(root, "src", "b", "part.chprt"), Pin(4, 20).Save());
            var asm = new AssemblyDocument { Directory = Path.Combine(root, "src") };
            asm.AddComponent("a/part.chprt");
            asm.AddComponent("b/part.chprt", Transform3.Translation(new Vec3(10, 10, 10)));
            asm.AddComponent("toolbox:ISO4762 M6x16");

            var bom = AssemblyTools.BillOfMaterials(asm);
            Assert.Equal("PL-001", bom[0].PartName);
            Assert.Equal("Base plate", bom[0].Description);
            Assert.Equal("part", bom[1].PartName);

            var target = Path.Combine(root, "out");
            var result = PackAndGo.Run(asm, "Fixture", target, prefix: "X-", zip: true);
            Assert.Equal(3, result.Files.Count);
            Assert.EndsWith("X-Fixture.chasm", result.AssemblyPath, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(target, "X-part.chprt")));
            Assert.True(File.Exists(Path.Combine(target, "X-part (2).chprt")));

            var copy = AssemblyDocument.Load(File.ReadAllText(result.AssemblyPath), target);
            Assert.Equal(["X-part.chprt", "X-part (2).chprt", "toolbox:ISO4762 M6x16"], copy.Components.Select(c => c.Path));
            Assert.Equal("PL-001", AssemblyTools.BillOfMaterials(copy)[0].PartName);
            Assert.Equal(3, copy.Components.Count(c => copy.PartOf(c).Bodies.Count > 0));

            using var zip = System.IO.Compression.ZipFile.OpenRead(result.ZipPath!);
            Assert.Equal(3, zip.Entries.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AssemblyCutGoesThroughSeveralComponentsOnly()
    {
        var asm = new AssemblyDocument();
        var plate = Block(40, 40, 10);
        asm.RegisterPart("plate.chprt", plate);
        var lower = asm.AddComponent("plate.chprt");
        var upper = asm.AddComponent("plate.chprt", Transform3.Translation(new Vec3(0, 0, 10)));
        var partVolume = plate.MassProperties().Volume;

        // A Ø10 hole through both plates at (20, 20), sketched on the assembly's Front plane.
        var s = new Sketch();
        s.AddCircle(new Vec2(20, 20), 5);
        var cut = asm.AddCut(new AssemblyCut { Sketch = s, ThroughAll = true });
        var hole = Math.PI * 25 * 10;
        Assert.Equal(partVolume - hole, asm.BodiesOf(lower)[0].Shape.MassProperties.Volume, 1e-6);
        Assert.Equal(partVolume - hole, asm.BodiesOf(upper)[0].Shape.MassProperties.Volume, 1e-6);
        Assert.Equal(partVolume, plate.MassProperties().Volume, 1e-9); // the part itself is untouched

        // Only the upper plate, and only 5 mm deep from z = 15 downward.
        cut.Components = [upper.Id];
        cut.ThroughAll = false;
        cut.PlaneOffset = 20;
        cut.Reverse = true;
        cut.Depth = 5;
        Assert.Equal(partVolume, asm.BodiesOf(lower)[0].Shape.MassProperties.Volume, 1e-6);
        Assert.Equal(partVolume - (hole / 2), asm.BodiesOf(upper)[0].Shape.MassProperties.Volume, 1e-6);
        Assert.Equal(partVolume - (hole / 2), asm.PlacedBodies(upper).Single().Placed.MassProperties.Volume, 1e-6);

        // Saved with the assembly; suppressing restores the parts.
        var loaded = AssemblyDocument.Load(asm.Save());
        loaded.RegisterPart("plate.chprt", plate);
        Assert.Single(loaded.Features);
        Assert.Equal(partVolume - (hole / 2), loaded.BodiesOf(loaded.Components[1])[0].Shape.MassProperties.Volume, 1e-6);
        cut.Suppressed = true;
        Assert.Equal(partVolume, asm.BodiesOf(upper)[0].Shape.MassProperties.Volume, 1e-6);
    }
}
