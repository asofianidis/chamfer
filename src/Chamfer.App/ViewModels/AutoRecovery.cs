using System.Globalization;

namespace Chamfer.App.ViewModels;

/// <summary>A document found in the recovery folder.</summary>
public sealed record RecoveredDocument(string Path, string Name, DateTime Saved);

/// <summary>
/// Auto-saved copies of the open document, one file per session, kept until the
/// document is saved or the app closes normally. Files left behind after a crash
/// are offered back on the next start.
/// </summary>
public sealed class AutoRecovery
{
    private readonly string _session = Guid.NewGuid().ToString("N")[..12];
    private string? _current;

    public AutoRecovery(string? folder = null)
    {
        Folder = folder ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chamfer", "AutoRecover");
    }

    public string Folder { get; }

    /// <summary>Writes the document; the name's extension decides how it's reopened.</summary>
    public string Save(string documentName, string extension, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Directory.CreateDirectory(Folder);
        var safe = string.Concat(documentName.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = System.IO.Path.Combine(Folder, $"{safe}~{_session}.{extension}");
        if (_current is not null && _current != path)
        {
            Discard();
        }

        // Write then move so a crash mid-write never leaves a truncated copy.
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
        _current = path;
        return path;
    }

    /// <summary>Removes this session's copy (after a save or a clean exit).</summary>
    public void Discard()
    {
        if (_current is { } path)
        {
            TryDelete(path);
            _current = null;
        }
    }

    /// <summary>Copies left by other sessions, newest first.</summary>
    public IReadOnlyList<RecoveredDocument> Find()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(Folder)
            .Where(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) && !string.Equals(f, _current, StringComparison.OrdinalIgnoreCase))
            .Select(f =>
            {
                var file = System.IO.Path.GetFileNameWithoutExtension(f);
                var tilde = file.LastIndexOf('~');
                var name = (tilde > 0 ? file[..tilde] : file) + System.IO.Path.GetExtension(f);
                return new RecoveredDocument(f, name, File.GetLastWriteTime(f));
            })
            .OrderByDescending(r => r.Saved)];
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave it; it'll be offered again next time.
        }
    }

    public static string Describe(RecoveredDocument r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return $"{r.Name} — saved {r.Saved.ToString("g", CultureInfo.CurrentCulture)}";
    }
}
