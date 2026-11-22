using Chamfer.Geometry;

namespace Chamfer.App.Editing;

/// <summary>The length unit the open document shows and accepts; values are stored in millimetres.</summary>
public static class DisplayUnits
{
    // Per execution flow: the app's UI thread has one, and tests running side by side don't share it.
    private static readonly AsyncLocal<LengthUnit> Current = new();

    public static LengthUnit Length
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    private static readonly AsyncLocal<int?> CurrentDecimals = new();

    /// <summary>Fixed decimal places for lengths shown, or null for as many as needed.</summary>
    public static int? Decimals
    {
        get => CurrentDecimals.Value;
        set => CurrentDecimals.Value = value;
    }

    public static string Suffix => Units.Suffix(Length);

    public static string Format(double mm) => Units.Format(mm, Length, decimals: Decimals);

    /// <summary>Parses a length: a bare number is in the document unit; "1in", "25 mm" and the like carry their own.</summary>
    public static bool TryParse(string text, out double mm)
    {
        ArgumentNullException.ThrowIfNull(text);
        var (_, perUnit) = Units.SplitSuffix(text);
        if (!ValueParser.TryParse(text, out var v))
        {
            mm = 0;
            return false;
        }

        mm = perUnit is null ? v * Units.MillimetersPer(Length) : v;
        return true;
    }
}
