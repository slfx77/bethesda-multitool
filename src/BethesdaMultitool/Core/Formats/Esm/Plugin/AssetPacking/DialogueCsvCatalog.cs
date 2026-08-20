using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>One selected Audio Transcriber row and its stable source ordering.</summary>
internal sealed record DialogueCsvRow(
    string CsvPath,
    int CsvOrder,
    int RowOrder,
    string FilePath,
    uint FormId,
    byte ResponseNumber,
    string Source,
    string Text);

/// <summary>
///     Shared deterministic CSV selection used by both NAM1 backfill and voice packing.
///     One row wins per (INFO FormID, response): a temp-prefixed voice filename first,
///     then configured CSV order, then row order. This keeps subtitle and audio provenance
///     aligned instead of independently choosing different build-era rows.
/// </summary>
internal sealed class DialogueCsvCatalog
{
    private DialogueCsvCatalog(
        IReadOnlyList<DialogueCsvRow> selectedRows,
        IReadOnlyList<DialogueCsvRow> selectedAudioRows,
        IReadOnlyDictionary<string, IReadOnlySet<int>> selectedRowOrdinalsByCsvPath,
        int rowsRead,
        int rowsParsed,
        int rowsQuarantined)
    {
        SelectedRows = selectedRows;
        SelectedAudioRows = selectedAudioRows;
        SelectedRowOrdinalsByCsvPath = selectedRowOrdinalsByCsvPath;
        RowsRead = rowsRead;
        RowsParsed = rowsParsed;
        RowsQuarantined = rowsQuarantined;
    }

    public IReadOnlyList<DialogueCsvRow> SelectedRows { get; }
    public IReadOnlyList<DialogueCsvRow> SelectedAudioRows { get; }
    public IReadOnlyDictionary<string, IReadOnlySet<int>> SelectedRowOrdinalsByCsvPath { get; }
    public int RowsRead { get; }
    public int RowsParsed { get; }
    public int RowsQuarantined { get; }

    public static DialogueCsvCatalog Load(
        IReadOnlyList<string> csvPaths,
        IConversionProgressSink? sink = null,
        IReadOnlyDictionary<(uint FormId, byte ResponseNumber), string>? preferredTexts = null)
    {
        var candidates = new List<DialogueCsvRow>();
        var rowsRead = 0;
        var rowsParsed = 0;
        var rowsQuarantined = 0;
        for (var csvOrder = 0; csvOrder < csvPaths.Count; csvOrder++)
        {
            var csvPath = csvPaths[csvOrder];
            if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath))
            {
                if (!string.IsNullOrWhiteSpace(csvPath))
                {
                    sink?.Warn("DialogueCsvCatalog", $"CSV not found: {csvPath}");
                }

                continue;
            }

            using var reader = new StreamReader(csvPath);
            var header = DialogueAudioCsvReader.ReadCsvRecord(reader);
            var fileColumn = DialogueAudioCsvReader.FindColumn(header, "File");
            var formIdColumn = DialogueAudioCsvReader.FindColumn(header, "FormID");
            var sourceColumn = DialogueAudioCsvReader.FindColumn(header, "Source");
            var textColumn = DialogueAudioCsvReader.FindColumn(header, "Text");
            if (fileColumn < 0 || formIdColumn < 0)
            {
                continue;
            }

            var rowOrder = 0;
            while (!reader.EndOfStream)
            {
                var fields = DialogueAudioCsvReader.ReadCsvRecord(reader);
                if (fields.Count == 0)
                {
                    continue;
                }

                rowOrder++;
                rowsRead++;
                if (fields.Count <= Math.Max(fileColumn, formIdColumn)
                    || !DialogueAudioCsvReader.TryParseFormId(fields[formIdColumn], out var formId))
                {
                    continue;
                }

                var filePath = fields[fileColumn];
                var responseNumber = DialogueTextBackfill.ExtractResponseNumber(filePath);
                if (responseNumber is null)
                {
                    continue;
                }

                var text = textColumn >= 0 && textColumn < fields.Count
                    ? fields[textColumn]
                    : string.Empty;
                var source = sourceColumn >= 0 && sourceColumn < fields.Count
                    ? fields[sourceColumn]
                    : string.Empty;
                rowsParsed++;

                // Audio Transcriber CSVs written before the response-aware ESM index fix
                // attached INFO's first NAM1 to every voice file sharing that FormID. The
                // Source=esm rows for suffixes _2, _3, ... are therefore not independent
                // response text: in the July corpus all 6,818 such rows repeat response 1.
                // Quarantine them from BOTH subtitle and audio selection. For retail INFO
                // overlays this leaves the unmatched master NAM1 and voice asset intact;
                // trusted per-file Whisper/accepted rows remain eligible at every slot.
                if (responseNumber.Value > 1
                    && string.Equals(source.Trim(), "esm", StringComparison.OrdinalIgnoreCase))
                {
                    rowsQuarantined++;
                    continue;
                }

                candidates.Add(new DialogueCsvRow(
                    csvPath, csvOrder, rowOrder, filePath, formId, responseNumber.Value, source, text));
            }
        }

        if (rowsQuarantined > 0)
        {
            sink?.Warn(
                "DialogueCsvCatalog",
                $"Quarantined {rowsQuarantined:N0} legacy Source=esm row(s) for response slots above 1; " +
                "those exports repeat INFO response 1 and cannot safely drive subtitles or audio.");
        }

        var selected = new List<DialogueCsvRow>();
        var selectedAudio = new List<DialogueCsvRow>();
        foreach (var group in candidates.GroupBy(static row => (row.FormId, row.ResponseNumber)))
        {
            var groupRows = group.ToList();
            var selectionPool = groupRows;
            var preferredText = string.Empty;
            var preferredWasRequested = preferredTexts is not null
                                        && preferredTexts.TryGetValue(group.Key, out preferredText)
                                        && !string.IsNullOrWhiteSpace(preferredText);
            if (preferredWasRequested)
            {
                var normalizedPreferred = DialogueAudioTextMatcher.NormalizeText(preferredText ?? string.Empty);
                selectionPool = groupRows
                    .Where(row => string.Equals(
                        DialogueAudioTextMatcher.NormalizeText(row.Text),
                        normalizedPreferred,
                        StringComparison.Ordinal))
                    .ToList();
            }

            // Direct captured text is authoritative. If no CSV row carries that text, do
            // not select unrelated audio; retail overlays intentionally fall back to the
            // master voice asset and new dialogue emits a missing-audio diagnostic.
            if (selectionPool.Count == 0)
            {
                selected.Add(Rank(groupRows).First());
                continue;
            }

            var winner = Rank(selectionPool).First();
            selected.Add(winner);

            // A generic INFO commonly has one file per voice type. Preserve every variant
            // from the winning CSV/source bucket whose text is identical to the subtitle;
            // collapsing the slot to the first row would silently discard most voices.
            var winnerIsTemp = IsTempVoiceFile(winner.FilePath);
            var normalizedWinnerText = DialogueAudioTextMatcher.NormalizeText(winner.Text);
            selectedAudio.AddRange(selectionPool.Where(row =>
                row.CsvOrder == winner.CsvOrder
                && IsTempVoiceFile(row.FilePath) == winnerIsTemp
                && string.Equals(
                    DialogueAudioTextMatcher.NormalizeText(row.Text),
                    normalizedWinnerText,
                    StringComparison.Ordinal)));
        }

        selected = selected
            .OrderBy(static row => row.CsvOrder)
            .ThenBy(static row => row.RowOrder)
            .ToList();
        selectedAudio = selectedAudio
            .OrderBy(static row => row.CsvOrder)
            .ThenBy(static row => row.RowOrder)
            .ToList();
        var ordinals = selectedAudio
            .GroupBy(static row => row.CsvPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlySet<int>)group.Select(static row => row.RowOrder).ToHashSet(),
                StringComparer.OrdinalIgnoreCase);
        return new DialogueCsvCatalog(
            selected, selectedAudio, ordinals, rowsRead, rowsParsed, rowsQuarantined);
    }

    private static IOrderedEnumerable<DialogueCsvRow> Rank(IEnumerable<DialogueCsvRow> rows)
    {
        return rows.OrderByDescending(static row => IsTempVoiceFile(row.FilePath))
            .ThenBy(static row => row.CsvOrder)
            .ThenBy(static row => row.RowOrder);
    }

    private static bool IsTempVoiceFile(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        return stem.StartsWith("temp", StringComparison.OrdinalIgnoreCase);
    }
}
