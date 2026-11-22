using System.Globalization;

namespace Chamfer.Geometry;

/// <summary>Length units for display and entry. Geometry is always stored in millimetres.</summary>
public enum LengthUnit
{
    Millimeter,
    Centimeter,
    Meter,
    Inch,
}

/// <summary>Conversions between millimetres and display units, and parsing of values with unit suffixes.</summary>
public static class Units
{
    public static double MillimetersPer(LengthUnit unit) => unit switch
    {
        LengthUnit.Centimeter => 10,
        LengthUnit.Meter => 1000,
        LengthUnit.Inch => 25.4,
        _ => 1,
    };

    public static string Suffix(LengthUnit unit) => unit switch
    {
        LengthUnit.Centimeter => "cm",
        LengthUnit.Meter => "m",
        LengthUnit.Inch => "in",
        _ => "mm",
    };

    /// <summary>The unit system's short name, as in a status bar: MMGS, CGS, MKS or IPS.</summary>
    public static string SystemName(LengthUnit unit) => unit switch
    {
        LengthUnit.Centimeter => "CGS",
        LengthUnit.Meter => "MKS",
        LengthUnit.Inch => "IPS",
        _ => "MMGS",
    };

    public static double ToDisplay(double mm, LengthUnit unit) => mm / MillimetersPer(unit);

    public static double FromDisplay(double value, LengthUnit unit) => value * MillimetersPer(unit);

    /// <summary>Formats a length in the unit, with sensible decimals (more for inches).</summary>
    /// <summary>
    /// A length in a unit: with <paramref name="decimals"/>, always that many decimal places (25.00);
    /// without, as many as it needs up to the unit's precision (25, 25.4).
    /// </summary>
    public static string Format(double mm, LengthUnit unit, bool withSuffix = false, int? decimals = null)
    {
        var v = ToDisplay(mm, unit);
        var format = decimals is { } d ? "F" + Math.Clamp(d, 0, 8).ToString(CultureInfo.InvariantCulture)
            : unit == LengthUnit.Inch ? "0.####" : unit == LengthUnit.Meter ? "0.#####" : "0.###";
        var text = v.ToString(format, CultureInfo.InvariantCulture);
        return withSuffix ? text + " " + Suffix(unit) : text;
    }

    private static readonly (string Suffix, double Mm)[] Suffixes =
    [
        ("mm", 1), ("cm", 10), ("in", 25.4), ("\"", 25.4), ("ft", 304.8), ("'", 304.8), ("m", 1000),
    ];

    /// <summary>
    /// Splits a trailing unit suffix off <paramref name="text"/>: returns the remaining text and the
    /// millimetres per unit of the suffix, or null when there is none.
    /// </summary>
    public static (string Text, double? MmPerUnit) SplitSuffix(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        foreach (var (suffix, mm) in Suffixes)
        {
            if (t.Length > suffix.Length && t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = t[..^suffix.Length].TrimEnd();
                if (rest.Length > 0 && (char.IsDigit(rest[^1]) || rest[^1] is ')' or '.'))
                {
                    return (rest, mm);
                }
            }
        }

        return (t, null);
    }
}
