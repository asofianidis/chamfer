using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Chamfer.Drawing;
using Chamfer.Geometry;
using DPen = Chamfer.Drawing.Pen;

namespace Chamfer.App.Controls;

/// <summary>Arguments for a click on a drawing sheet.</summary>
public sealed class SheetClickEventArgs(Point2 at, string? owner, KeyModifiers modifiers) : EventArgs
{
    /// <summary>Sheet coordinates in mm.</summary>
    public Point2 At { get; } = at;

    /// <summary>The view or dimension under the click, if any.</summary>
    public string? Owner { get; } = owner;

    public KeyModifiers Modifiers { get; } = modifiers;
}

/// <summary>Arguments for dragging a view on the sheet.</summary>
public sealed class SheetDragEventArgs(string owner, Point2 delta, bool finished) : EventArgs
{
    public string Owner { get; } = owner;

    /// <summary>Movement in sheet mm since the last event.</summary>
    public Point2 Delta { get; } = delta;

    public bool Finished { get; } = finished;
}

/// <summary>
/// Shows a drawing sheet on a desk: paper, views, dimensions and annotations.
/// The wheel zooms at the cursor, middle- or right-drag pans, left-click
/// selects, and dragging a view moves it.
/// </summary>
public sealed class DrawingCanvas : Control
{
    private SheetGraphics? _graphics;
    private double _scale = 2; // pixels per mm
    private Point2 _center = new(210, 148);
    private Point _last;
    private Point _down;
    private bool _panning;
    private string? _dragging;
    private bool _moved;

    public DrawingCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public static readonly DirectProperty<DrawingCanvas, SheetGraphics?> GraphicsProperty =
        AvaloniaProperty.RegisterDirect<DrawingCanvas, SheetGraphics?>(nameof(Graphics), c => c.Graphics, (c, v) => c.Graphics = v);

    public SheetGraphics? Graphics
    {
        get => _graphics;
        set
        {
            var first = _graphics is null || value is null || _graphics.Width != value.Width || _graphics.Height != value.Height;
            _graphics = value;
            if (first)
            {
                Fit();
            }

            InvalidateVisual();
        }
    }

    /// <summary>Views and dimensions drawn highlighted.</summary>
    public IReadOnlySet<string> Selected { get; set; } = new HashSet<string>();

    public event EventHandler<SheetClickEventArgs>? SheetClicked;

    public event EventHandler<SheetDragEventArgs>? ViewDragged;

    public void Fit()
    {
        if (_graphics is null || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        _center = new Point2(_graphics.Width / 2, _graphics.Height / 2);
        _scale = Math.Min(Bounds.Width / (_graphics.Width * 1.08), Bounds.Height / (_graphics.Height * 1.08));
        InvalidateVisual();
    }

    private Point ToScreen(Point2 p) => new(((p.X - _center.X) * _scale) + (Bounds.Width / 2), (Bounds.Height / 2) - ((p.Y - _center.Y) * _scale));

    public Point2 ToSheet(Point s) => new(((s.X - (Bounds.Width / 2)) / _scale) + _center.X, (((Bounds.Height / 2) - s.Y) / _scale) + _center.Y);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && change.OldValue is Rect { Width: < 1 })
        {
            Fit();
        }
    }

    // ------------------------------------------------------------------ input

    /// <summary>The view or dimension nearest a sheet point, within a few pixels.</summary>
    public string? OwnerAt(Point2 p)
    {
        if (_graphics is null)
        {
            return null;
        }

        var tol = 4 / _scale;
        string? best = null;
        var bestD = tol;
        foreach (var (prim, owner) in _graphics.Owners)
        {
            var d = prim switch
            {
                PolylinePrimitive pl => Distance(pl.Points, pl.Closed, p),
                TextPrimitive t => Point2.Distance(t.At, p) - t.Height,
                ArrowPrimitive a => Point2.Distance(a.Tip, p),
                _ => double.PositiveInfinity,
            };
            if (d < bestD)
            {
                bestD = d;
                best = owner;
            }
        }

        return best;
    }

    private static double Distance(IReadOnlyList<Point2> pts, bool closed, Point2 p)
    {
        var best = double.PositiveInfinity;
        var n = closed ? pts.Count : pts.Count - 1;
        for (var i = 0; i < n; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            var ab = b - a;
            var t = Math.Clamp(Point2.Dot(p - a, ab) / Math.Max(Point2.Dot(ab, ab), 1e-18), 0, 1);
            best = Math.Min(best, Point2.Distance(p, a + (ab * t)));
        }

        return best;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        _last = _down = pt.Position;
        _moved = false;
        e.Pointer.Capture(this);
        e.Handled = true;
        if (pt.Properties.IsMiddleButtonPressed || pt.Properties.IsRightButtonPressed)
        {
            _panning = true;
            return;
        }

        var owner = OwnerAt(ToSheet(pt.Position));
        _dragging = owner is not null && owner.StartsWith('v') ? owner : null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _down.X) + Math.Abs(p.Y - _down.Y) > 3)
        {
            _moved = true;
        }

        if (_panning)
        {
            _center = new Point2(_center.X - ((p.X - _last.X) / _scale), _center.Y + ((p.Y - _last.Y) / _scale));
            InvalidateVisual();
        }
        else if (_dragging is not null && _moved)
        {
            ViewDragged?.Invoke(this, new SheetDragEventArgs(_dragging, new Point2((p.X - _last.X) / _scale, -(p.Y - _last.Y) / _scale), false));
        }

        _last = p;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_dragging is not null && _moved)
        {
            ViewDragged?.Invoke(this, new SheetDragEventArgs(_dragging, default, true));
        }
        else if (!_panning && !_moved)
        {
            var at = ToSheet(e.GetPosition(this));
            SheetClicked?.Invoke(this, new SheetClickEventArgs(at, OwnerAt(at), e.KeyModifiers));
        }

        _panning = false;
        _dragging = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        var p = e.GetPosition(this);
        var before = ToSheet(p);
        _scale = Math.Clamp(_scale * Math.Pow(1.15, e.Delta.Y), 0.05, 200);
        var after = ToSheet(p);
        _center = _center + (before - after);
        InvalidateVisual();
    }

    // ----------------------------------------------------------------- render

    public override void Render(DrawingContext context)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        context.FillRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x2A, 0x2D, 0x33) : Color.FromRgb(0xD5, 0xDA, 0xE0)), new Rect(Bounds.Size));
        if (_graphics is not { } g)
        {
            return;
        }

        var paper = new Rect(ToScreen(new Point2(0, g.Height)), ToScreen(new Point2(g.Width, 0)));
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), paper.Translate(new Vector(4, 4)));
        context.FillRectangle(Brushes.White, paper);

        var ink = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16));
        var highlight = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
        foreach (var item in g.Items)
        {
            var selected = g.Owners.TryGetValue(item, out var owner) && Selected.Contains(owner);
            var brush = selected ? highlight
                : g.Layers.TryGetValue(item, out var layer) && g.LayerColors.TryGetValue(layer, out var rgb) && rgb != 0
                    ? new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb))
                    : ink;
            switch (item)
            {
                case PolylinePrimitive pl when pl.Points.Count > 1:
                    var width = Math.Max(0.8, SheetGraphics.PenWidth(pl.Pen) * _scale);
                    var dashes = SheetGraphics.Dashes(pl.Pen);
                    var pen = new Avalonia.Media.Pen(brush, width, dashes.Length > 0 ? new DashStyle(dashes.Select(d => d * _scale / width), 0) : null, PenLineCap.Round, PenLineJoin.Round);
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        ctx.BeginFigure(ToScreen(pl.Points[0]), false);
                        foreach (var q in pl.Points.Skip(1))
                        {
                            ctx.LineTo(ToScreen(q));
                        }

                        ctx.EndFigure(pl.Closed);
                    }

                    context.DrawGeometry(null, pen, geometry);
                    break;
                case ArrowPrimitive a:
                    var (l, r) = Exporters.ArrowWings(a);
                    var tri = new StreamGeometry();
                    using (var ctx = tri.Open())
                    {
                        ctx.BeginFigure(ToScreen(a.Tip), true);
                        ctx.LineTo(ToScreen(l));
                        ctx.LineTo(ToScreen(r));
                        ctx.EndFigure(true);
                    }

                    context.DrawGeometry(brush, null, tri);
                    break;
                case TextPrimitive t:
                    var size = t.Height * 1.4 * _scale;
                    if (size < 2)
                    {
                        break;
                    }

                    var text = new FormattedText(t.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
                    var at = ToScreen(t.At);
                    var dx = t.Anchor switch { TextAnchor.Center => -text.Width / 2, TextAnchor.Right => -text.Width, _ => 0 };
                    var origin = new Point(dx, -text.Baseline);
                    using (context.PushTransform(Matrix.CreateRotation(-t.Angle * Math.PI / 180) * Matrix.CreateTranslation(at.X, at.Y)))
                    {
                        context.DrawText(text, origin);
                    }

                    break;
            }
        }
    }
}
