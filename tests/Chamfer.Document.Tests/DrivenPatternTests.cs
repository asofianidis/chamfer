using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class DrivenPatternTests
{
    private const double Hole = Math.PI * 9 * 10; // Ø6 through a 10 mm plate

    /// <summary>A 100 × 60 × 10 plate with a Ø6 hole at (x, y).</summary>
    private static (PartDocument Doc, Feature Hole, TopoRef Top) Plate(double x = 10, double y = 10)
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 60));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        var plane = body.Shape.FacePlane(top)!.Value;
        var pts = new Sketch();
        var (u, v) = plane.ToLocal(new Vec3(x, y, 10));
        pts.AddPoint(new Vec2(u, v));
        var ps = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = pts });
        var hole = doc.Add(new HoleFeature { SketchId = ps.Id, Diameter = 6, EndCondition = EndCondition.ThroughAll });
        doc.Rebuild();
        return (doc, hole, body.FaceRef(top));
    }

    private static void AssertHoles(PartDocument doc, Feature pattern, int holes)
    {
        Assert.Equal(FeatureStatus.Ok, doc.Results[pattern.Id].Status);
        Assert.Equal((100 * 60 * 10) - (holes * Hole), doc.MassProperties().Volume, 1e-3);
    }

    [Fact]
    public void SketchDrivenPatternUsesSketchPoints()
    {
        var (doc, hole, _) = Plate();
        var s = new Sketch();
        s.AddPoint(new Vec2(30, 10));
        s.AddPoint(new Vec2(50, 30));
        s.AddPoint(new Vec2(80, 50));
        s.AddCircle(new Vec2(70, 20), 2, construction: true); // not a sketch point
        var sf = doc.Add(new SketchFeature { Sketch = s });
        var p = doc.Add(new SketchDrivenPatternFeature { Seeds = [hole.Id], SketchId = sf.Id });
        doc.Rebuild();
        AssertHoles(doc, p, 4);
    }

    [Fact]
    public void TablePatternUsesOffsets()
    {
        var (doc, hole, _) = Plate();
        var offsets = TablePatternFeature.Parse("20, 0\n40 0\nx,y\n\n20;20");
        Assert.Equal(3, offsets.Count);
        Assert.Equal(new PatternOffset(20, 20), offsets[^1]);
        var p = doc.Add(new TablePatternFeature { Seeds = [hole.Id], Offsets = [new(20, 0), new(40, 0), new(20, 20)] });
        doc.Rebuild();
        AssertHoles(doc, p, 4);

        var loaded = PartDocument.Load(doc.Save());
        loaded.Rebuild();
        Assert.Equal(doc.MassProperties().Volume, loaded.MassProperties().Volume, 1e-6);
    }

    [Fact]
    public void CurveDrivenPatternAlongOpenAndClosedCurves()
    {
        var (doc, hole, _) = Plate();
        var path = new Sketch();
        path.AddLine(new Vec2(10, 10), new Vec2(90, 10));
        var pf = doc.Add(new SketchFeature { Sketch = path });
        var p = doc.Add(new CurveDrivenPatternFeature { Seeds = [hole.Id], SketchId = pf.Id, Count = 5 });
        doc.Rebuild();
        AssertHoles(doc, p, 5);

        // Around a circle through the seed: six evenly spaced holes.
        var (doc2, hole2, _) = Plate(50, 10);
        var ring = new Sketch();
        ring.AddCircle(new Vec2(50, 30), 20);
        var rf = doc2.Add(new SketchFeature { Sketch = ring });
        var p2 = doc2.Add(new CurveDrivenPatternFeature { Seeds = [hole2.Id], SketchId = rf.Id, Count = 6 });
        doc2.Rebuild();
        AssertHoles(doc2, p2, 6);
    }

    [Fact]
    public void FillPatternCoversTheFace()
    {
        var (doc, hole, top) = Plate();
        var p = doc.Add(new FillPatternFeature { Seeds = [hole.Id], Face = top, Spacing = 20, Margin = 5 });
        doc.Rebuild();
        AssertHoles(doc, p, 15); // 5 × 3 grid from the seed at (10, 10)

        ((FillPatternFeature)p).Staggered = true;
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[p.Id].Status);
        var holes = Math.Round(((100 * 60 * 10) - doc.MassProperties().Volume) / Hole);
        Assert.InRange(holes, 13, 14); // hexagonal rows: 5-4-5 or 3-2-3-2-3 depending on the face's axes
    }
}
