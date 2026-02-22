using System.Globalization;

namespace FalloutXbox360Utils.Core.Formats.Subtitles;

/// <summary>
///     A single subtitle entry parsed from a transcriber CSV export.
/// </summary>
public sealed record SubtitleEntry(
    string? Text,
    string? Speaker,
    string? Quest,
    string? VoiceType,
    string? Source);

/// <summary>
///     Indexed subtitle data parsed from a transcriber CSV export.
///     Maps INFO FormIDs to subtitle text, speaker, and quest names.
///     CSV format: File,FormID,VoiceType,Speaker,Quest,Source,Text
/// </summary>
public sealed class SubtitleIndex
{
    private readonly Dictionary<uint, SubtitleEntry> _entries;

    public SubtitleIndex(Dictionary<uint, SubtitleEntry> entries) => _entries = entries;

    public int Count => _entries.Count;

    /// <summary>Returns the subtitle entry for the given INFO FormID, or null.</summary>
    public SubtitleEntry? Lookup(uint formId) =>
        _entries.TryGetValue(formId, out var entry) ? entry : null;

    /// <summary>
    ///     Parses a subtitle CSV file exported by FalloutAudioTranscriber.
    ///     Format: File,FormID,VoiceType,Speaker,Quest,Source,Text
    ///     Multiple rows with the same FormID are merged (first non-null wins per field).
    /// </summary>
    public static SubtitleIndex LoadFromCsv(string csvPath)
    {
        var entries = new Dictionary<uint, SubtitleEntry>();

        using var reader = new StreamReader(csvPath);

        // Skip header line
        var header = reader.ReadLine();
        if (header == null) return new SubtitleIndex(entries);

        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields = ParseCsvLine(line);
            if (fields.Count < 7) continue;

            // Column 1 = FormID (8-char hex, no 0x prefix)
            if (!uint.TryParse(fields[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var formId))
                continue;
            if (formId == 0) continue;

            var voiceType = NullIfEmpty(fields[2]);
            var speaker = NullIfEmpty(fields[3]);
            var quest = NullIfEmpty(fields[4]);
            var source = NullIfEmpty(fields[5]);
            var text = NullIfEmpty(fields[6]);

            if (entries.TryGetValue(formId, out var existing))
            {
                // Merge: first non-null wins per field
                entries[formId] = new SubtitleEntry(
                    existing.Text ?? text,
                    existing.Speaker ?? speaker,
                    existing.Quest ?? quest,
                    existing.VoiceType ?? voiceType,
                    existing.Source ?? source);
            }
            else
            {
                entries[formId] = new SubtitleEntry(text, speaker, quest, voiceType, source);
            }
        }

        return new SubtitleIndex(entries);
    }

    /// <summary>Parses a single CSV line, handling quoted fields with embedded commas and double-quotes.</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;

        while (i <= line.Length)
        {
            if (i == line.Length)
            {
                fields.Add("");
                break;
            }

            if (line[i] == '"')
            {
                // Quoted field
                i++; // skip opening quote
                var start = i;
                var value = new System.Text.StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            // Escaped quote
                            value.Append(line, start, i - start);
                            value.Append('"');
                            i += 2;
                            start = i;
                        }
                        else
                        {
                            // End of quoted field
                            value.Append(line, start, i - start);
                            i++; // skip closing quote
                            break;
                        }
                    }
                    else
                    {
                        i++;
                    }
                }

                if (i <= line.Length && start <= i)
                {
                    // Capture any remaining content if quote wasn't properly closed
                    if (i > start && value.Length == 0 && i <= line.Length)
                    {
                        // Already appended in the loop
                    }
                }

                fields.Add(value.ToString());

                // Skip comma separator
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                // Unquoted field
                var commaIdx = line.IndexOf(',', i);
                if (commaIdx < 0)
                {
                    fields.Add(line[i..]);
                    break;
                }

                fields.Add(line[i..commaIdx]);
                i = commaIdx + 1;
            }
        }

        return fields;
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
