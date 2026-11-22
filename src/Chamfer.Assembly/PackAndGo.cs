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

        // Copies of everything reached so far, by source (full path, or path within its assembly when in memory).
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Writes an assembly (and, first, everything it uses) and returns its new file name.
        string Pack(AssemblyDocument a, string name)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var path in a.Components.Select(c => c.Path).Distinct(StringComparer.Ordinal))
            {
                if (Toolbox.IsToolboxPath(path))
                {
                    continue;
                }

                var source = a.Resolve(path);
                var key = File.Exists(source) ? Path.GetFullPath(source) : $"{a.GetHashCode()}:{path}";
                if (!written.TryGetValue(key, out var newName))
                {
                    var component = a.Components.First(c => c.Path == path);
                    if (SubAssemblies.IsAssemblyPath(path))
                    {
                        newName = Pack(a.SubAssemblyOf(component)!, Path.GetFileNameWithoutExtension(path));
                    }
                    else
                    {
                        newName = Unique(Path.GetFileNameWithoutExtension(path), PartDocument.FileExtension);
                        var target = Path.Combine(targetDirectory, newName);
                        if (File.Exists(source))
                        {
                            File.Copy(source, target, overwrite: true);
                        }
                        else
                        {
                            // A part that only exists in memory (new or registered): save it.
                            File.WriteAllText(target, a.PartOf(component).Save());
                        }

                        files.Add(target);
                    }

                    written[key] = newName;
                }

                map[path] = newName;
            }

            var copy = AssemblyDocument.Load(a.Save(), targetDirectory);
            foreach (var c in copy.Components)
            {
                if (map.TryGetValue(c.Path, out var newPath))
                {
                    c.Path = newPath;
                }
            }

            var fileName = Unique(name, AssemblyDocument.FileExtension);
            var assemblyFile = Path.Combine(targetDirectory, fileName);
            File.WriteAllText(assemblyFile, copy.Save());
            files.Add(assemblyFile);
            return fileName;
        }

        var assemblyPath = Path.Combine(targetDirectory, Pack(asm, assemblyName));

        // The top assembly first.
        files.Remove(assemblyPath);
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
