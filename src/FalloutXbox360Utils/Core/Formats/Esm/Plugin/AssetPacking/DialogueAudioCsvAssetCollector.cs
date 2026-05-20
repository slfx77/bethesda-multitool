using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Semantic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

internal sealed record DialogueAudioCsvCollectionResult(
    IReadOnlySet<string> Paths,
    int CsvFilesRead,
    int RowsRead,
    int RowsMatched,
    int PathsAdded);

/// <summary>
///     Imports Fallout Audio Transcriber CSV rows as dialogue voice asset requests.
///     The CSV carries prototype INFO FormIDs and concrete voice file paths; the converted
///     ESP may carry newly allocated INFO IDs, so source-DMP INFO IDs are included when
///     a DMP path is available.
/// </summary>
internal static class DialogueAudioCsvAssetCollector
{
    public static async Task<DialogueAudioCsvCollectionResult> CollectAsync(
        RecordCollection convertedRecords,
        string? dmpPath,
        IReadOnlyList<string> csvPaths,
        IConversionProgressSink sink,
        CancellationToken cancellationToken)
    {
        if (csvPaths.Count == 0)
        {
            return Empty;
        }

        var dialogueFormIds = BuildDialogueFormIdSet(convertedRecords);
        var convertedInfoCount = dialogueFormIds.Count;
        var dmpInfoCount = 0;

        if (!string.IsNullOrWhiteSpace(dmpPath) && File.Exists(dmpPath))
        {
            try
            {
                using var dmpResult = await SemanticFileLoader
                    .LoadAsync(
                        dmpPath,
                        new SemanticFileLoadOptions
                        {
                            FileType = AnalysisFileType.Minidump,
                            ApplyDefaultCellWorldspaceAuthority = false
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var info in dmpResult.Records.Dialogues)
                {
                    if (info.FormId != 0 && dialogueFormIds.Add(info.FormId))
                    {
                        dmpInfoCount++;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sink.Warn("AssetCollect",
                    $"Dialogue audio CSV matching could not load source DMP FormIDs: {ex.Message}");
            }
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var csvFilesRead = 0;
        var rowsRead = 0;
        var rowsMatched = 0;

        foreach (var csvPath in csvPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(csvPath))
            {
                continue;
            }

            if (!File.Exists(csvPath))
            {
                sink.Warn("AssetCollect", $"Dialogue audio CSV not found: {csvPath}");
                continue;
            }

            var result = CollectFromCsv(csvPath, dialogueFormIds, paths);
            csvFilesRead++;
            rowsRead += result.RowsRead;
            rowsMatched += result.RowsMatched;
        }

        sink.Info("AssetCollect",
            $"Dialogue audio CSV contributed {paths.Count:N0} asset path(s) from " +
            $"{rowsMatched:N0}/{rowsRead:N0} matched row(s) " +
            $"({convertedInfoCount:N0} ESP INFO IDs, {dmpInfoCount:N0} source-DMP-only INFO IDs).");

        return new DialogueAudioCsvCollectionResult(
            paths,
            csvFilesRead,
            rowsRead,
            rowsMatched,
            paths.Count);
    }

    internal static DialogueAudioCsvCollectionResult CollectFromCsv(
        string csvPath,
        IReadOnlySet<uint> dialogueFormIds,
        HashSet<string> paths)
    {
        using var reader = new StreamReader(csvPath);
        var headerFields = ReadCsvRecord(reader);
        if (headerFields.Count == 0)
        {
            return new DialogueAudioCsvCollectionResult(paths, 1, 0, 0, 0);
        }

        var fileIndex = FindColumn(headerFields, "File");
        var formIdIndex = FindColumn(headerFields, "FormID");
        if (fileIndex < 0 || formIdIndex < 0)
        {
            return new DialogueAudioCsvCollectionResult(paths, 1, 0, 0, 0);
        }

        var rowsRead = 0;
        var rowsMatched = 0;
        var initialPathCount = paths.Count;

        while (!reader.EndOfStream)
        {
            var fields = ReadCsvRecord(reader);
            if (fields.Count == 0)
            {
                continue;
            }

            rowsRead++;
            if (fields.Count <= Math.Max(fileIndex, formIdIndex))
            {
                continue;
            }

            if (!TryParseFormId(fields[formIdIndex], out var formId) ||
                !dialogueFormIds.Contains(formId))
            {
                continue;
            }

            var filePath = fields[fileIndex];
            if (string.IsNullOrWhiteSpace(filePath))
            {
                continue;
            }

            var matchedPath = false;
            foreach (var requestPath in ExpandDialogueAudioRequests(filePath))
            {
                matchedPath |= paths.Add(requestPath);
            }

            if (matchedPath)
            {
                rowsMatched++;
            }
        }

        return new DialogueAudioCsvCollectionResult(
            paths,
            1,
            rowsRead,
            rowsMatched,
            paths.Count - initialPathCount);
    }

    private static HashSet<uint> BuildDialogueFormIdSet(RecordCollection records)
    {
        var result = new HashSet<uint>();
        foreach (var info in records.Dialogues)
        {
            if (info.FormId != 0)
            {
                result.Add(info.FormId);
            }
        }

        return result;
    }

    private static IEnumerable<string> ExpandDialogueAudioRequests(string filePath)
    {
        var normalized = AssetPathCollector.TryNormalizeRequestPath(filePath);
        if (normalized is null ||
            !normalized.StartsWith("sound\\voice\\", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var ext = Path.GetExtension(normalized);
        if (ext.Equals(".lip", StringComparison.OrdinalIgnoreCase))
        {
            yield return normalized;
            yield break;
        }

        // PC FNV voice playback expects paired OGG audio and LIP sync assets. April CSV
        // rows usually name Xbox XMA files, so request the runtime OGG path and let
        // resolution extension-swap back to XMA in a secondary 360 source.
        yield return Path.ChangeExtension(normalized, ".ogg");
        yield return Path.ChangeExtension(normalized, ".lip");
    }

    private static int FindColumn(List<string> headerFields, string name)
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

    private static bool TryParseFormId(string raw, out uint formId)
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

    private static DialogueAudioCsvCollectionResult Empty { get; } =
        new(new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, 0, 0, 0);
}
