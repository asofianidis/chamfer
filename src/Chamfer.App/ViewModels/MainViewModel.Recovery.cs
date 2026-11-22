using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Auto-save and document recovery.</summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _autoSaveTimer;

    /// <summary>Where auto-saved copies go (tests point this at a temporary folder).</summary>
    public AutoRecovery Recovery { get; set; } = new();

    /// <summary>Starts saving a recovery copy of unsaved work every <paramref name="interval"/>.</summary>
    public void StartAutoSave(TimeSpan interval)
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer = new DispatcherTimer { Interval = interval };
        _autoSaveTimer.Tick += (_, _) => AutoSaveNow();
        _autoSaveTimer.Start();
    }

    /// <summary>Writes a recovery copy if there are unsaved changes; returns its path.</summary>
    public string? AutoSaveNow()
    {
        if (!_dirty)
        {
            Recovery.Discard();
            return null;
        }

        try
        {
            var (ext, json) = DrawingDoc is { } d ? (Chamfer.Drawing.DrawingDocument.FileExtension, d.Save())
                : AssemblyDoc is { } a ? (Chamfer.Assembly.AssemblyDocument.FileExtension, a.Save())
                : (Chamfer.Document.PartDocument.FileExtension, Document.Save());
            var name = FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : DrawingDoc is not null ? "Drawing1" : AssemblyDoc is not null ? "Assembly1" : "Part1";
            return Recovery.Save(name, ext, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Called after a successful save or a clean exit.</summary>
    public void DiscardRecovery() => Recovery.Discard();

    /// <summary>Shows documents left by a previous session that didn't close normally.</summary>
    public void ShowRecoveryPanel()
    {
        var found = Recovery.Find();
        if (found.Count == 0)
        {
            return;
        }

        var panel = FeatureEditor.Info("Document Recovery");
        panel.AddInfo(new InfoField("Unsaved work", "Chamfer closed unexpectedly last time. These copies of unsaved documents were kept."));
        foreach (var r in found)
        {
            panel.AddInfo(new InfoField(r.Name, AutoRecovery.Describe(r)));
            panel.AddInfo(new ButtonField($"Open {r.Name}", new RelayCommand(() => OpenRecovered(r))));
            panel.AddInfo(new ButtonField($"Discard {r.Name}", new RelayCommand(() =>
            {
                AutoRecovery.TryDelete(r.Path);
                Editor = null;
                ShowRecoveryPanel();
            })));
        }

        Editor = panel;
    }

    /// <summary>Opens a recovered copy as a new, unsaved document and removes the copy.</summary>
    public void OpenRecovered(RecoveredDocument r)
    {
        ArgumentNullException.ThrowIfNull(r);
        string json;
        try
        {
            json = File.ReadAllText(r.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText = $"Couldn't read the recovered copy: {ex.Message}";
            return;
        }

        Editor = null;
        LoadFile(r.Name, json);
        FilePath = null;
        _dirty = true;
        AutoRecovery.TryDelete(r.Path);
        AutoSaveNow();
        MessageText = $"Recovered {r.Name}. Save it to keep it.";
    }
}
