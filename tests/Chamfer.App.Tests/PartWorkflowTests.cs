using Chamfer.App.Editing.Tools;
using Chamfer.App.Part;
using Chamfer.App.ViewModels;
using Chamfer.Document;
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

    [Fact]
    public void EquationsDriveAFeatureParameterFromTheUi()
    {
        var vm = BlockOnFront();
        vm.ShowEquationsCommand.Execute(null);
        var input = vm.Editor!.Fields.OfType<TextField>().Single();
        input.Text = "Thick = 5 * 3";
        input.Commit();
        Assert.Equal(15, vm.Document.VariableValues["Thick"]);

        var extrude = vm.Document.Features.OfType<ExtrudeFeature>().Single();
        vm.EditFeature(extrude);
        var depth = vm.Editor!.Fields.OfType<NumberField>().First(f => f.Label == "Depth");
        depth.Text = "=Thick";
        depth.Commit();
        Assert.True(depth.IsLinked);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(40 * 20 * 15, vm.Document.MassProperties().Volume, 1e-6);

        vm.ShowEquationsCommand.Execute(null);
        input = vm.Editor!.Fields.OfType<TextField>().Single();
        input.Text = "Thick = 4";
        input.Commit();
        Assert.Equal(40 * 20 * 4, vm.Document.MassProperties().Volume, 1e-6);
    }

    [Fact]
    public void ConfigurationsFromThePanel()
    {
        var vm = BlockOnFront();
        var extrude = vm.Document.Features.OfType<ExtrudeFeature>().Single();
        vm.ShowEquationsCommand.Execute(null);
        var eq = vm.Editor!.Fields.OfType<TextField>().Single();
        eq.Text = "D = 10";
        eq.Commit();
        vm.EditFeature(extrude);
        var depth = vm.Editor!.Fields.OfType<NumberField>().First(f => f.Label == "Depth");
        depth.Text = "=D";
        depth.Commit();
        vm.AcceptEditorCommand.Execute(null);

        vm.ShowConfigurationsCommand.Execute(null);
        var add = vm.Editor!.Fields.OfType<TextField>().First(f => f.Label == "Add configuration");
        add.Text = "Thick";
        add.Commit();
        Assert.Equal("Thick", vm.ActiveConfigurationName);
        var over = vm.Editor!.Fields.OfType<TextField>().First(f => f.Label.StartsWith("Override", StringComparison.Ordinal));
        over.Text = "D = 25";
        over.Commit();
        Assert.Equal(40 * 20 * 25, vm.Document.MassProperties().Volume, 1e-6);

        vm.ActiveConfigurationName = "Default";
        Assert.Equal(40 * 20 * 10, vm.Document.MassProperties().Volume, 1e-6);
        Assert.Equal(["Default", "Thick"], vm.ConfigurationNames.ToArray());
    }

    [Fact]
    public void CombineAndColourBodiesFromTheUi()
    {
        var vm = BlockOnFront();

        // A second, separate block overlapping the first.
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var editor = vm.Sketch!.Editor;
        editor.BeginChange();
        editor.Sketch.AddCornerRectangle(new Vec2(30, 0), new Vec2(60, 20));
        editor.EndChange(solve: true);
        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        vm.Editor!.Fields.OfType<BoolField>().Single(f => f.Label == "Merge result").Value = false;
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(2, vm.Document.Bodies.Count);

        var b1 = vm.Document.Bodies[0];
        var b2 = vm.Document.Bodies[1];
        vm.SetSelection([SelectionItem.Face(b1, 1), SelectionItem.Face(b2, 1)]);
        vm.SelectTabCommand.Execute("Bodies");
        Assert.Contains(vm.VisibleFeatureCommands, c => c.Label == "Combine");
        vm.VisibleFeatureCommands.Single(c => c.Label == "Combine").Command.Execute(null);
        var combine = Assert.IsType<CombineFeature>(vm.Editor!.Feature);
        Assert.Equal(b1.Name, combine.MainBody);
        Assert.Equal([b2.Name], combine.ToolBodies);
        vm.AcceptEditorCommand.Execute(null);
        var body = Assert.Single(vm.Document.Bodies);
        Assert.Equal(60 * 20 * 10, body.Shape.MassProperties.Volume, 1e-6);
        Assert.Null(vm.Document.RollbackIndex);

        vm.VisibleFeatureCommands.Single(c => c.Label == "Appearance").Command.Execute(null);
        vm.Editor!.Fields.OfType<ChoiceField>().Single().Index = 1; // Red
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(0xDC2626u, vm.Document.Bodies[0].Color);
    }

    [Fact]
    public void ToolboxScrewInAssemblyAndThreadPitchDefault()
    {
        var vm = new MainViewModel();
        vm.NewAssemblyCommand.Execute(null);
        vm.AssemblyCommands.Single(c => c.Label == "Toolbox").Command.Execute(null);
        var size = vm.Editor!.Fields.OfType<ChoiceField>().Single(f => f.Label == "Size");
        size.Index = Toolbox.SizeNames.ToList().IndexOf("M8");
        vm.AcceptEditorCommand.Execute(null);
        var screw = Assert.Single(vm.AssemblyDoc!.Components);
        Assert.Equal("toolbox:ISO4762 M8x20", screw.Path);
        Assert.Equal("ISO4762 M8x20<1>", screw.Name);
        Assert.Equal("ISO 4762 M8x20", Chamfer.Assembly.AssemblyTools.BillOfMaterials(vm.AssemblyDoc).Single().PartName);

        // A thread on a 10 mm shaft defaults to the M10 coarse pitch.
        var part = new MainViewModel();
        part.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        part.NewSketchCommand.Execute(null);
        var editor = part.Sketch!.Editor;
        editor.BeginChange();
        editor.Sketch.AddCircle(Vec2.Zero, 5);
        editor.EndChange(solve: true);
        part.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        part.AcceptEditorCommand.Execute(null);
        var body = part.Document.Bodies[0];
        var cyl = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Surface == Kernel.Occt.SurfaceKind.Cylinder);
        part.SetSelection([SelectionItem.Face(body, cyl)]);
        part.FeatureCommands.Single(c => c.Label == "Thread").Command.Execute(null);
        var thread = Assert.IsType<ThreadFeature>(part.Editor!.Feature);
        Assert.Equal(1.5, thread.Pitch);
        part.AcceptEditorCommand.Execute(null);
        Assert.Equal(Document.FeatureStatus.Ok, part.Document.Results[thread.Id].Status);
    }

    [Fact]
    public async Task MacroPanelRunsAndRecords()
    {
        var vm = new MainViewModel();
        vm.ShowMacrosCommand.Execute(null);
        var run = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.Editor!.Fields.OfType<ButtonField>().Single(b => b.Label == "Run").Command;
        await run.ExecuteAsync(null);
        Assert.Single(vm.Document.Bodies);
        var output = vm.Editor!.Fields.OfType<InfoField>().Single(f => f.Label == "Output");
        Assert.Contains("Volume:", output.Text, StringComparison.Ordinal);
        Assert.Contains("Done", output.Text, StringComparison.Ordinal);

        // Record adding a chamfer by hand, then check the recorded macro mentions it.
        vm.Editor!.Fields.OfType<ButtonField>().Single(b => b.Label == "Record").Command.Execute(null);
        Assert.True(vm.IsRecording);
        var body = vm.Document.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        vm.SetSelection([SelectionItem.Face(body, top)]);
        vm.FeatureCommands.Single(c => c.Label == "Chamfer").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        vm.ShowMacrosCommand.Execute(null);
        vm.Editor!.Fields.OfType<ButtonField>().Single(b => b.Label == "Stop Recording").Command.Execute(null);
        Assert.False(vm.IsRecording);
        var code = vm.Editor!.Fields.OfType<CodeField>().Single().Text;
        Assert.Contains("\"feature\": \"chamfer\"", code.Replace("\"\"", "\"", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void WeldmentFrameFromTheUi()
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("top", "Top Plane")]);
        vm.NewSketchCommand.Execute(null);
        var editor = vm.Sketch!.Editor;
        editor.BeginChange();
        editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(500, 300));
        editor.EndChange(solve: true);
        vm.SelectTabCommand.Execute("Weldments");
        vm.VisibleFeatureCommands.Single(c => c.Label == "Structural Member").Command.Execute(null);
        var member = Assert.IsType<StructuralMemberFeature>(vm.Editor!.Feature);
        vm.Editor.Fields.OfType<ChoiceField>().Single(f => f.Label == "Profile").Index = 1; // rectangular tube
        Assert.Equal(ProfileKind.RectangularTube, member.Kind);
        vm.Editor.Fields.OfType<ChoiceField>().Single(f => f.Label == "Size").Index = 1;
        Assert.Equal("60x40x3", member.Size);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(4, vm.Document.Bodies.Count);
        vm.VisibleFeatureCommands.Single(c => c.Label == "Cut List").Command.Execute(null);
        Assert.Equal(2, vm.Editor!.Fields.OfType<InfoField>().Count());
    }

    [Fact]
    public void ComponentPatternFromTheUi()
    {
        var vm = new MainViewModel();
        vm.NewAssemblyCommand.Execute(null);
        vm.AssemblyCommands.Single(c => c.Label == "Toolbox").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        var seed = Assert.Single(vm.AssemblyDoc!.Components);
        vm.SetSelection([new SelectionItem(SelectionKind.Feature, FeatureId: seed.Id, Label: seed.Name, ComponentId: seed.Id)]);
        vm.AssemblyCommands.Single(c => c.Label == "Linear Pattern").Command.Execute(null);
        var instances = vm.Editor!.Fields.OfType<NumberField>().Single(f => f.Label == "Instances");
        instances.Text = "4";
        instances.Commit();
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(4, vm.AssemblyDoc.Components.Count);
        Assert.Equal(150, vm.AssemblyDoc.Components[^1].Transform.Tx - seed.Transform.Tx, 1e-6);
        Assert.Equal(3, vm.AssemblyDoc.Mates.Count(m => m.Type == Chamfer.Assembly.MateType.Lock));

        vm.UndoCommand.Execute(null);
        Assert.Single(vm.AssemblyDoc.Components);
    }
}
