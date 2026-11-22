using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Simulation.Tests;

public class StaticTests
{
    /// <summary>A 100 × 10 × 10 steel bar along X.</summary>
    private static PartDocument Bar()
    {
        var doc = new PartDocument { Material = Material.Steel };
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 10));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Rebuild();
        return doc;
    }

    private static TopoRef FaceWhere(PartDocument doc, Func<Kernel.Occt.FaceInfo, bool> pick)
    {
        var b = doc.Bodies[0];
        return b.FaceRef(Enumerable.Range(1, b.FaceCount).Single(i => pick(b.Face(i))));
    }

    [Fact]
    public void ElementStiffnessIsSymmetricWithRigidBodyModes()
    {
        var k = Hex8.Stiffness(1000, 0.3);
        for (var i = 0; i < 24; i++)
        {
            for (var j = 0; j < 24; j++)
            {
                Assert.Equal(k[i, j], k[j, i], 1e-6);
            }
        }

        // A rigid translation produces no forces.
        for (var dof = 0; dof < 3; dof++)
        {
            for (var r = 0; r < 24; r++)
            {
                double f = 0;
                for (var c = dof; c < 24; c += 3)
                {
                    f += k[r, c];
                }

                Assert.Equal(0, f, 1e-6);
            }
        }
    }

    [Fact]
    public void AxialBarMatchesHookesLaw()
    {
        var doc = Bar();
        var study = new StaticStudy { Resolution = 20 };
        study.Fixtures.Add(FaceWhere(doc, f => f.Normal.X < -0.99));
        study.Loads.Add(new Load(FaceWhere(doc, f => f.Normal.X > 0.99), LoadKind.Force, 10000, Vec3.UnitX));
        var result = StaticSolver.Solve(doc, study, TestContext.Current.CancellationToken);

        // δ = F L / (E A) = 10000 × 100 / (210000 × 100) ≈ 0.0476 mm; σ = F / A = 100 MPa.
        Assert.Equal(10000.0 * 100 / (210000 * 100), result.MaxDisplacement, 0.01);
        var mid = result.StressAt(new Vec3(50, 5, 5));
        Assert.Equal(100.0, mid, 10.0);
        Assert.Equal(220 / result.MaxStress, result.FactorOfSafety, 1e-9);
    }

    [Fact]
    public void CantileverDeflectionMatchesBeamTheory()
    {
        var doc = Bar();
        var study = new StaticStudy { Resolution = 40 };
        study.Fixtures.Add(FaceWhere(doc, f => f.Normal.X < -0.99));
        study.Loads.Add(new Load(FaceWhere(doc, f => f.Normal.X > 0.99), LoadKind.Force, 100, -Vec3.UnitY));
        var result = StaticSolver.Solve(doc, study, TestContext.Current.CancellationToken);

        // δ = F L³ / (3 E I), I = b h³ / 12.
        var inertia = 10.0 * 1000 / 12;
        var expected = 100.0 * 1e6 / (3 * 210000 * inertia);
        var tip = result.DisplacementAt(new Vec3(100, 5, 5));
        Assert.True(tip.Y < 0, "the tip deflects downward");
        Assert.InRange(-tip.Y, expected * 0.85, expected * 1.15);

        // Bending stress near the root: σ = M c / I = 100 × 100 × 5 / I = 60 MPa at the surface.
        Assert.InRange(result.MaxStress, 40, 90);
        Assert.True(result.Iterations > 0);
    }

    [Fact]
    public void StudiesNeedFixtures()
    {
        var doc = Bar();
        var study = new StaticStudy();
        Assert.Throws<InvalidOperationException>(() => StaticSolver.Solve(doc, study, TestContext.Current.CancellationToken));
    }
}
