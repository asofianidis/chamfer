using Chamfer.Sketching;
using Chamfer.Sketching.Entities;

namespace Chamfer.Document.Profiles;

/// <summary>One curve of a profile loop, traversed in loop order.</summary>
/// <param name="EntityId">The sketch entity this segment came from.</param>
/// <param name="Reversed">True when the loop runs against the entity's own direction.</param>
public sealed record LoopSegment(int EntityId, EntityKind Kind, Vec2 Start, Vec2 End, Vec2 Center, double Radius, bool Ccw, bool Reversed)
{
    /// <summary>Points along the segment, excluding its end point.</summary>
    public IEnumerable<Vec2> Sample(int arcSteps = 24)
    {
        switch (Kind)
        {
            case EntityKind.Line:
                yield return Start;
                break;
            case EntityKind.Circle:
                for (var i = 0; i < arcSteps * 2; i++)
                {
                    yield return Center + Vec2.FromAngle((Ccw ? 1 : -1) * i * Math.PI / arcSteps, Radius);
                }

                break;
            default:
                var a0 = (Start - Center).Angle;
                var sweep = ArcSweep();
                var n = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / arcSteps)));
                for (var i = 0; i < n; i++)
                {
                    yield return Center + Vec2.FromAngle(a0 + (sweep * i / n), Radius);
                }

                break;
        }
    }

    /// <summary>Signed sweep from start to end (positive = counter-clockwise).</summary>
    public double ArcSweep()
    {
        var s = (End - Center).Angle - (Start - Center).Angle;
        if (Ccw)
        {
            while (s <= 1e-12)
            {
                s += 2 * Math.PI;
            }
        }
        else
        {
            while (s >= -1e-12)
            {
                s -= 2 * Math.PI;
            }
        }

        return s;
    }
}

/// <summary>A closed loop of segments.</summary>
public sealed class ProfileLoop(IReadOnlyList<LoopSegment> segments)
{
    public IReadOnlyList<LoopSegment> Segments { get; } = segments;

    public IReadOnlyList<Vec2> Polygon { get; } = [.. segments.SelectMany(s => s.Sample())];

    /// <summary>Signed area of the sampled polygon; positive when counter-clockwise.</summary>
    public double SignedArea
    {
        get
        {
            double a = 0;
            for (var i = 0; i < Polygon.Count; i++)
            {
                a += Vec2.Cross(Polygon[i], Polygon[(i + 1) % Polygon.Count]);
            }

            return a / 2;
        }
    }

    public bool Contains(Vec2 p)
    {
        var inside = false;
        for (int i = 0, j = Polygon.Count - 1; i < Polygon.Count; j = i++)
        {
            var a = Polygon[i];
            var b = Polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < ((b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>The same loop traversed the other way.</summary>
    public ProfileLoop Reversed() => new([.. Segments.Reverse().Select(s => s with
    {
        Start = s.End,
        End = s.Start,
        Ccw = !s.Ccw,
        Reversed = !s.Reversed,
    })]);
}

/// <summary>A planar region: a counter-clockwise outer loop and clockwise holes.</summary>
public sealed record ProfileRegion(ProfileLoop Outer, IReadOnlyList<ProfileLoop> Holes)
{
    /// <summary>A stable key for naming caps: the smallest entity id on the outer loop.</summary>
    public int Key => Outer.Segments.Min(s => s.EntityId);

    public double Area => Outer.SignedArea + Holes.Sum(h => h.SignedArea);
}

/// <summary>The closed regions found in a sketch.</summary>
public sealed record ProfileResult(IReadOnlyList<ProfileRegion> Regions, int OpenEntityCount);

/// <summary>
/// Finds closed regions in a sketch. Lines and arcs are joined end to end into
/// a planar graph; dangling chains are dropped; each connected component's
/// outer boundary is found by walking faces of the graph. Nested components
/// alternate between material and holes (even-odd), which is what extruding a
/// whole sketch does in mainstream CAD.
/// </summary>
public static class ProfileBuilder
{
    private sealed class Edge
    {
        public required SketchEntity Entity { get; init; }
        public int From { get; set; }
        public int To { get; set; }
        public bool Removed { get; set; }
    }

    private readonly record struct Half(int Edge, bool Reverse);

    public static ProfileResult Build(Sketch sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        var curves = sketch.Entities.Where(e => !e.IsConstruction && e is SketchLine or SketchArc or SketchCircle).ToList();
        var extent = curves.Count == 0 ? 1 : curves.SelectMany(Endpoints).Select(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y))).DefaultIfEmpty(1).Max();
        var tol = Math.Max(1e-7, extent * 1e-8) * 100;

        var nodes = new List<Vec2>();
        int NodeAt(Vec2 p)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (Vec2.Distance(nodes[i], p) <= tol)
                {
                    return i;
                }
            }

            nodes.Add(p);
            return nodes.Count - 1;
        }

        var outlines = new List<ProfileLoop>();
        var edges = new List<Edge>();
        foreach (var e in curves)
        {
            switch (e)
            {
                case SketchCircle c when c.Radius > tol:
                    outlines.Add(new ProfileLoop([new LoopSegment(c.Id, EntityKind.Circle, c.Center.Position, c.Center.Position, c.Center.Position, c.Radius, true, false)]));
                    break;
                case SketchLine l when l.Length > tol:
                    edges.Add(new Edge { Entity = l, From = NodeAt(l.Start.Position), To = NodeAt(l.End.Position) });
                    break;
                case SketchArc a when a.Radius > tol:
                    edges.Add(new Edge { Entity = a, From = NodeAt(a.Start.Position), To = NodeAt(a.End.Position) });
                    break;
            }
        }

        // A full-circle arc (start == end) is a loop on its own.
        foreach (var e in edges.Where(e => e.From == e.To))
        {
            var a = (SketchArc)e.Entity;
            outlines.Add(new ProfileLoop([new LoopSegment(a.Id, EntityKind.Circle, a.Center.Position, a.Center.Position, a.Center.Position, a.Radius, true, false)]));
            e.Removed = true;
        }

        // Prune dangling chains.
        var degree = new int[nodes.Count];
        foreach (var e in edges.Where(e => !e.Removed))
        {
            degree[e.From]++;
            degree[e.To]++;
        }

        var open = 0;
        bool pruned;
        do
        {
            pruned = false;
            foreach (var e in edges.Where(e => !e.Removed && (degree[e.From] == 1 || degree[e.To] == 1)))
            {
                e.Removed = true;
                degree[e.From]--;
                degree[e.To]--;
                open++;
                pruned = true;
            }
        }
        while (pruned);

        // Outgoing half-edges per node, sorted counter-clockwise by departure angle.
        var outgoing = new Dictionary<int, List<(Half H, double Angle)>>();
        for (var i = 0; i < edges.Count; i++)
        {
            if (edges[i].Removed)
            {
                continue;
            }

            foreach (var rev in new[] { false, true })
            {
                var h = new Half(i, rev);
                var from = rev ? edges[i].To : edges[i].From;
                if (!outgoing.TryGetValue(from, out var list))
                {
                    outgoing[from] = list = [];
                }

                list.Add((h, DepartureAngle(edges[i].Entity, rev, nodes[from])));
            }
        }

        foreach (var list in outgoing.Values)
        {
            list.Sort((a, b) => a.Angle.CompareTo(b.Angle));
        }

        Half Next(Half h)
        {
            var e = edges[h.Edge];
            var at = h.Reverse ? e.From : e.To;
            var list = outgoing[at];
            var twin = new Half(h.Edge, !h.Reverse);
            var i = list.FindIndex(x => x.H == twin);
            return list[(i - 1 + list.Count) % list.Count].H;
        }

        // Walk every face of the graph; clockwise walks are component outer boundaries.
        var visited = new HashSet<Half>();
        foreach (var start in outgoing.Values.SelectMany(l => l).Select(x => x.H))
        {
            if (visited.Contains(start))
            {
                continue;
            }

            var cycle = new List<Half>();
            var h = start;
            while (visited.Add(h))
            {
                cycle.Add(h);
                h = Next(h);
            }

            var loop = new ProfileLoop([.. cycle.Select(c => ToSegment(edges[c.Edge].Entity, c.Reverse))]);
            if (loop.SignedArea < 0)
            {
                outlines.Add(loop.Reversed());
            }
        }

        // Even-odd nesting of component outlines.
        var depth = outlines.Select(o => outlines.Count(other => !ReferenceEquals(other, o) && Inside(o, other))).ToArray();
        var regions = new List<ProfileRegion>();
        for (var i = 0; i < outlines.Count; i++)
        {
            if (depth[i] % 2 != 0)
            {
                continue;
            }

            var holes = new List<ProfileLoop>();
            for (var j = 0; j < outlines.Count; j++)
            {
                if (depth[j] == depth[i] + 1 && Inside(outlines[j], outlines[i]))
                {
                    holes.Add(outlines[j].Reversed());
                }
            }

            regions.Add(new ProfileRegion(outlines[i], holes));
        }

        regions.Sort((a, b) => a.Key.CompareTo(b.Key));
        return new ProfileResult(regions, open);
    }

    /// <summary>Is <paramref name="inner"/> inside <paramref name="outer"/>? Tested with a point of inner's boundary.</summary>
    private static bool Inside(ProfileLoop inner, ProfileLoop outer) =>
        Math.Abs(inner.SignedArea) < Math.Abs(outer.SignedArea) && outer.Contains(inner.Polygon[0]) &&
        outer.Contains(inner.Polygon[inner.Polygon.Count / 2]);

    private static IEnumerable<Vec2> Endpoints(SketchEntity e) => e switch
    {
        SketchLine l => [l.Start.Position, l.End.Position],
        SketchArc a => [a.Start.Position, a.End.Position, a.Center.Position],
        SketchCircle c => [c.Center.Position + new Vec2(c.Radius, c.Radius)],
        _ => [],
    };

    private static LoopSegment ToSegment(SketchEntity e, bool reverse) => e switch
    {
        SketchLine l => reverse
            ? new LoopSegment(l.Id, EntityKind.Line, l.End.Position, l.Start.Position, default, 0, true, true)
            : new LoopSegment(l.Id, EntityKind.Line, l.Start.Position, l.End.Position, default, 0, true, false),
        SketchArc a => reverse
            ? new LoopSegment(a.Id, EntityKind.Arc, a.End.Position, a.Start.Position, a.Center.Position, a.Radius, false, true)
            : new LoopSegment(a.Id, EntityKind.Arc, a.Start.Position, a.End.Position, a.Center.Position, a.Radius, true, false),
        _ => throw new InvalidOperationException($"Unexpected profile entity {e}"),
    };

    /// <summary>Angle of the direction a half-edge leaves its start node, nudged along the curve to break tangent ties.</summary>
    private static double DepartureAngle(SketchEntity e, bool reverse, Vec2 from)
    {
        Vec2 probe;
        switch (e)
        {
            case SketchLine l:
                probe = reverse ? l.Start.Position : l.End.Position;
                break;
            case SketchArc a:
                var sweep = a.Sweep;
                var t = Math.Min(0.02, 0.25) * sweep;
                var angle = reverse ? a.EndAngle - t : a.StartAngle + t;
                probe = a.Center.Position + Vec2.FromAngle(angle, a.Radius);
                break;
            default:
                throw new InvalidOperationException();
        }

        return (probe - from).Angle;
    }
}
