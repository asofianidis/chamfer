using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Assembly;

public enum MateEntityKind
{
    Plane,
    Axis,
    Point,
}

/// <summary>The geometry a mate works with: a plane (origin, normal), an axis (point, direction, radius) or a point.</summary>
public readonly record struct MateEntity(MateEntityKind Kind, Vec3 Origin, Vec3 Direction, double Radius = 0)
{
    public MateEntity Transformed(Transform3 t) => this with { Origin = t.Apply(Origin), Direction = t.ApplyVector(Direction).Normalized() };
}

/// <summary>Outcome of solving an assembly's mates.</summary>
/// <param name="DegreesOfFreedom">Remaining freedom of the movable components (0 = fully defined).</param>
/// <param name="FailedMates">Mates left unsatisfied (conflicting or impossible).</param>
public sealed record MateSolveResult(bool Converged, int DegreesOfFreedom, IReadOnlyList<Mate> FailedMates, double Residual);

/// <summary>
/// Solves mates by moving components. Each movable component has six unknowns
/// (a translation and a small rotation applied to its current placement);
/// Levenberg–Marquardt minimises the mate residuals, with the Jacobian taken
/// by finite differences. Fixed components never move.
/// </summary>
public static class MateSolver
{
    private const double Tolerance = 1e-9;

    public static MateEntity Resolve(AssemblyDocument asm, MateRef r)
    {
        ArgumentNullException.ThrowIfNull(asm);
        ArgumentNullException.ThrowIfNull(r);
        var c = asm.Find(r.ComponentId) ?? throw new InvalidOperationException($"Component {r.ComponentId} is missing.");
        return Local(asm.PartOf(c), r).Transformed(c.Transform);
    }

    /// <summary>The mate entity in the part's own coordinates.</summary>
    public static MateEntity Local(PartDocument part, MateRef r)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(r);
        if (r.PlaneId is { } planeId)
        {
            if (!part.State.Planes.TryGetValue(planeId, out var p))
            {
                throw new InvalidOperationException($"Plane {planeId} is missing.");
            }

            return new MateEntity(MateEntityKind.Plane, p.Origin, p.Normal);
        }

        foreach (var body in part.Bodies)
        {
            if (r.Face is { } fr && body.HasFace(fr.Name))
            {
                var f = body.Face(body.ResolveFace(fr, out _));
                return f.Surface switch
                {
                    SurfaceKind.Plane => new MateEntity(MateEntityKind.Plane, f.AxisOrigin, f.AxisDirection.Normalized()),
                    SurfaceKind.Cylinder or SurfaceKind.Cone => new MateEntity(MateEntityKind.Axis, f.AxisOrigin, f.AxisDirection.Normalized(), f.Radius),
                    SurfaceKind.Sphere => new MateEntity(MateEntityKind.Point, f.AxisOrigin, Vec3.UnitZ, f.Radius),
                    _ => new MateEntity(MateEntityKind.Plane, f.Centroid, f.Normal.Normalized()),
                };
            }

            if (r.Edge is { } er && body.HasEdge(er.Name))
            {
                var i = body.ResolveEdge(er, out _);
                var e = body.Edge(i);
                if (e.Curve == CurveKind.Line)
                {
                    return new MateEntity(MateEntityKind.Axis, e.Start, (e.End - e.Start).Normalized());
                }

                if (e.Curve == CurveKind.Circle)
                {
                    var n = Vec3.Cross(e.Start - e.CenterOrDirection, e.Midpoint - e.CenterOrDirection);
                    if (n.LengthSquared < 1e-18)
                    {
                        n = body.Face(body.Shape.FacesOfEdge(i)[0]).AxisDirection;
                    }

                    return new MateEntity(MateEntityKind.Axis, e.CenterOrDirection, n.Normalized(), e.Radius);
                }

                return new MateEntity(MateEntityKind.Point, e.Midpoint, Vec3.UnitZ);
            }
        }

        // The name is gone (the part changed); fall back to the nearest face to the hint.
        if ((r.Face ?? r.Edge) is { } hint && part.Bodies.Count > 0)
        {
            var body = part.Bodies[0];
            var f = body.Face(body.ResolveFace(hint, out _));
            return f.Surface == SurfaceKind.Plane
                ? new MateEntity(MateEntityKind.Plane, f.AxisOrigin, f.AxisDirection.Normalized())
                : new MateEntity(MateEntityKind.Axis, f.AxisOrigin, f.AxisDirection.Normalized(), f.Radius);
        }

        throw new InvalidOperationException("The mate reference can't be found.");
    }

    /// <summary>Which alignment needs the least rotation from the current placement.</summary>
    public static bool ClosestAlignment(AssemblyDocument asm, Mate mate)
    {
        ArgumentNullException.ThrowIfNull(mate);
        try
        {
            var a = Resolve(asm, mate.A);
            var b = Resolve(asm, mate.B);
            return Vec3.Dot(a.Direction, b.Direction) > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Which types of mate make sense between two entities.</summary>
    public static IReadOnlyList<MateType> Applicable(MateEntityKind a, MateEntityKind b) => (a, b) switch
    {
        (MateEntityKind.Plane, MateEntityKind.Plane) => [MateType.Coincident, MateType.Parallel, MateType.Perpendicular, MateType.Distance, MateType.Angle, MateType.Lock, MateType.Symmetric, MateType.Width, MateType.LimitDistance, MateType.LimitAngle],
        (MateEntityKind.Axis, MateEntityKind.Axis) => [MateType.Concentric, MateType.Parallel, MateType.Perpendicular, MateType.Angle, MateType.Coincident, MateType.Distance, MateType.Lock, MateType.Gear, MateType.Screw, MateType.RackPinion, MateType.Symmetric, MateType.LimitDistance],
        (MateEntityKind.Plane, MateEntityKind.Axis) or (MateEntityKind.Axis, MateEntityKind.Plane) => [MateType.Tangent, MateType.Coincident, MateType.Parallel, MateType.Perpendicular, MateType.Distance, MateType.Lock],
        _ => [MateType.Coincident, MateType.Distance, MateType.Lock],
    };

    /// <summary>Residuals of one mate for the given world-space entities and component placements.</summary>
    /// <summary>Rotation of a placement relative to its initial one, about an axis (radians).</summary>
    private static double TurnAbout(Transform3 now, Transform3? initial, Vec3 axis)
    {
        if (initial is not { } t0)
        {
            return 0;
        }

        var rel = now * AssemblyDocument.Inverse(t0);
        var u = axis.AnyPerpendicular();
        var v = rel.ApplyVector(u);
        v -= axis * Vec3.Dot(v, axis);
        return Math.Atan2(Vec3.Dot(Vec3.Cross(u, v), axis), Vec3.Dot(u, v));
    }

    private static void Residuals(Mate m, MateEntity a, MateEntity b, Transform3 ta, Transform3 tb, List<double> r, MateEntity? c = null, MateEntity? d = null)
    {
        var mechanical = m.Type is MateType.Gear or MateType.RackPinion or MateType.Screw or MateType.Symmetric or MateType.Width or MateType.Drive;
        var s = m.Aligned ? -1.0 : 1.0; // anti-aligned: dA = -dB  →  dA + dB = 0
        void Vec(Vec3 v)
        {
            r.Add(v.X);
            r.Add(v.Y);
            r.Add(v.Z);
        }

        void Facing() => Vec(a.Direction + (b.Direction * s));
        void Parallel() => Vec(Vec3.Cross(a.Direction, b.Direction));

        // Swap so that a plane, if any, comes first (not for mates where order matters).
        if (!mechanical && a.Kind != MateEntityKind.Plane && b.Kind == MateEntityKind.Plane)
        {
            (a, b) = (b, a);
        }

        switch (m.Type)
        {
            case MateType.Coincident:
                switch (a.Kind, b.Kind)
                {
                    case (MateEntityKind.Plane, MateEntityKind.Plane):
                        Facing();
                        r.Add(Vec3.Dot(b.Origin - a.Origin, a.Direction));
                        break;
                    case (MateEntityKind.Plane, MateEntityKind.Axis):
                        r.Add(Vec3.Dot(b.Direction, a.Direction));
                        r.Add(Vec3.Dot(b.Origin - a.Origin, a.Direction));
                        break;
                    case (MateEntityKind.Plane, MateEntityKind.Point):
                        r.Add(Vec3.Dot(b.Origin - a.Origin, a.Direction));
                        break;
                    case (MateEntityKind.Axis, MateEntityKind.Axis):
                        Parallel();
                        Vec(Vec3.Cross(b.Origin - a.Origin, a.Direction));
                        break;
                    case (MateEntityKind.Axis, MateEntityKind.Point):
                    case (MateEntityKind.Point, MateEntityKind.Axis):
                        var (ax, pt) = a.Kind == MateEntityKind.Axis ? (a, b) : (b, a);
                        Vec(Vec3.Cross(pt.Origin - ax.Origin, ax.Direction));
                        break;
                    default:
                        Vec(b.Origin - a.Origin);
                        break;
                }

                break;

            case MateType.Concentric:
                Parallel();
                Vec(Vec3.Cross(b.Origin - a.Origin, a.Direction));
                break;

            case MateType.Parallel:
                if (a.Kind == MateEntityKind.Plane && b.Kind == MateEntityKind.Axis)
                {
                    r.Add(Vec3.Dot(a.Direction, b.Direction));
                }
                else
                {
                    Facing();
                }

                break;

            case MateType.Perpendicular:
                if (a.Kind == MateEntityKind.Plane && b.Kind == MateEntityKind.Axis)
                {
                    Parallel();
                }
                else
                {
                    r.Add(Vec3.Dot(a.Direction, b.Direction));
                }

                break;

            case MateType.Angle:
                r.Add(Vec3.Dot(a.Direction, b.Direction) - (Math.Cos(m.Value * Math.PI / 180) * (m.Aligned ? 1 : -1)));
                break;

            case MateType.Distance:
                switch (a.Kind, b.Kind)
                {
                    case (MateEntityKind.Plane, MateEntityKind.Plane):
                        Facing();
                        r.Add(Vec3.Dot(b.Origin - a.Origin, a.Direction) - m.Value);
                        break;
                    case (MateEntityKind.Plane, _):
                        r.Add(Vec3.Dot(b.Origin - a.Origin, a.Direction) - (m.Aligned ? -m.Value : m.Value));
                        break;
                    case (MateEntityKind.Axis, MateEntityKind.Axis):
                        Parallel();
                        r.Add(Vec3.Cross(b.Origin - a.Origin, a.Direction).Length - m.Value);
                        break;
                    default:
                        r.Add(Vec3.Distance(a.Origin, b.Origin) - m.Value);
                        break;
                }

                break;

            case MateType.Tangent:
                if (a.Kind == MateEntityKind.Plane && b.Kind == MateEntityKind.Axis)
                {
                    r.Add(Vec3.Dot(a.Direction, b.Direction));
                    r.Add(Vec3.Dot(b.Origin - a.Origin, a.Direction) - (m.Aligned ? -b.Radius : b.Radius));
                }
                else if (a.Kind == MateEntityKind.Axis && b.Kind == MateEntityKind.Axis)
                {
                    // Two cylinders touching side by side.
                    Parallel();
                    r.Add(Vec3.Cross(b.Origin - a.Origin, a.Direction).Length - (a.Radius + b.Radius));
                }
                else
                {
                    r.Add(Vec3.Dot(b.Origin - a.Origin, a.Direction) - b.Radius);
                }

                break;

            case MateType.Symmetric when c is { } sym:
                var n = sym.Direction;
                if (a.Kind == MateEntityKind.Plane && b.Kind == MateEntityKind.Plane)
                {
                    Vec(a.Direction - (n * (2 * Vec3.Dot(a.Direction, n))) - b.Direction);
                }
                else
                {
                    Vec(Vec3.Cross(b.Origin - a.Origin, n));
                }

                r.Add(Vec3.Dot(a.Origin - sym.Origin, n) + Vec3.Dot(b.Origin - sym.Origin, n));
                break;

            case MateType.Width when c is { } c1 && d is { } d1:
                Vec(Vec3.Cross(a.Direction, c1.Direction));
                r.Add(Vec3.Dot(((a.Origin + b.Origin) * 0.5) - ((c1.Origin + d1.Origin) * 0.5), c1.Direction));
                break;

            case MateType.LimitDistance:
                var dist = a.Kind == MateEntityKind.Plane ? Vec3.Dot(b.Origin - a.Origin, a.Direction) : Vec3.Distance(a.Origin, b.Origin);
                if (a.Kind == MateEntityKind.Plane && b.Kind == MateEntityKind.Plane)
                {
                    Facing();
                }

                r.Add(dist > m.Value2 ? dist - m.Value2 : dist < m.Value ? dist - m.Value : 0);
                break;

            case MateType.LimitAngle:
                var angle = Math.Acos(Math.Clamp(Vec3.Dot(a.Direction, b.Direction), -1, 1)) * 180 / Math.PI;
                r.Add((angle > m.Value2 ? angle - m.Value2 : angle < m.Value ? angle - m.Value : 0) * Math.PI / 180 * 100);
                break;

            case MateType.Gear:
                var ra = Math.Max(a.Radius, 1);
                var thetaA = TurnAbout(ta, m.InitialA, a.Direction);
                var thetaB = TurnAbout(tb, m.InitialB, b.Direction);
                var ratio = m.Value > 0 ? m.Value : Math.Max(b.Radius, 1e-9) / ra;
                r.Add((thetaA + ((m.Aligned ? -1 : 1) * ratio * thetaB)) * ra);
                break;

            case MateType.RackPinion:
                var travel = m.InitialA is { } ia ? Vec3.Dot(new Vec3(ta.Tx - ia.Tx, ta.Ty - ia.Ty, ta.Tz - ia.Tz), a.Direction) : 0;
                var turn = TurnAbout(tb, m.InitialB, b.Direction);
                var perRadian = m.Value > 0 ? m.Value : b.Radius;
                r.Add(travel - ((m.Aligned ? -1 : 1) * perRadian * turn));
                break;

            case MateType.Drive:
                if (m.Aligned)
                {
                    var moved = m.InitialB is { } d0 ? Vec3.Dot(new Vec3(tb.Tx - d0.Tx, tb.Ty - d0.Ty, tb.Tz - d0.Tz), b.Direction) : 0;
                    r.Add(moved - m.Value);
                }
                else
                {
                    r.Add((TurnAbout(tb, m.InitialB, b.Direction) - m.Value) * Math.Max(b.Radius, 10));
                }

                break;

            case MateType.Screw:
                var advance = m.InitialB is { } ib ? Vec3.Dot(new Vec3(tb.Tx - ib.Tx, tb.Ty - ib.Ty, tb.Tz - ib.Tz), a.Direction) : 0;
                var spin = TurnAbout(tb, m.InitialB, a.Direction);
                r.Add(advance - ((m.Aligned ? -1 : 1) * m.Value * spin / (2 * Math.PI)));
                break;

            case MateType.Lock:
                var expected = ta * (m.Relative ?? Transform3.Identity);
                foreach (var p in new[] { Vec3.Zero, new Vec3(100, 0, 0), new Vec3(0, 100, 0) })
                {
                    Vec(tb.Apply(p) - expected.Apply(p));
                }

                break;
        }
    }

    /// <summary>Solves all active mates in place.</summary>
    public static MateSolveResult Solve(AssemblyDocument asm, int maxIterations = 200)
    {
        ArgumentNullException.ThrowIfNull(asm);
        var mates = asm.Mates.Where(m => !m.Suppressed && Active(asm, m.A) && Active(asm, m.B) && (m.C is null || Active(asm, m.C)) && (m.D is null || Active(asm, m.D))).ToList();
        var movable = asm.Components.Where(c => !c.Fixed && !c.Suppressed).ToList();
        var mated = movable.Where(c => mates.Any(m => new[] { m.A, m.B, m.C, m.D }.Any(r => r?.ComponentId == c.Id))).ToList();
        var freeUnmated = movable.Count - mated.Count;

        // Mate entities in each part's own coordinates, resolved once.
        var local = new Dictionary<MateRef, MateEntity>();
        var unresolved = new List<Mate>();
        foreach (var m in mates)
        {
            try
            {
                foreach (var r in new[] { m.A, m.B, m.C, m.D }.OfType<MateRef>())
                {
                    if (!local.ContainsKey(r))
                    {
                        local[r] = Local(asm.PartOf(asm.Find(r.ComponentId)!), r);
                    }
                }
            }
            catch (InvalidOperationException)
            {
                unresolved.Add(m);
            }
        }

        mates.RemoveAll(unresolved.Contains);
        var index = mated.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var start = mated.Select(c => c.Transform).ToArray();

        // Rotation unknowns are scaled by each component's size, so a unit of rotation costs about as
        // much as a unit of travel: the minimum-norm solve then slides parts rather than spinning them.
        var lever = mated.Select(c => Math.Max(1, asm.PartOf(c).Bodies.Aggregate(Box3.Empty, (b, x) => b.Union(x.Shape.Bounds)).Diagonal)).ToArray();

        Transform3 Place(Component c, double[] x)
        {
            if (!index.TryGetValue(c.Id, out var i))
            {
                return c.Transform;
            }

            var o = 6 * i;
            var t0 = start[i];
            var origin = new Vec3(t0.Tx, t0.Ty, t0.Tz);
            var w = new Vec3(x[o + 3], x[o + 4], x[o + 5]) / lever[i];
            var angle = w.Length;
            var rotated = angle < 1e-15 ? t0 : Transform3.Rotation(origin, w / angle, angle) * t0;
            return Transform3.Translation(new Vec3(x[o], x[o + 1], x[o + 2])) * rotated;
        }

        double[] Evaluate(double[] x, List<int>? owners = null)
        {
            var r = new List<double>();
            foreach (var m in mates)
            {
                var ca = asm.Find(m.A.ComponentId)!;
                var cb = asm.Find(m.B.ComponentId)!;
                var ta = Place(ca, x);
                var tb = Place(cb, x);
                var before = r.Count;
                MateEntity? ec = m.C is { } mc ? local[mc].Transformed(Place(asm.Find(mc.ComponentId)!, x)) : null;
                MateEntity? ed = m.D is { } md ? local[md].Transformed(Place(asm.Find(md.ComponentId)!, x)) : null;
                Residuals(m, local[m.A].Transformed(ta), local[m.B].Transformed(tb), ta, tb, r, ec, ed);
                for (var k = before; k < r.Count; k++)
                {
                    owners?.Add(mates.IndexOf(m));
                }
            }

            return [.. r];
        }

        var n = 6 * mated.Count;
        var xk = new double[n];
        var owner = new List<int>();
        var res = Evaluate(xk, owner);
        double Norm(double[] v) => Math.Sqrt(v.Sum(e => e * e));
        var lambda = 1e-3;
        var converged = Norm(res) < Tolerance;

        // Try the solve; if it stalls (a zero-gradient start such as an angle mate from exactly aligned,
        // or a part that must turn half a revolution), retry from nudged and flipped starts.
        var starts = new (double X, double Y, double Z)[] { (0.05, 0.07, 0.09), (Math.PI, 0, 0), (0, Math.PI, 0), (0, 0, Math.PI) };
        var best = (X: xk, Res: res);
        for (var attempt = 0; !converged && n > 0 && attempt <= starts.Length; attempt++)
        {
            if (attempt > 0)
            {
                lambda = 1e-3;
                xk = new double[n];
                var (rx, ry, rz) = starts[attempt - 1];
                for (var c = 0; c < mated.Count; c++)
                {
                    xk[(6 * c) + 3] = rx * lever[c];
                    xk[(6 * c) + 4] = ry * lever[c];
                    xk[(6 * c) + 5] = rz * lever[c];
                }

                res = Evaluate(xk);
            }

            for (var iter = 0; iter < maxIterations && !converged; iter++)
            {
                var j = Jacobian(Evaluate, xk, res.Length);
                var step = SolveDamped(j, res, lambda);
                if (step is null)
                {
                    break;
                }

                var trial = xk.Zip(step, (a, d) => a + d).ToArray();
                var trialRes = Evaluate(trial);
                if (Norm(trialRes) < Norm(res))
                {
                    xk = trial;
                    res = trialRes;
                    lambda = Math.Max(lambda / 3, 1e-12);
                }
                else
                {
                    lambda *= 5;
                    if (lambda > 1e10)
                    {
                        break;
                    }
                }

                converged = Norm(res) < Tolerance * Math.Max(1, res.Length);
            }

            if (Norm(res) < Norm(best.Res))
            {
                best = (xk, res);
            }
        }

        (xk, res) = converged ? (xk, res) : best;

        // Keep the result if it improved anything; partially solved is better than nothing.
        var initial = Norm(Evaluate(new double[n]));
        if (Norm(res) <= initial || converged)
        {
            var placed = mated.Select(c => Place(c, xk)).ToArray();
            for (var i = 0; i < mated.Count; i++)
            {
                var t = Orthonormalize(placed[i]);
                if (t.IsRigid)
                {
                    mated[i].Transform = t;
                }
            }

            start = mated.Select(c => c.Transform).ToArray();
            xk = new double[n];
        }

        // Degrees of freedom from the rank of the Jacobian at the solution.
        var dof = (6 * freeUnmated) + (n == 0 ? 0 : n - Rank(Jacobian(Evaluate, xk, Evaluate(xk).Length)));
        var failed = new List<Mate>(unresolved);
        if (!converged)
        {
            var finalRes = Evaluate(xk, owner = []);
            for (var k = 0; k < finalRes.Length; k++)
            {
                if (Math.Abs(finalRes[k]) > 1e-6 && !failed.Contains(mates[owner[k]]))
                {
                    failed.Add(mates[owner[k]]);
                }
            }
        }

        // Mechanical mates measure turns from a reference placement, and angles wrap at half a turn.
        // Once they're satisfied, move the reference up to now so motion can run on for many turns.
        if (converged)
        {
            foreach (var m in mates.Where(m => m.Type is MateType.Gear or MateType.RackPinion or MateType.Screw))
            {
                m.InitialA = asm.Find(m.A.ComponentId)?.Transform;
                m.InitialB = asm.Find(m.B.ComponentId)?.Transform;
            }
        }

        return new MateSolveResult(converged || mates.Count == 0, dof, failed, Norm(res));
    }

    private static bool Active(AssemblyDocument asm, MateRef r) => asm.Find(r.ComponentId) is { Suppressed: false };

    private static double[,] Jacobian(Func<double[], List<int>?, double[]> f, double[] x, int rows)
    {
        const double h = 1e-7;
        var j = new double[rows, x.Length];
        for (var c = 0; c < x.Length; c++)
        {
            var xp = (double[])x.Clone();
            var xm = (double[])x.Clone();
            xp[c] += h;
            xm[c] -= h;
            var fp = f(xp, null);
            var fm = f(xm, null);
            for (var r = 0; r < rows; r++)
            {
                j[r, c] = (fp[r] - fm[r]) / (2 * h);
            }
        }

        return j;
    }

    /// <summary>(JᵀJ + λ diag(JᵀJ)) δ = -Jᵀr, solved by Gaussian elimination with pivoting.</summary>
    private static double[]? SolveDamped(double[,] j, double[] r, double lambda)
    {
        var rows = r.Length;
        var n = j.GetLength(1);
        var a = new double[n, n + 1];
        for (var p = 0; p < n; p++)
        {
            for (var q = 0; q < n; q++)
            {
                double sum = 0;
                for (var k = 0; k < rows; k++)
                {
                    sum += j[k, p] * j[k, q];
                }

                a[p, q] = sum;
            }

            double g = 0;
            for (var k = 0; k < rows; k++)
            {
                g += j[k, p] * r[k];
            }

            a[p, n] = -g;
        }

        // Damping, plus a floor tied to the largest curvature so free directions (zero columns)
        // don't amplify finite-difference noise into large drifts.
        double maxDiag = 0;
        for (var p = 0; p < n; p++)
        {
            maxDiag = Math.Max(maxDiag, a[p, p]);
        }

        for (var p = 0; p < n; p++)
        {
            a[p, p] += (lambda * a[p, p]) + (1e-9 * maxDiag) + 1e-12;
        }

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
            {
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = row;
                }
            }

            if (Math.Abs(a[pivot, col]) < 1e-300)
            {
                return null;
            }

            for (var k = 0; k <= n; k++)
            {
                (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            }

            for (var row = col + 1; row < n; row++)
            {
                var f = a[row, col] / a[col, col];
                for (var k = col; k <= n; k++)
                {
                    a[row, k] -= f * a[col, k];
                }
            }
        }

        var x = new double[n];
        for (var row = n - 1; row >= 0; row--)
        {
            var sum = a[row, n];
            for (var k = row + 1; k < n; k++)
            {
                sum -= a[row, k] * x[k];
            }

            x[row] = sum / a[row, row];
        }

        return x;
    }

    /// <summary>Numerical rank by Gram–Schmidt on the Jacobian's rows.</summary>
    private static int Rank(double[,] j)
    {
        var rows = j.GetLength(0);
        var n = j.GetLength(1);
        var basis = new List<double[]>();
        for (var r = 0; r < rows; r++)
        {
            var v = new double[n];
            for (var c = 0; c < n; c++)
            {
                v[c] = j[r, c];
            }

            var norm0 = Math.Sqrt(v.Sum(e => e * e));
            if (norm0 < 1e-12)
            {
                continue;
            }

            foreach (var b in basis)
            {
                var dot = 0.0;
                for (var c = 0; c < n; c++)
                {
                    dot += v[c] * b[c];
                }

                for (var c = 0; c < n; c++)
                {
                    v[c] -= dot * b[c];
                }
            }

            var norm = Math.Sqrt(v.Sum(e => e * e));
            if (norm > 1e-6 * norm0)
            {
                basis.Add([.. v.Select(e => e / norm)]);
            }
        }

        return basis.Count;
    }

    /// <summary>Removes round-off drift from a rotation so transforms stay rigid.</summary>
    private static Transform3 Orthonormalize(Transform3 t)
    {
        var x = new Vec3(t.M00, t.M10, t.M20).Normalized();
        var y = new Vec3(t.M01, t.M11, t.M21);
        y = (y - (x * Vec3.Dot(y, x))).Normalized();
        var z = Vec3.Cross(x, y);
        return new Transform3(x.X, y.X, z.X, t.Tx, x.Y, y.Y, z.Y, t.Ty, x.Z, y.Z, z.Z, t.Tz);
    }
}
