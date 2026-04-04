using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Renders a list of <see cref="RecordReport" /> to CSV format.
///     Columns are derived from section.field keys across all records (ragged records handled).
///     Column names use "Section.Key" format (e.g., "Combat Stats.Damage").
/// </summary>
internal static class ReportCsvFormatter
{
    /// <summary>
    ///     Format a batch of same-type records as CSV.
    ///     First pass collects all unique column keys; second pass writes rows.
    /// </summary>
    internal static string Format(IReadOnlyList<RecordReport> reports)
    {
        if (reports.Count == 0) return "";

        // Collect all column keys in order of first appearance
        var columns = new List<string>();
        var columnSet = new HashSet<string>();

        foreach (var report in reports)
        {
            foreach (var section in report.Sections)
            {
                foreach (var field in section.Fields)
                {
                    var colName = $"{section.Name}.{field.Key}";
                    if (columnSet.Add(colName))
                        columns.Add(colName);
                }
            }
        }

        var sb = new StringBuilder();

        // Header row
        sb.Append("FormID,EditorID,DisplayName");
        foreach (var col in columns)
        {
            sb.Append(',');
            sb.Append(CsvEscape(col));
        }

        sb.AppendLine();

        // Data rows
        foreach (var report in reports)
        {
            // Build a lookup for this report's fields
            var fieldValues = new Dictionary<string, string>();
            foreach (var section in report.Sections)
            {
                foreach (var field in section.Fields)
                {
                    fieldValues[$"{section.Name}.{field.Key}"] = field.Value.Display;
                }
            }

            sb.Append($"0x{report.FormId:X8}");
            sb.Append(',');
            sb.Append(CsvEscape(report.EditorId ?? ""));
            sb.Append(',');
            sb.Append(CsvEscape(report.DisplayName ?? ""));

            foreach (var col in columns)
            {
                sb.Append(',');
                if (fieldValues.TryGetValue(col, out var value))
                    sb.Append(CsvEscape(value));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
