using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Chamfer.Geometry;
using Chamfer.Viewport;

namespace Chamfer.App.Controls;

/// <summary>Arguments for a pick in the 3D view.</summary>
public sealed class PickEventArgs(int id, KeyModifiers modifiers, int clickCount) : EventArgs
{
    /// <summary>Scene pick id, or 0 for empty space.</summary>
    public int Id { get; } = id;
    public KeyModifiers Modifiers { get; } = modifiers;
    public int ClickCount { get; } = clickCount;
}

/// <summary>
/// The 3D model view. Draws a <see cref="Viewport.Scene"/> with the software
/// renderer and handles view manipulation: middle-drag (or Alt+left-drag)
/// rotates about the point under the cursor, Ctrl+middle or right-drag pans,
/// Shift+middle-drag and the wheel zoom. Left clicks pick faces, edges and
/// overlays; hovering reports what's under the cursor.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Controls aren't disposable; the bitmap is released when the control leaves the visual tree.")]
public sealed class ModelViewport : Control
{
    private readonly SoftwareRenderer _renderer = new();
    private WriteableBitmap? _bitmap;
    private Scene _scene = new();
    private DragMode _drag;
    private Point _last;
    private Point _pressedAt;
    private Vec3? _pivot;
    private int _hover;
    private bool _moved;

    private enum DragMode
    {
        None,
        Orbit,
        Pan,
        Zoom,
        PendingRightPan,
        LeftPress,
    }

    public ModelViewport()
    {
        Focusable = true;
        ClipToBounds = true;
        PointerTouchPadGestureMagnify += (_, e) =>
        {
            ZoomAt(e.GetPosition(this), 1 / (1 + e.Delta.X));
            e.Handled = true;
        };
    }

    public Camera Camera { get; } = new();

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _bitmap?.Dispose();
        _bitmap = null;
    }

    public Scene Scene
    {
        get => _scene;
        set
        {
            _scene = value;
            _still = null;
            InvalidateVisual();
        }
    }

    private (uint[] Pixels, int Width, int Height)? _still;

    /// <summary>Shows a finished image (a photorealistic render) until the view or model changes.</summary>
    public void ShowStill(uint[] pixels, int width, int height)
    {
        _still = (pixels, width, height);
        InvalidateVisual();
    }

    /// <summary>The viewport's size in device pixels, for renders that should fill it.</summary>
    public (int Width, int Height) PixelSize => (PixelWidth, PixelHeight);

    /// <summary>Raised when the pick id under the cursor changes.</summary>
    public event EventHandler<int>? HoverChanged;

    /// <summary>Raised on a left click (without dragging).</summary>
    public event EventHandler<PickEventArgs>? Picked;

    /// <summary>Raised when the view changes (camera moved or resized).</summary>
    public event EventHandler? ViewChanged;

    /// <summary>The renderer's pixels per device-independent pixel.</summary>
    private double PixelScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;

    private int PixelWidth => Math.Max(1, (int)(Bounds.Width * PixelScale));
    private int PixelHeight => Math.Max(1, (int)(Bounds.Height * PixelScale));

    public void ZoomToFit()
    {
        _still = null;
        var box = _scene.Bounds;
        if (box.IsEmpty)
        {
            box = new Box3(new Vec3(-50, -50, -50), new Vec3(50, 50, 50));
        }

        Camera.Fit(box, PixelWidth, PixelHeight);
        Changed();
    }

    public void SetView(StandardView view, bool fit = true)
    {
        _still = null;
        Camera.SetView(view);
        if (fit)
        {
            ZoomToFit();
        }
        else
        {
            Changed();
        }
    }

    public void NormalTo(Plane3 plane)
    {
        Camera.NormalTo(plane);
        ZoomToFit();
    }

    /// <summary>Rotates the view by fixed steps (arrow keys).</summary>
    public void Rotate(double yawDegrees, double pitchDegrees)
    {
        _still = null;
        var rad = Math.PI / 180;
        Camera.Orbit(yawDegrees * rad / 0.008, pitchDegrees * rad / 0.008);
        Changed();
    }

    private void Changed()
    {
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ input

    private static bool CommandKey(KeyModifiers m) => m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _still = null;
        Focus();
        var point = e.GetCurrentPoint(this);
        var props = point.Properties;
        _last = _pressedAt = point.Position;
        _moved = false;
        e.Pointer.Capture(this);
        e.Handled = true;

        var m = e.KeyModifiers;
        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && m.HasFlag(KeyModifiers.Alt)))
        {
            _drag = CommandKey(m) || (m.HasFlag(KeyModifiers.Alt) && m.HasFlag(KeyModifiers.Shift)) ? DragMode.Pan
                : m.HasFlag(KeyModifiers.Shift) ? DragMode.Zoom
                : DragMode.Orbit;
            _pivot = _drag == DragMode.Orbit ? PointUnder(point.Position) : null;
        }
        else if (props.IsRightButtonPressed)
        {
            _drag = DragMode.PendingRightPan;
        }
        else if (props.IsLeftButtonPressed)
        {
            _drag = DragMode.LeftPress;
            if (e.ClickCount >= 2)
            {
                Picked?.Invoke(this, new PickEventArgs(PickAt(point.Position), m, e.ClickCount));
                _drag = DragMode.None;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var dx = (p.X - _last.X) * PixelScale;
        var dy = (p.Y - _last.Y) * PixelScale;
        if (Math.Abs(p.X - _pressedAt.X) + Math.Abs(p.Y - _pressedAt.Y) > 3)
        {
            _moved = true;
        }

        switch (_drag)
        {
            case DragMode.Orbit:
                Camera.Orbit(dx / PixelScale, dy / PixelScale, _pivot);
                Changed();
                break;
            case DragMode.Pan:
                Camera.Pan(dx, dy, PixelHeight);
                Changed();
                break;
            case DragMode.Zoom:
                Camera.Zoom(Math.Pow(1.01, dy / PixelScale), _pressedAt.X * PixelScale, _pressedAt.Y * PixelScale, PixelWidth, PixelHeight);
                Changed();
                break;
            case DragMode.PendingRightPan when _moved:
                _drag = DragMode.Pan;
                break;
            case DragMode.None:
                var id = PickAt(p);
                if (id != _hover)
                {
                    _hover = id;
                    HoverChanged?.Invoke(this, id);
                }

                break;
        }

        _last = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_drag == DragMode.LeftPress && !_moved)
        {
            Picked?.Invoke(this, new PickEventArgs(PickAt(e.GetPosition(this)), e.KeyModifiers, 1));
        }

        var wasDragging = _drag is DragMode.Orbit or DragMode.Pan or DragMode.Zoom;
        _drag = DragMode.None;
        if (wasDragging)
        {
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover != 0 && _drag == DragMode.None)
        {
            _hover = 0;
            HoverChanged?.Invoke(this, 0);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _still = null;
        e.Handled = true;
        if (OperatingSystem.IsMacOS() && !CommandKey(e.KeyModifiers))
        {
            // Trackpad: two-finger scroll orbits, pinch zooms.
            Camera.Orbit(-e.Delta.X * 6, -e.Delta.Y * 6);
            Changed();
            return;
        }

        ZoomAt(e.GetPosition(this), Math.Pow(0.87, e.Delta.Y));
    }

    private void ZoomAt(Point p, double factor)
    {
        Camera.Zoom(factor, p.X * PixelScale, p.Y * PixelScale, PixelWidth, PixelHeight);
        Changed();
    }

    private int PickAt(Point p)
    {
        if (_renderer.Width != PixelWidth || _renderer.Height != PixelHeight)
        {
            return 0;
        }

        return _renderer.Pick((int)(p.X * PixelScale), (int)(p.Y * PixelScale), (int)Math.Ceiling(3 * PixelScale));
    }

    private Vec3? PointUnder(Point p) =>
        _renderer.Width == PixelWidth ? _renderer.PointAt((int)(p.X * PixelScale), (int)(p.Y * PixelScale)) : null;

    // ----------------------------------------------------------------- render

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            InvalidateVisual();
        }
    }

    /// <summary>Renders the current scene into a pixel buffer (also used for screenshots).</summary>
    public SoftwareRenderer RenderFrame()
    {
        _renderer.Resize(PixelWidth, PixelHeight);
        _renderer.Render(_scene, Camera);
        return _renderer;
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        uint[] pixels;
        int width, height;
        if (_still is { } still)
        {
            (pixels, width, height) = still;
        }
        else
        {
            var r = RenderFrame();
            (pixels, width, height) = (r.Pixels, r.Width, r.Height);
        }

        if (_bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96 * PixelScale, 96 * PixelScale), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }

        using (var fb = _bitmap.Lock())
        {
            unsafe
            {
                fixed (uint* src = pixels)
                {
                    var rowBytes = width * 4;
                    for (var y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(src + (y * width), (byte*)fb.Address + (y * fb.RowBytes), fb.RowBytes, rowBytes);
                    }
                }
            }
        }

        context.DrawImage(_bitmap, new Rect(0, 0, width, height), new Rect(Bounds.Size));
        DrawTriad(context);
    }

    /// <summary>The XYZ axis triad in the lower-left corner.</summary>
    private void DrawTriad(DrawingContext context)
    {
        var origin = new Point(44, Bounds.Height - 44);
        const double length = 30;
        var axes = new (Vec3 Dir, string Label, IBrush Brush)[]
        {
            (Vec3.UnitX, "X", new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26))),
            (Vec3.UnitY, "Y", new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A))),
            (Vec3.UnitZ, "Z", new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB))),
        };

        // Draw back-to-front so axes pointing at the viewer stay on top.
        foreach (var (dir, label, brush) in axes.OrderBy(a => Vec3.Dot(a.Dir, Camera.Back)))
        {
            var x = Vec3.Dot(dir, Camera.Right) * length;
            var y = -Vec3.Dot(dir, Camera.Up) * length;
            var end = new Point(origin.X + x, origin.Y + y);
            context.DrawLine(new Pen(brush, 2), origin, end);
            var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, 11, brush);
            context.DrawText(text, new Point(end.X + (x >= 0 ? 2 : -10), end.Y - 7));
        }
    }
}
