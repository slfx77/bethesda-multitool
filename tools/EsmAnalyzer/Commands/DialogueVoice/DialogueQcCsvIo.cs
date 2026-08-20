using System.Buffers;
using System.Text;

namespace EsmAnalyzer.Commands.DialogueVoice;

/// <summary>
///     Minimal CSV reader/writer used by <see cref="DialogueQcCommand" />.
///     Handles quoted fields with embedded quotes/commas/newlines.
/// </summary>
internal static class DialogueQcCsvIo
{
    private static readonly SearchValues<char> CharsRequiringQuotes = SearchValues.Create(',', '"', '\r', '\n');

    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                continue;
            }

            if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                continue;
            }

            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
                continue;
            }

            if (c == '\n')
            {
                row.Add(field.ToString());
                field.Clear();
                rows.Add(row.ToArray());
                row.Clear();
                continue;
            }

            field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row.ToArray());
        }

        return rows;
    }

    public static string SerializeRow(string[] fields)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Escape(fields[i]));
        }

        return sb.ToString();
    }

    private static string Escape(string value)
    {
        if (value == null)
        {
            return "";
        }

        if (value.AsSpan().IndexOfAny(CharsRequiringQuotes) < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}