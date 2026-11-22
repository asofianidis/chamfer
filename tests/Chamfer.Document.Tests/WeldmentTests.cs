using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class WeldmentTests
{
    [Fact]
    public void RectangularFrameWithMiteredCorners()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(600, 400));
        var sf = doc.Add(new SketchFeature { Sketch = s, Plane = PlaneRef.Top });
        var member = doc.Add(new StructuralMemberFeature { SketchId = sf.Id, Kind = ProfileKind.SquareTube, Size = "40x40x3" });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[member.Id].Status);
        Assert.Equal(4, doc.Bodies.Count);
        Assert.All(doc.Bodies, b => Assert.True(b.Shape.IsValid));

        // Members don't overlap: total volume is the outer frame area minus the inner opening, times the wall section.
        var section = (40 * 40) - (34 * 34);
        var perimeter = 2 * (600 + 400); // centre-line length; mitres trade inside for outside evenly
        Assert.Equal(section * perimeter, doc.MassProperties().Volume, section * perimeter * 0.01);

        var cut = CutList.Build(doc);
        Assert.Equal(2, cut.Count);
        Assert.All(cut, c => Assert.Equal(2, c.Quantity));
        Assert.Contains(cut, c => Math.Abs(c.Length - 640) < 0.5);
        Assert.Contains(cut, c => Math.Abs(c.Length - 440) < 0.5);
        Assert.All(cut, c => Assert.Equal("Tube 40x40x3", c.Description));
    }

    [Fact]
    public void OpenFrameEndsStaySquare()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddLine(new Vec2(0, 0), new Vec2(500, 0));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new StructuralMemberFeature { SketchId = sf.Id, Kind = ProfileKind.Beam, Size = "IPE 100" });
        doc.Rebuild();
        var body = Assert.Single(doc.Bodies);
        Assert.Equal(500, body.Shape.Bounds.Size.X, 1e-6);
        Assert.Equal(100, body.Shape.Bounds.Size.Z, 1e-6); // beam height along the sketch normal
        Assert.Equal("IPE 100", Assert.Single(CutList.Build(doc)).Description);
    }
}
