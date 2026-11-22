using System.Globalization;
using Chamfer.Assembly;
using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Drawing;

/// <summary>A view's projection: orientation, its lines in view coordinates, hatching, and its placement on the sheet.</summary>
public sealed class ViewGeometry
{
    public required DrawingView View { get; init; }
    public required Vec3 Toward { get; init; }
    public required Vec3 XAxis { get; init; }
    public Vec3 YAxis => Vec3.Cross(Toward, XAxis);
    public required IReadOnlyList<ProjectedLine> Lines { get; init; }
    public IReadOnlyList<(Point2 A, Point2 B)> Hatch { get; init; } = [];
    public required double Scale { get; init; }

    /// <summary>The view-coordinate point placed at the view's sheet position.</summary>
    public required Point2 Center { get; init; }

    public Point2 ToView(Vec3 p) => new(Vec3.Dot(p, XAxis), Vec3.Dot(p, YAxis));

    public Point2 ToSheet(Point2 uv) => new(View.X + ((uv.X - Center.X) * Scale), View.Y + ((uv.Y - Center.Y) * Scale));

    public Point2 ToSheet(Vec3 p) => ToSheet(ToView(p));

    public Point2 FromSheet(Point2 s) => new(Center.X + ((s.X - View.X) / Scale), Center.Y + ((s.Y - View.Y) / Scale));

    /// <summary>The view's extent on the sheet.</summary>
    public (Point2 Min, Point2 Max) SheetBounds
    {
        get
        {
            var pts = Lines.SelectMany(l => l.Points).Select(ToSheet).ToList();
            return pts.Count == 0
                ? (new Point2(View.X - 10, View.Y - 10), new Point2(View.X + 10, View.Y + 10))
                : (new Point2(pts.Min(p => p.X), pts.Min(p => p.Y)), new Point2(pts.Max(p => p.X), pts.Max(p => p.Y)));
        }
    }
}

/// <summary>
/// Generates sheet graphics from a drawing: projects the model for each view
/// (with hidden-line removal), cuts sections and hatches them, clips details,
/// and lays out dimensions, centre marks, notes, the frame and title block.
/// Projections are cached until the model's bodies change.
/// </summary>
public sealed class DrawingRenderer(DrawingDocument doc)
{
    private readonly Dictionary<string, ViewGeometry> _cache = [];
    private IReadOnlyList<Body>? _bodies;

    public static (Vec3 Toward, Vec3 X) Orientation(ViewKind kind) => kind switch
    {
        ViewKind.Front => (Vec3.UnitZ, Vec3.UnitX),
        ViewKind.Back => (-Vec3.UnitZ, -Vec3.UnitX),
        ViewKind.Top => (Vec3.UnitY, Vec3.UnitX),
        ViewKind.Bottom => (-Vec3.UnitY, Vec3.UnitX),
        ViewKind.Right => (Vec3.UnitX, -Vec3.UnitZ),
        ViewKind.Left => (-Vec3.UnitX, Vec3.UnitZ),
        _ => (new Vec3(1, 1, 1).Normalized(), new Vec3(1, 0, -1).Normalized()),
    };

    /// <summary>Projection of one view (cached).</summary>
    public ViewGeometry Geometry(Sheet sheet, DrawingView view)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(view);
        var bodies = doc.Bodies;
        if (!ReferenceEquals(bodies, _bodies))
        {
            _cache.Clear();
            _bodies = bodies;
        }

        var scale = view.Scale ?? sheet.Scale;
        var key = $"{view.Id}|{view.Kind}|{view.ParentId}|{view.Ax},{view.Ay},{view.Bx},{view.By},{view.Radius}|{scale}";
        if (_cache.TryGetValue(key, out var cached))
        {
            // The view's position is read live, so moving a view doesn't need a new projection.
            return cached;
        }

        var geometry = view.Kind switch
        {
            ViewKind.Section => Section(sheet, view, scale),
            ViewKind.Detail => Detail(sheet, view, scale),
            _ => Standard(view, scale),
        };
        _cache[key] = geometry;
        return geometry;
    }

    private Shape? Compound()
    {
        var bodies = doc.Bodies;
        return bodies.Count == 0 ? null : bodies.Count == 1 ? bodies[0].Shape : Modeling.Compound([.. bodies.Select(b => b.Shape)]);
    }

    private ViewGeometry Standard(DrawingView view, double scale)
    {
        var (toward, x) = Orientation(view.Kind);
        var shape = Compound();
        var lines = shape is null ? [] : shape.Project(Vec3.Zero, toward, x, 0.005);
        return new ViewGeometry { View = view, Toward = toward, XAxis = x, Lines = lines, Scale = scale, Center = Middle(lines) };
    }

    private ViewGeometry Section(Sheet sheet, DrawingView view, double scale)
    {
        var parent = sheet.Views.Find(v => v.Id == view.ParentId) ?? throw new InvalidOperationException("The section's parent view is missing.");
        var pg = Geometry(sheet, parent);
        var a = (pg.XAxis * view.Ax) + (pg.YAxis * view.Ay);
        var b = (pg.XAxis * view.Bx) + (pg.YAxis * view.By);
        var dir = (b - a).Normalized();
        var n = Vec3.Cross(dir, pg.Toward).Normalized();
        var lines = new List<ProjectedLine>();
        var hatch = new List<(Point2, Point2)>();
        var shape = Compound();
        if (shape is not null)
        {
            using var tool = Modeling.PlaneFace(new Plane3(a, n, dir), (shape.Bounds.Diagonal * 2) + 10);
            using var split = Modeling.Split(shape, tool);
            var pieces = new List<Shape>();
            for (var i = 1; i <= split.Result.SolidCount; i++)
            {
                var piece = split.Result.Sub(TopoKind.Solid, i);
                if (Vec3.Dot(piece.MassProperties.CenterOfMass - a, n) < 0)
                {
                    pieces.Add(piece);
                }
                else
                {
                    piece.Dispose();
                }
            }

            if (pieces.Count > 0)
            {
                using var kept = Modeling.Compound(pieces);
                lines.AddRange(kept.Project(Vec3.Zero, n, dir, 0.005));
                var segments = new List<(Point2, Point2)>();
                var y = Vec3.Cross(n, dir);
                foreach (var piece in pieces)
                {
                    for (var f = 1; f <= piece.FaceCount; f++)
                    {
                        var info = piece.Face(f);
                        if (info.Surface != SurfaceKind.Plane || Vec3.Dot(info.Normal, n) < 0.999 || Math.Abs(Vec3.Dot(info.Centroid - a, n)) > 1e-6)
                        {
                            continue;
                        }

                        foreach (var e in piece.EdgesOfFace(f))
                        {
                            var pts = piece.SampleEdge(e, 0.01).Select(p => new Point2(Vec3.Dot(p, dir), Vec3.Dot(p, y))).ToList();
                            for (var k = 0; k + 1 < pts.Count; k++)
                            {
                                segments.Add((pts[k], pts[k + 1]));
                            }
                        }
                    }
                }

                hatch.AddRange(Hatching(segments, 3 / scale));
                foreach (var p in pieces)
                {
                    p.Dispose();
                }
            }
        }

        return new ViewGeometry { View = view, Toward = n, XAxis = dir, Lines = lines, Hatch = hatch, Scale = scale, Center = Middle(lines) };
    }

    private ViewGeometry Detail(Sheet sheet, DrawingView view, double scale)
    {
        var parent = sheet.Views.Find(v => v.Id == view.ParentId) ?? throw new InvalidOperationException("The detail's parent view is missing.");
        var pg = Geometry(sheet, parent);
        var c = new Point2(view.Ax, view.Ay);
        var clipped = new List<ProjectedLine>();
        foreach (var line in pg.Lines)
        {
            foreach (var run in ClipToCircle(line.Points, c, view.Radius))
            {
                clipped.Add(new ProjectedLine(line.Kind, run));
            }
        }

        var hatch = pg.Hatch.SelectMany(h => ClipToCircle([h.A, h.B], c, view.Radius)).Where(r => r.Count == 2).Select(r => (r[0], r[1])).ToList();
        return new ViewGeometry { View = view, Toward = pg.Toward, XAxis = pg.XAxis, Lines = clipped, Hatch = hatch, Scale = scale, Center = c };
    }

    private static Point2 Middle(IReadOnlyList<ProjectedLine> lines)
    {
        var pts = lines.SelectMany(l => l.Points).ToList();
        return pts.Count == 0 ? default : new Point2((pts.Min(p => p.X) + pts.Max(p => p.X)) / 2, (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2);
    }

    /// <summary>45° hatch lines inside the region bounded by the segments (even-odd).</summary>
    public static IEnumerable<(Point2 A, Point2 B)> Hatching(IReadOnlyList<(Point2 A, Point2 B)> boundary, double spacing)
    {
        if (boundary.Count == 0 || spacing <= 0)
        {
            yield break;
        }

        // Rotate by -45° so hatch lines become horizontal scanlines.
        var c = Math.Cos(-Math.PI / 4);
        var s = Math.Sin(-Math.PI / 4);
        Point2 Rot(Point2 p) => new((p.X * c) - (p.Y * s), (p.X * s) + (p.Y * c));
        Point2 Unrot(Point2 p) => new((p.X * c) + (p.Y * s), (-p.X * s) + (p.Y * c));
        var segs = boundary.Select(b => (A: Rot(b.A), B: Rot(b.B))).ToList();
        var minY = segs.Min(x => Math.Min(x.A.Y, x.B.Y));
        var maxY = segs.Max(x => Math.Max(x.A.Y, x.B.Y));
        var lines = (int)Math.Min(2000, Math.Ceiling((maxY - minY) / spacing));
        for (var i = 0; i <= lines; i++)
        {
            var y = minY + ((i + 0.5) * spacing);
            var xs = new List<double>();
            foreach (var (a, b) in segs)
            {
                if ((a.Y > y) != (b.Y > y))
                {
                    xs.Add(a.X + ((y - a.Y) * (b.X - a.X) / (b.Y - a.Y)));
                }
            }

            xs.Sort();
            for (var k = 0; k + 1 < xs.Count; k += 2)
            {
                yield return (Unrot(new Point2(xs[k], y)), Unrot(new Point2(xs[k + 1], y)));
            }
        }
    }

    /// <summary>Parts of a polyline inside a circle.</summary>
    public static IEnumerable<IReadOnlyList<Point2>> ClipToCircle(IReadOnlyList<Point2> pts, Point2 c, double r)
    {
        var run = new List<Point2>();
        bool Inside(Point2 p) => Point2.Distance(p, c) <= r;
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (i == 0)
            {
                if (Inside(p))
                {
                    run.Add(p);
                }

                continue;
            }

            var q = pts[i - 1];
            var inQ = Inside(q);
            var inP = Inside(p);
            if (inQ && inP)
            {
                run.Add(p);
                continue;
            }

            // Segment crosses the circle: find crossings by solving |q + t(p - q) - c| = r.
            var d = p - q;
            var f = q - c;
            var a = Point2.Dot(d, d);
            var b = 2 * Point2.Dot(f, d);
            var cc = Point2.Dot(f, f) - (r * r);
            var disc = (b * b) - (4 * a * cc);
            if (a < 1e-18 || disc < 0)
            {
                if (run.Count > 1)
                {
                    yield return run;
                }

                run = [];
                continue;
            }

            var sq = Math.Sqrt(disc);
            var t1 = (-b - sq) / (2 * a);
            var t2 = (-b + sq) / (2 * a);
            if (inQ)
            {
                run.Add(q + (d * Math.Clamp(t2, 0, 1)));
                yield return run;
                run = [];
            }
            else if (inP)
            {
                run = [q + (d * Math.Clamp(t1, 0, 1)), p];
            }
            else if (t1 >= 0 && t2 <= 1)
            {
                yield return [q + (d * t1), q + (d * t2)];
            }
        }

        if (run.Count > 1)
        {
            yield return run;
        }
    }

    // ------------------------------------------------------------- the sheet

    public SheetGraphics Render(Sheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var (w, h) = sheet.Dimensions2D;
        var g = new SheetGraphics(w, h);
        Frame(g, sheet);

        foreach (var view in sheet.Views)
        {
            ViewGeometry vg;
            try
            {
                vg = Geometry(sheet, view);
            }
            catch (Exception ex) when (ex is KernelException or InvalidOperationException)
            {
                g.Text(new Point2(view.X, view.Y), $"{view.Name}: {ex.Message}", 3, TextAnchor.Center, owner: view.Id);
                continue;
            }

            foreach (var line in vg.Lines)
            {
                var pen = line.Kind switch
                {
                    LineKind.Visible or LineKind.VisibleSilhouette => Pen.Visible,
                    LineKind.VisibleTangent => view.ShowTangent ? Pen.Tangent : (Pen?)null,
                    _ => view.ShowHidden ? Pen.Hidden : (Pen?)null,
                };
                if (pen is { } p)
                {
                    g.Polyline(p, [.. line.Points.Select(vg.ToSheet)], owner: view.Id);
                }
            }

            foreach (var (a, b) in vg.Hatch)
            {
                g.Line(Pen.Thin, vg.ToSheet(a), vg.ToSheet(b), view.Id);
            }

            if (view.Kind is not (ViewKind.Isometric or ViewKind.Detail))
            {
                CenterMarks(g, vg);
            }

            Labels(g, sheet, view, vg);
        }

        foreach (var layer in doc.Layers)
        {
            g.LayerColors[layer.Name] = layer.Color;
        }

        foreach (var d in sheet.Dimensions)
        {
            if (doc.IsLayerVisible(d.Layer) && sheet.Views.Find(v => v.Id == d.ViewId) is { } view)
            {
                try
                {
                    g.CurrentLayer = d.Layer;
                    Dimension(g, d, Geometry(sheet, view));
                }
                catch (Exception ex) when (ex is KernelException or InvalidOperationException)
                {
                }
                finally
                {
                    g.CurrentLayer = null;
                }
            }
        }

        if (doc.IsAssembly)
        {
            BomAndBalloons(g, sheet);
        }

        foreach (var t in sheet.Tolerances.Where(t => doc.IsLayerVisible(t.Layer)))
        {
            g.CurrentLayer = t.Layer;
            AnnotationGraphics.Draw(g, t);
        }

        foreach (var d in sheet.Datums.Where(d => doc.IsLayerVisible(d.Layer)))
        {
            g.CurrentLayer = d.Layer;
            AnnotationGraphics.Draw(g, d);
        }

        foreach (var f in sheet.Finishes.Where(f => doc.IsLayerVisible(f.Layer)))
        {
            g.CurrentLayer = f.Layer;
            AnnotationGraphics.Draw(g, f);
        }

        g.CurrentLayer = null;

        foreach (var table in sheet.HoleTables)
        {
            if (sheet.Views.Find(v => v.Id == table.ViewId) is { } view)
            {
                try
                {
                    DrawingTables.DrawHoleTable(g, table, DrawingTables.Holes(doc.Bodies, Geometry(sheet, view)));
                }
                catch (Exception ex) when (ex is KernelException or InvalidOperationException)
                {
                }
            }
        }

        if (sheet.Revisions is { } revisions)
        {
            DrawingTables.DrawRevisionTable(g, revisions);
        }

        foreach (var n in sheet.Notes.Where(n => doc.IsLayerVisible(n.Layer)))
        {
            g.CurrentLayer = n.Layer;
            var lines = n.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                g.Text(new Point2(n.X, n.Y - (i * n.Height * 1.5)), lines[i], n.Height);
            }
        }

        g.CurrentLayer = null;
        return g;
    }

    private void Labels(SheetGraphics g, Sheet sheet, DrawingView view, ViewGeometry vg)
    {
        var (min, _) = vg.SheetBounds;
        var scale = view.Scale ?? sheet.Scale;
        switch (view.Kind)
        {
            case ViewKind.Section:
                g.Text(new Point2(view.X, min.Y - 8), $"SECTION {view.Label}-{view.Label}", 4, TextAnchor.Center, owner: view.Id);
                g.Text(new Point2(view.X, min.Y - 13), $"SCALE {Scales.Format(scale)}", 3, TextAnchor.Center, owner: view.Id);
                if (sheet.Views.Find(v => v.Id == view.ParentId) is { } parent)
                {
                    var pg = Geometry(sheet, parent);
                    var a = pg.ToSheet(new Point2(view.Ax, view.Ay));
                    var b = pg.ToSheet(new Point2(view.Bx, view.By));
                    var d = (b - a).Normalized();
                    var ext = 6.0;
                    g.Line(Pen.Cut, a - (d * ext), b + (d * ext), parent.Id);

                    // Arrows point in the viewing direction (towards the kept material).
                    var look = vg.Toward * -1;
                    var arrow = new Point2(Vec3.Dot(look, pg.XAxis), Vec3.Dot(look, pg.YAxis)).Normalized();
                    foreach (var end in new[] { a - (d * ext), b + (d * ext) })
                    {
                        g.Line(Pen.Thin, end, end + (arrow * 8), parent.Id);
                        g.Arrow(end + (arrow * 8), arrow, 3, parent.Id);
                        g.Text(end + (arrow * 12) - new Point2(0, 2), view.Label, 5, TextAnchor.Center, owner: parent.Id);
                    }
                }

                break;
            case ViewKind.Detail:
                g.Text(new Point2(view.X, min.Y - 8), $"DETAIL {view.Label}", 4, TextAnchor.Center, owner: view.Id);
                g.Text(new Point2(view.X, min.Y - 13), $"SCALE {Scales.Format(scale)}", 3, TextAnchor.Center, owner: view.Id);
                if (sheet.Views.Find(v => v.Id == view.ParentId) is { } dp)
                {
                    var pg = Geometry(sheet, dp);
                    var c = pg.ToSheet(new Point2(view.Ax, view.Ay));
                    var r = view.Radius * pg.Scale;
                    g.Circle(Pen.Cut, c, r, dp.Id);
                    g.Text(c + new Point2(r * 0.75, r * 0.75 + 2), view.Label, 5, TextAnchor.Left, owner: dp.Id);
                }

                break;
        }
    }

    /// <summary>Cross-hair centre marks on circles seen face-on.</summary>
    private void CenterMarks(SheetGraphics g, ViewGeometry vg)
    {
        var done = new List<Point2>();
        foreach (var body in doc.Bodies)
        {
            for (var i = 1; i <= body.EdgeCount; i++)
            {
                var e = body.Edge(i);
                if (e.Curve != CurveKind.Circle || !e.IsClosed)
                {
                    continue;
                }

                var axis = body.Shape.FacesOfEdge(i).Select(f => body.Face(f)).FirstOrDefault(f => f.Surface is SurfaceKind.Cylinder or SurfaceKind.Cone)?.AxisDirection;
                if (axis is not { } ax || Math.Abs(Math.Abs(Vec3.Dot(ax.Normalized(), vg.Toward)) - 1) > 1e-6)
                {
                    continue;
                }

                var c = vg.ToSheet(e.CenterOrDirection);
                if (done.Any(p => Point2.Distance(p, c) < 0.01))
                {
                    continue;
                }

                done.Add(c);
                var r = (e.Radius * vg.Scale) + 2;
                g.Line(Pen.Center, c - new Point2(r, 0), c + new Point2(r, 0), vg.View.Id);
                g.Line(Pen.Center, c - new Point2(0, r), c + new Point2(0, r), vg.View.Id);
            }
        }
    }

    /// <summary>Resolves an anchor to a model point (and the circle radius for centre anchors on circular edges).</summary>
    public (Vec3 Point, double Radius) Resolve(DimensionAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (anchor.Edge is { } r)
        {
            foreach (var body in doc.Bodies)
            {
                if (!body.HasEdge(r.Name))
                {
                    continue;
                }

                var e = body.Edge(body.ResolveEdge(r, out _));
                return anchor.Which switch
                {
                    DimensionAnchor.Start => (e.Start, 0),
                    DimensionAnchor.End => (e.End, 0),
                    _ => e.Curve == CurveKind.Circle ? (e.CenterOrDirection, e.Radius) : (e.Midpoint, 0),
                };
            }
        }

        return (anchor.Point, 0);
    }

    /// <summary>The measured value of a dimension, in model millimetres.</summary>
    public double Measure(Sheet sheet, DrawingDimension d)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(d);
        var view = sheet.Views.Find(v => v.Id == d.ViewId) ?? throw new InvalidOperationException("Dimension view missing.");
        var vg = Geometry(sheet, view);
        var (pa, ra) = Resolve(d.A);
        if (d.Kind is DimensionKind.Diameter or DimensionKind.Radius)
        {
            return d.Kind == DimensionKind.Diameter ? 2 * ra : ra;
        }

        var a = vg.ToView(pa);
        var b = vg.ToView(Resolve(d.B ?? d.A).Point);
        return d.Kind switch
        {
            DimensionKind.Horizontal => Math.Abs(b.X - a.X),
            DimensionKind.Vertical => Math.Abs(b.Y - a.Y),
            _ => Point2.Distance(a, b),
        };
    }

    private void Dimension(SheetGraphics g, DrawingDimension d, ViewGeometry vg)
    {
        var owner = d.Id;
        var (pa, radius) = Resolve(d.A);
        var value = d.Kind switch
        {
            DimensionKind.Diameter => 2 * radius,
            DimensionKind.Radius => radius,
            _ => 0,
        };
        string Label(double v) => d.Text ?? (d.Kind switch
        {
            DimensionKind.Diameter => "⌀",
            DimensionKind.Radius => "R",
            _ => string.Empty,
        } + Units.Format(v, doc.LengthUnit) + (d.Tolerance ?? string.Empty));
        const double textHeight = 3.5;

        if (d.Kind is DimensionKind.Diameter or DimensionKind.Radius)
        {
            var c = vg.ToSheet(pa);
            var r = radius * vg.Scale;
            var dir = new Point2(1, 1).Normalized();
            var edge = c + (dir * r);
            var knee = c + (dir * (r + Math.Abs(d.Offset)));
            if (d.Kind == DimensionKind.Diameter)
            {
                g.Line(Pen.Thin, c - (dir * r), knee, owner);
                g.Arrow(c - (dir * r), dir * -1, 3, owner);
            }
            else
            {
                g.Line(Pen.Thin, c, knee, owner);
            }

            g.Arrow(edge, dir, 3, owner);
            g.Line(Pen.Thin, knee, knee + new Point2(12, 0), owner);
            g.Text(knee + new Point2(1, 1), Label(value), textHeight, owner: owner);
            return;
        }

        var a = vg.ToSheet(pa);
        var b = vg.ToSheet(Resolve(d.B ?? d.A).Point);
        Point2 along, normal;
        switch (d.Kind)
        {
            case DimensionKind.Horizontal:
                along = new Point2(1, 0);
                normal = new Point2(0, 1);
                value = Math.Abs(b.X - a.X) / vg.Scale;
                break;
            case DimensionKind.Vertical:
                along = new Point2(0, 1);
                normal = new Point2(-1, 0);
                value = Math.Abs(b.Y - a.Y) / vg.Scale;
                break;
            default:
                along = (b - a).Normalized();
                normal = along.Perp();
                value = Point2.Distance(a, b) / vg.Scale;
                break;
        }

        // Dimension line beyond the farther point, at Offset along the normal.
        var level = Math.Max(Point2.Dot(a, normal), Point2.Dot(b, normal)) + d.Offset;
        if (d.Offset < 0)
        {
            level = Math.Min(Point2.Dot(a, normal), Point2.Dot(b, normal)) + d.Offset;
        }

        Point2 OnLine(Point2 p) => p + (normal * (level - Point2.Dot(p, normal)));
        var da = OnLine(a);
        var db = OnLine(b);
        var side = Math.Sign(d.Offset == 0 ? 1 : d.Offset);
        foreach (var (p, q) in new[] { (a, da), (b, db) })
        {
            if (Point2.Distance(p, q) > 1)
            {
                g.Line(Pen.Thin, p + (normal * side), q + (normal * (2 * side)), owner);
            }
        }

        g.Line(Pen.Thin, da, db, owner);
        var u = (db - da).Normalized();
        g.Arrow(da, u * -1, 3, owner);
        g.Arrow(db, u, 3, owner);
        var mid = (da + db) * 0.5;
        var angle = Math.Atan2(along.Y, along.X) * 180 / Math.PI;
        if (angle > 90.001 || angle < -89.999)
        {
            angle += 180;
        }

        var textAt = mid + (normal * (side > 0 ? 1.2 : -1.2 - textHeight));
        var anchor = TextAnchor.Center;
        if (doc.Standard == DraftingStandard.Ansi)
        {
            // ANSI: text always reads horizontally; beside vertical dimension lines.
            angle = 0;
            if (Math.Abs(along.X) < 0.5)
            {
                var outward = normal * side;
                anchor = outward.X >= 0 ? TextAnchor.Left : TextAnchor.Right;
                textAt = mid + (outward.Normalized() * 1.5) - new Point2(0, textHeight / 2);
            }
        }

        g.Text(textAt, Label(value), textHeight, anchor, angle, owner);
    }

    /// <summary>Item numbers by component id: one number per distinct part and configuration, in BOM order.</summary>
    public IReadOnlyDictionary<string, int> ItemNumbers()
    {
        var asm = doc.Assembly;
        var bom = AssemblyTools.BillOfMaterials(asm);
        var groups = asm.Components.Where(c => !c.Suppressed).GroupBy(c => (c.Path, c.Configuration)).ToList();
        var numbers = new Dictionary<string, int>();
        for (var i = 0; i < groups.Count && i < bom.Count; i++)
        {
            foreach (var c in groups[i])
            {
                numbers[c.Id] = bom[i].Item;
            }
        }

        return numbers;
    }

    /// <summary>The point a balloon's leader attaches to: the component's centre in the view.</summary>
    public Point2 BalloonAnchor(Sheet sheet, Balloon b)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(b);
        var view = sheet.Views.Find(v => v.Id == b.ViewId) ?? throw new InvalidOperationException("Balloon view missing.");
        var vg = Geometry(sheet, view);
        var box = doc.Bodies.Where(x => x.Name == b.ComponentId).Aggregate(Box3.Empty, (acc, x) => acc.Union(x.Shape.Bounds));
        return vg.ToSheet(box.IsEmpty ? Vec3.Zero : box.Center);
    }

    private void BomAndBalloons(SheetGraphics g, Sheet sheet)
    {
        var asm = doc.Assembly;
        var numbers = ItemNumbers();
        foreach (var b in sheet.Balloons)
        {
            if (!numbers.TryGetValue(b.ComponentId, out var item) || sheet.Views.Find(v => v.Id == b.ViewId) is null)
            {
                continue;
            }

            const double r = 4.5;
            var center = new Point2(b.X, b.Y);
            var anchor = BalloonAnchor(sheet, b);
            var dir = (anchor - center).Normalized();
            g.Line(Pen.Thin, center + (dir * r), anchor, b.ComponentId);
            g.Circle(Pen.Thin, anchor, 0.5, b.ComponentId, 12);
            g.Circle(Pen.Thin, center, r, b.ComponentId);
            g.Text(center - new Point2(0, 1.6), item.ToString(CultureInfo.InvariantCulture), 3.5, TextAnchor.Center, owner: b.ComponentId);
        }

        if (sheet.Bom is not { } table)
        {
            return;
        }

        var lines = AssemblyTools.BillOfMaterials(asm);
        double[] widths = [12, 50, 60, 34, 12];
        string[] header = ["ITEM", "PART NUMBER", "DESCRIPTION", "MATERIAL", "QTY"];
        const double rowH = 7;
        var x0 = table.X;
        var y = table.Y;
        var total = widths.Sum();
        void Row(IReadOnlyList<string> cells, bool bold)
        {
            g.Polyline(Pen.Thin, [new(x0, y), new(x0 + total, y), new(x0 + total, y - rowH), new(x0, y - rowH)], closed: true, owner: "bom");
            var x = x0;
            for (var c = 0; c < cells.Count; c++)
            {
                if (c > 0)
                {
                    g.Line(Pen.Thin, new Point2(x, y), new Point2(x, y - rowH), "bom");
                }

                var anchor = c is 1 or 2 ? TextAnchor.Left : TextAnchor.Center;
                var at = anchor == TextAnchor.Left ? new Point2(x + 2, y - rowH + 2) : new Point2(x + (widths[c] / 2), y - rowH + 2);
                g.Text(at, cells[c], bold ? 3 : 2.8, anchor, owner: "bom");
                x += widths[c];
            }

            y -= rowH;
        }

        Row(header, bold: true);
        foreach (var l in lines)
        {
            Row([l.Item.ToString(CultureInfo.InvariantCulture), l.PartName, l.Description, l.Material, l.Quantity.ToString(CultureInfo.InvariantCulture)], bold: false);
        }
    }

    /// <summary>Border, zone ticks and title block.</summary>
    private void Frame(SheetGraphics g, Sheet sheet)
    {
        var (w, h) = sheet.Dimensions2D;
        const double m = 10;
        g.Polyline(Pen.Frame, [new(m, m), new(w - m, m), new(w - m, h - m), new(m, h - m)], closed: true);

        // Title block: 180 × 32 mm in the lower-right corner.
        var tw = Math.Min(180, w - (2 * m));
        const double th = 32;
        var x0 = w - m - tw;
        var y0 = m;
        g.Polyline(Pen.Frame, [new(x0, y0), new(x0 + tw, y0), new(x0 + tw, y0 + th), new(x0, y0 + th)], closed: true);
        g.Line(Pen.Thin, new Point2(x0, y0 + 16), new Point2(x0 + tw, y0 + 16));
        g.Line(Pen.Thin, new Point2(x0 + (tw * 0.6), y0), new Point2(x0 + (tw * 0.6), y0 + 16));
        g.Line(Pen.Thin, new Point2(x0 + (tw * 0.8), y0), new Point2(x0 + (tw * 0.8), y0 + 16));

        // Drawing properties first, then (part drawings) the model's custom properties.
        string? FromModel(string key) => doc.IsAssembly ? null : key switch
        {
            "Title" => PropertyEvaluator.Get(doc.Model, "Title") ?? PropertyEvaluator.Get(doc.Model, PropertyEvaluator.Description),
            "Number" => PropertyEvaluator.Get(doc.Model, PropertyEvaluator.PartNumber),
            _ => PropertyEvaluator.Get(doc.Model, key),
        };
        string P(string key, string fallback) => doc.Properties.TryGetValue(key, out var v) && v.Length > 0 ? v
            : FromModel(key) is { Length: > 0 } m ? m : fallback;
        var title = P("Title", Path.GetFileNameWithoutExtension(doc.ModelPath));
        g.Text(new Point2(x0 + 3, y0 + 23), title, 6);
        g.Text(new Point2(x0 + tw - 3, y0 + 26), P("Company", string.Empty), 3, TextAnchor.Right);
        g.Text(new Point2(x0 + tw - 3, y0 + 20), P("Number", string.Empty), 3.5, TextAnchor.Right);
        g.Text(new Point2(x0 + 3, y0 + 10), $"MATERIAL: {P("Material", doc.MaterialName)}", 2.5);
        g.Text(new Point2(x0 + 3, y0 + 4), $"DRAWN: {P("Author", string.Empty)}  {P("Date", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}", 2.5);
        g.Text(new Point2(x0 + (tw * 0.6) + 2, y0 + 10), "SCALE", 2);
        g.Text(new Point2(x0 + (tw * 0.6) + 2, y0 + 4), Scales.Format(sheet.Scale), 3);
        g.Text(new Point2(x0 + (tw * 0.8) + 2, y0 + 10), "SHEET / REV", 2);
        var index = doc.Sheets.IndexOf(sheet) + 1;
        g.Text(new Point2(x0 + (tw * 0.8) + 2, y0 + 4), $"{index} of {doc.Sheets.Count} / {P("Revision", "A")}", 3);
        g.Text(new Point2(m + 2, m + 2), doc.FirstAngle ? "FIRST ANGLE PROJECTION" : "THIRD ANGLE PROJECTION", 2.5);
    }
}
