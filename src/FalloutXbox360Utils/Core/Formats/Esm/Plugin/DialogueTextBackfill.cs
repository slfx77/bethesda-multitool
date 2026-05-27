using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Backfills INFO response text (NAM1) from a Fallout Audio Transcriber CSV when the
///     DMP capture left a response blank or marked it as "(NOT FOUND IN CRASH DUMP)".
///     The CSV carries one row per response (the per-row .xma filename embeds a response
///     index 1-based), so we can re-attach text to the right response slot even when the
///     DMP captured a TRDT but no NAM1.
/// </summary>
internal static class DialogueTextBackfill
{
    /// <summary>Sentinel response text emitted by the encoder when the DMP had nothing.</summary>
    public const string PlaceholderText = "(NOT FOUND IN CRASH DUMP)";

    private static readonly Regex VoiceFileResponsePattern = new(
        @"_(?<formid>[0-9A-Fa-f]{8})_(?<resp>\d+)\.(xma|ogg|lip|wav|mp3)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public sealed record BackfillResult(
        int RowsRead,
        int RowsParsed,
        int InfosTouched,
        int ResponsesFilled,
        int ResponsesAppended);

    /// <summary>
    ///     Parse all CSVs and apply text overrides in-place to <paramref name="dialogues" />.
    /// </summary>
    public static BackfillResult ApplyFromCsvs(
        List<DialogueRecord> dialogues,
        IReadOnlyList<string> csvPaths,
        IConversionProgressSink sink)
    {
        if (csvPaths.Count == 0 || dialogues.Count == 0)
        {
            return new BackfillResult(0, 0, 0, 0, 0);
        }

        var overrides = new Dictionary<uint, SortedDictionary<byte, string>>();
        var rowsRead = 0;
        var rowsParsed = 0;
        foreach (var csvPath in csvPaths)
        {
            if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            {
                if (!string.IsNullOrWhiteSpace(csvPath))
                {
                    sink.Warn("DialogueTextBackfill", $"CSV not found: {csvPath}");
                }

                continue;
            }

            var (r, p) = LoadCsv(csvPath, overrides);
            rowsRead += r;
            rowsParsed += p;
        }

        if (overrides.Count == 0)
        {
            sink.Info("DialogueTextBackfill",
                $"No usable rows found in {csvPaths.Count} CSV file(s).");
            return new BackfillResult(rowsRead, rowsParsed, 0, 0, 0);
        }

        var infosTouched = 0;
        var filled = 0;
        var appended = 0;
        for (var i = 0; i < dialogues.Count; i++)
        {
            var info = dialogues[i];
            if (!overrides.TryGetValue(info.FormId, out var byResp))
            {
                continue;
            }

            var (next, f, a) = ApplyOverridesToInfo(info, byResp);
            if (next == info)
            {
                continue;
            }

            dialogues[i] = next;
            infosTouched++;
            filled += f;
            appended += a;
        }

        sink.Info("DialogueTextBackfill",
            $"Applied {filled:N0} response text fill(s) + appended {appended:N0} new response(s) " +
            $"across {infosTouched:N0} INFO(s) from {rowsParsed:N0}/{rowsRead:N0} CSV row(s).");

        return new BackfillResult(rowsRead, rowsParsed, infosTouched, filled, appended);
    }

    internal static (DialogueRecord Result, int Filled, int Appended) ApplyOverridesToInfo(
        DialogueRecord info,
        IReadOnlyDictionary<byte, string> byResponseNumber)
    {
        var existing = info.Responses;
        var maxRespNum = byResponseNumber.Keys.Max();
        var targetCount = Math.Max(existing.Count, maxRespNum);

        var changed = false;
        var filled = 0;
        var appended = 0;
        var next = new List<DialogueResponse>(targetCount);

        // Pass 1 — patch existing slots when the text is empty or the placeholder sentinel.
        for (var idx = 0; idx < existing.Count; idx++)
        {
            var resp = existing[idx];
            var respNum = resp.ResponseNumber > 0 ? resp.ResponseNumber : (byte)(idx + 1);

            if (NeedsBackfill(resp.Text)
                && byResponseNumber.TryGetValue(respNum, out var csvText)
                && !string.IsNullOrEmpty(csvText))
            {
                next.Add(resp with { Text = csvText, ResponseNumber = respNum });
                filled++;
                changed = true;
            }
            else
            {
                next.Add(resp);
            }
        }

        // Pass 2 — append responses that the CSV declares but the DMP never captured.
        for (var n = (byte)(existing.Count + 1); n <= maxRespNum; n++)
        {
            if (!byResponseNumber.TryGetValue(n, out var csvText) || string.IsNullOrEmpty(csvText))
            {
                continue;
            }

            next.Add(new DialogueResponse
            {
                Text = csvText,
                ResponseNumber = n
            });
            appended++;
            changed = true;
        }

        return changed
            ? (info with { Responses = next }, filled, appended)
            : (info, 0, 0);
    }

    private static bool NeedsBackfill(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
               || string.Equals(text, PlaceholderText, StringComparison.Ordinal);
    }

    private static (int RowsRead, int RowsParsed) LoadCsv(
        string csvPath,
        Dictionary<uint, SortedDictionary<byte, string>> overrides)
    {
        using var reader = new StreamReader(csvPath);
        var header = ReadCsvRecord(reader);
        if (header.Count == 0)
        {
            return (0, 0);
        }

        var fileCol = FindColumn(header, "File");
        var formIdCol = FindColumn(header, "FormID");
        var textCol = FindColumn(header, "Text");
        if (fileCol < 0 || formIdCol < 0 || textCol < 0)
        {
            return (0, 0);
        }

        var rowsRead = 0;
        var rowsParsed = 0;
        while (!reader.EndOfStream)
        {
            var fields = ReadCsvRecord(reader);
            if (fields.Count == 0)
            {
                continue;
            }

            rowsRead++;
            var maxIndex = Math.Max(Math.Max(fileCol, formIdCol), textCol);
            if (fields.Count <= maxIndex)
            {
                continue;
            }

            if (!TryParseFormId(fields[formIdCol], out var formId))
            {
                continue;
            }

            var respNum = ExtractResponseNumber(fields[fileCol]);
            if (respNum is null)
            {
                continue;
            }

            var text = fields[textCol];
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (!overrides.TryGetValue(formId, out var byResp))
            {
                byResp = new SortedDictionary<byte, string>();
                overrides[formId] = byResp;
            }

            // First non-empty Text wins per (FormID, ResponseNumber). Multiple CSVs can
            // contribute different snapshots; keep the earliest parse rather than the latest
            // so the priority order in the caller list is meaningful (first CSV wins).
            byResp.TryAdd(respNum.Value, text);
            rowsParsed++;
        }

        return (rowsRead, rowsParsed);
    }

    internal static byte? ExtractResponseNumber(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        var match = VoiceFileResponsePattern.Match(filePath);
        if (!match.Success)
        {
            return null;
        }

        if (!byte.TryParse(match.Groups["resp"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var resp))
        {
            return null;
        }

        return resp == 0 ? null : resp;
    }

    private static int FindColumn(List<string> header, string name)
    {
        for (var i = 0; i < header.Count; i++)
        {
            if (string.Equals(header[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryParseFormId(string raw, out uint formId)
    {
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[2..];
        }

        return uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }

    private static List<string> ReadCsvRecord(TextReader reader)
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
