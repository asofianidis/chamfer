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

    [Fact]
    public async Task DrawingWorkflowRenders()
    {
        await Session.Value.Dispatch(() => RenderDrawing("light"), TestContext.Current.CancellationToken);
    }

    private static void RenderDrawing(string theme)
    {
        var (window, vm) = Open(theme);
        vm.LoadFile("plate.chprt", PlateWithHole().Save());
        Assert.True(vm.IsPartMode);
        vm.NewDrawingCommand.Execute(null);
        Assert.True(vm.IsDrawingMode);
        Assert.False(vm.IsModelMode);
        var sheet = vm.CurrentSheet!;
        Assert.Equal(4, sheet.Views.Count);

        // Select the front view, dimension it and cut a section through the hole.
        var front = sheet.Views[0];
        vm.OnSheetClick(new Geometry.Point2(front.X, front.Y), front.Id, add: false);
        vm.DrawingCommands.Single(c => c.Label == "Auto Dimension").Command.Execute(null);
        Assert.Equal(3, sheet.Dimensions.Count); // width, height, hole diameter
        vm.DrawingCommands.Single(c => c.Label == "Section View").Command.Execute(null);
        vm.AcceptEditorCommand.Execute(null);
        Assert.Contains(sheet.Views, v => v.Kind == Chamfer.Drawing.ViewKind.Section);
        Assert.Contains(vm.SheetGraphics!.Items.OfType<Chamfer.Drawing.TextPrimitive>(), t => t.Text == "60");

        // Dragging a view moves it; undo puts it back.
        var x = front.X;
        vm.OnViewDragged(front.Id, new Geometry.Point2(15, 0), finished: false);
        vm.OnViewDragged(front.Id, default, finished: true);
        Assert.Equal(x + 15, vm.CurrentSheet!.Views[0].X, 1e-9);
        vm.UndoCommand.Execute(null);
        Assert.Equal(x, vm.CurrentSheet!.Views[0].X, 1e-9);

        // GD&T: a position tolerance on the top edge, datum A on the bottom, a surface finish symbol.
        var outline = vm.SheetGraphics!.Items.OfType<Chamfer.Drawing.PolylinePrimitive>()
            .Where(p => p.Pen == Chamfer.Drawing.Pen.Visible && vm.SheetGraphics.Owners.TryGetValue(p, out var o) && o == front.Id).SelectMany(p => p.Points).ToList();
        var (top, bottom, left, right) = (outline.Max(p => p.Y), outline.Min(p => p.Y), outline.Min(p => p.X), outline.Max(p => p.X));
        vm.DrawingCommands.Single(c => c.Label == "Geometric Tolerance").Command.Execute(null);
        var datums = vm.Editor!.Fields.OfType<TextField>().Single(f => f.Label == "Datums");
        datums.Text = "A";
        datums.Commit();
        vm.Editor.Fields.OfType<BoolField>().Single(f => f.Label == "Diameter zone (Ø)").Value = true;
        vm.OnSheetClick(new Geometry.Point2(right, top - 15), null, add: false);
        vm.OnSheetClick(new Geometry.Point2(right + 12, top - 8), null, add: false);
        vm.DrawingCommands.Single(c => c.Label == "Datum Feature").Command.Execute(null);
        vm.OnSheetClick(new Geometry.Point2(right, bottom + 15), null, add: false);
        vm.OnSheetClick(new Geometry.Point2(right + 16, bottom + 15), null, add: false);
        vm.DrawingCommands.Single(c => c.Label == "Surface Finish").Command.Execute(null);
        vm.OnSheetClick(new Geometry.Point2(left + 15, top), null, add: false);
        sheet = vm.CurrentSheet!; // undo above reloaded the drawing
        Assert.Single(sheet.Tolerances);
        Assert.Equal(["A"], sheet.Tolerances[0].Datums);
        Assert.True(sheet.Tolerances[0].Diameter);
        Assert.Equal("A", Assert.Single(sheet.Datums).Label);
        Assert.Single(sheet.Finishes);

        // A hole table for the front view, and a first revision.
        vm.OnSheetClick(new Geometry.Point2(front.X, front.Y), front.Id, add: false);
        vm.DrawingCommands.Single(c => c.Label == "Hole Table").Command.Execute(null);
        vm.OnSheetClick(new Geometry.Point2(305, 150), null, add: false);
        Assert.Single(vm.CurrentSheet!.HoleTables);
        vm.DrawingCommands.Single(c => c.Label == "Add Revision").Command.Execute(null);
        var description = vm.Editor!.Fields.OfType<TextField>().Single(f => f.Label == "Description");
        description.Text = "First release";
        description.Commit();
        Assert.Equal("A", vm.CurrentSheet!.Revisions!.Rows.Single().Rev);
        Assert.Contains(vm.SheetGraphics!.Items.OfType<Chamfer.Drawing.TextPrimitive>(), t => t.Text == "A1");
        // A red Notes layer becomes active; a note placed now goes on it.
        vm.DrawingCommands.Single(c => c.Label == "Layers").Command.Execute(null);
        var newLayer = vm.Editor!.Fields.OfType<TextField>().Single();
        newLayer.Text = "Notes #C00000";
        newLayer.Commit();
        Assert.Equal("Notes", vm.ActiveLayer);
        vm.DrawingCommands.Single(c => c.Label == "Note").Command.Execute(null);
        var noteText = vm.Editor!.Fields.OfType<TextField>().Single();
        noteText.Text = "BREAK SHARP EDGES";
        noteText.Commit();
        vm.OnSheetClick(new Geometry.Point2(20, 40), null, add: false);
        Assert.Equal("Notes", vm.CurrentSheet!.Notes.Last().Layer);
        vm.OnSheetClick(new Geometry.Point2(2, 2), null, add: false); // clear the selection for the screenshot

        window.FindControl<Controls.DrawingCanvas>("Sheet")!.Fit();
        Capture(window, $"drawing-{theme}");
        vm.CloseDrawingCommand.Execute(null);
        Assert.True(vm.IsPartMode);
        window.Close();
    }

    [Fact]
    public async Task SheetMetalWorkflowRenders()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            vm.SetSelection([SelectionItem.Plane("top", "Top Plane")]);
            vm.NewSketchCommand.Execute(null);
            var editor = vm.Sketch!.Editor;
            editor.BeginChange();
            editor.Sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(120, 60));
            editor.Sketch.AddCircle(new Vec2(30, 30), 8);
            editor.EndChange(solve: true);
            vm.SelectTabCommand.Execute("Sheet Metal");
            vm.VisibleFeatureCommands.Single(c => c.Label == "Base Flange").Command.Execute(null);
            vm.AcceptEditorCommand.Execute(null);
            Assert.NotNull(vm.Document.State.SheetMetal);

            // Flanges on two opposite long edges of the face away from the sketch plane.
            var body = vm.Document.Bodies[0];
            var edges = Enumerable.Range(1, body.EdgeCount)
                .Where(i => body.Edge(i).Curve == Kernel.Occt.CurveKind.Line && Math.Abs(body.Edge(i).Length - 120) < 1e-6 && body.Edge(i).Midpoint.Y > 0.5)
                .Select(i => SelectionItem.Edge(body, i)).ToList();
            Assert.Equal(2, edges.Count);
            vm.SetSelection(edges);
            vm.VisibleFeatureCommands.Single(c => c.Label == "Edge Flange").Command.Execute(null);
            vm.AcceptEditorCommand.Execute(null);
            Assert.All(vm.Document.Results.Values, r => Assert.Equal(FeatureStatus.Ok, r.Status));

            var viewport = window.FindControl<Controls.ModelViewport>("Viewport")!;
            viewport.SetView(StandardView.Isometric);
            Capture(window, "sheetmetal-folded");
            vm.VisibleFeatureCommands.Single(c => c.Label == "Flatten").Command.Execute(null);
            Assert.True(vm.ShowFlat);
            viewport.SetView(StandardView.Top);
            Capture(window, "sheetmetal-flat");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SimulationWorkflowRenders()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            var bracket = new PartDocument();
            var s = new Sketch();
            s.AddPolyline([new Vec2(0, 0), new Vec2(80, 0), new Vec2(80, 10), new Vec2(10, 10), new Vec2(10, 60), new Vec2(0, 60)], closed: true);
            var sf = bracket.Add(new SketchFeature { Sketch = s });
            bracket.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 20 });
            vm.LoadFile("bracket.chprt", bracket.Save());
            var body = vm.Document.Bodies[0];
            int Face(Func<Kernel.Occt.FaceInfo, bool> pick) => Enumerable.Range(1, body.FaceCount).Single(i => pick(body.Face(i)));

            vm.SelectTabCommand.Execute("Simulation");
            vm.SetSelection([SelectionItem.Face(body, Face(f => f.Normal.Y > 0.99 && f.Centroid.Y > 59))]);
            vm.VisibleFeatureCommands.Single(c => c.Label == "Fixture").Command.Execute(null);
            vm.SetSelection([SelectionItem.Face(body, Face(f => f.Normal.X > 0.99 && f.Centroid.X > 79))]);
            vm.VisibleFeatureCommands.Single(c => c.Label == "Force").Command.Execute(null);
            vm.Editor!.Fields.OfType<ChoiceField>().Single().Index = 4; // -Y
            vm.AcceptEditorCommand.Execute(null);
            Assert.Single(vm.Study.Fixtures);
            Assert.Single(vm.Study.Loads);

            var run = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.VisibleFeatureCommands.Single(c => c.Label == "Run Study").Command;
            var task = run.ExecuteAsync(null);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Assert.NotNull(vm.StudyResult);
            Assert.True(vm.StudyResult!.MaxStress > 0);
            Assert.True(vm.ShowResults);
            Assert.Contains(vm.Editor!.Fields.OfType<InfoField>(), f => f.Label == "Factor of safety");
            window.FindControl<Controls.ModelViewport>("Viewport")!.SetView(StandardView.Isometric);
            Capture(window, "simulation");

            // Natural frequencies with the same fixture.
            var freq = (CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.VisibleFeatureCommands.Single(c => c.Label == "Frequency").Command;
            task = freq.ExecuteAsync(null);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Assert.NotNull(vm.FrequencyResult);
            var modes = vm.FrequencyResult!.Modes;
            Assert.Equal(4, modes.Count);
            Assert.True(modes[0].Frequency > 0);
            Assert.True(modes.Zip(modes.Skip(1)).All(p => p.First.Frequency <= p.Second.Frequency * (1 + 1e-9)));
            Assert.Equal(4, vm.Editor!.Fields.OfType<InfoField>().Count(f => f.Label.StartsWith("Mode ", StringComparison.Ordinal)));
            vm.Editor.Fields.OfType<ChoiceField>().Single(f => f.Label == "Show mode").Index = 1;
            Assert.Same(modes[1].Shape, vm.StudyResult);
            Capture(window, "frequency");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PhotoRenderShowsAndSaves()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            var plate = PlateWithHole();
            plate.Material = Material.Abs;
            plate.Add(new AppearanceFeature { Color = 0x2563EB });
            vm.LoadFile("plate.chprt", plate.Save());
            window.FindControl<Controls.ModelViewport>("Viewport")!.SetView(StandardView.Isometric);
            vm.StartRenderCommand.Execute(null);
            vm.RenderOptions.Samples = 16;
            var task = vm.RenderPhotoAsync();
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Assert.True(task.IsCompletedSuccessfully);
            var (pixels, w, h) = vm.LastRender!.Value;
            Assert.Equal(w * h, pixels.Length);
            Assert.Contains(pixels, p => (p & 0xFF) > ((p >> 16) & 0xFF) + 60); // the blue plate
            var file = Path.Combine(Path.GetTempPath(), $"chamfer-render-{Guid.NewGuid():N}.png");
            vm.SaveRenderTo(file);
            Assert.True(new FileInfo(file).Length > 1000);
            File.Delete(file);
            Capture(window, "render");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThreeDAnnotationsRender()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            vm.LoadFile("plate.chprt", PlateWithHole().Save());
            vm.TogglePmiCommand.Execute(null);
            Assert.True(vm.ShowPmi);
            var scene = vm.BuildScene();
            Assert.Contains(scene.Labels, l => l.Text == "60");
            Assert.Contains(scene.Labels, l => l.Text.StartsWith("Ø12", StringComparison.Ordinal));
            window.FindControl<Controls.ModelViewport>("Viewport")!.SetView(StandardView.Isometric);
            Capture(window, "pmi");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task F6MovesFocusBetweenRegions()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            vm.LoadFile("plate.chprt", PlateWithHole().Save());
            vm.ShowUnitsCommand.Execute(null); // a property panel with a focusable field
            Dispatcher.UIThread.RunJobs();
            var tree = window.FindControl<ListBox>("FeatureTree")!;
            var view = window.FindControl<Controls.ModelViewport>("Viewport")!;
            Assert.Equal("Feature tree", Avalonia.Automation.AutomationProperties.GetName(tree));

            Visual? Focused() => window.FocusManager?.GetFocusedElement() as Visual;
            // With a panel open, F6 moves between the panel and the view.
            var panel = window.FindControl<Panel>("PropertyPanel")!;
            window.KeyPress(Avalonia.Input.Key.F6, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.F6, null);
            Assert.True(Focused() is { } f && Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(panel, f));
            window.KeyPress(Avalonia.Input.Key.F6, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.F6, null);
            Assert.Same(view, Focused());

            // With no panel, between the tree and the view.
            vm.AcceptEditorCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.KeyPress(Avalonia.Input.Key.F6, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.F6, null);
            Assert.True(Focused() is { } t && (ReferenceEquals(t, tree) || Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(tree, t)));
            window.Close();
        }, TestContext.Current.CancellationToken);
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

        // An assembly drawing with a BOM and balloons, then back to the assembly.
        vm.NewDrawingCommand.Execute(null);
        Assert.True(vm.IsDrawingMode);
        vm.DrawingCommands.Single(c => c.Label == "BOM Table").Command.Execute(null);
        vm.DrawingCommands.Single(c => c.Label == "Auto Balloon").Command.Execute(null);
        Assert.Equal(2, vm.CurrentSheet!.Balloons.Count);
        Assert.Contains(vm.SheetGraphics!.Items.OfType<Chamfer.Drawing.TextPrimitive>(), t => t.Text == "PART NUMBER");
        window.FindControl<Controls.DrawingCanvas>("Sheet")!.Fit();
        Capture(window, $"assembly-drawing-{theme}");
        vm.DrawingCommands.Single(c => c.Label == "Add Sheet").Command.Execute(null);
        Assert.Equal(2, vm.DrawingDoc!.Sheets.Count);
        Assert.Equal(1, vm.SheetIndex);
        vm.CloseDrawingCommand.Execute(null);
        Assert.True(vm.IsAssemblyMode);

        // Edit the pin's part in place and come back.
        vm.SelectedNode = vm.Tree.First(n => n.Tag == pin);
        vm.AssemblyCommands.Single(c => c.Label == "Open Part").Command.Execute(null);
        Assert.True(vm.IsPartMode);
        Assert.True(vm.CanReturnToAssembly);
        vm.ReturnToAssemblyCommand.Execute(null);
        Assert.True(vm.IsAssemblyMode);
        window.Close();
    }

    [Fact]
    public async Task ReferenceGeometryRenders()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            var doc = PlateWithHole();
            doc.Add(new ReferenceAxisFeature { Kind = AxisKind.TwoPlanes, PlaneA = PlaneRef.Front, PlaneB = PlaneRef.Right });
            var point = doc.Add(new ReferencePointFeature { X = 10, Y = 10, Z = 10 });
            doc.Add(new CoordinateSystemFeature { OriginPoint = point.Id });
            vm.LoadFile("refs.chprt", doc.Save());
            Assert.Equal(3, vm.Document.State.Axes.Count + vm.Document.State.Points.Count + vm.Document.State.Frames.Count);
            Capture(window, "reference-geometry");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MoreDrawingViewsRender()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            vm.LoadFile("plate.chprt", PlateWithHole().Save());
            vm.NewDrawingCommand.Execute(null);
            var sheet = vm.CurrentSheet!;
            var front = sheet.Views[0];
            void SelectFront() => vm.OnSheetClick(new Geometry.Point2(front.X, front.Y), front.Id, add: false);

            SelectFront();
            vm.DrawingCommands.Single(c => c.Label == "Auxiliary View").Command.Execute(null);
            vm.AcceptEditorCommand.Execute(null);
            Assert.Contains(sheet.Views, v => v.Kind == Chamfer.Drawing.ViewKind.Auxiliary);

            SelectFront();
            vm.DrawingCommands.Single(c => c.Label == "Break View").Command.Execute(null);
            vm.AcceptEditorCommand.Execute(null);
            Assert.NotNull(front.Break);

            var iso = sheet.Views.Single(v => v.Kind == Chamfer.Drawing.ViewKind.Isometric);
            vm.OnSheetClick(new Geometry.Point2(iso.X, iso.Y), iso.Id, add: false);
            vm.DrawingCommands.Single(c => c.Label == "Crop View").Command.Execute(null);
            vm.AcceptEditorCommand.Execute(null);
            Assert.NotNull(iso.Crop);

            vm.UndoCommand.Execute(null); // the crop goes again
            Assert.Null(vm.CurrentSheet!.Views.Single(v => v.Kind == Chamfer.Drawing.ViewKind.Isometric).Crop);
            Capture(window, "drawing-more-views");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DimensionPropertiesSetAFit()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            vm.LoadFile("plate.chprt", PlateWithHole().Save());
            vm.NewDrawingCommand.Execute(null);
            var sheet = vm.CurrentSheet!;
            var front = sheet.Views[0];
            vm.OnSheetClick(new Geometry.Point2(front.X, front.Y), front.Id, add: false);
            vm.DrawingCommands.Single(c => c.Label == "Auto Dimension").Command.Execute(null);
            var diameter = sheet.Dimensions.Single(x => x.Kind == Chamfer.Drawing.DimensionKind.Diameter);
            vm.DrawingSelection.Clear();
            vm.DrawingSelection.Add(diameter.Id);
            vm.DrawingCommands.Single(c => c.Label == "Dimension Properties").Command.Execute(null);
            vm.Editor!.Fields.OfType<ChoiceField>().Single().Index = 1; // H7
            vm.Editor.Fields.OfType<TextField>().Single(f => f.Label.StartsWith("Tolerance", StringComparison.Ordinal)).Text = " A";
            vm.AcceptEditorCommand.Execute(null);
            Assert.Equal("H7", diameter.Fit);
            Assert.Equal("A", diameter.Tolerance);
            Assert.Contains(vm.SheetGraphics!.Items.OfType<Chamfer.Drawing.TextPrimitive>(), t => t.Text.EndsWith(" H7A", StringComparison.Ordinal));
            vm.UndoCommand.Execute(null);
            Assert.Null(vm.CurrentSheet!.Dimensions.Single(x => x.Kind == Chamfer.Drawing.DimensionKind.Diameter).Fit);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WeldAndDatumTargetSymbolsRender()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            vm.LoadFile("plate.chprt", PlateWithHole().Save());
            vm.NewDrawingCommand.Execute(null);
            var sheet = vm.CurrentSheet!;
            var front = sheet.Views[0];
            sheet.Welds.Add(new Chamfer.Drawing.WeldSymbol { Id = "w1", Size = "6", Length = "50", AnchorX = front.X + 20, AnchorY = front.Y + 30, X = front.X + 45, Y = front.Y + 55, AllAround = true, FieldWeld = true });
            sheet.Welds.Add(new Chamfer.Drawing.WeldSymbol { Id = "w2", Kind = Chamfer.Drawing.WeldKind.VGroove, Size = "8", BothSides = true, AnchorX = front.X - 20, AnchorY = front.Y + 30, X = front.X - 45, Y = front.Y + 55 });
            sheet.DatumTargets.Add(new Chamfer.Drawing.DatumTarget { Id = "t1", Label = "A1", Area = "⌀6", AnchorX = front.X, AnchorY = front.Y - 20, X = front.X + 25, Y = front.Y - 45 });
            vm.RebuildAndRefresh();
            Capture(window, "drawing-welds");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TwistedBarRenders()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            var doc = new PartDocument();
            var s = new Sketch();
            s.AddCenterRectangle(Vec2.Zero, new Vec2(10, 5));
            var sf = doc.Add(new SketchFeature { Sketch = s });
            doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 100 });
            doc.Add(new FlexFeature { Mode = FlexMode.Twisting, Angle = 120, Length = 100 });
            vm.LoadFile("twist.chprt", doc.Save());
            Assert.All(vm.Document.Results.Values, r => Assert.Equal(FeatureStatus.Ok, r.Status));
            Capture(window, "flex-twist");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SidePanelMovesAndHides()
    {
        await Session.Value.Dispatch(() =>
        {
            var (window, vm) = Open("light");
            vm.LayoutFile = Path.Combine(Path.GetTempPath(), $"chamfer-layout-{Guid.NewGuid():N}.txt");
            var pane = window.FindControl<Border>("SidePane")!;
            var view = window.FindControl<Controls.ModelViewport>("Viewport")!;
            double X(Visual v) => v.TranslatePoint(new Point(0, 0), window)!.Value.X;
            Assert.True(X(pane) < X(view));

            vm.TogglePanelSideCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(X(pane) > X(view));

            window.KeyPress(Avalonia.Input.Key.F9, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.F9, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.PanelHidden);
            Assert.False(pane.IsVisible);

            // Remembered for next time.
            var next = new MainViewModel { LayoutFile = vm.LayoutFile };
            next.LoadLayout();
            Assert.True(next.PanelOnRight);
            Assert.True(next.PanelHidden);
            File.Delete(vm.LayoutFile);
            window.Close();
        }, TestContext.Current.CancellationToken);
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
