using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Tests;

public class RecognitionTests
{
    /// <summary>An "imported" 60 × 40 × 10 plate with Ø6 holes at x = 15 and 45 and a Ø10 hole at x = 30.</summary>
    private static PartDocument ImportedPlate()
    {
        using var box = Modeling.Box(60, 40, 10);
        var shape = box;
        var owned = new List<Shape>();
        foreach (var (x, r) in new[] { (15.0, 3.0), (45.0, 3.0), (30.0, 5.0) })
        {
            using var cyl = Modeling.Cylinder(r, 10);
            using var placed = Modeling.Transform(cyl, Transform3.Translation(new Vec3(x, 20, 0)));
            using var cut = Modeling.Boolean(shape, placed.Result, BooleanKind.Cut);
            shape = cut.Result;
            owned.Add(shape);
        }

        var doc = new PartDocument();
        doc.Add(new ImportFeature { SourceName = "plate.step", Brep = shape.ToBrep() });
        doc.Rebuild();
        foreach (var s in owned)
        {
            s.Dispose();
        }

        return doc;
    }

    [Fact]
    public void DeleteFaceFillsHoles()
    {
        var doc = ImportedPlate();
        var body = doc.Bodies[0];
        var bigHole = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Surface == SurfaceKind.Cylinder && Math.Abs(body.Face(i).Radius - 5) < 1e-6);
        var del = doc.Add(new DeleteFaceFeature { Faces = [body.FaceRef(bigHole)] });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[del.Id].Status);
        Assert.Equal((60 * 40 * 10) - (2 * Math.PI * 9 * 10), doc.MassProperties().Volume, 1e-3);
    }

    [Fact]
    public void RecognizedHolesBecomeEditableFeatures()
    {
        var doc = ImportedPlate();
        var before = doc.MassProperties().Volume;
        Assert.Equal(3, FeatureRecognition.RecognizeHoles(doc, doc.Bodies[0]));
        doc.Rebuild();
        Assert.All(doc.Features, f => Assert.Equal(FeatureStatus.Ok, doc.Results[f.Id].Status));
        Assert.Single(doc.Features.OfType<DeleteFaceFeature>());
        var holes = doc.Features.OfType<HoleFeature>().ToList();
        Assert.Equal(2, holes.Count); // one per diameter
        Assert.Equal(before, doc.MassProperties().Volume, 1e-3);

        // Now they're features: widen the Ø6 holes to Ø8.
        holes.Single(h => Math.Abs(h.Diameter - 6) < 1e-6).Diameter = 8;
        doc.Rebuild();
        Assert.Equal(before - (2 * Math.PI * (16 - 9) * 10), doc.MassProperties().Volume, 1e-3);
    }
}
