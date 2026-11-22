using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class LibraryFeatureTests
{
    private static (PartDocument Doc, TopoRef Top) Block(double size)
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCenterRectangle(Vec2.Zero, new Vec2(size / 2, size / 2));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
        doc.Rebuild();
        var b = doc.Bodies[0];
        return (doc, b.FaceRef(Enumerable.Range(1, b.FaceCount).Single(i => b.Face(i).Normal.Z > 0.99)));
    }

    [Fact]
    public void SaveAndInsertOnAnotherFace()
    {
        var (source, top) = Block(40);
        var pocket = new Sketch();
        pocket.AddCircle(Vec2.Zero, 5);
        var sketch = source.Add(new SketchFeature { Plane = new PlaneRef(Face: top), Sketch = pocket });
        var cut = source.Add(new ExtrudeFeature { SketchId = sketch.Id, IsCut = true, Depth = 5 });
        source.Rebuild();
        var json = LibraryFeatures.Save(source, [sketch, cut]);
        Assert.Throws<InvalidOperationException>(() => LibraryFeatures.Save(source, [cut])); // the sketch must come along

        var (target, targetTop) = Block(60);
        var before = target.MassProperties().Volume;
        var added = LibraryFeatures.Insert(target, json, new PlaneRef(Face: targetTop));
        Assert.Equal(2, added.Count);
        Assert.Equal(added[0].Id, ((ExtrudeFeature)added[1]).SketchId); // references follow the new ids
        target.Rebuild();
        Assert.All(added, f => Assert.Equal(FeatureStatus.Ok, target.Results[f.Id].Status));
        Assert.Equal(before - (Math.PI * 25 * 5), target.MassProperties().Volume, 1e-3);

        // Inserting twice gives independent copies.
        var again = LibraryFeatures.Insert(target, json, new PlaneRef(Face: targetTop));
        Assert.DoesNotContain(again, f => added.Any(a => a.Id == f.Id));
    }
}
