using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Simulation.Tests;

public class BucklingTests
{
    [Fact]
    public void EulerColumn()
    {
        // 100 long (X), 10 × 10 steel, clamped at x = 0, 1 kN compression at x = 100.
        var doc = new PartDocument { Material = Material.Steel };
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 10));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var b = doc.Bodies[0];
        TopoRef Face(Func<Kernel.Occt.FaceInfo, bool> pick) => b.FaceRef(Enumerable.Range(1, b.FaceCount).Single(i => pick(b.Face(i))));
        var study = new StaticStudy { Resolution = 40 };
        study.Fixtures.Add(Face(f => f.Normal.X < -0.99));
        study.Loads.Add(new Load(Face(f => f.Normal.X > 0.99), LoadKind.Force, 1000, -Vec3.UnitX));
        var r = BucklingSolver.Solve(doc, study, 2, TestContext.Current.CancellationToken);

        // Fixed–free Euler load: P = π² E I / (4 L²), I = 10⁴ / 12.
        var critical = Math.PI * Math.PI * 210000 * (10000.0 / 12) / (4 * 100 * 100);
        Assert.Equal(2, r.Modes.Count);
        Assert.InRange(r.Modes[0].LoadFactor, critical / 1000 * 0.85, critical / 1000 * 1.15);
        Assert.Equal(r.Modes[0].LoadFactor, r.Modes[1].LoadFactor, r.Modes[0].LoadFactor * 0.02); // square section: two equal modes

        // The mode swings the free end sideways.
        var tip = r.Modes[0].Shape.DisplacementAt(new Vec3(100, 5, 5));
        Assert.True(Math.Sqrt((tip.Y * tip.Y) + (tip.Z * tip.Z)) > 0.9, $"tip {tip}");
        Assert.True(r.Static.MaxStress > 0);

        // Pulling doesn't buckle.
        var tension = new StaticStudy { Resolution = 30 };
        tension.Fixtures.Add(study.Fixtures[0]);
        tension.Loads.Add(study.Loads[0] with { Direction = Vec3.UnitX });
        Assert.Empty(BucklingSolver.Solve(doc, tension, 1, TestContext.Current.CancellationToken).Modes);
    }
}
