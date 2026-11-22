using System.Globalization;
using System.Text.RegularExpressions;

namespace Chamfer.Document;

/// <summary>A named document property, such as Description or PartNumber.</summary>
public sealed class CustomProperty
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The text as entered; may contain tokens like <c>{Mass}</c> or <c>{Width}</c>.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Evaluates custom property text. Tokens in braces are replaced: <c>{Mass}</c> (kg),
/// <c>{Volume}</c> (mm³), <c>{SurfaceArea}</c> (mm²), <c>{Material}</c>, <c>{Density}</c>,
/// <c>{Configuration}</c>, another property's name, or a global variable's name.
/// </summary>
public static partial class PropertyEvaluator
{
    /// <summary>Well-known property names used by BOMs and title blocks.</summary>
    public const string Description = "Description";

    public const string PartNumber = "PartNumber";

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_ ]*)\}")]
    private static partial Regex Token();

    public static string Evaluate(PartDocument doc, string text) => Evaluate(doc, text, 0);

    private static string Evaluate(PartDocument doc, string text, int depth)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(text);
        var inv = CultureInfo.InvariantCulture;
        PartMassProperties? mass = null;
        PartMassProperties Mass() => mass ??= doc.MassProperties();
        return Token().Replace(text, m =>
        {
            var key = m.Groups[1].Value.Trim();
            switch (key.ToUpperInvariant())
            {
                case "MASS":
                    return Mass().Mass.ToString("0.###", inv);
                case "VOLUME":
                    return Mass().Volume.ToString("0.##", inv);
                case "SURFACEAREA":
                    return Mass().SurfaceArea.ToString("0.##", inv);
                case "MATERIAL":
                    return doc.Material.Name;
                case "DENSITY":
                    return doc.Material.Density.ToString("0.##", inv);
                case "CONFIGURATION":
                    return doc.ActiveConfiguration;
            }

            if (depth < 4 && doc.Properties.Find(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) is { } other)
            {
                return Evaluate(doc, other.Value, depth + 1);
            }

            if (doc.Variables.Find(v => string.Equals(v.Name, key, StringComparison.OrdinalIgnoreCase)) is { } variable &&
                doc.VariableValues.TryGetValue(variable.Name, out var value))
            {
                return value.ToString("0.###", inv);
            }

            return m.Value;
        });
    }

    /// <summary>The evaluated value of a property, or null when the part doesn't have it.</summary>
    public static string? Get(PartDocument doc, string name)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return doc.Properties.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) is { } p ? Evaluate(doc, p.Value) : null;
    }

    /// <summary>Adds, updates or (with an empty value) removes a property.</summary>
    public static void Set(PartDocument doc, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(name);
        var existing = doc.Properties.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(value))
        {
            if (existing is not null)
            {
                doc.Properties.Remove(existing);
            }

            return;
        }

        if (existing is null)
        {
            doc.Properties.Add(new CustomProperty { Name = name.Trim(), Value = value });
        }
        else
        {
            existing.Value = value;
        }
    }
}

/// <summary>One entity of a mate reference: a face and how it mates ("concentric" or "coincident").</summary>
public sealed record MateReferenceEntity(TopoRef Face, string Kind);

/// <summary>
/// A named set of faces that tells an assembly how to mate this part automatically: when it is
/// inserted, it is mated to another component with a reference of the same name.
/// </summary>
public sealed class MateReference
{
    public string Name { get; set; } = "Default";

    public List<MateReferenceEntity> Entities { get; set; } = [];
}
