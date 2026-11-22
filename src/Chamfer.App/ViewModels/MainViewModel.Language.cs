using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>The display language for commands and tabs.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Where the chosen language is remembered (tests point this elsewhere).</summary>
    public string LanguageFile { get; set; } = Path.Combine(AppDataFolder, "language.txt");

    /// <summary>Reads the remembered language (unless CHAMFER_LANG overrides it).</summary>
    public void LoadLanguage()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("CHAMFER_LANG") is null && File.Exists(LanguageFile) &&
                File.ReadAllText(LanguageFile).Trim() is { } code && Localizer.Languages.ContainsKey(code))
            {
                Localizer.Language = code;
            }
        }
        catch (IOException)
        {
            // Keep the default.
        }
    }

    /// <summary>Switches the language, remembers it and redraws the ribbon.</summary>
    public void SetLanguage(string code)
    {
        if (!Localizer.Languages.ContainsKey(code))
        {
            return;
        }

        Localizer.Language = code;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LanguageFile)!);
            File.WriteAllText(LanguageFile, code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText = $"Couldn't save the language: {ex.Message}";
        }

        // New item containers pick up the translated names.
        var tabs = CommandTabs;
        CommandTabs = [];
        CommandTabs = tabs;
        OnPropertyChanged(nameof(VisibleFeatureCommands));
        OnPropertyChanged(nameof(AssemblyCommands));
        OnPropertyChanged(nameof(DrawingCommands));
    }

    [RelayCommand]
    private void ShowLanguage()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Language");
        var codes = Localizer.Languages.Keys.ToList();
        panel.AddInfo(new ChoiceField("Language", [.. codes.Select(c => Localizer.Languages[c])], Math.Max(0, codes.IndexOf(Localizer.Language)), i =>
        {
            SetLanguage(codes[i]);
        }));
        panel.AddInfo(new InfoField("Coverage", "Command and tab names are translated; panels and messages are in English for now."));
        Editor = panel;
    }
}
