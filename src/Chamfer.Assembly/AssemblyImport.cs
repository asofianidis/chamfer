using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Kernel.Occt;

namespace Chamfer.Assembly;

/// <summary>Turns a STEP assembly into Chamfer parts and an assembly that places them.</summary>
public static class AssemblyImport
{
    /// <summary>
    /// Writes one part file per distinct STEP part (its body, colour and name as part number) into
    /// <paramref name="folder"/>, and an assembly that places them; returns the assembly and its path.
    /// </summary>
    public static (AssemblyDocument Assembly, string Path) FromStep(string stepPath, string folder)
    {
        ArgumentNullException.ThrowIfNull(stepPath);
        ArgumentNullException.ThrowIfNull(folder);
        Directory.CreateDirectory(folder);
        var (parts, instances) = Modeling.ReadStepAssembly(stepPath);
        try
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            foreach (var (shape, name, color) in parts)
            {
                var part = new PartDocument();
                part.Add(new ImportFeature { SourceName = Path.GetFileName(stepPath), Brep = shape.ToBrep() });
                if (color is { } c)
                {
                    part.Add(new AppearanceFeature { Color = c });
                }

                PropertyEvaluator.Set(part, PropertyEvaluator.PartNumber, name);
                var file = Unique(Safe(name), used) + "." + PartDocument.FileExtension;
                File.WriteAllText(System.IO.Path.Combine(folder, file), part.Save());
                files.Add(file);
            }

            var asm = new AssemblyDocument { Directory = folder };
            foreach (var (part, placement, name) in instances)
            {
                var component = asm.AddComponent(files[part], placement.IsRigid ? placement : Geometry.Transform3.Identity);
                component.Name = name;
            }

            // Imported placements are final: fix everything so nothing drifts.
            foreach (var c in asm.Components)
            {
                c.Fixed = true;
            }

            var path = System.IO.Path.Combine(folder, Unique(Safe(System.IO.Path.GetFileNameWithoutExtension(stepPath)), used) + "." + AssemblyDocument.FileExtension);
            File.WriteAllText(path, asm.Save());
            return (asm, path);
        }
        finally
        {
            foreach (var (shape, _, _) in parts)
            {
                shape.Dispose();
            }
        }
    }

    private static string Safe(string name)
    {
        var s = string.Concat(name.Trim().Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return s.Length == 0 ? "Part" : s;
    }

    private static string Unique(string name, HashSet<string> used)
    {
        var candidate = name;
        for (var n = 2; !used.Add(candidate); n++)
        {
            candidate = $"{name} ({n})";
        }

        return candidate;
    }
}
