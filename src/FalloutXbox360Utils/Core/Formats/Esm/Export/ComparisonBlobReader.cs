using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Inverse of <see cref="CrossDumpJsonHtmlWriter" />: extracts the embedded
///     deflate+base64 record-data payload from a compare_*.html and rehydrates it back
///     into the <see cref="RecordReport" /> graph used elsewhere in the comparison pipeline.
///     Lets <c>report consistency</c> consume an existing comparison run without re-loading
///     raw ESM/DMP files.
/// </summary>
internal static class ComparisonBlobReader
{
    private static readonly Regex SinglePayloadRegex = new(
        "<script[^>]*id=\"record-data\"[^>]*>(?<payload>[^<]+)</script>",
        RegexOptions.Compiled);

    private static readonly Regex ChunkRegex = new(
        "<script[^>]*class=\"record-data-chunk\"[^>]*>(?<payload>[^<]+)</script>",
        RegexOptions.Compiled);

    /// <summary>
    ///     Matches the per-group chunk scripts emitted by
    ///     <c>CrossDumpJsonHtmlWriter.GenerateChunkedPage</c> — the index blob's
    ///     <c>"chunked": true</c> flag tells us to go looking for these.
    /// </summary>
    private static readonly Regex ChunkDataZRegex = new(
        "<script[^>]*id=\"chunk-\\d+\"[^>]*data-z=\"(?<payload>[^\"]+)\"[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex ExternalChunkMarkerRegex = new(
        "<script[^>]*id=\"chunk-\\d+\"[^>]*data-external-key=\"(?<key>[^\"]+)\"[^>]*data-src=\"(?<src>[^\"]+)\"[^>]*>",
        RegexOptions.Compiled);

    private static readonly Regex ExternalChunkPayloadRegex = new(
        "__comparisonExternalChunks\\[\"(?<key>[^\"]+)\"\\]=\"(?<payload>[^\"]*)\"",
        RegexOptions.Compiled);

    /// <summary>
    ///     Parse a single compare_*.html file, returning the rehydrated record table.
    ///     Handles both layouts emitted by <see cref="CrossDumpJsonHtmlWriter" />:
    ///     <list type="bullet">
    ///         <item>
    ///             inline single payload (<c>&lt;script id="record-data"&gt;</c>) with
    ///             records embedded directly;
    ///         </item>
    ///         <item>
    ///             chunked pages (cells/dialogue/NPC) where the index blob carries
    ///             <c>"chunked": true</c> + manifest, and record bodies live in sibling
    ///             <c>&lt;script id="chunk-N" data-z="…"&gt;</c> scripts.
    ///         </item>
    ///     </list>
    /// </summary>
    internal static HtmlPage? Read(string htmlPath)
    {
        var html = File.ReadAllText(htmlPath, Encoding.UTF8);
        var compressed = ExtractCompressedPayload(html);
        if (compressed == null) return null;

        var json = Decompress(compressed);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var page = new HtmlPage
        {
            RecordType = root.TryGetProperty("recordType", out var rt) ? rt.GetString() ?? "" : ""
        };
        ReadDumps(root, page);

        // Inline records (non-chunked pages)
        if (root.TryGetProperty("records", out var recordsEl) && recordsEl.ValueKind == JsonValueKind.Object)
        {
            ReadRecordsInto(recordsEl, page.RecordType, page.Records);
        }

        // Chunked pages: walk each sibling chunk script and merge its records.
        var isChunked = root.TryGetProperty("chunked", out var cf) && cf.ValueKind == JsonValueKind.True;
        if (isChunked)
        {
            foreach (Match m in ChunkDataZRegex.Matches(html))
            {
                var chunkJson = Decompress(m.Groups["payload"].Value.Trim());
                using var chunkDoc = JsonDocument.Parse(chunkJson);
                var chunkRoot = chunkDoc.RootElement;
                if (chunkRoot.ValueKind == JsonValueKind.Object)
                {
                    ReadRecordsInto(chunkRoot, page.RecordType, page.Records);
                }
            }

            foreach (var compressedChunkPayload in ReadExternalChunkPayloads(htmlPath, html))
            {
                var chunkJson = Decompress(compressedChunkPayload);
                using var chunkDoc = JsonDocument.Parse(chunkJson);
                var chunkRoot = chunkDoc.RootElement;
                if (chunkRoot.ValueKind == JsonValueKind.Object)
                {
                    ReadRecordsInto(chunkRoot, page.RecordType, page.Records);
                }
            }
        }

        return page;
    }

    private static IEnumerable<string> ReadExternalChunkPayloads(string htmlPath, string html)
    {
        var htmlDirectory = Path.GetDirectoryName(htmlPath) ?? ".";
        foreach (Match marker in ExternalChunkMarkerRegex.Matches(html))
        {
            var key = WebUtility.HtmlDecode(marker.Groups["key"].Value);
            var src = WebUtility.HtmlDecode(marker.Groups["src"].Value)
                .Replace('/', Path.DirectorySeparatorChar);
            var chunkPath = Path.Combine(htmlDirectory, src);
            if (!File.Exists(chunkPath))
            {
                continue;
            }

            var script = File.ReadAllText(chunkPath, Encoding.UTF8);
            foreach (Match payloadMatch in ExternalChunkPayloadRegex.Matches(script))
            {
                if (string.Equals(payloadMatch.Groups["key"].Value, key, StringComparison.Ordinal))
                {
                    yield return payloadMatch.Groups["payload"].Value.Trim();
                }
            }
        }
    }

    private static void ReadDumps(JsonElement root, HtmlPage page)
    {
        if (!root.TryGetProperty("dumps", out var dumpsEl) || dumpsEl.ValueKind != JsonValueKind.Array)
            return;

        foreach (var d in dumpsEl.EnumerateArray())
        {
            page.Dumps.Add(new DumpInfo(
                d.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "",
                d.TryGetProperty("shortName", out var sn) ? sn.GetString() ?? "" : "",
                d.TryGetProperty("date", out var dt) && dt.GetString() is { } dts
                    ? DateTime.Parse(dts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    : DateTime.MinValue,
                d.TryGetProperty("isDmp", out var idp) && idp.GetBoolean(),
                d.TryGetProperty("isBase", out var ib) && ib.GetBoolean(),
                d.TryGetProperty("dateSource", out var ds) ? ds.GetString() ?? "" : ""));
        }
    }

    /// <summary>
    ///     Rehydrate a records object (either the inline top-level <c>"records"</c>
    ///     map or a per-group chunk) into the flat <c>FormID → dumpIdx → RecordReport</c>
    ///     view. Idempotent; later records overwrite earlier ones at the same key.
    /// </summary>
    private static void ReadRecordsInto(
        JsonElement recordsEl,
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> target)
    {
        foreach (var entry in recordsEl.EnumerateObject())
        {
            var formId = ParseFormIdHex(entry.Name);
            var rec = entry.Value;

            List<int> present = [];
            if (rec.TryGetProperty("present", out var presentEl) &&
                presentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in presentEl.EnumerateArray())
                    present.Add(p.GetInt32());
            }

            var snapshots = new Dictionary<int, RecordReport>();
            if (rec.TryGetProperty("snapshots", out var snapsEl) &&
                snapsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var snap in snapsEl.EnumerateObject())
                {
                    var dumpIdx = int.Parse(snap.Name, CultureInfo.InvariantCulture);
                    var report = ReadReport(snap.Value, recordType, formId);
                    if (report != null) snapshots[dumpIdx] = report;
                }
            }

            // Expand delta-encoded snapshots: every entry in `present` should
            // resolve to the most recent snapshot at or before that dump index.
            RecordReport? carry = null;
            var expanded = new Dictionary<int, RecordReport>();
            var orderedPresent = present.Count > 0 ? present.OrderBy(i => i).ToList() : null;
            if (orderedPresent != null)
            {
                foreach (var idx in orderedPresent)
                {
                    if (snapshots.TryGetValue(idx, out var snap))
                    {
                        carry = snap;
                    }

                    if (carry != null) expanded[idx] = carry;
                }
            }
            else
            {
                foreach (var (idx, snap) in snapshots) expanded[idx] = snap;
            }

            target[formId] = expanded;
        }
    }

    private static string? ExtractCompressedPayload(string html)
    {
        var single = SinglePayloadRegex.Match(html);
        if (single.Success)
        {
            return single.Groups["payload"].Value.Trim();
        }

        var chunkMatches = ChunkRegex.Matches(html);
        if (chunkMatches.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (Match m in chunkMatches)
            {
                sb.Append(m.Groups["payload"].Value.Trim());
            }

            return sb.ToString();
        }

        return null;
    }

    private static string Decompress(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        using var input = new MemoryStream(bytes);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static uint ParseFormIdHex(string s)
    {
        var trimmed = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
        return uint.Parse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private static RecordReport? ReadReport(JsonElement el, string fallbackRecordType, uint formId)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        var recordType = el.TryGetProperty("recordType", out var rt)
            ? rt.GetString() ?? fallbackRecordType
            : fallbackRecordType;
        var editorId = el.TryGetProperty("editorId", out var eid) && eid.ValueKind == JsonValueKind.String
            ? eid.GetString()
            : null;
        var displayName = el.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
            ? dn.GetString()
            : null;

        var sections = new List<ReportSection>();
        if (el.TryGetProperty("sections", out var secsEl) && secsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var sec in secsEl.EnumerateArray())
            {
                var name = sec.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var fields = new List<ReportField>();
                if (sec.TryGetProperty("fields", out var fldsEl) && fldsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fldsEl.EnumerateArray())
                    {
                        var field = ReadField(f);
                        if (field != null) fields.Add(field);
                    }
                }

                sections.Add(new ReportSection(name, fields));
            }
        }

        return new RecordReport(recordType, formId, editorId, displayName, sections);
    }

    private static ReportField? ReadField(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (!el.TryGetProperty("key", out var keyEl)) return null;
        var key = keyEl.GetString() ?? "";
        var formIdRef = el.TryGetProperty("formIdRef", out var fr) && fr.ValueKind == JsonValueKind.String
            ? fr.GetString()
            : null;
        if (!el.TryGetProperty("value", out var valEl)) return null;
        var value = ReadValue(valEl);
        if (value == null) return null;
        return new ReportField(key, value, formIdRef);
    }

    private static ReportValue? ReadValue(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "int":
            {
                var raw = el.GetProperty("raw").GetInt64();
                var display = el.TryGetProperty("display", out var d) ? d.GetString() : null;
                return display != null
                    ? new ReportValue.IntVal((int)raw, display)
                    : new ReportValue.IntVal((int)raw);
            }
            case "float":
            {
                var raw = el.GetProperty("raw").GetDouble();
                var display = el.TryGetProperty("display", out var d) ? d.GetString() ?? "" : "";
                return new ReportValue.FloatVal(raw, display);
            }
            case "string":
            {
                var raw = el.TryGetProperty("raw", out var r) ? r.GetString() ?? "" : "";
                return new ReportValue.StringVal(raw);
            }
            case "bool":
            {
                var raw = el.GetProperty("raw").GetBoolean();
                var display = el.TryGetProperty("display", out var d) ? d.GetString() : null;
                return display != null
                    ? new ReportValue.BoolVal(raw, display)
                    : new ReportValue.BoolVal(raw);
            }
            case "formId":
            {
                uint raw = 0;
                if (el.TryGetProperty("rawInt", out var ri))
                {
                    raw = (uint)ri.GetInt64();
                }
                else if (el.TryGetProperty("raw", out var r) && r.ValueKind == JsonValueKind.String)
                {
                    raw = ParseFormIdHex(r.GetString() ?? "0x0");
                }

                var display = el.TryGetProperty("display", out var d) ? d.GetString() ?? $"0x{raw:X8}" : $"0x{raw:X8}";
                return new ReportValue.FormIdVal(raw, display);
            }
            case "list":
            {
                var items = new List<ReportValue>();
                if (el.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in itemsEl.EnumerateArray())
                    {
                        var v = ReadValue(item);
                        if (v != null) items.Add(v);
                    }
                }

                var display = el.TryGetProperty("display", out var d) ? d.GetString() : null;
                return display != null
                    ? new ReportValue.ListVal(items, display)
                    : new ReportValue.ListVal(items);
            }
            case "composite":
            {
                var fields = new List<ReportField>();
                if (el.TryGetProperty("fields", out var fldsEl) && fldsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fldsEl.EnumerateArray())
                    {
                        var field = ReadField(f);
                        if (field != null) fields.Add(field);
                    }
                }

                var display = el.TryGetProperty("display", out var d) ? d.GetString() : null;
                return display != null
                    ? new ReportValue.CompositeVal(fields, display)
                    : new ReportValue.CompositeVal(fields);
            }
            default:
                return null;
        }
    }

    internal sealed record DumpInfo(
        string FileName,
        string ShortName,
        DateTime Date,
        bool IsDmp,
        bool IsBase,
        string DateSource = "");

    internal sealed class HtmlPage
    {
        internal string RecordType { get; init; } = "";
        internal List<DumpInfo> Dumps { get; init; } = [];
        internal Dictionary<uint, Dictionary<int, RecordReport>> Records { get; init; } = [];
    }
}
