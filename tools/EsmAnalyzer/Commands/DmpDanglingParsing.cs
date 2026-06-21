using System.Globalization;
using System.Text;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Shared low-level parsing helpers used across the attribute-dangling pipeline
///     (authority JSON loading, sweep CSV loading, positions CSV loading).
/// </summary>
internal static class DmpDanglingParsing
{
    public static bool TryParseHexUInt(string? s, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }
        var span = s.AsSpan();
        if (span.Length > 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X'))
        {
            span = span[2..];
        }
        return uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public static string[] SplitCsv(string line)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                parts.Add(cur.ToString());
                cur.Clear();
            }
            else
            {
                cur.Append(c);
            }
        }
        parts.Add(cur.ToString());
        return parts.ToArray();
    }
}
