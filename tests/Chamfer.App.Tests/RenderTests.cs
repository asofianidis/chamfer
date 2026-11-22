using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Chamfer.App.Editing.Tools;
using Chamfer.App.Part;
using Chamfer.App.ViewModels;
using Chamfer.App.Views;
using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;
using Chamfer.Viewport;

namespace Chamfer.App.Tests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the real main window off-screen and drives the part workflow
/// through the view model. Catches XAML/binding failures and writes
/// screenshots to the directory in CHAMFER_SCREENSHOT_DIR (if set) for review.
/// </summary>
public sealed class RenderTests
{
    // Avalonia supports one headless platform per process, so every render shares this session.
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    [Fact]
    public async Task SketchModeRendersInBothThemes()
    {
        await Session.Value.Dispatch(
            () =>
            {
                RenderSketch("light");
                RenderSketch("dark");
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PartWorkflowBuildsAndRendersAModel()
    {
        await Session.Value.Dispatch(
            () =>
            {
                RenderPart("light");
                RenderPart("dark");
            },
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AssemblyWorkflowMatesAndRenders()
    {
        await Session.Value.Dispatch(() => RenderAssembly("light"), TestContext.Current.CancellationToken);
    }

    private static PartDocument PlateWithHole()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(60, 40));
        s.AddCircle(new Vec2(30, 20), 6);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        return doc;
    }

    private static PartDocument Pin()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCircle(Vec2.Zero, 6);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 35 });
        return doc;
    }

    private static void RenderAssembly(string theme)
    {
        var (window, vm) = Open(theme);
        vm.NewAssemblyCommand.Execute(null);
        Assert.True(vm.IsAssemblyMode);
        var plate = vm.InsertPart("plate.chprt", PlateWithHole());
        var pin = vm.InsertPart("pin.chprt", Pin());
        Assert.Equal("Under defined (6 DOF)", vm.StatusText);

        var asm = vm.AssemblyDoc!;
        var plateBody = asm.PartOf(plate).Bodies[0];
        var pinBody = asm.PartOf(pin).Bodies[0];
        int FaceOf(Body b, Func<Kernel.Occt.FaceInfo, bool> pick) => Enumerable.Range(1, b.FaceCount).First(i => pick(b.Face(i)));
        SelectionItem Item(Chamfer.Assembly.Component c, Body b, int face) => new(SelectionKind.Face, b.FaceRef(face), Label: "face", ComponentId: c.Id);

        // Concentric pin and hole.
        vm.SetSelection([Item(plate, plateBody, FaceOf(plateBody, f => f.Surface == Kernel.Occt.SurfaceKind.Cylinder)), Item(pin, pinBody, FaceOf(pinBody, f => f.Surface == Kernel.Occt.SurfaceKind.Cylinder))]);
        vm.AssemblyCommands.Single(c => c.Label == "Mate").Command.Execute(null);
        Assert.Equal("Concentric", vm.Editor!.Fields.OfType<ChoiceField>().First().Options[vm.Editor.Fields.OfType<ChoiceField>().First().Index]);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Single(asm.Mates);
        Assert.Equal("Under defined (2 DOF)", vm.StatusText);

        // Pin bottom flush with plate bottom.
        vm.SetSelection([Item(plate, plateBody, FaceOf(plateBody, f => f.Normal.Z < -0.99)), Item(pin, pinBody, FaceOf(pinBody, f => f.Normal.Z < -0.99))]);
        vm.AssemblyCommands.Single(c => c.Label == "Mate").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Equal("Under defined (1 DOF)", vm.StatusText);
        var axis = pin.Transform.Apply(Vec3.Zero);
        Assert.Equal(30, axis.X, 1e-6);
        Assert.Equal(20, axis.Y, 1e-6);

        // Undo removes the last mate; redo restores it.
        vm.UndoCommand.Execute(null);
        Assert.Single(asm.Mates);
        vm.RedoCommand.Execute(null);
        Assert.Equal(2, asm.Mates.Count);

        vm.AssemblyCommands.Single(c => c.Label == "Bill of Materials").Command.Execute(null);
        Assert.Contains(vm.Editor!.Fields.OfType<InfoField>(), f => f.Label.Contains("pin", StringComparison.Ordinal));
        vm.AcceptEditorCommand.Execute(null);
        vm.AssemblyCommands.Single(c => c.Label == "Interference").Command.Execute(null);
        Assert.Contains(vm.Editor!.Fields.OfType<InfoField>(), f => f.Text == "No interferences.");
        vm.AcceptEditorCommand.Execute(null);

        var viewport = window.FindControl<Controls.ModelViewport>("Viewport")!;
        viewport.SetView(StandardView.Isometric);
        Capture(window, $"assembly-{theme}");

        // Edit the pin's part in place and come back.
        vm.SelectedNode = vm.Tree.First(n => n.Tag == pin);
        vm.AssemblyCommands.Single(c => c.Label == "Open Part").Command.Execute(null);
        Assert.True(vm.IsPartMode);
        Assert.True(vm.CanReturnToAssembly);
        vm.ReturnToAssemblyCommand.Execute(null);
        Assert.True(vm.IsAssemblyMode);
        window.Close();
    }

    private static (MainWindow Window, MainViewModel Vm) Open(string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static void Capture(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("CHAMFER_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
#pragma warning disable CS0618 // The options overload isn't needed for a plain PNG.
            frame.Save(Path.Combine(dir, $"{name}.png"));
#pragma warning restore CS0618
        }
    }

    private static void RenderSketch(string theme)
    {
        var (window, vm) = Open(theme);

        // Pick the Front plane, then start a sketch on it.
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        Assert.True(vm.IsSketchMode);
        var sketchVm = vm.Sketch!;

        var editor = EditorWorkflowTests.DimensionedRectangle();
        sketchVm.Editor.Load(Sketching.Serialization.SketchSerializer.Serialize(editor.Sketch), null);
        var sketch = sketchVm.Editor.Sketch;
        sketchVm.Editor.BeginChange();
        var circle = sketch.AddCircle(new Vec2(30, 17.5), 8);
        sketch.AddConstraint(new Sketching.Constraints.RadialDimensionConstraint(circle, diameter: true) { LabelOffset = new Vec2(14, 14) });
        var slot = sketch.AddSlot(new Vec2(-40, 10), new Vec2(-15, 10), 5);
        sketch.AddArc(new Vec2(80, 0), new Vec2(95, 0), new Vec2(80, 15));
        sketch.AddLine(new Vec2(-40, 30), new Vec2(-10, 45), construction: true);
        sketchVm.Editor.EndChange(solve: true);
        sketchVm.Editor.Select(slot.Side1);
        sketchVm.Editor.Tool = new LineTool(construction: false);

        Dispatcher.UIThread.RunJobs();
        window.FindControl<Controls.SketchCanvas>("Canvas")!.ZoomToFit();
        Capture(window, $"main-window-{theme}");

        Assert.Contains("Under Defined", sketchVm.StatusText);
        Assert.Equal(sketch.Constraints.Count, sketchVm.ConstraintRows.Count);

        // Leaving the sketch keeps it as a feature.
        vm.ExitSketchCommand.Execute(null);
        Assert.True(vm.IsModelMode);
        Assert.Single(vm.Document.Features.OfType<SketchFeature>());
        window.Close();
    }

    private static void RenderPart(string theme)
    {
        var (window, vm) = Open(theme);

        // Sketch a rectangle with a hole on the Front plane.
        vm.SetSelection([SelectionItem.Plane("front", "Front Plane")]);
        vm.NewSketchCommand.Execute(null);
        var s = vm.Sketch!.Editor;
        s.BeginChange();
        s.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(80, 50));
        s.Sketch.AddCircle(new Vec2(55, 25), 10);
        s.EndChange(solve: true);

        // Extruded Boss uses the sketch being edited.
        vm.FeatureCommands.Single(c => c.Label == "Extruded Boss").Command.Execute(null);
        Assert.True(vm.IsModelMode);
        var extrude = Assert.IsType<ExtrudeFeature>(vm.Editor!.Feature);
        var depth = vm.Editor.Fields.OfType<NumberField>().First(f => f.Label == "Depth");
        depth.Text = "30";
        depth.Commit();
        Assert.Equal(30, extrude.Depth);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Null(vm.Editor);
        Assert.Equal((80 * 50 * 30) - (Math.PI * 100 * 30), vm.Document.MassProperties().Volume, 1e-6);

        // Fillet the four vertical outer edges, picked through the scene's pick table.
        var body = vm.Document.Bodies[0];
        var edges = Enumerable.Range(1, body.EdgeCount)
            .Where(i => body.Edge(i).Curve == Kernel.Occt.CurveKind.Line && Math.Abs(body.Edge(i).CenterOrDirection.Z) > 0.99 &&
                (Math.Abs(body.Edge(i).Midpoint.X) < 1e-6 || Math.Abs(body.Edge(i).Midpoint.X - 80) < 1e-6))
            .Select(i => SelectionItem.Edge(body, i)).ToList();
        Assert.Equal(4, edges.Count);
        vm.SetSelection(edges);
        vm.FeatureCommands.Single(c => c.Label == "Fillet").Command.Execute(null);
        var radius = vm.Editor!.Fields.OfType<NumberField>().First(f => f.Label == "Radius");
        radius.Text = "6";
        radius.Commit();
        vm.AcceptEditorCommand.Execute(null);
        Assert.All(vm.Document.Results.Values, r => Assert.Equal(FeatureStatus.Ok, r.Status));
        Assert.Equal(5 + 3, vm.Tree.Count); // material, 3 planes, origin, extrude, its sketch, fillet

        // Undo removes the fillet; redo brings it back.
        vm.UndoCommand.Execute(null);
        Assert.DoesNotContain(vm.Document.Features, f => f is FilletFeature);
        vm.RedoCommand.Execute(null);
        Assert.Contains(vm.Document.Features, f => f is FilletFeature);

        // Hover and pick in the real viewport.
        var viewport = window.FindControl<Controls.ModelViewport>("Viewport")!;
        viewport.SetView(StandardView.Isometric);
        Dispatcher.UIThread.RunJobs();
        var frame = viewport.RenderFrame();
        var center = frame.Pick(frame.Width / 2, frame.Height / 2);
        Assert.IsType<FacePick>(vm.SceneBuilder.Target(center));
        vm.OnPick(center, add: false, clickCount: 1);
        Assert.Equal(SelectionKind.Face, Assert.Single(vm.Selection).Kind);

        vm.ShowMassPropertiesCommand.Execute(null);
        Assert.Contains(vm.Editor!.Fields.OfType<InfoField>(), f => f.Label == "Mass");
        Capture(window, $"part-{theme}");
        vm.AcceptEditorCommand.Execute(null);

        // Save and reload round trip.
        var json = vm.Document.Save();
        var reloaded = PartDocument.Load(json);
        reloaded.Rebuild();
        Assert.Equal(vm.Document.MassProperties().Volume, reloaded.MassProperties().Volume, 1e-9);
        window.Close();
    }
}
