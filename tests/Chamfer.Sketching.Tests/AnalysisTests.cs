using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Tests;

public class AnalysisTests
{
    [Fact]
    public void FreeEntitiesHaveExpectedDegreesOfFreedom()
    {
        Assert.Equal(2, DofOf(s => s.AddPoint(new Vec2(1, 2))));
        Assert.Equal(4, DofOf(s => s.AddLine(new Vec2(0, 0), new Vec2(1, 2))));
        Assert.Equal(3, DofOf(s => s.AddCircle(new Vec2(0, 0), 3)));
        Assert.Equal(5, DofOf(s => s.AddArc(new Vec2(0, 0), new Vec2(3, 0), new Vec2(0, 3))));
    }

    [Fact]
    public void RectangleNeedsPositionAndSize()
    {
        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(10, 5));
        var analysis = sketch.Analyze();
        Assert.Equal(4, analysis.DegreesOfFreedom);
        Assert.Equal(SketchStatus.UnderDefined, analysis.Status);

        sketch.AddConstraint(new FixConstraint(sides[0].Start));
        sketch.AddConstraint(new DistanceConstraint(sides[0]));
        Assert.Equal(1, sketch.Analyze().DegreesOfFreedom);
        Assert.True(sketch.Analyze().IsFullyDefined(sides[0]));
        Assert.False(sketch.Analyze().IsFullyDefined(sides[1]));

        sketch.AddConstraint(new DistanceConstraint(sides[1]));
        analysis = sketch.Analyze();
        Assert.Equal(0, analysis.DegreesOfFreedom);
        Assert.Equal(SketchStatus.FullyDefined, analysis.Status);
        Assert.All(sides, s => Assert.True(analysis.IsFullyDefined(s)));
    }

    [Fact]
    public void RedundantRelationIsReportedAsOverDefined()
    {
        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(10, 5));
        // Opposite sides of an axis-aligned rectangle are already parallel.
        var redundant = sketch.AddConstraint(new ParallelConstraint(sides[0], sides[2]));

        var analysis = sketch.Analyze();
        Assert.Equal(SketchStatus.OverDefined, analysis.Status);
        Assert.True(analysis.IsRedundant(redundant));
        Assert.Single(analysis.RedundantConstraints);
        Assert.Equal(4, analysis.DegreesOfFreedom);
    }

    [Fact]
    public void FixedArcIsFullyDefinedNotOverDefined()
    {
        var sketch = new Sketch();
        var arc = sketch.AddArc(new Vec2(0, 0), new Vec2(3, 0), new Vec2(0, 3));
        sketch.AddConstraint(new FixConstraint(arc));
        Assert.Equal(SketchStatus.FullyDefined, sketch.Analyze().Status);
    }

    [Fact]
    public void FullyDimensionedSlotIsFullyDefined()
    {
        var sketch = new Sketch();
        var slot = sketch.AddSlot(new Vec2(0, 0), new Vec2(20, 0), 4);
        Assert.True(sketch.Solve().Converged);
        var analysis = sketch.Analyze();
        Assert.Empty(analysis.RedundantConstraints);
        // Centerline (4) + radius (1).
        Assert.Equal(5, analysis.DegreesOfFreedom);

        sketch.AddConstraint(new FixConstraint(slot.Centerline));
        sketch.AddConstraint(new RadialDimensionConstraint(slot.Cap1, diameter: false));
        Assert.Equal(SketchStatus.FullyDefined, sketch.Analyze().Status);
    }

    [Fact]
    public void PolygonHasCenterRadiusAndRotationFree()
    {
        var sketch = new Sketch();
        sketch.AddPolygon(new Vec2(0, 0), new Vec2(10, 0), 6);
        var analysis = sketch.Analyze();
        Assert.Empty(analysis.RedundantConstraints);
        Assert.Equal(4, analysis.DegreesOfFreedom);
    }

    [Fact]
    public void CenterRectangleHasCenterAndSizeFree()
    {
        var sketch = new Sketch();
        sketch.AddCenterRectangle(new Vec2(0, 0), new Vec2(10, 5));
        var analysis = sketch.Analyze();
        Assert.Empty(analysis.RedundantConstraints);
        Assert.Equal(4, analysis.DegreesOfFreedom);
    }

    [Fact]
    public void ThreePointRectangleCanRotate()
    {
        var sketch = new Sketch();
        sketch.AddThreePointRectangle(new Vec2(0, 0), new Vec2(10, 5), new Vec2(8, 12));
        var analysis = sketch.Analyze();
        Assert.Empty(analysis.RedundantConstraints);
        // Position (2), rotation (1), width and height (2).
        Assert.Equal(5, analysis.DegreesOfFreedom);
    }

    private static int DofOf(Action<Sketch> build)
    {
        var sketch = new Sketch();
        build(sketch);
        return sketch.Analyze().DegreesOfFreedom;
    }
}

public class OriginTests
{
    [Fact]
    public void OriginIsFixedAndCanBeReferenced()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(1, 1), new Vec2(10, 3));
        sketch.AddConstraint(new CoincidentConstraint(line.Start, sketch.Origin));
        sketch.AddConstraint(new HorizontalConstraint(line));
        sketch.AddConstraint(new DistanceConstraint(line, 12));

        Assert.True(sketch.Solve().Converged);
        Assert.Equal(Vec2.Zero, sketch.Origin.Position);
        Assert.Equal(0, line.Start.Position.Length, 1e-9);
        Assert.Equal(Solving.SketchStatus.FullyDefined, sketch.Analyze().Status);
        Assert.False(sketch.Remove(sketch.Origin));
        Assert.Same(sketch.Origin, sketch.FindEntity(Sketch.OriginId));

        var copy = Serialization.SketchSerializer.Deserialize(Serialization.SketchSerializer.Serialize(sketch));
        Assert.Equal(Solving.SketchStatus.FullyDefined, copy.Analyze().Status);
    }
}
