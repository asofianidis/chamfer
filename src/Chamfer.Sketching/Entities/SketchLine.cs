namespace Chamfer.Sketching.Entities;

/// <summary>A line segment between two owned end points.</summary>
public sealed class SketchLine : SketchEntity
{
    public SketchLine(Vec2 start, Vec2 end)
    {
        Start = new SketchPoint(start) { Owner = this };
        End = new SketchPoint(end) { Owner = this };
    }

    public override EntityKind Kind => EntityKind.Line;

    public SketchPoint Start { get; }
    public SketchPoint End { get; }

    public Vec2 Direction => End.Position - Start.Position;
    public double Length => Direction.Length;
    public Vec2 Midpoint => Vec2.Lerp(Start.Position, End.Position, 0.5);

    public override IReadOnlyList<SketchPoint> Points => [Start, End];

    public override IEnumerable<Param> Params => [Start.XParam, Start.YParam, End.XParam, End.YParam];

    /// <summary>Parameter t of the projection of <paramref name="p"/> onto the infinite line (0 = start, 1 = end).</summary>
    public double Project(Vec2 p)
    {
        var d = Direction;
        var len2 = d.LengthSquared;
        return len2 < 1e-300 ? 0 : Vec2.Dot(p - Start.Position, d) / len2;
    }

    public override Vec2 ClosestPoint(Vec2 p) =>
        Vec2.Lerp(Start.Position, End.Position, Math.Clamp(Project(p), 0, 1));

    public override double DistanceTo(Vec2 p) => Vec2.Distance(ClosestPoint(p), p);
}
