using System.Text;

namespace Chamfer.Document;

/// <summary>
/// A named variant of a part. It overrides global variable values (which can
/// drive any dimension or parameter through equations) and the suppression
/// state of features.
/// </summary>
public sealed class Configuration
{
    public string Name { get; set; } = "Default";

    /// <summary>Variable name → expression, replacing the document's definition while this configuration is active.</summary>
    public Dictionary<string, string> Variables { get; set; } = [];

    /// <summary>Feature id → suppressed, overriding the feature's own state.</summary>
    public Dictionary<string, bool> Suppression { get; set; } = [];

    public Configuration Copy(string name) => new()
    {
        Name = name,
        Variables = new Dictionary<string, string>(Variables),
        Suppression = new Dictionary<string, bool>(Suppression),
    };
}

/// <summary>
/// Design tables: configurations as a CSV spreadsheet. The first column is the
/// configuration name; other columns are variables (by name) or feature
/// suppression states (<c>$STATE@FeatureName</c>, with values S/U).
/// </summary>
public static class DesignTable
{
    private const string StatePrefix = "$STATE@";

    public static string Export(PartDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var variables = doc.Variables.Select(v => v.Name)
            .Concat(doc.Configurations.SelectMany(c => c.Variables.Keys))
            .Distinct(StringComparer.Ordinal).ToList();
        var states = doc.Features.Where(f => doc.Configurations.Any(c => c.Suppression.ContainsKey(f.Id))).ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', variables.Prepend("Configuration").Concat(states.Select(f => StatePrefix + f.Name)).Select(Quote)));
        foreach (var c in doc.Configurations)
        {
            var cells = new List<string> { c.Name };
            foreach (var v in variables)
            {
                cells.Add(c.Variables.TryGetValue(v, out var e) ? e : doc.Variables.FirstOrDefault(x => x.Name == v)?.Expression ?? string.Empty);
            }

            foreach (var f in states)
            {
                var suppressed = c.Suppression.TryGetValue(f.Id, out var s) ? s : f.IsSuppressed;
                cells.Add(suppressed ? "S" : "U");
            }

            sb.AppendLine(string.Join(',', cells.Select(Quote)));
        }

        return sb.ToString();
    }

    /// <summary>Replaces the document's configurations with the rows of a table.</summary>
    public static void Import(PartDocument doc, string csv)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(csv);
        var rows = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).Select(Split).ToList();
        if (rows.Count < 2)
        {
            throw new FormatException("A design table needs a header row and at least one configuration.");
        }

        var header = rows[0];
        var configs = new List<Configuration>();
        foreach (var row in rows.Skip(1))
        {
            var c = new Configuration { Name = row[0].Trim() };
            if (c.Name.Length == 0)
            {
                continue;
            }

            for (var i = 1; i < header.Count && i < row.Count; i++)
            {
                var column = header[i].Trim();
                var cell = row[i].Trim();
                if (cell.Length == 0)
                {
                    continue;
                }

                if (column.StartsWith(StatePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var name = column[StatePrefix.Length..];
                    var feature = doc.Features.FirstOrDefault(f => f.Name == name)
                        ?? throw new FormatException($"No feature named '{name}'.");
                    c.Suppression[feature.Id] = cell.StartsWith('S') || cell.StartsWith('s');
                }
                else
                {
                    c.Variables[column] = cell;
                }
            }

            configs.Add(c);
        }

        if (configs.Count == 0)
        {
            throw new FormatException("The design table has no configurations.");
        }

        doc.Configurations.Clear();
        doc.Configurations.AddRange(configs);
        if (!configs.Any(c => c.Name == doc.ActiveConfiguration))
        {
            doc.ActiveConfiguration = configs[0].Name;
        }
    }

    private static string Quote(string s) =>
        s.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{s.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : s;

    private static List<string> Split(string line)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    sb.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch == ',' || ch == ';' || ch == '\t')
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }

        cells.Add(sb.ToString());
        return cells;
    }

}
