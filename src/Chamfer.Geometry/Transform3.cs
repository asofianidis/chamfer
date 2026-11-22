namespace Chamfer.Geometry;

/// <summary>An affine transform: a 3x3 linear part (row-major) and a translation.</summary>
public readonly record struct Transform3(
    double M00, double M01, double M02, double Tx,
    double M10, double M11, double M12, double Ty,
    double M20, double M21, double M22, double Tz)
{
    public static readonly Transform3 Identity = new(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0);

    public static Transform3 Translation(Vec3 t) => new(1, 0, 0, t.X, 0, 1, 0, t.Y, 0, 0, 1, t.Z);

    /// <summary>Rotation by <paramref name="angle"/> radians about an axis through <paramref name="origin"/>.</summary>
    public static Transform3 Rotation(Vec3 origin, Vec3 axis, double angle)
    {
        var a = axis.Normalized();
        double c = Math.Cos(angle), s = Math.Sin(angle), t = 1 - c;
        var r = new Transform3(
            (t * a.X * a.X) + c, (t * a.X * a.Y) - (s * a.Z), (t * a.X * a.Z) + (s * a.Y), 0,
            (t * a.X * a.Y) + (s * a.Z), (t * a.Y * a.Y) + c, (t * a.Y * a.Z) - (s * a.X), 0,
            (t * a.X * a.Z) - (s * a.Y), (t * a.Y * a.Z) + (s * a.X), (t * a.Z * a.Z) + c, 0);
        return Translation(origin) * r * Translation(-origin);
    }

    /// <summary>Reflection through a plane.</summary>
    /// <summary>Uniform scaling about a point.</summary>
    public static Transform3 Scale(Vec3 origin, double factor) =>
        new(factor, 0, 0, origin.X * (1 - factor), 0, factor, 0, origin.Y * (1 - factor), 0, 0, factor, origin.Z * (1 - factor));

    public static Transform3 Mirror(Vec3 origin, Vec3 normal)
    {
        var n = normal.Normalized();
        var r = new Transform3(
            1 - (2 * n.X * n.X), -2 * n.X * n.Y, -2 * n.X * n.Z, 0,
            -2 * n.X * n.Y, 1 - (2 * n.Y * n.Y), -2 * n.Y * n.Z, 0,
            -2 * n.X * n.Z, -2 * n.Y * n.Z, 1 - (2 * n.Z * n.Z), 0);
        return Translation(origin) * r * Translation(-origin);
    }

    public static Transform3 operator *(Transform3 a, Transform3 b) => new(
        (a.M00 * b.M00) + (a.M01 * b.M10) + (a.M02 * b.M20),
        (a.M00 * b.M01) + (a.M01 * b.M11) + (a.M02 * b.M21),
        (a.M00 * b.M02) + (a.M01 * b.M12) + (a.M02 * b.M22),
        (a.M00 * b.Tx) + (a.M01 * b.Ty) + (a.M02 * b.Tz) + a.Tx,
        (a.M10 * b.M00) + (a.M11 * b.M10) + (a.M12 * b.M20),
        (a.M10 * b.M01) + (a.M11 * b.M11) + (a.M12 * b.M21),
        (a.M10 * b.M02) + (a.M11 * b.M12) + (a.M12 * b.M22),
        (a.M10 * b.Tx) + (a.M11 * b.Ty) + (a.M12 * b.Tz) + a.Ty,
        (a.M20 * b.M00) + (a.M21 * b.M10) + (a.M22 * b.M20),
        (a.M20 * b.M01) + (a.M21 * b.M11) + (a.M22 * b.M21),
        (a.M20 * b.M02) + (a.M21 * b.M12) + (a.M22 * b.M22),
        (a.M20 * b.Tx) + (a.M21 * b.Ty) + (a.M22 * b.Tz) + a.Tz);

    public Vec3 Apply(Vec3 p) => new(
        (M00 * p.X) + (M01 * p.Y) + (M02 * p.Z) + Tx,
        (M10 * p.X) + (M11 * p.Y) + (M12 * p.Z) + Ty,
        (M20 * p.X) + (M21 * p.Y) + (M22 * p.Z) + Tz);

    public Vec3 ApplyVector(Vec3 v) => new(
        (M00 * v.X) + (M01 * v.Y) + (M02 * v.Z),
        (M10 * v.X) + (M11 * v.Y) + (M12 * v.Z),
        (M20 * v.X) + (M21 * v.Y) + (M22 * v.Z));

    public double Determinant =>
        (M00 * ((M11 * M22) - (M12 * M21))) - (M01 * ((M10 * M22) - (M12 * M20))) + (M02 * ((M10 * M21) - (M11 * M20)));

    public double[] ToArray() => [M00, M01, M02, Tx, M10, M11, M12, Ty, M20, M21, M22, Tz];
}
