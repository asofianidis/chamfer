using System.Text.Json;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Customisable keyboard shortcuts and finding commands by name.</summary>
public sealed partial class MainViewModel
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private Dictionary<string, string>? _shortcuts;

    /// <summary>Where custom shortcuts are saved (tests point this elsewhere).</summary>
    public string ShortcutsFile { get; set; } = Path.Combine(AppDataFolder, "shortcuts.json");

    /// <summary>Built-in shortcuts; the user's file adds to and overrides them.</summary>
    public static IReadOnlyDictionary<string, string> DefaultShortcuts { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl+E"] = "Extruded Boss",
        ["Ctrl+Shift+E"] = "Extruded Cut",
        ["Ctrl+R"] = "Revolved Boss",
        ["Ctrl+Shift+F"] = "Fillet",
        ["Ctrl+M"] = "Mate",
        ["Ctrl+K"] = "Find Command",
        ["F1"] = "Shortcuts",
    };

    /// <summary>Gesture ("Ctrl+Shift+E", "F5", "Q") to command name.</summary>
    public IReadOnlyDictionary<string, string> Shortcuts => _shortcuts ??= LoadShortcuts();

    private Dictionary<string, string> LoadShortcuts()
    {
        var map = new Dictionary<string, string>(DefaultShortcuts, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(ShortcutsFile) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ShortcutsFile)) is { } custom)
            {
                foreach (var (k, v) in custom)
                {
                    if (string.IsNullOrWhiteSpace(v))
                    {
                        map.Remove(Normalize(k));
                    }
                    else
                    {
                        map[Normalize(k)] = v;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            MessageText = $"Couldn't read shortcuts: {ex.Message}";
        }

        return map;
    }

    /// <summary>Canonical gesture text: modifiers in Ctrl, Alt, Shift order, then the key.</summary>
    public static string Normalize(string gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var mods = parts[..^1].Select(p => p.ToUpperInvariant() switch
        {
            "CONTROL" or "CTRL" or "CMD" or "COMMAND" => "Ctrl",
            "ALT" or "OPTION" => "Alt",
            "SHIFT" => "Shift",
            _ => p,
        }).Distinct().OrderBy(m => m switch { "Ctrl" => 0, "Alt" => 1, "Shift" => 2, _ => 3 });
        var key = parts[^1].Length == 1 ? parts[^1].ToUpperInvariant() : parts[^1];
        return string.Join('+', mods.Append(key));
    }

    /// <summary>Sets (or with an empty command, removes) a shortcut and saves the user's shortcuts.</summary>
    public void SetShortcut(string gesture, string command)
    {
        var key = Normalize(gesture);
        if (key.Length == 0)
        {
            return;
        }

        var map = (Dictionary<string, string>)Shortcuts;
        if (string.IsNullOrWhiteSpace(command))
        {
            map.Remove(key);
        }
        else
        {
            map[key] = command.Trim();
        }

        // Save the differences from the defaults (removed defaults as empty).
        var custom = map.Where(p => !DefaultShortcuts.TryGetValue(p.Key, out var d) || d != p.Value).ToDictionary(p => p.Key, p => p.Value);
        foreach (var d in DefaultShortcuts.Keys.Where(k => !map.ContainsKey(k)))
        {
            custom[d] = string.Empty;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutsFile)!);
            File.WriteAllText(ShortcutsFile, JsonSerializer.Serialize(custom, IndentedJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageText = $"Couldn't save shortcuts: {ex.Message}";
        }
    }

    /// <summary>Runs the command bound to a gesture; false when none is bound or it can't run now.</summary>
    public bool RunShortcut(string gesture) =>
        Shortcuts.TryGetValue(Normalize(gesture), out var name) && RunCommand(name);

    /// <summary>Every command with a name, for shortcuts and search (only those that apply in the current mode).</summary>
    public IReadOnlyList<(string Name, IRelayCommand Command)> NamedCommands()
    {
        var list = new List<(string, IRelayCommand)>();
        IEnumerable<CommandItem> modeCommands = DrawingDoc is not null ? DrawingCommands
            : AssemblyDoc is not null ? AssemblyCommands
            : FeatureCommands;
        list.AddRange(modeCommands.Select(c => (c.Label, c.Command)));
        list.Add(("Save", SaveCommand));
        list.Add(("Undo", UndoCommand));
        list.Add(("Redo", RedoCommand));
        list.Add(("Zoom to Fit", ZoomToFitCommand));
        list.Add(("Find Command", ShowFindCommandCommand));
        list.Add(("Shortcuts", ShowShortcutsCommand));
        if (AssemblyDoc is null && DrawingDoc is null)
        {
            list.Add(("Mass Properties", ShowMassPropertiesCommand));
            list.Add(("Equations", ShowEquationsCommand));
            list.Add(("Units", ShowUnitsCommand));
            list.Add(("Render", StartRenderCommand));
        }

        return list;
    }

    /// <summary>Runs a command by name (case-insensitive).</summary>
    public bool RunCommand(string name)
    {
        var match = NamedCommands().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (match.Command is null || !match.Command.CanExecute(null))
        {
            return false;
        }

        match.Command.Execute(null);
        return true;
    }

    [RelayCommand]
    private void ShowFindCommand()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Find Command");
        panel.AddInfo(new TextField("Search", string.Empty, text =>
        {
            var hits = NamedCommands().Where(c => c.Name.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)).Take(12).ToList();
            if (hits.Count == 1)
            {
                Editor = null;
                hits[0].Command.Execute(null);
                return;
            }

            ShowFindCommand();
            foreach (var (name, command) in hits)
            {
                Editor!.AddInfo(new ButtonField(name, new RelayCommand(() =>
                {
                    Editor = null;
                    command.Execute(null);
                })));
            }

            if (hits.Count == 0)
            {
                Editor!.AddInfo(new InfoField("Search", $"No command matches '{text}'."));
            }
        }));
        panel.AddInfo(new InfoField("How", "Type part of a command's name and press Enter."));
        Editor = panel;
    }

    [RelayCommand]
    private void ShowShortcuts()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Shortcuts");
        foreach (var (gesture, name) in Shortcuts.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase))
        {
            panel.AddInfo(new InfoField(gesture, name));
        }

        panel.AddInfo(new TextField("Gesture = command", string.Empty, text =>
        {
            var eq = text.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                MessageText = "Type a shortcut as Ctrl+Shift+H = Hole Wizard (or Ctrl+Shift+H = to remove it).";
                return;
            }

            SetShortcut(text[..eq], text[(eq + 1)..]);
            ShowShortcuts();
        }));
        panel.AddInfo(new InfoField("Built in", "Ctrl+S save, Ctrl+Z / Ctrl+Y undo and redo, Ctrl+1..8 views, F fit, Delete, Escape."));
        Editor = panel;
    }
}
