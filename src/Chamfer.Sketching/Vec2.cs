namespace Chamfer.Sketching;

/// <summary>A double-precision 2D vector in sketch space (millimetres).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static readonly Vec2 Zero = new(0, 0);
    public static readonly Vec2 UnitX = new(1, 0);
    public static readonly Vec2 UnitY = new(0, 1);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;

    /// <summary>Unit vector in the same direction, or <see cref="Zero"/> for a zero vector.</summary>
    public Vec2 Normalized()
    {
        var len = Length;
        return len < 1e-300 ? Zero : new Vec2(X / len, Y / len);
    }

    /// <summary>The vector rotated 90 degrees counter-clockwise.</summary>
    public Vec2 Perp() => new(-Y, X);

    public Vec2 Rotate(double radians)
    {
        var (s, c) = Math.SinCos(radians);
        return new Vec2(X * c - Y * s, X * s + Y * c);
    }

    public double Angle => Math.Atan2(Y, X);

    public static double Dot(Vec2 a, Vec2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>The z-component of the 3D cross product (a.X*b.Y - a.Y*b.X).</summary>
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;

    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public static Vec2 FromAngle(double radians, double length = 1)
    {
        var (s, c) = Math.SinCos(radians);
        return new Vec2(c * length, s * length);
    }

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);
    public static Vec2 operator /(Vec2 a, double s) => new(a.X / s, a.Y / s);

    public override string ToString() => $"({X:0.####}, {Y:0.####})";
}
