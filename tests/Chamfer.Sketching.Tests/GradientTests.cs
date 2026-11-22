using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Tests;

/// <summary>Checks every constraint's analytic Jacobian against central finite differences.</summary>
public class GradientTests
{
    public static TheoryData<string> ConstraintNames => [.. BuildAll(out _).Keys];

    [Theory]
    [MemberData(nameof(ConstraintNames))]
    public void AnalyticJacobianMatchesFiniteDifferences(string name)
    {
        var all = BuildAll(out var sketch);
        var source = all[name];
        var parameters = sketch.AllParams();
        AssertGradient(source, parameters);
    }

    [Fact]
    public void ArcBuiltInEquationGradientIsCorrect()
    {
        var sketch = new Sketch();
        var arc = sketch.AddArc(new Vec2(1, 2), new Vec2(4, 2.5), new Vec2(-1, 4.5));
        AssertGradient(arc, sketch.AllParams());
    }

    internal static void AssertGradient(IEquationSource source, IReadOnlyList<Param> parameters)
    {
        var rows = source.EquationCount;
        var cols = parameters.Count;
        for (var i = 0; i < cols; i++)
        {
            parameters[i].Index = i;
        }

        try
        {
            var analytic = new DenseSystem(rows, cols);
            analytic.Evaluate([source]);

            const double h = 1e-6;
            var plus = new DenseSystem(rows, cols) { WithJacobian = false };
            var minus = new DenseSystem(rows, cols) { WithJacobian = false };
            for (var c = 0; c < cols; c++)
            {
                var p = parameters[c];
                var v = p.Value;
                p.Value = v + h;
                plus.Evaluate([source]);
                p.Value = v - h;
                minus.Evaluate([source]);
                p.Value = v;
                for (var r = 0; r < rows; r++)
                {
                    var numeric = (plus.Residuals[r] - minus.Residuals[r]) / (2 * h);
                    var exact = analytic.Jacobian[r * cols + c];
                    Assert.True(
                        Math.Abs(numeric - exact) < 1e-5 * Math.Max(1, Math.Abs(numeric)),
                        $"{source}: row {r}, param {c}: analytic {exact}, numeric {numeric}");
                }
            }
        }
        finally
        {
            foreach (var p in parameters)
            {
                p.Index = -1;
            }
        }
    }

    /// <summary>One instance of every constraint, on deliberately unsatisfied, non-degenerate geometry.</summary>
    private static Dictionary<string, IEquationSource> BuildAll(out Sketch sketch)
    {
        sketch = new Sketch();
        var l1 = sketch.AddLine(new Vec2(0.3, -0.2), new Vec2(5.1, 1.7));
        var l2 = sketch.AddLine(new Vec2(-1.4, 3.3), new Vec2(2.2, 6.9));
        var p = sketch.AddPoint(new Vec2(2.7, 4.1));
        var q = sketch.AddPoint(new Vec2(-3.2, 0.9));
        var circle = sketch.AddCircle(new Vec2(7.5, 2.5), 1.9);
        var arc = sketch.AddArc(new Vec2(-4, -3), new Vec2(-1.5, -2.6), new Vec2(-4.4, -0.8));
        var arc2 = sketch.AddArc(new Vec2(1, -5), new Vec2(2.8, -4.1), new Vec2(0.2, -3.2));

        // Arc meeting a line at an end point, to exercise the end-point tangent forms.
        var tl = sketch.AddLine(new Vec2(10, 0), new Vec2(14, 0.4));
        var ta = sketch.AddArc(new Vec2(14.1, 3), new Vec2(14, 0.4), new Vec2(16.8, 2.4));
        var tb = sketch.AddArc(new Vec2(19.5, 2.9), new Vec2(16.8, 2.4), new Vec2(19.6, 0.3));

        return new Dictionary<string, IEquationSource>
        {
            ["Coincident"] = new CoincidentConstraint(p, q),
            ["HorizontalLine"] = new HorizontalConstraint(l1),
            ["VerticalPoints"] = new VerticalConstraint(p, q),
            ["Parallel"] = new ParallelConstraint(l1, l2),
            ["Perpendicular"] = new PerpendicularConstraint(l1, l2),
            ["Collinear"] = new CollinearConstraint(l1, l2),
            ["PointOnLine"] = new PointOnLineConstraint(p, l1),
            ["PointOnCircle"] = new PointOnCircleConstraint(p, circle),
            ["PointOnArc"] = new PointOnCircleConstraint(q, arc),
            ["Midpoint"] = new MidpointConstraint(p, l2),
            ["Symmetric"] = new SymmetricConstraint(p, q, l1),
            ["TangentLineCircle"] = new TangentConstraint(l1, circle),
            ["TangentLineArc"] = new TangentConstraint(l2, arc),
            ["TangentCircleArcExternal"] = new TangentConstraint(circle, arc),
            ["TangentArcArc"] = new TangentConstraint(arc, arc2),
            ["TangentLineArcAtEndpoint"] = new TangentConstraint(tl, ta),
            ["TangentArcArcAtEndpoint"] = new TangentConstraint(ta, tb),
            ["Concentric"] = new ConcentricConstraint(circle, arc),
            ["EqualLines"] = new EqualConstraint(l1, l2),
            ["EqualRadii"] = new EqualConstraint(circle, arc),
            ["Coradial"] = new CoradialConstraint(arc, arc2),
            ["FixLine"] = Perturbed(new FixConstraint(l1), l1),
            ["FixArc"] = Perturbed(new FixConstraint(arc), arc),
            ["Distance"] = new DistanceConstraint(p, q, 3),
            ["LineLength"] = new DistanceConstraint(l1, 2),
            ["HorizontalDistance"] = new AxisDistanceConstraint(p, q, vertical: false, 1.5),
            ["VerticalDistance"] = new AxisDistanceConstraint(p, q, vertical: true, 1.5),
            ["PointLineDistance"] = new PointLineDistanceConstraint(p, l1, 1),
            ["Angle"] = new AngleConstraint(l1, l2, 0.4),
            ["Radius"] = new RadialDimensionConstraint(arc, diameter: false, 1),
            ["Diameter"] = new RadialDimensionConstraint(circle, diameter: true, 1),
        };
    }

    private static FixConstraint Perturbed(FixConstraint fix, SketchEntity entity)
    {
        foreach (var param in entity.Params)
        {
            param.Value += 0.37;
        }

        return fix;
    }
}
