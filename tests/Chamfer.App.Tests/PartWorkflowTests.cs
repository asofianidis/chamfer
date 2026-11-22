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

    [Fact]
    public void ImportDxfIntoSketchAndExtrude()
    {
        var file = Path.Combine(Path.GetTempPath(), $"chamfer-{Guid.NewGuid():N}.dxf");
        File.WriteAllText(file, string.Join('\n', ["0", "SECTION", "2", "ENTITIES",
            "0", "LWPOLYLINE", "90", "4", "70", "1", "10", "0", "20", "0", "10", "30", "20", "0", "10", "30", "20", "20", "10", "0", "20", "20",
            "0", "ENDSEC", "0", "EOF"]));
        try
        {
            var vm = new MainViewModel();
            vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
            vm.NewSketchCommand.Execute(null);
            vm.ImportDxfFile(file);
            Assert.StartsWith("Imported 4 lines", vm.MessageText, StringComparison.Ordinal);
            vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
            vm.AcceptEditorCommand.Execute(null);
            Assert.Equal(30 * 20 * 10, vm.Document.MassProperties().Volume, 1e-6);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void MotionStudyFromTheUi()
    {
        var vm = new MainViewModel();
        vm.NewAssemblyCommand.Execute(null);
        vm.AssemblyCommands.Single(c => c.Label == "Toolbox").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        var asm = vm.AssemblyDoc!;
        var screw = Assert.Single(asm.Components);
        screw.Fixed = false;
        var start = screw.Transform;
        var body = asm.PartOf(screw).Bodies[0];
        var shank = Enumerable.Range(1, body.FaceCount).First(i => body.Face(i).Surface == Kernel.Occt.SurfaceKind.Cylinder);
        vm.SetSelection([new SelectionItem(SelectionKind.Face, body.FaceRef(shank), Label: "shank", ComponentId: screw.Id)]);
        vm.AssemblyCommands.Single(c => c.Label == "Motion Study").Command.Execute(null);
        var panel = vm.Editor!;
        panel.Fields.OfType<ButtonField>().Single(f => f.Label == "Calculate").Command.Execute(null);
        Assert.NotNull(vm.MotionResult);
        Assert.Equal(49, vm.MotionResult.Frames.Count); // 2 s at 24 fps, plus the start
        Assert.StartsWith("49 frames", panel.Fields.OfType<InfoField>().Single(f => f.Label == "Result").Text, StringComparison.Ordinal);

        // Scrub to half a second: 30 rpm is a quarter turn.
        var timeField = panel.Fields.OfType<NumberField>().Single(f => f.Label == "Time");
        timeField.Text = "0.5";
        timeField.Commit();
        Assert.NotEqual(start, screw.Transform);

        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(start, screw.Transform);
    }

    [Fact]
    public void CustomPropertiesAndPackAndGoFromTheUi()
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var editor = vm.Sketch!.Editor;
        editor.BeginChange();
        editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(30, 20));
        editor.EndChange(solve: true);
        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);

        vm.ShowPropertiesCommand.Execute(null);
        var entry = vm.Editor!.Fields.OfType<TextField>().Single();
        entry.Text = "Description = Block in {Material}";
        entry.Commit();
        Assert.Contains(vm.Editor!.Fields.OfType<InfoField>(), f => f.Label == "Description" && f.Text.StartsWith("Block in Plain Carbon Steel", StringComparison.Ordinal));
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Document.Properties);
        vm.RedoCommand.Execute(null);
        Assert.Single(vm.Document.Properties);

        // Pack and Go an assembly holding a toolbox screw.
        vm.NewAssemblyCommand.Execute(null);
        vm.AssemblyCommands.Single(c => c.Label == "Toolbox").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        var folder = Path.Combine(Path.GetTempPath(), $"chamfer-pg-{Guid.NewGuid():N}");
        try
        {
            var result = vm.PackAndGoTo(folder, suffix: "-rev2");
            Assert.NotNull(result);
            Assert.EndsWith("Assembly1-rev2.chasm", result.AssemblyPath, StringComparison.Ordinal);
            Assert.StartsWith("Pack and Go saved 1 files", vm.MessageText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void FillPatternFromTheUi()
    {
        var part = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 60));
        var sf = part.Add(new SketchFeature { Sketch = s });
        part.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        part.Rebuild();
        var top = Enumerable.Range(1, part.Bodies[0].FaceCount).Single(i => part.Bodies[0].Face(i).Normal.Z > 0.99);
        var (u, v) = part.Bodies[0].Shape.FacePlane(top)!.Value.ToLocal(new Geometry.Vec3(10, 10, 10));
        var pts = new Sketch();
        pts.AddPoint(new Vec2(u, v));
        var ps = part.Add(new SketchFeature { Plane = new PlaneRef(Face: part.Bodies[0].FaceRef(top)), Sketch = pts });
        var hole = part.Add(new HoleFeature { SketchId = ps.Id, Diameter = 6, EndCondition = EndCondition.ThroughAll });

        var vm = new MainViewModel();
        vm.LoadFile("plate.chprt", part.Save());
        var body = vm.Document.Bodies[0];
        var topFace = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        vm.SetSelection([SelectionItem.ForFeature(vm.Document.Find(hole.Id)!), SelectionItem.Face(body, topFace)]);
        vm.FeatureCommands.Single(c => c.Label == "Fill Pattern").Command.Execute(null);
        var fill = Assert.IsType<FillPatternFeature>(vm.Editor!.Feature);
        Assert.Equal([hole.Id], fill.Seeds);
        Assert.NotNull(fill.Face);
        var spacing = vm.Editor.Fields.OfType<NumberField>().Single(f => f.Label == "Spacing");
        spacing.Text = "20";
        spacing.Commit();
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal((100 * 60 * 10) - (15 * Math.PI * 9 * 10), vm.Document.MassProperties().Volume, 1e-3);
    }

    [Fact]
    public void AutoSaveAndRecoveryAfterACrash()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"chamfer-ar-{Guid.NewGuid():N}");
        try
        {
            var vm = new MainViewModel { Recovery = new AutoRecovery(folder) };
            Assert.Null(vm.AutoSaveNow()); // nothing unsaved yet
            vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
            vm.NewSketchCommand.Execute(null);
            var editor = vm.Sketch!.Editor;
            editor.BeginChange();
            editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(30, 20));
            editor.EndChange(solve: true);
            vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
            vm.AcceptEditorCommand.Execute(null);
            var copy = vm.AutoSaveNow();
            Assert.NotNull(copy);
            Assert.True(File.Exists(copy));
            Assert.EndsWith(".chprt", copy, StringComparison.Ordinal);

            // "Crash": a new session sees the copy and recovers it as an unsaved part.
            var next = new MainViewModel { Recovery = new AutoRecovery(folder) };
            var found = Assert.Single(next.Recovery.Find());
            Assert.Equal("Part1.chprt", found.Name);
            next.ShowRecoveryPanel();
            Assert.Equal("Document Recovery", next.Editor!.Title);
            next.Editor.Fields.OfType<ButtonField>().Single(f => f.Label.StartsWith("Open", StringComparison.Ordinal)).Command.Execute(null);
            Assert.Equal(20 * 30 * 10, next.Document.MassProperties().Volume, 1e-6);
            Assert.Null(next.FilePath);
            Assert.False(File.Exists(found.Path));
            Assert.DoesNotContain(new AutoRecovery(folder).Find(), r => r.Path == found.Path);

            // A new document abandons the work, so its recovery copy goes too.
            next.NewCommand.Execute(null);
            Assert.Empty(Directory.EnumerateFiles(folder));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void ScaleAndCutWithPlaneFromTheUi()
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var editor = vm.Sketch!.Editor;
        editor.BeginChange();
        editor.Sketch.AddCornerRectangle(new Vec2(-10, 0), new Vec2(10, 20));
        editor.EndChange(solve: true);
        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        var volume = vm.Document.MassProperties().Volume;

        vm.SelectTabCommand.Execute("Bodies");
        vm.VisibleFeatureCommands.Single(c => c.Label == "Scale").Command.Execute(null);
        var factor = vm.Editor!.Fields.OfType<NumberField>().Single(f => f.Label == "Scale factor");
        factor.Text = "2";
        factor.Commit();
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(volume * 8, vm.Document.MassProperties().Volume, 1e-6);

        vm.SelectTabCommand.Execute("Surfaces");
        vm.SetSelection([SelectionItem.Plane("right", "Right Plane")]);
        vm.VisibleFeatureCommands.Single(c => c.Label == "Cut With Surface").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(volume * 4, vm.Document.MassProperties().Volume, 1e-6);
    }

    [Fact]
    public void Sketch3DFromTheUi()
    {
        var vm = new MainViewModel();
        vm.FeatureCommands.Single(c => c.Label == "3D Sketch").Command.Execute(null);
        var s3 = Assert.IsType<Sketch3DFeature>(vm.Editor!.Feature);
        var points = vm.Editor.Fields.OfType<TextField>().Single();
        points.Text = "0,0,0 | @10,0,0 | @0,10,10";
        points.Commit();
        Assert.Equal(3, s3.Points.Count);
        points.Text = "nonsense";
        points.Commit();
        Assert.NotNull(vm.Editor.Message);
        Assert.Equal(3, s3.Points.Count);
        vm.AcceptEditorCommand.Execute(null);
        Assert.True(vm.Document.State.Curves.ContainsKey(s3.Id));
    }

    [Fact]
    public void AssemblyCutFromTheUi()
    {
        var vm = new MainViewModel();
        vm.NewAssemblyCommand.Execute(null);
        vm.AssemblyCommands.Single(c => c.Label == "Toolbox").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        var asm = vm.AssemblyDoc!;
        var screw = asm.Components[0];
        var full = asm.PartOf(screw).MassProperties().Volume;

        vm.AssemblyCommands.Single(c => c.Label == "Assembly Cut").Command.Execute(null);
        var d = vm.Editor!.Fields.OfType<NumberField>().Single(f => f.Label == "Diameter / width");
        d.Text = "2";
        d.Commit();
        vm.AcceptEditorCommand.Execute(null);
        var cut = Assert.Single(asm.Features);
        Assert.NotEqual("pending", cut.Id);
        Assert.True(asm.BodiesOf(screw)[0].Shape.MassProperties.Volume < full - 1);
        Assert.Equal(full, asm.PartOf(screw).MassProperties().Volume, 1e-9);
        Assert.Contains(vm.Tree, n => n.Tag == cut);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.AssemblyDoc!.Features);
    }

    [Fact]
    public async Task ThermalStudyFromTheUi()
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var editor = vm.Sketch!.Editor;
        editor.BeginChange();
        editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 10));
        editor.EndChange(solve: true);
        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        var body = vm.Document.Bodies[0];
        int End(double sign) => Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.X * sign > 0.99);

        vm.SelectTabCommand.Execute("Simulation");
        vm.SetSelection([SelectionItem.Face(body, End(-1))]);
        vm.VisibleFeatureCommands.Single(c => c.Label == "Temperature").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        vm.SetSelection([SelectionItem.Face(body, End(1))]);
        vm.VisibleFeatureCommands.Single(c => c.Label == "Convection").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(2, vm.Thermal.Loads.Count);

        var run = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.VisibleFeatureCommands.Single(c => c.Label == "Run Thermal").Command;
        await run.ExecuteAsync(null);
        Assert.NotNull(vm.ThermalResult);
        Assert.Equal(100, vm.ThermalResult.MaxTemperature, 1e-6);
        Assert.True(vm.ThermalResult.MinTemperature is > 20 and < 100);
        Assert.Equal(vm.ThermalResult.MinTemperature, vm.StudyResult!.MinStress, 1e-9);
        Assert.Equal("Thermal Study", vm.Editor!.Title);
    }

    [Fact]
    public void MateReferencesFromTheUiMateOnInsert()
    {
        PartDocument MakePart(Action<Sketch> sketch, double depth)
        {
            var vm = new MainViewModel();
            var s = new Sketch();
            sketch(s);
            var sf = vm.Document.Add(new SketchFeature { Sketch = s });
            vm.Document.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = depth });
            vm.RebuildAndRefresh();
            var body = vm.Document.Bodies[0];
            int Face(Func<Kernel.Occt.FaceInfo, bool> pick) => Enumerable.Range(1, body.FaceCount).First(i => pick(body.Face(i)));
            vm.SetSelection([SelectionItem.Face(body, Face(f => f.Surface == Kernel.Occt.SurfaceKind.Cylinder))]);
            vm.FeatureCommands.Single(c => c.Label == "Mate Reference").Command.Execute(null);
            var name = vm.Editor!.Fields.OfType<TextField>().Single();
            name.Text = "Pin";
            name.Commit();
            vm.AcceptEditorCommand.Execute(null);
            Assert.Equal("concentric", Assert.Single(vm.Document.MateReferences).Entities.Single().Kind);
            return vm.Document;
        }

        var plate = MakePart(s =>
        {
            s.AddCornerRectangle(new Vec2(0, 0), new Vec2(60, 40));
            s.AddCircle(new Vec2(30, 20), 6);
        }, 10);
        var pin = MakePart(s => s.AddCircle(Vec2.Zero, 6), 35);

        var asmVm = new MainViewModel();
        asmVm.NewAssemblyCommand.Execute(null);
        asmVm.InsertPart("plate.chprt", plate);
        var c = asmVm.InsertPart("pin.chprt", pin);
        var mate = Assert.Single(asmVm.AssemblyDoc!.Mates);
        Assert.Equal(Chamfer.Assembly.MateType.Concentric, mate.Type);
        Assert.Contains("mate references", asmVm.MessageText, StringComparison.Ordinal);
        var box = asmVm.AssemblyDoc.PlacedBodies(c).Single().Placed.Bounds;
        Assert.Equal(30, box.Center.X, 1e-6);
        Assert.Equal(20, box.Center.Y, 1e-6);
    }

    [Fact]
    public void InchUnitsForEntryAndDisplay()
    {
        var vm = new MainViewModel();
        vm.ShowUnitsCommand.Execute(null);
        vm.Editor!.Fields.OfType<ChoiceField>().Single().Index = 3; // IPS
        Assert.Equal(Geometry.LengthUnit.Inch, vm.Document.Units);
        Assert.StartsWith("IPS", vm.StatusText, StringComparison.Ordinal);

        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var editor = vm.Sketch!.Editor;
        editor.BeginChange();
        var sides = editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(50.8, 25.4));
        var width = editor.Sketch.AddConstraint(new Chamfer.Sketching.Constraints.DistanceConstraint(sides[0], 50.8));
        editor.EndChange(solve: true);
        Assert.Equal("2", Chamfer.App.Editing.DimensionGeometry.FormatValue(width)); // 50.8 mm shows as 2 in

        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        var depth = vm.Editor!.Fields.OfType<NumberField>().Single(f => f.Label == "Depth");
        Assert.Equal("in", depth.Unit);
        depth.Text = "0.5";
        depth.Commit();
        Assert.Equal(12.7, ((ExtrudeFeature)vm.Editor.Feature!).Depth, 1e-9);
        Assert.Equal("0.5", depth.Text);
        depth.Text = "10mm";
        depth.Commit();
        Assert.Equal(10, ((ExtrudeFeature)vm.Editor.Feature!).Depth, 1e-9);
        Assert.Equal("0.3937", depth.Text);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(50.8 * 25.4 * 10, vm.Document.MassProperties().Volume, 0.1); // the height is undimensioned
    }

    [Fact]
    public async Task BucklingStudyFromTheUi()
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var editor = vm.Sketch!.Editor;
        editor.BeginChange();
        editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(100, 10));
        editor.EndChange(solve: true);
        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        var body = vm.Document.Bodies[0];
        int End(double sign) => Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.X * sign > 0.99);

        vm.SelectTabCommand.Execute("Simulation");
        vm.SetSelection([SelectionItem.Face(body, End(-1))]);
        vm.VisibleFeatureCommands.Single(c => c.Label == "Fixture").Command.Execute(null);
        vm.SetSelection([SelectionItem.Face(body, End(1))]);
        vm.VisibleFeatureCommands.Single(c => c.Label == "Force").Command.Execute(null);
        vm.Editor!.Fields.OfType<ChoiceField>().Single().Index = 2; // -X: compression
        vm.AcceptEditorCommand.Execute(null);

        var run = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.VisibleFeatureCommands.Single(c => c.Label == "Buckling").Command;
        await run.ExecuteAsync(null);
        Assert.NotNull(vm.BucklingResult);
        Assert.NotEmpty(vm.BucklingResult.Modes);
        Assert.True(vm.BucklingResult.Modes[0].LoadFactor > 1);
        Assert.Equal("Buckling Study", vm.Editor!.Title);
    }

    [Fact]
    public void TemplatesAndDesignLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"chamfer-lib-{Guid.NewGuid():N}");
        try
        {
            // A template: inch units, ABS and a company property.
            var vm = new MainViewModel { TemplateFolder = Path.Combine(root, "templates") };
            vm.LibraryFolders[0] = Path.Combine(root, "library");
            vm.Document.Units = Geometry.LengthUnit.Inch;
            vm.Document.Material = Material.Abs;
            PropertyEvaluator.Set(vm.Document, "Company", "Acme");
            Assert.NotNull(vm.SaveAsTemplate("Plastic Inch"));

            var fresh = new MainViewModel { TemplateFolder = vm.TemplateFolder };
            fresh.ShowTemplatesCommand.Execute(null);
            fresh.Editor!.Fields.OfType<ButtonField>().Single(f => f.Label == "New part from Plastic Inch").Command.Execute(null);
            Assert.Equal(Geometry.LengthUnit.Inch, fresh.Document.Units);
            Assert.Equal("ABS", fresh.Document.Material.Name);
            Assert.Equal("Acme", PropertyEvaluator.Get(fresh.Document, "Company"));
            Assert.Null(fresh.FilePath);

            // The design library: add a part, then insert it into an assembly.
            vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
            vm.NewSketchCommand.Execute(null);
            var editor = vm.Sketch!.Editor;
            editor.BeginChange();
            editor.Sketch.AddCircle(Vec2.Zero, 5);
            editor.EndChange(solve: true);
            vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
            vm.AcceptEditorCommand.Execute(null);
            vm.ShowDesignLibraryCommand.Execute(null);
            var add = vm.Editor!.Fields.OfType<TextField>().Single(f => f.Label == "Add this part to the library as");
            add.Text = "Spacer";
            add.Commit();
            Assert.Single(vm.LibraryParts());

            var asm = new MainViewModel();
            asm.LibraryFolders[0] = vm.LibraryFolders[0];
            asm.NewAssemblyCommand.Execute(null);
            asm.AssemblyCommands.Single(c => c.Label == "Design Library").Command.Execute(null);
            asm.Editor!.Fields.OfType<ButtonField>().Single(f => f.Label == "Insert Spacer").Command.Execute(null);
            Assert.Equal("Spacer<1>", Assert.Single(asm.AssemblyDoc!.Components).Name);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ShortcutsAndFindCommand()
    {
        var file = Path.Combine(Path.GetTempPath(), $"chamfer-keys-{Guid.NewGuid():N}.json");
        try
        {
            Assert.Equal("Ctrl+Shift+E", MainViewModel.Normalize("shift + ctrl + e"));
            Assert.Equal("Ctrl+F5", MainViewModel.Normalize("Cmd+F5"));

            var vm = new MainViewModel { ShortcutsFile = file };
            Assert.True(vm.RunShortcut("Ctrl+K"));
            Assert.Equal("Find Command", vm.Editor!.Title);
            var search = vm.Editor.Fields.OfType<TextField>().Single();
            search.Text = "fillet";
            search.Commit();
            Assert.IsType<FilletFeature>(vm.Editor!.Feature); // one match runs it
            vm.CancelEditorCommand.Execute(null);

            vm.RunCommand("Find Command");
            search = vm.Editor!.Fields.OfType<TextField>().Single();
            search.Text = "pattern";
            search.Commit();
            Assert.True(vm.Editor!.Fields.OfType<ButtonField>().Count() >= 6); // several matches list as buttons

            vm.SetShortcut("ctrl+shift+h", "Hole Wizard");
            vm.SetShortcut("Ctrl+M", string.Empty);
            Assert.False(vm.RunShortcut("Q"));

            var next = new MainViewModel { ShortcutsFile = file };
            Assert.Equal("Hole Wizard", next.Shortcuts["Ctrl+Shift+H"]);
            Assert.False(next.Shortcuts.ContainsKey("Ctrl+M"));
            Assert.Equal("Extruded Boss", next.Shortcuts["Ctrl+E"]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void DraggingTheExtrudeHandleChangesTheDepth()
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var editor = vm.Sketch!.Editor;
        editor.BeginChange();
        editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(30, 20));
        editor.EndChange(solve: true);
        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        var scene = vm.BuildScene();
        Assert.Contains(scene.Lines, l => l.PickId == MainViewModel.HandlePickId);
        Assert.True(vm.IsHandle(MainViewModel.HandlePickId));
        var (tip, dir) = vm.ActiveHandle!.Value;
        Assert.Equal(new Geometry.Vec3(15, 10, 10), tip);
        Assert.Equal(Geometry.Vec3.UnitZ, dir);

        // A mouse ray passing the handle's axis at z = 35 drags the depth to 35.
        vm.DragHandle(new Geometry.Vec3(-100, 10, 35), Geometry.Vec3.UnitX);
        var ex = Assert.IsType<ExtrudeFeature>(vm.Editor!.Feature);
        Assert.Equal(35, ex.Depth, 1e-9);
        Assert.Equal("35", vm.Editor.Fields.OfType<NumberField>().Single(f => f.Label == "Depth").Text);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(30 * 20 * 35, vm.Document.MassProperties().Volume, 1e-6);
        vm.BuildScene();
        Assert.False(vm.IsHandle(MainViewModel.HandlePickId)); // no handle once the editor closes
    }

    [Fact]
    public void VaultFromTheUi()
    {
        var root = Path.Combine(Path.GetTempPath(), $"chamfer-vault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "part.chprt");
            File.WriteAllText(path, new PartDocument().Save());
            var vm = new MainViewModel { VaultUser = "alice" };
            vm.LoadFile(path, File.ReadAllText(path));
            vm.ShowVaultCommand.Execute(null);
            vm.Editor!.Fields.OfType<ButtonField>().Single(f => f.Label == "Check out").Command.Execute(null);
            Assert.Contains(vm.Editor!.Fields.OfType<InfoField>(), f => f.Label == "Status" && f.Text.Contains("alice", StringComparison.Ordinal));
            var checkIn = vm.Editor.Fields.OfType<TextField>().Single();
            checkIn.Text = "First release";
            checkIn.Commit();
            Assert.Equal("Checked in revision A.", vm.MessageText);
            Assert.Contains(vm.Editor!.Fields.OfType<InfoField>(), f => f.Label == "Rev A" && f.Text.EndsWith("First release", StringComparison.Ordinal));
            Assert.Contains(vm.Editor.Fields.OfType<ButtonField>(), f => f.Label == "Restore A");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LibraryFeatureFromTheUi()
    {
        var root = Path.Combine(Path.GetTempPath(), $"chamfer-lf-{Guid.NewGuid():N}");
        try
        {
            static PartDocument Block()
            {
                var d = new PartDocument();
                var s = new Sketch();
                s.AddCenterRectangle(Vec2.Zero, new Vec2(20, 20));
                var sf = d.Add(new SketchFeature { Sketch = s });
                d.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
                return d;
            }

            var vm = new MainViewModel();
            vm.LibraryFolders[0] = root;
            vm.LoadFile("source.chprt", Block().Save());
            var body = vm.Document.Bodies[0];
            var top = body.FaceRef(Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99));
            var pocket = new Sketch();
            pocket.AddCircle(Vec2.Zero, 4);
            var sketch = vm.Document.Add(new SketchFeature { Plane = new PlaneRef(Face: top), Sketch = pocket });
            var cut = vm.Document.Add(new ExtrudeFeature { SketchId = sketch.Id, IsCut = true, Depth = 6 });
            vm.RebuildAndRefresh();
            vm.SetSelection([SelectionItem.ForFeature(sketch), SelectionItem.ForFeature(cut)]);
            vm.ShowDesignLibraryCommand.Execute(null);
            var save = vm.Editor!.Fields.OfType<TextField>().Single(f => f.Label == "Save selected features as library feature");
            save.Text = "Pocket";
            save.Commit();
            Assert.Single(vm.LibraryFeatureFiles());

            var target = new MainViewModel();
            target.LibraryFolders[0] = root;
            target.LoadFile("target.chprt", Block().Save());
            var tb = target.Document.Bodies[0];
            var before = target.Document.MassProperties().Volume;
            target.SetSelection([SelectionItem.Face(tb, Enumerable.Range(1, tb.FaceCount).Single(i => tb.Face(i).Normal.Z > 0.99))]);
            target.ShowDesignLibraryCommand.Execute(null);
            target.Editor!.Fields.OfType<ButtonField>().Single(f => f.Label == "Insert feature Pocket").Command.Execute(null);
            Assert.Equal(before - (Math.PI * 16 * 6), target.Document.MassProperties().Volume, 1e-3);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void TangentArcGeometry()
    {
        var left = Chamfer.App.Editing.Tools.TangentArcTool.Solve(Vec2.Zero, new Vec2(1, 0), new Vec2(10, 10))!.Value;
        Assert.True(left.Ccw);
        Assert.True(Vec2.Distance(left.Center, new Vec2(0, 10)) < 1e-12);
        var right = Chamfer.App.Editing.Tools.TangentArcTool.Solve(Vec2.Zero, new Vec2(1, 0), new Vec2(10, -10))!.Value;
        Assert.False(right.Ccw);
        Assert.True(Vec2.Distance(right.Center, new Vec2(0, -10)) < 1e-12);
        Assert.Null(Chamfer.App.Editing.Tools.TangentArcTool.Solve(Vec2.Zero, new Vec2(1, 0), new Vec2(10, 0)));
    }

    [Fact]
    public void SketchBlocksFromTheUi()
    {
        var vm = new MainViewModel();
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var sketch = vm.Sketch!;
        var editor = sketch.Editor;
        editor.BeginChange();
        var sides = editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(20, 10));
        editor.EndChange(solve: true);
        foreach (var l in sides)
        {
            editor.Select(l, toggle: true);
        }

        sketch.MakeBlockCommand.Execute(null);
        Assert.Single(editor.Sketch.Blocks);
        editor.ClearSelection();
        editor.Select(sides[0]);
        Assert.Equal(4, editor.Selection.Count); // the whole block

        sketch.CopyBlockCommand.Execute(null);
        Assert.Equal(2, editor.Sketch.Blocks.Count);
        Assert.Equal(8, editor.Sketch.Entities.OfType<Chamfer.Sketching.Entities.SketchLine>().Count());

        editor.Select(sides[0]);
        sketch.ExplodeBlockCommand.Execute(null);
        Assert.Single(editor.Sketch.Blocks);
    }

    [Fact]
    public void UpdateCheckComparesReleaseTags()
    {
        var current = new Version(1, 2, 0);
        Assert.Equal((true, new Version(1, 3, 0)), MainViewModel.CompareRelease(current, "{\"tag_name\": \"v1.3.0\"}"));
        Assert.Equal((false, new Version(1, 2, 0)), MainViewModel.CompareRelease(current, "{\"tag_name\": \"v1.2\"}"));
        Assert.Equal((true, new Version(2, 0, 0)), MainViewModel.CompareRelease(current, "{\"tag_name\": \"2.0.0-beta.1\"}"));
        Assert.Equal((false, (Version?)null), MainViewModel.CompareRelease(current, "{\"message\": \"Not Found\"}"));
        Assert.Equal((false, (Version?)null), MainViewModel.CompareRelease(current, "not json"));
    }

    [Fact]
    public void LanguagesTranslateCommandNames()
    {
        var file = Path.Combine(Path.GetTempPath(), $"chamfer-lang-{Guid.NewGuid():N}.txt");
        try
        {
            var vm = new MainViewModel { LanguageFile = file };
            vm.ShowLanguageCommand.Execute(null);
            vm.Editor!.Fields.OfType<ChoiceField>().Single().Index = Localizer.Languages.Keys.ToList().IndexOf("de");
            Assert.Equal("Linear austragen", Localizer.T("Extruded Boss"));
            Assert.Equal("Blech", Localizer.T("Sheet Metal"));
            Assert.Equal("Something new", Localizer.T("Something new")); // untranslated names stay as they are
            Assert.Equal("de", File.ReadAllText(file));
            Assert.Contains(vm.FeatureCommands, c => c.Label == "Extruded Boss"); // identifiers stay English

            Localizer.Language = "en";
            new MainViewModel { LanguageFile = file }.LoadLanguage();
            Assert.Equal("de", Localizer.Language);
            Assert.True(Localizer.Coverage("fr") > 40);
        }
        finally
        {
            Localizer.Language = "en";
            File.Delete(file);
        }
    }

    [Fact]
    public void MoveStopsAtCollisions()
    {
        var vm = new MainViewModel();
        vm.NewAssemblyCommand.Execute(null);
        for (var i = 0; i < 2; i++)
        {
            vm.AssemblyCommands.Single(c => c.Label == "Toolbox").Command.Execute(null);
            vm.AcceptEditorCommand.Execute(null);
        }

        var moving = vm.AssemblyDoc!.Components[^1];
        vm.SetSelection([new SelectionItem(SelectionKind.Feature, FeatureId: moving.Id, Label: moving.Name, ComponentId: moving.Id)]);
        vm.AssemblyCommands.Single(c => c.Label == "Move").Command.Execute(null);
        void MoveX(double x)
        {
            var field = vm.Editor!.Fields.OfType<NumberField>().Single(f => f.Label == "Move X");
            field.Text = x.ToString(System.Globalization.CultureInfo.InvariantCulture);
            field.Commit();
        }

        var offset = moving.Transform.Tx - vm.AssemblyDoc.Components[0].Transform.Tx; // inserted beside the first
        MoveX(100);
        var start = moving.Transform.Tx;
        vm.Editor!.Fields.OfType<BoolField>().Single(f => f.Label == "Stop at collisions").Value = true;
        MoveX(-offset); // onto the first screw: refused
        Assert.Equal(start, moving.Transform.Tx, 1e-9);
        Assert.Contains("collide", vm.Editor!.Message, StringComparison.Ordinal);
        MoveX(150); // clear again: allowed
        Assert.Equal(start + 50, moving.Transform.Tx, 1e-9);
        Assert.Null(vm.Editor!.Message);
        vm.AcceptEditorCommand.Execute(null);
    }

    [Fact]
    public void InContextPartFromTheUi()
    {
        var vm = new MainViewModel();
        vm.NewAssemblyCommand.Execute(null);
        var plate = new PartDocument();
        var outline = new Sketch();
        outline.AddCornerRectangle(new Vec2(0, 0), new Vec2(30, 20));
        var sf = plate.Add(new SketchFeature { Sketch = outline });
        plate.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 5 });
        var source = vm.InsertPart("plate.chprt", plate);
        var asm = vm.AssemblyDoc!;
        var body = asm.PartOf(source).Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).First(i => body.Face(i).Normal.Z > 0.99);
        vm.SetSelection([new SelectionItem(SelectionKind.Face, body.FaceRef(top), Label: "Face", ComponentId: source.Id)]);

        vm.AssemblyCommands.Single(c => c.Label == "Sketch in Context").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        var made = Assert.Single(asm.Components, c => c.Id != source.Id);
        Assert.Equal(30 * 20 * 2, asm.PartOf(made).Bodies[0].Shape.MassProperties.Volume, 1e-6);

        // Moving the source updates the new part.
        if (source.Fixed)
        {
            vm.SetSelection([new SelectionItem(SelectionKind.Feature, FeatureId: source.Id, Label: source.Name, ComponentId: source.Id)]);
            vm.AssemblyCommands.Single(c => c.Label == "Fix / Float").Command.Execute(null);
        }

        vm.SetSelection([new SelectionItem(SelectionKind.Feature, FeatureId: source.Id, Label: source.Name, ComponentId: source.Id)]);
        vm.AssemblyCommands.Single(c => c.Label == "Move").Command.Execute(null);
        var moveZ = vm.Editor!.Fields.OfType<NumberField>().Single(f => f.Label == "Move Z");
        moveZ.Text = "7";
        moveZ.Commit();
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal(12, asm.PlacedBodies(made).Single().Placed.Bounds.Min.Z, 1e-6);

        vm.AssemblyCommands.Single(c => c.Label == "External References").Command.Execute(null);
        Assert.Single(vm.Editor!.Fields.OfType<BoolField>(), f => f.Label.StartsWith("Lock ", StringComparison.Ordinal));
    }

    [Fact]
    public void FreeformFromTheUi()
    {
        var vm = BlockOnFront();
        var before = vm.Document.MassProperties().Volume;
        var body = vm.Document.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        vm.SetSelection([SelectionItem.Face(body, top)]);
        vm.VisibleFeatureCommands.Single(c => c.Label == "Freeform").Command.Execute(null);
        var fields = vm.Editor!.Fields.OfType<NumberField>().ToList();
        Assert.True(fields.Single(f => f.Label == "Point 3,3").IsVisible);
        Assert.False(fields.Single(f => f.Label == "Point 4,4").IsVisible);

        var middle = fields.Single(f => f.Label == "Point 2,2");
        middle.Text = "4";
        middle.Commit();

        // Growing the grid keeps the middle point's offset where it was.
        var rows = fields.Single(f => f.Label == "Rows");
        rows.Text = "4";
        rows.Commit();
        Assert.True(fields.Single(f => f.Label == "Point 4,1").IsVisible);
        Assert.Equal(4, Assert.IsType<FreeformFeature>(vm.Document.Features[^1]).OffsetAt(1, 1));
        vm.AcceptEditorCommand.Execute(null);
        Assert.True(vm.Document.MassProperties().Volume > before + 50);
    }

    [Fact]
    public void ReferenceGeometryFromTheUi()
    {
        var vm = BlockOnFront();
        var body = vm.Document.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);

        // A point at the top face's centre, from the selection.
        vm.SetSelection([SelectionItem.Face(body, top)]);
        vm.VisibleFeatureCommands.Single(c => c.Label == "Point").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        var center = Assert.Single(vm.Document.Features.OfType<ReferencePointFeature>());
        Assert.Equal(PointKind.FaceCenter, center.Kind);
        Assert.True(vm.Document.State.Points.ContainsKey(center.Id));

        // An axis: switching its type shows the matching selection box.
        vm.SetSelection([]);
        vm.VisibleFeatureCommands.Single(c => c.Label == "Axis").Command.Execute(null);
        var type = vm.Editor!.Fields.OfType<ChoiceField>().Single(f => f.Label == "Type");
        type.Index = 1;
        Assert.True(vm.Editor.Fields.OfType<SelectionField>().Single(f => f.Label == "Two planes").IsVisible);
        Assert.False(vm.Editor.Fields.OfType<SelectionField>().Single(f => f.Label == "Two points").IsVisible);
        vm.CancelEditorCommand.Execute(null);

        // A coordinate system at the point; mass properties can be shown in it.
        vm.VisibleFeatureCommands.Single(c => c.Label == "Coordinate System").Command.Execute(null);
        var origin = vm.Editor!.Fields.OfType<NumberField>().Single(f => f.Label == "Origin X");
        origin.Text = "10";
        origin.Commit();
        vm.AcceptEditorCommand.Execute(null);
        vm.ShowMassPropertiesCommand.Execute(null);
        var relative = vm.Editor!.Fields.OfType<ChoiceField>().Single(f => f.Label == "Relative to");
        var globalCom = vm.Editor.Fields.OfType<InfoField>().Single(f => f.Label == "Centre of mass").Text;
        relative.Index = 1;
        Assert.NotEqual(globalCom, vm.Editor!.Fields.OfType<InfoField>().Single(f => f.Label == "Centre of mass").Text);
    }

    [Fact]
    public void HoleWizardSwitchesStandards()
    {
        var vm = new MainViewModel();
        var hole = new HoleFeature();
        vm.Document.Add(hole);
        var panel = FeatureEditor.Create(vm.Document, hole, isNew: false, [], () => { });
        var standard = panel.Fields.OfType<ChoiceField>().Single(f => f.Label == "Standard");
        var size = panel.Fields.OfType<ChoiceField>().Single(f => f.Label == "Size");
        Assert.Contains("M6", size.Options);

        standard.Index = HoleStandards.Names.ToList().IndexOf(HoleStandards.AnsiInch);
        Assert.Contains("1/4-20", size.Options);
        Assert.Equal(82, hole.CountersinkAngle);

        standard.Index = HoleStandards.Names.ToList().IndexOf(HoleStandards.PipeTap);
        Assert.Equal(HoleType.PipeTap, hole.Type);
        Assert.Equal("1/16 NPT", hole.Size);
        Assert.Equal(0.25 * 25.4, hole.Diameter, 1e-9);
    }

    [Fact]
    public void SubassemblyInsertEditAndReturn()
    {
        var dir = Directory.CreateTempSubdirectory("chamfer-subui-");
        try
        {
            // A subassembly of two toolbox screws, on disk.
            var sub = new Chamfer.Assembly.AssemblyDocument();
            sub.AddComponent(new Chamfer.Document.FastenerSpec(Chamfer.Document.FastenerKind.SocketHeadCapScrew, "M6", 20).Path);
            sub.AddComponent(new Chamfer.Document.FastenerSpec(Chamfer.Document.FastenerKind.HexNut, "M6", 0).Path,
                Chamfer.Geometry.Transform3.Translation(new Chamfer.Geometry.Vec3(30, 0, 0)));
            var subPath = Path.Combine(dir.FullName, "pair.chasm");
            File.WriteAllText(subPath, sub.Save());

            var vm = new MainViewModel();
            vm.NewAssemblyCommand.Execute(null);
            var c = vm.InsertPart(subPath);
            Assert.Equal(2, vm.AssemblyDoc!.PartOf(c).Bodies.Count);
            Assert.Contains(vm.Tree, n => n.Glyph == "◫");
            Assert.Equal(2, vm.Tree.Count(n => n.Indent.Left > 0 && n.Tag is null && n.Glyph == "⬚"));

            // Edit it in place: add a washer, then go back up.
            vm.SetSelection([new SelectionItem(SelectionKind.Feature, FeatureId: c.Id, Label: c.Name, ComponentId: c.Id)]);
            vm.AssemblyCommands.Single(x => x.Label == "Open Part").Command.Execute(null);
            Assert.Equal(2, vm.AssemblyDoc!.Components.Count);
            vm.InsertPart(new Chamfer.Document.FastenerSpec(Chamfer.Document.FastenerKind.Washer, "M6", 0).Path);
            vm.ReturnToAssemblyCommand.Execute(null);
            Assert.Equal(3, vm.AssemblyDoc!.PartOf(c).Bodies.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
