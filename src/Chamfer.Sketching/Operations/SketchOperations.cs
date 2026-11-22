using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.Sketching.Operations;

/// <summary>
/// Editing operations on sketch geometry: trim, extend, fillet, chamfer,
/// offset, mirror and patterns. Each one edits the sketch in place, keeps it
/// connected by relating new end points to whatever they touch, and leaves the
/// solve to the caller.
/// </summary>
public static class SketchOperations
{
    private const double Tol = 1e-6;

    // ------------------------------------------------------------------ trim

    /// <summary>
    /// Power trim: removes the piece of <paramref name="entity"/> around
    /// <paramref name="at"/> between its nearest intersections with other
    /// curves. An entity with no intersections is deleted. Returns false if
    /// nothing changed.
    /// </summary>
    public static bool Trim(Sketch sketch, SketchEntity entity, Vec2 at)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entity);
        if (!Curves.IsCurve(entity))
        {
            return false;
        }

        var cuts = Cuts(sketch, entity);
        var t = Curves.ParamOf(entity, at);
        var (lo, hi) = Curves.Range(entity);

        if (entity is SketchCircle circle)
        {
            if (cuts.Count < 2)
            {
                return sketch.Remove(circle);
            }

            // The kept part runs counter-clockwise from the cut after the click to the cut before it.
            var after = cuts.FirstOrDefault(c => c.T > t, cuts[0]);
            var before = cuts.LastOrDefault(c => c.T < t, cuts[^1]);
            var arc = sketch.AddArc(circle.Center.Position, after.Point, before.Point, circle.IsConstruction);
            sketch.Remove(circle);
            Relate(sketch, arc.Start, after.With);
            Relate(sketch, arc.End, before.With);
            return true;
        }

        var below = cuts.Where(c => c.T < t - 1e-9).Select(c => (Cut?)c).LastOrDefault();
        var above = cuts.Where(c => c.T > t + 1e-9).Select(c => (Cut?)c).FirstOrDefault();
        var keepStart = below is { } b && b.T > lo + 1e-9;
        var keepEnd = above is { } a && a.T < hi - 1e-9;

        if (!keepStart && !keepEnd)
        {
            return sketch.Remove(entity);
        }

        var (startPoint, endPoint) = Ends(entity);
        if (keepStart && keepEnd)
        {
            // Split: this entity keeps [lo, below]; a new one takes [above, hi].
            var tail = CopyPiece(sketch, entity, above!.Value.Point, endPoint.Position);
            var (tailStart, tailEnd) = Ends(tail);
            MovePoint(sketch, entity, endPoint, below!.Value.Point);
            Relate(sketch, endPoint, below.Value.With);
            Relate(sketch, tailStart, above.Value.With);
            RelateToNeighbours(sketch, tailEnd, tail);
            if (entity is SketchLine l && tail is SketchLine tl)
            {
                sketch.AddConstraint(new CollinearConstraint(l, tl));
            }
            else if (entity is SketchArc && tail is SketchArc)
            {
                sketch.AddConstraint(new CoradialConstraint(entity, tail));
            }

            return true;
        }

        if (keepStart)
        {
            MovePoint(sketch, entity, endPoint, below!.Value.Point);
            Relate(sketch, endPoint, below.Value.With);
        }
        else
        {
            MovePoint(sketch, entity, startPoint, above!.Value.Point);
            Relate(sketch, startPoint, above.Value.With);
        }

        return true;
    }

    // ---------------------------------------------------------------- extend

    /// <summary>
    /// Extends the end of a line or arc nearest <paramref name="at"/> to the
    /// first curve it meets. Returns false if it meets nothing.
    /// </summary>
    public static bool Extend(Sketch sketch, SketchEntity entity, Vec2 at)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entity);
        if (entity is not (SketchLine or SketchArc))
        {
            return false;
        }

        var (start, end) = Ends(entity);
        var atEnd = Vec2.Distance(at, end.Position) <= Vec2.Distance(at, start.Position);
        var moving = atEnd ? end : start;
        Cut? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var other in sketch.Entities.Where(o => o != entity && Curves.IsCurve(o)))
        {
            foreach (var p in Curves.CarrierIntersections(entity, other).Where(p => Curves.Within(other, p)))
            {
                double distance;
                if (entity is SketchLine line)
                {
                    var t = Curves.ParamOf(line, p);
                    distance = atEnd ? t - 1 : -t;
                    distance *= line.Length;
                }
                else
                {
                    var arc = (SketchArc)entity;
                    var phi = Curves.ParamOf(arc, p);
                    distance = (atEnd ? phi - arc.Sweep : (2 * Math.PI) - phi) * arc.Radius;
                    if (phi <= arc.Sweep + 1e-9 && phi >= -1e-9)
                    {
                        continue;
                    }
                }

                if (distance > Tol && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = new Cut(0, p, other);
                }
            }
        }

        if (best is not { } target)
        {
            return false;
        }

        MovePoint(sketch, entity, moving, target.Point);
        Relate(sketch, moving, target.With);
        return true;
    }

    // ---------------------------------------------------------- fillet/chamfer

    /// <summary>Finds two lines meeting at a corner near <paramref name="at"/>.</summary>
    public static (SketchLine A, SketchLine B, Vec2 Corner)? FindCorner(Sketch sketch, Vec2 at, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        var lines = sketch.Entities.OfType<SketchLine>().Where(l => !l.IsConstruction).ToList();
        foreach (var a in lines)
        {
            foreach (var pa in new[] { a.Start, a.End })
            {
                if (Vec2.Distance(pa.Position, at) > tolerance)
                {
                    continue;
                }

                foreach (var b in lines.Where(b => b != a))
                {
                    if (Vec2.Distance(b.Start.Position, pa.Position) < Tol || Vec2.Distance(b.End.Position, pa.Position) < Tol)
                    {
                        return (a, b, pa.Position);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Rounds the corner where two lines meet with a tangent arc of the given radius.</summary>
    public static SketchArc Fillet(Sketch sketch, SketchLine a, SketchLine b, double radius)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var (pa, pb, corner, ua, ub, la, lb) = Corner(a, b);
        var theta = Math.Acos(Math.Clamp(Vec2.Dot(ua, ub), -1, 1));
        if (radius <= 0 || theta < 1e-6 || Math.PI - theta < 1e-6)
        {
            throw new ArgumentException("The lines must meet at an angle, and the radius must be positive.");
        }

        var setback = radius / Math.Tan(theta / 2);
        if (setback >= la - Tol || setback >= lb - Tol)
        {
            throw new ArgumentException("The fillet radius is too large for these lines.");
        }

        var ta = corner + (ua * setback);
        var tb = corner + (ub * setback);
        var center = corner + ((ua + ub).Normalized() * (radius / Math.Sin(theta / 2)));
        var ccw = Vec2.Cross(ta - center, tb - center) > 0;
        var arc = sketch.AddArc(center, ccw ? ta : tb, ccw ? tb : ta);

        DetachCorner(sketch, pa, pb);
        pa.Position = ta;
        pb.Position = tb;
        sketch.AddConstraint(new CoincidentConstraint(pa, ccw ? arc.Start : arc.End));
        sketch.AddConstraint(new CoincidentConstraint(pb, ccw ? arc.End : arc.Start));
        sketch.AddConstraint(new TangentConstraint(a, arc));
        sketch.AddConstraint(new TangentConstraint(b, arc));
        sketch.AddConstraint(new RadialDimensionConstraint(arc, diameter: false, radius) { LabelOffset = (center - corner).Normalized() * -radius });
        return arc;
    }

    /// <summary>Bevels the corner where two lines meet, cutting <paramref name="distance"/> back along each.</summary>
    public static SketchLine Chamfer(Sketch sketch, SketchLine a, SketchLine b, double distance, double? distance2 = null)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var d2 = distance2 ?? distance;
        var (pa, pb, corner, ua, ub, la, lb) = Corner(a, b);
        if (distance <= 0 || d2 <= 0 || distance >= la - Tol || d2 >= lb - Tol)
        {
            throw new ArgumentException("The chamfer distance must be positive and shorter than both lines.");
        }

        var ta = corner + (ua * distance);
        var tb = corner + (ub * d2);
        var bevel = sketch.AddLine(ta, tb);
        DetachCorner(sketch, pa, pb);
        pa.Position = ta;
        pb.Position = tb;
        sketch.AddConstraint(new CoincidentConstraint(pa, bevel.Start));
        sketch.AddConstraint(new CoincidentConstraint(pb, bevel.End));
        return bevel;
    }

    private static (SketchPoint Pa, SketchPoint Pb, Vec2 Corner, Vec2 Ua, Vec2 Ub, double La, double Lb) Corner(SketchLine a, SketchLine b)
    {
        foreach (var pa in new[] { a.Start, a.End })
        {
            foreach (var pb in new[] { b.Start, b.End })
            {
                if (Vec2.Distance(pa.Position, pb.Position) < 1e-5)
                {
                    var farA = pa == a.Start ? a.End : a.Start;
                    var farB = pb == b.Start ? b.End : b.Start;
                    var corner = pa.Position;
                    return (pa, pb, corner, (farA.Position - corner).Normalized(), (farB.Position - corner).Normalized(),
                        Vec2.Distance(farA.Position, corner), Vec2.Distance(farB.Position, corner));
                }
            }
        }

        throw new ArgumentException("The lines don't share an end point.");
    }

    /// <summary>Drops relations and dimensions that pinned two corner points together or in place.</summary>
    private static void DetachCorner(Sketch sketch, SketchPoint pa, SketchPoint pb)
    {
        foreach (var c in sketch.Constraints.Where(c => c.References.Contains(pa) || c.References.Contains(pb)).ToList())
        {
            sketch.RemoveConstraint(c);
        }
    }

    // ---------------------------------------------------------------- offset

    /// <summary>
    /// Offsets lines, arcs and circles by <paramref name="distance"/>. Connected
    /// entities are offset as chains with their corners re-joined. Closed chains
    /// and circles grow outward for a positive distance (inward when
    /// <paramref name="reverse"/>); open chains go to the left of their direction.
    /// </summary>
    public static IReadOnlyList<SketchEntity> Offset(Sketch sketch, IEnumerable<SketchEntity> entities, double distance, bool reverse = false)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entities);
        if (distance <= 0)
        {
            throw new ArgumentException("The offset distance must be positive.");
        }

        var sign = reverse ? -1 : 1;
        var created = new List<SketchEntity>();
        var selected = entities.Where(Curves.IsCurve).Distinct().ToList();
        foreach (var c in selected.OfType<SketchCircle>())
        {
            var r = c.Radius + (sign * distance);
            if (r <= Tol)
            {
                throw new ArgumentException("The offset would collapse a circle.");
            }

            created.Add(sketch.AddCircle(c.Center.Position, r, c.IsConstruction));
        }

        foreach (var chain in Chains(selected.Where(e => e is SketchLine or SketchArc).ToList()))
        {
            created.AddRange(OffsetChain(sketch, chain, distance * sign));
        }

        return created;
    }

    private readonly record struct Link(SketchEntity Entity, bool Reversed)
    {
        public Vec2 From => Reversed ? End(Entity) : Start(Entity);
        public Vec2 To => Reversed ? Start(Entity) : End(Entity);
    }

    private static Vec2 Start(SketchEntity e) => e switch { SketchLine l => l.Start.Position, SketchArc a => a.Start.Position, _ => default };
    private static Vec2 End(SketchEntity e) => e switch { SketchLine l => l.End.Position, SketchArc a => a.End.Position, _ => default };

    /// <summary>Orders connected lines and arcs into chains.</summary>
    private static List<(List<Link> Links, bool Closed)> Chains(List<SketchEntity> items)
    {
        var chains = new List<(List<Link>, bool)>();
        var left = new List<SketchEntity>(items);
        while (left.Count > 0)
        {
            var chain = new List<Link> { new(left[0], false) };
            left.RemoveAt(0);

            // Grow forward, then backward.
            for (var grew = true; grew;)
            {
                grew = false;
                var tail = chain[^1].To;
                var next = left.FindIndex(e => Vec2.Distance(Start(e), tail) < Tol || Vec2.Distance(End(e), tail) < Tol);
                if (next >= 0)
                {
                    var e = left[next];
                    chain.Add(new Link(e, Vec2.Distance(Start(e), tail) >= Tol));
                    left.RemoveAt(next);
                    grew = true;
                }
            }

            for (var grew = true; grew;)
            {
                grew = false;
                var head = chain[0].From;
                var prev = left.FindIndex(e => Vec2.Distance(Start(e), head) < Tol || Vec2.Distance(End(e), head) < Tol);
                if (prev >= 0)
                {
                    var e = left[prev];
                    chain.Insert(0, new Link(e, Vec2.Distance(End(e), head) >= Tol));
                    left.RemoveAt(prev);
                    grew = true;
                }
            }

            var closed = chain.Count > 1 && Vec2.Distance(chain[^1].To, chain[0].From) < Tol;
            chains.Add((chain, closed));
        }

        return chains;
    }

    private static List<SketchEntity> OffsetChain(Sketch sketch, (List<Link> Links, bool Closed) chain, double distance)
    {
        var (links, closed) = chain;

        // Left of the traversal for open chains; outward (right of a counter-clockwise loop) for closed ones.
        var left = distance;
        if (closed)
        {
            double area = 0;
            foreach (var link in links)
            {
                area += Vec2.Cross(link.From, link.To);
                if (link.Entity is SketchArc arc)
                {
                    // Add the circular segment between chord and arc.
                    var seg = (arc.Radius * arc.Radius / 2) * (arc.Sweep - Math.Sin(arc.Sweep));
                    area += 2 * (link.Reversed ? -seg : seg);
                }
            }

            left = area > 0 ? -distance : distance;
        }

        // Offset carriers.
        var pieces = new List<(Link Link, Vec2 From, Vec2 To, Vec2 Center, double Radius)>();
        foreach (var link in links)
        {
            switch (link.Entity)
            {
                case SketchLine:
                    var dir = (link.To - link.From).Normalized();
                    var n = dir.Perp() * left;
                    pieces.Add((link, link.From + n, link.To + n, default, 0));
                    break;
                case SketchArc arc:
                    // Left of a counter-clockwise arc is towards its centre.
                    var r = arc.Radius + (link.Reversed ? left : -left);
                    if (r <= Tol)
                    {
                        throw new ArgumentException("The offset would collapse an arc.");
                    }

                    var c = arc.Center.Position;
                    pieces.Add((link, c + ((link.From - c).Normalized() * r), c + ((link.To - c).Normalized() * r), c, r));
                    break;
            }
        }

        // Re-join neighbouring pieces at the intersection of their carriers nearest the original vertex.
        var joins = closed ? pieces.Count : pieces.Count - 1;
        for (var i = 0; i < joins; i++)
        {
            var j = (i + 1) % pieces.Count;
            var a = pieces[i];
            var b = pieces[j];
            if (Vec2.Distance(a.To, b.From) < Tol)
            {
                continue;
            }

            var vertex = a.Link.To;
            var best = Vec2.Lerp(a.To, b.From, 0.5);
            var bestD = double.PositiveInfinity;
            foreach (var p in CarrierPoints(a.From, a.To, a.Center, a.Radius, b.From, b.To, b.Center, b.Radius))
            {
                var d = Vec2.Distance(p, vertex);
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                }
            }

            pieces[i] = a with { To = best };
            pieces[j] = b with { From = best };
        }

        var created = new List<SketchEntity>();
        foreach (var p in pieces)
        {
            SketchEntity e = p.Link.Entity switch
            {
                SketchLine => sketch.AddLine(p.From, p.To, p.Link.Entity.IsConstruction),
                _ => p.Link.Reversed
                    ? sketch.AddArc(p.Center, p.To, p.From, p.Link.Entity.IsConstruction)
                    : sketch.AddArc(p.Center, p.From, p.To, p.Link.Entity.IsConstruction),
            };
            created.Add(e);
        }

        for (var i = 0; i < joins; i++)
        {
            var a = created[i];
            var b = created[(i + 1) % created.Count];
            var pa = EndPoint(a, pieces[i].Link.Reversed, atTo: true);
            var pb = EndPoint(b, pieces[(i + 1) % pieces.Count].Link.Reversed, atTo: false);
            sketch.AddConstraint(new CoincidentConstraint(pa, pb));
        }

        return created;
    }

    private static SketchPoint EndPoint(SketchEntity e, bool reversed, bool atTo)
    {
        var (s, t) = Ends(e);
        if (e is SketchArc)
        {
            // Reversed arcs were created with swapped ends, so they run in chain order already reversed.
            return atTo ? (reversed ? s : t) : (reversed ? t : s);
        }

        return atTo ? t : s;
    }

    private static IEnumerable<Vec2> CarrierPoints(Vec2 a0, Vec2 a1, Vec2 ac, double ar, Vec2 b0, Vec2 b1, Vec2 bc, double br)
    {
        SketchEntity Make(Vec2 p0, Vec2 p1, Vec2 c, double r) =>
            r > 0 ? new SketchCircle(c, r) : new SketchLine(p0, p1);

        return Curves.CarrierIntersections(Make(a0, a1, ac, ar), Make(b0, b1, bc, br));
    }

    // ---------------------------------------------------------------- mirror

    /// <summary>Mirrors entities about a line, with symmetric relations tying each copy to its original.</summary>
    public static IReadOnlyList<SketchEntity> Mirror(Sketch sketch, IEnumerable<SketchEntity> entities, SketchLine axis)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(axis);
        var o = axis.Start.Position;
        var d = axis.Direction.Normalized();
        if (d == Vec2.Zero)
        {
            throw new ArgumentException("The mirror line has no length.");
        }

        Vec2 Reflect(Vec2 p)
        {
            var v = p - o;
            var along = d * Vec2.Dot(v, d);
            return o + along - (v - along);
        }

        var created = new List<SketchEntity>();
        foreach (var e in entities.Where(e => e != axis).Distinct().ToList())
        {
            switch (e)
            {
                case SketchLine l:
                    var nl = sketch.AddLine(Reflect(l.Start.Position), Reflect(l.End.Position), l.IsConstruction);
                    Symmetric(sketch, l.Start, nl.Start, axis);
                    Symmetric(sketch, l.End, nl.End, axis);
                    created.Add(nl);
                    break;
                case SketchArc a:
                    // Reflection reverses orientation: the mirrored arc runs from the image of the end to the image of the start.
                    var na = sketch.AddArc(Reflect(a.Center.Position), Reflect(a.End.Position), Reflect(a.Start.Position), a.IsConstruction);
                    Symmetric(sketch, a.Center, na.Center, axis);
                    Symmetric(sketch, a.Start, na.End, axis);
                    Symmetric(sketch, a.End, na.Start, axis);
                    created.Add(na);
                    break;
                case SketchCircle c:
                    var nc = sketch.AddCircle(Reflect(c.Center.Position), c.Radius, c.IsConstruction);
                    Symmetric(sketch, c.Center, nc.Center, axis);
                    sketch.AddConstraint(new EqualConstraint(c, nc));
                    created.Add(nc);
                    break;
                case SketchPoint p when p.Owner is null:
                    var np = sketch.AddPoint(Reflect(p.Position));
                    Symmetric(sketch, p, np, axis);
                    created.Add(np);
                    break;
            }
        }

        return created;
    }

    private static void Symmetric(Sketch sketch, SketchPoint a, SketchPoint b, SketchLine axis)
    {
        // A point on the axis mirrors onto itself; a coincidence says that more simply.
        if (Vec2.Distance(a.Position, b.Position) < Tol)
        {
            sketch.AddConstraint(new CoincidentConstraint(a, b));
        }
        else
        {
            sketch.AddConstraint(new SymmetricConstraint(a, b, axis));
        }
    }

    // --------------------------------------------------------------- patterns

    /// <summary>Copies entities along a direction: <paramref name="count"/> instances including the originals.</summary>
    public static IReadOnlyList<SketchEntity> LinearPattern(Sketch sketch, IEnumerable<SketchEntity> entities, Vec2 step, int count)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entities);
        var items = entities.ToList();
        var created = new List<SketchEntity>();
        for (var i = 1; i < count; i++)
        {
            var offset = step * i;
            created.AddRange(items.Select(e => Copy(sketch, e, p => p + offset)).OfType<SketchEntity>());
        }

        return created;
    }

    /// <summary>Copies entities around a centre: <paramref name="count"/> instances spread evenly over <paramref name="totalAngle"/>.</summary>
    public static IReadOnlyList<SketchEntity> CircularPattern(Sketch sketch, IEnumerable<SketchEntity> entities, Vec2 center, int count, double totalAngle = 2 * Math.PI)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        ArgumentNullException.ThrowIfNull(entities);
        var items = entities.ToList();
        var created = new List<SketchEntity>();
        var full = Math.Abs(totalAngle - (2 * Math.PI)) < 1e-9;
        var step = totalAngle / (full ? count : Math.Max(1, count - 1));
        for (var i = 1; i < count; i++)
        {
            var angle = step * i;
            created.AddRange(items.Select(e => Copy(sketch, e, p => center + (p - center).Rotate(angle))).OfType<SketchEntity>());
        }

        return created;
    }

    private static SketchEntity? Copy(Sketch sketch, SketchEntity e, Func<Vec2, Vec2> map) => e switch
    {
        SketchLine l => sketch.AddLine(map(l.Start.Position), map(l.End.Position), l.IsConstruction),
        SketchArc a => sketch.AddArc(map(a.Center.Position), map(a.Start.Position), map(a.End.Position), a.IsConstruction),
        SketchCircle c => sketch.AddCircle(map(c.Center.Position), c.Radius, c.IsConstruction),
        SketchPoint { Owner: null } p => sketch.AddPoint(map(p.Position)),
        _ => null,
    };

    // ---------------------------------------------------------------- helpers

    private readonly record struct Cut(double T, Vec2 Point, SketchEntity With);

    /// <summary>Where other curves cross <paramref name="entity"/>, sorted by parameter.</summary>
    private static List<Cut> Cuts(Sketch sketch, SketchEntity entity)
    {
        var cuts = new List<Cut>();
        foreach (var other in sketch.Entities.Where(o => o != entity && Curves.IsCurve(o)))
        {
            foreach (var p in Curves.Intersections(entity, other))
            {
                cuts.Add(new Cut(Curves.ParamOf(entity, p), p, other));
            }
        }

        cuts.Sort((a, b) => a.T.CompareTo(b.T));
        return cuts;
    }

    private static (SketchPoint Start, SketchPoint End) Ends(SketchEntity e) => e switch
    {
        SketchLine l => (l.Start, l.End),
        SketchArc a => (a.Start, a.End),
        _ => throw new ArgumentException("Not an open curve.", nameof(e)),
    };

    /// <summary>A new entity like <paramref name="e"/> running from one point to another.</summary>
    private static SketchEntity CopyPiece(Sketch sketch, SketchEntity e, Vec2 from, Vec2 to) => e switch
    {
        SketchLine l => sketch.AddLine(from, to, l.IsConstruction),
        SketchArc a => sketch.AddArc(a.Center.Position, from, to, a.IsConstruction),
        _ => throw new ArgumentException("Not an open curve.", nameof(e)),
    };

    /// <summary>
    /// Moves an end point, dropping the relations and dimensions that pinned it
    /// (and a length dimension on the entity), which no longer describe the geometry.
    /// </summary>
    private static void MovePoint(Sketch sketch, SketchEntity owner, SketchPoint point, Vec2 to)
    {
        foreach (var c in sketch.Constraints.Where(c => c.References.Contains(point) || (c is DistanceConstraint d && d.Line == owner)).ToList())
        {
            sketch.RemoveConstraint(c);
        }

        point.Position = to;
    }

    /// <summary>Ties a new end point to the curve it lies on (or a point there).</summary>
    private static void Relate(Sketch sketch, SketchPoint point, SketchEntity with)
    {
        foreach (var other in with.Points)
        {
            if (Vec2.Distance(other.Position, point.Position) < Tol)
            {
                sketch.AddConstraint(new CoincidentConstraint(point, other));
                return;
            }
        }

        switch (with)
        {
            case SketchLine l:
                sketch.AddConstraint(new PointOnLineConstraint(point, l));
                break;
            case SketchArc or SketchCircle:
                sketch.AddConstraint(new PointOnCircleConstraint(point, with));
                break;
        }
    }

    /// <summary>Re-creates coincidences for a point that took over another point's location.</summary>
    private static void RelateToNeighbours(Sketch sketch, SketchPoint point, SketchEntity owner)
    {
        foreach (var other in sketch.AllPoints.Where(p => p != point && p.Owner != owner && Vec2.Distance(p.Position, point.Position) < Tol))
        {
            sketch.AddConstraint(new CoincidentConstraint(point, other));
            return;
        }
    }
}
