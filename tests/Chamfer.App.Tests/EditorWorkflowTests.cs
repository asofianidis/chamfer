using Chamfer.App.Editing;
using Chamfer.App.Editing.Tools;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;
using Chamfer.Sketching.Solving;

namespace Chamfer.App.Tests;

/// <summary>Drives the real tools with pointer input, the way the canvas does.</summary>
public class EditorWorkflowTests
{
    internal static void Click(SketchEditor editor, Vec2 at, int clickCount = 1)
    {
        var snap = editor.Tool.UsesSnapping ? editor.FindSnap(at) : Snap.None(at);
        var p = new ToolPointer(at, snap, Toggle: false, clickCount);
        editor.Tool.PointerMove(p);
        editor.Tool.PointerDown(p);
        editor.Tool.PointerUp(p);
    }

    internal static void Move(SketchEditor editor, Vec2 at)
    {
        var snap = editor.Tool.UsesSnapping ? editor.FindSnap(at) : Snap.None(at);
        editor.Tool.PointerMove(new ToolPointer(at, snap, false, 1));
    }

    /// <summary>A rectangle snapped to the origin, dimensioned with Smart Dimension: fully defined.</summary>
    internal static SketchEditor DimensionedRectangle()
    {
        var editor = new SketchEditor { PixelSize = 0.1 };
        editor.Tool = new CornerRectangleTool();
        Click(editor, new Vec2(0.05, -0.05)); // snaps to the origin
        Click(editor, new Vec2(40, 25));

        var sides = editor.Sketch.Entities.OfType<SketchLine>().ToList();
        Assert.Equal(4, sides.Count);

        editor.Tool = new DimensionTool();
        double? pending = null;
        editor.DimensionEditRequested += (_, dim) => editor.SetDimensionValue(dim, pending!.Value);

        pending = 60;
        Click(editor, sides[0].Midpoint);           // bottom edge
        Click(editor, new Vec2(20, -10));           // place below

        pending = 35;
        Click(editor, sides[1].Midpoint);           // right edge
        Click(editor, new Vec2(75, 17));            // place to the right
        return editor;
    }

    [Fact]
    public void RectangleSnappedToOriginAndDimensionedIsFullyDefined()
    {
        var editor = DimensionedRectangle();
        var sides = editor.Sketch.Entities.OfType<SketchLine>().ToList();

        Assert.Equal(SketchStatus.FullyDefined, editor.Analysis.Status);
        Assert.Equal(60, sides[0].Length, 1e-6);
        Assert.Equal(35, sides[1].Length, 1e-6);
        Assert.Equal(0, sides[0].Start.Position.Length, 1e-9);
        Assert.Contains(editor.Sketch.Constraints, c => c is CoincidentConstraint cc && ReferenceEquals(cc.B, editor.Sketch.Origin));
    }

    [Fact]
    public void LineChainInfersHorizontalAndVertical()
    {
        var editor = new SketchEditor { PixelSize = 0.1 };
        editor.Tool = new LineTool(construction: false);
        Click(editor, new Vec2(10, 10));
        Click(editor, new Vec2(40, 10.8));  // nearly horizontal
        Click(editor, new Vec2(40.6, 30));  // nearly vertical
        editor.Tool.Finish();

        var lines = editor.Sketch.Entities.OfType<SketchLine>().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(lines[0].Start.Y, lines[0].End.Y, 1e-9);
        Assert.Equal(lines[1].Start.X, lines[1].End.X, 1e-9);
        Assert.Contains(editor.Sketch.Constraints, c => c is HorizontalConstraint);
        Assert.Contains(editor.Sketch.Constraints, c => c is VerticalConstraint);
        Assert.Contains(editor.Sketch.Constraints, c => c is CoincidentConstraint);
        Assert.Empty(editor.Analysis.RedundantConstraints);
    }

    [Fact]
    public void ClosingARectangleWithLinesDropsRedundantAutoRelations()
    {
        var editor = new SketchEditor { PixelSize = 0.1 };
        editor.Tool = new LineTool(construction: false);
        Click(editor, new Vec2(0, 0));
        Click(editor, new Vec2(30, 0));
        Click(editor, new Vec2(30, 20));
        Click(editor, new Vec2(0, 20));
        Click(editor, new Vec2(0.05, 0.05)); // back onto the start point: closes and ends the chain

        Assert.Equal(4, editor.Sketch.Entities.OfType<SketchLine>().Count());
        Assert.Empty(editor.Analysis.RedundantConstraints);
        Assert.NotEqual(SketchStatus.OverDefined, editor.Analysis.Status);
    }

    [Fact]
    public void UndoAndRedoRestoreTheSketch()
    {
        var editor = DimensionedRectangle();
        var constraints = editor.Sketch.Constraints.Count;

        editor.Undo(); // second dimension value
        editor.Undo(); // second dimension
        Assert.Equal(constraints - 1, editor.Sketch.Constraints.Count);

        editor.Redo();
        editor.Redo();
        Assert.Equal(constraints, editor.Sketch.Constraints.Count);
        Assert.Equal(SketchStatus.FullyDefined, editor.Analysis.Status);
    }

    [Fact]
    public void RedundantDimensionIsAddedAsDriven()
    {
        var editor = DimensionedRectangle();
        var top = editor.Sketch.Entities.OfType<SketchLine>().ElementAt(2);
        editor.Tool = new DimensionTool();
        Click(editor, top.Midpoint);
        Click(editor, top.Midpoint + new Vec2(0, 12));

        var dim = editor.Sketch.Constraints.OfType<DimensionConstraint>().Last();
        Assert.False(dim.IsDriving);
        Assert.Equal(SketchStatus.FullyDefined, editor.Analysis.Status);
    }

    [Fact]
    public void ConflictingRelationIsRejectedAndSelectionKept()
    {
        var editor = DimensionedRectangle();
        var bottom = editor.Sketch.Entities.OfType<SketchLine>().First();
        editor.Select(bottom);

        Assert.False(editor.ApplyRelation(ConstraintKind.Vertical));
        Assert.Contains(bottom, editor.Selection);
        Assert.NotNull(editor.Message);
        Assert.Equal(SketchStatus.FullyDefined, editor.Analysis.Status);
    }

    [Fact]
    public void DraggingUnderDefinedGeometryMovesItAndKeepsRelations()
    {
        var editor = new SketchEditor { PixelSize = 0.1 };
        editor.Tool = new CornerRectangleTool();
        Click(editor, new Vec2(10, 10));
        Click(editor, new Vec2(30, 20));
        var corner = editor.Sketch.Entities.OfType<SketchLine>().First().End; // bottom-right

        editor.Tool = new SelectTool();
        var down = new ToolPointer(corner.Position, Snap.None(corner.Position), false, 1);
        editor.Tool.PointerDown(down);
        var target = new Vec2(45, 5);
        editor.Tool.PointerMove(new ToolPointer(target, Snap.None(target), false, 1));
        editor.Tool.PointerUp(new ToolPointer(target, Snap.None(target), false, 1));

        Assert.Equal(target.X, corner.X, 1e-6);
        Assert.Equal(target.Y, corner.Y, 1e-6);
        var lines = editor.Sketch.Entities.OfType<SketchLine>().ToList();
        Assert.Equal(lines[0].Start.Y, lines[0].End.Y, 1e-6); // still horizontal
        Assert.Equal(lines[1].Start.X, lines[1].End.X, 1e-6); // still vertical
        Assert.True(editor.CanUndo);
    }

    [Theory]
    [InlineData("25", 25)]
    [InlineData("25.4*2", 50.8)]
    [InlineData("(100 - 12)/2", 44)]
    [InlineData("1in", 25.4)]
    [InlineData("90°", 90)]
    public void ValueParserHandlesExpressions(string text, double expected)
    {
        Assert.True(ValueParser.TryParse(text, out var value));
        Assert.Equal(expected, value, 1e-9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("2*(3")]
    public void ValueParserRejectsGarbage(string text) => Assert.False(ValueParser.TryParse(text, out _));
}

public class ToolSwitchTests
{
    [Fact]
    public void SwitchingToolsKeepsTheSelection()
    {
        var editor = new SketchEditor();
        var line = editor.Sketch.AddLine(new Vec2(0, 0), new Vec2(10, 0));
        editor.Select(line);
        editor.Tool = new DimensionTool();
        editor.Tool = new SelectTool();
        Assert.Contains(line, editor.Selection);
    }
}
