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

    /// <summary>Moves view points as a break closes the view up (null when there's no break).</summary>
    public Func<Point2, Point2>? Map { get; init; }

    /// <summary>Whether a view point is shown (not cropped away or in a break); null shows all.</summary>
    public Func<Point2, bool>? Shows { get; init; }

    public Point2 ToView(Vec3 p)
    {
        var uv = new Point2(Vec3.Dot(p, XAxis), Vec3.Dot(p, YAxis));
        return Map is null ? uv : Map(uv);
    }

    /// <summary>Whether a model point falls in the shown part of the view.</summary>
    public bool IsShown(Vec3 p) => Shows?.Invoke(new Point2(Vec3.Dot(p, XAxis), Vec3.Dot(p, YAxis))) != false;

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
        var key = $"{view.Id}|{view.Kind}|{view.ParentId}|{view.Ax},{view.Ay},{view.Bx},{view.By},{view.Radius}|{scale}|{view.Side}|" +
            $"{string.Join(',', view.Crop ?? [])}|{string.Join(',', view.Break ?? [])}|{view.BreakAlongY}";
        if (_cache.TryGetValue(key, out var cached))
        {
            // The view's position is read live, so moving a view doesn't need a new projection.
            return cached;
        }

        var geometry = view.Kind switch
        {
            ViewKind.Section => Section(sheet, view, scale),
            ViewKind.Detail => Detail(sheet, view, scale),
            ViewKind.Projected => Projected(sheet, view, scale),
            ViewKind.Auxiliary => Auxiliary(sheet, view, scale),
            _ => Standard(view, scale),
        };
        geometry = CropAndBreak(geometry);
        _cache[key] = geometry;
        return geometry;
    }

    /// <summary>The orientation of a view projected to one side of a parent with the given axes (third-angle).</summary>
    public static (Vec3 Toward, Vec3 X) ProjectedAxes(Vec3 toward, Vec3 x, ViewSide side)
    {
        var y = Vec3.Cross(toward, x);
        return side switch
        {
            ViewSide.Right => (x, toward * -1),
            ViewSide.Left => (x * -1, toward),
            ViewSide.Above => (y, x),
            _ => (y * -1, x),
        };
    }

    private ViewGeometry Projected(Sheet sheet, DrawingView view, double scale)
    {
        var parent = sheet.Views.Find(v => v.Id == view.ParentId) ?? throw new InvalidOperationException("The projected view's parent is missing.");
        var pg = Geometry(sheet, parent);
        var (toward, x) = ProjectedAxes(pg.Toward, pg.XAxis, view.Side);
        return Oriented(view, toward, x, scale);
    }

    private ViewGeometry Auxiliary(Sheet sheet, DrawingView view, double scale)
    {
        var parent = sheet.Views.Find(v => v.Id == view.ParentId) ?? throw new InvalidOperationException("The auxiliary view's parent is missing.");
        var pg = Geometry(sheet, parent);
        var edge = (pg.XAxis * (view.Bx - view.Ax)) + (pg.YAxis * (view.By - view.Ay));
        if (edge.LengthSquared < 1e-18)
        {
            throw new InvalidOperationException("The auxiliary view's reference edge has no length.");
        }

        edge = edge.Normalized();
        return Oriented(view, Vec3.Cross(edge, pg.Toward).Normalized(), edge, scale);
    }

    private ViewGeometry Oriented(DrawingView view, Vec3 toward, Vec3 x, double scale)
    {
        var shape = Compound();
        var lines = shape is null ? [] : shape.Project(Vec3.Zero, toward, x, 0.005);
        return new ViewGeometry { View = view, Toward = toward, XAxis = x, Lines = lines, Scale = scale, Center = Middle(lines) };
    }

    /// <summary>Applies a view's crop rectangle and break, if it has them.</summary>
    private static ViewGeometry CropAndBreak(ViewGeometry g)
    {
        var view = g.View;
        var lines = g.Lines;
        var hatch = g.Hatch;
        Func<Point2, bool>? shows = null;
        Func<Point2, Point2>? map = null;
        if (view.Crop is [var x0, var y0, var x1, var y1])
        {
            var (min, max) = (new Point2(Math.Min(x0, x1), Math.Min(y0, y1)), new Point2(Math.Max(x0, x1), Math.Max(y0, y1)));
            lines = [.. lines.SelectMany(l => ClipToBox(l.Points, min, max).Select(r => new ProjectedLine(l.Kind, r)))];
            hatch = [.. hatch.SelectMany(h => ClipToBox([h.A, h.B], min, max)).Where(r => r.Count == 2).Select(r => (r[0], r[1]))];
            shows = p => p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;
        }

        if (view.Break is [var b0, var b1] && Math.Abs(b1 - b0) > 1e-9)
        {
            var (from, to) = (Math.Min(b0, b1), Math.Max(b0, b1));
            var alongY = view.BreakAlongY;
            var gap = 4 / g.Scale; // the break shows as a 4 mm gap on the sheet
            var shift = to - from - gap;
            double Axis(Point2 p) => alongY ? p.Y : p.X;
            Point2 Shift(Point2 p) => Axis(p) > to - 1e-9 ? (alongY ? new Point2(p.X, p.Y - shift) : new Point2(p.X - shift, p.Y)) : p;
            var big = 1e7;
            var (lowMin, lowMax) = alongY ? (new Point2(-big, -big), new Point2(big, from)) : (new Point2(-big, -big), new Point2(from, big));
            var (highMin, highMax) = alongY ? (new Point2(-big, to), new Point2(big, big)) : (new Point2(to, -big), new Point2(big, big));
            IEnumerable<IReadOnlyList<Point2>> Keep(IReadOnlyList<Point2> pts) =>
                ClipToBox(pts, lowMin, lowMax).Concat(ClipToBox(pts, highMin, highMax).Select(r => (IReadOnlyList<Point2>)[.. r.Select(Shift)]));
            var extent = lines.SelectMany(l => l.Points).ToList();
            lines = [.. lines.SelectMany(l => Keep(l.Points).Select(r => new ProjectedLine(l.Kind, r)))];
            hatch = [.. hatch.SelectMany(h => Keep([h.A, h.B])).Where(r => r.Count == 2).Select(r => (r[0], r[1]))];

            // Zigzag break lines at both edges of the gap, across the part.
            if (extent.Count > 0)
            {
                var lo = alongY ? extent.Min(p => p.X) : extent.Min(p => p.Y);
                var hi = alongY ? extent.Max(p => p.X) : extent.Max(p => p.Y);
                var zig = new List<ProjectedLine>(lines);
                foreach (var at in new[] { from, from + gap })
                {
                    var pts = new List<Point2>();
                    const int Steps = 8;
                    for (var k = 0; k <= Steps; k++)
                    {
                        var across = lo - (2 / g.Scale) + ((hi - lo + (4 / g.Scale)) * k / Steps);
                        var wobble = (k % 2 == 0 ? -1 : 1) * 1 / g.Scale;
                        pts.Add(alongY ? new Point2(across, at + wobble) : new Point2(at + wobble, across));
                    }

                    zig.Add(new ProjectedLine(LineKind.VisibleTangent, pts));
                }

                lines = zig;
            }

            var previous = shows;
            shows = p => (Axis(p) <= from || Axis(p) >= to) && previous?.Invoke(p) != false;
            map = Shift;
        }

        return ReferenceEquals(lines, g.Lines)
            ? g
            : new ViewGeometry { View = view, Toward = g.Toward, XAxis = g.XAxis, Lines = lines, Hatch = hatch, Scale = g.Scale, Center = Middle(lines), Map = map, Shows = shows };
    }

    /// <summary>Parts of a polyline inside an axis-aligned box (Liang–Barsky per segment).</summary>
    public static IEnumerable<IReadOnlyList<Point2>> ClipToBox(IReadOnlyList<Point2> pts, Point2 min, Point2 max)
    {
        var run = new List<Point2>();
        for (var i = 0; i + 1 < pts.Count; i++)
        {
            var (p, q) = (pts[i], pts[i + 1]);
            var d = q - p;
            double t0 = 0, t1 = 1;
            var inside = true;
            foreach (var (pp, qq) in new[] { (-d.X, p.X - min.X), (d.X, max.X - p.X), (-d.Y, p.Y - min.Y), (d.Y, max.Y - p.Y) })
            {
                if (Math.Abs(pp) < 1e-15)
                {
                    if (qq < 0)
                    {
                        inside = false;
                        break;
                    }

                    continue;
                }

                var t = qq / pp;
                if (pp < 0)
                {
                    t0 = Math.Max(t0, t);
                }
                else
                {
                    t1 = Math.Min(t1, t);
                }
            }

            if (!inside || t0 > t1)
            {
                if (run.Count > 1)
                {
                    yield return run;
                }

                run = [];
                continue;
            }

            var a = p + (d * t0);
            var b = p + (d * t1);
            if (run.Count == 0 || Point2.Distance(run[^1], a) > 1e-9)
            {
                if (run.Count > 1)
                {
                    yield return run;
                }

                run = [a];
            }

            run.Add(b);
            if (t1 < 1)
            {
                yield return run;
                run = [];
            }
        }

        if (run.Count > 1)
        {
            yield return run;
        }
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
                Centerlines(g, vg);
                CosmeticThreads(g, vg);
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

        foreach (var weld in sheet.Welds.Where(x => doc.IsLayerVisible(x.Layer)))
        {
            g.CurrentLayer = weld.Layer;
            AnnotationGraphics.Draw(g, weld);
        }

        foreach (var t in sheet.DatumTargets.Where(t => doc.IsLayerVisible(t.Layer)))
        {
            g.CurrentLayer = t.Layer;
            AnnotationGraphics.Draw(g, t);
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
            case ViewKind.Auxiliary:
                g.Text(new Point2(view.X, min.Y - 8), $"VIEW {view.Label}", 4, TextAnchor.Center, owner: view.Id);
                if (sheet.Views.Find(v => v.Id == view.ParentId) is { } ap)
                {
                    // An arrow in the parent showing the viewing direction, square to the edge.
                    var pg = Geometry(sheet, ap);
                    var mid = pg.ToSheet(new Point2((view.Ax + view.Bx) / 2, (view.Ay + view.By) / 2));
                    var look = vg.Toward * -1;
                    var arrow = new Point2(Vec3.Dot(look, pg.XAxis), Vec3.Dot(look, pg.YAxis)).Normalized();
                    var tail = mid - (arrow * 18);
                    g.Line(Pen.Thin, tail, mid - (arrow * 4), ap.Id);
                    g.Arrow(mid - (arrow * 4), arrow, 3, ap.Id);
                    g.Text(tail - (arrow * 4) - new Point2(0, 2), view.Label, 5, TextAnchor.Center, owner: ap.Id);
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

                if (!vg.IsShown(e.CenterOrDirection))
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

    /// <summary>Chain-dotted centrelines along cylinders seen side-on, a little past each end.</summary>
    private void Centerlines(SheetGraphics g, ViewGeometry vg)
    {
        var done = new List<(Point2, Point2)>();
        foreach (var body in doc.Bodies)
        {
            for (var f = 1; f <= body.FaceCount; f++)
            {
                var face = body.Face(f);
                if (face.Surface != SurfaceKind.Cylinder || Math.Abs(Vec3.Dot(face.AxisDirection.Normalized(), vg.Toward)) > 1e-6)
                {
                    continue;
                }

                var axis = face.AxisDirection.Normalized();
                var along = body.Shape.EdgesOfFace(f).SelectMany(e => body.Shape.SampleEdge(e)).Select(p => Vec3.Dot(p - face.AxisOrigin, axis)).ToList();
                if (along.Count == 0)
                {
                    continue;
                }

                var p0 = face.AxisOrigin + (axis * along.Min());
                var p1 = face.AxisOrigin + (axis * along.Max());
                if (!vg.IsShown(p0) && !vg.IsShown(p1))
                {
                    continue;
                }

                var a = vg.ToSheet(p0);
                var b = vg.ToSheet(p1);
                var dir = (b - a).Normalized();
                (a, b) = (a - (dir * 2), b + (dir * 2));
                if (done.Any(x => Point2.Distance(x.Item1, a) < 0.01 && Point2.Distance(x.Item2, b) < 0.01))
                {
                    continue;
                }

                done.Add((a, b));
                g.Line(Pen.Center, a, b, vg.View.Id);
            }
        }
    }

    /// <summary>
    /// Thin lines at the thread's major diameter for tapped Hole Wizard holes: a three-quarter circle
    /// end-on (ISO convention) and two lines along the hole side-on.
    /// </summary>
    private void CosmeticThreads(SheetGraphics g, ViewGeometry vg)
    {
        if (doc.IsAssembly)
        {
            return;
        }

        var tapped = doc.Model.Features.OfType<Document.Features.HoleFeature>().Where(h => h.ThreadDiameter > 0).ToList();
        foreach (var hole in tapped)
        {
            foreach (var body in doc.Bodies)
            {
                for (var f = 1; f <= body.FaceCount; f++)
                {
                    var face = body.Face(f);
                    if (face.Surface != SurfaceKind.Cylinder || !body.FaceNames[f - 1].Contains(hole.Id, StringComparison.Ordinal) ||
                        Math.Abs(face.Radius - (hole.Diameter / 2)) > 1e-6)
                    {
                        continue;
                    }

                    var axis = face.AxisDirection.Normalized();
                    var major = hole.ThreadDiameter / 2;
                    var along = body.Shape.EdgesOfFace(f).SelectMany(e => body.Shape.SampleEdge(e)).Select(p => Vec3.Dot(p - face.AxisOrigin, axis)).ToList();
                    var p0 = face.AxisOrigin + (axis * along.Min());
                    var p1 = face.AxisOrigin + (axis * along.Max());
                    if (Math.Abs(Math.Abs(Vec3.Dot(axis, vg.Toward)) - 1) < 1e-6)
                    {
                        if (!vg.IsShown(p0))
                        {
                            continue;
                        }

                        var c = vg.ToSheet(p0);
                        var r = major * vg.Scale;
                        var arc = Enumerable.Range(0, 55).Select(k => c + new Point2(Math.Cos(k * 1.5 * Math.PI / 54) * r, Math.Sin(k * 1.5 * Math.PI / 54) * r)).ToList();
                        g.Polyline(Pen.Thin, arc, owner: vg.View.Id);
                    }
                    else if (Math.Abs(Vec3.Dot(axis, vg.Toward)) < 1e-6)
                    {
                        var side = Vec3.Cross(axis, vg.Toward).Normalized() * major;
                        foreach (var s in new[] { side, side * -1 })
                        {
                            if (vg.IsShown(p0 + s) || vg.IsShown(p1 + s))
                            {
                                g.Line(Pen.Thin, vg.ToSheet(p0 + s), vg.ToSheet(p1 + s), vg.View.Id);
                            }
                        }
                    }
                }
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
            DimensionKind.Horizontal or DimensionKind.OrdinateHorizontal => Math.Abs(b.X - a.X),
            DimensionKind.Vertical or DimensionKind.OrdinateVertical => Math.Abs(b.Y - a.Y),
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
        } + Units.Format(v, doc.LengthUnit) + FitText(d, v) + (d.Tolerance ?? string.Empty));
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
        if (d.Kind is DimensionKind.OrdinateHorizontal or DimensionKind.OrdinateVertical)
        {
            // A leader from the point out past the datum's side, with the distance from zero at its end.
            var across = d.Kind == DimensionKind.OrdinateHorizontal;
            var gap = 1.5;
            var end = across ? new Point2(b.X, a.Y + d.Offset) : new Point2(a.X + d.Offset, b.Y);
            var dir = (end - b).Normalized();
            if (Point2.Distance(b, end) > gap)
            {
                g.Line(Pen.Thin, b + (dir * gap), end, owner);
            }

            var ordinate = (across ? Math.Abs(b.X - a.X) : Math.Abs(b.Y - a.Y)) / vg.Scale;
            var text = d.Text ?? (Units.Format(ordinate, doc.LengthUnit) + FitText(d, ordinate) + (d.Tolerance ?? string.Empty));
            if (across)
            {
                g.Text(end + new Point2(0, d.Offset < 0 ? -1 - textHeight : 1), text, textHeight, TextAnchor.Center, owner: owner);
            }
            else
            {
                g.Text(end + new Point2(d.Offset < 0 ? -1 : 1, -textHeight / 2), text, textHeight, d.Offset < 0 ? TextAnchor.Right : TextAnchor.Left, owner: owner);
            }

            return;
        }

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

    /// <summary>" H7" or " H7 (+0.015/0)" for a dimension with a fit.</summary>
    private static string FitText(DrawingDimension d, double size) =>
        d.Fit is { Length: > 0 } fit
            ? " " + fit + (d.ShowLimits && Fits.LimitsText(size, fit) is { } limits ? $" ({limits})" : string.Empty)
            : string.Empty;

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
