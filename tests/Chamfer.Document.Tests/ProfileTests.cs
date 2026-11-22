using Chamfer.Document.Profiles;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class ProfileTests
{
    [Fact]
    public void RectangleIsOneCounterClockwiseRegion()
    {
        var sketch = new Sketch();
        sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var result = ProfileBuilder.Build(sketch);
        var region = Assert.Single(result.Regions);
        Assert.Equal(4, region.Outer.Segments.Count);
        Assert.Equal(800, region.Outer.SignedArea, 1e-9);
        Assert.Empty(region.Holes);
        Assert.Equal(0, result.OpenEntityCount);
    }

    [Fact]
    public void ClockwiseDrawnRectangleIsStillCounterClockwise()
    {
        var sketch = new Sketch();
        sketch.AddPolyline([new Vec2(0, 0), new Vec2(0, 20), new Vec2(40, 20), new Vec2(40, 0)], closed: true);
        var region = Assert.Single(ProfileBuilder.Build(sketch).Regions);
        Assert.Equal(800, region.Outer.SignedArea, 1e-9);
    }

    [Fact]
    public void NestedLoopsAlternateBetweenMaterialAndHoles()
    {
        var sketch = new Sketch();
        sketch.AddCornerRectangle(new Vec2(-50, -50), new Vec2(50, 50));
        sketch.AddCircle(Vec2.Zero, 30);
        sketch.AddCircle(Vec2.Zero, 10);
        var regions = ProfileBuilder.Build(sketch).Regions;
        Assert.Equal(2, regions.Count);
        var outer = regions.Single(r => r.Holes.Count == 1);
        Assert.Equal(10000 - (Math.PI * 900), outer.Area, 15.0); // arcs are sampled
        var island = regions.Single(r => r.Holes.Count == 0);
        Assert.Equal(Math.PI * 100, island.Area, 2.0);
    }

    [Fact]
    public void InternalLinesAndDanglingLinesAreIgnored()
    {
        var sketch = new Sketch();
        sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        sketch.AddLine(new Vec2(0, 0), new Vec2(40, 20));    // diagonal between corners: an internal edge
        sketch.AddLine(new Vec2(20, 0), new Vec2(20, 20));   // ends mid-edge, so it dangles (no intersection splitting yet)
        sketch.AddLine(new Vec2(40, 20), new Vec2(60, 30));  // dangling
        var result = ProfileBuilder.Build(sketch);
        var region = Assert.Single(result.Regions);
        Assert.Equal(800, region.Area, 1e-9);
        Assert.Equal(4, region.Outer.Segments.Count);
        Assert.Equal(2, result.OpenEntityCount);
    }

    [Fact]
    public void SlotWithArcsHasCorrectArea()
    {
        var sketch = new Sketch();
        sketch.AddSlot(new Vec2(0, 0), new Vec2(30, 0), 5);
        var region = Assert.Single(ProfileBuilder.Build(sketch).Regions);
        Assert.Equal((30 * 10) + (Math.PI * 25), region.Area, 0.5);
        Assert.Contains(region.Outer.Segments, s => s.Kind == Sketching.Entities.EntityKind.Arc);
    }

    [Fact]
    public void ConstructionGeometryIsNotProfile()
    {
        var sketch = new Sketch();
        sketch.AddCircle(Vec2.Zero, 10, construction: true);
        Assert.Empty(ProfileBuilder.Build(sketch).Regions);
    }
}
