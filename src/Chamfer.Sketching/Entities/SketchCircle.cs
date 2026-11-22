using Chamfer.Sketching.Solving;

namespace Chamfer.Sketching.Entities;

public sealed class SketchCircle : SketchEntity, IRadial
{
    public SketchCircle(Vec2 center, double radius)
    {
        Center = new SketchPoint(center) { Owner = this };
        RadiusParam = new Param(radius);
    }

    public override EntityKind Kind => EntityKind.Circle;

    public SketchPoint Center { get; }
    public Param RadiusParam { get; }

    public double Radius => RadiusParam.Value;

    public override IReadOnlyList<SketchPoint> Points => [Center];

    public override IEnumerable<Param> Params => [Center.XParam, Center.YParam, RadiusParam];

    public void PartialRadius(IEquationSink sink, double scale) => sink.Partial(RadiusParam, scale);

    public override Vec2 ClosestPoint(Vec2 p)
    {
        var dir = (p - Center.Position).Normalized();
        if (dir == Vec2.Zero)
        {
            dir = Vec2.UnitX;
        }

        return Center.Position + dir * Math.Abs(Radius);
    }

    public override double DistanceTo(Vec2 p) => Math.Abs(Vec2.Distance(p, Center.Position) - Math.Abs(Radius));
}
