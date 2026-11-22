using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Serialization;
using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Tests;

public class DragTests
{
    private const double Tol = 1e-6;

    [Fact]
    public void DraggingEndpointKeepsLineHorizontal()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(10, 0));
        sketch.AddConstraint(new HorizontalConstraint(line));

        Assert.True(sketch.Drag(line.End, new Vec2(15, 3)).Converged);
        Assert.Equal(new Vec2(15, 3), line.End.Position);
        Assert.Equal(3, line.Start.Y, Tol);
    }

    [Fact]
    public void DraggingFullyDefinedGeometryDoesNotMoveIt()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(10, 0));
        sketch.AddConstraint(new FixConstraint(line));

        sketch.Drag(line.End, new Vec2(15, 3));
        Assert.Equal(10, line.End.X, Tol);
        Assert.Equal(0, line.End.Y, Tol);
    }

    [Fact]
    public void PointOnFixedCircleSlidesAlongItTowardCursor()
    {
        var sketch = new Sketch();
        var circle = sketch.AddCircle(new Vec2(0, 0), 10);
        var point = sketch.AddPoint(new Vec2(10, 0));
        sketch.AddConstraint(new FixConstraint(circle));
        sketch.AddConstraint(new PointOnCircleConstraint(point, circle));

        Assert.True(sketch.Drag(point, new Vec2(0, 20)).Converged);
        Assert.Equal(10, point.Position.Length, Tol);
        // The nearest point on the circle to the cursor.
        Assert.Equal(0, point.X, 1e-4);
        Assert.Equal(10, point.Y, 1e-4);
    }

    [Fact]
    public void DraggedPointStaysPutWhileFreeGeometryGivesWay()
    {
        var sketch = new Sketch();
        var circle = sketch.AddCircle(new Vec2(0, 0), 10);
        var point = sketch.AddPoint(new Vec2(10, 0));
        sketch.AddConstraint(new PointOnCircleConstraint(point, circle));

        Assert.True(sketch.Drag(point, new Vec2(30, 0)).Converged);
        Assert.Equal(new Vec2(30, 0), point.Position);
        Assert.Equal(Vec2.Distance(point.Position, circle.Center.Position), circle.Radius, Tol);
    }
}

public class SerializationTests
{
    [Fact]
    public void RoundTripPreservesGeometryConstraintsAndAnalysis()
    {
        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(30, 20));
        sketch.AddConstraint(new FixConstraint(sides[0].Start));
        var width = sketch.AddConstraint(new DistanceConstraint(sides[0]) { LabelOffset = new Vec2(0, -5) });
        sketch.AddConstraint(new DistanceConstraint(sides[1]) { IsDriving = false });
        var slot = sketch.AddSlot(new Vec2(5, 10), new Vec2(25, 10), 3);
        sketch.AddConstraint(new AngleConstraint(sides[0], slot.Side1));
        var circle = sketch.AddCircle(new Vec2(15, 30), 4);
        sketch.AddConstraint(new TangentConstraint(sides[2], circle));
        sketch.AddConstraint(new RadialDimensionConstraint(circle, diameter: true));
        Assert.True(sketch.Solve().Converged);
        width.Value = 40;
        Assert.True(sketch.Solve().Converged);

        var json = SketchSerializer.Serialize(sketch);
        var copy = SketchSerializer.Deserialize(json);

        Assert.Equal(sketch.Entities.Count, copy.Entities.Count);
        Assert.Equal(sketch.Constraints.Count, copy.Constraints.Count);
        Assert.Equal(json, SketchSerializer.Serialize(copy));
        Assert.Equal(sketch.Analyze().DegreesOfFreedom, copy.Analyze().DegreesOfFreedom);
        Assert.True(copy.Solve().Converged);

        var copiedWidth = (DistanceConstraint)copy.FindConstraint(width.Id)!;
        Assert.Equal(40, copiedWidth.Value, 1e-9);
        Assert.Equal(new Vec2(0, -5), copiedWidth.LabelOffset);
        var copiedSide = (SketchLine)copy.FindEntity(sides[0].Id)!;
        Assert.Equal(40, copiedSide.Length, 1e-7);

        // New entities get ids that don't collide with loaded ones.
        var added = copy.AddPoint(new Vec2(1, 1));
        Assert.DoesNotContain(copy.AllEntities, e => e != added && e.Id == added.Id);
    }

    [Fact]
    public void ConstructionFlagSurvivesRoundTrip()
    {
        var sketch = new Sketch();
        sketch.AddLine(new Vec2(0, 0), new Vec2(1, 1), construction: true);
        var copy = SketchSerializer.Deserialize(SketchSerializer.Serialize(sketch));
        Assert.True(copy.Entities[0].IsConstruction);
        Assert.Equal(SketchStatus.UnderDefined, copy.Analyze().Status);
    }
}
