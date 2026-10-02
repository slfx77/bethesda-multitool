using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

internal sealed record DialogueAudioPackingRequest(string RequestedPath, string PackPath);

internal sealed record DialogueAudioPackingRow(
    string CsvPath,
    int RowOrdinal,
    string FilePath,
    uint FormId,
    byte? ResponseNumber,
    string? ModelName,
    string? ModelSha256,
    string? ModelPath,
    string? ModelBytes,
    bool IsBound,
    string Disposition,
    IReadOnlyList<DialogueAudioPackingRequest> Requests);

/// <summary>Preserves selected CSV identities and the outcome of each requested audio/LIP asset.</summary>
internal sealed class DialogueAudioPackingAudit(IReadOnlyList<DialogueAudioPackingRow> rows)
{
    private readonly HashSet<string> _requested = rows.SelectMany(row => row.Requests)
        .Select(request => request.RequestedPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AssetOutcome> _outcomes = new(StringComparer.OrdinalIgnoreCase);

    public void Record(string requestedPath, DataFolderResolution resolution, string outcome,
        string? packedPath = null, byte[]? outputBytes = null, string? error = null)
    {
        if (!_requested.Contains(requestedPath))
        {
            return;
        }

        var sourceContainer = resolution.Source switch
        {
            LooseFileAssetSource loose => loose.AbsolutePath,
            BsaAssetSource bsa => bsa.ArchivePath,
            Ba2AssetSource ba2 => ba2.ArchivePath,
            _ => null
        };
        _outcomes[requestedPath] = new AssetOutcome(outcome, resolution.Kind.ToString(),
            resolution.ResolvedPath, resolution.Source?.NormalizedPath, sourceContainer,
            resolution.SourceFolderIndex, packedPath, outputBytes?.LongLength,
            outputBytes is null ? null : Convert.ToHexStringLower(SHA256.HashData(outputBytes)), error);
    }

    public void Write(string outputBsaPath, string status, IReadOnlyList<string>? outputPaths = null)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var path = Path.GetFullPath(outputBsaPath + ".dialogue-audio.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("status", status);
        writer.WriteStartArray("outputArchives");
        foreach (var output in outputPaths ?? [])
        {
            writer.WriteStringValue(output);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("rows");
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("csvPath", Path.GetFullPath(row.CsvPath));
            writer.WriteNumber("rowOrdinal", row.RowOrdinal);
            writer.WriteString("filePath", row.FilePath);
            writer.WriteString("formId", row.FormId.ToString("X8", CultureInfo.InvariantCulture));
            if (row.ResponseNumber.HasValue)
            {
                writer.WriteNumber("responseNumber", row.ResponseNumber.Value);
            }
            else
            {
                writer.WriteNull("responseNumber");
            }
            writer.WriteString("binding", row.IsBound ? "Bound" : "Unbound");
            writer.WriteString("disposition", row.Disposition);
            writer.WriteStartObject("transcriptionModel");
            writer.WriteString("name", row.ModelName);
            writer.WriteString("sha256", row.ModelSha256);
            writer.WriteString("path", row.ModelPath);
            writer.WriteString("bytes", row.ModelBytes);
            writer.WriteEndObject();
            writer.WriteStartArray("assets");
            foreach (var request in row.Requests)
            {
                writer.WriteStartObject();
                writer.WriteString("requestedPath", request.RequestedPath);
                writer.WriteString("plannedPackPath", request.PackPath);
                if (_outcomes.TryGetValue(request.RequestedPath, out var outcome))
                {
                    writer.WriteString("outcome", outcome.Status == "Prepared" && status == "Complete"
                        ? "Packed" : outcome.Status);
                    writer.WriteString("resolution", outcome.Resolution);
                    writer.WriteString("resolvedPath", outcome.ResolvedPath);
                    writer.WriteString("sourcePath", outcome.SourcePath);
                    writer.WriteString("sourceContainer", outcome.SourceContainer);
                    writer.WriteNumber("sourceFolderIndex", outcome.SourceFolderIndex);
                    writer.WriteString("packedPath", outcome.PackedPath);
                    if (outcome.Bytes.HasValue)
                    {
                        writer.WriteNumber("bytes", outcome.Bytes.Value);
                    }
                    writer.WriteString("sha256", outcome.Sha256);
                    writer.WriteString("error", outcome.Error);
                }
                else
                {
                    writer.WriteString("outcome", "Pending");
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private sealed record AssetOutcome(string Status, string Resolution, string? ResolvedPath,
        string? SourcePath, string? SourceContainer, int SourceFolderIndex, string? PackedPath,
        long? Bytes, string? Sha256, string? Error);
}
