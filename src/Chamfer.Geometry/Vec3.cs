namespace Chamfer.Geometry;

/// <summary>A 3D vector or point in model space (millimetres).</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static readonly Vec3 UnitX = new(1, 0, 0);
    public static readonly Vec3 UnitY = new(0, 1, 0);
    public static readonly Vec3 UnitZ = new(0, 0, 1);

    public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));
    public double LengthSquared => (X * X) + (Y * Y) + (Z * Z);

    public Vec3 Normalized()
    {
        var l = Length;
        return l < 1e-300 ? Zero : new(X / l, Y / l, Z / l);
    }

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator -(Vec3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator *(double s, Vec3 a) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3 operator /(Vec3 a, double s) => new(a.X / s, a.Y / s, a.Z / s);

    public static double Dot(Vec3 a, Vec3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;

    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + ((b - a) * t);

    /// <summary>Some unit vector perpendicular to this one.</summary>
    public Vec3 AnyPerpendicular()
    {
        var n = Normalized();
        var helper = Math.Abs(n.X) < 0.9 ? UnitX : UnitY;
        return Cross(n, helper).Normalized();
    }

    public override string ToString() => FormattableString.Invariant($"({X:0.###}, {Y:0.###}, {Z:0.###})");
}
