using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>Durable request evidence, including paths omitted from the legacy resolution list.</summary>
internal static class AssetRequestAuditWriter
{
    internal static void TryWrite(AssetPackingOptions options, IReadOnlyList<AssetRequest> requests,
        IReadOnlyDictionary<string, DataFolderResolution> lookups, IReadOnlyList<AssetResolution> resolutions,
        IReadOnlyDictionary<string, string> preparedPaths, IReadOnlyList<string> completedArchives,
        string runStatus, IConversionProgressSink sink)
    {
        try
        {
            var path = options.OutputBsaPath + ".requests.json";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("schema", "bethesda-multitool/asset-requests");
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("createdUtc", DateTime.UtcNow);
            writer.WriteString("status", runStatus);
            writer.WriteString("collectionStatus", runStatus == "Complete" ? "completed" : "interrupted-or-unavailable");
            writer.WriteString("scope", "Collected source requests and packing observations; runtime requirements are unverified.");
            WriteInputs(writer, options);
            writer.WriteString("outputBsaPath", options.OutputBsaPath);
            writer.WriteStartArray("completedArchives");
            foreach (var archive in completedArchives) writer.WriteStringValue(archive);
            writer.WriteEndArray();
            var outcomes = resolutions.ToDictionary(r => r.RequestedPath, StringComparer.OrdinalIgnoreCase);
            writer.WriteStartArray("requests");
            foreach (var request in requests)
            {
                lookups.TryGetValue(request.Path, out var lookup);
                outcomes.TryGetValue(request.Path, out var outcome);
                preparedPaths.TryGetValue(request.Path, out var packedPath);
                WriteRequest(writer, request, lookup, outcome, packedPath, runStatus);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            sink.Info("AssetPacking", $"Request provenance written: {path}");
        }
        catch (Exception error)
        {
            sink.Warn("AssetPacking", $"Could not write request provenance: {error.GetType().Name}: {error.Message}");
        }
    }

    private static void WriteInputs(Utf8JsonWriter writer, AssetPackingOptions options)
    {
        writer.WriteStartObject("inputs");
        WritePath(writer, "convertedPlugin", options.ConvertedEsmPath);
        WritePath(writer, "dump", options.DmpPath);
        WritePath(writer, "baseline", options.BaselineDataFolder);
        writer.WriteStartArray("secondaries");
        for (var i = 0; i < options.SecondaryDataFolders.Count; i++)
        {
            writer.WriteStartObject();
            writer.WriteNumber("index", i);
            writer.WriteBoolean("xbox360", options.SecondaryDataFolders[i].IsXbox360Format);
            WritePath(writer, "source", options.SecondaryDataFolders[i].Path);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("dialogueCsv");
        foreach (var csv in options.DialogueAudioCsvPaths)
        {
            writer.WriteStartObject();
            WritePath(writer, "source", csv);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRequest(Utf8JsonWriter writer, AssetRequest request, DataFolderResolution? lookup,
        AssetResolution? outcome, string? packedPath, string runStatus)
    {
        writer.WriteStartObject();
        writer.WriteString("path", request.Path);
        writer.WriteString("requirement", "unknown");
        var assessment = lookup is null ? "request-not-evaluated; requirement unknown"
            : new AssetResolution { RequestedPath = request.Path, Kind = outcome?.Kind ?? lookup.Kind,
                RequestEvidence = request.Evidence }.RequestAssessment;
        writer.WriteString("assessment", assessment);
        WriteEvidence(writer, request.Evidence);
        WriteLookup(writer, lookup);
        WritePacking(writer, lookup, outcome, packedPath, runStatus);
        writer.WriteEndObject();
    }

    private static void WriteLookup(Utf8JsonWriter writer, DataFolderResolution? lookup)
    {
        writer.WriteStartObject("lookup");
        writer.WriteString("status", lookup is null ? "not-observed" : "observed");
        writer.WriteString("kind", lookup?.Kind.ToString());
        writer.WriteString("resolvedPath", lookup?.ResolvedPath);
        if (lookup is not null && lookup.Kind != AssetResolutionKind.Missing)
            writer.WriteNumber("sourceFolderIndex", lookup.SourceFolderIndex);
        else writer.WriteNull("sourceFolderIndex");
        writer.WriteString("reason", lookup?.UnresolvedReason);
        writer.WriteStartArray("unverifiedAlternatives");
        foreach (var alternative in lookup?.UnverifiedCandidates ?? [])
        {
            writer.WriteStartObject();
            writer.WriteString("path", alternative.Path);
            writer.WriteNumber("sourceFolderIndex", alternative.SourceFolderIndex);
            writer.WriteString("reason", alternative.Reason);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WritePacking(Utf8JsonWriter writer, DataFolderResolution? lookup,
        AssetResolution? outcome, string? packedPath, string runStatus)
    {
        writer.WriteStartObject("packing");
        var packingStatus = "not-observed";
        if (lookup?.Kind == AssetResolutionKind.AlreadyInBaseline) packingStatus = "skipped-baseline";
        else if (packedPath is not null) packingStatus = runStatus == "Complete" ? "written" : "prepared";
        else if (outcome?.Kind == AssetResolutionKind.Missing) packingStatus = "not-packed";
        else if (outcome?.Kind == AssetResolutionKind.ConversionFailed) packingStatus = "conversion-failed";
        writer.WriteString("status", packingStatus);
        writer.WriteString("path", packedPath);
        writer.WriteString("kind", outcome?.Kind.ToString());
        writer.WriteString("reason", outcome?.UnresolvedReason);
        writer.WriteString("conversionError", outcome?.ConversionError);
        writer.WriteEndObject();
    }

    private static void WritePath(Utf8JsonWriter writer, string name, string? path)
    {
        writer.WriteStartObject(name);
        writer.WriteString("path", path);
        writer.WriteString("identity", path is null ? "not-supplied" : "path-only");
        writer.WriteNull("sha256");
        writer.WriteString("hashStatus", "not-computed");
        writer.WriteEndObject();
    }

    private static void WriteEvidence(Utf8JsonWriter writer, IReadOnlyList<AssetRequestEvidence> evidence)
    {
        writer.WriteStartArray("evidence");
        foreach (var reason in evidence)
        {
            writer.WriteStartObject();
            writer.WriteString("basis", reason.Basis);
            if (reason.OwnerFormId is { } owner) writer.WriteNumber("ownerFormId", owner);
            else writer.WriteNull("ownerFormId");
            writer.WriteString("ownerType", reason.OwnerType);
            writer.WriteString("field", reason.Field);
            writer.WriteString("parentPath", reason.ParentPath);
            writer.WriteString("parentBasis", reason.ParentBasis);
            if (reason.SourceOwnerFormId is { } sourceOwner) writer.WriteNumber("sourceOwnerFormId", sourceOwner);
            else writer.WriteNull("sourceOwnerFormId");
            writer.WriteString("sourcePath", reason.SourcePath);
            writer.WriteString("selectedParentPath", reason.SelectedParentPath);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
