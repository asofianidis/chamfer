using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Simulation.Tests;

public class FrequencyTests
{
    [Fact]
    public void CantileverModesMatchBeamTheory()
    {
        // 200 long (X), 20 wide (Y), 10 thick (Z), steel, clamped at x = 0.
        var doc = new PartDocument { Material = Material.Steel };
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(200, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var study = new FrequencyStudy { Resolution = 40, Modes = 3 };
        study.Fixtures.Add(body.FaceRef(Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.X < -0.99)));
        var result = FrequencySolver.Solve(doc, study, TestContext.Current.CancellationToken);
        Assert.Equal(3, result.Modes.Count);

        // f = (βL)² / (2π) √(EI / (ρ A L⁴)), βL = 1.875 (first), 4.694 (second bending mode).
        static double Bending(double betaL, double width, double thickness)
        {
            var inertia = width * Math.Pow(thickness, 3) / 12;
            return betaL * betaL / (2 * Math.PI) * Math.Sqrt(210000 * inertia / (7800e-12 * 200 * Math.Pow(200, 4)));
        }

        var f1 = Bending(1.8751, 20, 10);   // ~210 Hz, flapping through the 10 mm thickness
        var f2 = Bending(1.8751, 10, 20);   // ~419 Hz, sideways through the 20 mm width
        var f3 = Bending(4.6941, 20, 10);   // ~1313 Hz, second flapping mode
        Assert.InRange(result.Modes[0].Frequency, f1 * 0.88, f1 * 1.12);
        Assert.InRange(result.Modes[1].Frequency, f2 * 0.88, f2 * 1.12);
        Assert.InRange(result.Modes[2].Frequency, f3 * 0.85, f3 * 1.15);

        // The first mode flaps in Z: the tip moves up/down, not sideways, and the root stays put.
        var tip = result.Modes[0].Shape.DisplacementAt(new Vec3(200, 10, 5));
        Assert.True(Math.Abs(tip.Z) > 0.9, $"tip {tip}");
        Assert.True(Math.Abs(tip.Y) < 0.1);
        Assert.True(result.Modes[0].Shape.DisplacementAt(new Vec3(0, 10, 5)).Length < 1e-9);
        var side = result.Modes[1].Shape.DisplacementAt(new Vec3(200, 10, 5));
        Assert.True(Math.Abs(side.Y) > 0.9, $"second mode tip {side}");
    }

    [Fact]
    public void EigenSolverOrdersValuesAndNormalisesVectors()
    {
        var a = new double[,] { { 6, 2, 0 }, { 2, 5, 1 }, { 0, 1, 4 } };
        var b = new double[,] { { 2, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
        var (values, vectors) = FrequencySolver.GeneralizedEigen(a, b);
        Assert.True(values[0] <= values[1] && values[1] <= values[2]);
        for (var k = 0; k < 3; k++)
        {
            // A φ = λ B φ and φᵀ B φ = 1.
            double norm = 0;
            for (var i = 0; i < 3; i++)
            {
                double av = 0, bv = 0;
                for (var j = 0; j < 3; j++)
                {
                    av += a[i, j] * vectors[j, k];
                    bv += b[i, j] * vectors[j, k];
                }

                Assert.Equal(av, values[k] * bv, 1e-9);
                norm += vectors[i, k] * bv;
            }

            Assert.Equal(1, norm, 1e-9);
        }
    }
}
