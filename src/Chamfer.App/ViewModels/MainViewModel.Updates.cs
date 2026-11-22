using System.Text.Json;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Checking GitHub releases for a newer version (only when asked).</summary>
public sealed partial class MainViewModel
{
    public const string ReleasesApi = "https://api.github.com/repos/asofianidis/chamfer/releases/latest";

    public const string ReleasesPage = "https://github.com/asofianidis/chamfer/releases";

    /// <summary>This build's version.</summary>
    public static Version CurrentVersion =>
        typeof(MainViewModel).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>Reads a release's tag ("v1.2.3") from GitHub's latest-release JSON and says whether it's newer.</summary>
    public static (bool Newer, Version? Latest) CompareRelease(Version current, string releaseJson)
    {
        ArgumentNullException.ThrowIfNull(current);
        try
        {
            using var doc = JsonDocument.Parse(releaseJson);
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } text)
            {
                return (false, null);
            }

            var core = text.TrimStart('v', 'V').Split('-', '+')[0];
            if (!Version.TryParse(core.Contains('.', StringComparison.Ordinal) ? core : core + ".0", out var latest))
            {
                return (false, null);
            }

            var normalized = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            return (normalized > current, normalized);
        }
        catch (JsonException)
        {
            return (false, null);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdates()
    {
        MessageText = "Checking for updates...";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Chamfer-UpdateCheck");
            var json = await http.GetStringAsync(new Uri(ReleasesApi));
            var (newer, latest) = CompareRelease(CurrentVersion, json);
            MessageText = latest is null ? "No released version found yet."
                : newer ? $"Chamfer {latest} is available (you have {CurrentVersion}). Download it from {ReleasesPage}."
                : $"You have the latest version ({CurrentVersion}).";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            MessageText = $"Couldn't check for updates: {ex.Message}";
        }
    }
}
