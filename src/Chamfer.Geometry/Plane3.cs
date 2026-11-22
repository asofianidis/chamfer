namespace Chamfer.Geometry;

/// <summary>
/// A plane with an orthonormal frame: origin, normal (local Z) and X direction.
/// Local Y is normal x X, so the frame is right-handed.
/// </summary>
public readonly record struct Plane3
{
    public Plane3(Vec3 origin, Vec3 normal, Vec3 xDirection)
    {
        var n = normal.Normalized();
        var x = (xDirection - (n * Vec3.Dot(xDirection, n))).Normalized();
        if (x.LengthSquared < 0.5)
        {
            x = n.AnyPerpendicular();
        }

        Origin = origin;
        Normal = n;
        XDirection = x;
    }

    public Vec3 Origin { get; }
    public Vec3 Normal { get; }
    public Vec3 XDirection { get; }
    public Vec3 YDirection => Vec3.Cross(Normal, XDirection);

    /// <summary>Front plane: XY, normal +Z.</summary>
    public static Plane3 Front => new(Vec3.Zero, Vec3.UnitZ, Vec3.UnitX);

    /// <summary>Top plane: XZ, normal +Y. Local X = +X, local Y = -Z.</summary>
    public static Plane3 Top => new(Vec3.Zero, Vec3.UnitY, Vec3.UnitX);

    /// <summary>Right plane: YZ, normal +X. Local X = -Z, local Y = +Y.</summary>
    public static Plane3 Right => new(Vec3.Zero, Vec3.UnitX, -Vec3.UnitZ);

    public Vec3 ToWorld(double u, double v) => Origin + (XDirection * u) + (YDirection * v);

    public (double U, double V) ToLocal(Vec3 p)
    {
        var d = p - Origin;
        return (Vec3.Dot(d, XDirection), Vec3.Dot(d, YDirection));
    }

    public double SignedDistance(Vec3 p) => Vec3.Dot(p - Origin, Normal);

    public Plane3 Offset(double distance) => new(Origin + (Normal * distance), Normal, XDirection);

    /// <summary>Intersection of a ray with the plane, or null when parallel.</summary>
    public Vec3? Intersect(Vec3 rayOrigin, Vec3 rayDirection)
    {
        var denom = Vec3.Dot(rayDirection, Normal);
        if (Math.Abs(denom) < 1e-12)
        {
            return null;
        }

        var t = Vec3.Dot(Origin - rayOrigin, Normal) / denom;
        return rayOrigin + (rayDirection * t);
    }

    /// <summary>The 9 doubles (origin, normal, x) the native kernel expects.</summary>
    public double[] ToArray() =>
        [Origin.X, Origin.Y, Origin.Z, Normal.X, Normal.Y, Normal.Z, XDirection.X, XDirection.Y, XDirection.Z];
}
