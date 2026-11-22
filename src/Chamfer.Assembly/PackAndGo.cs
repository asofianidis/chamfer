using System.IO.Compression;
using Chamfer.Document;

namespace Chamfer.Assembly;

/// <summary>What Pack and Go wrote.</summary>
public sealed record PackAndGoResult(string AssemblyPath, IReadOnlyList<string> Files, string? ZipPath);

/// <summary>
/// Copies an assembly and every part it references into one folder, optionally renaming
/// them with a prefix and suffix, and rewrites the component paths to the copies.
/// Toolbox parts stay as library references.
/// </summary>
public static class PackAndGo
{
    public static PackAndGoResult Run(AssemblyDocument asm, string assemblyName, string targetDirectory,
        string prefix = "", string suffix = "", bool zip = false)
    {
        ArgumentNullException.ThrowIfNull(asm);
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        Directory.CreateDirectory(targetDirectory);
        prefix ??= string.Empty;
        suffix ??= string.Empty;

        var files = new List<string>();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Unique(string baseName, string extension)
        {
            var name = $"{prefix}{baseName}{suffix}.{extension}";
            for (var n = 2; !used.Add(name); n++)
            {
                name = $"{prefix}{baseName}{suffix} ({n}).{extension}";
            }

            return name;
        }

        foreach (var path in asm.Components.Select(c => c.Path).Distinct(StringComparer.Ordinal))
        {
            if (Toolbox.IsToolboxPath(path))
            {
                continue;
            }

            var newName = Unique(Path.GetFileNameWithoutExtension(path), PartDocument.FileExtension);
            var target = Path.Combine(targetDirectory, newName);
            var source = asm.Resolve(path);
            if (File.Exists(source))
            {
                File.Copy(source, target, overwrite: true);
            }
            else
            {
                // A part that only exists in memory (new or registered): save it.
                File.WriteAllText(target, asm.PartOf(asm.Components.First(c => c.Path == path)).Save());
            }

            map[path] = newName;
            files.Add(target);
        }

        var copy = AssemblyDocument.Load(asm.Save(), targetDirectory);
        foreach (var c in copy.Components)
        {
            if (map.TryGetValue(c.Path, out var newPath))
            {
                c.Path = newPath;
            }
        }

        var assemblyPath = Path.Combine(targetDirectory, Unique(assemblyName, AssemblyDocument.FileExtension));
        File.WriteAllText(assemblyPath, copy.Save());
        files.Insert(0, assemblyPath);

        string? zipPath = null;
        if (zip)
        {
            zipPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory)) + ".zip";
            File.Delete(zipPath);
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var f in files)
            {
                archive.CreateEntryFromFile(f, Path.GetFileName(f));
            }
        }

        return new PackAndGoResult(assemblyPath, files, zipPath);
    }
}
