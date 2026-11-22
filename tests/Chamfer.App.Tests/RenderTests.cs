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
