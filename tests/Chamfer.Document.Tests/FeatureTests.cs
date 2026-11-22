using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;

namespace Chamfer.Document.Tests;

public class FeatureTests
{
    private const double Tol = 1e-6;

    private static (PartDocument Doc, SketchFeature Sketch, ExtrudeFeature Extrude, DistanceConstraint Width) Block(double w = 40, double h = 20, double depth = 10)
    {
        var doc = new PartDocument();
        var sketch = new Sketch();
        var lines = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(w, h));
        sketch.AddConstraint(new FixConstraint(lines[0].Start));
        var width = sketch.AddConstraint(new DistanceConstraint(lines[0], w));
        sketch.AddConstraint(new DistanceConstraint(lines[1], h));
        Assert.True(sketch.Solve().Converged);
        var sf = doc.Add(new SketchFeature { Sketch = sketch });
        var ex = doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = depth });
        doc.Rebuild();
        return (doc, sf, ex, width);
    }

    private static void AssertOk(PartDocument doc)
    {
        foreach (var f in doc.Features)
        {
            var r = doc.Results[f.Id];
            Assert.True(r.Status is FeatureStatus.Ok, $"{f.Name}: {r.Status} {r.Message}");
        }
    }

    private static double Volume(PartDocument doc) => doc.MassProperties().Volume;

    private static int FaceWhere(Body body, Func<FaceInfo, bool> predicate) =>
        Enumerable.Range(1, body.FaceCount).Single(i => predicate(body.Face(i)));

    private static int EdgeWhere(Body body, Func<EdgeInfo, bool> predicate) =>
        Enumerable.Range(1, body.EdgeCount).Single(i => predicate(body.Edge(i)));

    [Fact]
    public void ExtrudedBlockHasNamedFaces()
    {
        var (doc, sketch, ex, _) = Block();
        AssertOk(doc);
        var body = Assert.Single(doc.Bodies);
        Assert.Equal(8000, Volume(doc), Tol);
        Assert.Equal(6, body.FaceCount);
        Assert.Contains($"{ex.Id}:end:{sketch.Sketch.Entities.Min(e => e.Id)}", body.FaceNames);
        Assert.Equal(4, body.FaceNames.Count(n => n.Contains(":side:", StringComparison.Ordinal)));
        Assert.Equal(12, body.EdgeNames.Distinct().Count());
        var mass = doc.MassProperties();
        Assert.Equal(8000e-9 * 7800, mass.Mass, 1e-9);
        Assert.True(Vec3.Distance(new Vec3(20, 10, 5), mass.CenterOfMass) < 1e-9);
    }

    [Fact]
    public void ExtrudeEndConditions()
    {
        var (doc, _, ex, _) = Block();
        ex.EndCondition = EndCondition.MidPlane;
        doc.Rebuild();
        Assert.Equal(-5, doc.Bodies[0].Shape.Bounds.Min.Z, Tol);

        ex.EndCondition = EndCondition.Blind;
        ex.HasDirection2 = true;
        ex.Depth2 = 4;
        doc.Rebuild();
        Assert.Equal(-4, doc.Bodies[0].Shape.Bounds.Min.Z, Tol);
        Assert.Equal(10, doc.Bodies[0].Shape.Bounds.Max.Z, Tol);

        ex.HasDirection2 = false;
        ex.Reverse = true;
        doc.Rebuild();
        Assert.Equal(-10, doc.Bodies[0].Shape.Bounds.Min.Z, Tol);
        AssertOk(doc);
    }

    [Fact]
    public void ThroughAllCutOnTopFace()
    {
        var (doc, _, _, _) = Block();
        var body = doc.Bodies[0];
        var top = FaceWhere(body, f => f.Normal.Z > 0.99);
        var hole = new Sketch();
        hole.AddCircle(new Vec2(20, 10), 5);
        var sf = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = hole });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, IsCut = true, EndCondition = EndCondition.ThroughAll });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(8000 - (Math.PI * 25 * 10), Volume(doc), 1e-6);
        Assert.Equal(7, doc.Bodies[0].FaceCount);
    }

    [Fact]
    public void BossOnFaceMergesIntoBody()
    {
        var (doc, _, _, _) = Block();
        var body = doc.Bodies[0];
        var top = FaceWhere(body, f => f.Normal.Z > 0.99);
        var boss = new Sketch();
        boss.AddCircle(new Vec2(20, 10), 5);
        var sf = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = boss });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 6 });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Single(doc.Bodies);
        Assert.Equal(8000 + (Math.PI * 25 * 6), Volume(doc), 1e-6);
        Assert.Equal(16, doc.Bodies[0].Shape.Bounds.Max.Z, Tol);
    }

    [Fact]
    public void RevolveAboutCenterline()
    {
        var doc = new PartDocument();
        var sketch = new Sketch();
        sketch.AddLine(new Vec2(0, -20), new Vec2(0, 20), construction: true);
        sketch.AddCornerRectangle(new Vec2(10, 0), new Vec2(20, 10));
        var sf = doc.Add(new SketchFeature { Sketch = sketch });
        var rev = doc.Add(new RevolveFeature { SketchId = sf.Id });
        doc.Rebuild();
        AssertOk(doc);
        // A washer: outer radius 20, inner radius 10, height 10.
        Assert.Equal(Math.PI * (400 - 100) * 10, Volume(doc), 1e-6);

        rev.Angle = 90;
        doc.Rebuild();
        Assert.Equal(Math.PI * (400 - 100) * 10 / 4, Volume(doc), 1e-6);
    }

    [Fact]
    public void FilletFollowsItsEdgeThroughUpstreamEdits()
    {
        var (doc, _, _, width) = Block();
        var body = doc.Bodies[0];
        // The vertical edge at x = 40, y = 0.
        var edge = EdgeWhere(body, e => e.Curve == CurveKind.Line && Math.Abs(e.CenterOrDirection.Z) > 0.99 &&
            Vec3.Distance(e.Midpoint, new Vec3(40, 0, 5)) < 1e-6);
        var fillet = doc.Add(new FilletFeature { Edges = [body.EdgeRef(edge)], Radius = 3 });
        doc.Rebuild();
        AssertOk(doc);
        var removed = (9 - (Math.PI * 9 / 4)) * 10;
        Assert.Equal(8000 - removed, Volume(doc), 1e-4);

        // Widen the block: the same logical corner (now at x = 60) must stay filleted.
        width.Value = 60;
        Assert.True(doc.Features.OfType<SketchFeature>().First().Sketch.Solve().Converged);
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal((60 * 20 * 10) - removed, Volume(doc), 1e-4);
        var round = FaceWhere(doc.Bodies[0], f => f.Surface == SurfaceKind.Cylinder);
        Assert.Equal(60 - 3, doc.Bodies[0].Face(round).AxisOrigin.X, 1e-6);
        Assert.Contains(doc.Bodies[0].FaceNames, n => n.StartsWith($"{fillet.Id}:fillet(", StringComparison.Ordinal));
    }

    [Fact]
    public void ChamferShellAndDraft()
    {
        var (doc, _, _, _) = Block();
        var body = doc.Bodies[0];
        var edge = EdgeWhere(body, e => Vec3.Distance(e.Midpoint, new Vec3(40, 10, 10)) < 1e-6);
        doc.Add(new ChamferFeature { Edges = [body.EdgeRef(edge)], Distance = 2 });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(8000 - (2 * 20), Volume(doc), 1e-6);

        var (doc2, _, _, _) = Block();
        var b2 = doc2.Bodies[0];
        var top = FaceWhere(b2, f => f.Normal.Z > 0.99);
        doc2.Add(new ShellFeature { FacesToRemove = [b2.FaceRef(top)], Thickness = 1 });
        doc2.Rebuild();
        AssertOk(doc2);
        Assert.Equal(8000 - (38 * 18 * 9), Volume(doc2), 1e-4);

        var (doc3, _, _, _) = Block();
        var b3 = doc3.Bodies[0];
        var side = FaceWhere(b3, f => f.Normal.X > 0.99);
        doc3.Add(new DraftFeature { Faces = [b3.FaceRef(side)], NeutralPlane = PlaneRef.Front, Angle = 5 });
        doc3.Rebuild();
        AssertOk(doc3);
        Assert.NotEqual(8000, Volume(doc3), 1e-3);
    }

    [Fact]
    public void CounterboreHolesInLinearAndCircularPatterns()
    {
        var (doc, _, _, _) = Block(80, 80, 20);
        var body = doc.Bodies[0];
        var top = FaceWhere(body, f => f.Normal.Z > 0.99);
        var points = new Sketch();
        var topPlane = body.Shape.FacePlane(top)!.Value;
        var (u, v) = topPlane.ToLocal(new Vec3(10, 10, 20));
        points.AddPoint(new Vec2(u, v));
        var ps = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = points });
        var hole = doc.Add(new HoleFeature
        {
            SketchId = ps.Id,
            Type = HoleType.Counterbore,
            Diameter = 6.6,
            CounterboreDiameter = 11,
            CounterboreDepth = 6.5,
            EndCondition = EndCondition.ThroughAll,
        });
        doc.Rebuild();
        AssertOk(doc);
        var one = (Math.PI * 3.3 * 3.3 * 20) + (Math.PI * ((5.5 * 5.5) - (3.3 * 3.3)) * 6.5);
        Assert.Equal((80 * 80 * 20) - one, Volume(doc), 1e-4);

        doc.Add(new LinearPatternFeature { Seeds = [hole.Id], Direction = AxisRef.X, Spacing = 20, Count = 3, Direction2 = AxisRef.Y, Spacing2 = 60, Count2 = 2 });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal((80 * 80 * 20) - (6 * one), Volume(doc), 1e-3);

        // Circular pattern about the Z axis of a block centred on the origin.
        var doc2 = new PartDocument();
        var square = new Sketch();
        square.AddCenterRectangle(Vec2.Zero, new Vec2(40, 40));
        var sq = doc2.Add(new SketchFeature { Sketch = square });
        doc2.Add(new ExtrudeFeature { SketchId = sq.Id, Depth = 20 });
        doc2.Rebuild();
        var b2 = doc2.Bodies[0];
        var t2 = FaceWhere(b2, f => f.Normal.Z > 0.99);
        var pts2 = new Sketch();
        var (u2, v2) = b2.Shape.FacePlane(t2)!.Value.ToLocal(new Vec3(0, 30, 20));
        pts2.AddPoint(new Vec2(u2, v2));
        var ps2 = doc2.Add(new SketchFeature { Plane = new PlaneRef(Face: b2.FaceRef(t2)), Sketch = pts2 });
        var h2 = doc2.Add(new HoleFeature { SketchId = ps2.Id, Diameter = 5, EndCondition = EndCondition.ThroughAll });
        doc2.Add(new CircularPatternFeature { Seeds = [h2.Id], Axis = AxisRef.Z, Count = 4 });
        doc2.Rebuild();
        AssertOk(doc2);
        Assert.Equal((80 * 80 * 20) - (4 * Math.PI * 6.25 * 20), Volume(doc2), 1e-4);
    }

    [Fact]
    public void MirrorBoss()
    {
        var (doc, _, _, _) = Block();
        var s = new Sketch();
        s.AddCircle(new Vec2(-30, 10), 5);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        var boss = doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10, Merge = false });
        doc.Add(new MirrorFeature { Seeds = [boss.Id], Plane = PlaneRef.Right });
        doc.Rebuild();
        AssertOk(doc);
        // The mirrored cylinder lands at x = 30, inside the block, so it fuses without adding volume.
        Assert.Equal(8000 + (Math.PI * 25 * 10), Volume(doc), 1e-4);
    }

    [Fact]
    public void ErrorsAreReportedAndDownstreamContinues()
    {
        var (doc, _, _, _) = Block();
        var body = doc.Bodies[0];
        var edge = EdgeWhere(body, e => Vec3.Distance(e.Midpoint, new Vec3(40, 10, 10)) < 1e-6);
        var bad = doc.Add(new FilletFeature { Edges = [body.EdgeRef(edge)], Radius = 50 });
        doc.Add(new ChamferFeature { Edges = [body.EdgeRef(edge)], Distance = 1 });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Error, doc.Results[bad.Id].Status);
        Assert.Equal(FeatureStatus.Ok, doc.Results[doc.Features[^1].Id].Status);
        Assert.Equal(8000 - 0.5 * 20, Volume(doc), 1e-6);
    }

    [Fact]
    public void SuppressAndRollback()
    {
        var (doc, _, ex, _) = Block();
        var s = new Sketch();
        s.AddCircle(new Vec2(20, 10), 5);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        var cut = doc.Add(new ExtrudeFeature { SketchId = sf.Id, IsCut = true, EndCondition = EndCondition.ThroughAll, Reverse = true });
        doc.Rebuild();
        AssertOk(doc);
        var cutVolume = Volume(doc);
        Assert.True(cutVolume < 8000);

        cut.IsSuppressed = true;
        doc.Rebuild();
        Assert.Equal(8000, Volume(doc), Tol);
        Assert.Equal(FeatureStatus.Suppressed, doc.Results[cut.Id].Status);

        cut.IsSuppressed = false;
        doc.RollbackIndex = doc.Features.IndexOf(cut);
        doc.Rebuild();
        Assert.Equal(8000, Volume(doc), Tol);
        Assert.Equal(FeatureStatus.RolledBack, doc.Results[cut.Id].Status);

        doc.RollbackIndex = null;
        doc.Rebuild();
        Assert.Equal(cutVolume, Volume(doc), Tol);
        Assert.Same(ex, doc.Find(ex.Id));
    }

    [Fact]
    public void DeletingASketchDeletesItsFeatures()
    {
        var (doc, sketch, ex, _) = Block();
        var removed = doc.Delete(sketch);
        Assert.Contains(ex, removed);
        Assert.Empty(doc.Features);
        doc.Rebuild();
        Assert.Empty(doc.Bodies);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        var (doc, _, _, _) = Block();
        var body = doc.Bodies[0];
        var edge = EdgeWhere(body, e => Vec3.Distance(e.Midpoint, new Vec3(40, 10, 10)) < 1e-6);
        doc.Add(new FilletFeature { Edges = [body.EdgeRef(edge)], Radius = 2 });
        doc.Material = Material.Aluminum;
        doc.Rebuild();
        var expected = Volume(doc);

        var json = doc.Save();
        Assert.Contains("\"feature\": \"extrude\"", json, StringComparison.Ordinal);
        var loaded = PartDocument.Load(json);
        loaded.Rebuild();
        AssertOk(loaded);
        Assert.Equal(expected, Volume(loaded), 1e-9);
        Assert.Equal(Material.Aluminum, loaded.Material);
        Assert.Equal(doc.Features.Select(f => f.Name), loaded.Features.Select(f => f.Name));

        // New features keep getting fresh ids.
        var next = loaded.Add(new ReferencePlaneFeature());
        Assert.DoesNotContain(doc.Features, f => f.Id == next.Id);
    }

    [Fact]
    public void MomentsOfInertiaOfABlock()
    {
        var (doc, _, _, _) = Block(40, 20, 10);
        var m = doc.MassProperties();
        // Rectangular block about its centroid: Ixx = m (b² + c²) / 12, with b, c the other two sides.
        Assert.Equal(m.Mass * ((20 * 20) + (10 * 10)) / 12, m.Ixx, 1e-6);
        Assert.Equal(m.Mass * ((40 * 40) + (10 * 10)) / 12, m.Iyy, 1e-6);
        Assert.Equal(m.Mass * ((40 * 40) + (20 * 20)) / 12, m.Izz, 1e-6);
        Assert.Equal(0, m.Ixy, 1e-9);
    }

    [Fact]
    public void ReorderRespectsDependencies()
    {
        var (doc, sketch, ex, _) = Block();
        var plane = doc.Add(new ReferencePlaneFeature { Offset = 5 });
        Assert.False(doc.Move(ex, -1));       // can't move above its sketch
        Assert.False(doc.Move(sketch, 1));    // can't move below its user
        Assert.True(doc.Move(plane, -2));     // an unrelated plane can go first
        Assert.Same(plane, doc.Features[0]);
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(8000, Volume(doc), Tol);
    }

    [Fact]
    public void ExportAndImportStep()
    {
        var (doc, _, _, _) = Block();
        var dir = Directory.CreateTempSubdirectory("chamfer-doc-");
        try
        {
            var path = Path.Combine(dir.FullName, "block.step");
            doc.Export(path);
            var imported = new PartDocument();
            var feature = imported.Add(ImportFeature.FromFile(path));
            imported.Rebuild();
            AssertOk(imported);
            Assert.Equal(8000, Volume(imported), 1e-6);
            Assert.Equal("block.step", feature.SourceName);

            // Imported bodies take part in later features and survive a save/load.
            var body = imported.Bodies[0];
            var edge = EdgeWhere(body, e => Vec3.Distance(e.Midpoint, new Vec3(40, 10, 10)) < 1e-6);
            imported.Add(new ChamferFeature { Edges = [body.EdgeRef(edge)], Distance = 2 });
            var reloaded = PartDocument.Load(imported.Save());
            reloaded.Rebuild();
            AssertOk(reloaded);
            Assert.Equal(8000 - 40, Volume(reloaded), 1e-6);

            foreach (var ext in new[] { "igs", "stl", "brep" })
            {
                var other = Path.Combine(dir.FullName, $"block.{ext}");
                doc.Export(other);
                Assert.True(new FileInfo(other).Length > 100);
            }
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void IncrementalRebuildReusesUnchangedPrefix()
    {
        var (doc, _, _, _) = Block();
        var bodyBefore = doc.Bodies[0];
        var s = new Sketch();
        s.AddCircle(new Vec2(20, 10), 5);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        var cut = doc.Add(new ExtrudeFeature { SketchId = sf.Id, IsCut = true, Depth = 3, Reverse = true });
        doc.Rebuild();
        var stateAfterCut = doc.Bodies[0];
        cut.Depth = 4;
        doc.Rebuild();
        Assert.NotSame(stateAfterCut, doc.Bodies[0]);
        doc.RollbackIndex = 2;
        doc.Rebuild();
        Assert.Same(bodyBefore, doc.Bodies[0]);
    }
}
