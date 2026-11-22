using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Tests;

public class SolverTests
{
    private const double Tol = 1e-7;

    [Fact]
    public void HorizontalAndVerticalRelationsAreSatisfied()
    {
        var sketch = new Sketch();
        var h = sketch.AddLine(new Vec2(0, 0), new Vec2(10, 1));
        var v = sketch.AddLine(new Vec2(20, 0), new Vec2(21, 10));
        sketch.AddConstraint(new HorizontalConstraint(h));
        sketch.AddConstraint(new VerticalConstraint(v));

        Assert.True(sketch.Solve().Converged);
        Assert.Equal(h.Start.Y, h.End.Y, Tol);
        Assert.Equal(v.Start.X, v.End.X, Tol);
    }

    [Fact]
    public void SolvingMovesUnconstrainedGeometryAsLittleAsPossible()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(10, 1));
        var bystander = sketch.AddLine(new Vec2(50, 50), new Vec2(60, 70));
        sketch.AddConstraint(new HorizontalConstraint(line));

        Assert.True(sketch.Solve().Converged);
        // The minimum-norm step splits the correction evenly between the two end points.
        Assert.Equal(0.5, line.Start.Y, Tol);
        Assert.Equal(0.5, line.End.Y, Tol);
        Assert.Equal(new Vec2(50, 50), bystander.Start.Position);
        Assert.Equal(new Vec2(60, 70), bystander.End.Position);
    }

    [Fact]
    public void DimensionedRectangleReachesTargetSize()
    {
        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(1, 1), new Vec2(9, 4));
        sketch.AddConstraint(new DistanceConstraint(sides[0], 25));
        sketch.AddConstraint(new DistanceConstraint(sides[1], 12.5));

        Assert.True(sketch.Solve().Converged);
        Assert.Equal(25, sides[0].Length, Tol);
        Assert.Equal(25, sides[2].Length, Tol);
        Assert.Equal(12.5, sides[1].Length, Tol);
        Assert.Equal(12.5, sides[3].Length, Tol);
    }

    [Fact]
    public void AngleDimensionRotatesLine()
    {
        var sketch = new Sketch();
        var baseLine = sketch.AddLine(new Vec2(0, 0), new Vec2(10, 0));
        var arm = sketch.AddLine(new Vec2(0, 0), new Vec2(8, 3));
        sketch.AddConstraint(new FixConstraint(baseLine));
        sketch.AddConstraint(new CoincidentConstraint(baseLine.Start, arm.Start));
        sketch.AddConstraint(new AngleConstraint(baseLine, arm, Math.PI / 3));

        Assert.True(sketch.Solve().Converged);
        Assert.Equal(60, arm.Direction.Angle * 180 / Math.PI, 1e-6);
    }

    [Fact]
    public void TangentLineTouchesCircle()
    {
        var sketch = new Sketch();
        var circle = sketch.AddCircle(new Vec2(0, 0), 5);
        var line = sketch.AddLine(new Vec2(-10, 7), new Vec2(10, 6));
        sketch.AddConstraint(new FixConstraint(circle));
        sketch.AddConstraint(new TangentConstraint(line, circle));

        Assert.True(sketch.Solve().Converged);
        var distance = Math.Abs(Vec2.Cross(line.Direction.Normalized(), circle.Center.Position - line.Start.Position));
        Assert.Equal(5, distance, Tol);
    }

    [Fact]
    public void TangentAtArcEndpointMakesLinePerpendicularToRadius()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(10, 0));
        var arc = sketch.AddArc(new Vec2(10.5, 5), new Vec2(10, 0), new Vec2(15, 5));
        sketch.AddConstraint(new CoincidentConstraint(line.End, arc.Start));
        var tangent = sketch.AddConstraint(new TangentConstraint(line, arc));

        Assert.Same(arc.Start, tangent.ContactPointEntity);
        Assert.True(sketch.Solve().Converged);
        Assert.Equal(0, Vec2.Dot(line.Direction.Normalized(), (arc.Start.Position - arc.Center.Position).Normalized()), Tol);
    }

    [Fact]
    public void SymmetricPointsMirrorAcrossAxis()
    {
        var sketch = new Sketch();
        var axis = sketch.AddLine(new Vec2(0, -5), new Vec2(0, 5), construction: true);
        var a = sketch.AddPoint(new Vec2(-3, 1));
        var b = sketch.AddPoint(new Vec2(4, 2));
        sketch.AddConstraint(new FixConstraint(axis));
        sketch.AddConstraint(new SymmetricConstraint(a, b, axis));

        Assert.True(sketch.Solve().Converged);
        Assert.Equal(-a.X, b.X, Tol);
        Assert.Equal(a.Y, b.Y, Tol);
    }

    [Fact]
    public void ConflictingConstraintIsRejectedAndGeometryRestored()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(10, 0));
        sketch.AddConstraint(new HorizontalConstraint(line));
        sketch.AddConstraint(new DistanceConstraint(line, 10));
        Assert.True(sketch.Solve().Converged);
        var before = sketch.TakeSnapshot();

        // A horizontal 10 mm line cannot also be vertical.
        Assert.False(sketch.TryAddConstraint(new VerticalConstraint(line)));
        Assert.Equal(2, sketch.Constraints.Count);
        Assert.All(before, kv => Assert.Equal(kv.Value, kv.Key.Value));
        Assert.False(sketch.LastSolveFailed);
    }

    [Fact]
    public void FailedSolveLeavesGeometryUnchanged()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(10, 0));
        sketch.AddConstraint(new FixConstraint(line));
        sketch.AddConstraint(new DistanceConstraint(line, 20));

        Assert.False(sketch.Solve().Converged);
        Assert.True(sketch.LastSolveFailed);
        Assert.Equal(new Vec2(10, 0), line.End.Position);
        Assert.Equal(SketchStatus.OverDefined, sketch.Analyze().Status);
    }

    [Fact]
    public void ChangingDimensionValueResizesGeometry()
    {
        var sketch = new Sketch();
        var circle = sketch.AddCircle(new Vec2(0, 0), 5);
        var dia = sketch.AddConstraint(new RadialDimensionConstraint(circle, diameter: true));
        Assert.Equal(10, dia.Value, Tol);

        dia.Value = 30;
        Assert.True(sketch.Solve().Converged);
        Assert.Equal(15, circle.Radius, Tol);
    }

    [Fact]
    public void DrivenDimensionMeasuresWithoutConstraining()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(3, 4));
        var dim = sketch.AddConstraint(new DistanceConstraint(line) { IsDriving = false });

        Assert.Equal(5, dim.Value, Tol);
        line.End.Position = new Vec2(6, 8);
        Assert.Equal(10, dim.Value, Tol);
        Assert.Equal(4, sketch.Analyze().DegreesOfFreedom);
    }

    [Fact]
    public void RemovingEntityRemovesItsConstraints()
    {
        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(10, 5));
        Assert.Equal(8, sketch.Constraints.Count);

        sketch.Remove(sides[0]);

        Assert.Equal(3, sketch.Entities.Count);
        // Gone: bottom's horizontal + its two corner coincidences.
        Assert.Equal(5, sketch.Constraints.Count);
        Assert.Null(sketch.FindEntity(sides[0].Start.Id));
    }

    [Fact]
    public void ThreePointArcPassesThroughMiddlePoint()
    {
        var a = new Vec2(10, 0);
        var mid = new Vec2(0, 10);
        var b = new Vec2(-10, 0);
        var arc = SketchArc.ThroughThreePoints(a, mid, b)!;
        Assert.Equal(10, arc.Radius, Tol);
        Assert.True(arc.ContainsAngle((mid - arc.Center.Position).Angle));

        var flipped = SketchArc.ThroughThreePoints(a, new Vec2(0, -10), b)!;
        Assert.True(flipped.ContainsAngle(-Math.PI / 2));
        Assert.Null(SketchArc.ThroughThreePoints(a, new Vec2(0, 0), b));
    }
}
