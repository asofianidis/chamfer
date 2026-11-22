using System.Globalization;
using System.Text.Json;
using Chamfer.Assembly;
using Chamfer.Document;

namespace Chamfer.Drawing;

/// <summary>One checked-in revision of a file.</summary>
public sealed record VaultRevision(string File, string Rev, string User, DateTime Time, string Comment, string Snapshot);

/// <summary>Who has a file checked out, and since when.</summary>
public sealed record VaultLock(string User, DateTime Since);

/// <summary>
/// Basic product data management on a folder: check-out locks, check-in with revision letters,
/// comments and snapshots, history and restore, and where-used for parts.
/// </summary>
public sealed class Vault
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Vault(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Store);
    }

    public string Root { get; }

    private string Store => Path.Combine(Root, ".vault");

    private string LogFile => Path.Combine(Store, "log.json");

    private string Relative(string path) => Path.GetRelativePath(Root, Path.GetFullPath(path)).Replace('\\', '/');

    private string LockFile(string path) => Path.Combine(Store, "locks", Relative(path).Replace('/', '~') + ".lock");

    /// <summary>The current lock on a file, if any.</summary>
    public VaultLock? LockOf(string path)
    {
        var f = LockFile(path);
        return File.Exists(f) ? JsonSerializer.Deserialize<VaultLock>(File.ReadAllText(f)) : null;
    }

    /// <summary>Locks a file for <paramref name="user"/>; fails when someone else has it.</summary>
    public void CheckOut(string path, string user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        if (LockOf(path) is { } held && held.User != user)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} is checked out by {held.User} since {held.Since.ToString("g", CultureInfo.InvariantCulture)}.");
        }

        var f = LockFile(path);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllText(f, JsonSerializer.Serialize(new VaultLock(user, DateTime.Now), Json));
    }

    /// <summary>Records a new revision of the file (snapshot, comment) and releases the lock.</summary>
    public VaultRevision CheckIn(string path, string user, string comment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        if (LockOf(path) is { } held && held.User != user)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} is checked out by {held.User}.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("There's no such file to check in.", path);
        }

        var rel = Relative(path);
        var log = History();
        var count = log.Count(r => r.File == rel);
        var rev = Letter(count);
        var snapshot = Path.Combine("history", rel.Replace('/', '~'), rev + Path.GetExtension(path)).Replace('\\', '/');
        var target = Path.Combine(Store, snapshot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(path, target, overwrite: true);
        var entry = new VaultRevision(rel, rev, user, DateTime.Now, comment ?? string.Empty, snapshot);
        File.WriteAllText(LogFile, JsonSerializer.Serialize(log.Append(entry).ToList(), Json));
        File.Delete(LockFile(path));
        return entry;
    }

    /// <summary>Every revision in the vault (or of one file), oldest first.</summary>
    public IReadOnlyList<VaultRevision> History(string? path = null)
    {
        var all = File.Exists(LogFile) ? JsonSerializer.Deserialize<List<VaultRevision>>(File.ReadAllText(LogFile)) ?? [] : [];
        return path is null ? all : [.. all.Where(r => r.File == Relative(path))];
    }

    /// <summary>Puts a past revision back as the working file.</summary>
    public void Restore(string path, string rev)
    {
        var entry = History(path).LastOrDefault(r => r.Rev == rev) ?? throw new InvalidOperationException($"There's no revision {rev}.");
        File.Copy(Path.Combine(Store, entry.Snapshot), path, overwrite: true);
    }

    /// <summary>Assemblies and drawings in the vault that use a file (directly).</summary>
    public IReadOnlyList<string> WhereUsed(string path)
    {
        var target = Path.GetFullPath(path);
        var users = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(Store, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ext = Path.GetExtension(file).TrimStart('.');
            try
            {
                var dir = Path.GetDirectoryName(file)!;
                var uses = ext.Equals(AssemblyDocument.FileExtension, StringComparison.OrdinalIgnoreCase)
                    ? AssemblyDocument.Load(File.ReadAllText(file), dir).Components.Any(c => Same(c.Path, dir, target))
                    : ext.Equals(DrawingDocument.FileExtension, StringComparison.OrdinalIgnoreCase) && Same(DrawingDocument.Load(File.ReadAllText(file), dir).ModelPath, dir, target);
                if (uses)
                {
                    users.Add(Relative(file));
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException)
            {
                // Not a readable Chamfer file: it can't use anything.
            }
        }

        return users;
    }

    private static bool Same(string reference, string dir, string target) =>
        !reference.StartsWith("toolbox:", StringComparison.Ordinal) && reference.Length > 0 &&
        string.Equals(Path.GetFullPath(Path.IsPathRooted(reference) ? reference : Path.Combine(dir, reference)), target, StringComparison.OrdinalIgnoreCase);

    /// <summary>A, B, ... Z, AA, AB, ...</summary>
    private static string Letter(int n)
    {
        var s = string.Empty;
        do
        {
            s = (char)('A' + (n % 26)) + s;
            n = (n / 26) - 1;
        }
        while (n >= 0);
        return s;
    }

    /// <summary>The revision letter a file is at (its latest check-in), if any.</summary>
    public string? RevisionOf(string path)
    {
        var h = History(path);
        return h.Count > 0 ? h[^1].Rev : null;
    }

    /// <summary>Puts the current revision letter in a part's Revision property (for title blocks and BOMs).</summary>
    public static void StampRevision(PartDocument part, string rev)
    {
        ArgumentNullException.ThrowIfNull(part);
        PropertyEvaluator.Set(part, "Revision", rev);
    }
}
