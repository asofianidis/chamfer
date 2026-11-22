namespace Chamfer.Sketching.Entities;

public sealed class SketchPoint : SketchEntity
{
    public SketchPoint(Vec2 position)
    {
        XParam = new Param(position.X);
        YParam = new Param(position.Y);
    }

    public override EntityKind Kind => EntityKind.Point;

    public Param XParam { get; }
    public Param YParam { get; }

    public double X => XParam.Value;
    public double Y => YParam.Value;

    public Vec2 Position
    {
        get => new(XParam.Value, YParam.Value);
        set
        {
            XParam.Value = value.X;
            YParam.Value = value.Y;
        }
    }

    public override IEnumerable<Param> Params => [XParam, YParam];

    public override double DistanceTo(Vec2 p) => Vec2.Distance(Position, p);

    public override Vec2 ClosestPoint(Vec2 p) => Position;
}
