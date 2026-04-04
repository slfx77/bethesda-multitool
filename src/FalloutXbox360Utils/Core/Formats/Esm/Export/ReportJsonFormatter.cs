using System.Text.Json;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Renders <see cref="RecordReport" /> to JSON using <see cref="Utf8JsonWriter" /> (AOT-compatible).
///     Typed values include their type tag for downstream consumers that need numeric comparison.
/// </summary>
internal static class ReportJsonFormatter
{
    private static readonly JsonWriterOptions DefaultOptions = new() { Indented = true };

    /// <summary>Format a single record report as a JSON string.</summary>
    internal static string Format(RecordReport report, bool indented = true)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, indented ? DefaultOptions : default))
        {
            WriteReport(writer, report);
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Format multiple record reports as a JSON array.</summary>
    internal static string FormatBatch(IReadOnlyList<RecordReport> reports, bool indented = true)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, indented ? DefaultOptions : default))
        {
            writer.WriteStartArray();
            foreach (var report in reports)
            {
                WriteReport(writer, report);
            }

            writer.WriteEndArray();
        }

        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Write a single report to a <see cref="Utf8JsonWriter" />.</summary>
    internal static void WriteReport(Utf8JsonWriter writer, RecordReport report)
    {
        writer.WriteStartObject();
        writer.WriteString("recordType", report.RecordType);
        writer.WriteString("formId", $"0x{report.FormId:X8}");
        writer.WriteNumber("formIdRaw", report.FormId);

        if (report.EditorId != null)
            writer.WriteString("editorId", report.EditorId);
        else
            writer.WriteNull("editorId");

        if (report.DisplayName != null)
            writer.WriteString("displayName", report.DisplayName);
        else
            writer.WriteNull("displayName");

        writer.WritePropertyName("sections");
        writer.WriteStartArray();
        foreach (var section in report.Sections)
        {
            WriteSection(writer, section);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSection(Utf8JsonWriter writer, ReportSection section)
    {
        writer.WriteStartObject();
        writer.WriteString("name", section.Name);

        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        foreach (var field in section.Fields)
        {
            WriteField(writer, field);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteField(Utf8JsonWriter writer, ReportField field)
    {
        writer.WriteStartObject();
        writer.WriteString("key", field.Key);

        if (field.FormIdRef != null)
            writer.WriteString("formIdRef", field.FormIdRef);

        WriteValue(writer, "value", field.Value);
        writer.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter writer, string propertyName, ReportValue value)
    {
        writer.WritePropertyName(propertyName);
        WriteValueInline(writer, value);
    }

    private static void WriteValueInline(Utf8JsonWriter writer, ReportValue value)
    {
        writer.WriteStartObject();

        switch (value)
        {
            case ReportValue.IntVal v:
                writer.WriteString("type", "int");
                writer.WriteNumber("raw", v.Raw);
                writer.WriteString("display", v.Display);
                break;

            case ReportValue.FloatVal v:
                writer.WriteString("type", "float");
                writer.WriteNumber("raw", v.Raw);
                writer.WriteString("display", v.Display);
                break;

            case ReportValue.StringVal v:
                writer.WriteString("type", "string");
                writer.WriteString("raw", v.Raw);
                break;

            case ReportValue.BoolVal v:
                writer.WriteString("type", "bool");
                writer.WriteBoolean("raw", v.Raw);
                writer.WriteString("display", v.Display);
                break;

            case ReportValue.FormIdVal v:
                writer.WriteString("type", "formId");
                writer.WriteString("raw", $"0x{v.Raw:X8}");
                writer.WriteNumber("rawInt", v.Raw);
                writer.WriteString("display", v.Display);
                break;

            case ReportValue.ListVal v:
                writer.WriteString("type", "list");
                writer.WriteString("display", v.Display);
                writer.WritePropertyName("items");
                writer.WriteStartArray();
                foreach (var item in v.Items)
                {
                    WriteValueInline(writer, item);
                }

                writer.WriteEndArray();
                break;

            case ReportValue.CompositeVal v:
                writer.WriteString("type", "composite");
                writer.WriteString("display", v.Display);
                writer.WritePropertyName("fields");
                writer.WriteStartArray();
                foreach (var field in v.Fields)
                {
                    WriteField(writer, field);
                }

                writer.WriteEndArray();
                break;

            default:
                writer.WriteString("type", "unknown");
                writer.WriteString("display", value.Display);
                break;
        }

        writer.WriteEndObject();
    }
}
