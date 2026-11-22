using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Chamfer.App.Editing;
using Chamfer.App.Editing.Tools;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.App.Controls;

/// <summary>
/// The 2D sketch view: draws the sketch, handles pan/zoom, and forwards pointer input to the active tool.
/// Sketch coordinates are millimetres with Y up.
/// </summary>
public sealed class SketchCanvas : Control
{
    public static readonly StyledProperty<SketchEditor?> EditorProperty =
        AvaloniaProperty.Register<SketchCanvas, SketchEditor?>(nameof(Editor));

    private const int ArcSegments = 72;

    private Vec2 _center = Vec2.Zero;
    private double _scale = 4; // pixels per millimetre
    private bool _rightDown;
    private bool _panning;
    private Point _panLast;
    private Point _rightDownAt;
    private SketchEditor? _subscribed;

    public SketchCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        PointerTouchPadGestureMagnify += OnMagnify;
    }

    public SketchEditor? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>The cursor moved; carries the cursor position in sketch coordinates.</summary>
    public event EventHandler<Vec2>? CursorMoved;

    /// <summary>
    /// Draws what lies behind the sketch (the part, seen normal to the sketch
    /// plane). Called with the canvas size, view centre and scale (pixels per mm).
    /// </summary>
    public Action<DrawingContext, Size, Vec2, double>? Underlay { get; set; }

    /// <summary>The view centre in sketch coordinates and the scale in pixels per millimetre.</summary>
    public (Vec2 Center, double Scale) View => (_center, _scale);

    /// <summary>Sets the view centre (sketch coordinates) and scale (pixels per millimetre).</summary>
    public void SetView(Vec2 center, double scale)
    {
        _center = center;
        SetScale(scale);
    }

    private SketchPalette Palette => ActualThemeVariant == ThemeVariant.Dark ? SketchPalette.Dark : SketchPalette.Light;

    // ------------------------------------------------------------------ View transform

    public Point ToScreen(Vec2 w) =>
        new((w.X - _center.X) * _scale + Bounds.Width / 2, Bounds.Height / 2 - (w.Y - _center.Y) * _scale);

    public Vec2 ToWorld(Point s) =>
        new((s.X - Bounds.Width / 2) / _scale + _center.X, (Bounds.Height / 2 - s.Y) / _scale + _center.Y);

    private void SetScale(double scale)
    {
        _scale = Math.Clamp(scale, 1e-3, 1e5);
        if (Editor is { } editor)
        {
            editor.PixelSize = 1 / _scale;
        }

        InvalidateVisual();
    }

    private void ZoomAt(Point screen, double factor)
    {
        var before = ToWorld(screen);
        SetScale(_scale * factor);
        var after = ToWorld(screen);
        _center += before - after;
        InvalidateVisual();
    }

    /// <summary>Zooms and pans so the whole sketch is visible.</summary>
    public void ZoomToFit()
    {
        if (Editor is not { } editor || Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }

        var points = editor.Sketch.Entities.SelectMany(SamplePoints).Append(Vec2.Zero).ToList();
        var min = new Vec2(points.Min(p => p.X), points.Min(p => p.Y));
        var max = new Vec2(points.Max(p => p.X), points.Max(p => p.Y));
        var size = max - min;
        var extent = Math.Max(size.X / Bounds.Width, size.Y / Bounds.Height);
        _center = Vec2.Lerp(min, max, 0.5);
        SetScale(extent < 1e-9 ? 4 : 0.8 / extent);
    }

    private static IEnumerable<Vec2> SamplePoints(SketchEntity e) => e switch
    {
        SketchCircle c => [c.Center.Position - new Vec2(c.Radius, c.Radius), c.Center.Position + new Vec2(c.Radius, c.Radius)],
        SketchEllipse el => el.Sample(16),
        SketchArc a => [a.Start.Position, a.End.Position, a.Midpoint],
        SketchLine l => [l.Start.Position, l.End.Position],
        SketchPoint p => [p.Position],
        _ => [],
    };

    // ------------------------------------------------------------------ Editor wiring

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty)
        {
            if (_subscribed is not null)
            {
                _subscribed.Invalidated -= OnEditorInvalidated;
                _subscribed.Changed -= OnEditorInvalidated;
            }

            _subscribed = Editor;
            if (_subscribed is not null)
            {
                _subscribed.Invalidated += OnEditorInvalidated;
                _subscribed.Changed += OnEditorInvalidated;
                _subscribed.PixelSize = 1 / _scale;
            }

            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty || change.Property.Name == nameof(ActualThemeVariant))
        {
            InvalidateVisual();
        }
    }

    private void OnEditorInvalidated(object? sender, EventArgs e) => InvalidateVisual();

    // ------------------------------------------------------------------ Input

    private static bool IsToggleModifier(KeyModifiers m) =>
        m.HasFlag(KeyModifiers.Shift) || m.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    private ToolPointer MakePointer(PointerEventArgs e, int clickCount = 1)
    {
        var world = ToWorld(e.GetPosition(this));
        var tool = Editor!.Tool;
        var snap = tool.UsesSnapping ? Editor.FindSnap(world) : Snap.None(world);
        return new ToolPointer(world, snap, IsToggleModifier(e.KeyModifiers), clickCount);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        e.Pointer.Capture(this);
        e.Handled = true;

        if (point.Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _panLast = point.Position;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // Right-drag pans; a right-click without moving finishes the current operation.
            _rightDown = true;
            _rightDownAt = point.Position;
            _panLast = point.Position;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && Editor is { } editor)
        {
            editor.Tool.PointerDown(MakePointer(e, e.ClickCount));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_rightDown && !_panning && Distance(position, _rightDownAt) > 4)
        {
            _panning = true;
        }

        if (_panning)
        {
            var delta = position - _panLast;
            _center -= new Vec2(delta.X / _scale, -delta.Y / _scale);
            _panLast = position;
            InvalidateVisual();
            return;
        }

        if (Editor is { } editor)
        {
            var p = MakePointer(e);
            editor.Tool.PointerMove(p);
            CursorMoved?.Invoke(this, p.Snap.Position);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            if (!_panning)
            {
                Editor?.Tool.Finish();
                Editor?.Invalidate();
            }

            _rightDown = false;
            _panning = false;
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Middle)
        {
            _panning = false;
            return;
        }

        if (e.InitialPressMouseButton == MouseButton.Left && Editor is { } editor)
        {
            editor.Tool.PointerUp(MakePointer(e));
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        e.Handled = true;
        var zoomModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        // macOS trackpads scroll in two dimensions: scrolling pans, pinching (or Cmd+scroll) zooms.
        // Elsewhere the mouse wheel zooms about the cursor, as in most CAD tools.
        if (OperatingSystem.IsMacOS() && !zoomModifier)
        {
            _center -= new Vec2(-e.Delta.X * 10 / _scale, e.Delta.Y * 10 / _scale);
            InvalidateVisual();
            return;
        }

        ZoomAt(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
    }

    private void OnMagnify(object? sender, PointerDeltaEventArgs e)
    {
        ZoomAt(e.GetPosition(this), 1 + e.Delta.X);
        e.Handled = true;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ------------------------------------------------------------------ Rendering

    public override void Render(DrawingContext context)
    {
        var palette = Palette;
        context.FillRectangle(palette.Background, new Rect(Bounds.Size));
        Underlay?.Invoke(context, Bounds.Size, _center, _scale);
        DrawGrid(context, palette);
        DrawOrigin(context, palette);
        if (Editor is not { } editor)
        {
            return;
        }

        var overDefined = editor.Analysis.RedundantConstraints
            .SelectMany(c => c.References)
            .SelectMany(r => r.Points.Cast<SketchEntity>().Append(r).Append(r.Root))
            .ToHashSet();

        IBrush ColorOf(SketchEntity e)
        {
            if (editor.IsSelected(e) || (e.Owner is { } o && editor.IsSelected(o) && e is not SketchPoint))
            {
                return palette.Selected;
            }

            if (ReferenceEquals(editor.Hover, e))
            {
                return palette.Hover;
            }

            if (overDefined.Contains(e) || editor.Analysis.SolveFailed)
            {
                return palette.OverDefined;
            }

            return editor.Analysis.IsFullyDefined(e) ? palette.FullyDefined : palette.UnderDefined;
        }

        foreach (var e in editor.Sketch.Entities.Where(e => e is not SketchPoint))
        {
            var emphasized = editor.IsSelected(e) || ReferenceEquals(editor.Hover, e);
            var pen = MakePen(ColorOf(e), emphasized ? 2.6 : 1.6, e.IsConstruction);
            DrawEntity(context, e, pen);
        }

        foreach (var point in editor.Sketch.AllPoints)
        {
            var brush = ColorOf(point);
            var r = editor.IsSelected(point) || ReferenceEquals(editor.Hover, point) ? 4.5 : 3;
            context.DrawEllipse(brush, null, ToScreen(point.Position), r, r);
        }

        foreach (var dim in editor.Sketch.Constraints.OfType<DimensionConstraint>())
        {
            var brush = editor.IsSelected(dim) ? palette.Selected
                : ReferenceEquals(editor.HoverDimension, dim) ? palette.Hover
                : editor.Analysis.IsRedundant(dim) ? palette.OverDefined
                : dim.IsDriving ? palette.Dimension
                : palette.DrivenDimension;
            DrawDimension(context, dim, brush, palette);
        }

        DrawRelationGlyphs(context, editor, palette);

        foreach (var item in editor.Tool.Preview)
        {
            DrawPreview(context, item, palette);
        }
    }

    private static Pen MakePen(IBrush brush, double thickness, bool dashed = false) =>
        new(brush, thickness, dashed ? new DashStyle([6, 4], 0) : null, PenLineCap.Round, PenLineJoin.Round);

    private void DrawGrid(DrawingContext context, SketchPalette palette)
    {
        // Minor spacing is the smallest 1-2-5 step that's at least 12 pixels.
        var raw = 12 / _scale;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1.0, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        var major = step * (Math.Abs(step / magnitude - 2) < 1e-9 ? 5 : 10);

        var topLeft = ToWorld(new Point(0, 0));
        var bottomRight = ToWorld(new Point(Bounds.Width, Bounds.Height));
        var minorPen = new Pen(palette.GridMinor, 1);
        var majorPen = new Pen(palette.GridMajor, 1);

        for (var x = Math.Floor(topLeft.X / step) * step; x <= bottomRight.X; x += step)
        {
            var sx = Math.Round(ToScreen(new Vec2(x, 0)).X) + 0.5;
            var isMajor = Math.Abs(Math.IEEERemainder(x, major)) < step / 2;
            context.DrawLine(isMajor ? majorPen : minorPen, new Point(sx, 0), new Point(sx, Bounds.Height));
        }

        for (var y = Math.Floor(bottomRight.Y / step) * step; y <= topLeft.Y; y += step)
        {
            var sy = Math.Round(ToScreen(new Vec2(0, y)).Y) + 0.5;
            var isMajor = Math.Abs(Math.IEEERemainder(y, major)) < step / 2;
            context.DrawLine(isMajor ? majorPen : minorPen, new Point(0, sy), new Point(Bounds.Width, sy));
        }
    }

    private void DrawOrigin(DrawingContext context, SketchPalette palette)
    {
        var o = ToScreen(Vec2.Zero);
        var pen = MakePen(palette.Origin, 1.6);
        const double len = 22;
        context.DrawLine(pen, o, new Point(o.X + len, o.Y));
        context.DrawLine(pen, o, new Point(o.X, o.Y - len));
        DrawArrowHead(context, palette.Origin, new Point(o.X + len, o.Y), new Vector(1, 0), 7);
        DrawArrowHead(context, palette.Origin, new Point(o.X, o.Y - len), new Vector(0, -1), 7);
        var highlight = Editor is { } editor && (ReferenceEquals(editor.Hover, editor.Sketch.Origin) || editor.IsSelected(editor.Sketch.Origin));
        context.DrawEllipse(highlight ? palette.Hover : palette.Origin, null, o, highlight ? 4.5 : 3, highlight ? 4.5 : 3);
    }

    private void DrawEntity(DrawingContext context, SketchEntity e, IPen pen)
    {
        switch (e)
        {
            case SketchLine l:
                context.DrawLine(pen, ToScreen(l.Start.Position), ToScreen(l.End.Position));
                break;
            case SketchCircle c:
                context.DrawEllipse(null, pen, ToScreen(c.Center.Position), c.Radius * _scale, c.Radius * _scale);
                break;
            case SketchArc a:
                DrawArc(context, pen, a.Center.Position, a.Radius, a.StartAngle, a.Sweep);
                break;
            case SketchEllipse el:
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var pts = el.Sample(ArcSegments).ToList();
                    g.BeginFigure(ToScreen(pts[0]), false);
                    foreach (var q in pts.Skip(1))
                    {
                        g.LineTo(ToScreen(q));
                    }

                    g.EndFigure(true);
                }

                context.DrawGeometry(null, pen, geometry);
                break;
        }
    }

    private void DrawArc(DrawingContext context, IPen pen, Vec2 center, double radius, double start, double sweep)
    {
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var n = Math.Max(2, (int)Math.Ceiling(ArcSegments * Math.Abs(sweep) / (2 * Math.PI)));
            g.BeginFigure(ToScreen(center + Vec2.FromAngle(start, radius)), false);
            for (var i = 1; i <= n; i++)
            {
                g.LineTo(ToScreen(center + Vec2.FromAngle(start + sweep * i / n, radius)));
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawArrowHead(DrawingContext context, IBrush brush, Point tip, Vector direction, double size)
    {
        var len = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
        if (len < 1e-12)
        {
            return;
        }

        var d = direction / len;
        var n = new Vector(-d.Y, d.X);
        var back = tip - d * size;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(tip, true);
            g.LineTo(back + n * (size * 0.38));
            g.LineTo(back - n * (size * 0.38));
            g.EndFigure(true);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    private static Vector ScreenDirection(Vec2 worldDirection) => new(worldDirection.X, -worldDirection.Y);

    private void DrawDimension(DrawingContext context, DimensionConstraint dim, IBrush brush, SketchPalette palette)
    {
        var layout = DimensionGeometry.Layout(dim);
        var thin = new Pen(brush, 1);
        foreach (var (a, b) in layout.ExtensionLines)
        {
            // Leave a small gap at the geometry so extension lines don't merge with it.
            var dir = (b - a).Normalized();
            context.DrawLine(thin, ToScreen(a + dir * (3 / _scale)), ToScreen(b + dir * (4 / _scale)));
        }

        foreach (var (a, b) in layout.DimensionLines)
        {
            context.DrawLine(thin, ToScreen(a), ToScreen(b));
        }

        foreach (var (tip, direction) in layout.Arrows)
        {
            DrawArrowHead(context, brush, ToScreen(tip), ScreenDirection(direction), 9);
        }

        if (layout.Arc is { } arc)
        {
            DrawArc(context, thin, arc.Center, arc.Radius, arc.StartAngle, arc.Sweep);
            if (dim is AngleConstraint angle)
            {
                // Extend the lines out to the arc if they stop short of it.
                foreach (var line in (SketchLine[])[angle.A, angle.B])
                {
                    var far = Vec2.Distance(line.Start.Position, arc.Center) > Vec2.Distance(line.End.Position, arc.Center) ? line.Start : line.End;
                    var reach = Vec2.Distance(far.Position, arc.Center);
                    if (reach < arc.Radius)
                    {
                        var dir = (far.Position - arc.Center).Normalized();
                        context.DrawLine(thin, ToScreen(far.Position), ToScreen(arc.Center + dir * (arc.Radius + 3 / _scale)));
                    }
                }
            }
        }

        DrawLabel(context, layout.Text, ToScreen(layout.Label), brush, palette.LabelBackground, 12.5);
    }

    private static void DrawLabel(DrawingContext context, string text, Point center, IBrush foreground, IBrush background, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, foreground);
        var origin = new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2);
        context.DrawRectangle(background, null, new Rect(origin.X - 3, origin.Y - 1, formatted.Width + 6, formatted.Height + 2), 3, 3);
        context.DrawText(formatted, origin);
    }

    private void DrawRelationGlyphs(DrawingContext context, SketchEditor editor, SketchPalette palette)
    {
        // Each entity collects the relations to show next to it.
        var perEntity = new Dictionary<SketchEntity, List<SketchConstraint>>();
        foreach (var c in editor.Sketch.Constraints)
        {
            if (c is DimensionConstraint)
            {
                continue;
            }

            var involved = c.References.Any(r => editor.IsSelected(r) || editor.IsSelected(r.Root)
                || ReferenceEquals(editor.Hover, r) || ReferenceEquals(editor.Hover, r.Root)) || editor.IsSelected(c);
            if (!editor.ShowAllRelations && !involved)
            {
                continue;
            }

            if (c.Kind == ConstraintKind.Coincident && !involved)
            {
                continue;
            }

            var anchor = c.References[0];
            if (!perEntity.TryGetValue(anchor, out var list))
            {
                perEntity[anchor] = list = [];
            }

            list.Add(c);
        }

        foreach (var (entity, constraints) in perEntity)
        {
            var at = ToScreen(GlyphAnchor(entity));
            var x = at.X + 10;
            var y = at.Y - 18;
            foreach (var c in constraints)
            {
                var glyph = RelationRules.Glyph(c.Kind);
                var foreground = editor.Analysis.IsRedundant(c) ? palette.OverDefined : palette.GlyphForeground;
                var formatted = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 11, foreground);
                var rect = new Rect(x, y, Math.Max(formatted.Width + 6, 16), formatted.Height + 2);
                context.DrawRectangle(editor.IsSelected(c) ? palette.Selected : palette.GlyphBackground, null, rect, 3, 3);
                context.DrawText(formatted, new Point(x + (rect.Width - formatted.Width) / 2, y + 1));
                x += rect.Width + 3;
            }
        }
    }

    private static Vec2 GlyphAnchor(SketchEntity e) => e switch
    {
        SketchLine l => l.Midpoint,
        SketchCircle c => c.Center.Position + new Vec2(0, c.Radius),
        SketchEllipse el => el.PointAt(Math.PI / 2),
        SketchArc a => a.Midpoint,
        SketchPoint p => p.Position,
        _ => Vec2.Zero,
    };

    private void DrawPreview(DrawingContext context, PreviewItem item, SketchPalette palette)
    {
        switch (item)
        {
            case PreviewLine l:
                context.DrawLine(MakePen(palette.Preview, 1.6, l.Construction), ToScreen(l.A), ToScreen(l.B));
                break;
            case PreviewCircle c:
                context.DrawEllipse(null, MakePen(palette.Preview, 1.4, c.Construction), ToScreen(c.Center), c.Radius * _scale, c.Radius * _scale);
                break;
            case PreviewArc a:
                DrawArc(context, MakePen(palette.Preview, 1.6), a.Center, a.Radius, a.StartAngle, a.Sweep);
                break;
            case PreviewGlyph g:
            {
                var at = ToScreen(g.At);
                context.DrawEllipse(palette.Preview, null, at, 3, 3);
                DrawLabel(context, g.Text, new Point(at.X + 16, at.Y + 16), palette.GlyphForeground, palette.GlyphBackground, 11);
                break;
            }

            case PreviewBox b:
            {
                var a = ToScreen(b.Corner1);
                var c = ToScreen(b.Corner2);
                var rect = new Rect(new Point(Math.Min(a.X, c.X), Math.Min(a.Y, c.Y)), new Point(Math.Max(a.X, c.X), Math.Max(a.Y, c.Y)));
                var stroke = MakePen(b.Crossing ? palette.CrossingBoxStroke : palette.WindowBoxStroke, 1, b.Crossing);
                context.DrawRectangle(b.Crossing ? palette.CrossingBoxFill : palette.WindowBoxFill, stroke, rect);
                break;
            }

            case PreviewDimension d:
                DrawDimension(context, d.Dimension, palette.Preview, palette);
                break;
        }
    }
}
