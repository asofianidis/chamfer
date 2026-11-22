using System.Globalization;
using Chamfer.Drawing;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Basic PDM for the open file: check-out, check-in with revisions, history and where-used.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The name recorded on check-outs and check-ins.</summary>
    public string VaultUser { get; set; } = Environment.UserName;

    [RelayCommand]
    private void ShowVault()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Vault");
        if (FilePath is not { } path || !File.Exists(path))
        {
            panel.AddInfo(new InfoField("Vault", "Save the file first; its folder is the vault."));
            Editor = panel;
            return;
        }

        var vault = new Vault(Path.GetDirectoryName(path)!);
        var inv = CultureInfo.InvariantCulture;
        panel.AddInfo(new InfoField("File", $"{Path.GetFileName(path)}, revision {vault.RevisionOf(path) ?? "(none yet)"}"));
        var held = vault.LockOf(path);
        panel.AddInfo(new InfoField("Status", held is null ? "Not checked out" : $"Checked out by {held.User} since {held.Since.ToString("g", inv)}"));
        if (held is null || held.User == VaultUser)
        {
            if (held is null)
            {
                panel.AddInfo(new ButtonField("Check out", new RelayCommand(() => RunVault(() => vault.CheckOut(path, VaultUser), () => "Checked out."))));
            }

            panel.AddInfo(new TextField("Check in with comment", string.Empty, comment =>
            {
                string? rev = null;
                RunVault(() => rev = vault.CheckIn(path, VaultUser, comment).Rev, () => $"Checked in revision {rev}.");
            }));
        }

        foreach (var r in vault.History(path).Reverse())
        {
            panel.AddInfo(new InfoField($"Rev {r.Rev}", $"{r.Time.ToString("g", inv)} · {r.User} · {r.Comment}"));
            panel.AddInfo(new ButtonField($"Restore {r.Rev}", new RelayCommand(() => RunVault(() =>
            {
                vault.Restore(path, r.Rev);
                LoadFile(path, File.ReadAllText(path));
                FilePath = path;
            }, () => $"Restored revision {r.Rev}."))));
        }

        var used = vault.WhereUsed(path);
        panel.AddInfo(new InfoField("Where used", used.Count == 0 ? "Not used by any assembly or drawing here." : string.Join("\n", used)));
        Editor = panel;
    }

    private void RunVault(Action action, Func<string> done)
    {
        string message;
        try
        {
            action();
            message = done();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            message = ex.Message;
        }

        // Refreshing the panel rebuilds the status line, so the message goes after it.
        ShowVault();
        MessageText = message;
    }
}
