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
    public void SweepWithTwistAndGuideCurve()
    {
        var doc = new PartDocument();
        var path = new Sketch();
        path.AddLine(new Vec2(0, 0), new Vec2(0, 50));
        var pathFeature = doc.Add(new SketchFeature { Plane = PlaneRef.Front, Sketch = path });
        var bar = new Sketch();
        bar.AddCenterRectangle(Vec2.Zero, new Vec2(10, 3)); // 20 x 6, across X
        var barFeature = doc.Add(new SketchFeature { Plane = PlaneRef.Top, Sketch = bar });
        var sweep = doc.Add(new SweepFeature { ProfileSketchId = barFeature.Id, PathSketchId = pathFeature.Id, TwistAngle = 90 });
        doc.Rebuild();
        AssertOk(doc);

        // A quarter turn: the same cross-section all along, ending 20 wide in Z instead of X.
        Assert.Equal(20 * 6 * 50, Volume(doc), 6000 * 0.01);
        var shape = doc.Bodies[0].Shape;
        var end = Enumerable.Range(1, shape.FaceCount).Single(i => shape.Face(i).Normal.Y > 0.99 && Math.Abs(shape.Face(i).Centroid.Y - 50) < 1e-3);
        var corners = shape.EdgesOfFace(end).SelectMany(e => shape.SampleEdge(e)).ToList();
        Assert.Equal(6, corners.Max(p => p.X) - corners.Min(p => p.X), 0.05);
        Assert.Equal(20, corners.Max(p => p.Z) - corners.Min(p => p.Z), 0.05);

        // A guide line from the profile's edge out to twice its radius: a cone frustum.
        var disc = new Sketch();
        disc.AddCircle(Vec2.Zero, 5);
        var discFeature = doc.Add(new SketchFeature { Plane = PlaneRef.Top, Sketch = disc });
        var guide = new Sketch();
        guide.AddLine(new Vec2(5, 0), new Vec2(10, 50));
        var guideFeature = doc.Add(new SketchFeature { Plane = PlaneRef.Front, Sketch = guide });
        doc.Delete(sweep);
        doc.Add(new SweepFeature { ProfileSketchId = discFeature.Id, PathSketchId = pathFeature.Id, GuideSketchId = guideFeature.Id });
        doc.Rebuild();
        AssertOk(doc);
        var frustum = Math.PI * 50 / 3 * (25 + 50 + 100);
        Assert.Equal(frustum, Volume(doc), frustum * 0.02);
        Assert.Equal(20, doc.Bodies[0].Shape.Bounds.Size.X, 0.3);
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

        // Leaving the square normal to it keeps the loft fuller near the base.
        loft.Ruled = false;
        doc.Rebuild();
        var plain = Volume(doc);
        loft.StartConstraint = BoundaryTangency.NormalToProfile;
        doc.Rebuild();
        AssertOk(doc);
        Assert.True(Volume(doc) > plain + 50, $"{Volume(doc)} vs {plain}");
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
    public void LoftedSurfaceThickenedIntoACut()
    {
        var doc = new PartDocument();
        var block = new Sketch();
        block.AddCornerRectangle(new Vec2(-30, -30), new Vec2(30, 30));
        var bf = doc.Add(new SketchFeature { Plane = PlaneRef.Top, Sketch = block });
        doc.Add(new ExtrudeFeature { SketchId = bf.Id, Depth = 40 });

        // A lofted cone surface from a circle at y = 10 up to a wider one at y = 30, inside the block.
        var lowPlane = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Top, Offset = 10 });
        var highPlane = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Top, Offset = 30 });
        var square = new Sketch();
        square.AddCircle(Vec2.Zero, 8);
        var circle = new Sketch();
        circle.AddCircle(Vec2.Zero, 12);
        var s1 = doc.Add(new SketchFeature { Plane = new PlaneRef(lowPlane.Id), Sketch = square });
        var s2 = doc.Add(new SketchFeature { Plane = new PlaneRef(highPlane.Id), Sketch = circle });
        var loft = doc.Add(new LoftFeature { SectionSketchIds = [s1.Id, s2.Id], IsSurface = true });
        doc.Rebuild();
        AssertOk(doc);
        var surface = Assert.Single(doc.Bodies, b => b.IsSurface);

        // Thickened into the block as a cut: a thin-walled slot, material removed.
        doc.Add(new ThickenFeature { Body = surface.Name, Thickness = 1, IsCut = true });
        doc.Rebuild();
        AssertOk(doc);
        Assert.DoesNotContain(doc.Bodies, b => b.IsSurface);
        var removed = (60 * 60 * 40) - Volume(doc);
        var wall = Math.PI * (8 + 12) * Math.Sqrt((20 * 20) + (4 * 4)); // the cone's side area
        Assert.Equal(wall, removed, wall * 0.05); // a 1 mm wall of it
    }

    [Fact]
    public void RuledSurfacesFromEdges()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var topEdges = Enumerable.Range(1, body.EdgeCount)
            .Where(i => Math.Abs(body.Edge(i).Start.Z - 10) < 1e-6 && Math.Abs(body.Edge(i).End.Z - 10) < 1e-6)
            .Select(body.EdgeRef).ToList();
        Assert.Equal(4, topEdges.Count);

        // Along +Z from the top edges: a 5 mm wall standing on the rim, 120 x 5 mm.
        var ruled = doc.Add(new RuledSurfaceFeature { Edges = topEdges, Distance = 5, Type = RuledDirection.AlongDirection, Direction = AxisRef.Z });
        doc.Rebuild();
        AssertOk(doc);
        var surface = Assert.Single(doc.Bodies, b => b.IsSurface);
        Assert.Equal(4, surface.FaceCount);
        Assert.Equal(120 * 5, surface.Shape.MassProperties.SurfaceArea, 1e-6);
        Assert.Equal(15, surface.Shape.Bounds.Max.Z, 1e-6);

        // Normal to a face beside each edge: still 120 x 5 of strip, whichever face it is.
        ruled.Type = RuledDirection.NormalToFace;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(120 * 5, doc.Bodies.Single(b => b.IsSurface).Shape.MassProperties.SurfaceArea, 1e-6);
    }

    [Fact]
    public void SplitLineDividesAFace()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var topIndex = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        var top = body.FaceRef(topIndex);
        var side = body.FaceRef(Enumerable.Range(1, body.FaceCount).First(i => body.Face(i).Normal.X > 0.99));

        // A circle on the top face splits it in two: a disc and the face around it.
        var circle = new Sketch();
        var (u, v) = body.Shape.FacePlane(topIndex)!.Value.ToLocal(new Vec3(20, 10, 10));
        circle.AddCircle(new Vec2(u, v), 5);
        var cf = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: top), Sketch = circle });
        var split = doc.Add(new SplitLineFeature { SketchId = cf.Id });
        doc.Rebuild();
        AssertOk(doc);
        var after = doc.Bodies[0];
        Assert.Equal(7, after.FaceCount);
        Assert.Equal(40 * 20 * 10, Volume(doc), 1e-6); // the solid is unchanged
        var tops = Enumerable.Range(1, after.FaceCount).Where(i => after.Face(i).Normal.Z > 0.99).Select(i => after.Face(i).Area).OrderBy(a => a).ToList();
        Assert.Equal(Math.PI * 25, tops[0], 1e-3);
        Assert.Equal((40 * 20) - (Math.PI * 25), tops[1], 1e-3);

        // The other faces keep their names, so later features still find them.
        Assert.True(after.HasFace(side.Name));

        // Through all also splits the bottom face.
        split.ThroughAll = true;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(8, doc.Bodies[0].FaceCount);
    }

    [Fact]
    public void ProjectedAndCompositeCurvesBecomeSweepPaths()
    {
        // A cylinder of radius 20 along Z; a line in a sketch on a plane beside it, projected onto its side.
        var doc = new PartDocument();
        var disc = new Sketch();
        disc.AddCircle(Vec2.Zero, 20);
        var df = doc.Add(new SketchFeature { Sketch = disc });
        doc.Add(new ExtrudeFeature { SketchId = df.Id, Depth = 40 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var side = body.FaceRef(Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Surface == Chamfer.Kernel.Occt.SurfaceKind.Cylinder));
        var beside = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Right, Offset = 30 }); // x = 30, normal +X
        var line = new Sketch();
        line.AddLine(new Vec2(-5, -10), new Vec2(-35, 10)); // (u, v) = (-z, y): a slanted line
        var lf = doc.Add(new SketchFeature { Plane = new PlaneRef(beside.Id), Sketch = line });
        var projected = doc.Add(new ProjectedCurveFeature { SketchId = lf.Id, Face = side, Reverse = true });
        doc.Rebuild();
        AssertOk(doc);
        var curve = doc.State.Curves[projected.Id];
        var pts = Enumerable.Range(1, curve.EdgeCount).SelectMany(e => curve.SampleEdge(e)).ToList();
        Assert.All(pts, p => Assert.Equal(20, Math.Sqrt((p.X * p.X) + (p.Y * p.Y)), 1e-3)); // on the cylinder
        Assert.All(pts, p => Assert.True(p.X > 0)); // the near side

        // Two sketched lines joined end to end, used as one sweep path.
        var a = new Sketch();
        a.AddLine(new Vec2(0, 0), new Vec2(0, 30));
        var b = new Sketch();
        b.AddLine(new Vec2(0, 30), new Vec2(30, 30));
        var af = doc.Add(new SketchFeature { Plane = PlaneRef.Front, Sketch = a });
        var bf = doc.Add(new SketchFeature { Plane = PlaneRef.Front, Sketch = b });
        var joined = doc.Add(new CompositeCurveFeature { CurveIds = [af.Id, bf.Id] });
        var ring = new Sketch();
        ring.AddCircle(Vec2.Zero, 2);
        var rf = doc.Add(new SketchFeature { Plane = PlaneRef.Top, Sketch = ring });
        doc.Add(new SweepFeature { ProfileSketchId = rf.Id, PathSketchId = joined.Id, Merge = false });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(2, doc.Bodies.Count);
        Assert.InRange(doc.Bodies[1].Shape.MassProperties.Volume, Math.PI * 4 * 55, Math.PI * 4 * 62);
    }

    [Fact]
    public void FullRoundOnARib()
    {
        // A rib 10 thick (x), 30 tall (y), 20 long (z); round its top off completely.
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(10, 30));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        TopoRef Face(Func<Vec3, bool> normal) => body.FaceRef(Enumerable.Range(1, body.FaceCount).Single(i => normal(body.Face(i).Normal)));
        doc.Add(new FullRoundFilletFeature { SideFace1 = Face(n => n.X < -0.99), CenterFace = Face(n => n.Y > 0.99), SideFace2 = Face(n => n.X > 0.99) });
        doc.Rebuild();
        AssertOk(doc);

        // 10 x 25 x 20 of block below the round, and a half-disc of radius 5 along the 20.
        Assert.Equal((10 * 25 * 20) + (Math.PI * 25 / 2 * 20), Volume(doc), 1e-3);
        var after = doc.Bodies[0];
        Assert.DoesNotContain(Enumerable.Range(1, after.FaceCount), i => after.Face(i).Surface == Chamfer.Kernel.Occt.SurfaceKind.Plane && after.Face(i).Normal.Y > 0.99);
        Assert.Equal(30, after.Shape.Bounds.Max.Y, 1e-6);
    }

    [Fact]
    public void FaceFilletRoundsOnlyTheSharedEdge()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        TopoRef Face(Func<Vec3, bool> normal) => body.FaceRef(Enumerable.Range(1, body.FaceCount).Single(i => normal(body.Face(i).Normal)));
        var top = Face(n => n.Z > 0.99);
        var front = Face(n => n.Y < -0.99);

        // Top and front faces meet along one 40 mm edge: just that one is rounded.
        doc.Add(new FilletFeature { Faces = [top, front], BetweenFaces = true, Radius = 3 });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(16000 - ((1 - (Math.PI / 4)) * 9 * 40), Volume(doc), 1e-3);
    }

    [Fact]
    public void VertexChamferCutsACorner()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var body = doc.Bodies[0];

        // The top corner at (40, 20, 10): an edge ending (or starting) there.
        var corner = new Vec3(40, 20, 10);
        var edge = Enumerable.Range(1, body.EdgeCount).First(i => Vec3.Distance(body.Edge(i).Start, corner) < 1e-6 || Vec3.Distance(body.Edge(i).End, corner) < 1e-6);
        var atEnd = Vec3.Distance(body.Edge(edge).End, corner) < 1e-6;
        var chamfer = doc.Add(new VertexChamferFeature { Vertices = [new VertexRef(body.EdgeRef(edge), atEnd)], Distance = 3 });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(8000 - (27.0 / 6), Volume(doc), 1e-6); // a 3 x 3 x 3 corner tetrahedron
        Assert.Equal(7, doc.Bodies[0].FaceCount);

        // Unequal distances: 2, 4 and 6 along the three edges.
        chamfer.Distance = 2;
        chamfer.Distance2 = 4;
        chamfer.Distance3 = 6;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(8000 - (48.0 / 6), Volume(doc), 1e-6);
    }

    [Fact]
    public void IndentPressesAToolIntoATarget()
    {
        // A 60 x 60 x 10 plate, and a separate 20 x 20 x 20 block sunk 5 into its top.
        var doc = new PartDocument();
        var plate = new Sketch();
        plate.AddCornerRectangle(new Vec2(0, 0), new Vec2(60, 60));
        var pf = doc.Add(new SketchFeature { Sketch = plate });
        doc.Add(new ExtrudeFeature { SketchId = pf.Id, Depth = 10 });
        var level = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Front, Offset = 5 });
        var block = new Sketch();
        block.AddCornerRectangle(new Vec2(20, 20), new Vec2(40, 40));
        var bf = doc.Add(new SketchFeature { Plane = new PlaneRef(level.Id), Sketch = block });
        doc.Add(new ExtrudeFeature { SketchId = bf.Id, Depth = 20, Merge = false });
        doc.Rebuild();
        Assert.Equal(2, doc.Bodies.Count);
        var (target, tool) = (doc.Bodies[0].Name, doc.Bodies[1].Name);

        // No clearance: a 20 x 20 x 5 pocket; the tool stays.
        var indent = doc.Add(new IndentFeature { TargetBody = target, ToolBody = tool });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(2, doc.Bodies.Count);
        Assert.Equal((60 * 60 * 10) - (20 * 20 * 5), doc.Bodies[0].Shape.MassProperties.Volume, 1e-6);

        // A 1 mm clearance: the pocket grows by 1 all round (rounded at its corners), and 1 deeper.
        indent.Clearance = 1;
        doc.Rebuild();
        AssertOk(doc);
        var pocket = (60 * 60 * 10) - doc.Bodies[0].Shape.MassProperties.Volume;
        Assert.InRange(pocket, 20 * 20 * 6, 22 * 22 * 6);
    }

    [Fact]
    public void WrapEmbossesAndDebossesOntoACylinder()
    {
        // A cylinder of radius 20, 60 long along Z; a 20 x 20 square sketched on a plane beside it (x = 20).
        var doc = new PartDocument();
        var disc = new Sketch();
        disc.AddCircle(Vec2.Zero, 20);
        var df = doc.Add(new SketchFeature { Sketch = disc });
        doc.Add(new ExtrudeFeature { SketchId = df.Id, Depth = 60 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var side = body.FaceRef(Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Surface == Chamfer.Kernel.Occt.SurfaceKind.Cylinder));
        var beside = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Right, Offset = 20 });
        var square = new Sketch();
        square.AddCornerRectangle(new Vec2(-40, -10), new Vec2(-20, 10)); // (u, v) = (-z, y): z 20..40, y -10..10
        var sf = doc.Add(new SketchFeature { Plane = new PlaneRef(beside.Id), Sketch = square });
        var wrap = doc.Add(new WrapFeature { SketchId = sf.Id, Face = side, Depth = 2 });
        doc.Rebuild();
        AssertOk(doc);

        // 20 mm round a radius-20 cylinder is one radian; raised 2 over 20 high.
        var cylinder = Math.PI * 400 * 60;
        var shell = 0.5 * 1 * ((22 * 22) - (20 * 20)) * 20;
        Assert.Equal(cylinder + shell, Volume(doc), shell * 0.02);
        Assert.Single(doc.Bodies);

        wrap.Deboss = true;
        doc.Rebuild();
        AssertOk(doc);
        var sunk = 0.5 * 1 * ((20 * 20) - (18 * 18)) * 20;
        Assert.Equal(cylinder - sunk, Volume(doc), sunk * 0.02);
    }

    [Fact]
    public void FlexTwistsTapersAndStretches()
    {
        // A 20 x 10 bar, 100 long up Z, centred on the axis.
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCenterRectangle(Vec2.Zero, new Vec2(10, 5));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 100 });
        var flex = doc.Add(new FlexFeature { Mode = FlexMode.Twisting, Angle = 90, Length = 100 });
        doc.Rebuild();
        AssertOk(doc);

        // A quarter turn keeps the volume; the far end now lies 10 across X and 20 across Y.
        Assert.Equal(20000, Volume(doc), 20000 * 0.01);
        var shape = doc.Bodies[0].Shape;
        var end = Enumerable.Range(1, shape.FaceCount).Single(i => shape.Face(i).Normal.Z > 0.99 && shape.Face(i).Centroid.Z > 99);
        var pts = shape.EdgesOfFace(end).SelectMany(e => shape.SampleEdge(e)).ToList();
        Assert.Equal(10, pts.Max(p => p.X) - pts.Min(p => p.X), 0.1);
        Assert.Equal(20, pts.Max(p => p.Y) - pts.Min(p => p.Y), 0.1);

        // Tapering to half size: the area shrinks with the square of the size, (1 - t/2)^2 integrated.
        flex.Mode = FlexMode.Tapering;
        flex.EndScale = 0.5;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(200 * 100 * (1 - 0.5 + (0.25 / 3)), Volume(doc), 200 * 100 * 0.01);

        // Stretching by 50 mm: exactly half as long again.
        flex.Mode = FlexMode.Stretching;
        flex.Stretch = 50;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(30000, Volume(doc), 1);
        Assert.Equal(150, doc.Bodies[0].Shape.Bounds.Size.Z, 1e-3);
    }

    [Fact]
    public void DeformPushesABulge()
    {
        // A 100 x 100 x 10 plate; pull up the middle of its top face 5 mm, fading out over 30 mm.
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCenterRectangle(Vec2.Zero, new Vec2(50, 50));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        var top = doc.Add(new ReferencePointFeature { X = 0, Y = 0, Z = 10 });
        doc.Add(new DeformFeature { PointId = top.Id, Direction = AxisRef.Z, Distance = 5, Radius = 30 });
        doc.Rebuild();
        AssertOk(doc);

        // The influence is a ball of radius 30 round the point: the top rises by 5 (1 - r^2/R^2)^2,
        // and the bottom face, 10 below, lifts too (by the same falloff at its distance).
        var rise = Math.PI * 900 * 5 / 3;
        var lift = 5 * Math.PI * Math.Pow(800, 3) / 3 / (900.0 * 900);
        Assert.Equal(100000 + rise - lift, Volume(doc), (rise - lift) * 0.05);
        Assert.Equal(15, doc.Bodies[0].Shape.Bounds.Max.Z, 0.5);
        Assert.Equal(100, doc.Bodies[0].Shape.Bounds.Size.X, 1e-3); // the edges don't move
    }

    [Fact]
    public void BendAllowanceFromTablesAndDeductions()
    {
        var sm = new SheetMetalState(2, 3, 0.4, Plane3.Front, new Vec3(0, 0, 2), [], [], [], AcceptsFlanges: true);
        var quarter = Math.PI / 2;
        Assert.Equal(quarter * (3 + (0.4 * 2)), sm.BendAllowance(quarter), 1e-12);

        var table = sm with { Method = BendAllowanceMethod.BendTable, BendTable = [(45, 2.5), (90, 5.6), (135, 8.0)] };
        Assert.Equal(5.6, table.BendAllowance(quarter), 1e-12);
        Assert.Equal(6.8, table.BendAllowance(112.5 * Math.PI / 180), 1e-9); // halfway between 90 and 135
        Assert.Equal(2.5 * 30 / 45, table.BendAllowance(Math.PI / 6), 1e-9); // below the table, in proportion

        // Deduction: 2 OSSB - BD, OSSB = (r + t) tan(45 deg) = 5 at 90 degrees.
        var deduction = sm with { Method = BendAllowanceMethod.BendDeduction, BendDeduction90 = 4.2 };
        Assert.Equal((2 * 5) - 4.2, deduction.BendAllowance(quarter), 1e-12);

        var flange = new BaseFlangeFeature { BendTable = "45: 2.5\n90 = 5.6\nnonsense\n135: 8" };
        Assert.Equal([(45.0, 2.5), (90.0, 5.6), (135.0, 8.0)], flange.TableRows());
    }

    [Fact]
    public void TrimUntrimAndExtendSurfaces()
    {
        // A flat 40 x 20 surface with a 5 mm hole in it.
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        s.AddCircle(new Vec2(10, 10), 5);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new PlanarSurfaceFeature { SketchId = sf.Id });
        doc.Rebuild();
        var surface = Assert.Single(doc.Bodies).Name;
        double Area() => doc.Bodies.Single().Shape.MassProperties.SurfaceArea;
        var holed = (40 * 20) - (Math.PI * 25);
        Assert.Equal(holed, Area(), 1e-3);

        // Untrim fills the hole.
        var edit = doc.Add(new SurfaceEditFeature { Body = surface, Operation = SurfaceEdit.Untrim });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(800, Area(), 1e-3);

        // Extend grows the outline 2 mm all round (rounded corners).
        edit.Operation = SurfaceEdit.Extend;
        edit.Distance = 2;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal((44 * 24) - (4 * 4) + (Math.PI * 4) - (Math.PI * 25), Area(), 0.05);

        // Trim with the Right plane moved to x = 25: keep the 15 mm on the far side.
        var cut = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Right, Offset = 25 });
        edit.Operation = SurfaceEdit.Trim;
        edit.Tool = new PlaneRef(cut.Id);
        doc.Move(cut, -1);
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(15 * 20, Area(), 1e-3);
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
