using System.Text;
using System.Text.Json;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Builds compressed JSON blobs for comparison HTML pages.
///     Handles delta encoding, sparse dump detection, and record serialization.
/// </summary>
internal static class ComparisonJsonBlobBuilder
{
    private static readonly JsonWriterOptions CompactJsonOptions = new() { Indented = false };

    /// <summary>
    ///     Field keys that carry no semantic information for cross-build comparison
    ///     (file/memory positions, etc.) and would otherwise generate spurious CHANGED
    ///     marks. Stripped from RecordReports before delta-encoding and JSON emission.
    /// </summary>
    private static readonly HashSet<string> ExcludedFieldKeys =
        new(StringComparer.Ordinal) { "Offset" };

    /// <summary>
    ///     Returns a copy of the report with comparison-irrelevant fields removed.
    ///     Sections that become empty after filtering are dropped. Cheap — runs once
    ///     per (record, dump) pair just before delta encoding.
    /// </summary>
    private static RecordReport StripExcludedFields(RecordReport report)
    {
        var hasAny = false;
        foreach (var section in report.Sections)
        {
            foreach (var field in section.Fields)
            {
                if (ExcludedFieldKeys.Contains(field.Key))
                {
                    hasAny = true;
                    break;
                }
            }

            if (hasAny) break;
        }

        if (!hasAny) return report;

        var newSections = new List<ReportSection>(report.Sections.Count);
        foreach (var section in report.Sections)
        {
            var keptFields = new List<ReportField>(section.Fields.Count);
            foreach (var field in section.Fields)
            {
                if (!ExcludedFieldKeys.Contains(field.Key)) keptFields.Add(field);
            }

            if (keptFields.Count > 0)
                newSections.Add(new ReportSection(section.Name, keptFields));
        }

        return new RecordReport(
            report.RecordType, report.FormId, report.EditorId, report.DisplayName, newSections);
    }

    private static string? NormalizeHistoryName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return IsSyntheticVirtualLabel(trimmed) ? null : trimmed;
    }

    private static bool IsSyntheticVirtualLabel(string value)
    {
        return value.StartsWith("[Virtual ", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("Virtual ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Build a JSON blob for a record type page with full options:
    ///     record type tag, dual group sets, per-record metadata, and grid coordinates.
    /// </summary>
    internal static string Build(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        string recordType,
        Dictionary<uint, string>? groups,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        Dictionary<uint, (int X, int Y)>? cellGridCoords)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
        {
            writer.WriteStartObject();

            // Record type tag (allows JS to detect dialogue pages without sniffing metadata)
            writer.WriteString("recordType", recordType);

            // Dumps array
            writer.WritePropertyName("dumps");
            writer.WriteStartArray();
            foreach (var d in dumps)
            {
                writer.WriteStartObject();
                writer.WriteString("fileName", d.FileName);
                writer.WriteString("date", d.FileDate.ToString("o"));
                writer.WriteString("dateSource", d.DateSource);
                writer.WriteString("shortName", d.ShortName);
                writer.WriteBoolean("isDmp", d.IsDmp);
                writer.WriteBoolean("isBase", d.IsBase);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            // Detect sparse dumps for this record type (O(N) pre-scan instead of O(N*M))
            writer.WritePropertyName("sparseDumps");
            writer.WriteStartArray();

            var presentDumps = new HashSet<int>();
            foreach (var dm in formIdMap.Values)
            {
                foreach (var k in dm.Keys)
                {
                    presentDumps.Add(k);
                }
            }

            for (var dumpIdx = 0; dumpIdx < dumps.Count; dumpIdx++)
            {
                if (!presentDumps.Contains(dumpIdx))
                    writer.WriteNumberValue(dumpIdx);
            }

            writer.WriteEndArray();

            // Records: keyed by FormID hex string
            writer.WritePropertyName("records");
            writer.WriteStartObject();
            foreach (var (formId, dumpMap) in formIdMap)
            {
                writer.WritePropertyName($"0x{formId:X8}");
                writer.WriteStartObject();

                // EditorId and DisplayName: use the latest real value. Walking
                // descending + ??= picks the newest non-null, so a name added in a
                // late dump wins over an earlier-dump fallback. Walking ascending
                // (the previous code) picked the earliest non-null, contradicting
                // this comment and hiding the most recently observed name.
                string? editorId = null;
                string? displayName = null;
                foreach (var report in dumpMap.OrderByDescending(kvp => kvp.Key).Select(kvp => kvp.Value))
                {
                    editorId ??= NormalizeHistoryName(report.EditorId);
                    displayName ??= NormalizeHistoryName(report.DisplayName);
                }

                if (editorId != null)
                    writer.WriteString("editorId", editorId);
                else
                    writer.WriteNull("editorId");

                if (displayName != null)
                    writer.WriteString("displayName", displayName);
                else
                    writer.WriteNull("displayName");

                // All distinct EditorIDs and display names across builds (for name-change tracking)
                var allEditorIds = dumpMap.OrderBy(kvp => kvp.Key)
                    .Select(kvp => kvp.Value.EditorId)
                    .Select(NormalizeHistoryName)
                    .Where(e => e != null)
                    .Distinct()
                    .ToList();
                if (allEditorIds.Count > 1)
                {
                    writer.WritePropertyName("editorIdHistory");
                    writer.WriteStartArray();
                    foreach (var eid in allEditorIds) writer.WriteStringValue(eid);
                    writer.WriteEndArray();
                }

                var allNames = dumpMap.OrderBy(kvp => kvp.Key)
                    .Select(kvp => kvp.Value.DisplayName)
                    .Select(NormalizeHistoryName)
                    .Where(n => n != null)
                    .Distinct()
                    .ToList();
                if (allNames.Count > 1)
                {
                    writer.WritePropertyName("nameHistory");
                    writer.WriteStartArray();
                    foreach (var name in allNames) writer.WriteStringValue(name);
                    writer.WriteEndArray();
                }

                // Per-record metadata (quest, topic, speaker for dialogue)
                Dictionary<string, string>? meta = null;
                metadata?.TryGetValue(formId, out meta);
                if (meta is { Count: > 0 })
                {
                    writer.WritePropertyName("metadata");
                    writer.WriteStartObject();
                    foreach (var (key, value) in meta)
                        writer.WriteString(key, value);
                    writer.WriteEndObject();
                }

                WriteSearchText(writer, dumpMap, meta);

                // Snapshots: delta-encoded -- store only when report structurally differs from previous.
                // Strip comparison-irrelevant fields (Offset, etc.) so they don't drive false CHANGED marks.
                writer.WritePropertyName("snapshots");
                writer.WriteStartObject();
                RecordReport? previousReport = null;
                foreach (var (dumpIdx, rawReport) in dumpMap.OrderBy(kvp => kvp.Key))
                {
                    var report = StripExcludedFields(rawReport);
                    if (previousReport == null || !RecordReportComparer.Equals(previousReport, report))
                    {
                        writer.WritePropertyName(dumpIdx.ToString());
                        ReportJsonFormatter.WriteReport(writer, report);
                    }

                    previousReport = report;
                }

                writer.WriteEndObject();

                // Per-dump presence bitmap (for badge computation when snapshot is omitted)
                writer.WritePropertyName("present");
                writer.WriteStartArray();
                foreach (var dumpIdx in dumpMap.Keys.OrderBy(k => k))
                    writer.WriteNumberValue(dumpIdx);
                writer.WriteEndArray();

                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            // Group sets (dual grouping for dialogue: Quest + NPC)
            if (alternateGroups != null && defaultGroupMode != null && groups is { Count: > 0 })
            {
                writer.WritePropertyName("groupSets");
                writer.WriteStartObject();

                writer.WritePropertyName(defaultGroupMode);
                writer.WriteStartObject();
                foreach (var (formId, group) in groups)
                    writer.WriteString($"0x{formId:X8}", group);
                writer.WriteEndObject();

                var altMode = defaultGroupMode == "Quest" ? "NPC" : "Alt";
                writer.WritePropertyName(altMode);
                writer.WriteStartObject();
                foreach (var (formId, group) in alternateGroups)
                    writer.WriteString($"0x{formId:X8}", group);
                writer.WriteEndObject();

                writer.WriteEndObject();
                writer.WriteString("defaultGroupMode", defaultGroupMode);
            }
            // Single group map (cells, etc.)
            else if (groups is { Count: > 0 })
            {
                writer.WritePropertyName("groups");
                writer.WriteStartObject();
                foreach (var (formId, group) in groups)
                    writer.WriteString($"0x{formId:X8}", group);
                writer.WriteEndObject();
            }

            // Grid coordinates
            if (cellGridCoords is { Count: > 0 })
            {
                writer.WritePropertyName("gridCoords");
                writer.WriteStartObject();
                foreach (var (formId, (x, y)) in cellGridCoords)
                {
                    writer.WritePropertyName($"0x{formId:X8}");
                    writer.WriteStartArray();
                    writer.WriteNumberValue(x);
                    writer.WriteNumberValue(y);
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    ///     Build compressed chunked JSON blobs for grouped pages (cells).
    ///     Returns a compressed index payload + compressed per-group chunk payloads.
    ///     Groups exceeding the limit are split into sub-chunks.
    /// </summary>
    internal static (string CompressedIndexPayload, List<(string GroupKey, string CompressedPayload)> Chunks)
        BuildChunked(
            Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
            List<DumpSnapshot> dumps,
            string recordType,
            Dictionary<uint, string>? groups,
            Dictionary<uint, (int X, int Y)>? cellGridCoords,
            Dictionary<uint, Dictionary<string, string>>? metadata,
            long maxChunkBytes = 5 * 1024 * 1024)
    {
        var compressedIndexPayload = BuildChunkedIndex(
            formIdMap,
            dumps,
            recordType,
            groups,
            cellGridCoords);
        var chunks = BuildChunkPayloads(
                formIdMap,
                recordType,
                groups,
                metadata,
                maxChunkBytes)
            .ToList();

        return (compressedIndexPayload, chunks);
    }

    internal static string BuildChunkedIndex(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        string recordType,
        Dictionary<uint, string>? groups,
        Dictionary<uint, (int X, int Y)>? cellGridCoords)
    {
        var groupCounts = CountRecordsByGroup(formIdMap, groups);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("recordType", recordType);
            writer.WriteBoolean("chunked", true);

            WriteDumpsArray(writer, dumps);
            WriteSparseDumps(writer, formIdMap, dumps);

            // Group manifest: group name -> record count.
            writer.WritePropertyName("groupManifest");
            writer.WriteStartObject();
            foreach (var groupKey in OrderGroupKeys(groupCounts))
            {
                writer.WritePropertyName(groupKey);
                writer.WriteNumberValue(groupCounts[groupKey]);
            }

            writer.WriteEndObject();

            if (groups is { Count: > 0 })
            {
                writer.WritePropertyName("groups");
                writer.WriteStartObject();
                foreach (var (formId, group) in groups)
                    writer.WriteString($"0x{formId:X8}", group);
                writer.WriteEndObject();
            }

            if (cellGridCoords is { Count: > 0 })
            {
                writer.WritePropertyName("gridCoords");
                writer.WriteStartObject();
                foreach (var (formId, (x, y)) in cellGridCoords)
                {
                    writer.WritePropertyName($"0x{formId:X8}");
                    writer.WriteStartArray();
                    writer.WriteNumberValue(x);
                    writer.WriteNumberValue(y);
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return CompressWrittenJsonToBase64(ms);
    }

    internal static IEnumerable<(string GroupKey, string CompressedPayload)> BuildChunkPayloads(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        string recordType,
        Dictionary<uint, string>? groups,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        long maxChunkBytes = 5 * 1024 * 1024)
    {
        _ = recordType;

        var groupCounts = CountRecordsByGroup(formIdMap, groups);
        foreach (var groupKey in OrderGroupKeys(groupCounts))
        {
            var groupRecordCount = groupCounts[groupKey];
            var recordsWrittenForGroup = 0;
            var recordsInCurrentChunk = 0;
            var partNum = 1;
            var currentChunk = CreateChunkJsonStream();

            foreach (var (formId, dumpMap) in formIdMap)
            {
                if (!string.Equals(ResolveGroupKey(groups, formId), groupKey, StringComparison.Ordinal))
                {
                    continue;
                }

                using var singleRecordJson = BuildSingleRecordChunkBytes(formId, dumpMap, metadata);
                var entryByteCount = Math.Max(0, singleRecordJson.Length - 2);
                var commaByteCount = recordsInCurrentChunk == 0 ? 0 : 1;
                var projectedChunkBytes = currentChunk.Length + commaByteCount + entryByteCount + 1;

                if (recordsInCurrentChunk > 0 && projectedChunkBytes > maxChunkBytes)
                {
                    var hasMoreRecords = recordsWrittenForGroup < groupRecordCount;
                    yield return (
                        FormatChunkLabel(groupKey, partNum, hasMoreRecords),
                        FinishAndCompressChunk(currentChunk));
                    currentChunk.Dispose();

                    partNum++;
                    recordsInCurrentChunk = 0;
                    currentChunk = CreateChunkJsonStream();
                }

                AppendSingleRecordToChunk(currentChunk, singleRecordJson, recordsInCurrentChunk == 0);
                recordsInCurrentChunk++;
                recordsWrittenForGroup++;
            }

            if (recordsInCurrentChunk > 0)
            {
                yield return (
                    FormatChunkLabel(groupKey, partNum, false),
                    FinishAndCompressChunk(currentChunk));
            }

            currentChunk.Dispose();
        }
    }

    private static Dictionary<string, int> CountRecordsByGroup(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        Dictionary<uint, string>? groups)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var formId in formIdMap.Keys)
        {
            var groupKey = ResolveGroupKey(groups, formId);
            counts[groupKey] = counts.TryGetValue(groupKey, out var count) ? count + 1 : 1;
        }

        return counts;
    }

    private static IEnumerable<string> OrderGroupKeys(Dictionary<string, int> groupCounts)
    {
        return groupCounts.Keys
            .OrderBy(groupKey => groupKey == "Interior Cells" ? 1 : 0)
            .ThenBy(groupKey => groupKey, StringComparer.Ordinal);
    }

    private static string ResolveGroupKey(Dictionary<uint, string>? groups, uint formId)
    {
        return groups != null && groups.TryGetValue(formId, out var group) ? group : "(Ungrouped)";
    }

    private static string FormatChunkLabel(string groupKey, int partNum, bool hasMoreRecords)
    {
        return hasMoreRecords || partNum > 1 ? $"{groupKey} (part {partNum})" : groupKey;
    }

    private static MemoryStream CreateChunkJsonStream()
    {
        var stream = new MemoryStream();
        stream.WriteByte((byte)'{');
        return stream;
    }

    private static string FinishAndCompressChunk(MemoryStream chunkStream)
    {
        chunkStream.WriteByte((byte)'}');
        return CompressWrittenJsonToBase64(chunkStream);
    }

    private static void AppendSingleRecordToChunk(
        MemoryStream chunkStream,
        MemoryStream singleRecordJson,
        bool isFirstRecord)
    {
        if (!isFirstRecord)
        {
            chunkStream.WriteByte((byte)',');
        }

        if (singleRecordJson.Length <= 2)
        {
            return;
        }

        if (singleRecordJson.TryGetBuffer(out var buffer))
        {
            chunkStream.Write(
                buffer.Array!,
                buffer.Offset + 1,
                (int)singleRecordJson.Length - 2);
            return;
        }

        var bytes = singleRecordJson.ToArray();
        chunkStream.Write(bytes, 1, bytes.Length - 2);
    }

    private static MemoryStream BuildSingleRecordChunkBytes(
        uint formId,
        Dictionary<int, RecordReport> dumpMap,
        Dictionary<uint, Dictionary<string, string>>? metadata)
    {
        var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
        {
            writer.WriteStartObject();
            writer.WritePropertyName($"0x{formId:X8}");
            WriteRecordEntry(writer, formId, dumpMap, metadata);
            writer.WriteEndObject();
        }

        return ms;
    }

    private static string CompressWrittenJsonToBase64(MemoryStream jsonStream)
    {
        if (jsonStream.TryGetBuffer(out var buffer))
        {
            return ComparisonHtmlHelpers.CompressToBase64(
                new ReadOnlySpan<byte>(buffer.Array!, buffer.Offset, (int)jsonStream.Length));
        }

        return ComparisonHtmlHelpers.CompressToBase64(jsonStream.ToArray());
    }

    /// <summary>Write a single record entry (shared by Build and BuildChunked).</summary>
    private static void WriteRecordEntry(
        Utf8JsonWriter writer, uint formId,
        Dictionary<int, RecordReport> dumpMap,
        Dictionary<uint, Dictionary<string, string>>? metadata)
    {
        writer.WriteStartObject();

        // Mirror Build(): walk descending so the LATEST non-null wins.
        string? editorId = null;
        string? displayName = null;
        foreach (var report in dumpMap.OrderByDescending(kvp => kvp.Key).Select(kvp => kvp.Value))
        {
            editorId ??= NormalizeHistoryName(report.EditorId);
            displayName ??= NormalizeHistoryName(report.DisplayName);
        }

        writer.WriteString("editorId", editorId);
        writer.WriteString("displayName", displayName);

        var allEditorIds = dumpMap.OrderBy(kvp => kvp.Key)
            .Select(kvp => NormalizeHistoryName(kvp.Value.EditorId)).Where(e => e != null).Distinct().ToList();
        if (allEditorIds.Count > 1)
        {
            writer.WritePropertyName("editorIdHistory");
            writer.WriteStartArray();
            foreach (var eid in allEditorIds) writer.WriteStringValue(eid);
            writer.WriteEndArray();
        }

        var allNames = dumpMap.OrderBy(kvp => kvp.Key)
            .Select(kvp => NormalizeHistoryName(kvp.Value.DisplayName)).Where(n => n != null).Distinct().ToList();
        if (allNames.Count > 1)
        {
            writer.WritePropertyName("nameHistory");
            writer.WriteStartArray();
            foreach (var name in allNames) writer.WriteStringValue(name);
            writer.WriteEndArray();
        }

        Dictionary<string, string>? meta = null;
        metadata?.TryGetValue(formId, out meta);
        if (meta is { Count: > 0 })
        {
            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            foreach (var (key, value) in meta)
                writer.WriteString(key, value);
            writer.WriteEndObject();
        }

        WriteSearchText(writer, dumpMap, meta);

        writer.WritePropertyName("snapshots");
        writer.WriteStartObject();
        RecordReport? previousReport = null;
        foreach (var (dumpIdx, rawReport) in dumpMap.OrderBy(kvp => kvp.Key))
        {
            var report = StripExcludedFields(rawReport);
            if (previousReport == null || !RecordReportComparer.Equals(previousReport, report))
            {
                writer.WritePropertyName(dumpIdx.ToString());
                ReportJsonFormatter.WriteReport(writer, report);
            }

            previousReport = report;
        }

        writer.WriteEndObject();

        writer.WritePropertyName("present");
        writer.WriteStartArray();
        foreach (var dumpIdx in dumpMap.Keys.OrderBy(k => k))
            writer.WriteNumberValue(dumpIdx);
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteSearchText(
        Utf8JsonWriter writer,
        Dictionary<int, RecordReport> dumpMap,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var recordType = dumpMap.Values.FirstOrDefault()?.RecordType;
        if (!string.Equals(recordType, "Dialogue", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(recordType, "DialogTopic", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var searchText = BuildSearchText(dumpMap, metadata);
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            writer.WriteString("searchText", searchText);
        }
    }

    private static string BuildSearchText(
        Dictionary<int, RecordReport> dumpMap,
        IReadOnlyDictionary<string, string>? metadata)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (metadata != null &&
            metadata.TryGetValue("searchText", out var metadataSearchText))
        {
            AddSearchText(values, metadataSearchText);
        }

        foreach (var report in dumpMap.Values)
        {
            AddSearchText(values, report.EditorId);
            AddSearchText(values, report.DisplayName);
            foreach (var section in report.Sections)
            {
                AddSearchText(values, section.Name);
                foreach (var field in section.Fields)
                {
                    AddSearchText(values, field.Key);
                    AddSearchText(values, field.FormIdRef);
                    AddSearchText(values, field.Value.Display);
                    AddReportValueSearchText(values, field.Value);
                }
            }
        }

        return string.Join(' ', values);
    }

    private static void AddReportValueSearchText(HashSet<string> values, ReportValue value)
    {
        switch (value)
        {
            case ReportValue.StringVal stringValue:
                AddSearchText(values, stringValue.Raw);
                break;
            case ReportValue.FormIdVal formIdValue:
                AddSearchText(values, $"0x{formIdValue.Raw:X8}");
                break;
            case ReportValue.ListVal listValue:
                foreach (var item in listValue.Items)
                {
                    AddReportValueSearchText(values, item);
                }

                break;
            case ReportValue.CompositeVal composite:
                foreach (var field in composite.Fields)
                {
                    AddSearchText(values, field.Key);
                    AddSearchText(values, field.FormIdRef);
                    AddSearchText(values, field.Value.Display);
                    AddReportValueSearchText(values, field.Value);
                }

                break;
        }
    }

    private static void AddSearchText(HashSet<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value);
        }
    }

    private static void WriteDumpsArray(Utf8JsonWriter writer, List<DumpSnapshot> dumps)
    {
        writer.WritePropertyName("dumps");
        writer.WriteStartArray();
        foreach (var d in dumps)
        {
            writer.WriteStartObject();
            writer.WriteString("fileName", d.FileName);
            writer.WriteString("date", d.FileDate.ToString("o"));
            writer.WriteString("dateSource", d.DateSource);
            writer.WriteString("shortName", d.ShortName);
            writer.WriteBoolean("isDmp", d.IsDmp);
            writer.WriteBoolean("isBase", d.IsBase);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteSparseDumps(Utf8JsonWriter writer,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap, List<DumpSnapshot> dumps)
    {
        writer.WritePropertyName("sparseDumps");
        writer.WriteStartArray();
        var presentDumps = new HashSet<int>();
        foreach (var dm in formIdMap.Values)
        {
            foreach (var k in dm.Keys)
            {
                presentDumps.Add(k);
            }
        }

        for (var dumpIdx = 0; dumpIdx < dumps.Count; dumpIdx++)
        {
            if (!presentDumps.Contains(dumpIdx))
            {
                writer.WriteNumberValue(dumpIdx);
            }
        }

        writer.WriteEndArray();
    }
}
