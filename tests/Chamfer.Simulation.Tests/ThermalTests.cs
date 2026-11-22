using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Simulation.Tests;

public class ThermalTests
{
    /// <summary>A 100 × 10 × 10 steel bar along X (k = 45 W/m·K).</summary>
    private static (PartDocument Doc, TopoRef Left, TopoRef Right, TopoRef[] Sides) Bar()
    {
        var doc = new PartDocument { Material = Material.Steel };
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 10));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var b = doc.Bodies[0];
        TopoRef Face(Func<Kernel.Occt.FaceInfo, bool> pick) => b.FaceRef(Enumerable.Range(1, b.FaceCount).Single(i => pick(b.Face(i))));
        var sides = Enumerable.Range(1, b.FaceCount).Where(i => Math.Abs(b.Face(i).Normal.X) < 0.5).Select(b.FaceRef).ToArray();
        return (doc, Face(f => f.Normal.X < -0.99), Face(f => f.Normal.X > 0.99), sides);
    }

    [Fact]
    public void ConductionBetweenTwoTemperaturesIsLinear()
    {
        var (doc, left, right, _) = Bar();
        var study = new ThermalStudy { Resolution = 40 };
        study.Loads.Add(new ThermalLoad(left, ThermalLoadKind.Temperature, 100));
        study.Loads.Add(new ThermalLoad(right, ThermalLoadKind.Temperature, 0));
        var r = ThermalSolver.Solve(doc, study, TestContext.Current.CancellationToken);
        Assert.Equal(100, r.MaxTemperature, 1e-6);
        Assert.Equal(0, r.MinTemperature, 1e-6);
        Assert.Equal(50, r.TemperatureAt(new Vec3(50, 5, 5)), 1e-6);
        Assert.Equal(75, r.TemperatureAt(new Vec3(25, 5, 5)), 1e-6);

        // Q = k A ΔT / L = 0.045 W/(mm·K) × 100 mm² × 100 K / 100 mm.
        Assert.Equal(4.5, r.HeatThroughFixedFaces, 1e-6);
        var plot = r.AsPlot();
        Assert.Equal(0, plot.MinStress, 1e-6);
        Assert.Equal(100, plot.MaxStress, 1e-6);
    }

    [Fact]
    public void HeatPowerAndConvection()
    {
        var (doc, left, right, sides) = Bar();
        var study = new ThermalStudy { Resolution = 40 };
        study.Loads.Add(new ThermalLoad(left, ThermalLoadKind.Temperature, 20));
        study.Loads.Add(new ThermalLoad(right, ThermalLoadKind.HeatPower, 10));
        var r = ThermalSolver.Solve(doc, study, TestContext.Current.CancellationToken);

        // ΔT = Q L / (k A) = 10 × 100 / (0.045 × 100) ≈ 222 K.
        Assert.Equal(20 + (10 * 100 / (0.045 * 100)), r.MaxTemperature, 1e-3);
        Assert.Equal(10, r.HeatThroughFixedFaces, 1e-6);

        // A fin: hot root, convection on the sides cools it toward the ambient air.
        var fin = new ThermalStudy { Resolution = 40 };
        fin.Loads.Add(new ThermalLoad(left, ThermalLoadKind.Temperature, 100));
        foreach (var side in sides)
        {
            fin.Loads.Add(new ThermalLoad(side, ThermalLoadKind.Convection, 50, Ambient: 20));
        }

        var rf = ThermalSolver.Solve(doc, fin, TestContext.Current.CancellationToken);
        var root = rf.TemperatureAt(new Vec3(1, 5, 5));
        var mid = rf.TemperatureAt(new Vec3(50, 5, 5));
        var tip = rf.TemperatureAt(new Vec3(99, 5, 5));
        Assert.True(root > mid && mid > tip && tip > 20, $"{root} {mid} {tip}");

        // Insulated-tip fin: θ(x)/θ0 = cosh(m (L − x)) / cosh(m L), m = √(h P / (k A)).
        var m = Math.Sqrt(50e-6 * 40 / (0.045 * 100));
        var expectedTip = 20 + (80 / Math.Cosh(m * 100));
        Assert.InRange(tip, expectedTip - 3, expectedTip + 3);

        Assert.Throws<InvalidOperationException>(() => ThermalSolver.Solve(doc, new ThermalStudy { Loads = { new ThermalLoad(right, ThermalLoadKind.HeatPower, 1) } }, TestContext.Current.CancellationToken));
    }
}
