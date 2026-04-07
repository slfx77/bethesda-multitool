using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates static HTML comparison pages with embedded compressed JSON data,
///     rendered client-side by JavaScript. Consumes <see cref="CrossDumpRecordIndex.StructuredRecords" />
///     instead of pre-formatted text, enabling field-level diff computed in the browser.
/// </summary>
internal static class CrossDumpJsonHtmlWriter
{
    private const int DefaultMaxInlineCompressedPayloadLength = 8 * 1024 * 1024;
    private const int DefaultMaxCellJsonPayloadLength = 96 * 1024 * 1024;
    private const int PayloadChunkSize = 512 * 1024;

    private sealed record PayloadBundle(string CompressedPayload, int JsonLength);

    private sealed record CellChunkPayload(
        Dictionary<uint, Dictionary<int, RecordReport>> Records,
        Dictionary<uint, (int X, int Y)>? GridCoords,
        string CompressedPayload);

    private sealed record CellPageChunk(
        string GroupName,
        string FileName,
        int RecordCount,
        int PartIndex,
        int PartCount,
        Dictionary<uint, Dictionary<int, RecordReport>> Records,
        Dictionary<uint, (int X, int Y)>? GridCoords,
        string CompressedPayload);

    /// <summary>
    ///     Generate all HTML files: one per record type (with embedded JSON) plus an index page.
    /// </summary>
    internal static Dictionary<string, string> GenerateAll(
        CrossDumpRecordIndex index,
        int maxInlineCompressedPayloadLength = DefaultMaxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength = DefaultMaxCellJsonPayloadLength)
    {
        var files = new Dictionary<string, string>();

        foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
        {
            // Resolve groups: dialogue uses dual group sets, others use single group map
            Dictionary<uint, string>? groups = null;
            Dictionary<uint, string>? alternateGroups = null;
            string? defaultGroupMode = null;
            Dictionary<uint, Dictionary<string, string>>? metadata = null;

            if (string.Equals(recordType, "Dialogue", StringComparison.OrdinalIgnoreCase))
            {
                index.RecordGroups.TryGetValue("Dialogue_Quest", out groups);
                index.RecordGroups.TryGetValue("Dialogue_NPC", out alternateGroups);
                defaultGroupMode = "Quest";
                index.RecordMetadata.TryGetValue(recordType, out metadata);
            }
            else
            {
                index.RecordGroups.TryGetValue(recordType, out groups);
            }

            var gridCoords = string.Equals(recordType, "Cell", StringComparison.OrdinalIgnoreCase)
                ? index.CellGridCoords
                : null;
            foreach (var (filename, html) in GenerateRecordTypeFiles(
                         recordType,
                         formIdMap,
                         index.Dumps,
                         groups,
                         alternateGroups,
                         defaultGroupMode,
                         metadata,
                         gridCoords,
                         maxInlineCompressedPayloadLength,
                         maxCellJsonPayloadLength))
            {
                files[filename] = html;
            }
        }

        files["index.html"] = GenerateIndexPage(index);
        return files;
    }

    private static Dictionary<string, string> GenerateRecordTypeFiles(
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string>? groups,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        int maxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength)
    {
        var files = new Dictionary<string, string>();
        var payload = BuildCompressedPayload(
            formIdMap,
            dumps,
            recordType,
            groups,
            alternateGroups,
            defaultGroupMode,
            metadata,
            cellGridCoords);

        if (ShouldUseChunkedPage(recordType, groups, payload,
                maxInlineCompressedPayloadLength, maxCellJsonPayloadLength))
        {
            files[$"compare_{recordType.ToLowerInvariant()}.html"] = GenerateChunkedPage(
                recordType, formIdMap, dumps, groups!, cellGridCoords, metadata,
                alternateGroups, defaultGroupMode,
                maxInlineCompressedPayloadLength, maxCellJsonPayloadLength);
            return files;
        }

        files[$"compare_{recordType.ToLowerInvariant()}.html"] = GenerateRecordTypePage(
            recordType,
            formIdMap,
            dumps,
            groups,
            alternateGroups,
            defaultGroupMode,
            metadata,
            cellGridCoords,
            payload.CompressedPayload);
        return files;
    }

    private static string GenerateRecordTypePage(
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string>? groups,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        string compressedPayload,
        string? titleOverride = null,
        string? headingOverride = null,
        string? summaryOverride = null,
        string backLinkHref = "index.html",
        string backLinkText = "&larr; Back to index",
        string? noticeHtml = null)
    {
        var sb = new StringBuilder();
        var pageTitle = titleOverride ?? $"{recordType} \u2014 Cross-Build Comparison";
        var pageHeading = headingOverride ?? $"{recordType} \u2014 Cross-Build Comparison";
        var pageSummary = summaryOverride ?? $"{dumps.Count} builds, {formIdMap.Count:N0} records";

        ComparisonHtmlHelpers.AppendHtmlHeader(sb, pageTitle);

        // Initial column visibility — hide columns 3+ until nav buttons change it
        if (dumps.Count > 3)
        {
            sb.Append("  <style id=\"build-col-style\">");
            for (var i = 3; i < dumps.Count; i++)
                sb.Append($".build-col-{i}{{display:none !important}}");
            sb.AppendLine("</style>");
        }

        sb.AppendLine(
            $"  <h1>{ComparisonHtmlHelpers.Esc(pageHeading)} </h1>");
        sb.AppendLine($"  <p class=\"summary\">{ComparisonHtmlHelpers.Esc(pageSummary)}</p>");
        if (!string.IsNullOrEmpty(noticeHtml))
        {
            sb.AppendLine($"  <div class=\"summary\">{noticeHtml}</div>");
        }

        // Navigation + controls
        sb.AppendLine("  <div class=\"controls\">");
        sb.AppendLine($"    <a href=\"{backLinkHref}\">{backLinkText}</a>");
        sb.AppendLine(
            "    <input type=\"text\" id=\"search\" placeholder=\"Search by FormID, EditorID, or name...\" oninput=\"filterRows()\">");
        sb.AppendLine("    <button onclick=\"expandAll()\">Expand All</button>");
        sb.AppendLine("    <button onclick=\"collapseAll()\">Collapse All</button>");
        sb.AppendLine("    <span id=\"matchCount\" class=\"match-count\"></span>");

        // Dialogue-specific: group-mode radio buttons
        if (alternateGroups != null && defaultGroupMode != null)
        {
            sb.AppendLine("    <div class=\"group-mode-selector\">");
            sb.AppendLine("      <label>Group by:</label>");
            sb.AppendLine(
                "      <label><input type=\"radio\" name=\"groupMode\" value=\"Quest\" checked onchange=\"switchGroupMode(this.value)\"> Quest</label>");
            sb.AppendLine(
                "      <label><input type=\"radio\" name=\"groupMode\" value=\"NPC\" onchange=\"switchGroupMode(this.value)\"> NPC</label>");
            sb.AppendLine("    </div>");
        }

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

        // Table skeleton — JS will populate the tbody
        sb.AppendLine("  <div id=\"tables-container\"></div>");

        // Embed compressed JSON data in chunked script text blocks rather than a giant
        // HTML attribute. Large attribute values can be truncated by browsers/DOM parsing,
        // which surfaces later as "Unexpected end of JSON input" during hydration.
        AppendPayloadScripts(sb, compressedPayload);

        // JavaScript renderer
        sb.AppendLine($"  <script>{ComparisonJsRenderer.Script}</script>");
        ComparisonHtmlHelpers.AppendHtmlFooter(sb);
        return sb.ToString();
    }

    private static bool ShouldUseChunkedPage(
        string recordType,
        Dictionary<uint, string>? groups,
        PayloadBundle payload,
        int maxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength)
    {
        return groups is { Count: > 0 }
               && (payload.CompressedPayload.Length > maxInlineCompressedPayloadLength
                   || payload.JsonLength > maxCellJsonPayloadLength);
    }

    private static PayloadBundle BuildCompressedPayload(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        string recordType,
        Dictionary<uint, string>? groups,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        Dictionary<uint, (int X, int Y)>? cellGridCoords)
    {
        var jsonBlob = ComparisonJsonBlobBuilder.Build(
            formIdMap,
            dumps,
            recordType,
            groups,
            alternateGroups,
            defaultGroupMode,
            metadata,
            cellGridCoords);
        return new PayloadBundle(
            ComparisonHtmlHelpers.CompressToBase64(jsonBlob),
            Encoding.UTF8.GetByteCount(jsonBlob));
    }

    /// <summary>
    ///     Generate a single HTML page with per-group chunked data.
    ///     Each group's records are in a separate compressed script tag, loaded on demand
    ///     when the group is expanded. This keeps any single JSON parse under 5MB.
    /// </summary>
    private static string GenerateChunkedPage(
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string> groups,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        Dictionary<uint, Dictionary<string, string>>? metadata,
        Dictionary<uint, string>? alternateGroups,
        string? defaultGroupMode,
        int maxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength)
    {
        // Build the index blob (metadata only — no records)
        var (indexBlob, chunks) = ComparisonJsonBlobBuilder.BuildChunked(
            formIdMap, dumps, recordType, groups, cellGridCoords, metadata);

        var sb = new StringBuilder();
        var pageTitle = $"{recordType} \u2014 Cross-Build Comparison";
        ComparisonHtmlHelpers.AppendHtmlHeader(sb, pageTitle);

        if (dumps.Count > 3)
        {
            sb.Append("  <style id=\"build-col-style\">");
            for (var i = 3; i < dumps.Count; i++)
                sb.Append($".build-col-{i}{{display:none !important}}");
            sb.AppendLine("</style>");
        }

        sb.AppendLine($"  <h1>{ComparisonHtmlHelpers.Esc(pageTitle)}</h1>");
        sb.AppendLine(
            $"  <p class=\"summary\">{dumps.Count} builds, {formIdMap.Count:N0} records (chunked by group)</p>");

        // Controls
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

        sb.AppendLine("  <div id=\"loading\">Loading index...</div>");
        sb.AppendLine("  <div id=\"tables-container\"></div>");

        // Index blob (small — just metadata, groups, gridCoords)
        AppendPayloadScripts(sb, ComparisonHtmlHelpers.CompressToBase64(indexBlob));

        // Per-group chunk blobs
        for (var i = 0; i < chunks.Count; i++)
        {
            var (groupKey, chunkBlob) = chunks[i];
            var chunkId = $"chunk-{i}";
            sb.AppendLine(
                $"  <script type=\"application/json\" id=\"{chunkId}\" " +
                $"data-group=\"{ComparisonHtmlHelpers.Esc(groupKey)}\" " +
                $"data-z=\"{ComparisonHtmlHelpers.CompressToBase64(chunkBlob)}\"></script>");
        }

        sb.AppendLine($"  <script>{ComparisonJsRenderer.Script}</script>");
        ComparisonHtmlHelpers.AppendHtmlFooter(sb);
        return sb.ToString();
    }

    private static Dictionary<string, string> GenerateSplitCellPages(
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string> groups,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        int maxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength)
    {
        var files = new Dictionary<string, string>();
        var usedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupedRecords = new Dictionary<string, List<KeyValuePair<uint, Dictionary<int, RecordReport>>>>(
            StringComparer.Ordinal);

        foreach (var entry in formIdMap.OrderBy(kvp => kvp.Key))
        {
            var groupName = groups.TryGetValue(entry.Key, out var explicitGroup) && !string.IsNullOrWhiteSpace(explicitGroup)
                ? explicitGroup
                : "(Ungrouped)";
            if (!groupedRecords.TryGetValue(groupName, out var bucket))
            {
                bucket = [];
                groupedRecords[groupName] = bucket;
            }

            bucket.Add(entry);
        }

        var allPages = new List<CellPageChunk>();
        foreach (var groupName in OrderGroupKeys(groupedRecords.Keys))
        {
            var entries = groupedRecords[groupName];
            var payloads = SplitCellGroupPayloads(
                entries,
                dumps,
                cellGridCoords,
                maxInlineCompressedPayloadLength,
                maxCellJsonPayloadLength);
            var stem = MakeUniqueStem($"compare_cell_{Slugify(groupName)}", usedStems);
            for (var i = 0; i < payloads.Count; i++)
            {
                var payload = payloads[i];
                var fileName = payloads.Count == 1
                    ? $"{stem}.html"
                    : $"{stem}_part{i + 1}.html";
                allPages.Add(new CellPageChunk(
                    groupName,
                    fileName,
                    payload.Records.Count,
                    i + 1,
                    payloads.Count,
                    payload.Records,
                    payload.GridCoords,
                    payload.CompressedPayload));
            }
        }

        files["compare_cell.html"] = GenerateSplitCellLandingPage(dumps, allPages);

        foreach (var page in allPages)
        {
            var pageLabel = page.PartCount == 1
                ? page.GroupName
                : $"{page.GroupName} (Part {page.PartIndex} of {page.PartCount})";
            var pageNotice = page.PartCount == 1
                ? $"Generated as a standalone Cell subpage because the full Cell comparison exceeds browser payload limits."
                : $"Generated as one chunk of a split Cell comparison because the full Cell payload exceeds browser payload limits.";
            files[page.FileName] = GenerateRecordTypePage(
                "Cell",
                page.Records,
                dumps,
                null,
                null,
                null,
                null,
                page.GridCoords,
                page.CompressedPayload,
                titleOverride: $"Cell \u2014 {pageLabel} \u2014 Cross-Build Comparison",
                headingOverride: $"Cell \u2014 {pageLabel}",
                summaryOverride: $"{dumps.Count} builds, {page.RecordCount:N0} records",
                backLinkHref: "compare_cell.html",
                backLinkText: "&larr; Back to Cell index",
                noticeHtml: ComparisonHtmlHelpers.Esc(pageNotice));
        }

        return files;
    }

    private static List<CellChunkPayload> SplitCellGroupPayloads(
        List<KeyValuePair<uint, Dictionary<int, RecordReport>>> entries,
        List<DumpSnapshot> dumps,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        int maxInlineCompressedPayloadLength,
        int maxCellJsonPayloadLength)
    {
        var accepted = new List<CellChunkPayload>();
        SplitRecursive(entries);
        return accepted;

        void SplitRecursive(List<KeyValuePair<uint, Dictionary<int, RecordReport>>> slice)
        {
            var recordMap = slice.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            var coords = cellGridCoords == null
                ? null
                : slice.Where(kvp => cellGridCoords.ContainsKey(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => cellGridCoords[kvp.Key]);
            var payload = BuildCompressedPayload(
                recordMap,
                dumps,
                "Cell",
                null,
                null,
                null,
                null,
                coords);

            if ((payload.CompressedPayload.Length <= maxInlineCompressedPayloadLength
                 && payload.JsonLength <= maxCellJsonPayloadLength)
                || slice.Count <= 1)
            {
                accepted.Add(new CellChunkPayload(recordMap, coords, payload.CompressedPayload));
                return;
            }

            var midpoint = slice.Count / 2;
            SplitRecursive(slice.Take(midpoint).ToList());
            SplitRecursive(slice.Skip(midpoint).ToList());
        }
    }

    private static string GenerateSplitCellLandingPage(
        List<DumpSnapshot> dumps,
        List<CellPageChunk> pages)
    {
        var sb = new StringBuilder();
        ComparisonHtmlHelpers.AppendHtmlHeader(sb, "Cell \u2014 Cross-Build Comparison");
        sb.AppendLine("  <h1>Cell \u2014 Cross-Build Comparison</h1>");
        sb.AppendLine($"  <p class=\"summary\">{dumps.Count} builds, {pages.Sum(p => p.RecordCount):N0} records</p>");
        sb.AppendLine("  <p class=\"summary\">The full Cell report was split into smaller pages because browsers do not reliably handle the monolithic Cell payload.</p>");
        sb.AppendLine("  <div class=\"controls\">");
        sb.AppendLine("    <a href=\"index.html\">&larr; Back to index</a>");
        sb.AppendLine("  </div>");
        sb.AppendLine("  <table class=\"compact\">");
        sb.AppendLine("    <thead><tr><th>Group</th><th>Records</th><th>Pages</th></tr></thead>");
        sb.AppendLine("    <tbody>");

        foreach (var group in pages.GroupBy(p => p.GroupName).OrderBy(g => OrderGroupKey(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.Append("      <tr>");
            sb.Append($"<td>{ComparisonHtmlHelpers.Esc(group.Key)}</td>");
            sb.Append($"<td>{group.Sum(p => p.RecordCount):N0}</td>");
            sb.Append("<td>");

            var pageLinks = group.OrderBy(p => p.PartIndex).ToList();
            for (var i = 0; i < pageLinks.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(" | ");
                }

                var label = pageLinks.Count == 1 ? "Open" : $"Part {pageLinks[i].PartIndex}";
                sb.Append($"<a href=\"{pageLinks[i].FileName}\">{ComparisonHtmlHelpers.Esc(label)}</a>");
            }

            sb.AppendLine("</td></tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");
        ComparisonHtmlHelpers.AppendHtmlFooter(sb);
        return sb.ToString();
    }

    private static IEnumerable<string> OrderGroupKeys(IEnumerable<string> groupKeys)
    {
        return groupKeys
            .OrderBy(OrderGroupKey)
            .ThenBy(key => key, StringComparer.Ordinal);
    }

    private static int OrderGroupKey(string groupKey)
    {
        return string.Equals(groupKey, "Interior Cells", StringComparison.Ordinal) ? 1 : 0;
    }

    private static string MakeUniqueStem(string baseStem, ISet<string> usedStems)
    {
        var candidate = baseStem;
        var suffix = 2;
        while (!usedStems.Add(candidate))
        {
            candidate = $"{baseStem}_{suffix++}";
        }

        return candidate;
    }

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "group";
        }

        var sb = new StringBuilder(value.Length);
        var lastWasSeparator = false;
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                sb.Append('_');
                lastWasSeparator = true;
            }
        }

        var slug = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(slug) ? "group" : slug;
    }

    private static string GenerateIndexPage(CrossDumpRecordIndex index)
    {
        var sb = new StringBuilder();
        ComparisonHtmlHelpers.AppendHtmlHeader(sb, "Cross-Build Comparison Index");

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
                $"      <tr><td>{i + 1}</td><td>{ComparisonHtmlHelpers.Esc(displayFileName)}</td><td>{displayDate}</td></tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");

        // Record type summary with links
        sb.AppendLine("  <h2>Record Types</h2>");
        sb.AppendLine("  <table class=\"compact\">");
        sb.AppendLine("    <thead>");
        sb.AppendLine("      <tr><th>Type</th><th>Records</th>");
        foreach (var d in index.Dumps)
            sb.AppendLine($"        <th>{ComparisonHtmlHelpers.Esc(d.ShortName)}</th>");
        sb.AppendLine("      </tr>");
        sb.AppendLine("    </thead>");
        sb.AppendLine("    <tbody>");

        foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
        {
            var filename = $"compare_{recordType.ToLowerInvariant()}.html";
            sb.Append(
                $"      <tr><td><a href=\"{filename}\">{ComparisonHtmlHelpers.Esc(recordType)}</a></td>");
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

        ComparisonHtmlHelpers.AppendHtmlFooter(sb);
        return sb.ToString();
    }

    private static void AppendPayloadScripts(StringBuilder sb, string compressed)
    {
        if (compressed.Length <= PayloadChunkSize)
        {
            sb.AppendLine(
                $"  <script type=\"application/octet-stream\" id=\"record-data\">{compressed}</script>");
            return;
        }

        sb.AppendLine("  <div id=\"record-data\" hidden>");
        for (var offset = 0; offset < compressed.Length; offset += PayloadChunkSize)
        {
            var length = Math.Min(PayloadChunkSize, compressed.Length - offset);
            var chunk = compressed.Substring(offset, length);
            sb.AppendLine(
                $"    <script type=\"application/octet-stream\" class=\"record-data-chunk\">{chunk}</script>");
        }

        sb.AppendLine("  </div>");
    }
}
