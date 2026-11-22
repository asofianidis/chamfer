using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Drawing;

/// <summary>Which way a set of dimensions measures.</summary>
public enum DimensionSetKind
{
    /// <summary>Every point measured from one datum, the dimension lines stacked.</summary>
    Baseline,

    /// <summary>Each point measured from the one before, the dimension lines in a row.</summary>
    Chain,

    /// <summary>Each point labelled with its distance from a zero datum.</summary>
    Ordinate,
}

/// <summary>
/// Dimensions a view's features in sets: hole centres (and the far edge) from the lower-left
/// corner, as baseline, chain or ordinate dimensions along X or Y.
/// </summary>
public static class DimensionSets
{
    /// <summary>The view's lower-left vertex: the datum the sets measure from.</summary>
    public static (DimensionAnchor Anchor, Point2 Uv)? Datum(PartDocument model, ViewGeometry vg)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(vg);
        return Vertices(model, vg).Select(v => ((DimensionAnchor, Point2)?)v).MinBy(v => v!.Value.Item2.X + v.Value.Item2.Y);
    }

    /// <summary>
    /// The points a set dimensions: centres of holes seen end-on, and the vertex farthest along
    /// the measuring direction (so the set reaches the far edge), each once per position.
    /// </summary>
    public static IReadOnlyList<(DimensionAnchor Anchor, Point2 Uv)> Targets(PartDocument model, ViewGeometry vg, bool vertical)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(vg);
        double Along(Point2 p) => vertical ? p.Y : p.X;
        var found = new List<(DimensionAnchor, Point2)>();
        foreach (var body in model.Bodies)
        {
            for (var i = 1; i <= body.EdgeCount; i++)
            {
                var e = body.Edge(i);
                var axis = body.Shape.FacesOfEdge(i).Select(body.Face).FirstOrDefault(f => f.Surface == SurfaceKind.Cylinder)?.AxisDirection;
                if (e.Curve == CurveKind.Circle && e.IsClosed && axis is { } ax && Math.Abs(Math.Abs(Vec3.Dot(ax.Normalized(), vg.Toward)) - 1) < 1e-6)
                {
                    found.Add((new DimensionAnchor(body.EdgeRef(i), DimensionAnchor.Center, e.CenterOrDirection), vg.ToView(e.CenterOrDirection)));
                }
            }
        }

        if (Vertices(model, vg).Select(v => ((DimensionAnchor, Point2)?)v).MaxBy(v => Along(v!.Value.Item2)) is { } far)
        {
            found.Add(far);
        }

        // One per position along the measuring direction, nearest the datum first.
        return [.. found.GroupBy(f => Math.Round(Along(f.Item2), 4)).Select(g => g.First()).OrderBy(f => Along(f.Item2))];
    }

    /// <summary>Adds a set of dimensions to a view; returns them.</summary>
    public static IReadOnlyList<DrawingDimension> Add(DrawingDocument doc, Sheet sheet, DrawingView view, ViewGeometry vg, DimensionSetKind kind, bool vertical)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(view);
        if (Datum(doc.Model, vg) is not { } datum)
        {
            return [];
        }

        double Along(Point2 p) => vertical ? p.Y : p.X;
        var targets = Targets(doc.Model, vg, vertical).Where(t => Along(t.Uv) - Along(datum.Uv) > 1e-6).ToList();
        var linear = vertical ? DimensionKind.Vertical : DimensionKind.Horizontal;
        var side = vertical ? -1 : -1; // left of the view, or below it
        var made = new List<DrawingDimension>();
        switch (kind)
        {
            case DimensionSetKind.Baseline:
                for (var i = 0; i < targets.Count; i++)
                {
                    made.Add(doc.AddDimension(sheet, view, linear, datum.Anchor, targets[i].Anchor, side * (10 + (7 * i))));
                }

                break;
            case DimensionSetKind.Chain:
                var from = datum.Anchor;
                foreach (var t in targets)
                {
                    made.Add(doc.AddDimension(sheet, view, linear, from, t.Anchor, side * 10));
                    from = t.Anchor;
                }

                break;
            default:
                var ordinate = vertical ? DimensionKind.OrdinateVertical : DimensionKind.OrdinateHorizontal;
                made.Add(doc.AddDimension(sheet, view, ordinate, datum.Anchor, datum.Anchor, side * 10));
                made.AddRange(targets.Select(t => doc.AddDimension(sheet, view, ordinate, datum.Anchor, t.Anchor, side * 10)));
                break;
        }

        return made;
    }

    private static IEnumerable<(DimensionAnchor Anchor, Point2 Uv)> Vertices(PartDocument model, ViewGeometry vg)
    {
        foreach (var body in model.Bodies)
        {
            for (var i = 1; i <= body.EdgeCount; i++)
            {
                var e = body.Edge(i);
                if (e.Curve != CurveKind.Line)
                {
                    continue;
                }

                yield return (new DimensionAnchor(body.EdgeRef(i), DimensionAnchor.Start, e.Start), vg.ToView(e.Start));
                yield return (new DimensionAnchor(body.EdgeRef(i), DimensionAnchor.End, e.End), vg.ToView(e.End));
            }
        }
    }
}
