using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates static HTML comparison pages with embedded compressed JSON data,
///     rendered client-side by JavaScript. Consumes <see cref="CrossDumpRecordIndex.StructuredRecords" />
///     instead of pre-formatted text, enabling field-level diff computed in the browser.
/// </summary>
internal static class CrossDumpJsonHtmlWriter
{
    /// <summary>
    ///     Generate all HTML files: one per record type (with embedded JSON) plus an index page.
    /// </summary>
    internal static Dictionary<string, string> GenerateAll(CrossDumpRecordIndex index)
    {
        var files = new Dictionary<string, string>();

        foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
        {
            index.RecordGroups.TryGetValue(recordType, out var groups);
            var gridCoords = string.Equals(recordType, "Cell", StringComparison.OrdinalIgnoreCase)
                ? index.CellGridCoords
                : null;
            var html = GenerateRecordTypePage(recordType, formIdMap, index.Dumps, groups, gridCoords);
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
        ComparisonHtmlHelpers.AppendHtmlHeader(sb, $"{recordType} \u2014 Cross-Build Comparison");

        // Initial column visibility — hide columns 3+ until nav buttons change it
        if (dumps.Count > 3)
        {
            sb.Append("  <style id=\"build-col-style\">");
            for (var i = 3; i < dumps.Count; i++)
                sb.Append($".build-col-{i}{{display:none !important}}");
            sb.AppendLine("</style>");
        }

        sb.AppendLine(
            $"  <h1>{ComparisonHtmlHelpers.Esc(recordType)} \u2014 Cross-Build Comparison</h1>");
        sb.AppendLine(
            $"  <p class=\"summary\">{dumps.Count} builds, {formIdMap.Count:N0} records</p>");

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

        // Table skeleton — JS will populate the tbody
        sb.AppendLine("  <div id=\"tables-container\"></div>");

        // Embed compressed JSON data
        var jsonBlob = ComparisonJsonBlobBuilder.Build(formIdMap, dumps, groups, cellGridCoords);
        var compressed = ComparisonHtmlHelpers.CompressToBase64(jsonBlob);
        sb.AppendLine(
            $"  <script type=\"application/json\" id=\"record-data\" data-z=\"{compressed}\"></script>");

        // JavaScript renderer
        sb.AppendLine($"  <script>{ComparisonJsRenderer.Script}</script>");
        ComparisonHtmlHelpers.AppendHtmlFooter(sb);
        return sb.ToString();
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
}
