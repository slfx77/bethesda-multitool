using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     RFC-4180-style CSV tokenizer for the Bethesda Audio Transcriber export consumed by
///     <see cref="DialogueAudioCsvAssetCollector" />. Handles quoted fields, escaped quotes
///     (<c>""</c>), and embedded newlines inside quoted cells.
/// </summary>
internal static class DialogueAudioCsvReader
{
    internal static int FindColumn(List<string> headerFields, string name)
    {
        for (var i = 0; i < headerFields.Count; i++)
        {
            if (string.Equals(headerFields[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    internal static bool TryParseFormId(string raw, out uint formId)
    {
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[2..];
        }

        return uint.TryParse(
            raw,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out formId);
    }

    internal static List<string> ReadCsvRecord(TextReader reader)
    {
        var record = new StringBuilder();
        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            if (record.Length > 0)
            {
                record.Append('\n');
            }

            record.Append(line);
            if (HasBalancedQuotes(record))
            {
                break;
            }
        }

        return record.Length == 0 ? [] : ParseCsvFields(record.ToString());
    }

    private static bool HasBalancedQuotes(StringBuilder record)
    {
        var inQuotes = false;
        var i = 0;
        while (i < record.Length)
        {
            if (record[i] != '"')
            {
                i++;
                continue;
            }

            if (inQuotes && i + 1 < record.Length && record[i + 1] == '"')
            {
                i += 2;
                continue;
            }

            inQuotes = !inQuotes;
            i++;
        }

        return !inQuotes;
    }

    private static List<string> ParseCsvFields(string record)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        var i = 0;
        while (i < record.Length)
        {
            var ch = record[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < record.Length && record[i + 1] == '"')
                {
                    field.Append('"');
                    i += 2;
                }
                else
                {
                    inQuotes = !inQuotes;
                    i++;
                }

                continue;
            }

            if (ch == ',' && !inQuotes)
            {
                fields.Add(field.ToString());
                field.Clear();
                i++;
                continue;
            }

            field.Append(ch);
            i++;
        }

        fields.Add(field.ToString());
        return fields;
    }
}
