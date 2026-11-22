using Chamfer.Document;
using Chamfer.Kernel.Occt;

namespace Chamfer.Assembly;

/// <summary>
/// Subassemblies: an assembly file used as a component of another. It behaves as one rigid
/// component whose bodies are its own components' bodies, placed as they are in it.
/// </summary>
public static class SubAssemblies
{
    /// <summary>Whether a component path names an assembly rather than a part.</summary>
    public static bool IsAssemblyPath(string path) =>
        path?.EndsWith("." + AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// A read-only part standing in for a subassembly: one body per body of each of its
    /// components, placed in the subassembly's coordinates, with face names prefixed by the
    /// component id so mates can refer to them. Its density averages the parts', so mass is right.
    /// </summary>
    internal static PartDocument StandIn(AssemblyDocument sub)
    {
        sub.Solve();
        var part = new PartDocument();
        part.Add(new SubAssemblyBodiesFeature(sub));
        part.Rebuild();

        double volume = 0, mass = 0;
        foreach (var c in sub.Components.Where(c => !c.Suppressed))
        {
            var p = sub.PartOf(c);
            var m = p.MassProperties();
            volume += m.Volume;
            mass += m.Mass;
        }

        part.Material = new Material("Mixed (subassembly)", volume > 0 ? mass / volume * 1e9 : Material.Steel.Density);
        return part;
    }
}

/// <summary>Builds a subassembly's placed bodies into its stand-in part (never saved).</summary>
internal sealed class SubAssemblyBodiesFeature(AssemblyDocument sub) : Feature
{
    public override string DefaultName => "Components";

    public override string CacheKey => $"subassembly:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(sub)}";

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        foreach (var c in sub.Components.Where(c => !c.Suppressed))
        {
            foreach (var body in sub.BodiesOf(c))
            {
                using var op = Modeling.Transform(body.Shape, c.Transform);
                var names = body.FaceNames.Select(n => $"{c.Id}/{n}").ToArray();
                ctx.Bodies.Add(new Body(op.Result, names, c.Name, body.Color));
            }
        }
    }
}
