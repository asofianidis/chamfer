using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Chamfer.App.Editing;
using Chamfer.App.Part;
using Chamfer.App.ViewModels;
using Chamfer.Document;
using Chamfer.Viewport;
using Chamfer.Sketching.Constraints;
using SkVec2 = Chamfer.Sketching.Vec2;

namespace Chamfer.App.Views;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Windows aren't disposable; the bitmap is released on close.")]
public partial class MainWindow : Window
{
    private DimensionConstraint? _editing;
    private MainViewModel? _vm;
    private SketchViewModel? _wiredSketch;
    private readonly SoftwareRenderer _underlayRenderer = new();
    private WriteableBitmap? _underlayBitmap;
    private PartScene? _underlayScene;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnAnyLostFocus, RoutingStrategies.Bubble);
        DimensionInput.KeyDown += OnDimensionInputKeyDown;
        DimensionInput.LostFocus += (_, _) => HideDimensionInput();
        FeatureTree.DoubleTapped += (_, _) => _vm?.EditSelectedCommand.Execute(null);
        MaterialBox.SelectionChanged += (_, _) =>
        {
            if (MaterialBox.SelectedItem is Material m && _vm is { } vm && vm.Document.Material != m)
            {
                vm.SetMaterialCommand.Execute(m);
            }
        };
        Viewport.HoverChanged += (_, id) => _vm?.OnHover(id);
        Viewport.Picked += (_, e) => _vm?.OnPick(e.Id, e.Modifiers.HasFlag(KeyModifiers.Shift) || e.Modifiers.HasFlag(CommandModifier), e.ClickCount);
        Canvas.Underlay = DrawUnderlay;
        Sheet.SheetClicked += (_, e) => _vm?.OnSheetClick(e.At, e.Owner, e.Modifiers.HasFlag(KeyModifiers.Shift) || e.Modifiers.HasFlag(CommandModifier));
        Sheet.ViewDragged += (_, e) => _vm?.OnViewDragged(e.Owner, e.Delta, e.Finished);
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.SceneChanged -= OnSceneChanged;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _vm = ViewModel;
        if (_vm is not { } vm)
        {
            return;
        }

        vm.StorageProvider = StorageProvider;
        vm.FitAction = Viewport.ZoomToFit;
        vm.ViewProvider = () => (Viewport.Camera.Clone(), Viewport.PixelSize.Width, Viewport.PixelSize.Height);
        vm.ShowImageAction = Viewport.ShowStill;
        vm.ViewAction = v => Viewport.SetView(v);
        vm.NormalToAction = plane =>
        {
            Viewport.Camera.NormalTo(plane);
            if (vm.Document.Bodies.Count == 0)
            {
                Viewport.ZoomToFit();
            }
            else
            {
                Viewport.InvalidateVisual();
            }
        };
        vm.SceneChanged += OnSceneChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.PickUnderlayEdge = PickUnderlayEdge;
        Sheet.Selected = vm.DrawingSelection;
        ApplyTheme();
        Viewport.Camera.SetView(StandardView.Trimetric);
        OnSceneChanged(this, EventArgs.Empty);
        Viewport.ZoomToFit();
    }

    private void ApplyTheme()
    {
        if (_vm is null)
        {
            return;
        }

        _vm.Palette = ActualThemeVariant == ThemeVariant.Dark ? ViewPalette.Dark : ViewPalette.Light;
        OnSceneChanged(this, EventArgs.Empty);
    }

    private void OnSceneChanged(object? sender, EventArgs e)
    {
        if (_vm is null)
        {
            return;
        }

        Viewport.Scene = _vm.BuildScene();
        Canvas.InvalidateVisual();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Sketch))
        {
            WireSketch();
        }
    }

    /// <summary>Hooks the sketch canvas up to a newly entered sketch and matches its view to the 3D camera.</summary>
    private void WireSketch()
    {
        if (_vm?.Sketch is not { } sketch || ReferenceEquals(sketch, _wiredSketch))
        {
            _wiredSketch = _vm?.Sketch;
            return;
        }

        _wiredSketch = sketch;
        sketch.ZoomToFitAction = Canvas.ZoomToFit;
        sketch.Editor.DimensionEditRequested += (_, dim) => ShowDimensionInput(dim);
        Canvas.CursorMoved -= OnCanvasCursorMoved;
        Canvas.CursorMoved += OnCanvasCursorMoved;

        if (_vm.EditingPlane is { } plane)
        {
            var cam = Viewport.Camera;
            var (u, v) = plane.ToLocal(cam.Target);
            var height = Viewport.Bounds.Height > 1 ? Viewport.Bounds.Height : 600;
            Canvas.SetView(new SkVec2(u, v), height / cam.ViewHeight);
        }

        Post(() => Canvas.Focus());
    }

    private static void Post(Action a) => Avalonia.Threading.Dispatcher.UIThread.Post(a);

    private void OnCanvasCursorMoved(object? sender, SkVec2 world)
    {
        if (_vm?.Sketch is { } sketch)
        {
            sketch.SetCursor(world);
            sketch.RefreshHint();
            _vm.HintText = sketch.HintText;
        }
    }

    /// <summary>Draws the part behind the sketch, seen normal to the sketch plane, at the canvas's pan and zoom.</summary>
    private void DrawUnderlay(DrawingContext context, Size size, SkVec2 center, double scale)
    {
        if (_vm?.EditingPlane is not { } plane || _vm.Document.Bodies.Count == 0 || size.Width < 1 || size.Height < 1)
        {
            return;
        }

        var renderScale = RenderScaling;
        var w = Math.Max(1, (int)(size.Width * renderScale));
        var h = Math.Max(1, (int)(size.Height * renderScale));
        var cam = new Camera();
        cam.NormalTo(plane);
        cam.Target = plane.ToWorld(center.X, center.Y);
        cam.ViewHeight = size.Height / scale;

        var palette = ActualThemeVariant == ThemeVariant.Dark ? ViewPalette.Dark : ViewPalette.Light;
        var background = ActualThemeVariant == ThemeVariant.Dark ? Rgba.FromHex(0x1E1F22) : Rgba.FromHex(0xFFFFFF);
        var faded = palette with { Top = background, Bottom = background, Body = Rgba.Lerp(palette.Body, background, 0.35), Edge = Rgba.Lerp(palette.Edge, background, 0.5) };
        _underlayScene = new PartScene();
        var scene = _underlayScene.Build(_vm.Document, [], null, faded, DisplayStyle.ShadedWithEdges, _vm.EditingSketch?.Id);
        scene.Quads.Clear();
        scene.Lines.Clear();
        scene.Points.Clear();
        _underlayRenderer.Resize(w, h);
        _underlayRenderer.Render(scene, cam);
        if (_underlayBitmap is null || _underlayBitmap.PixelSize.Width != w || _underlayBitmap.PixelSize.Height != h)
        {
            _underlayBitmap?.Dispose();
            _underlayBitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96 * renderScale, 96 * renderScale), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (var fb = _underlayBitmap.Lock())
        {
            unsafe
            {
                fixed (uint* src = _underlayRenderer.Pixels)
                {
                    for (var y = 0; y < h; y++)
                    {
                        Buffer.MemoryCopy(src + (y * w), (byte*)fb.Address + (y * fb.RowBytes), fb.RowBytes, w * 4);
                    }
                }
            }
        }

        context.DrawImage(_underlayBitmap, new Rect(0, 0, w, h), new Rect(size));
    }

    /// <summary>The model edge drawn behind the sketch at a sketch-space point, from the last underlay render.</summary>
    private (Body Body, int Edge)? PickUnderlayEdge(SkVec2 at)
    {
        if (_underlayScene is null || _underlayRenderer.Width <= 1)
        {
            return null;
        }

        var screen = Canvas.ToScreen(at);
        var scale = RenderScaling;
        var id = _underlayRenderer.Pick((int)(screen.X * scale), (int)(screen.Y * scale), (int)Math.Ceiling(5 * scale));
        return _underlayScene.Target(id) is EdgePick edge ? (edge.Body, edge.Index) : null;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _underlayBitmap?.Dispose();
        _underlayBitmap = null;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Viewport.Focus();
        Viewport.ZoomToFit();
    }

    private static KeyModifiers CommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    private void OnAnyLostFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox { DataContext: NumberField field })
        {
            field.Commit();
        }
        else if (e.Source is TextBox { DataContext: TextField text })
        {
            text.Commit();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox)
        {
            return;
        }

        var command = e.KeyModifiers.HasFlag(CommandModifier);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (command)
        {
            switch (e.Key)
            {
                case Key.Z when shift:
                case Key.Y:
                    vm.RedoCommand.Execute(null);
                    break;
                case Key.Z:
                    vm.UndoCommand.Execute(null);
                    break;
                case Key.S when shift:
                    vm.SaveAsCommand.Execute(null);
                    break;
                case Key.S:
                    vm.SaveCommand.Execute(null);
                    break;
                case Key.O:
                    vm.OpenCommand.Execute(null);
                    break;
                case Key.N:
                    vm.NewCommand.Execute(null);
                    break;
                case Key.D1: vm.ShowViewCommand.Execute("front"); break;
                case Key.D2: vm.ShowViewCommand.Execute("back"); break;
                case Key.D3: vm.ShowViewCommand.Execute("left"); break;
                case Key.D4: vm.ShowViewCommand.Execute("right"); break;
                case Key.D5: vm.ShowViewCommand.Execute("top"); break;
                case Key.D6: vm.ShowViewCommand.Execute("bottom"); break;
                case Key.D7: vm.ShowViewCommand.Execute("isometric"); break;
                case Key.D8: vm.ShowViewCommand.Execute("normal"); break;
                default:
                    return;
            }

            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.None && e.KeyModifiers != KeyModifiers.Shift)
        {
            return;
        }

        if (vm.Sketch is { } sketch)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    vm.Escape();
                    break;
                case Key.Enter:
                    sketch.Editor.Tool.Finish();
                    sketch.Editor.Invalidate();
                    break;
                case Key.Delete:
                case Key.Back:
                    sketch.DeleteCommand.Execute(null);
                    break;
                case Key.F:
                    Canvas.ZoomToFit();
                    break;
                case Key.X:
                    sketch.ToggleConstructionCommand.Execute(null);
                    break;
                default:
                    var tool = sketch.Tools.FirstOrDefault(t => t.Shortcut.Length == 1 && e.Key.ToString().Length == 1 && t.Shortcut[0] == e.Key.ToString()[0]);
                    if (tool is null)
                    {
                        return;
                    }

                    tool.Activate.Execute(null);
                    break;
            }

            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                vm.Escape();
                break;
            case Key.Enter when vm.Editor is not null:
                vm.AcceptEditorCommand.Execute(null);
                break;
            case Key.Delete:
            case Key.Back:
                vm.DeleteSelectedCommand.Execute(null);
                break;
            case Key.F:
                Viewport.ZoomToFit();
                break;
            case Key.Left:
                Viewport.Rotate(shift ? 90 : 15, 0);
                break;
            case Key.Right:
                Viewport.Rotate(shift ? -90 : -15, 0);
                break;
            case Key.Up:
                Viewport.Rotate(0, shift ? 90 : 15);
                break;
            case Key.Down:
                Viewport.Rotate(0, shift ? -90 : -15);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ShowDimensionInput(DimensionConstraint dim)
    {
        _editing = dim;
        var label = Canvas.ToScreen(DimensionGeometry.Layout(dim).Label);
        var value = dim.IsAngular ? dim.Value * 180 / Math.PI : dim.Value;
        DimensionInput.Text = value.ToString("0.####", CultureInfo.InvariantCulture);
        Avalonia.Controls.Canvas.SetLeft(DimensionInput, Math.Clamp(label.X - DimensionInput.Width / 2, 4, Math.Max(4, Overlay.Bounds.Width - DimensionInput.Width - 4)));
        Avalonia.Controls.Canvas.SetTop(DimensionInput, Math.Clamp(label.Y + 14, 4, Math.Max(4, Overlay.Bounds.Height - 40)));
        DimensionInput.IsVisible = true;
        DimensionInput.Focus();
        DimensionInput.SelectAll();
    }

    private void HideDimensionInput()
    {
        _editing = null;
        DimensionInput.IsVisible = false;
        Canvas.Focus();
    }

    private void OnDimensionInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideDimensionInput();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _editing is { } dim && ViewModel?.Sketch is { } sketch && ViewModel is { } vm)
        {
            e.Handled = true;
            var text = DimensionInput.Text?.Trim() ?? string.Empty;
            double value;
            if (text.StartsWith('='))
            {
                if (vm.LinkDimension(dim.Id, text[1..].Trim()) is not { } linked)
                {
                    sketch.Editor.SetMessage("That equation can't be evaluated. Check the variable names in Equations.");
                    return;
                }

                value = linked;
            }
            else if (!ValueParser.TryParse(text, out value))
            {
                sketch.Editor.SetMessage("Enter a number (25, 25.4*2, 1in) or an equation (=Width/2).");
                return;
            }
            else
            {
                vm.LinkDimension(dim.Id, null);
            }

            if (sketch.Editor.SetDimensionValue(dim, value))
            {
                HideDimensionInput();
            }
        }
    }
}
