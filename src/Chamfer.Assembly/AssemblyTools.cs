using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Assembly;

/// <summary>Two components whose solids overlap.</summary>
public sealed record Interference(Component A, Component B, double Volume);

/// <summary>A bill-of-materials line.</summary>
public sealed record BomItem(int Item, string PartName, string? Configuration, int Quantity, string Material, double MassEach);

/// <summary>Interference detection, bills of materials and exploded views.</summary>
public static class AssemblyTools
{
    /// <summary>Finds every pair of components whose bodies overlap by more than <paramref name="minVolume"/> mm³.</summary>
    public static IReadOnlyList<Interference> FindInterferences(AssemblyDocument asm, double minVolume = 1e-6)
    {
        ArgumentNullException.ThrowIfNull(asm);
        var comps = asm.Components.Where(c => !c.Suppressed).ToList();
        var placed = comps.ToDictionary(c => c.Id, c => asm.PlacedBodies(c).Select(p => p.Placed).ToList());
        var found = new List<Interference>();
        for (var i = 0; i < comps.Count; i++)
        {
            for (var j = i + 1; j < comps.Count; j++)
            {
                double volume = 0;
                foreach (var a in placed[comps[i].Id])
                {
                    foreach (var b in placed[comps[j].Id])
                    {
                        if (!Overlap(a.Bounds, b.Bounds))
                        {
                            continue;
                        }

                        try
                        {
                            using var common = Modeling.Boolean(a, b, BooleanKind.Common);
                            using var result = common.Result;
                            volume += result.MassProperties.Volume;
                        }
                        catch (KernelException)
                        {
                        }
                    }
                }

                if (volume > minVolume)
                {
                    found.Add(new Interference(comps[i], comps[j], volume));
                }
            }
        }

        return found;
    }

    /// <summary>One line per distinct part and configuration, in insertion order.</summary>
    public static IReadOnlyList<BomItem> BillOfMaterials(AssemblyDocument asm)
    {
        ArgumentNullException.ThrowIfNull(asm);
        var lines = new List<BomItem>();
        foreach (var group in asm.Components.Where(c => !c.Suppressed).GroupBy(c => (c.Path, c.Configuration)))
        {
            var first = group.First();
            var part = asm.PartOf(first);
            lines.Add(new BomItem(lines.Count + 1, Path.GetFileNameWithoutExtension(first.Path), group.Key.Configuration,
                group.Count(), part.Material.Name, part.MassProperties().Mass));
        }

        return lines;
    }

    /// <summary>
    /// Spreads components away from the assembly's centre: each one moves
    /// along the line from the centre to its own centre, by <paramref name="factor"/>
    /// times that distance (fixed components stay).
    /// </summary>
    public static void AutoExplode(AssemblyDocument asm, double factor = 1)
    {
        ArgumentNullException.ThrowIfNull(asm);
        var centers = asm.Components.Where(c => !c.Suppressed).ToDictionary(c => c.Id, c => Center(asm, c));
        if (centers.Count == 0)
        {
            return;
        }

        var all = centers.Values.Aggregate(Box3.Empty, (b, p) => b.Include(p)).Center;
        foreach (var c in asm.Components.Where(c => !c.Suppressed))
        {
            var d = centers[c.Id] - all;
            c.ExplodeOffset = c.Fixed || d.LengthSquared < 1e-12 ? Vec3.Zero : d * factor;
        }
    }

    public static void Collapse(AssemblyDocument asm)
    {
        ArgumentNullException.ThrowIfNull(asm);
        foreach (var c in asm.Components)
        {
            c.ExplodeOffset = Vec3.Zero;
        }
    }

    /// <summary>Centre of a component's bounding box in assembly space.</summary>
    public static Vec3 Center(AssemblyDocument asm, Component c)
    {
        ArgumentNullException.ThrowIfNull(asm);
        ArgumentNullException.ThrowIfNull(c);
        var box = asm.PlacedBodies(c).Aggregate(Box3.Empty, (b, p) => b.Union(p.Placed.Bounds));
        return box.IsEmpty ? new Vec3(c.Transform.Tx, c.Transform.Ty, c.Transform.Tz) : box.Center;
    }

    /// <summary>Writes every visible component, placed, to a STEP, IGES, STL or BRep file.</summary>
    public static void Export(AssemblyDocument asm, string path)
    {
        ArgumentNullException.ThrowIfNull(asm);
        ArgumentNullException.ThrowIfNull(path);
        var shapes = asm.Components.Where(c => !c.Suppressed && !c.Hidden).SelectMany(c => asm.PlacedBodies(c).Select(p => p.Placed)).ToList();
        if (shapes.Count == 0)
        {
            throw new InvalidOperationException("There is nothing to export.");
        }

        using var all = Modeling.Compound(shapes);
        switch (Path.GetExtension(path).ToUpperInvariant())
        {
            case ".STEP" or ".STP": all.WriteStep(path); break;
            case ".IGES" or ".IGS": all.WriteIges(path); break;
            case ".STL": all.WriteStl(path, 0.02); break;
            case ".BREP" or ".BRP": all.WriteBrep(path); break;
            default: throw new NotSupportedException($"Can't export {Path.GetExtension(path)} files.");
        }
    }

    private static bool Overlap(Box3 a, Box3 b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;
}
