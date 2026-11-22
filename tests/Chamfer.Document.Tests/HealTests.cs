using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Tests;

public class HealTests
{
    [Fact]
    public void ImportedLooseFacesHealIntoASolid()
    {
        // An "imported" box whose faces don't share edges and have a small gap.
        using var box = Modeling.Box(10, 20, 30);
        var faces = new List<Shape>();
        for (var i = 1; i <= box.FaceCount; i++)
        {
            using var face = box.Sub(TopoKind.Face, i);
            using var op = Modeling.Transform(face, i == 2 ? Transform3.Translation(new Vec3(0, 0.003, 0)) : Transform3.Identity, copy: true);
            faces.Add(op.Result);
        }

        using var loose = Modeling.Compound(faces);
        var doc = new PartDocument();
        doc.Add(new ImportFeature { SourceName = "box.step", Brep = loose.ToBrep() });
        doc.Rebuild();
        Assert.True(doc.Bodies[0].IsSurface);

        var heal = doc.Add(new HealFeature { Tolerance = 0.01 });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[heal.Id].Status);
        var body = Assert.Single(doc.Bodies);
        Assert.False(body.IsSurface);
        Assert.True(body.Shape.Diagnose(0.01).IsClean);
        Assert.Equal(6000, doc.MassProperties().Volume, 1);

        // Healing a clean part changes nothing and says so.
        doc.Rebuild();
        var again = doc.Add(new HealFeature());
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Warning, doc.Results[again.Id].Status);

        foreach (var f in faces)
        {
            f.Dispose();
        }
    }
}
