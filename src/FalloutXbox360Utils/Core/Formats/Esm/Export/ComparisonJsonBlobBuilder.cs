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
    ///     Build a compressed base64 JSON blob for a record type page.
    /// </summary>
    internal static string Build(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<string, Dictionary<uint, string>>? allGroups,
        string recordType,
        Dictionary<uint, (int X, int Y)>? cellGridCoords)
    {
        // Resolve groups for this record type
        Dictionary<uint, string>? groups = null;
        allGroups?.TryGetValue(recordType, out groups);

        return Build(formIdMap, dumps, groups, cellGridCoords);
    }

    /// <summary>
    ///     Build a compressed base64 JSON blob for a record type page with pre-resolved groups.
    /// </summary>
    internal static string Build(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string>? groups,
        Dictionary<uint, (int X, int Y)>? cellGridCoords)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
        {
            writer.WriteStartObject();

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

                // Snapshots: delta-encoded -- store only when report structurally differs from previous
                writer.WritePropertyName("snapshots");
                writer.WriteStartObject();
                RecordReport? previousReport = null;
                foreach (var (dumpIdx, report) in dumpMap.OrderBy(kvp => kvp.Key))
                {
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

            // Groups (if any)
            if (groups is { Count: > 0 })
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
}
