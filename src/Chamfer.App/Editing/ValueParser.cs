using System.Globalization;

namespace Chamfer.App.Editing;

/// <summary>
/// Parses dimension input such as <c>25</c>, <c>25.4*2</c> or <c>(100 - 12)/2</c>.
/// Supports + - * / and parentheses. A trailing <c>in</c> converts inches to millimetres.
/// </summary>
public static class ValueParser
{
    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().TrimEnd('°').Trim();
        var scale = 1.0;
        if (s.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^2];
        }
        else if (s.EndsWith("in", StringComparison.OrdinalIgnoreCase) || s.EndsWith('"'))
        {
            s = s.EndsWith('"') ? s[..^1] : s[..^2];
            scale = 25.4;
        }

        var parser = new Parser(s);
        try
        {
            value = parser.Expression() * scale;
            parser.SkipSpaces();
            return parser.AtEnd && double.IsFinite(value);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        public void SkipSpaces()
        {
            while (!AtEnd && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        private bool Accept(char c)
        {
            SkipSpaces();
            if (!AtEnd && text[_pos] == c)
            {
                _pos++;
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
            var v = Factor();
            while (true)
            {
                if (Accept('*'))
                {
                    v *= Factor();
                }
                else if (Accept('/'))
                {
                    v /= Factor();
                }
                else
                {
                    return v;
                }
            }
        }

        private double Factor()
        {
            if (Accept('-'))
            {
                return -Factor();
            }

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
            var start = _pos;
            while (!AtEnd && (char.IsDigit(text[_pos]) || text[_pos] == '.'))
            {
                _pos++;
            }

            if (start == _pos ||
                !double.TryParse(text.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                throw new FormatException("Expected a number.");
            }

            return number;
        }
    }
}
