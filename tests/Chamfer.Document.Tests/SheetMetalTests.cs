using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class SheetMetalTests
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
    public void PlateWithEdgeFlangeFoldsAndFlattens()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 50));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new BaseFlangeFeature { SketchId = sf.Id, Thickness = 2, BendRadius = 1, KFactor = 0.5 });
        doc.Rebuild();
        AssertOk(doc);
        var body = doc.Bodies[0];

        // The long top edge (y = 50) on the face at z = 2.
        var edge = Enumerable.Range(1, body.EdgeCount).Single(i => Vec3.Distance(body.Edge(i).Midpoint, new Vec3(50, 50, 2)) < 1e-6);
        doc.Add(new EdgeFlangeFeature { Edges = [body.EdgeRef(edge)], Length = 20 });
        doc.Rebuild();
        AssertOk(doc);
        var folded = Assert.Single(doc.Bodies).Shape;
        var bendVolume = 100 * (Math.PI / 4) * ((3 * 3) - (1 * 1));
        Assert.Equal(10000 + bendVolume + 4000, folded.MassProperties.Volume, 1e-3);

        // The flange rises above the plate: inner radius 1 from z = 2, leg 20 long.
        Assert.Equal(2 + 1 + 20, folded.Bounds.Max.Z, 1e-6);
        Assert.Equal(50 + 1 + 2, folded.Bounds.Max.Y, 1e-6);

        // Flat: 50 + bend allowance (π/2 × (1 + 0.5 × 2) = π) + 20 by 100.
        var (w, h) = FlatPattern.Size(doc);
        Assert.Equal(100, w, 1e-6);
        Assert.Equal(50 + Math.PI + 20, h, 1e-6);
        using var flat = FlatPattern.Solid(doc);
        Assert.Equal(folded.MassProperties.Volume, flat.MassProperties.Volume, 1e-3); // K = 0.5 preserves volume
        Assert.Single(doc.State.SheetMetal!.BendLines);
    }

    [Fact]
    public void ReverseFlangeBendsDown()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(60, 40));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new BaseFlangeFeature { SketchId = sf.Id, Thickness = 1, BendRadius = 2 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var edge = Enumerable.Range(1, body.EdgeCount).Single(i => Vec3.Distance(body.Edge(i).Midpoint, new Vec3(60, 20, 1)) < 1e-6);
        doc.Add(new EdgeFlangeFeature { Edges = [body.EdgeRef(edge)], Length = 15, Reverse = true });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(-(2 + 15), doc.Bodies[0].Shape.Bounds.Min.Z, 1e-6);
    }

    [Fact]
    public void BentLBracketFromOpenProfile()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddPolyline([new Vec2(0, 30), new Vec2(0, 0), new Vec2(40, 0)]);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new BaseFlangeFeature { SketchId = sf.Id, Thickness = 1, BendRadius = 2, KFactor = 0.5, Depth = 20 });
        doc.Rebuild();
        AssertOk(doc);
        var body = Assert.Single(doc.Bodies);
        var bbox = body.Shape.Bounds;
        Assert.Equal(40, bbox.Size.X, 1e-6);
        Assert.Equal(30, bbox.Size.Y, 1e-6);
        Assert.Equal(20, bbox.Size.Z, 1e-6);
        Assert.Contains(Enumerable.Range(1, body.FaceCount), i => body.Face(i).Surface == SurfaceKind.Cylinder && Math.Abs(body.Face(i).Radius - 2) < 1e-6);

        // Flat length: (30 - 3) + (40 - 3) + π/2 × (2 + 0.5) for the outside dimensions given.
        var (w, h) = FlatPattern.Size(doc);
        Assert.Equal(27 + 37 + (Math.PI / 2 * 2.5), w, 1e-6);
        Assert.Equal(20, h, 1e-6);
        using var flat = FlatPattern.Solid(doc);
        Assert.Equal(body.Shape.MassProperties.Volume, flat.MassProperties.Volume, 1e-3);
    }

    [Fact]
    public void CutsInTheBaseAppearInTheFlatPattern()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(60, 40));
        s.AddCircle(new Vec2(20, 20), 4);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new BaseFlangeFeature { SketchId = sf.Id, Thickness = 1.5, BendRadius = 1 });
        var c = new Sketch();
        c.AddCircle(new Vec2(45, 20), 5);
        var cf = doc.Add(new SketchFeature { Sketch = c });
        doc.Add(new ExtrudeFeature { SketchId = cf.Id, IsCut = true, EndCondition = EndCondition.ThroughAll, HasDirection2 = true, EndCondition2 = EndCondition.ThroughAll });
        doc.Rebuild();
        AssertOk(doc);
        using var flat = FlatPattern.Solid(doc);
        Assert.Equal(((60 * 40) - (Math.PI * 16) - (Math.PI * 25)) * 1.5, flat.MassProperties.Volume, 1e-3);

        // Round trip.
        var loaded = PartDocument.Load(doc.Save());
        loaded.Rebuild();
        Assert.NotNull(loaded.State.SheetMetal);
    }

    private static (PartDocument Doc, int TopEdge) Plate()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 50));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new BaseFlangeFeature { SketchId = sf.Id, Thickness = 2, BendRadius = 1, KFactor = 0.5 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        return (doc, Enumerable.Range(1, body.EdgeCount).Single(i => Vec3.Distance(body.Edge(i).Midpoint, new Vec3(50, 50, 2)) < 1e-6));
    }

    [Fact]
    public void ClosedAndOpenHems()
    {
        var (doc, edge) = Plate();
        var hem = doc.Add(new HemFeature { Edges = [doc.Bodies[0].EdgeRef(edge)], Length = 6 });
        doc.Rebuild();
        AssertOk(doc);
        var folded = Assert.Single(doc.Bodies).Shape;
        Assert.True(folded.IsValid);

        // Closed hem: inner radius 0.02 (a hair of clearance), leg back over the plate.
        const double r = 0.02;
        var bend = 100 * (Math.PI / 2) * (((r + 2) * (r + 2)) - (r * r));
        Assert.Equal(10000 + bend + (6 * 2 * 100), folded.MassProperties.Volume, 1e-3);
        Assert.Equal(50 + r + 2, folded.Bounds.Max.Y, 1e-6);
        Assert.Equal(2 + (2 * r) + 2, folded.Bounds.Max.Z, 1e-6);

        // Flat: the hem adds its bend allowance π (r + K t) plus the leg; K = 0.5 keeps the volume.
        var (_, h) = FlatPattern.Size(doc);
        Assert.Equal(50 + (Math.PI * (r + 1)) + 6, h, 1e-6);
        using var flat = FlatPattern.Solid(doc);
        Assert.Equal(folded.MassProperties.Volume, flat.MassProperties.Volume, 1e-3);

        // Open hem with a 1 mm gap.
        ((HemFeature)hem).Gap = 1;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(2 + 1 + 2, doc.Bodies[0].Shape.Bounds.Max.Z, 1e-6);
    }

    [Fact]
    public void TabExtendsTheSheetAndTheFlatPattern()
    {
        var (doc, _) = Plate();
        var body = doc.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99 && body.Face(i).Centroid.Z > 1.9);
        var plane = body.Shape.FacePlane(top)!.Value;
        var s = new Sketch();
        var (u0, v0) = plane.ToLocal(new Vec3(100, 10, 2));
        var (u1, v1) = plane.ToLocal(new Vec3(130, 40, 2));
        s.AddCornerRectangle(new Vec2(u0, v0), new Vec2(u1, v1));
        var sf = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = s });
        doc.Add(new TabFeature { SketchId = sf.Id });
        doc.Rebuild();
        AssertOk(doc);
        var shape = Assert.Single(doc.Bodies).Shape;
        Assert.Equal(10000 + (30 * 30 * 2), shape.MassProperties.Volume, 1e-3);
        Assert.Equal(2, shape.Bounds.Size.Z, 1e-9); // stays in the sheet
        var (w, _) = FlatPattern.Size(doc);
        Assert.Equal(130, w, 1e-6);
        using var flat = FlatPattern.Solid(doc);
        Assert.Equal(shape.MassProperties.Volume, flat.MassProperties.Volume, 1e-3);
    }

    [Fact]
    public void SketchedBendFoldsAlongALine()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 60));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new BaseFlangeFeature { SketchId = sf.Id, Thickness = 2, BendRadius = 1, KFactor = 0.5 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99 && body.Face(i).Centroid.Z > 1.9);
        var plane = body.Shape.FacePlane(top)!.Value;
        var bend = new Sketch();
        var (u0, v0) = plane.ToLocal(new Vec3(-10, 30, 2));
        var (u1, v1) = plane.ToLocal(new Vec3(110, 30, 2));
        bend.AddLine(new Vec2(u0, v0), new Vec2(u1, v1));
        var bf = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = bend });
        var feature = (SketchedBendFeature)doc.Add(new SketchedBendFeature { SketchId = bf.Id, Angle = 90 });
        doc.Rebuild();
        AssertOk(doc);
        var folded = Assert.Single(doc.Bodies).Shape;
        Assert.True(folded.IsValid);
        Assert.Equal(1, folded.SolidCount);
        Assert.Equal(100 * 60 * 2, folded.MassProperties.Volume, 1e-3); // K = 0.5 keeps the volume

        // The far side stands up: it starts at the end of the bend and rises its remaining length.
        var rise = 2 + 1 + (30 - (Math.PI / 2));
        Assert.Equal(rise, folded.Bounds.Max.Z, 1e-6);
        Assert.Equal(30 - (Math.PI / 2) + 1 + 2, folded.Bounds.Max.Y, 1e-6);

        // Flat pattern: unchanged outline, one bend line across the plate.
        var (w, h) = FlatPattern.Size(doc);
        Assert.Equal((100.0, 60.0), (Math.Round(w, 6), Math.Round(h, 6)));
        var line = Assert.Single(doc.State.SheetMetal!.BendLines);
        Assert.Equal(100, Point2.Distance(line.A, line.B), 1e-6);
        using var flat = FlatPattern.Solid(doc);
        Assert.Equal(folded.MassProperties.Volume, flat.MassProperties.Volume, 1e-3);

        // Folding the other side, downward.
        feature.FlipSide = true;
        feature.Reverse = true;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(-(30 - (Math.PI / 2) + 1), doc.Bodies[0].Shape.Bounds.Min.Z, 1e-6);
    }

    [Fact]
    public void DimplesPressAHollowBump()
    {
        var (doc, _) = Plate();
        var body = doc.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99 && body.Face(i).Centroid.Z > 1.9);
        var plane = body.Shape.FacePlane(top)!.Value;
        var pts = new Sketch();
        var (u, v) = plane.ToLocal(new Vec3(50, 30, 2));
        pts.AddPoint(new Vec2(u, v));
        var sf = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = pts });
        var dimple = (DimpleFeature)doc.Add(new DimpleFeature { SketchId = sf.Id, Diameter = 20, Height = 4 });
        doc.Rebuild();
        AssertOk(doc);
        var shape = Assert.Single(doc.Bodies).Shape;
        Assert.True(shape.IsValid);

        // The bump and the cavity behind it are the same cap shifted by the thickness: volume unchanged.
        Assert.Equal(100 * 50 * 2, shape.MassProperties.Volume, 1e-3);
        Assert.Equal(2 + 4, shape.Bounds.Max.Z, 1e-6);
        var inside = shape.Classify([new Vec3(50, 30, 1), new Vec3(50, 30, 5), new Vec3(20, 30, 1)]);
        Assert.Equal([0, 1, 1], inside.Select(b => (int)b)); // hollow behind, solid shell above, plate elsewhere

        // Pressed the other way, it bulges below the plate; the flat pattern ignores it.
        dimple.Reverse = true;
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(-4, doc.Bodies[0].Shape.Bounds.Min.Z, 1e-6);
        Assert.Equal((100.0, 50.0), FlatPattern.Size(doc));
    }
}
