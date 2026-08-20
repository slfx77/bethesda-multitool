using System.Globalization;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

/// <summary>
///     Backfills INFO response text (NAM1) from a Bethesda Audio Transcriber CSV when the
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

        var catalog = DialogueCsvCatalog.Load(csvPaths, sink);
        var overrides = new Dictionary<uint, SortedDictionary<byte, string>>();
        foreach (var row in catalog.SelectedRows)
        {
            if (string.IsNullOrEmpty(row.Text))
            {
                continue;
            }

            if (!overrides.TryGetValue(row.FormId, out var byResponse))
            {
                byResponse = new SortedDictionary<byte, string>();
                overrides[row.FormId] = byResponse;
            }

            byResponse[row.ResponseNumber] = row.Text;
        }

        if (overrides.Count == 0)
        {
            sink.Info("DialogueTextBackfill",
                $"No usable rows found in {csvPaths.Count} CSV file(s).");
            return new BackfillResult(catalog.RowsRead, catalog.RowsParsed, 0, 0, 0);
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
            $"across {infosTouched:N0} INFO(s) from {catalog.RowsParsed:N0}/{catalog.RowsRead:N0} CSV row(s).");

        return new BackfillResult(
            catalog.RowsRead, catalog.RowsParsed, infosTouched, filled, appended);
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

    /// <summary>Counts from a text-backfill pass: CSV rows read/parsed and INFO responses filled or appended.</summary>
    public sealed record BackfillResult(
        int RowsRead,
        int RowsParsed,
        int InfosTouched,
        int ResponsesFilled,
        int ResponsesAppended);
}
