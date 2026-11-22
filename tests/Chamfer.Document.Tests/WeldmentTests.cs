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

    [Fact]
    public void EndCapsAndGussets()
    {
        // An L of two 40x40x3 tubes: one joint (gusset), two free ends (caps).
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddLine(new Vec2(0, 0), new Vec2(300, 0));
        s.AddLine(new Vec2(0, 0), new Vec2(0, 200));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        var member = (StructuralMemberFeature)doc.Add(new StructuralMemberFeature
        {
            SketchId = sf.Id, Size = "40x40x3", EndCaps = true, EndCapThickness = 4, Gussets = true, GussetSize = 50, GussetThickness = 6,
        });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[member.Id].Status);
        Assert.Equal(2 + 2 + 1, doc.Bodies.Count);
        var caps = doc.Bodies.Where(b => b.Name.StartsWith("End cap", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, caps.Count);
        Assert.All(caps, c => Assert.Equal(40 * 40 * 4, c.Shape.MassProperties.Volume, 1e-6));
        Assert.Contains(caps, c => Math.Abs(c.Shape.Bounds.Max.X - 304) < 1e-6); // beyond the free end at x = 300

        var gusset = doc.Bodies.Single(b => b.Name.StartsWith("Gusset", StringComparison.Ordinal));
        Assert.Equal(50 * 50 / 2.0 * 6, gusset.Shape.MassProperties.Volume, 1e-6);
        Assert.Equal(20, gusset.Shape.Bounds.Min.X, 1e-6); // starts at the tubes' inside faces
        Assert.Equal(20, gusset.Shape.Bounds.Min.Y, 1e-6);
        Assert.Equal(6, gusset.Shape.Bounds.Size.Z, 1e-6);

        // Bodies don't overlap the tubes, and everything lands in the cut list.
        var tubes = doc.Bodies.Where(b => b.Name.StartsWith("Tube", StringComparison.Ordinal)).ToList();
        foreach (var extra in caps.Append(gusset))
        {
            foreach (var t in tubes)
            {
                using var common = Kernel.Occt.Modeling.Boolean(extra.Shape, t.Shape, Kernel.Occt.BooleanKind.Common);
                Assert.True(common.Result.MassProperties.Volume < 1e-6);
            }
        }

        var cut = CutList.Build(doc);
        Assert.Contains(cut, c => c.Description.StartsWith("End cap", StringComparison.Ordinal) && c.Quantity == 2);
        Assert.Contains(cut, c => c.Description.StartsWith("Gusset", StringComparison.Ordinal) && c.Quantity == 1);
    }
}
