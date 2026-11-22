using Chamfer.Geometry;

namespace Chamfer.App.Editing;

/// <summary>
/// Parses dimension input such as <c>25</c>, <c>25.4*2</c>, <c>(100 - 12)/2</c> or <c>1in</c>.
/// See <see cref="Expressions"/> for the full syntax; values are millimetres (or degrees).
/// </summary>
public static class ValueParser
{
    public static bool TryParse(string? text, out double value) => Expressions.TryEvaluate(text, null, out value);

    /// <summary>Parses with variables available, e.g. <c>Width/2</c>.</summary>
    public static bool TryParse(string? text, Func<string, double?>? variables, out double value) =>
        Expressions.TryEvaluate(text, variables, out value);
}
