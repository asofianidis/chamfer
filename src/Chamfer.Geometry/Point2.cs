namespace Chamfer.Geometry;

/// <summary>A 2D point on a drawing sheet or in view coordinates (millimetres).</summary>
public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 a, double s) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public static double Distance(Point2 a, Point2 b) => (a - b).Length;

    public static double Dot(Point2 a, Point2 b) => (a.X * b.X) + (a.Y * b.Y);

    public static double Cross(Point2 a, Point2 b) => (a.X * b.Y) - (a.Y * b.X);

    public Point2 Normalized()
    {
        var l = Length;
        return l < 1e-300 ? default : new(X / l, Y / l);
    }

    public Point2 Perp() => new(-Y, X);
}
