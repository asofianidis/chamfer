using System.Globalization;

namespace Chamfer.Drawing;

/// <summary>
/// ISO 286 limits and fits: standard tolerance grades IT5 to IT11 and the common fundamental
/// deviations for shafts (lower case: d e f g h js k m n p) and holes (upper case), for sizes up
/// to 500 mm. Deviations are in millimetres.
/// </summary>
public static class Fits
{
    private static readonly double[] Ranges = [3, 6, 10, 18, 30, 50, 80, 120, 180, 250, 315, 400, 500];

    // Tolerance grades in micrometres, per size range.
    private static readonly Dictionary<int, int[]> It = new()
    {
        [4] = [3, 4, 4, 5, 6, 7, 8, 10, 12, 14, 16, 18, 20],
        [5] = [4, 5, 6, 8, 9, 11, 13, 15, 18, 20, 23, 25, 27],
        [6] = [6, 8, 9, 11, 13, 16, 19, 22, 25, 29, 32, 36, 40],
        [7] = [10, 12, 15, 18, 21, 25, 30, 35, 40, 46, 52, 57, 63],
        [8] = [14, 18, 22, 27, 33, 39, 46, 54, 63, 72, 81, 89, 97],
        [9] = [25, 30, 36, 43, 52, 62, 74, 87, 100, 115, 130, 140, 155],
        [10] = [40, 48, 58, 70, 84, 100, 120, 140, 160, 185, 210, 230, 250],
        [11] = [60, 75, 90, 110, 130, 160, 190, 220, 250, 290, 320, 360, 400],
    };

    // Shaft upper deviations (es, a-h) and lower deviations (ei, k-p), micrometres.
    private static readonly Dictionary<char, int[]> ShaftUpper = new()
    {
        ['d'] = [-20, -30, -40, -50, -65, -80, -100, -120, -145, -170, -190, -210, -230],
        ['e'] = [-14, -20, -25, -32, -40, -50, -60, -72, -85, -100, -110, -125, -135],
        ['f'] = [-6, -10, -13, -16, -20, -25, -30, -36, -43, -50, -56, -62, -68],
        ['g'] = [-2, -4, -5, -6, -7, -9, -10, -12, -14, -15, -17, -18, -20],
        ['h'] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
    };

    private static readonly Dictionary<char, int[]> ShaftLower = new()
    {
        ['k'] = [0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4, 4, 5],
        ['m'] = [2, 4, 6, 7, 8, 9, 11, 13, 15, 17, 20, 21, 23],
        ['n'] = [4, 8, 10, 12, 15, 17, 20, 23, 27, 31, 34, 37, 40],
        ['p'] = [6, 12, 15, 18, 22, 26, 32, 37, 43, 50, 56, 62, 68],
    };

    /// <summary>Common fits offered in the dimension panel.</summary>
    public static IReadOnlyList<string> Common { get; } =
        ["H7", "H8", "H9", "H11", "h6", "h7", "g6", "f7", "k6", "m6", "n6", "p6", "js6", "H7/g6", "H7/h6", "H7/k6", "H7/n6", "H7/p6", "H8/f7", "H9/d9"];

    /// <summary>
    /// The upper and lower deviations of one tolerance zone (like "H7" or "g6") at a nominal size,
    /// in mm, or null when the zone isn't covered.
    /// </summary>
    public static (double Upper, double Lower)? Deviations(double size, string zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        zone = zone.Trim();
        var digits = zone.SkipWhile(char.IsLetter).ToArray();
        var letters = zone[..(zone.Length - digits.Length)];
        if (size <= 0 || size > 500 || letters.Length == 0 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var grade) || !It.TryGetValue(grade, out var grades))
        {
            return null;
        }

        var r = Array.FindIndex(Ranges, max => size <= max);
        double tolerance = grades[r];
        var hole = char.IsUpper(letters[0]);
        var letter = char.ToLowerInvariant(letters[0]);
        double? es = null, ei = null;
        if (letters.Equals("js", StringComparison.OrdinalIgnoreCase))
        {
            (es, ei) = (tolerance / 2, -tolerance / 2);
        }
        else if (letters.Length == 1 && ShaftUpper.TryGetValue(letter, out var upper))
        {
            es = upper[r];
            ei = es - tolerance;
        }
        else if (letters.Length == 1 && ShaftLower.TryGetValue(letter, out var lower))
        {
            // k has no lower deviation above IT7.
            ei = letter == 'k' && grade > 7 ? 0 : lower[r];
            es = ei + tolerance;
        }

        if (es is null || ei is null)
        {
            return null;
        }

        if (!hole)
        {
            return (es.Value / 1000, ei.Value / 1000);
        }

        // Holes mirror the shaft's zone about the zero line (ISO 286-1); K, M, N and P of fine grades
        // add Δ = ITn − IT(n−1) so a hole/shaft pair fits alike.
        var delta = It.TryGetValue(grade - 1, out var finer) ? tolerance - finer[r] : 0;
        var corrected = (letter is 'k' or 'm' or 'n' && grade <= 8) || (letter == 'p' && grade <= 7);
        double holeUpper, holeLower;
        if (letter == 'k' && grade > 8)
        {
            holeUpper = 0;
        }
        else if (letter == 'n' && grade > 8)
        {
            holeUpper = 0;
        }
        else if (ShaftLower.ContainsKey(letter))
        {
            holeUpper = -ei.Value + (corrected ? delta : 0);
        }
        else
        {
            holeUpper = double.NaN;
        }

        if (double.IsNaN(holeUpper))
        {
            holeLower = -es.Value;
            holeUpper = holeLower + tolerance;
        }
        else
        {
            holeLower = holeUpper - tolerance;
        }

        return (holeUpper / 1000, holeLower / 1000);
    }

    /// <summary>"+0.015/0" style limits for each zone of a fit, joined by " / " for hole/shaft pairs.</summary>
    public static string? LimitsText(double size, string fit)
    {
        ArgumentNullException.ThrowIfNull(fit);
        var parts = new List<string>();
        foreach (var zone in fit.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (Deviations(size, zone) is not { } d)
            {
                return null;
            }

            parts.Add($"{Signed(d.Upper)}/{Signed(d.Lower)}");
        }

        return string.Join("  ", parts);
    }

    private static string Signed(double v) =>
        Math.Abs(v) < 5e-7 ? "0" : (v > 0 ? "+" : "−") + Math.Abs(v).ToString("0.000", CultureInfo.InvariantCulture);
}
