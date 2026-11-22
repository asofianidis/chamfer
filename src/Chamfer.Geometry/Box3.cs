namespace Chamfer.Geometry;

/// <summary>An axis-aligned bounding box.</summary>
public readonly record struct Box3(Vec3 Min, Vec3 Max)
{
    public static readonly Box3 Empty = new(
        new Vec3(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity),
        new Vec3(double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X;
    public Vec3 Center => (Min + Max) / 2;
    public Vec3 Size => IsEmpty ? Vec3.Zero : Max - Min;
    public double Diagonal => Size.Length;

    public Box3 Include(Vec3 p) => new(
        new Vec3(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z)),
        new Vec3(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z)));

    public Box3 Union(Box3 other) => other.IsEmpty ? this : IsEmpty ? other : Include(other.Min).Include(other.Max);
}
