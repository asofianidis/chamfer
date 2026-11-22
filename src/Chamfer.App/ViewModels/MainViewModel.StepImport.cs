using Avalonia.Platform.Storage;
using Chamfer.Assembly;

namespace Chamfer.App.ViewModels;

/// <summary>Importing STEP assemblies as Chamfer parts and an assembly.</summary>
public sealed partial class MainViewModel
{
    private async Task ImportStepAssemblyAsync()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Import STEP Assembly", FileTypeFilter = [StepFileType] });
        if (files is not [var file] || file.TryGetLocalPath() is not { } step)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for the imported parts" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } folder)
        {
            ImportStepAssembly(step, folder);
        }
    }

    /// <summary>Converts a STEP assembly into part files and an assembly in a folder, and opens it.</summary>
    public void ImportStepAssembly(string stepPath, string folder)
    {
        ArgumentNullException.ThrowIfNull(stepPath);
        try
        {
            var (_, path) = AssemblyImport.FromStep(stepPath, folder);
            LoadFile(path, File.ReadAllText(path));
            FilePath = path;
            MessageText = $"Imported {Path.GetFileName(stepPath)}: {AssemblyDoc?.Components.Count ?? 0} components.";
        }
        catch (Exception ex) when (ex is IOException or Chamfer.Kernel.Occt.KernelException or UnauthorizedAccessException)
        {
            MessageText = $"Couldn't import {Path.GetFileName(stepPath)}: {ex.Message}";
        }
    }
}
