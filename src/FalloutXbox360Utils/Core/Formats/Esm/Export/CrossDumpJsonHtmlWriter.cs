using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Web;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates static HTML comparison pages with embedded compressed JSON data,
///     rendered client-side by JavaScript. Consumes <see cref="CrossDumpRecordIndex.StructuredRecords" />
///     instead of pre-formatted text, enabling field-level diff computed in the browser.
/// </summary>
internal static class CrossDumpJsonHtmlWriter
{
    private static readonly JsonWriterOptions CompactJsonOptions = new() { Indented = false };

    /// <summary>
    ///     Generate all HTML files: one per record type (with embedded JSON) plus an index page.
    /// </summary>
    internal static Dictionary<string, string> GenerateAll(CrossDumpRecordIndex index)
    {
        var files = new Dictionary<string, string>();

        foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
        {
            index.RecordGroups.TryGetValue(recordType, out var groups);
            var html = GenerateRecordTypePage(recordType, formIdMap, index.Dumps, groups,
                index.CellGridCoords);
            if (!string.IsNullOrEmpty(html))
            {
                files[$"compare_{recordType.ToLowerInvariant()}.html"] = html;
            }
        }

        files["index.html"] = GenerateIndexPage(index);
        return files;
    }

    private static string GenerateRecordTypePage(
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string>? groups,
        Dictionary<uint, (int X, int Y)>? cellGridCoords)
    {
        var sb = new StringBuilder();
        AppendHtmlHeader(sb, $"{recordType} \u2014 Cross-Build Comparison");

        // Initial column visibility — hide columns 3+ until nav buttons change it
        if (dumps.Count > 3)
        {
            sb.Append("  <style id=\"build-col-style\">");
            for (var i = 3; i < dumps.Count; i++)
                sb.Append($".build-col-{i}{{display:none !important}}");
            sb.AppendLine("</style>");
        }

        sb.AppendLine($"  <h1>{Esc(recordType)} \u2014 Cross-Build Comparison</h1>");
        sb.AppendLine($"  <p class=\"summary\">{dumps.Count} builds, {formIdMap.Count:N0} records</p>");

        // Navigation + controls
        sb.AppendLine("  <div class=\"controls\">");
        sb.AppendLine("    <a href=\"index.html\">&larr; Back to index</a>");
        sb.AppendLine(
            "    <input type=\"text\" id=\"search\" placeholder=\"Search by FormID, EditorID, or name...\" oninput=\"filterRows()\">");
        sb.AppendLine("    <button onclick=\"expandAll()\">Expand All</button>");
        sb.AppendLine("    <button onclick=\"collapseAll()\">Collapse All</button>");
        sb.AppendLine("    <span id=\"matchCount\" class=\"match-count\"></span>");
        if (dumps.Count > 3)
        {
            sb.AppendLine(
                $"    <div class=\"build-nav\" data-total=\"{dumps.Count}\" data-start=\"0\" data-size=\"3\">");
            sb.AppendLine("      <button onclick=\"navBuilds('first')\">&laquo; First</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('prev3')\">&lsaquo; 3</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('prev1')\">&lsaquo; 1</button>");
            sb.AppendLine(
                $"      <span class=\"build-nav-label\">Builds 1\u20133 of {dumps.Count}</span>");
            sb.AppendLine("      <button onclick=\"navBuilds('next1')\">1 &rsaquo;</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('next3')\">3 &rsaquo;</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('last')\">&raquo; Last</button>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");

        // Loading indicator (shown until JS hydration completes)
        sb.AppendLine("  <div id=\"loading\">Loading records...</div>");

        // Table skeleton -- JS will populate the tbody
        sb.AppendLine("  <div id=\"tables-container\"></div>");

        // Embed compressed JSON data
        var jsonBlob = BuildJsonBlob(formIdMap, dumps, groups, cellGridCoords);
        var compressed = CompressToBase64(jsonBlob);
        sb.AppendLine(
            $"  <script type=\"application/json\" id=\"record-data\" data-z=\"{compressed}\"></script>");

        // JavaScript renderer
        sb.AppendLine($"  <script>{RendererJavaScript}</script>");
        AppendHtmlFooter(sb);
        return sb.ToString();
    }

    /// <summary>
    ///     Serialize all record data for a single record type page into a JSON blob.
    ///     Uses delta encoding: only stores a snapshot when it differs from the previous build.
    /// </summary>
    private static string BuildJsonBlob(
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

            // Detect sparse dumps for this record type
            writer.WritePropertyName("sparseDumps");
            writer.WriteStartArray();
            for (var dumpIdx = 0; dumpIdx < dumps.Count; dumpIdx++)
            {
                if (!formIdMap.Values.Any(dm => dm.ContainsKey(dumpIdx)))
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

                // Snapshots: delta-encoded -- store only when report JSON differs from previous
                writer.WritePropertyName("snapshots");
                writer.WriteStartObject();
                string? previousJson = null;
                foreach (var (dumpIdx, report) in dumpMap.OrderBy(kvp => kvp.Key))
                {
                    var reportJson = ReportJsonFormatter.Format(report, indented: false);
                    if (reportJson != previousJson)
                    {
                        writer.WritePropertyName(dumpIdx.ToString());
                        writer.WriteRawValue(reportJson);
                        previousJson = reportJson;
                    }
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

    private static string GenerateIndexPage(CrossDumpRecordIndex index)
    {
        var sb = new StringBuilder();
        AppendHtmlHeader(sb, "Cross-Build Comparison Index");

        sb.AppendLine("  <h1>Cross-Build Comparison Index</h1>");

        var total = index.StructuredRecords.Values.Sum(m => m.Count);

        sb.AppendLine(
            $"  <p class=\"summary\">{index.Dumps.Count} builds, {total:N0} total records</p>");

        // Build info table
        sb.AppendLine("  <h2>Builds</h2>");
        sb.AppendLine("  <table class=\"compact\">");
        sb.AppendLine("    <thead><tr><th>#</th><th>File</th><th>Build Date</th></tr></thead>");
        sb.AppendLine("    <tbody>");
        for (var i = 0; i < index.Dumps.Count; i++)
        {
            var d = index.Dumps[i];
            var displayFileName = d.IsBase ? d.ShortName : d.FileName;
            var displayDate = d.IsBase ? "(base)" : $"{d.FileDate:yyyy-MM-dd HH:mm}";
            sb.AppendLine(
                $"      <tr><td>{i + 1}</td><td>{Esc(displayFileName)}</td><td>{displayDate}</td></tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");

        // Record type summary with links
        sb.AppendLine("  <h2>Record Types</h2>");
        sb.AppendLine("  <table class=\"compact\">");
        sb.AppendLine("    <thead>");
        sb.AppendLine("      <tr><th>Type</th><th>Records</th>");
        foreach (var d in index.Dumps)
            sb.AppendLine($"        <th>{Esc(d.ShortName)}</th>");
        sb.AppendLine("      </tr>");
        sb.AppendLine("    </thead>");
        sb.AppendLine("    <tbody>");

        foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
        {
            var filename = $"compare_{recordType.ToLowerInvariant()}.html";

            sb.Append($"      <tr><td><a href=\"{filename}\">{Esc(recordType)}</a></td>");
            sb.Append($"<td>{formIdMap.Count:N0}</td>");
            for (var dumpIdx = 0; dumpIdx < index.Dumps.Count; dumpIdx++)
            {
                var count = formIdMap.Values.Count(dm => dm.ContainsKey(dumpIdx));
                sb.Append($"<td>{count:N0}</td>");
            }

            sb.AppendLine("</tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");

        AppendHtmlFooter(sb);
        return sb.ToString();
    }

    private static void AppendHtmlHeader(StringBuilder sb, string title)
    {
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine($"  <title>{Esc(title)}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine(CssStyles);
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
    }

    private static void AppendHtmlFooter(StringBuilder sb)
    {
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
    }

    private static string CompressToBase64(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    private static string Esc(string text) => HttpUtility.HtmlEncode(text);

    // CSS styles -- shared with CrossDumpHtmlWriter, extended for JSON-rendered field-level diff
    private const string CssStyles = """
        * { box-sizing: border-box; }
        body {
          font-family: system-ui, -apple-system, sans-serif;
          margin: 20px;
          background: #fff;
          color: #1a1a1a;
        }
        h1 { margin-bottom: 4px; }
        .summary { color: #666; margin-top: 0; }
        a { color: #0066cc; }

        .controls {
          display: flex;
          align-items: center;
          gap: 12px;
          padding: 8px 12px;
          flex-wrap: wrap;
          position: sticky;
          top: 0;
          z-index: 10;
          background: #fff;
          border-bottom: 1px solid #ddd;
          margin: 0;
        }
        .controls input[type="text"] {
          padding: 6px 12px;
          border: 1px solid #ccc;
          border-radius: 4px;
          font-size: 13px;
          min-width: 300px;
        }
        .controls button {
          padding: 5px 12px;
          border: 1px solid #ccc;
          border-radius: 4px;
          background: #f5f5f5;
          cursor: pointer;
          font-size: 12px;
        }
        .controls button:hover { background: #e8e8e8; }
        .match-count { font-size: 12px; color: #888; }

        #loading {
          padding: 20px;
          text-align: center;
          color: #888;
          font-size: 14px;
        }

        table { border-collapse: separate; border-spacing: 0; width: 100%; margin: 8px 0; }
        tbody { content-visibility: auto; contain-intrinsic-size: auto 500px; }
        table.compact { width: auto; }
        table.compact th { position: static; }
        th, td { border: 1px solid #ddd; padding: 6px 8px; vertical-align: top; text-align: left; }
        th {
          background: #f5f5f5;
          position: sticky;
          top: 38px;
          z-index: 3;
          font-size: 13px;
        }
        .col-editor {
          position: sticky;
          left: 0;
          z-index: 1;
          background: #fff;
          min-width: 120px;
          white-space: nowrap;
        }
        th.col-editor { z-index: 5; }
        .col-name { min-width: 80px; white-space: nowrap; }
        .col-coords { min-width: 70px; white-space: nowrap; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 12px; }
        .col-formid { min-width: 90px; white-space: nowrap; }
        .sortable { cursor: pointer; user-select: none; }
        .formid { font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 12px; }
        .name-change { color: #999; font-size: 11px; }
        .dump-date { font-size: 11px; color: #888; font-weight: normal; }
        .build-header { cursor: pointer; }
        .build-header:hover { background: #e8e8f0; }
        .build-filter-label { font-size: 10px; font-weight: 600; letter-spacing: 0.5px; text-transform: uppercase; }

        .summary-row { cursor: pointer; }
        .summary-row:hover { background: #f0f4ff; }
        .summary-row:hover .col-editor { background: #f0f4ff; }
        .summary-row td { padding: 4px 8px; }
        .detail-row td { padding: 2px 4px; }
        .detail-row .col-editor { background: #fff; }

        .badge {
          display: inline-block;
          padding: 2px 8px;
          border-radius: 3px;
          font-size: 10px;
          font-weight: 600;
          letter-spacing: 0.5px;
          text-transform: uppercase;
        }
        .badge-new { background: #d4edda; color: #155724; }
        .badge-changed { background: #fff3cd; color: #856404; }
        .badge-same { background: #e9ecef; color: #6c757d; }
        .badge-removed { background: #f8d7da; color: #721c24; }
        .badge-absent { color: #ccc; }
        .badge-sparse { background: #e2e3f1; color: #5a5b8a; }
        .badge-base { background: #d6e4f0; color: #2c5282; }

        .record-detail {
          font-family: 'Cascadia Code', 'Fira Code', 'Consolas', monospace;
          font-size: 11px;
          line-height: 1.4;
          white-space: pre-wrap;
          margin: 0;
        }
        .field-new { background: #d4edda; }
        .field-changed { background: #fff3cd; }
        .field-removed { background: #f8d7da; color: #999; text-decoration: line-through; }
        .field-sparse { color: #999; font-style: italic; }
        .section-header { font-weight: bold; margin-top: 4px; }
        .field-arrow { color: #888; margin: 0 4px; }

        .toc {
          background: #f8f9fa;
          border: 1px solid #ddd;
          border-radius: 4px;
          padding: 8px 16px;
          margin: 8px 0 16px 0;
        }
        .toc ul { margin: 4px 0; padding-left: 20px; columns: 3; }
        .toc li { font-size: 13px; margin: 2px 0; }

        .group-section { margin: 8px 0; }
        .group-header {
          margin: 0;
          padding: 8px 0;
          border-bottom: 2px solid #0066cc;
          color: #0066cc;
          font-size: 18px;
          cursor: pointer;
          user-select: none;
        }
        .group-header:hover { opacity: 0.8; }

        .build-nav { display: flex; align-items: center; gap: 6px; margin-left: auto; }
        .build-nav button { padding: 3px 8px; font-size: 11px; border: 1px solid #ccc; border-radius: 3px; background: #f5f5f5; cursor: pointer; }
        .build-nav button:hover { background: #e0e0e0; }
        .build-nav button:disabled { opacity: 0.4; cursor: default; }
        .build-nav-label { font-size: 12px; color: #666; min-width: 120px; text-align: center; }
        .hidden { display: none !important; }

        @media (prefers-color-scheme: dark) {
          body { background: #1a1a1a; color: #e0e0e0; }
          a { color: #6db3f2; }
          th { background: #2a2a2a; border-color: #444; }
          td { border-color: #444; }
          .controls { background: #1a1a1a; border-bottom-color: #444; }
          .col-editor { background: #1a1a1a; }
          .detail-row .col-editor { background: #1a1a1a; }
          .summary { color: #999; }
          .dump-date { color: #777; }
          .controls input[type="text"] { background: #2a2a2a; color: #e0e0e0; border-color: #555; }
          .controls button { background: #333; color: #e0e0e0; border-color: #555; }
          .controls button:hover { background: #444; }
          .summary-row:hover { background: #252535; }
          .summary-row:hover .col-editor { background: #252535; }
          .badge-new { background: #1e3a1e; color: #8fd19e; }
          .badge-changed { background: #3a3520; color: #e0c878; }
          .badge-same { background: #2a2a2a; color: #888; }
          .badge-removed { background: #3a1e1e; color: #e08888; }
          .badge-absent { color: #555; }
          .badge-sparse { background: #2a2a3a; color: #9999cc; }
          .badge-base { background: #1e2a3a; color: #6b9fd4; }
          .field-new { background: #1e3a1e; }
          .field-changed { background: #3a3520; }
          .field-removed { background: #3a1e1e; color: #777; }
          .field-sparse { color: #666; }
          .group-header { color: #6db3f2; border-bottom-color: #6db3f2; }
          .toc { background: #2a2a2a; border-color: #444; }
          .build-header:hover { background: #333; }
          .build-nav button { background: #333; color: #e0e0e0; border-color: #555; }
          .build-nav button:hover { background: #444; }
          .build-nav-label { color: #999; }
        }
    """;

    /// <summary>
    ///     Client-side JavaScript that decompresses the embedded JSON blob and renders
    ///     the summary table, detail views with field-level diff, search, sort, and pagination.
    /// </summary>
    private const string RendererJavaScript = """
        // --- Decompression ---
        async function inflate(b64) {
          var bin = atob(b64);
          var bytes = new Uint8Array(bin.length);
          for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
          var ds = new DecompressionStream('deflate-raw');
          var writer = ds.writable.getWriter();
          writer.write(bytes);
          writer.close();
          var reader = ds.readable.getReader();
          var chunks = [];
          while (true) {
            var result = await reader.read();
            if (result.done) break;
            chunks.push(result.value);
          }
          var total = chunks.reduce(function(s, c) { return s + c.length; }, 0);
          var merged = new Uint8Array(total);
          var off = 0;
          for (var c of chunks) { merged.set(c, off); off += c.length; }
          return new TextDecoder().decode(merged);
        }

        // --- Global state ---
        var DATA = null;
        var _expandCancel = false;
        var _pendingBuildSort = null;

        // --- Initialization ---
        document.addEventListener('DOMContentLoaded', async function() {
          var el = document.getElementById('record-data');
          var compressed = el.getAttribute('data-z');
          var json = await inflate(compressed);
          DATA = JSON.parse(json);
          render();
          document.getElementById('loading').style.display = 'none';
        });

        // --- Escaping ---
        function esc(s) {
          if (!s) return '';
          return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
        }

        // --- Main render ---
        function render() {
          var container = document.getElementById('tables-container');
          var dumps = DATA.dumps;
          var records = DATA.records;
          var groups = DATA.groups || {};
          var gridCoords = DATA.gridCoords || {};
          var sparseDumps = new Set(DATA.sparseDumps || []);
          var hasCoords = Object.keys(gridCoords).length > 0;

          // Check if we need grouping
          var hasGroups = Object.keys(groups).length > 0;

          if (hasGroups) {
            // Group records by group key
            var grouped = {};
            for (var formId in records) {
              var groupKey = groups[formId] || '(Ungrouped)';
              if (!grouped[groupKey]) grouped[groupKey] = {};
              grouped[groupKey][formId] = records[formId];
            }
            // Sort groups: exterior first, interior last
            var groupKeys = Object.keys(grouped).sort(function(a, b) {
              if (a === 'Interior Cells' && b !== 'Interior Cells') return 1;
              if (b === 'Interior Cells' && a !== 'Interior Cells') return -1;
              return a < b ? -1 : a > b ? 1 : 0;
            });

            // Table of contents
            var tocHtml = '<div class="toc"><strong>Sections:</strong><ul>';
            for (var gi = 0; gi < groupKeys.length; gi++) {
              var gk = groupKeys[gi];
              var gid = 'group-' + gk.replace(/ /g, '-').replace(/\(/g, '_').replace(/\)/g, '_');
              var cnt = Object.keys(grouped[gk]).length;
              tocHtml += '<li><a href="#' + gid + '" onclick="expandGroup(\'' + gid + '\')">'
                + esc(gk) + ' (' + cnt.toLocaleString() + ')</a></li>';
            }
            tocHtml += '</ul></div>';
            container.innerHTML = tocHtml;

            for (var gi = 0; gi < groupKeys.length; gi++) {
              var gk = groupKeys[gi];
              var gid = 'group-' + gk.replace(/ /g, '-').replace(/\(/g, '_').replace(/\)/g, '_');
              var sectionDiv = document.createElement('div');
              sectionDiv.className = 'group-section';
              sectionDiv.id = gid;

              var cnt = Object.keys(grouped[gk]).length;
              var headerEl = document.createElement('h2');
              headerEl.className = 'group-header';
              headerEl.textContent = '\u25B6 ' + gk + ' (' + cnt.toLocaleString() + ')';
              headerEl.onclick = function() { toggleGroup(this); };
              sectionDiv.appendChild(headerEl);

              var contentDiv = document.createElement('div');
              contentDiv.className = 'group-content';
              contentDiv.style.display = 'none';
              var tbl = buildTable(grouped[gk], dumps, sparseDumps, hasCoords, gridCoords);
              contentDiv.appendChild(tbl);
              sectionDiv.appendChild(contentDiv);
              container.appendChild(sectionDiv);
            }
          } else {
            var tbl = buildTable(records, dumps, sparseDumps, hasCoords, gridCoords);
            container.appendChild(tbl);
          }
        }

        // --- Table builder ---
        function buildTable(records, dumps, sparseDumps, hasCoords, gridCoords) {
          var table = document.createElement('table');
          // Header
          var thead = document.createElement('thead');
          var headerRow = document.createElement('tr');
          headerRow.innerHTML =
            '<th class="col-editor sortable" onclick="sortBy(this,\'editor\')">Editor ID '
            + '<span class="sort-indicator"></span></th>'
            + '<th class="col-name sortable" onclick="sortBy(this,\'name\')">Name '
            + '<span class="sort-indicator"></span></th>'
            + (hasCoords
              ? '<th class="col-coords sortable" onclick="sortBy(this,\'coords\')">Coords '
                + '<span class="sort-indicator"></span></th>'
              : '')
            + '<th class="col-formid sortable" onclick="sortBy(this,\'formid\')">Form ID '
            + '<span class="sort-indicator"></span></th>';
          for (var i = 0; i < dumps.length; i++) {
            headerRow.innerHTML +=
              '<th class="build-header build-col-' + i + '" data-dump-idx="' + i
              + '" onclick="filterByBuild(this)">'
              + esc(dumps[i].shortName) + '<br><span class="dump-date">'
              + (dumps[i].isBase ? '(base)' : dumps[i].date.substring(0, 10))
              + '</span><br><span class="build-filter-label"></span></th>';
          }
          thead.appendChild(headerRow);
          table.appendChild(thead);

          // Body
          var tbody = document.createElement('tbody');

          // Sort records by editorId
          var formIds = Object.keys(records).sort(function(a, b) {
            var ea = (records[a].editorId || '').toLowerCase();
            var eb = (records[b].editorId || '').toLowerCase();
            return ea < eb ? -1 : ea > eb ? 1 : 0;
          });

          for (var fi = 0; fi < formIds.length; fi++) {
            var formId = formIds[fi];
            var rec = records[formId];
            var editorId = rec.editorId || '';
            var displayName = rec.displayName || '';
            var present = new Set(rec.present || []);

            // Name change display
            var editorIdDisplay = esc(editorId);
            if (rec.editorIdHistory && rec.editorIdHistory.length > 1) {
              editorIdDisplay = rec.editorIdHistory.map(esc)
                .join('<br><span class="name-change">\u21B3 </span>');
            }
            var nameDisplay = esc(displayName);
            if (rec.nameHistory && rec.nameHistory.length > 1) {
              nameDisplay = rec.nameHistory.map(esc)
                .join('<br><span class="name-change">\u21B3 </span>');
            }

            var coordsDisplay = '';
            if (hasCoords && gridCoords[formId]) {
              coordsDisplay = '(' + gridCoords[formId][0] + ', ' + gridCoords[formId][1] + ')';
            }

            var searchData = (formId + ' ' + editorId + ' ' + displayName + ' ' + coordsDisplay)
              .toLowerCase();

            // Compute badges -- resolve snapshots for each dump
            var resolvedSnapshots = resolveSnapshots(rec, dumps.length);

            // Summary row
            var summaryRow = document.createElement('tr');
            summaryRow.className = 'summary-row';
            summaryRow.setAttribute('data-search', searchData);
            summaryRow.setAttribute('data-editor', editorId);
            summaryRow.setAttribute('data-name', displayName);
            summaryRow.setAttribute('data-coords', coordsDisplay);
            summaryRow.setAttribute('data-formid', formId);
            if (hasCoords && gridCoords[formId]) {
              summaryRow.setAttribute('data-cx', gridCoords[formId][0]);
              summaryRow.setAttribute('data-cy', gridCoords[formId][1]);
            }
            summaryRow.onclick = function() { toggleDetail(this); };

            var rowHtml =
              '<td class="col-editor">' + editorIdDisplay + '</td>'
              + '<td class="col-name">' + nameDisplay + '</td>'
              + (hasCoords ? '<td class="col-coords">' + esc(coordsDisplay) + '</td>' : '')
              + '<td class="col-formid formid">' + formId + '</td>';

            // Status badges per dump
            var previousSnapshotKey = null;
            for (var di = 0; di < dumps.length; di++) {
              var colClass = 'build-col-' + di;
              if (present.has(di)) {
                var snapshotKey = resolvedSnapshots[di] !== null
                  ? resolvedSnapshots[di].key : null;
                if (previousSnapshotKey === null) {
                  if (dumps[di].isBase)
                    rowHtml += '<td class="' + colClass
                      + '"><span class="badge badge-base">BASE</span></td>';
                  else
                    rowHtml += '<td class="' + colClass
                      + '"><span class="badge badge-new">NEW</span></td>';
                } else if (snapshotKey === previousSnapshotKey) {
                  rowHtml += '<td class="' + colClass
                    + '"><span class="badge badge-same">SAME</span></td>';
                } else {
                  rowHtml += '<td class="' + colClass
                    + '"><span class="badge badge-changed">CHANGED</span></td>';
                }
                previousSnapshotKey = snapshotKey;
              } else if (sparseDumps.has(di)) {
                rowHtml += '<td class="' + colClass
                  + '"><span class="badge badge-sparse">SPARSE</span></td>';
              } else {
                if (previousSnapshotKey !== null) {
                  var badgeText = dumps[di].isDmp ? 'NOT PRESENT' : 'REMOVED';
                  rowHtml += '<td class="' + colClass
                    + '"><span class="badge badge-removed">' + badgeText + '</span></td>';
                } else {
                  rowHtml += '<td class="' + colClass
                    + '"><span class="badge badge-absent">&mdash;</span></td>';
                }
              }
            }
            summaryRow.innerHTML = rowHtml;
            tbody.appendChild(summaryRow);

            // Detail row (hidden, rendered on demand)
            var detailRow = document.createElement('tr');
            detailRow.className = 'detail-row';
            detailRow.style.display = 'none';
            detailRow.setAttribute('data-formid', formId);
            var detailHtml =
              '<td class="col-editor"></td><td class="col-name"></td>'
              + (hasCoords ? '<td class="col-coords"></td>' : '')
              + '<td class="col-formid"></td>';
            for (var di = 0; di < dumps.length; di++) {
              detailHtml += '<td class="build-col-' + di + '"></td>';
            }
            detailRow.innerHTML = detailHtml;
            tbody.appendChild(detailRow);
          }

          table.appendChild(tbody);
          return table;
        }

        // --- Snapshot resolution ---
        // Returns array[dumpCount] where each entry is {key, report} or null.
        // key = the snapshot dict key that provides this dump's data (for SAME detection).
        function resolveSnapshots(rec, dumpCount) {
          var result = new Array(dumpCount);
          var present = new Set(rec.present || []);
          var currentKey = null;
          var currentReport = null;
          for (var di = 0; di < dumpCount; di++) {
            if (rec.snapshots.hasOwnProperty(di.toString())) {
              currentKey = di.toString();
              currentReport = rec.snapshots[currentKey];
            }
            if (present.has(di) && currentReport !== null) {
              result[di] = { key: currentKey, report: currentReport };
            } else {
              result[di] = null;
            }
          }
          return result;
        }

        // --- Detail rendering with field-level diff ---
        function renderDetail(detailRow) {
          if (detailRow.dataset.rendered) return;
          detailRow.dataset.rendered = '1';

          var formId = detailRow.getAttribute('data-formid');
          var rec = DATA.records[formId];
          if (!rec) return;

          var dumps = DATA.dumps;
          var sparseDumps = new Set(DATA.sparseDumps || []);
          var resolvedSnapshots = resolveSnapshots(rec, dumps.length);
          var present = new Set(rec.present || []);
          var hasCoords = Object.keys(DATA.gridCoords || {}).length > 0;

          // Fixed columns offset: editor + name + (coords?) + formid
          var fixedCols = hasCoords ? 4 : 3;
          var cells = detailRow.querySelectorAll('td');

          var previousReport = null;
          for (var di = 0; di < dumps.length; di++) {
            var td = cells[fixedCols + di];
            if (!td) continue;

            if (present.has(di) && resolvedSnapshots[di] !== null) {
              var report = resolvedSnapshots[di].report;
              var html = renderFieldDiff(report, previousReport,
                dumps[di].isBase && previousReport === null);
              td.innerHTML = '<div class="record-detail">' + html + '</div>';
              previousReport = report;
            } else if (sparseDumps.has(di)) {
              td.innerHTML = '<div class="record-detail">'
                + '<span class="field-sparse">(sparse dump \u2014 type not loaded)</span></div>';
            } else if (previousReport !== null) {
              var msg = dumps[di].isDmp ? '(not present in this dump)' : '(removed)';
              td.innerHTML = '<div class="record-detail">'
                + '<span class="field-removed">' + msg + '</span></div>';
            }
          }
        }

        // --- Field-level diff between two RecordReport JSON objects ---
        function renderFieldDiff(current, previous, isBase) {
          if (!current || !current.sections) return '';
          var lines = [];

          // Build lookup for previous sections and fields
          var prevSections = {};
          if (previous && previous.sections) {
            for (var si = 0; si < previous.sections.length; si++) {
              var ps = previous.sections[si];
              prevSections[ps.name] = {};
              for (var fi = 0; fi < ps.fields.length; fi++) {
                prevSections[ps.name][ps.fields[fi].key] = ps.fields[fi];
              }
            }
          }

          // Track which previous sections have been matched (for removed-section detection)
          var matchedPrevSections = new Set();

          for (var si = 0; si < current.sections.length; si++) {
            var section = current.sections[si];
            var isNewSection = !prevSections[section.name];
            if (!isNewSection) matchedPrevSections.add(section.name);
            lines.push('<span class="section-header">' + esc(section.name) + '</span>');

            // Build set of current field keys for removed-field detection
            var curFieldKeys = new Set();
            for (var fi = 0; fi < section.fields.length; fi++) {
              curFieldKeys.add(section.fields[fi].key);
            }

            for (var fi = 0; fi < section.fields.length; fi++) {
              var field = section.fields[fi];
              var displayVal = formatValue(field.value);
              var line = '  ' + esc(field.key) + ': ' + displayVal;

              if (previous === null) {
                // First appearance
                if (isBase) {
                  lines.push(line);
                } else {
                  lines.push('<span class="field-new">' + line + '</span>');
                }
              } else if (isNewSection) {
                lines.push('<span class="field-new">' + line + '</span>');
              } else {
                var prevField = prevSections[section.name]
                  ? prevSections[section.name][field.key] : null;
                if (!prevField) {
                  lines.push('<span class="field-new">' + line + '</span>');
                } else if (valuesEqual(field.value, prevField.value)) {
                  lines.push(line);
                } else {
                  var prevDisplay = formatValue(prevField.value);
                  lines.push('<span class="field-changed">  ' + esc(field.key) + ': '
                    + prevDisplay + '<span class="field-arrow"> \u2192 </span>'
                    + displayVal + '</span>');
                }
              }
            }

            // Removed fields within matched sections
            if (!isNewSection && prevSections[section.name]) {
              var prevFields = prevSections[section.name];
              for (var key in prevFields) {
                if (!curFieldKeys.has(key)) {
                  lines.push('<span class="field-removed">  ' + esc(key) + ': '
                    + formatValue(prevFields[key].value) + '</span>');
                }
              }
            }
          }

          // Removed sections (present in previous but not current)
          if (previous && previous.sections) {
            for (var si = 0; si < previous.sections.length; si++) {
              var ps = previous.sections[si];
              if (matchedPrevSections.has(ps.name)) continue;
              lines.push('<span class="field-removed section-header">'
                + esc(ps.name) + '</span>');
              for (var fi = 0; fi < ps.fields.length; fi++) {
                lines.push('<span class="field-removed">  ' + esc(ps.fields[fi].key) + ': '
                  + formatValue(ps.fields[fi].value) + '</span>');
              }
            }
          }

          return lines.join('\n');
        }

        // --- Value formatting ---
        function formatValue(val) {
          if (!val) return '';
          switch (val.type) {
            case 'int':
            case 'float':
            case 'bool':
              return esc(val.display || String(val.raw));
            case 'string':
              return esc(val.raw || '');
            case 'formId':
              return esc(val.display || val.raw || '');
            case 'list':
              if (!val.items || val.items.length === 0)
                return esc(val.display || '(empty)');
              var parts = [];
              for (var i = 0; i < val.items.length; i++) {
                var item = val.items[i];
                if (item.type === 'composite' && item.fields) {
                  var subParts = [];
                  for (var j = 0; j < item.fields.length; j++) {
                    subParts.push(esc(item.fields[j].key) + '='
                      + formatValue(item.fields[j].value));
                  }
                  parts.push('{' + subParts.join(', ') + '}');
                } else {
                  parts.push(formatValue(item));
                }
              }
              return parts.join(', ');
            case 'composite':
              if (!val.fields) return esc(val.display || '');
              var cParts = [];
              for (var i = 0; i < val.fields.length; i++) {
                cParts.push(esc(val.fields[i].key) + '='
                  + formatValue(val.fields[i].value));
              }
              return '{' + cParts.join(', ') + '}';
            default:
              return esc(val.display || '');
          }
        }

        // --- Deep value equality ---
        function valuesEqual(a, b) {
          if (a === b) return true;
          if (!a || !b) return false;
          if (a.type !== b.type) return false;
          switch (a.type) {
            case 'int': return a.raw === b.raw;
            case 'float': return a.raw === b.raw;
            case 'bool': return a.raw === b.raw;
            case 'string': return a.raw === b.raw;
            case 'formId': return a.raw === b.raw;
            case 'list':
              if (!a.items || !b.items) return a.items === b.items;
              if (a.items.length !== b.items.length) return false;
              for (var i = 0; i < a.items.length; i++) {
                if (!valuesEqual(a.items[i], b.items[i])) return false;
              }
              return true;
            case 'composite':
              if (!a.fields || !b.fields) return a.fields === b.fields;
              if (a.fields.length !== b.fields.length) return false;
              for (var i = 0; i < a.fields.length; i++) {
                if (a.fields[i].key !== b.fields[i].key) return false;
                if (!valuesEqual(a.fields[i].value, b.fields[i].value)) return false;
              }
              return true;
            default:
              return a.display === b.display;
          }
        }

        // --- Row expand/collapse ---
        function toggleDetail(summaryRow) {
          var detailRow = summaryRow.nextElementSibling;
          if (detailRow && detailRow.classList.contains('detail-row')) {
            if (detailRow.style.display === 'none') {
              renderDetail(detailRow);
              detailRow.style.display = '';
            } else {
              detailRow.style.display = 'none';
            }
          }
        }
        function expandAll() {
          _expandCancel = false;
          // Expand all collapsed group sections first
          document.querySelectorAll('.group-content').forEach(function(gc) {
            if (gc.style.display === 'none') {
              gc.style.display = '';
              var header = gc.previousElementSibling;
              if (header) header.textContent = header.textContent.replace('\u25B6', '\u25BC');
            }
          });
          var rows = Array.from(document.querySelectorAll('.detail-row:not(.hidden)'));
          var i = 0;
          function batch() {
            if (_expandCancel) return;
            var end = Math.min(i + 50, rows.length);
            for (; i < end; i++) {
              if (_expandCancel) return;
              renderDetail(rows[i]);
              rows[i].style.display = '';
            }
            if (i < rows.length) requestAnimationFrame(batch);
          }
          batch();
        }
        function collapseAll() {
          _expandCancel = true;
          document.querySelectorAll('.detail-row').forEach(function(r) {
            r.style.display = 'none';
          });
          document.querySelectorAll('.group-content').forEach(function(gc) {
            gc.style.display = 'none';
            var header = gc.previousElementSibling;
            if (header) header.textContent = header.textContent.replace('\u25BC', '\u25B6');
          });
        }

        // --- Search / filter ---
        function filterRows() {
          var query = document.getElementById('search').value.toLowerCase();
          var summaryRows = document.querySelectorAll('.summary-row');
          var visible = 0;
          summaryRows.forEach(function(row) {
            var searchData = row.getAttribute('data-search') || '';
            var match = !query || searchData.indexOf(query) !== -1;
            row.classList.toggle('hidden', !match);
            var detail = row.nextElementSibling;
            if (detail && detail.classList.contains('detail-row')) {
              if (!match) detail.classList.add('hidden');
              else detail.classList.remove('hidden');
            }
            if (match) visible++;
          });
          var countEl = document.getElementById('matchCount');
          if (query) {
            countEl.textContent = visible + ' of ' + summaryRows.length + ' records';
          } else {
            countEl.textContent = '';
          }
        }

        // --- Group collapse/expand ---
        function toggleGroup(header) {
          var content = header.nextElementSibling;
          if (content.style.display === 'none') {
            content.style.display = '';
            header.textContent = header.textContent.replace('\u25B6', '\u25BC');
            if (_pendingBuildSort) {
              var tbody = content.querySelector('tbody');
              if (tbody) {
                applyBuildSort(tbody, _pendingBuildSort.idx,
                  _pendingBuildSort.sortType, _pendingBuildSort.fixedCols);
              }
            }
          } else {
            content.style.display = 'none';
            header.textContent = header.textContent.replace('\u25BC', '\u25B6');
          }
        }
        function expandGroup(groupId) {
          var section = document.getElementById(groupId);
          if (!section) return;
          var header = section.querySelector('.group-header');
          var content = section.querySelector('.group-content');
          if (content && content.style.display === 'none') {
            content.style.display = '';
            if (header) header.textContent = header.textContent.replace('\u25B6', '\u25BC');
          }
        }

        // --- Build column pagination ---
        function navBuilds(dir) {
          var nav = document.querySelector('.build-nav');
          if (!nav) return;
          var total = parseInt(nav.dataset.total);
          var size = parseInt(nav.dataset.size);
          var start = parseInt(nav.dataset.start);
          if (dir === 'first') start = 0;
          else if (dir === 'prev3') start = Math.max(0, start - size);
          else if (dir === 'prev1') start = Math.max(0, start - 1);
          else if (dir === 'next1') start = Math.min(total - size, start + 1);
          else if (dir === 'next3') start = Math.min(total - size, start + size);
          else if (dir === 'last') start = Math.max(0, total - size);
          if (start < 0) start = 0;
          nav.dataset.start = start;
          var styleEl = document.getElementById('build-col-style');
          if (!styleEl) {
            styleEl = document.createElement('style');
            styleEl.id = 'build-col-style';
            document.head.appendChild(styleEl);
          }
          var rules = '';
          for (var i = 0; i < total; i++) {
            if (i < start || i >= start + size) {
              rules += '.build-col-' + i + '{display:none !important}';
            }
          }
          styleEl.textContent = rules;
          var label = nav.querySelector('.build-nav-label');
          if (label) label.textContent = 'Builds ' + (start + 1) + '\u2013'
            + Math.min(start + size, total) + ' of ' + total;
          var btns = nav.querySelectorAll('button');
          btns[0].disabled = start === 0;
          btns[1].disabled = start === 0;
          btns[2].disabled = start === 0;
          btns[3].disabled = start + size >= total;
          btns[4].disabled = start + size >= total;
          btns[5].disabled = start + size >= total;
        }

        // --- Build column sort ---
        var _badgeOrder = {'BASE':0,'NEW':1,'CHANGED':2,'REMOVED':3,
          'NOT PRESENT':4,'SAME':5,'SPARSE':6,'\u2014':7,'':8};
        var _badgeTypes = ['','BASE','NEW','CHANGED','REMOVED','NOT PRESENT','SAME'];

        function filterByBuild(th) {
          var table = th.closest('table');
          var tbody = table.querySelector('tbody');
          var idx = parseInt(th.getAttribute('data-dump-idx'));
          var fixedCols = tbody.querySelector('.col-coords') ? 4 : 3;
          var current = th.dataset.filterState || '';
          var curIdx = _badgeTypes.indexOf(current);
          var sortType = _badgeTypes[(curIdx + 1) % _badgeTypes.length];
          th.dataset.filterState = sortType;
          var label = th.querySelector('.build-filter-label');
          if (label) label.textContent = sortType ? '\u25B2 ' + sortType : '';
          table.querySelectorAll('.build-header').forEach(function(h) {
            if (h !== th) {
              h.dataset.filterState = '';
              var l = h.querySelector('.build-filter-label');
              if (l) l.textContent = '';
            }
          });
          applyBuildSort(tbody, idx, sortType, fixedCols);
          _pendingBuildSort = sortType
            ? { idx: idx, sortType: sortType, fixedCols: fixedCols } : null;
          document.querySelectorAll('.group-content').forEach(function(gc) {
            if (gc.style.display !== 'none') {
              var otherTbody = gc.querySelector('tbody');
              if (otherTbody && otherTbody !== tbody) {
                applyBuildSort(otherTbody, idx, sortType, fixedCols);
              }
            }
          });
        }
        function applyBuildSort(tbody, idx, sortType, fixedCols) {
          var summaryRows = Array.from(tbody.querySelectorAll('.summary-row'));
          if (summaryRows.length === 0) return;
          var pairs = summaryRows.map(function(sr) {
            var cells = sr.querySelectorAll('td');
            var cell = cells[fixedCols + idx];
            var badge = cell ? cell.querySelector('.badge') : null;
            var badgeText = badge ? badge.textContent.trim() : '';
            return { summary: sr, detail: sr.nextElementSibling,
              badge: badgeText, formid: sr.getAttribute('data-formid') || '' };
          });
          if (!sortType) {
            pairs.sort(function(a, b) {
              return a.formid < b.formid ? -1 : a.formid > b.formid ? 1 : 0;
            });
          } else {
            pairs.sort(function(a, b) {
              var aMatch = a.badge === sortType ? -1 : 0;
              var bMatch = b.badge === sortType ? -1 : 0;
              if (aMatch !== bMatch) return aMatch - bMatch;
              var aOrd = _badgeOrder[a.badge] !== undefined ? _badgeOrder[a.badge] : 9;
              var bOrd = _badgeOrder[b.badge] !== undefined ? _badgeOrder[b.badge] : 9;
              if (aOrd !== bOrd) return aOrd - bOrd;
              return a.formid < b.formid ? -1 : a.formid > b.formid ? 1 : 0;
            });
          }
          var frag = document.createDocumentFragment();
          pairs.forEach(function(p) {
            frag.appendChild(p.summary);
            if (p.detail) frag.appendChild(p.detail);
          });
          tbody.appendChild(frag);
        }

        // --- Column sort ---
        function sortBy(th, col) {
          var table = th.closest('table');
          var tbody = table.querySelector('tbody');
          var prevCol = table.dataset.sortCol;
          var asc = prevCol === col ? table.dataset.sortAsc !== 'true' : true;
          table.dataset.sortCol = col;
          table.dataset.sortAsc = asc;
          table.querySelectorAll('.sort-indicator').forEach(function(s) { s.textContent = ''; });
          th.querySelector('.sort-indicator').textContent = asc ? '\u25B2' : '\u25BC';
          var summaryRows = Array.from(tbody.querySelectorAll('.summary-row'));
          var pairs = summaryRows.map(function(sr) {
            return { summary: sr, detail: sr.nextElementSibling };
          });
          pairs.sort(function(a, b) {
            var cmp;
            if (col === 'coords') {
              var ax = parseInt(a.summary.getAttribute('data-cx')) || 0;
              var ay = parseInt(a.summary.getAttribute('data-cy')) || 0;
              var bx = parseInt(b.summary.getAttribute('data-cx')) || 0;
              var by = parseInt(b.summary.getAttribute('data-cy')) || 0;
              cmp = ax !== bx ? ax - bx : ay - by;
            } else {
              var va = (a.summary.getAttribute('data-' + col) || '').toLowerCase();
              var vb = (b.summary.getAttribute('data-' + col) || '').toLowerCase();
              cmp = va < vb ? -1 : va > vb ? 1 : 0;
            }
            return asc ? cmp : -cmp;
          });
          var frag = document.createDocumentFragment();
          pairs.forEach(function(p) {
            frag.appendChild(p.summary);
            if (p.detail) frag.appendChild(p.detail);
          });
          tbody.appendChild(frag);
        }
    """;
}
