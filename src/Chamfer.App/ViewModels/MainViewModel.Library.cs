using Chamfer.App.Part;
using Chamfer.Document;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Part templates and the design library: folders of reusable parts.</summary>
public sealed partial class MainViewModel
{
    private static readonly string AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chamfer");

    /// <summary>Where part templates are kept (tests point this elsewhere).</summary>
    public string TemplateFolder { get; set; } = Path.Combine(AppDataFolder, "Templates");

    /// <summary>Folders the design library lists parts from.</summary>
    public List<string> LibraryFolders { get; } = [Path.Combine(AppDataFolder, "Library")];

    /// <summary>Saves the current part as a template that new parts can start from.</summary>
    public string? SaveAsTemplate(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (AssemblyDoc is not null || DrawingDoc is not null)
        {
            MessageText = "Templates are made from parts.";
            return null;
        }

        Directory.CreateDirectory(TemplateFolder);
        var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var path = Path.Combine(TemplateFolder, safe + "." + PartDocument.FileExtension);
        File.WriteAllText(path, Document.Save());
        MessageText = $"Saved template '{safe}'.";
        return path;
    }

    /// <summary>Starts a new, unsaved part from a template.</summary>
    public void NewFromTemplate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        LoadFile(path, File.ReadAllText(path));
        FilePath = null;
        MessageText = $"New part from template '{Path.GetFileNameWithoutExtension(path)}'.";
    }

    [RelayCommand]
    private void ShowTemplates()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Templates");
        var templates = Directory.Exists(TemplateFolder)
            ? Directory.EnumerateFiles(TemplateFolder, "*." + PartDocument.FileExtension).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        foreach (var t in templates)
        {
            panel.AddInfo(new ButtonField($"New part from {Path.GetFileNameWithoutExtension(t)}", new RelayCommand(() => NewFromTemplate(t))));
        }

        if (templates.Count == 0)
        {
            panel.AddInfo(new InfoField("Templates", "No templates yet."));
        }

        if (AssemblyDoc is null && DrawingDoc is null)
        {
            panel.AddInfo(new TextField("Save this part as template", string.Empty, name =>
            {
                SaveAsTemplate(name);
                ShowTemplates();
            }));
        }

        panel.AddInfo(new InfoField("Folder", TemplateFolder));
        Editor = panel;
    }

    /// <summary>Library features in the design library folders.</summary>
    public IReadOnlyList<string> LibraryFeatureFiles() =>
        [.. LibraryFolders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*." + LibraryFeatures.FileExtension, SearchOption.AllDirectories))
            .Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Saves the features selected in the tree (with their sketches) as a library feature.</summary>
    public string? SaveLibraryFeature(string name)
    {
        var features = Selection.Where(s => s.Kind == SelectionKind.Feature && s.FeatureId is not null)
            .Select(s => Document.Find(s.FeatureId!)).OfType<Chamfer.Document.Feature>().ToList();
        try
        {
            var json = LibraryFeatures.Save(Document, features);
            var folder = LibraryFolders[0];
            Directory.CreateDirectory(folder);
            var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(folder, safe + "." + LibraryFeatures.FileExtension);
            File.WriteAllText(path, json);
            MessageText = $"Saved library feature '{safe}'.";
            return path;
        }
        catch (InvalidOperationException ex)
        {
            MessageText = ex.Message;
            return null;
        }
    }

    /// <summary>Inserts a library feature onto the selected face or plane.</summary>
    public void InsertLibraryFeature(string path)
    {
        if (SelectedPlane() is not { } target)
        {
            MessageText = "Select the face or plane to put the library feature on.";
            return;
        }

        Snapshot();
        try
        {
            var added = LibraryFeatures.Insert(Document, File.ReadAllText(path), target);
            RebuildAndRefresh();
            MessageText = $"Inserted {Path.GetFileNameWithoutExtension(path)} ({added.Count} features).";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            MessageText = $"Couldn't insert it: {ex.Message}";
        }
    }

    /// <summary>Parts in the design library folders.</summary>
    public IReadOnlyList<string> LibraryParts() =>
        [.. LibraryFolders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*." + PartDocument.FileExtension, SearchOption.AllDirectories))
            .Order(StringComparer.OrdinalIgnoreCase)];

    [RelayCommand]
    private void ShowDesignLibrary()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Design Library");
        var parts = LibraryParts();
        foreach (var path in parts)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (AssemblyDoc is not null)
            {
                panel.AddInfo(new ButtonField($"Insert {name}", new RelayCommand(() =>
                {
                    try
                    {
                        InsertPart(path);
                    }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or NotSupportedException)
                    {
                        MessageText = $"Couldn't insert {name}: {ex.Message}";
                    }
                })));
            }
            else
            {
                panel.AddInfo(new ButtonField($"Open {name}", new RelayCommand(() => LoadFile(path, File.ReadAllText(path)))));
            }
        }

        if (parts.Count == 0)
        {
            panel.AddInfo(new InfoField("Library", "No parts yet. Save parts into the library folder to reuse them."));
        }

        // Library features: insert onto the selected face or plane (part mode).
        if (AssemblyDoc is null && DrawingDoc is null)
        {
            foreach (var path in LibraryFeatureFiles())
            {
                var name = Path.GetFileNameWithoutExtension(path);
                panel.AddInfo(new ButtonField($"Insert feature {name}", new RelayCommand(() => InsertLibraryFeature(path))));
            }

            panel.AddInfo(new TextField("Save selected features as library feature", string.Empty, name =>
            {
                SaveLibraryFeature(name);
                ShowDesignLibrary();
            }));
        }

        panel.AddInfo(new InfoField("Folders", string.Join("\n", LibraryFolders)));
        if (AssemblyDoc is null && DrawingDoc is null && Document.Bodies.Count > 0)
        {
            panel.AddInfo(new TextField("Add this part to the library as", string.Empty, name =>
            {
                var folder = LibraryFolders[0];
                Directory.CreateDirectory(folder);
                var safe = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                File.WriteAllText(Path.Combine(folder, safe + "." + PartDocument.FileExtension), Document.Save());
                ShowDesignLibrary();
            }));
        }

        Editor = panel;
    }
}
