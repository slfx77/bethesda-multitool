using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Renders <see cref="RecordReport" /> to formatted text matching the existing GeckWriter output style.
///     Section headers use box-drawing characters, fields are aligned with fixed-width labels.
/// </summary>
internal static class ReportTextFormatter
{
    private const int SeparatorWidth = GeckReportHelpers.SeparatorWidth;
    private const char SeparatorChar = GeckReportHelpers.SeparatorChar;
    private const int LabelWidth = 16; // aligns with "  Key:           Value" (2 indent + 14 label + 2 pad)
    private const string Indent = "  ";

    /// <summary>
    ///     Format a single record as a detailed text block (for cross-dump comparison detail rows).
    ///     Produces the same output as the current GeckWriter Append*ReportEntry methods.
    /// </summary>
    internal static string Format(RecordReport report)
    {
        var sb = new StringBuilder();
        AppendRecordHeader(sb, report);
        AppendIdentityFields(sb, report);

        foreach (var section in report.Sections)
        {
            AppendSection(sb, section);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Format a batch of records with a top-level section header and per-record entries.
    ///     Used by <see cref="GeckReportGenerator" /> for full text reports.
    /// </summary>
    internal static string FormatBatch(string sectionTitle, IReadOnlyList<RecordReport> reports)
    {
        var sb = new StringBuilder();
        GeckReportHelpers.AppendSectionHeader(sb, $"{sectionTitle} ({reports.Count})");

        foreach (var report in reports)
        {
            sb.AppendLine();
            AppendRecordHeader(sb, report);
            AppendIdentityFields(sb, report);

            foreach (var section in report.Sections)
            {
                AppendSection(sb, section);
            }
        }

        return sb.ToString();
    }

    private static void AppendRecordHeader(StringBuilder sb, RecordReport report)
    {
        var title = !string.IsNullOrEmpty(report.DisplayName)
            ? $"{report.RecordType.ToUpperInvariant()}: {report.EditorId ?? "(unknown)"} \u2014 {report.DisplayName}"
            : $"{report.RecordType.ToUpperInvariant()}: {report.EditorId ?? "(unknown)"}";

        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
        var padding = (SeparatorWidth - title.Length) / 2;
        sb.AppendLine(new string(' ', Math.Max(0, padding)) + title);
        sb.AppendLine(new string(SeparatorChar, SeparatorWidth));
    }

    private static void AppendIdentityFields(StringBuilder sb, RecordReport report)
    {
        AppendField(sb, "FormID", GeckReportHelpers.FormatFormId(report.FormId));
        AppendField(sb, "Editor ID", report.EditorId ?? "(none)");
        AppendField(sb, "Display Name", report.DisplayName ?? "(none)");
    }

    private static void AppendSection(StringBuilder sb, ReportSection section)
    {
        sb.AppendLine();
        var headerLabel = $"{Indent}\u2500\u2500 {section.Name} ";
        var remaining = SeparatorWidth - headerLabel.Length;
        sb.AppendLine(headerLabel + new string('\u2500', Math.Max(1, remaining)));

        foreach (var field in section.Fields)
        {
            AppendReportField(sb, field);
        }
    }

    private static void AppendReportField(StringBuilder sb, ReportField field)
    {
        switch (field.Value)
        {
            case ReportValue.ListVal listVal:
                AppendListField(sb, field.Key, listVal);
                break;
            case ReportValue.CompositeVal compositeVal:
                // Composite at top level — render as sub-block with header
                sb.AppendLine($"{Indent}{field.Key}:");
                foreach (var subField in compositeVal.Fields)
                {
                    AppendField(sb, $"  {subField.Key}", subField.Value.Display);
                }

                break;
            default:
                AppendField(sb, field.Key, field.Value.Display);
                break;
        }
    }

    private static void AppendListField(StringBuilder sb, string label, ReportValue.ListVal listVal)
    {
        if (listVal.Items.Count == 0)
        {
            AppendField(sb, label, "(none)");
            return;
        }

        sb.AppendLine($"{Indent}{label}:");
        foreach (var item in listVal.Items)
        {
            switch (item)
            {
                case ReportValue.CompositeVal composite:
                    // Render composite items as indented sub-blocks
                    sb.AppendLine($"{Indent}  - {composite.Display}");
                    foreach (var subField in composite.Fields)
                    {
                        sb.AppendLine($"{Indent}    {subField.Key,-LabelWidth}{subField.Value.Display}");
                    }

                    break;
                default:
                    sb.AppendLine($"{Indent}  - {item.Display}");
                    break;
            }
        }
    }

    private static void AppendField(StringBuilder sb, string label, string value)
    {
        sb.AppendLine($"{Indent}{label + ":",-LabelWidth}{value}");
    }
}
