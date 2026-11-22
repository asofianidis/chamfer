using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Where the side panel sits (left or right), and whether it's shown; remembered between sessions.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Where the layout is remembered (tests point this elsewhere).</summary>
    public string LayoutFile { get; set; } = Path.Combine(AppDataFolder, "layout.txt");

    [ObservableProperty]
    public partial bool PanelOnRight { get; set; }

    [ObservableProperty]
    public partial bool PanelHidden { get; set; }

    partial void OnPanelOnRightChanged(bool value) => SaveLayout();

    partial void OnPanelHiddenChanged(bool value) => SaveLayout();

    [RelayCommand]
    private void TogglePanelSide() => PanelOnRight = !PanelOnRight;

    [RelayCommand]
    private void TogglePanel() => PanelHidden = !PanelHidden;

    private bool _loadingLayout;

    /// <summary>Reads the remembered layout ("right" and/or "hidden" words).</summary>
    public void LoadLayout()
    {
        try
        {
            if (File.Exists(LayoutFile))
            {
                var words = File.ReadAllText(LayoutFile).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                _loadingLayout = true;
                PanelOnRight = words.Contains("right");
                PanelHidden = words.Contains("hidden");
            }
        }
        catch (IOException)
        {
            // Keep the defaults.
        }
        finally
        {
            _loadingLayout = false;
        }
    }

    private void SaveLayout()
    {
        if (_loadingLayout)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)!);
            File.WriteAllText(LayoutFile, $"{(PanelOnRight ? "right" : "left")} {(PanelHidden ? "hidden" : "shown")}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not worth interrupting the user for.
        }
    }
}
