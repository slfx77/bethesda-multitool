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
                if (ExcludedFieldKeys.Contains(field.Key)) { hasAny = true; break; }
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
                foreach (var k in dm.Keys)
                    presentDumps.Add(k);

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

                // EditorId and DisplayName: use the latest non-null value
                string? editorId = null;
                string? displayName = null;
                foreach (var report in dumpMap.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value))
                {
                    editorId ??= report.EditorId;
                    displayName ??= report.DisplayName;
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
                if (metadata != null && metadata.TryGetValue(formId, out var meta) && meta.Count > 0)
                {
                    writer.WritePropertyName("metadata");
                    writer.WriteStartObject();
                    foreach (var (key, value) in meta)
                        writer.WriteString(key, value);
                    writer.WriteEndObject();
                }

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
    ///     Build chunked JSON blobs for grouped pages (cells).
    ///     Returns an index blob + per-group chunk blobs, each under the size limit.
    ///     Groups exceeding the limit are split into sub-chunks.
    /// </summary>
    internal static (string IndexBlob, List<(string GroupKey, string ChunkBlob)> Chunks) BuildChunked(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        string recordType,
        Dictionary<uint, string>? groups,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        long maxChunkBytes = 5 * 1024 * 1024)
    {
        // Partition records by group
        var grouped = new Dictionary<string, Dictionary<uint, Dictionary<int, RecordReport>>>();
        foreach (var (formId, dumpMap) in formIdMap)
        {
            var groupKey = groups != null && groups.TryGetValue(formId, out var g) ? g : "(Ungrouped)";
            if (!grouped.TryGetValue(groupKey, out var groupMap))
            {
                groupMap = new Dictionary<uint, Dictionary<int, RecordReport>>();
                grouped[groupKey] = groupMap;
            }

            groupMap[formId] = dumpMap;
        }

        // Build index blob (metadata only, no records)
        string indexBlob;
        using (var ms = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("recordType", recordType);
                writer.WriteBoolean("chunked", true);

                WriteDumpsArray(writer, dumps);
                WriteSparseDumps(writer, formIdMap, dumps);

                // Group manifest: group name → record count + chunk index
                writer.WritePropertyName("groupManifest");
                writer.WriteStartObject();
                foreach (var (groupKey, groupMap) in grouped.OrderBy(g =>
                             g.Key == "Interior Cells" ? 1 : 0).ThenBy(g => g.Key))
                {
                    writer.WritePropertyName(groupKey);
                    writer.WriteNumberValue(groupMap.Count);
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

            indexBlob = Encoding.UTF8.GetString(ms.ToArray());
        }

        // Build per-group chunk blobs with per-record size tracking.
        // Serialize each record individually and accumulate into chunks,
        // flushing when the chunk exceeds the byte limit.
        var chunks = new List<(string GroupKey, string ChunkBlob)>();
        foreach (var (groupKey, groupMap) in grouped.OrderBy(g =>
                     g.Key == "Interior Cells" ? 1 : 0).ThenBy(g => g.Key))
        {
            var currentChunk = new Dictionary<uint, Dictionary<int, RecordReport>>();
            long currentSize = 2; // "{}" wrapper
            var partNum = 1;

            foreach (var (formId, dumpMap) in groupMap)
            {
                // Estimate record size by serializing just this entry
                var singleJson = BuildRecordsChunk(
                    new Dictionary<uint, Dictionary<int, RecordReport>> { { formId, dumpMap } },
                    dumps, metadata);
                var entrySize = (long)Encoding.UTF8.GetByteCount(singleJson);

                // If adding this record would exceed the limit, flush current chunk
                if (currentChunk.Count > 0 && currentSize + entrySize > maxChunkBytes)
                {
                    var label = groupMap.Count > currentChunk.Count
                        ? $"{groupKey} (part {partNum})"
                        : groupKey;
                    chunks.Add((label, BuildRecordsChunk(currentChunk, dumps, metadata)));
                    partNum++;
                    currentChunk.Clear();
                    currentSize = 2;
                }

                currentChunk[formId] = dumpMap;
                currentSize += entrySize;
            }

            if (currentChunk.Count > 0)
            {
                var label = partNum > 1 ? $"{groupKey} (part {partNum})" : groupKey;
                chunks.Add((label, BuildRecordsChunk(currentChunk, dumps, metadata)));
            }
        }

        return (indexBlob, chunks);
    }

    /// <summary>Build a JSON blob containing only record data (no dumps/groups metadata).</summary>
    private static string BuildRecordsChunk(
        Dictionary<uint, Dictionary<int, RecordReport>> records,
        List<DumpSnapshot> dumps,
        Dictionary<uint, Dictionary<string, string>>? metadata)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
        {
            writer.WriteStartObject();
            foreach (var (formId, dumpMap) in records)
            {
                writer.WritePropertyName($"0x{formId:X8}");
                WriteRecordEntry(writer, formId, dumpMap, metadata);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Write a single record entry (shared by Build and BuildChunked).</summary>
    private static void WriteRecordEntry(
        Utf8JsonWriter writer, uint formId,
        Dictionary<int, RecordReport> dumpMap,
        Dictionary<uint, Dictionary<string, string>>? metadata)
    {
        writer.WriteStartObject();

        string? editorId = null;
        string? displayName = null;
        foreach (var report in dumpMap.OrderBy(kvp => kvp.Key).Select(kvp => kvp.Value))
        {
            editorId ??= report.EditorId;
            displayName ??= report.DisplayName;
        }

        writer.WriteString("editorId", editorId);
        writer.WriteString("displayName", displayName);

        var allEditorIds = dumpMap.OrderBy(kvp => kvp.Key)
            .Select(kvp => kvp.Value.EditorId).Where(e => e != null).Distinct().ToList();
        if (allEditorIds.Count > 1)
        {
            writer.WritePropertyName("editorIdHistory");
            writer.WriteStartArray();
            foreach (var eid in allEditorIds) writer.WriteStringValue(eid);
            writer.WriteEndArray();
        }

        var allNames = dumpMap.OrderBy(kvp => kvp.Key)
            .Select(kvp => kvp.Value.DisplayName).Where(n => n != null).Distinct().ToList();
        if (allNames.Count > 1)
        {
            writer.WritePropertyName("nameHistory");
            writer.WriteStartArray();
            foreach (var name in allNames) writer.WriteStringValue(name);
            writer.WriteEndArray();
        }

        if (metadata != null && metadata.TryGetValue(formId, out var meta) && meta.Count > 0)
        {
            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            foreach (var (key, value) in meta)
                writer.WriteString(key, value);
            writer.WriteEndObject();
        }

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

    private static void WriteDumpsArray(Utf8JsonWriter writer, List<DumpSnapshot> dumps)
    {
        writer.WritePropertyName("dumps");
        writer.WriteStartArray();
        foreach (var d in dumps)
        {
            writer.WriteStartObject();
            writer.WriteString("fileName", d.FileName);
            writer.WriteString("date", d.FileDate.ToString("o"));
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
            foreach (var k in dm.Keys)
                presentDumps.Add(k);
        for (var dumpIdx = 0; dumpIdx < dumps.Count; dumpIdx++)
            if (!presentDumps.Contains(dumpIdx))
                writer.WriteNumberValue(dumpIdx);
        writer.WriteEndArray();
    }
}
