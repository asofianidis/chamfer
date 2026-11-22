using System.Globalization;
using System.Text;

namespace Chamfer.Geometry;

/// <summary>
/// Evaluates arithmetic expressions used for dimensions, feature parameters
/// and global variables: <c>+ - * / ^</c>, parentheses, variables (plain or
/// "quoted" names), functions, and units. Lengths are in millimetres
/// (<c>mm cm m in ft "</c>); angles in degrees (<c>deg rad</c>); trig functions
/// take and return degrees.
/// </summary>
public static class Expressions
{
    private static readonly Dictionary<string, double> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mm"] = 1,
        ["cm"] = 10,
        ["m"] = 1000,
        ["in"] = 25.4,
        ["ft"] = 304.8,
        ["deg"] = 1,
        ["rad"] = 180 / Math.PI,
    };

    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        "sin", "cos", "tan", "asin", "acos", "atan", "atan2", "sqrt", "abs", "min", "max", "round", "floor", "ceil", "sgn", "exp", "ln", "log",
    };

    /// <summary>Evaluates; throws <see cref="FormatException"/> on syntax errors or unknown names.</summary>
    public static double Evaluate(string text, Func<string, double?>? variable = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var p = new Parser(text, variable);
        var v = p.Expression();
        p.SkipSpaces();
        if (!p.AtEnd)
        {
            throw new FormatException($"Unexpected '{text[p.Position..]}'.");
        }

        if (!double.IsFinite(v))
        {
            throw new FormatException("The result isn't a finite number.");
        }

        return v;
    }

    public static bool TryEvaluate(string? text, Func<string, double?>? variable, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            value = Evaluate(text, variable);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Variable names an expression refers to.</summary>
    public static IReadOnlyList<string> References(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var names = new List<string>();
        var p = new Parser(text, name =>
        {
            names.Add(name);
            return 1;
        });
        try
        {
            p.Expression();
        }
        catch (FormatException)
        {
        }

        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    private sealed class Parser(string text, Func<string, double?>? variable)
    {
        public int Position { get; private set; }

        public bool AtEnd => Position >= text.Length;

        public void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(text[Position]))
            {
                Position++;
            }
        }

        private bool Accept(char c)
        {
            SkipSpaces();
            if (!AtEnd && text[Position] == c)
            {
                Position++;
                return true;
            }

            return false;
        }

        public double Expression()
        {
            var v = Term();
            while (true)
            {
                if (Accept('+'))
                {
                    v += Term();
                }
                else if (Accept('-'))
                {
                    v -= Term();
                }
                else
                {
                    return v;
                }
            }
        }

        private double Term()
        {
            var v = Unary();
            while (true)
            {
                if (Accept('*'))
                {
                    v *= Unary();
                }
                else if (Accept('/'))
                {
                    v /= Unary();
                }
                else
                {
                    return v;
                }
            }
        }

        // Unary minus binds looser than ^, so -3^2 = -9; exponents are right-associative and may be negative.
        private double Unary()
        {
            if (Accept('-'))
            {
                return -Unary();
            }

            return Accept('+') ? Unary() : Power();
        }

        private double Power()
        {
            var b = Unit(Primary());
            return Accept('^') ? Math.Pow(b, Unary()) : b;
        }

        /// <summary>An optional unit suffix after a value.</summary>
        private double Unit(double v)
        {
            SkipSpaces();
            if (!AtEnd && text[Position] == '"' && (Position + 1 >= text.Length || !char.IsLetter(text[Position + 1])))
            {
                Position++;
                return v * 25.4;
            }

            if (!AtEnd && text[Position] == '°')
            {
                Position++;
                return v;
            }

            var save = Position;
            var word = Word();
            if (word.Length > 0 && Units.TryGetValue(word, out var scale))
            {
                return v * scale;
            }

            Position = save;
            return v;
        }

        private string Word()
        {
            var start = Position;
            while (!AtEnd && (char.IsLetterOrDigit(text[Position]) || text[Position] is '_' or '@' or '.'))
            {
                if (Position == start && char.IsDigit(text[Position]))
                {
                    break;
                }

                Position++;
            }

            return text[start..Position];
        }

        private double Primary()
        {
            if (Accept('('))
            {
                var v = Expression();
                if (!Accept(')'))
                {
                    throw new FormatException("Missing ')'.");
                }

                return v;
            }

            SkipSpaces();
            if (AtEnd)
            {
                throw new FormatException("Expected a value.");
            }

            var c = text[Position];
            if (char.IsDigit(c) || c == '.')
            {
                var start = Position;
                while (!AtEnd && (char.IsDigit(text[Position]) || text[Position] == '.'))
                {
                    Position++;
                }

                // Exponent, e.g. 1e-3, but not a unit starting with 'e'.
                if (!AtEnd && text[Position] is 'e' or 'E' && Position + 1 < text.Length && (char.IsDigit(text[Position + 1]) || text[Position + 1] is '-' or '+'))
                {
                    Position++;
                    if (text[Position] is '-' or '+')
                    {
                        Position++;
                    }

                    while (!AtEnd && char.IsDigit(text[Position]))
                    {
                        Position++;
                    }
                }

                return double.TryParse(text.AsSpan(start, Position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                    ? n
                    : throw new FormatException("Bad number.");
            }

            if (c == '"')
            {
                // A quoted variable name, as in "Width" or "D1@Sketch1".
                Position++;
                var sb = new StringBuilder();
                while (!AtEnd && text[Position] != '"')
                {
                    sb.Append(text[Position++]);
                }

                if (AtEnd)
                {
                    throw new FormatException("Missing closing quote.");
                }

                Position++;
                return Lookup(sb.ToString());
            }

            var name = Word();
            if (name.Length == 0)
            {
                throw new FormatException($"Unexpected '{c}'.");
            }

            if (Functions.Contains(name) && Accept('('))
            {
                var args = new List<double> { Expression() };
                while (Accept(','))
                {
                    args.Add(Expression());
                }

                if (!Accept(')'))
                {
                    throw new FormatException("Missing ')'.");
                }

                return Call(name.ToLowerInvariant(), args);
            }

            if (name.Equals("pi", StringComparison.OrdinalIgnoreCase))
            {
                return Math.PI;
            }

            return Lookup(name);
        }

        private double Lookup(string name) =>
            variable?.Invoke(name) ?? throw new FormatException($"Unknown name '{name}'.");

        private static double Call(string f, List<double> a)
        {
            const double toRad = Math.PI / 180;
            double One() => a.Count == 1 ? a[0] : throw new FormatException($"{f} takes one argument.");
            return f switch
            {
                "sin" => Math.Sin(One() * toRad),
                "cos" => Math.Cos(One() * toRad),
                "tan" => Math.Tan(One() * toRad),
                "asin" => Math.Asin(One()) / toRad,
                "acos" => Math.Acos(One()) / toRad,
                "atan" => Math.Atan(One()) / toRad,
                "atan2" => a.Count == 2 ? Math.Atan2(a[0], a[1]) / toRad : throw new FormatException("atan2 takes two arguments."),
                "sqrt" => Math.Sqrt(One()),
                "abs" => Math.Abs(One()),
                "min" => a.Min(),
                "max" => a.Max(),
                "round" => Math.Round(One(), MidpointRounding.AwayFromZero),
                "floor" => Math.Floor(One()),
                "ceil" => Math.Ceiling(One()),
                "sgn" => Math.Sign(One()),
                "exp" => Math.Exp(One()),
                "ln" => Math.Log(One()),
                "log" => Math.Log10(One()),
                _ => throw new FormatException($"Unknown function '{f}'."),
            };
        }
    }
}
