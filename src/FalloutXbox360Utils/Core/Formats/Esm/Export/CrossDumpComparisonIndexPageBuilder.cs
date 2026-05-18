using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CrossDumpComparisonIndexPageBuilder
{
    public static string Generate(CrossDumpRecordIndex index)
    {
        var summaries = index.StructuredRecords
            .OrderBy(entry => entry.Key)
            .Select(entry => BuildRecordTypeSummary(entry.Key, entry.Value, index.Dumps.Count))
            .ToList();

        return Generate(index.Dumps, summaries);
    }

    public static string Generate(
        IReadOnlyList<DumpSnapshot> dumps,
        IReadOnlyList<CrossDumpRecordTypeSummary> recordTypes)
    {
        var sb = new StringBuilder();
        ComparisonHtmlHelpers.AppendHtmlHeader(sb, "Cross-Build Comparison Index");

        sb.AppendLine("  <h1>Cross-Build Comparison Index</h1>");

        var total = recordTypes.Sum(summary => summary.FormIdCount);
        sb.AppendLine(
            $"  <p class=\"summary\">{dumps.Count} builds, {total:N0} total records</p>");

        AppendBuildsTable(sb, dumps);
        AppendRecordTypeTable(sb, dumps, recordTypes);

        ComparisonHtmlHelpers.AppendHtmlFooter(sb);
        return sb.ToString();
    }

    public static CrossDumpRecordTypeSummary BuildRecordTypeSummary(
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap,
        int dumpCount)
    {
        var dumpCounts = new int[dumpCount];
        foreach (var dumpMap in formIdMap.Values)
        {
            foreach (var dumpIndex in dumpMap.Keys)
            {
                if ((uint)dumpIndex < (uint)dumpCounts.Length)
                {
                    dumpCounts[dumpIndex]++;
                }
            }
        }

        return new CrossDumpRecordTypeSummary(recordType, formIdMap.Count, dumpCounts);
    }

    private static void AppendBuildsTable(StringBuilder sb, IReadOnlyList<DumpSnapshot> dumps)
    {
        sb.AppendLine("  <h2>Builds</h2>");
        sb.AppendLine("  <table class=\"compact\">");
        sb.AppendLine("    <thead><tr><th>#</th><th>File</th><th>Build Date</th></tr></thead>");
        sb.AppendLine("    <tbody>");
        for (var i = 0; i < dumps.Count; i++)
        {
            var d = dumps[i];
            var displayFileName = d.IsBase ? d.ShortName : d.FileName;
            var displayDate = d.IsBase ? "(base)" : $"{d.FileDate:yyyy-MM-dd HH:mm}";
            if (!d.IsBase && !string.IsNullOrWhiteSpace(d.DateSource))
            {
                displayDate += $"<br><span class=\"muted\">{ComparisonHtmlHelpers.Esc(d.DateSource)}</span>";
            }

            sb.AppendLine(
                $"      <tr><td>{i + 1}</td><td>{ComparisonHtmlHelpers.Esc(displayFileName)}</td><td>{displayDate}</td></tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");
    }

    private static void AppendRecordTypeTable(
        StringBuilder sb,
        IReadOnlyList<DumpSnapshot> dumps,
        IEnumerable<CrossDumpRecordTypeSummary> recordTypes)
    {
        sb.AppendLine("  <h2>Record Types</h2>");
        sb.AppendLine("  <table class=\"compact\">");
        sb.AppendLine("    <thead>");
        sb.AppendLine("      <tr><th>Type</th><th>Records</th>");
        foreach (var d in dumps)
        {
            sb.AppendLine($"        <th>{ComparisonHtmlHelpers.Esc(d.ShortName)}</th>");
        }

        sb.AppendLine("      </tr>");
        sb.AppendLine("    </thead>");
        sb.AppendLine("    <tbody>");

        foreach (var summary in recordTypes.OrderBy(summary => summary.RecordType, StringComparer.OrdinalIgnoreCase))
        {
            var filename = $"compare_{summary.RecordType.ToLowerInvariant()}.html";
            sb.Append(
                $"      <tr><td><a href=\"{filename}\">{ComparisonHtmlHelpers.Esc(summary.RecordType)}</a></td>");
            sb.Append($"<td>{summary.FormIdCount:N0}</td>");
            for (var dumpIdx = 0; dumpIdx < dumps.Count; dumpIdx++)
            {
                var count = dumpIdx < summary.DumpCounts.Count ? summary.DumpCounts[dumpIdx] : 0;
                sb.Append($"<td>{count:N0}</td>");
            }

            sb.AppendLine("</tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");
    }
}
