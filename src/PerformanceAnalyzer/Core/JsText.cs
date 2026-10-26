using System.Globalization;
using System.Text;

namespace PerformanceAnalyzer.Core;

public static class JsText
{
    private const char LineSeparator = '\u2028';
    private const char ParagraphSeparator = '\u2029';

    /// <summary>A JavaScript string literal, safe to paste into a script.</summary>
    public static string Literal(string? text)
    {
        var literal = new StringBuilder((text?.Length ?? 0) + 2).Append('"');

        foreach (var character in text ?? "")
        {
            switch (character)
            {
                case '"':
                    literal.Append("\\\"");
                    break;
                case '\\':
                    literal.Append("\\\\");
                    break;
                // Line and paragraph separators end a line in older JavaScript, and '<' keeps "</script>" inert.
                case < ' ' or LineSeparator or ParagraphSeparator or '<':
                    literal.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    literal.Append(character);
                    break;
            }
        }

        return literal.Append('"').ToString();
    }
}
