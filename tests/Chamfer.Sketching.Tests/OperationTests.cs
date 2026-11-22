using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Operations;

namespace Chamfer.Sketching.Tests;

public class OperationTests
{
    private const double Tol = 1e-7;

    [Fact]
    public void LineIntersections()
    {
        var a = new SketchLine(new Vec2(0, 0), new Vec2(10, 10));
        var b = new SketchLine(new Vec2(0, 10), new Vec2(10, 0));
        var p = Assert.Single(Curves.Intersections(a, b));
        Assert.Equal(5, p.X, Tol);
        Assert.Equal(5, p.Y, Tol);

        var c = new SketchCircle(new Vec2(5, 5), 2);
        Assert.Equal(2, Curves.Intersections(a, c).Count());
        var arc = new SketchArc(new Vec2(5, 5), new Vec2(7, 5), new Vec2(3, 5)); // upper half
        var hit = Assert.Single(Curves.Intersections(a, arc));
        Assert.True(hit.Y > 5);
    }

    [Fact]
    public void TrimMiddleOfLineSplitsIt()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(30, 0));
        sketch.AddLine(new Vec2(10, -5), new Vec2(10, 5));
        sketch.AddLine(new Vec2(20, -5), new Vec2(20, 5));

        Assert.True(SketchOperations.Trim(sketch, line, new Vec2(15, 0.1)));
        var horizontal = sketch.Entities.OfType<SketchLine>().Where(l => Math.Abs(l.Direction.Y) < Tol).ToList();
        Assert.Equal(2, horizontal.Count);
        Assert.Contains(horizontal, l => Math.Abs(l.End.X - 10) < Tol && Math.Abs(l.Start.X) < Tol);
        Assert.Contains(horizontal, l => Math.Abs(l.Start.X - 20) < Tol && Math.Abs(l.End.X - 30) < Tol);
        Assert.Contains(sketch.Constraints, c => c is CollinearConstraint);
        Assert.True(sketch.Solve().Converged);
    }

    [Fact]
    public void TrimEndAndIsolatedEntities()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(30, 0));
        var cross = sketch.AddLine(new Vec2(10, -5), new Vec2(10, 5));
        Assert.True(SketchOperations.Trim(sketch, line, new Vec2(25, 0)));
        Assert.Equal(10, line.End.X, Tol);
        Assert.Contains(sketch.Constraints, c => c is PointOnLineConstraint pol && pol.References.Contains(cross));

        var lonely = sketch.AddCircle(new Vec2(100, 100), 5);
        Assert.True(SketchOperations.Trim(sketch, lonely, new Vec2(105, 100)));
        Assert.DoesNotContain(lonely, sketch.Entities);
    }

    [Fact]
    public void TrimCircleLeavesArc()
    {
        var sketch = new Sketch();
        var circle = sketch.AddCircle(Vec2.Zero, 10);
        sketch.AddLine(new Vec2(-20, 0), new Vec2(20, 0));
        Assert.True(SketchOperations.Trim(sketch, circle, new Vec2(0, 10)));
        var arc = Assert.Single(sketch.Entities.OfType<SketchArc>());
        // The upper half was removed, so the arc covers the lower half.
        Assert.Equal(Math.PI, arc.Sweep, 1e-6);
        Assert.True(arc.Midpoint.Y < 0);
    }

    [Fact]
    public void ExtendLineToBoundary()
    {
        var sketch = new Sketch();
        var line = sketch.AddLine(new Vec2(0, 0), new Vec2(5, 0));
        sketch.AddLine(new Vec2(12, -5), new Vec2(12, 5));
        sketch.AddCircle(new Vec2(30, 0), 3);
        Assert.True(SketchOperations.Extend(sketch, line, new Vec2(4, 0)));
        Assert.Equal(12, line.End.X, Tol);
        Assert.True(SketchOperations.Extend(sketch, line, new Vec2(11, 0)) is var again);
        Assert.Equal(27, line.End.X, 1e-6);
        Assert.True(again);
        Assert.False(SketchOperations.Extend(sketch, line, new Vec2(0, 0)));
    }

    [Fact]
    public void FilletRectangleCorner()
    {
        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var corner = SketchOperations.FindCorner(sketch, new Vec2(40.1, 20.1), 0.5);
        Assert.NotNull(corner);
        var arc = SketchOperations.Fillet(sketch, corner.Value.A, corner.Value.B, 5);
        Assert.True(sketch.Solve().Converged);
        Assert.Equal(5, arc.Radius, 1e-6);
        Assert.Equal(Math.PI / 2, arc.Sweep, 1e-6);
        Assert.Equal(35, arc.Center.X, 1e-6);
        Assert.Equal(15, arc.Center.Y, 1e-6);
        Assert.Equal(2, sketch.Constraints.Count(c => c is TangentConstraint));
        Assert.Contains(sketch.Constraints, c => c is RadialDimensionConstraint);
        Assert.Throws<ArgumentException>(() => SketchOperations.Fillet(sketch, sides[0], sides[3], 50));
    }

    [Fact]
    public void ChamferCorner()
    {
        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var bevel = SketchOperations.Chamfer(sketch, sides[0], sides[1], 3);
        Assert.True(sketch.Solve().Converged);
        Assert.Equal(Math.Sqrt(18), bevel.Length, 1e-6);
    }

    [Fact]
    public void OffsetClosedRectangleOutward()
    {
        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var created = SketchOperations.Offset(sketch, sides, 2);
        Assert.Equal(4, created.Count);
        var xs = created.OfType<SketchLine>().SelectMany(l => new[] { l.Start.X, l.End.X }).ToList();
        var ys = created.OfType<SketchLine>().SelectMany(l => new[] { l.Start.Y, l.End.Y }).ToList();
        Assert.Equal(-2, xs.Min(), 1e-6);
        Assert.Equal(42, xs.Max(), 1e-6);
        Assert.Equal(-2, ys.Min(), 1e-6);
        Assert.Equal(22, ys.Max(), 1e-6);
        Assert.Equal(4, sketch.Constraints.Count(c => c is CoincidentConstraint && c.References.All(r => created.Contains(r.Root))));
        Assert.True(sketch.Solve().Converged);

        var inward = SketchOperations.Offset(sketch, sides, 2, reverse: true).OfType<SketchLine>().ToList();
        Assert.Equal(2, inward.SelectMany(l => new[] { l.Start.X, l.End.X }).Min(), 1e-6);
    }

    [Fact]
    public void OffsetSlotKeepsArcsConcentric()
    {
        var sketch = new Sketch();
        var slot = sketch.AddSlot(new Vec2(0, 0), new Vec2(30, 0), 5);
        var created = SketchOperations.Offset(sketch, [slot.Side1, slot.Side2, slot.Cap1, slot.Cap2], 1);
        var arcs = created.OfType<SketchArc>().ToList();
        Assert.Equal(2, arcs.Count);
        Assert.All(arcs, a => Assert.Equal(6, a.Radius, 1e-6));
        Assert.All(arcs, a => Assert.Equal(Math.PI, a.Sweep, 1e-6));
        Assert.True(sketch.Solve().Converged);

        var circle = sketch.AddCircle(new Vec2(50, 50), 4);
        var bigger = Assert.IsType<SketchCircle>(Assert.Single(SketchOperations.Offset(sketch, [circle], 1)));
        Assert.Equal(5, bigger.Radius, Tol);
    }

    [Fact]
    public void MirrorAddsSymmetricCopies()
    {
        var sketch = new Sketch();
        var axis = sketch.AddLine(new Vec2(0, -10), new Vec2(0, 10), construction: true);
        sketch.AddConstraint(new FixConstraint(axis));
        var line = sketch.AddLine(new Vec2(2, 0), new Vec2(8, 5));
        var arc = sketch.AddArc(new Vec2(5, -5), new Vec2(8, -5), new Vec2(5, -2));
        var circle = sketch.AddCircle(new Vec2(4, 8), 1);
        var created = SketchOperations.Mirror(sketch, [line, arc, circle], axis);
        Assert.Equal(3, created.Count);
        var ml = Assert.IsType<SketchLine>(created[0]);
        Assert.Equal(-2, ml.Start.X, Tol);
        Assert.Equal(-8, ml.End.X, Tol);
        var ma = Assert.IsType<SketchArc>(created[1]);
        Assert.Equal(arc.Sweep, ma.Sweep, 1e-9);
        Assert.Equal(-5, ma.Center.X, Tol);
        Assert.True(sketch.Solve().Converged);

        // Moving the original moves the copy.
        line.End.Position = new Vec2(9, 6);
        Assert.True(sketch.Solve().Converged);
        Assert.Equal(-line.End.X, ml.End.X, 1e-6);
    }

    [Fact]
    public void SketchPatterns()
    {
        var sketch = new Sketch();
        var c = sketch.AddCircle(new Vec2(10, 0), 1);
        Assert.Equal(4, SketchOperations.LinearPattern(sketch, [c], new Vec2(5, 0), 5).Count);
        var around = SketchOperations.CircularPattern(sketch, [c], Vec2.Zero, 6);
        Assert.Equal(5, around.Count);
        var third = Assert.IsType<SketchCircle>(around[2]);
        Assert.Equal(-10, third.Center.X, 1e-9);
    }
}
