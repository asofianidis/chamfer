using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Kernel.Tests;

public class HealingTests
{
    [Fact]
    public void LooseFacesAreSewnIntoASolid()
    {
        using var box = Modeling.Box(10, 20, 30);
        Assert.True(box.Diagnose().IsClean);

        // Six faces copied independently, one nudged by a hair: no shared edges, a tiny gap.
        var faces = new List<Shape>();
        for (var i = 1; i <= box.FaceCount; i++)
        {
            using var face = box.Sub(TopoKind.Face, i);
            var t = i == 1 ? Transform3.Translation(new Vec3(0.0002, 0, 0)) : Transform3.Identity;
            using var op = Modeling.Transform(face, t, copy: true);
            faces.Add(op.Result);
        }

        using var loose = Modeling.Compound(faces);
        var before = loose.Diagnose();
        Assert.False(before.IsClean);
        Assert.Equal(0, before.Solids);
        Assert.Equal(6, before.Faces);
        Assert.Equal(8, before.FreeEdges); // coincident copies count as joined; the nudged face and its neighbours gap

        using var healed = loose.Heal(1e-3);
        var after = healed.Diagnose();
        Assert.True(after.IsClean, after.ToString());
        Assert.Equal(1, after.Solids);
        Assert.Equal(6000, healed.MassProperties.Volume, 0.5);

        foreach (var f in faces)
        {
            f.Dispose();
        }
    }
}
