using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

public enum CombineOperation
{
    Add,
    Subtract,
    Common,
}

/// <summary>Combines bodies: adds, subtracts or intersects tool bodies with a main body.</summary>
public sealed class CombineFeature : Feature
{
    public override string DefaultName => "Combine";

    public CombineOperation Operation { get; set; } = CombineOperation.Add;

    /// <summary>The body that survives (by name).</summary>
    public string MainBody { get; set; } = string.Empty;

    /// <summary>Bodies combined into it; they're consumed.</summary>
    public List<string> ToolBodies { get; set; } = [];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ToolBodies.Count == 0)
        {
            throw new FeatureException("Select the bodies to combine.");
        }

        var main = ctx.FindBody(MainBody);
        var current = new Naming.Input(main.Shape, main.FaceNames, main.EdgeNames);
        var kind = Operation switch
        {
            CombineOperation.Add => BooleanKind.Fuse,
            CombineOperation.Subtract => BooleanKind.Cut,
            _ => BooleanKind.Common,
        };
        foreach (var name in ToolBodies.Where(n => n != main.Name))
        {
            var tool = ctx.FindBody(name);
            using var op = Modeling.Boolean(current.Shape, tool.Shape, kind);
            var names = Naming.Transfer(op, op.Result, [current, new Naming.Input(tool.Shape, tool.FaceNames, tool.EdgeNames)], Id);
            current = new Naming.Input(op.Result, names);
            ctx.Bodies.Remove(tool);
        }

        if (current.Shape.SolidCount == 0)
        {
            throw new FeatureException("The combination leaves nothing.");
        }

        ctx.Bodies[ctx.Bodies.IndexOf(main)] = new Body(current.Shape, current.FaceNames, main.Name, main.Color);
    }
}

/// <summary>
/// Indent: presses a tool body into a target body, leaving a pocket shaped like the tool with a
/// clearance all round (the tool grown by the clearance); the tool body stays.
/// </summary>
public sealed class IndentFeature : Feature
{
    public override string DefaultName => "Indent";

    public string TargetBody { get; set; } = string.Empty;

    public string ToolBody { get; set; } = string.Empty;

    /// <summary>Gap between the tool and the pocket's walls.</summary>
    public double Clearance { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var target = ctx.FindBody(TargetBody);
        var tool = ctx.FindBody(ToolBody);
        if (ReferenceEquals(target, tool))
        {
            throw new FeatureException("Pick different target and tool bodies.");
        }

        if (Clearance < 0)
        {
            throw new FeatureException("The clearance can't be negative.");
        }

        Operation? grown = null;
        try
        {
            if (Clearance > 1e-9)
            {
                try
                {
                    grown = Modeling.OffsetShape(tool.Shape, Clearance);
                }
                catch (KernelException ex)
                {
                    throw new FeatureException("The tool can't be grown by that clearance.", ex);
                }
            }

            var cutter = grown?.Result ?? tool.Shape;
            using var op = Modeling.Boolean(target.Shape, cutter, BooleanKind.Cut);
            if (op.Result.SolidCount == 0)
            {
                throw new FeatureException("The indent leaves nothing of the target.");
            }

            if (Math.Abs(op.Result.MassProperties.Volume - target.Shape.MassProperties.Volume) < 1e-9)
            {
                ctx.Warn("The tool doesn't reach into the target.");
            }

            var names = Naming.Transfer(op, op.Result, [new Naming.Input(target.Shape, target.FaceNames, target.EdgeNames)], Id, "indent");
            ctx.Bodies[ctx.Bodies.IndexOf(target)] = new Body(op.Result, names, target.Name, target.Color);
        }
        finally
        {
            grown?.Dispose();
        }
    }
}

/// <summary>Cuts bodies in two with a plane; each piece becomes its own body.</summary>
public sealed class SplitBodyFeature : Feature
{
    public override string DefaultName => "Split";

    public PlaneRef Plane { get; set; } = PlaneRef.Right;

    /// <summary>Bodies to split; empty splits every body the plane crosses.</summary>
    public List<string> Bodies { get; set; } = [];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var plane = ctx.ResolvePlane(Plane);
        var targets = Bodies.Count == 0 ? [.. ctx.Bodies] : Bodies.Select(ctx.FindBody).ToList();
        var split = false;
        foreach (var body in targets)
        {
            using var tool = Modeling.PlaneFace(plane, (body.Shape.Bounds.Diagonal * 2) + 10);
            using var op = Modeling.Split(body.Shape, tool);
            var whole = op.Result;
            if (whole.SolidCount < 2)
            {
                continue;
            }

            split = true;
            var names = Naming.Transfer(op, whole, [new Naming.Input(body.Shape, body.FaceNames, body.EdgeNames)], Id, "split");
            var index = ctx.Bodies.IndexOf(body);
            ctx.Bodies.RemoveAt(index);
            for (var i = 1; i <= whole.SolidCount; i++)
            {
                var piece = whole.Sub(TopoKind.Solid, i);
                var pieceNames = new string[piece.FaceCount];
                for (var f = 1; f <= piece.FaceCount; f++)
                {
                    using var face = piece.Sub(TopoKind.Face, f);
                    var k = whole.IndexOf(TopoKind.Face, face);
                    pieceNames[f - 1] = k > 0 ? names[k - 1] : $"{Id}:face{f}";
                }

                ctx.Bodies.Insert(index + i - 1, new Body(piece, pieceNames, $"{body.Name}-{i}", body.Color));
            }
        }

        if (!split)
        {
            ctx.Warn("The plane doesn't cut any of the selected bodies.");
        }
    }
}

/// <summary>Removes bodies from the part (or keeps only the selected ones).</summary>
public sealed class DeleteBodyFeature : Feature
{
    public override string DefaultName => "Body-Delete/Keep";

    public List<string> Bodies { get; set; } = [];

    /// <summary>Keep the listed bodies and delete the rest.</summary>
    public bool Keep { get; set; }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var listed = Bodies.Select(ctx.FindBody).ToHashSet();
        ctx.Bodies.RemoveAll(b => Keep ? !listed.Contains(b) : listed.Contains(b));
    }
}

/// <summary>Moves or rotates bodies, or makes moved copies of them.</summary>
public sealed class MoveCopyBodyFeature : Feature
{
    public override string DefaultName => "Body-Move/Copy";

    public List<string> Bodies { get; set; } = [];

    public double Dx { get; set; }

    public double Dy { get; set; }

    public double Dz { get; set; }

    /// <summary>Rotation in degrees about the X, Y and Z axes through the origin, applied in that order before translating.</summary>
    public double Rx { get; set; }

    public double Ry { get; set; }

    public double Rz { get; set; }

    public bool Copy { get; set; }

    /// <summary>Number of copies when <see cref="Copy"/> is set; each copy applies the transform once more.</summary>
    public int Copies { get; set; } = 1;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Bodies.Count == 0)
        {
            throw new FeatureException("Select the bodies to move or copy.");
        }

        const double deg = Math.PI / 180;
        var step = Transform3.Translation(new Vec3(Dx, Dy, Dz)) *
            Transform3.Rotation(Vec3.Zero, Vec3.UnitZ, Rz * deg) *
            Transform3.Rotation(Vec3.Zero, Vec3.UnitY, Ry * deg) *
            Transform3.Rotation(Vec3.Zero, Vec3.UnitX, Rx * deg);
        foreach (var body in Bodies.Select(ctx.FindBody).ToList())
        {
            var count = Copy ? Math.Max(1, Copies) : 1;
            var t = step;
            for (var k = 1; k <= count; k++)
            {
                using var op = Modeling.Transform(body.Shape, t);
                var moved = op.Result;
                var names = new string[moved.FaceCount];
                for (var f = 1; f <= body.FaceCount; f++)
                {
                    foreach (var r in op.Modified(body.Shape, TopoKind.Face, f, TopoKind.Face) ?? [])
                    {
                        names[r - 1] = Copy ? $"{body.FaceNames[f - 1]}@{Id}:{k}" : body.FaceNames[f - 1];
                    }
                }

                var finished = Naming.Finish(moved, names, Id);
                if (Copy)
                {
                    ctx.Bodies.Add(new Body(moved, finished, ctx.NewBodyName(), body.Color));
                }
                else
                {
                    ctx.Bodies[ctx.Bodies.IndexOf(body)] = new Body(moved, finished, body.Name, body.Color);
                }

                t = step * t;
            }
        }
    }
}

/// <summary>Sets the display colour of bodies.</summary>
public sealed class AppearanceFeature : Feature
{
    public override string DefaultName => "Appearance";

    /// <summary>Bodies to colour; empty colours every body.</summary>
    public List<string> Bodies { get; set; } = [];

    /// <summary>Colour as 0xRRGGBB.</summary>
    public uint Color { get; set; } = 0x3B82F6;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var targets = Bodies.Count == 0 ? [.. ctx.Bodies] : Bodies.Select(ctx.FindBody).ToList();
        foreach (var b in targets)
        {
            ctx.Bodies[ctx.Bodies.IndexOf(b)] = b.WithColor(Color & 0xFFFFFF);
        }
    }
}

/// <summary>Repairs bodies (typically imported ones): sews gaps, fixes faces and shells, makes closed shells solid.</summary>
public sealed class HealFeature : Feature
{
    public override string DefaultName => "Heal";

    /// <summary>Bodies to heal; empty heals all of them.</summary>
    public List<string> Bodies { get; set; } = [];

    /// <summary>Gaps up to this size (mm) are closed.</summary>
    public double Tolerance { get; set; } = 0.01;

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Tolerance <= 0)
        {
            throw new FeatureException("The tolerance must be positive.");
        }

        var targets = Bodies.Count == 0 ? [.. ctx.Bodies] : Bodies.Select(ctx.FindBody).ToList();
        var fixedCount = 0;
        foreach (var body in targets)
        {
            var before = body.Shape.Diagnose(Tolerance);
            if (before.IsClean)
            {
                continue;
            }

            var healed = body.Shape.Heal(Tolerance);
            var after = healed.Diagnose(Tolerance);
            var names = Naming.Finish(healed, new string?[healed.FaceCount], $"{Id}:{ctx.Bodies.IndexOf(body)}");
            ctx.Bodies[ctx.Bodies.IndexOf(body)] = new Body(healed, names, body.Name, body.Color);
            fixedCount++;
            if (!after.IsClean)
            {
                ctx.Warn($"{body.Name} still has {after.FreeEdges} open edge(s) and {after.InvalidFaces} bad face(s) after healing.");
            }
        }

        if (fixedCount == 0)
        {
            ctx.Warn("Nothing needed healing.");
        }
    }
}
