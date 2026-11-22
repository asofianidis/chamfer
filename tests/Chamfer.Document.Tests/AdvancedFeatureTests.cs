using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class AdvancedFeatureTests
{
    private static void AssertOk(PartDocument doc)
    {
        foreach (var f in doc.Features)
        {
            var r = doc.Results[f.Id];
            Assert.True(r.Status is FeatureStatus.Ok, $"{f.Name}: {r.Status} {r.Message}");
        }
    }

    private static double Volume(PartDocument doc) => doc.MassProperties().Volume;

    [Fact]
    public void SweepCircleAlongLShapedPath()
    {
        var doc = new PartDocument();
        var path = new Sketch();
        path.AddLine(new Vec2(0, 0), new Vec2(0, 50));
        path.AddLine(new Vec2(0, 50), new Vec2(40, 50));
        var pathFeature = doc.Add(new SketchFeature { Plane = PlaneRef.Front, Sketch = path });

        // Profile on the Top plane (normal +Y), centred on the path start.
        var profile = new Sketch();
        profile.AddCircle(Vec2.Zero, 3);
        var profileFeature = doc.Add(new SketchFeature { Plane = PlaneRef.Top, Sketch = profile });
        doc.Add(new SweepFeature { ProfileSketchId = profileFeature.Id, PathSketchId = pathFeature.Id });
        doc.Rebuild();
        AssertOk(doc);
        // Two straight legs; the mitred corner changes the volume only slightly.
        Assert.InRange(Volume(doc), Math.PI * 9 * 85, Math.PI * 9 * 95);
    }

    [Fact]
    public void LoftBetweenSquareAndCircle()
    {
        var doc = new PartDocument();
        var bottom = new Sketch();
        bottom.AddCenterRectangle(Vec2.Zero, new Vec2(10, 10));
        var b = doc.Add(new SketchFeature { Plane = PlaneRef.Top, Sketch = bottom });
        var plane = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Top, Offset = 30 });
        var top = new Sketch();
        top.AddCircle(Vec2.Zero, 6);
        var t = doc.Add(new SketchFeature { Plane = new PlaneRef(plane.Id), Sketch = top });
        var loft = doc.Add(new LoftFeature { SectionSketchIds = [b.Id, t.Id] });
        doc.Rebuild();
        AssertOk(doc);
        var v = Volume(doc);
        Assert.InRange(v, Math.PI * 36 * 30, 400 * 30);
        Assert.Equal(30, doc.Bodies[0].Shape.Bounds.Size.Y, 1e-6);

        loft.Ruled = true;
        doc.Rebuild();
        AssertOk(doc);
    }

    [Fact]
    public void BoundaryLeavesItsProfileNormal()
    {
        var doc = new PartDocument();
        var bottom = new Sketch();
        bottom.AddCenterRectangle(Vec2.Zero, new Vec2(10, 10));
        var b = doc.Add(new SketchFeature { Plane = PlaneRef.Top, Sketch = bottom });
        var plane = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Top, Offset = 30 });
        var top = new Sketch();
        top.AddCenterRectangle(Vec2.Zero, new Vec2(5, 5));
        var t = doc.Add(new SketchFeature { Plane = new PlaneRef(plane.Id), Sketch = top });

        // Without constraints it's the straight frustum (20² to 10² over 30: 7000 mm³).
        var boundary = doc.Add(new BoundaryFeature { ProfileSketchIds = [b.Id, t.Id], StartTangency = BoundaryTangency.None, EndTangency = BoundaryTangency.None });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(7000, Volume(doc), 1);

        // Leaving the base normal keeps it wider for longer: more volume, still under the 20² prism.
        boundary.StartTangency = BoundaryTangency.NormalToProfile;
        doc.Rebuild();
        AssertOk(doc);
        var normal = Volume(doc);
        Assert.InRange(normal, 7300, 12000);
        Assert.Equal(30, doc.Bodies[0].Shape.Bounds.Size.Y, 1e-3);
        Assert.Equal(20, doc.Bodies[0].Shape.Bounds.Size.X, 0.25); // about as wide as the base (B-spline bounds are loose)

        // A longer tangent pulls harder.
        boundary.StartTangentLength = 2;
        doc.Rebuild();
        AssertOk(doc);
        Assert.True(Volume(doc) > normal);

        // As a surface: an open shell through the outlines, no volume.
        boundary.IsSurface = true;
        doc.Rebuild();
        AssertOk(doc);
        Assert.True(doc.Bodies[0].IsSurface);

        var reloaded = PartDocument.Load(doc.Save());
        Assert.IsType<BoundaryFeature>(reloaded.Features[^1]);
    }

    [Fact]
    public void FreeformPullsATopFaceIntoADome()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 40));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var top = body.FaceRef(Enumerable.Range(1, body.FaceCount).First(i => body.Face(i).Normal.Z > 0.99));
        var free = doc.Add(new FreeformFeature { Face = top });

        // Nothing moved: unchanged, with a warning.
        doc.Rebuild();
        Assert.Equal(16000, Volume(doc), 1e-6);

        // The middle control point pulled out 5 mm: a smooth bump, still on the same edges.
        free.SetOffset(1, 1, 5);
        doc.Rebuild();
        AssertOk(doc);
        var bumped = Volume(doc);
        Assert.InRange(bumped, 16000 + 100, 16000 + (40 * 40 * 5));
        Assert.Equal(15, doc.Bodies[0].Shape.Bounds.Max.Z, 0.3);
        Assert.Equal(40, doc.Bodies[0].Shape.Bounds.Size.X, 1e-3);
        Assert.Equal(6, doc.Bodies[0].FaceCount);

        // Pushed in instead: a dent.
        free.SetOffset(1, 1, -4);
        doc.Rebuild();
        AssertOk(doc);
        Assert.InRange(Volume(doc), 16000 - (40 * 40 * 4), 16000 - 100);

        // A side face keeps its name, so features placed on it afterwards still find it.
        var side = body.FaceRef(Enumerable.Range(1, body.FaceCount).First(i => body.Face(i).Normal.X > 0.99));
        var boss = new Sketch();
        boss.AddCircle(new Vec2(0, 0), 2);
        var onSide = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: side), Sketch = boss });
        doc.Add(new ExtrudeFeature { SketchId = onSide.Id, Depth = 3 });
        doc.Rebuild();
        AssertOk(doc);

        var reloaded = PartDocument.Load(doc.Save());
        Assert.Equal(-4, reloaded.Features.OfType<FreeformFeature>().Single().OffsetAt(1, 1));
    }

    [Fact]
    public void VariableRadiusFilletTapers()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var edge = Enumerable.Range(1, body.EdgeCount).First(i =>
        {
            var e = body.Edge(i);
            return Math.Abs(e.Start.Z - 20) < 1e-6 && Math.Abs(e.End.Z - 20) < 1e-6 && Math.Abs(e.Start.Y - e.End.Y) < 1e-6 && Math.Abs(e.Start.Y) < 1e-6;
        });
        var fillet = doc.Add(new FilletFeature { Edges = [body.EdgeRef(edge)], Radius = 2 });
        doc.Rebuild();
        AssertOk(doc);
        var constant = 16000 - Volume(doc); // (1 - pi/4) r^2 L
        Assert.Equal((1 - (Math.PI / 4)) * 4 * 40, constant, 1e-3);

        // 2 to 6 along the edge removes more, but less than a constant 6.
        fillet.EndRadius = 6;
        doc.Rebuild();
        AssertOk(doc);
        var variable = 16000 - Volume(doc);
        Assert.InRange(variable, constant * 2, (1 - (Math.PI / 4)) * 36 * 40);
        Assert.Equal(6, PartDocument.Load(doc.Save()).Features.OfType<FilletFeature>().Single().EndRadius);
    }

    [Fact]
    public void PipeTapHolesTaperAndInchSizesConvert()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 40));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 30 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        var (u, v) = body.Shape.FacePlane(top)!.Value.ToLocal(new Vec3(20, 20, 30));
        var pts = new Sketch();
        pts.AddPoint(new Vec2(u, v));
        var ps = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = pts });
        var size = HoleStandards.Find("1/4 NPT")!;
        Assert.Equal(0.4375 * 25.4, size.TapDrill, 1e-9);
        Assert.Equal(0.540 * 25.4, size.ThreadDiameter, 1e-9);
        doc.Add(new HoleFeature
        {
            SketchId = ps.Id, Standard = HoleStandards.PipeTap, Size = size.Name, Type = HoleType.PipeTap,
            Diameter = size.TapDrill, Depth = 20, DrillPointAngle = 0,
        });
        doc.Rebuild();
        AssertOk(doc);

        // A 1:16 taper on diameter: a frustum from the tap drill down to 20/16 mm narrower.
        var r1 = size.TapDrill / 2;
        var r2 = r1 - (20.0 / 32);
        var frustum = Math.PI * 20 / 3 * ((r1 * r1) + (r1 * r2) + (r2 * r2));
        Assert.Equal((40 * 40 * 30) - frustum, Volume(doc), 1e-2);

        // Inch screw sizes and the 82 degree countersink.
        Assert.Equal(0.266 * 25.4, HoleStandards.Find("1/4-20")!.Clearance, 1e-9);
        Assert.Equal(0.25 * 25.4, HoleStandards.Find("1/4-20")!.ThreadDiameter, 1e-9);
        Assert.Equal(6, HoleStandards.Find("M6")!.ThreadDiameter);
        Assert.Equal(82, HoleStandards.CountersinkAngle(HoleStandards.AnsiInch));
    }

    [Fact]
    public void RibFillsTheCornerOfAnLBracket()
    {
        var doc = new PartDocument();
        var l = new Sketch();
        l.AddPolyline([new Vec2(0, 0), new Vec2(50, 0), new Vec2(50, 5), new Vec2(5, 5), new Vec2(5, 50), new Vec2(0, 50)], closed: true);
        var lf = doc.Add(new SketchFeature { Sketch = l });
        doc.Add(new ExtrudeFeature { SketchId = lf.Id, Depth = 20, EndCondition = EndCondition.MidPlane });
        doc.Rebuild();
        var before = Volume(doc);

        var rib = new Sketch();
        rib.AddLine(new Vec2(5, 35), new Vec2(35, 5));
        var rf = doc.Add(new SketchFeature { Sketch = rib });
        doc.Add(new RibFeature { SketchId = rf.Id, Thickness = 3 });
        doc.Rebuild();
        AssertOk(doc);
        // The triangle between the diagonal and the walls is 30 × 30 / 2, three wide.
        Assert.Equal(before + (30 * 30 / 2.0 * 3), Volume(doc), 1);
    }

    [Fact]
    public void ExtrudeUpToSurfaceAndOffset()
    {
        var doc = new PartDocument();
        // A tall block, then a boss sketched on the Front plane extruded up to the block's +Z face from below.
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 30 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);

        var hole = new Sketch();
        hole.AddCircle(new Vec2(20, 10), 4);
        var hf = doc.Add(new SketchFeature { Plane = PlaneRef.Front, Sketch = hole });
        var pocket = doc.Add(new ExtrudeFeature
        {
            SketchId = hf.Id,
            IsCut = true,
            Reverse = true,
            EndCondition = EndCondition.OffsetFromSurface,
            UpToFace = body.FaceRef(top),
            SurfaceOffset = 5,
        });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal((40 * 20 * 30) - (Math.PI * 16 * 25), Volume(doc), 1e-6);

        pocket.EndCondition = EndCondition.UpToSurface;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal((40 * 20 * 30) - (Math.PI * 16 * 30), Volume(doc), 1e-6);
    }

    [Fact]
    public void UpToInclinedSurfaceClipsTheExtrusion()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 40 });
        doc.Rebuild();
        // Chamfer the top so there's an inclined face, then extrude a boss up to it from the bottom.
        var body = doc.Bodies[0];
        var edge = Enumerable.Range(1, body.EdgeCount).Single(i => Vec3.Distance(body.Edge(i).Midpoint, new Vec3(40, 10, 40)) < 1e-6);
        doc.Add(new ChamferFeature { Edges = [body.EdgeRef(edge)], Distance = 20 });
        doc.Rebuild();
        body = doc.Bodies[0];
        var inclined = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.X > 0.5 && body.Face(i).Normal.Z > 0.5);

        var post = new Sketch();
        post.AddCircle(new Vec2(50, 10), 3);
        var pf = doc.Add(new SketchFeature { Sketch = post });
        doc.Add(new ExtrudeFeature { SketchId = pf.Id, EndCondition = EndCondition.UpToSurface, UpToFace = body.FaceRef(inclined), Merge = false });
        doc.Rebuild();
        AssertOk(doc);
        // The post stops where the extended inclined plane (x + z = 60) crosses it: z = 13 at x = 47.
        var postBody = doc.Bodies.Single(b => b.Shape.Bounds.Min.X > 45);
        Assert.Equal(13, postBody.Shape.Bounds.Max.Z, 0.05);
        Assert.Equal(7, postBody.Shape.Bounds.Max.Z - (2 * 3), 0.05);
    }

    [Fact]
    public void TaperedAndThinExtrusions()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCenterRectangle(Vec2.Zero, new Vec2(20, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        var ex = doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10, DraftAngle = 10 });
        doc.Rebuild();
        AssertOk(doc);
        var tapered = Volume(doc);
        Assert.True(tapered < 40 * 40 * 10);
        Assert.True(tapered > 30 * 30 * 10);

        ex.DraftAngle = 0;
        ex.IsThin = true;
        ex.ThinThickness = 2;
        doc.Rebuild();
        AssertOk(doc);
        // A square tube: 40×40 outside... thin walls go inward for closed profiles.
        Assert.Equal(((40 * 40) - (36 * 36)) * 10, Volume(doc), 1e-6);

        ex.ThinReverse = true;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(((44 * 44) - (40 * 40)) * 10, Volume(doc), 1e-6);
    }

    [Fact]
    public void ThinOpenProfileMakesAWall()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddPolyline([new Vec2(0, 0), new Vec2(30, 0), new Vec2(30, 20)]);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10, IsThin = true, ThinThickness = 2 });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(((30 * 2) + (20 * 2) - 4) * 10, Volume(doc), 1);
    }
}
