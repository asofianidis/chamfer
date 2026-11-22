namespace Chamfer.Sketching.Entities;

/// <summary>
/// A smooth curve through points (Catmull–Rom tangents), made of cubic Bézier pieces. Its points
/// are ordinary sketch points, so they can be dragged, related and dimensioned.
/// </summary>
public sealed class SketchSpline : SketchEntity
{
    private readonly List<SketchPoint> _points;

    public SketchSpline(IEnumerable<Vec2> through)
    {
        ArgumentNullException.ThrowIfNull(through);
        _points = [.. through.Select(p => new SketchPoint(p) { Owner = this })];
        if (_points.Count < 2)
        {
            throw new ArgumentException("A spline needs at least two points.", nameof(through));
        }
    }

    public override EntityKind Kind => EntityKind.Spline;

    public override IReadOnlyList<SketchPoint> Points => _points;

    public SketchPoint Start => _points[0];

    public SketchPoint End => _points[^1];

    public override IEnumerable<Param> Params => _points.SelectMany(p => p.Params);

    /// <summary>The cubic Bézier pieces through the points, as control points (start, c1, c2, end).</summary>
    public IReadOnlyList<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> Pieces() => BezierPieces([.. _points.Select(p => p.Position)]);

    public static IReadOnlyList<(Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3)> BezierPieces(IReadOnlyList<Vec2> pts)
    {
        ArgumentNullException.ThrowIfNull(pts);
        var n = pts.Count;
        var tangents = new Vec2[n];
        for (var i = 0; i < n; i++)
        {
            tangents[i] = i == 0 ? pts[1] - pts[0]
                : i == n - 1 ? pts[n - 1] - pts[n - 2]
                : (pts[i + 1] - pts[i - 1]) * 0.5;
        }

        var pieces = new List<(Vec2, Vec2, Vec2, Vec2)>(n - 1);
        for (var i = 0; i + 1 < n; i++)
        {
            pieces.Add((pts[i], pts[i] + (tangents[i] / 3), pts[i + 1] - (tangents[i + 1] / 3), pts[i + 1]));
        }

        return pieces;
    }

    public static Vec2 Bezier((Vec2 P0, Vec2 P1, Vec2 P2, Vec2 P3) b, double t)
    {
        var u = 1 - t;
        return (b.P0 * (u * u * u)) + (b.P1 * (3 * u * u * t)) + (b.P2 * (3 * u * t * t)) + (b.P3 * (t * t * t));
    }

    /// <summary>Points along the spline, start to end inclusive.</summary>
    public IReadOnlyList<Vec2> Sample(int perPiece = 16)
    {
        var list = new List<Vec2>();
        foreach (var piece in Pieces())
        {
            for (var k = 0; k < perPiece; k++)
            {
                list.Add(Bezier(piece, (double)k / perPiece));
            }
        }

        list.Add(End.Position);
        return list;
    }

    public override Vec2 ClosestPoint(Vec2 p)
    {
        var pts = Sample(24);
        var best = pts[0];
        var bestD = double.MaxValue;
        for (var i = 0; i + 1 < pts.Count; i++)
        {
            var (a, b) = (pts[i], pts[i + 1]);
            var ab = b - a;
            var len2 = Vec2.Dot(ab, ab);
            var t = len2 < 1e-18 ? 0 : Math.Clamp(Vec2.Dot(p - a, ab) / len2, 0, 1);
            var q = a + (ab * t);
            var d = Vec2.Distance(p, q);
            if (d < bestD)
            {
                (best, bestD) = (q, d);
            }
        }

        return best;
    }

    public override double DistanceTo(Vec2 p) => Vec2.Distance(ClosestPoint(p), p);
}
