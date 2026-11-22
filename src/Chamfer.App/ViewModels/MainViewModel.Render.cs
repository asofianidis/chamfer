using System.Globalization;
using Avalonia.Platform.Storage;
using Chamfer.Document;
using Chamfer.Viewport;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Photorealistic rendering of the current view.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The view to render: a copy of the camera and the size in pixels (set by the window).</summary>
    public Func<(Camera Camera, int Width, int Height)>? ViewProvider { get; set; }

    /// <summary>Shows a finished render in the viewport (set by the window).</summary>
    public Action<uint[], int, int>? ShowImageAction { get; set; }

    /// <summary>The last finished render.</summary>
    public (uint[] Pixels, int Width, int Height)? LastRender { get; private set; }

    public RenderSettings RenderOptions { get; } = new() { Samples = 64 };

    /// <summary>The render scene (lighting and sky) from <see cref="RenderSettings.Scenes"/>.</summary>
    private string _renderScene = "Daylight";

    /// <summary>Glossy metal for metals, matte for wood, clear-coated plastic otherwise.</summary>
    public static Finish FinishOf(Material m)
    {
        ArgumentNullException.ThrowIfNull(m);
        var name = m.Name.ToUpperInvariant();
        return name.Contains("STEEL", StringComparison.Ordinal) || name.Contains("ALLOY", StringComparison.Ordinal) ||
               name.Contains("BRASS", StringComparison.Ordinal) || name.Contains("TI-", StringComparison.Ordinal) ||
               name.Contains("ALUMIN", StringComparison.Ordinal) || name.Contains("COPPER", StringComparison.Ordinal)
            ? Finish.Metal
            : name.Contains("OAK", StringComparison.Ordinal) || name.Contains("WOOD", StringComparison.Ordinal) ? Finish.Matte : Finish.Plastic;
    }

    [RelayCommand]
    private void StartRender()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Render");
        var qualities = new[] { 16, 64, 256 };
        panel.AddInfo(new ChoiceField("Quality", ["Draft (16 samples)", "Good (64 samples)", "Best (256 samples)"], 1, i => RenderOptions.Samples = qualities[i]));
        panel.AddInfo(new ChoiceField("Scene", [.. RenderSettings.Scenes], Math.Max(0, RenderSettings.Scenes.ToList().IndexOf(_renderScene)), i =>
        {
            _renderScene = RenderSettings.Scenes[i];
            RenderOptions.ApplyScene(_renderScene);
        }));
        panel.AddInfo(new BoolField("Floor with shadows", RenderOptions.Floor, v => RenderOptions.Floor = v));
        panel.AddInfo(new NumberField("Exposure", string.Empty, () => RenderOptions.Exposure, v => RenderOptions.Exposure = v, 0.05));
        panel.AddInfo(new ButtonField("Render", new AsyncRelayCommand(RenderPhotoAsync)));
        panel.AddInfo(new ButtonField("Save PNG...", new AsyncRelayCommand(SaveRenderAsync)));
        panel.AddInfo(new InfoField("Finish", "Metals render glossy, wood matte, other materials as coated plastic. Click the view to go back."));
        Editor = panel;
    }

    /// <summary>Path-traces the current view and shows it.</summary>
    public async Task RenderPhotoAsync()
    {
        Camera camera;
        int width, height;
        if (ViewProvider?.Invoke() is { } view && view.Width > 1 && view.Height > 1)
        {
            (camera, width, height) = view;
        }
        else
        {
            (camera, width, height) = (new Camera(), 640, 480);
            camera.SetView(StandardView.Isometric);
            camera.Fit(BuildScene().Bounds, width, height);
        }

        // Render the model, not the selection highlight.
        var selection = Selection.ToList();
        SetSelection([]);
        var scene = BuildScene();
        SetSelection(selection);
        var settings = new RenderSettings
        {
            Samples = RenderOptions.Samples,
            Floor = RenderOptions.Floor,
            Exposure = RenderOptions.Exposure,
        };
        settings.ApplyScene(_renderScene);
        foreach (var (k, v) in MeshFinishes(scene))
        {
            settings.Finishes[k] = v;
        }

        IsSolving = true;
        MessageText = "Rendering...";
        try
        {
            var progress = new Progress<double>(p => MessageText = string.Create(CultureInfo.InvariantCulture, $"Rendering... {p:P0}"));
            var pixels = await Task.Run(() => new PathTracer().Render(scene, camera, width, height, settings, progress));
            LastRender = (pixels, width, height);
            ShowImageAction?.Invoke(pixels, width, height);
            MessageText = string.Create(CultureInfo.InvariantCulture, $"Rendered {width} x {height} at {settings.Samples} samples per pixel.");
        }
        finally
        {
            IsSolving = false;
        }
    }

    /// <summary>The finish of each mesh in the scene, from the materials of the parts.</summary>
    private Dictionary<int, Finish> MeshFinishes(Scene scene)
    {
        var map = new Dictionary<int, Finish>();
        if (AssemblyDoc is { } asm)
        {
            // The assembly scene lists each visible component's bodies in order.
            var index = 0;
            foreach (var c in asm.Components.Where(c => !c.Suppressed && !c.Hidden))
            {
                var finish = FinishOf(asm.PartOf(c).Material);
                for (var b = 0; b < asm.BodiesOf(c).Count && index < scene.Meshes.Count; b++)
                {
                    map[index++] = finish;
                }
            }
        }
        else
        {
            var finish = FinishOf(Document.Material);
            for (var i = 0; i < scene.Meshes.Count; i++)
            {
                map[i] = finish;
            }
        }

        return map;
    }

    private async Task SaveRenderAsync()
    {
        if (LastRender is null)
        {
            await RenderPhotoAsync();
        }

        if (LastRender is null || StorageProvider is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Render",
            SuggestedFileName = (FilePath is { } p ? Path.GetFileNameWithoutExtension(p) : "render") + ".png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            SaveRenderTo(path);
        }
    }

    /// <summary>Writes the last render as a PNG.</summary>
    public void SaveRenderTo(string path)
    {
        if (LastRender is { } r)
        {
            PngWriter.Write(path, r.Pixels, r.Width, r.Height);
            MessageText = $"Saved {Path.GetFileName(path)}.";
        }
    }
}
