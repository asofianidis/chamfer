using Avalonia.Platform.Storage;
using Chamfer.Api;
using Chamfer.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>A multi-line code editor field.</summary>
public sealed partial class CodeField(string label, string text) : PropertyField(label)
{
    [ObservableProperty]
    public partial string Text { get; set; } = text;
}

/// <summary>Macros (C# scripts against the API), macro recording, and plugins.</summary>
public sealed partial class MainViewModel : IPluginHost
{
    private static readonly FilePickerFileType MacroFileType = new("C# macro") { Patterns = ["*.csx", "*.cs"] };

    private const string ExampleMacro = """
        // C# macro. Api is the scripting API for the open part; Print writes to the output.
        var s = Api.NewSketch("front").Rectangle(0, 0, 60, 40).Circle(30, 20, 8).Done();
        var block = Api.Extrude(s, 10);
        Api.FilletEdges(e => e.Curve == CurveKind.Line && Math.Abs(e.CenterOrDirection.Z) > 0.99, 4);
        Print($"Volume: {Api.Volume:0.##} mm³, mass {Api.Mass * 1000:0.##} g");
        """;

    private string _macroCode = ExampleMacro;
    private string? _recordingFrom;
    private readonly List<CommandItem> _pluginCommands = [];

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    /// <summary>Loads plugins from the given folders and adds their commands under a Plugins tab.</summary>
    public void LoadPlugins(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        foreach (var folder in folders)
        {
            foreach (var plugin in PluginLoader.Load(folder, e => MessageText = $"Plugin error: {e}"))
            {
                try
                {
                    plugin.Initialize(this);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    MessageText = $"Plugin {plugin.Name} failed to start: {ex.Message}";
                }
            }
        }
    }

    void IPluginHost.AddCommand(string label, string tooltip, Action<ChamferApi> run)
    {
        var command = new RelayCommand(() =>
        {
            if (AssemblyDoc is not null || DrawingDoc is not null)
            {
                MessageText = "Plugin commands work on parts.";
                return;
            }

            Snapshot();
            try
            {
                run(new ChamferApi(Document));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MessageText = $"{label}: {ex.Message}";
            }

            RebuildAndRefresh();
        });
        _pluginCommands.Add(new CommandItem(label, tooltip, command, "✦", "Plugins"));
        FeatureCommands = [.. FeatureCommands, _pluginCommands[^1]];
        if (!CommandTabs.Contains("Plugins"))
        {
            CommandTabs = [.. CommandTabs, "Plugins"];
        }
    }

    [RelayCommand]
    private void ShowMacros()
    {
        if (AssemblyDoc is not null || DrawingDoc is not null)
        {
            MessageText = "Macros work on parts.";
            return;
        }

        CancelEditor();
        var panel = FeatureEditor.Info("Macro");
        var code = new CodeField("C# code", _macroCode);
        code.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CodeField.Text))
            {
                _macroCode = code.Text;
            }
        };
        var output = new InfoField("Output", string.Empty);
        panel.AddInfo(code);
        panel.AddInfo(new ButtonField("Run", new AsyncRelayCommand(async () =>
        {
            Snapshot();
            var api = new ChamferApi(Document);
            var result = await Macros.RunAsync(_macroCode, api);
            RebuildAndRefresh();
            output.Text = string.Join("\n", result.Output) + (result.Error is { } err ? $"\n⚠ {err}" : result.Succeeded ? "\n✓ Done" : string.Empty);
            if (Editor != panel)
            {
                Editor = panel;
            }
        })));
        panel.AddInfo(new ButtonField(IsRecording ? "Stop Recording" : "Record", new RelayCommand(() =>
        {
            if (!IsRecording)
            {
                _recordingFrom = Document.Save();
                IsRecording = true;
                MessageText = "Recording: add features as usual, then come back and click Stop Recording.";
                Editor = null;
                return;
            }

            IsRecording = false;
            _macroCode = Macros.Record(PartDocument.Load(_recordingFrom!), Document);
            ShowMacros();
        })));
        panel.AddInfo(new ButtonField("Open Macro", new AsyncRelayCommand(OpenMacro)));
        panel.AddInfo(new ButtonField("Save Macro", new AsyncRelayCommand(SaveMacro)));
        panel.AddInfo(output);
        Editor = panel;
    }

    private async Task OpenMacro()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open Macro", FileTypeFilter = [MacroFileType] });
        if (files is [var file] && file.TryGetLocalPath() is { } path)
        {
            _macroCode = await File.ReadAllTextAsync(path);
            ShowMacros();
        }
    }

    private async Task SaveMacro()
    {
        if (StorageProvider is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Macro",
            DefaultExtension = "csx",
            SuggestedFileName = "Macro1",
            FileTypeChoices = [MacroFileType],
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await File.WriteAllTextAsync(path, _macroCode);
        }
    }
}
