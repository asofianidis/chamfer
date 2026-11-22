using Chamfer.App.Editing.Tools;
using Chamfer.App.Part;
using Chamfer.App.ViewModels;
using Chamfer.Document.Features;
using Chamfer.Sketching;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Tests;

/// <summary>Part-level workflows driven through the view model, without a window.</summary>
public class PartWorkflowTests
{
    private static MainViewModel BlockOnFront(double w = 40, double h = 20, double depth = 10)
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var editor = vm.Sketch!.Editor;
        editor.BeginChange();
        editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(w, h));
        editor.EndChange(solve: true);
        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        var d = vm.Editor!.Fields.OfType<NumberField>().First(f => f.Label == "Depth");
        d.Text = depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        d.Commit();
        vm.AcceptEditorCommand.Execute(null);
        return vm;
    }

    [Fact]
    public void SketchToolsTrimAndFilletThroughTheEditor()
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var sketchVm = vm.Sketch!;
        var editor = sketchVm.Editor;
        editor.PixelSize = 0.1;
        editor.BeginChange();
        editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        editor.Sketch.AddLine(new Vec2(20, -10), new Vec2(20, 30));
        editor.EndChange(solve: true);

        // Trim the part of the vertical line sticking out above the rectangle.
        sketchVm.ActivateTool("trim");
        EditorWorkflowTests.Click(editor, new Vec2(20, 25));
        var vertical = editor.Sketch.Entities.OfType<SketchLine>().Single(l => Math.Abs(l.Direction.X) < 1e-9 && Math.Abs(l.Start.X - 20) < 1e-9);
        Assert.Equal(20, Math.Max(vertical.Start.Y, vertical.End.Y), 1e-6);

        // Round the top-right corner.
        sketchVm.OperationValue = 4;
        sketchVm.ActivateTool("sketch-fillet");
        EditorWorkflowTests.Click(editor, new Vec2(40, 20));
        var arc = Assert.Single(editor.Sketch.Entities.OfType<SketchArc>());
        Assert.Equal(4, arc.Radius, 1e-6);

        // Undo restores the sharp corner.
        editor.Undo();
        Assert.Empty(editor.Sketch.Entities.OfType<SketchArc>());
    }

    [Fact]
    public void OffsetAndMirrorCommandsUseTheSelection()
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var sketchVm = vm.Sketch!;
        var editor = sketchVm.Editor;
        editor.BeginChange();
        var sides = editor.Sketch.AddCornerRectangle(new Vec2(10, 0), new Vec2(30, 10));
        var axis = editor.Sketch.AddLine(new Vec2(0, -5), new Vec2(0, 20), construction: true);
        editor.EndChange(solve: true);

        foreach (var s in sides)
        {
            editor.Select(s, toggle: true);
        }

        sketchVm.OperationValue = 1;
        sketchVm.OffsetCommand.Execute(null);
        Assert.Equal(8 + 1, editor.Sketch.Entities.OfType<SketchLine>().Count());

        foreach (var s in sides)
        {
            editor.Select(s, toggle: true);
        }

        editor.Select(axis, toggle: true);
        sketchVm.MirrorCommand.Execute(null);
        Assert.Contains(editor.Sketch.Entities.OfType<SketchLine>(), l => Math.Abs(l.Start.X + 10) < 1e-6 || Math.Abs(l.End.X + 10) < 1e-6);
    }

    [Fact]
    public void ConvertFaceProjectsTheFaceOutline()
    {
        var vm = BlockOnFront();
        var body = vm.Document.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        vm.SetSelection([SelectionItem.Face(body, top)]);
        vm.NewSketchCommand.Execute(null);
        Assert.True(vm.IsSketchMode);
        vm.ConvertFaceCommand.Execute(null);
        var lines = vm.Sketch!.Editor.Sketch.Entities.OfType<SketchLine>().ToList();
        Assert.Equal(4, lines.Count);
        Assert.Equal(120, lines.Sum(l => l.Length), 1e-6);

        // The converted outline is a closed profile: a boss on it doubles the block.
        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(40 * 20 * 20, vm.Document.MassProperties().Volume, 1e-6);
        Assert.Equal(2, vm.Document.Features.OfType<ExtrudeFeature>().Count());
    }

    [Fact]
    public void HoleWizardFromFacePoints()
    {
        var vm = BlockOnFront(80, 40, 20);
        var body = vm.Document.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        vm.SetSelection([SelectionItem.Face(body, top)]);
        vm.FeatureCommands.Single(c => c.Label == "Hole Wizard").Command.Execute(null);
        Assert.True(vm.IsSketchMode);
        Assert.IsType<PointTool>(vm.Sketch!.Editor.Tool);
        var editor = vm.Sketch.Editor;
        var plane = vm.EditingPlane!.Value;
        editor.BeginChange();
        foreach (var x in new[] { 20.0, 60.0 })
        {
            var (u, v) = plane.ToLocal(new Geometry.Vec3(x, 20, 20));
            editor.Sketch.AddPoint(new Vec2(u, v));
        }

        editor.EndChange(solve: true);
        vm.ExitSketchCommand.Execute(null);
        var hole = Assert.IsType<HoleFeature>(vm.Editor!.Feature);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(Document.FeatureStatus.Ok, vm.Document.Results[hole.Id].Status);
        Assert.True(vm.Document.MassProperties().Volume < 80 * 40 * 20 - (2 * Math.PI * 3.3 * 3.3 * 20));
    }

    [Fact]
    public void SweepFromTwoSelectedSketches()
    {
        var vm = new MainViewModel();
        SketchFeature Make(string plane, Action<Sketch> draw)
        {
            vm.SetSelection([SelectionItem.Plane(plane, plane)]);
            vm.NewSketchCommand.Execute(null);
            var editor = vm.Sketch!.Editor;
            editor.BeginChange();
            draw(editor.Sketch);
            editor.EndChange(solve: true);
            var sf = vm.EditingSketch!;
            vm.ExitSketchCommand.Execute(null);
            return sf;
        }

        var profile = Make("top", s => s.AddCircle(Vec2.Zero, 2));
        var path = Make("front", s => s.AddLine(Vec2.Zero, new Vec2(0, 40)));
        vm.SetSelection([SelectionItem.ForFeature(profile), SelectionItem.ForFeature(path)]);
        vm.FeatureCommands.Single(c => c.Label == "Swept Boss").Command.Execute(null);
        var sweep = Assert.IsType<SweepFeature>(vm.Editor!.Feature);
        Assert.Equal(profile.Id, sweep.ProfileSketchId);
        Assert.Equal(path.Id, sweep.PathSketchId);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(Math.PI * 4 * 40, vm.Document.MassProperties().Volume, 1e-3);

        // Rename through the edit panel.
        vm.EditFeature(sweep);
        var name = vm.Editor!.Fields.OfType<TextField>().Single();
        name.Text = "Rod";
        name.Commit();
        vm.AcceptEditorCommand.Execute(null);
        Assert.Contains(vm.Tree, n => n.Name == "Rod");
    }
}
