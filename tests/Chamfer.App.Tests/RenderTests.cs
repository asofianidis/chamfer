using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Chamfer.App.Editing.Tools;
using Chamfer.App.ViewModels;
using Chamfer.App.Views;
using Chamfer.Sketching;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Tests;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the real main window off-screen. Catches XAML/binding failures and writes screenshots to
/// the directory in CHAMFER_SCREENSHOT_DIR (if set) for visual review.
/// </summary>
public sealed class RenderTests
{
    // Avalonia supports one headless platform per process, so every render shares this session.
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    [Fact]
    public async Task MainWindowRendersASketchInBothThemes()
    {
        await Session.Value.Dispatch(
            () =>
            {
                Render("light");
                Render("dark");
            },
            TestContext.Current.CancellationToken);
    }

    private static void Render(string theme)
    {
        {
            {
                Application.Current!.RequestedThemeVariant = theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
                var vm = new MainViewModel();
                var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
                window.Show();

                var editor = EditorWorkflowTests.DimensionedRectangle();
                // Rebuild the same sketch in the window's editor through its public API.
                vm.Editor.Load(Sketching.Serialization.SketchSerializer.Serialize(editor.Sketch), null);
                var sketch = vm.Editor.Sketch;
                vm.Editor.BeginChange();
                var circle = sketch.AddCircle(new Vec2(30, 17.5), 8);
                sketch.AddConstraint(new Sketching.Constraints.RadialDimensionConstraint(circle, diameter: true) { LabelOffset = new Vec2(14, 14) });
                var slot = sketch.AddSlot(new Vec2(-40, 10), new Vec2(-15, 10), 5);
                var arc = sketch.AddArc(new Vec2(80, 0), new Vec2(95, 0), new Vec2(80, 15));
                sketch.AddLine(new Vec2(-40, 30), new Vec2(-10, 45), construction: true);
                vm.Editor.EndChange(solve: true);
                vm.Editor.Select(slot.Side1);
                vm.Editor.Select(arc);
                vm.Editor.Tool = new LineTool(construction: false);

                Dispatcher.UIThread.RunJobs();
                window.FindControl<Controls.SketchCanvas>("Canvas")!.ZoomToFit();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);

                if (Environment.GetEnvironmentVariable("CHAMFER_SCREENSHOT_DIR") is { Length: > 0 } dir)
                {
                    Directory.CreateDirectory(dir);
#pragma warning disable CS0618 // The options overload isn't needed for a plain PNG.
                    frame.Save(Path.Combine(dir, $"main-window-{theme}.png"));
#pragma warning restore CS0618
                }

                Assert.Contains("Under Defined", vm.StatusText);
                Assert.Equal(sketch.Constraints.Count, vm.ConstraintRows.Count);
                window.Close();
            }
        }
    }
}
