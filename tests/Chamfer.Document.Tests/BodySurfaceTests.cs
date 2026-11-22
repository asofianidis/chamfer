using System.IO.Compression;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class BodySurfaceTests
{
    private static void AssertOk(PartDocument doc)
    {
        foreach (var f in doc.Features)
        {
            var r = doc.Results[f.Id];
            Assert.True(r.Status is FeatureStatus.Ok, $"{f.Name}: {r.Status} {r.Message}");
        }
    }

    private static SketchFeature Rect(PartDocument doc, double x0, double y0, double x1, double y1, PlaneRef? plane = null)
    {
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(x0, y0), new Vec2(x1, y1));
        return doc.Add(new SketchFeature { Sketch = s, Plane = plane ?? PlaneRef.Front });
    }

    /// <summary>Two overlapping, unmerged blocks: Body1 is 0..40, Body2 is 20..60 in X.</summary>
    private static PartDocument TwoBodies()
    {
        var doc = new PartDocument();
        var a = Rect(doc, 0, 0, 40, 20);
        doc.Add(new ExtrudeFeature { SketchId = a.Id, Depth = 10 });
        var b = Rect(doc, 20, 0, 60, 20);
        doc.Add(new ExtrudeFeature { SketchId = b.Id, Depth = 10, Merge = false });
        doc.Rebuild();
        Assert.Equal(2, doc.Bodies.Count);
        return doc;
    }

    [Theory]
    [InlineData(CombineOperation.Add, 60 * 20 * 10)]
    [InlineData(CombineOperation.Subtract, 20 * 20 * 10)]
    [InlineData(CombineOperation.Common, 20 * 20 * 10)]
    public void CombineBodies(CombineOperation operation, double volume)
    {
        var doc = TwoBodies();
        doc.Add(new CombineFeature { Operation = operation, MainBody = "Body1", ToolBodies = ["Body2"] });
        doc.Rebuild();
        AssertOk(doc);
        var body = Assert.Single(doc.Bodies);
        Assert.Equal(volume, body.Shape.MassProperties.Volume, 1e-6);
    }

    [Fact]
    public void SplitMoveCopyDeleteAndColour()
    {
        var doc = new PartDocument();
        var s = Rect(doc, 0, 0, 40, 20);
        doc.Add(new ExtrudeFeature { SketchId = s.Id, Depth = 10 });
        doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Right, Offset = 10 });
        doc.Rebuild();
        var plane = doc.Features[^1];
        doc.Add(new SplitBodyFeature { Plane = new PlaneRef(plane.Id) });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(2, doc.Bodies.Count);
        Assert.Equal(["Body1-1", "Body1-2"], doc.Bodies.Select(b => b.Name).OrderBy(n => n).ToArray());

        doc.Add(new MoveCopyBodyFeature { Bodies = ["Body1-1"], Dz = 50, Copy = true, Copies = 2 });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(4, doc.Bodies.Count);
        Assert.Equal(110, doc.Bodies.Max(b => b.Shape.Bounds.Max.Z), 1e-6);

        doc.Add(new DeleteBodyFeature { Bodies = ["Body1-2"] });
        doc.Add(new AppearanceFeature { Color = 0xFF0000 });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(3, doc.Bodies.Count);
        Assert.All(doc.Bodies, b => Assert.Equal(0xFF0000u, b.Color));
    }

    [Fact]
    public void SurfacesKnitIntoASolidAndThicken()
    {
        var doc = new PartDocument();

        // An open box: four walls extruded from a rectangle, plus a planar bottom and top.
        var walls = Rect(doc, 0, 0, 20, 20, PlaneRef.Top);
        doc.Add(new SurfaceExtrudeFeature { SketchId = walls.Id, Depth = 10 });
        var bottom = Rect(doc, 0, 0, 20, 20, PlaneRef.Top);
        doc.Add(new PlanarSurfaceFeature { SketchId = bottom.Id });
        var topPlane = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Top, Offset = 10 });
        var top = Rect(doc, 0, 0, 20, 20, new PlaneRef(topPlane.Id));
        doc.Add(new PlanarSurfaceFeature { SketchId = top.Id });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(3, doc.Bodies.Count);
        Assert.All(doc.Bodies, b => Assert.True(b.IsSurface));

        doc.Add(new KnitFeature { Bodies = [.. doc.Bodies.Select(b => b.Name)] });
        doc.Rebuild();
        AssertOk(doc);
        var solid = Assert.Single(doc.Bodies);
        Assert.False(solid.IsSurface);
        Assert.Equal(20 * 20 * 10, solid.Shape.MassProperties.Volume, 1e-4);
    }

    [Fact]
    public void ThickenRevolveFillAndOffset()
    {
        var doc = new PartDocument();
        var s = Rect(doc, 0, 0, 30, 20, PlaneRef.Top);
        doc.Add(new PlanarSurfaceFeature { SketchId = s.Id });
        doc.Rebuild();
        doc.Add(new ThickenFeature { Body = doc.Bodies[0].Name, Thickness = 2 });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(30 * 20 * 2, Assert.Single(doc.Bodies).Shape.MassProperties.Volume, 1e-4);

        // A revolved surface: a line 10 from the axis, swept round, is a cylinder wall.
        var cyl = new PartDocument();
        var cs = new Sketch();
        cs.AddLine(new Vec2(0, 0), new Vec2(0, 30), construction: true);
        cs.AddLine(new Vec2(10, 0), new Vec2(10, 30));
        var csf = cyl.Add(new SketchFeature { Sketch = cs });
        cyl.Add(new SurfaceRevolveFeature { SketchId = csf.Id });
        cyl.Rebuild();
        AssertOk(cyl);
        Assert.Equal(2 * Math.PI * 10 * 30, cyl.Bodies[0].Shape.MassProperties.SurfaceArea, 1e-3);

        // Fill a circle boundary: a disc.
        var fill = new PartDocument();
        var fs = new Sketch();
        fs.AddCircle(Vec2.Zero, 5);
        var fsf = fill.Add(new SketchFeature { Sketch = fs });
        fill.Add(new FilledSurfaceFeature { SketchId = fsf.Id });
        fill.Rebuild();
        AssertOk(fill);
        Assert.Equal(Math.PI * 25, fill.Bodies[0].Shape.MassProperties.SurfaceArea, 1e-2);

        // Offset the top face of a block upward.
        var block = new PartDocument();
        var bs = Rect(block, 0, 0, 10, 10);
        block.Add(new ExtrudeFeature { SketchId = bs.Id, Depth = 5 });
        block.Rebuild();
        var body = block.Bodies[0];
        var topFace = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        block.Add(new OffsetSurfaceFeature { Faces = [body.FaceRef(topFace)], Distance = 3 });
        block.Rebuild();
        AssertOk(block);
        var offset = block.Bodies.Single(b => b.IsSurface);
        Assert.Equal(8, offset.Shape.Bounds.Max.Z, 1e-6);
    }

    [Fact]
    public void MeshExports()
    {
        var doc = TwoBodies();
        var meshes = doc.Bodies.Select(b => (b.Name, b.Shape.Tessellate())).ToList();
        var obj = MeshExport.Obj(meshes);
        Assert.Contains("o Body1", obj, StringComparison.Ordinal);
        Assert.Contains("\nf ", obj, StringComparison.Ordinal);

        var bytes = MeshExport.ThreeMf(meshes);
        using var zip = new ZipArchive(new MemoryStream(bytes));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        using var reader = new StreamReader(zip.GetEntry("3D/3dmodel.model")!.Open());
        var model = reader.ReadToEnd();
        Assert.Contains("unit=\"millimeter\"", model, StringComparison.Ordinal);
        Assert.Equal(2, model.Split("<object ").Length - 1);

        // Welded box: 8 corners and 12 triangles.
        using var box = Kernel.Occt.Modeling.Box(10, 10, 10);
        var (verts, tris) = MeshExport.Weld(box.Tessellate());
        Assert.Equal(8, verts.Count);
        Assert.Equal(12, tris.Count);
    }
}
