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

        // Collision checks for the moving component alone; touching faces don't collide.
        var collision = Assert.Single(AssemblyTools.CollisionsWith(asm, b));
        Assert.Same(a, collision.B);
        b.Transform = Transform3.Translation(new Vec3(0, 0, 10));
        Assert.Empty(AssemblyTools.CollisionsWith(asm, b));
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
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a scanner may still hold a file just written.
            }
        }
    }

    [Fact]
    public void InContextGasketFollowsItsSource()
    {
        var asm = new AssemblyDocument();
        asm.RegisterPart("base.chprt", Block(40, 20, 10, holeRadius: 5));
        asm.RegisterPart("gasket.chprt", new PartDocument());
        var baseComp = asm.AddComponent("base.chprt");
        var gasket = asm.AddComponent("gasket.chprt", Transform3.Translation(new Vec3(100, 0, 50)));
        var top = FaceWhere(asm.PartOf(baseComp), f => f.Normal.Z > 0.99);

        // Sketch the base's top face outline in the gasket part, and extrude it.
        var (plane, sketch) = InContext.SketchOnFace(asm, gasket, baseComp, top);
        var part = asm.PartOf(gasket);
        part.Add(new ExtrudeFeature { SketchId = sketch.Id, Depth = 2 });
        part.Rebuild();
        Assert.All(part.Features, f => Assert.Equal(FeatureStatus.Ok, part.Results[f.Id].Status));
        Assert.Equal(((40 * 20) - (Math.PI * 25)) * 2, part.Bodies[0].Shape.MassProperties.Volume, 1e-3);
        Box3 Placed() => asm.PlacedBodies(gasket).Single().Placed.Bounds;
        Assert.Equal(0, Placed().Min.X, 1e-6);
        Assert.Equal(40, Placed().Max.X, 1e-6);
        Assert.Equal(10, Placed().Min.Z, 1e-6); // sits on the base's top face, wherever the gasket component is

        // The source moves: the gasket follows on update.
        Assert.Empty(InContext.Update(asm));
        baseComp.Transform = Transform3.Translation(new Vec3(5, 0, 0));
        Assert.Same(gasket, Assert.Single(InContext.Update(asm)));
        Assert.Equal(5, Placed().Min.X, 1e-6);

        // Locked references stop updating; the part file round-trips with its reference.
        InContext.SetLocked(plane, true);
        baseComp.Transform = Transform3.Translation(new Vec3(20, 0, 0));
        Assert.Empty(InContext.Update(asm));
        Assert.Equal(5, Placed().Min.X, 1e-6);
        var reloaded = PartDocument.Load(part.Save());
        var saved = Assert.Single(reloaded.Features.OfType<InContextPlaneFeature>());
        Assert.True(saved.Reference!.Locked);
        Assert.Equal(baseComp.Id, saved.Reference.SourceComponentId);

        InContext.Break(plane);
        Assert.Empty(InContext.References(asm));
        Assert.Throws<InvalidOperationException>(() => InContext.SketchOnFace(asm, gasket, gasket, top));
    }

    [Fact]
    public void HingeLeavesOneRotation()
    {
        var asm = new AssemblyDocument();
        var plate = Block(40, 40, 10, holeRadius: 5);
        var pin = Pin(5, 30);
        asm.RegisterPart("plate.chprt", plate);
        asm.RegisterPart("pin.chprt", pin);
        var p = asm.AddComponent("plate.chprt");
        p.Fixed = true;
        var q = asm.AddComponent("pin.chprt", Transform3.Translation(new Vec3(-30, 10, 40)) * Transform3.Rotation(Vec3.Zero, Vec3.UnitX, 0.2));
        var hinge = asm.AddMate(MateType.Hinge,
            new MateRef(p.Id, FaceWhere(plate, f => f.Surface == SurfaceKind.Cylinder)),
            new MateRef(q.Id, FaceWhere(pin, f => f.Surface == SurfaceKind.Cylinder)));
        hinge.C = new MateRef(p.Id, FaceWhere(plate, f => f.Normal.Z > 0.99));
        hinge.D = new MateRef(q.Id, FaceWhere(pin, f => f.Normal.Z < -0.99));
        var result = asm.Solve();
        Assert.True(result.Converged, $"residual {result.Residual}");
        Assert.Equal(1, result.DegreesOfFreedom);
        var axisPoint = q.Transform.Apply(Vec3.Zero);
        Assert.Equal(20, axisPoint.X, 1e-6);
        Assert.Equal(20, axisPoint.Y, 1e-6);
        Assert.Equal(10, axisPoint.Z, 1e-6); // standing on the plate's top face
    }

    [Fact]
    public void SlotKeepsThePinCentred()
    {
        var asm = new AssemblyDocument();
        var slotted = new PartDocument();
        var u = new Sketch();
        u.AddPolyline([new Vec2(0, 0), new Vec2(40, 0), new Vec2(40, 30), new Vec2(25, 30), new Vec2(25, 10), new Vec2(15, 10), new Vec2(15, 30), new Vec2(0, 30)], closed: true);
        var sf = slotted.Add(new SketchFeature { Sketch = u });
        slotted.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        slotted.Rebuild();
        var pin = Pin(4, 20);
        asm.RegisterPart("slotted.chprt", slotted);
        asm.RegisterPart("pin.chprt", pin);
        var s = asm.AddComponent("slotted.chprt");
        s.Fixed = true;
        var q = asm.AddComponent("pin.chprt", Transform3.Translation(new Vec3(17, 18, -5)));
        var wall1 = FaceWhere(slotted, f => f.Normal.X > 0.99 && Math.Abs(f.Centroid.X - 15) < 1e-6);
        var wall2 = FaceWhere(slotted, f => f.Normal.X < -0.99 && Math.Abs(f.Centroid.X - 25) < 1e-6);
        var slot = asm.AddMate(MateType.Slot, new MateRef(q.Id, FaceWhere(pin, f => f.Surface == SurfaceKind.Cylinder)), new MateRef(s.Id, wall1));
        slot.C = new MateRef(s.Id, wall2);
        var result = asm.Solve();
        Assert.True(result.Converged, $"residual {result.Residual}");
        Assert.Equal(20, q.Transform.Apply(Vec3.Zero).X, 1e-6);
        Assert.Equal(4, result.DegreesOfFreedom); // slides along and out of the slot, tilts in it, turns
    }

    [Fact]
    public void LinearCouplerMovesTheOtherPart()
    {
        var asm = new AssemblyDocument();
        var block = Block(10, 10, 10);
        asm.RegisterPart("block.chprt", block);
        var a = asm.AddComponent("block.chprt");
        var b = asm.AddComponent("block.chprt", Transform3.Translation(new Vec3(50, 0, 0)));
        asm.AddMate(MateType.LinearCoupler,
            new MateRef(a.Id, FaceWhere(block, f => f.Normal.X > 0.99)),
            new MateRef(b.Id, FaceWhere(block, f => f.Normal.Y > 0.99)), value: 2);
        a.Transform = Transform3.Translation(new Vec3(10, 0, 0));
        a.Fixed = true;
        var result = asm.Solve();
        Assert.True(result.Converged, $"residual {result.Residual}");
        Assert.Equal(20, b.Transform.Ty, 1e-6);
    }

    [Fact]
    public void UniversalJointFollowsTheCardanRelation()
    {
        var asm = new AssemblyDocument();
        var shaft = Pin(3, 40);
        asm.RegisterPart("shaft.chprt", shaft);
        var tilt = Transform3.Rotation(Vec3.Zero, Vec3.UnitX, Math.PI / 6); // 30 degrees between the shafts
        var groundA = asm.AddComponent("shaft.chprt");
        var groundB = asm.AddComponent("shaft.chprt", tilt);
        groundA.Fixed = true;
        groundB.Fixed = true;
        var a = asm.AddComponent("shaft.chprt");
        var b = asm.AddComponent("shaft.chprt", tilt);
        var cyl = FaceWhere(shaft, f => f.Surface == SurfaceKind.Cylinder);
        asm.AddMate(MateType.Concentric, new MateRef(groundA.Id, cyl), new MateRef(a.Id, cyl));
        asm.AddMate(MateType.Concentric, new MateRef(groundB.Id, cyl), new MateRef(b.Id, cyl));
        asm.AddMate(MateType.UniversalJoint, new MateRef(a.Id, cyl), new MateRef(b.Id, cyl));
        var b0 = b.Transform;

        // Turn shaft A by 0.6 rad and hold it there.
        a.Transform = Transform3.Rotation(Vec3.Zero, Vec3.UnitZ, 0.6);
        a.Fixed = true;
        var result = asm.Solve();
        Assert.True(result.Converged, $"residual {result.Residual}");

        var axis = tilt.ApplyVector(Vec3.UnitZ);
        var ref0 = axis.AnyPerpendicular();
        var turned = (b.Transform * AssemblyDocument.Inverse(b0)).ApplyVector(ref0);
        turned -= axis * Vec3.Dot(turned, axis);
        var thetaB = Math.Atan2(Vec3.Dot(Vec3.Cross(ref0, turned), axis), Vec3.Dot(ref0, turned));
        Assert.Equal(Math.Atan(Math.Tan(0.6) * Math.Cos(Math.PI / 6)), Math.Abs(thetaB), 1e-4);
    }

    [Fact]
    public void SubassembliesActAsOneRigidComponent()
    {
        // A hinge subassembly: a plate with a pin standing in its hole.
        var sub = new AssemblyDocument();
        var plate = Block(40, 40, 10, holeRadius: 5);
        var pin = Pin(5, 30);
        sub.RegisterPart("plate.chprt", plate);
        sub.RegisterPart("pin.chprt", pin);
        var p = sub.AddComponent("plate.chprt");
        p.Fixed = true;
        var q = sub.AddComponent("pin.chprt", Transform3.Translation(new Vec3(60, 0, 0)));
        var hinge = sub.AddMate(MateType.Hinge,
            new MateRef(p.Id, FaceWhere(plate, f => f.Surface == SurfaceKind.Cylinder)),
            new MateRef(q.Id, FaceWhere(pin, f => f.Surface == SurfaceKind.Cylinder)));
        hinge.C = new MateRef(p.Id, FaceWhere(plate, f => f.Normal.Z > 0.99));
        hinge.D = new MateRef(q.Id, FaceWhere(pin, f => f.Normal.Z < -0.99));

        // The top assembly: a base, and the subassembly sitting on it by the plate's bottom face.
        var top = new AssemblyDocument();
        var baseBlock = Block(100, 100, 20);
        top.RegisterPart("base.chprt", baseBlock);
        top.RegisterSubAssembly("hinge.chasm", sub);
        var b = top.AddComponent("base.chprt");
        b.Fixed = true;
        var h = top.AddComponent("hinge.chasm", Transform3.Translation(new Vec3(10, 10, 50)));
        var standIn = top.PartOf(h);
        Assert.Equal(2, standIn.Bodies.Count);
        Assert.Equal(plate.MassProperties().Mass + pin.MassProperties().Mass, standIn.MassProperties().Mass, 1e-9);
        Assert.Same(sub, top.SubAssemblyOf(h));

        var plateBottom = FaceWhere(standIn, f => f.Normal.Z < -0.99);
        Assert.StartsWith(p.Id + "/", plateBottom.Name, StringComparison.Ordinal);
        top.AddMate(MateType.Coincident, new MateRef(b.Id, FaceWhere(baseBlock, f => f.Normal.Z > 0.99)), new MateRef(h.Id, plateBottom));
        Assert.True(top.Solve().Converged);
        Assert.Equal(20, top.PlacedBodies(h).Min(x => x.Placed.Bounds.Min.Z), 1e-6);

        // One BOM line for the subassembly; Pack and Go copies everything, nested.
        var bom = AssemblyTools.BillOfMaterials(top);
        Assert.Equal(2, bom.Count);
        Assert.Contains(bom, l => l.PartName == "hinge");
        var dir = Directory.CreateTempSubdirectory("chamfer-sub-");
        try
        {
            var packed = PackAndGo.Run(top, "Top", dir.FullName);
            Assert.Equal(5, packed.Files.Count); // top, hinge, base, plate, pin
            var reopened = AssemblyDocument.Load(File.ReadAllText(packed.AssemblyPath), dir.FullName);
            var hingeAgain = reopened.Components.Single(c => SubAssemblies.IsAssemblyPath(c.Path));
            Assert.Equal(2, reopened.PartOf(hingeAgain).Bodies.Count);

            // An assembly that contains itself is refused.
            var loop = new AssemblyDocument();
            loop.Components.Add(new Component { Id = "c1", Name = "loop<1>", Path = "loop.chasm" });
            File.WriteAllText(Path.Combine(dir.FullName, "loop.chasm"), loop.Save());
            var outer = new AssemblyDocument { Directory = dir.FullName };
            var looped = outer.AddComponent("loop.chasm");
            Assert.Throws<InvalidOperationException>(() => outer.PartOf(looped));
        }
        finally
        {
            try
            {
                dir.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a scanner may still hold a file just written.
            }
        }
    }

    [Fact]
    public void MirrorComponentsAndCheckClearance()
    {
        var asm = new AssemblyDocument();
        var bracket = new PartDocument();
        var l = new Sketch();
        l.AddPolyline([new Vec2(0, 0), new Vec2(30, 0), new Vec2(30, 5), new Vec2(5, 5), new Vec2(5, 20), new Vec2(0, 20)], closed: true);
        var sf = bracket.Add(new SketchFeature { Sketch = l });
        bracket.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 8 });
        bracket.Rebuild();
        asm.RegisterPart("bracket.chprt", bracket);
        var seed = asm.AddComponent("bracket.chprt", Transform3.Translation(new Vec3(10, 3, 2)) * Transform3.Rotation(Vec3.Zero, Vec3.UnitZ, 0.3));

        Shape Placed(Component c) => asm.PlacedBodies(c).Single().Placed;
        double Overlap(Shape a, Shape b)
        {
            using var common = Modeling.Boolean(a, b, BooleanKind.Common);
            using var r = common.Result;
            return r.MassProperties.Volume;
        }

        // Opposite hand: exactly the mirror image across the YZ plane.
        var volume = Placed(seed).MassProperties.Volume;
        var mirrored = asm.MirrorComponent(seed, Vec3.Zero, Vec3.UnitX, oppositeHand: true, "bracket-mirror.chprt");
        Assert.True(mirrored.Transform.IsRigid);
        using (var image = Modeling.Mirror(Placed(seed), Vec3.Zero, Vec3.UnitX))
        {
            Assert.Equal(volume, Overlap(image.Result, Placed(mirrored)), 1e-3);
        }

        // A symmetric block turned over lands on its mirror image too, with the same part.
        asm.RegisterPart("block.chprt", Block(20, 10, 6));
        var block = asm.AddComponent("block.chprt", Transform3.Translation(new Vec3(0, 40, 0)) * Transform3.Rotation(Vec3.Zero, Vec3.UnitY, 0.4));
        var turned = asm.MirrorComponent(block, new Vec3(0, 0, 30), Vec3.UnitZ, oppositeHand: false, "unused.chprt");
        Assert.Equal("block.chprt", turned.Path);
        using (var image = Modeling.Mirror(Placed(block), new Vec3(0, 0, 30), Vec3.UnitZ))
        {
            Assert.Equal(20 * 10 * 6, Overlap(image.Result, Placed(turned)), 1e-3);
        }

        // Clearance: the brackets sit either side of the mirror plane, within 25 mm of each other.
        var close = AssemblyTools.Clearances(asm, 25);
        var pair = Assert.Single(close, c => c.A == seed && c.B == mirrored);
        Assert.Equal(Vec3.Distance(pair.OnA, pair.OnB), pair.Distance, 1e-9);
        Assert.True(pair.Distance > 0);
        Assert.DoesNotContain(AssemblyTools.Clearances(asm, 0.001), c => c.A == seed && c.B == mirrored);
    }

    [Fact]
    public void PathAndCamMates()
    {
        var asm = new AssemblyDocument();
        var rail = Pin(30, 5); // its top circle (radius 30, at z = 5) is the path
        var pin = Pin(3, 20);
        asm.RegisterPart("rail.chprt", rail);
        asm.RegisterPart("pin.chprt", pin);
        var r = asm.AddComponent("rail.chprt");
        r.Fixed = true;
        var p = asm.AddComponent("pin.chprt", Transform3.Translation(new Vec3(12, 40, 30)));
        var railBody = rail.Bodies[0];
        var circle = railBody.EdgeRef(Enumerable.Range(1, railBody.EdgeCount).First(i => railBody.Edge(i).Curve == CurveKind.Circle && railBody.Edge(i).CenterOrDirection.Z > 4));
        var pinBody = pin.Bodies[0];
        var bottomEdge = pinBody.EdgeRef(Enumerable.Range(1, pinBody.EdgeCount).First(i => pinBody.Edge(i).Curve == CurveKind.Circle && pinBody.Edge(i).CenterOrDirection.Z < 1));

        // Path: the pin's bottom circle centre rides on the rail's top edge.
        asm.AddMate(MateType.Path, new MateRef(r.Id, Edge: circle), new MateRef(p.Id, Edge: bottomEdge));
        var result = asm.Solve();
        Assert.True(result.Converged, $"residual {result.Residual}");
        var at = p.Transform.Apply(Vec3.Zero);
        Assert.Equal(30, Math.Sqrt((at.X * at.X) + (at.Y * at.Y)), 0.05);
        Assert.Equal(5, at.Z, 1e-3);

        // Cam: an eccentric disc (radius 20, centred 5 off its axis) and a 3 mm follower touching it.
        var cam = new AssemblyDocument();
        var disc = new PartDocument();
        var ds = new Sketch();
        ds.AddCircle(new Vec2(5, 0), 20);
        var dsf = disc.Add(new SketchFeature { Sketch = ds });
        disc.Add(new ExtrudeFeature { SketchId = dsf.Id, Depth = 10 });
        disc.Rebuild();
        cam.RegisterPart("disc.chprt", disc);
        cam.RegisterPart("pin.chprt", pin);
        var c = cam.AddComponent("disc.chprt");
        c.Fixed = true;
        var f = cam.AddComponent("pin.chprt", Transform3.Translation(new Vec3(40, 3, 0)));
        var profile = disc.Bodies[0].EdgeRef(Enumerable.Range(1, disc.Bodies[0].EdgeCount).First(i => disc.Bodies[0].Edge(i).Curve == CurveKind.Circle));
        cam.AddMate(MateType.Cam, new MateRef(c.Id, Edge: profile), new MateRef(f.Id, FaceWhere(pin, x => x.Surface == SurfaceKind.Cylinder)));
        Assert.True(cam.Solve().Converged);
        var axis = f.Transform.Apply(Vec3.Zero);
        Assert.Equal(23, Math.Sqrt(((axis.X - 5) * (axis.X - 5)) + (axis.Y * axis.Y)), 0.05);
    }

    [Fact]
    public void AssemblyHolesWithCounterboresAndCountersinks()
    {
        // Two stacked plates; an assembly hole of radius 3 through both, counterbored 10 wide and 4 deep from the top.
        var asm = new AssemblyDocument();
        asm.RegisterPart("plate.chprt", Block(40, 40, 10));
        var lower = asm.AddComponent("plate.chprt");
        var upper = asm.AddComponent("plate.chprt", Transform3.Translation(new Vec3(0, 0, 10)));
        var hole = new Sketch();
        hole.AddCircle(new Vec2(20, 20), 3);
        var cut = asm.AddCut(new AssemblyCut { PlaneId = "front", PlaneOffset = 20, Sketch = hole, ThroughAll = true, Reverse = true, HoleStyle = HoleType.Counterbore, CounterDiameter = 10, CounterboreDepth = 4 });
        double Volume(Component c) => asm.PlacedBodies(c).Sum(p => p.Placed.MassProperties.Volume);
        Assert.Equal(16000 - (Math.PI * 9 * 10) - (Math.PI * (25 - 9) * 4), Volume(upper), 1e-3); // the counterbore is in the upper plate
        Assert.Equal(16000 - (Math.PI * 9 * 10), Volume(lower), 1e-3);

        // A 90 degree countersink instead: a cone from radius 5 down to 3, 2 deep.
        cut.HoleStyle = HoleType.Countersink;
        var cone = Math.PI * 2 / 3 * ((25) + (15) + (9));
        Assert.Equal(16000 - (Math.PI * 9 * 10) - (cone - (Math.PI * 9 * 2)), Volume(upper), 1e-3);
    }
}
