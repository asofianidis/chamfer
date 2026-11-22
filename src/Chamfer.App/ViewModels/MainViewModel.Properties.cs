using Avalonia.Platform.Storage;
using Chamfer.Assembly;
using Chamfer.Document;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Custom properties of parts, and Pack and Go for assemblies.</summary>
public sealed partial class MainViewModel
{
    [RelayCommand]
    private void ShowProperties()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Custom Properties");
        panel.AddInfo(new InfoField("How to use",
            "Type Name = value and press Enter to add or change a property (Name = to delete it). " +
            "Values can use {Mass}, {Volume}, {Material}, {Configuration}, other properties and global variables in braces. " +
            "Description and PartNumber appear in BOMs and title blocks."));
        panel.AddInfo(new TextField("Name = value", string.Empty, SetProperty));
        foreach (var p in Document.Properties)
        {
            panel.AddInfo(new InfoField(p.Name, p.Value.Contains('{', StringComparison.Ordinal)
                ? $"{PropertyEvaluator.Evaluate(Document, p.Value)}   ({p.Value})"
                : p.Value));
        }

        Editor = panel;
    }

    /// <summary>Applies "Name = value" from the properties panel.</summary>
    public void SetProperty(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        if (eq <= 0)
        {
            MessageText = "Type a property as Name = value.";
            return;
        }

        Snapshot();
        PropertyEvaluator.Set(Document, text[..eq].Trim(), text[(eq + 1)..].Trim());
        ShowProperties();
    }

    private void StartPackAndGo()
    {
        if (AssemblyDoc is null)
        {
            return;
        }

        CancelEditor();
        var prefix = string.Empty;
        var suffix = string.Empty;
        var zip = false;
        var panel = FeatureEditor.Info("Pack and Go");
        panel.AddInfo(new InfoField("What it does", "Copies the assembly and every part it uses into one folder (toolbox parts stay library references)."));
        panel.AddInfo(new TextField("Prefix", string.Empty, v => prefix = v));
        panel.AddInfo(new TextField("Suffix", string.Empty, v => suffix = v));
        panel.AddInfo(new BoolField("Also make a zip file", false, v => zip = v));
        panel.AddInfo(new ButtonField("Choose folder and save", new AsyncRelayCommand(async () =>
        {
            if (StorageProvider is null)
            {
                return;
            }

            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Pack and Go to folder" });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
            {
                PackAndGoTo(folder, prefix, suffix, zip);
            }
        })));
        Editor = panel;
    }

    /// <summary>Runs Pack and Go into a folder (used by the panel and tests).</summary>
    public PackAndGoResult? PackAndGoTo(string folder, string prefix = "", string suffix = "", bool zip = false)
    {
        if (AssemblyDoc is not { } asm)
        {
            return null;
        }

        try
        {
            var name = FilePath is null ? "Assembly1" : Path.GetFileNameWithoutExtension(FilePath);
            var result = PackAndGo.Run(asm, name, folder, prefix, suffix, zip);
            MessageText = $"Pack and Go saved {result.Files.Count} files to {folder}" + (result.ZipPath is { } z ? $" and {Path.GetFileName(z)}." : ".");
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText = $"Pack and Go failed: {ex.Message}";
            return null;
        }
    }
}
