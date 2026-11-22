using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>A reference plane offset from another plane or planar face.</summary>
public sealed class ReferencePlaneFeature : Feature
{
    public override string DefaultName => "Plane";

    public PlaneRef Base { get; set; } = PlaneRef.Front;

    public double Offset { get; set; } = 10;

    /// <summary>Offset to the other side and flip the normal.</summary>
    public bool Flip { get; set; }

    public override IEnumerable<string> Dependencies => Base.PlaneId is { } id ? [id] : [];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var b = ctx.ResolvePlane(Base);
        var p = b.Offset(Flip ? -Offset : Offset);
        ctx.Planes[Id] = Flip ? new Plane3(p.Origin, -p.Normal, p.XDirection) : p;
    }
}

/// <summary>Base for features that repeat the tool bodies of seed features.</summary>
public abstract class PatternFeatureBase : Feature
{
    /// <summary>Boss, cut, hole or pattern features to repeat.</summary>
    public List<string> Seeds { get; set; } = [];

    public override IEnumerable<string> Dependencies => Seeds;

    /// <summary>The instance transforms, excluding the seed itself.</summary>
    protected abstract IEnumerable<(string Tag, Func<Shape, Operation> Apply)> Instances(RebuildContext ctx);

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Seeds.Count == 0)
        {
            throw new FeatureException("Select features to pattern.");
        }

        var instances = Instances(ctx).ToList();
        var made = new List<(Shape Shape, string[] Names)>();
        foreach (var seedId in Seeds)
        {
            if (!ctx.Tools.TryGetValue(seedId, out var seed))
            {
                throw new FeatureException("A seed feature is missing or failed.");
            }

            foreach (var (tag, apply) in instances)
            {
                using var op = apply(seed.Shape);
                var copy = op.Result;
                var names = new string?[copy.FaceCount];
                for (var i = 1; i <= seed.Shape.FaceCount; i++)
                {
                    foreach (var r in op.Modified(seed.Shape, TopoKind.Face, i, TopoKind.Face) ?? [])
                    {
                        names[r - 1] ??= $"{seed.FaceNames[i - 1]}@{Id}:{tag}";
                    }
                }

                var finished = Naming.Finish(copy, names, Id);
                made.Add((copy, finished));
                if (seed.IsCut)
                {
                    ctx.RemoveMaterial(copy, finished);
                }
                else
                {
                    ctx.AddMaterial(copy, finished, merge: true);
                }
            }
        }

        if (made.Count > 0)
        {
            var (tool, names) = ProfileShapes.Combine(made);
            ctx.Tools[Id] = new FeatureTool(tool, names, ctx.Tools[Seeds[0]].IsCut);
        }
    }
}

/// <summary>Repeats features along one or two directions.</summary>
public sealed class LinearPatternFeature : PatternFeatureBase
{
    public override string DefaultName => "LPattern";

    public AxisRef Direction { get; set; } = AxisRef.X;

    public double Spacing { get; set; } = 10;

    /// <summary>Total instances in direction 1, including the seed.</summary>
    public int Count { get; set; } = 2;

    public bool Reverse { get; set; }

    public AxisRef? Direction2 { get; set; }

    public double Spacing2 { get; set; } = 10;

    public int Count2 { get; set; } = 1;

    public bool Reverse2 { get; set; }

    protected override IEnumerable<(string, Func<Shape, Operation>)> Instances(RebuildContext ctx)
    {
        if (Count < 1 || Count2 < 1 || Count * Count2 > 10000)
        {
            throw new FeatureException("Instance counts must be between 1 and 10000 in total.");
        }

        var d1 = ctx.ResolveAxis(Direction).Direction * (Reverse ? -1 : 1);
        var d2 = Direction2 is null ? Vec3.Zero : ctx.ResolveAxis(Direction2).Direction * (Reverse2 ? -1 : 1);
        var n2 = Direction2 is null ? 1 : Count2;
        for (var i = 0; i < Count; i++)
        {
            for (var j = 0; j < n2; j++)
            {
                if (i == 0 && j == 0)
                {
                    continue;
                }

                var t = Transform3.Translation((d1 * (Spacing * i)) + (d2 * (Spacing2 * j)));
                yield return ($"{i},{j}", s => Modeling.Transform(s, t));
            }
        }
    }
}

/// <summary>Repeats features around an axis.</summary>
public sealed class CircularPatternFeature : PatternFeatureBase
{
    public override string DefaultName => "CirPattern";

    public AxisRef Axis { get; set; } = AxisRef.Z;

    /// <summary>Total instances, including the seed.</summary>
    public int Count { get; set; } = 4;

    /// <summary>Degrees covered by the pattern.</summary>
    public double TotalAngle { get; set; } = 360;

    public bool Reverse { get; set; }

    protected override IEnumerable<(string, Func<Shape, Operation>)> Instances(RebuildContext ctx)
    {
        if (Count < 1 || Count > 10000)
        {
            throw new FeatureException("The instance count must be between 1 and 10000.");
        }

        var (origin, dir) = ctx.ResolveAxis(Axis);
        if (Reverse)
        {
            dir = -dir;
        }

        var full = Math.Abs(TotalAngle - 360) < 1e-9;
        var step = TotalAngle * Math.PI / 180 / (full ? Count : Math.Max(1, Count - 1));
        for (var i = 1; i < Count; i++)
        {
            var t = Transform3.Rotation(origin, dir, step * i);
            yield return (i.ToString(System.Globalization.CultureInfo.InvariantCulture), s => Modeling.Transform(s, t));
        }
    }
}

/// <summary>Mirrors features about a plane.</summary>
public sealed class MirrorFeature : PatternFeatureBase
{
    public override string DefaultName => "Mirror";

    public PlaneRef Plane { get; set; } = PlaneRef.Right;

    protected override IEnumerable<(string, Func<Shape, Operation>)> Instances(RebuildContext ctx)
    {
        var p = ctx.ResolvePlane(Plane);
        yield return ("m", s => Modeling.Mirror(s, p.Origin, p.Normal));
    }
}
