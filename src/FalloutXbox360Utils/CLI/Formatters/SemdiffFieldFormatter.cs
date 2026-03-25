using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Utils;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Field formatting and display logic for the semantic diff command.
/// </summary>
internal static class SemdiffFieldFormatter
{
    /// <summary>
    ///     Escapes brackets for Spectre.Console markup.
    /// </summary>
    internal static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }

    internal static void DisplayRecordDiff(SemdiffTypes.RecordDiff diff,
        string labelA = "File A", string labelB = "File B")
    {
        var formIdStr = $"0x{diff.FormId:X8}";
        var edidA = diff.RecordA?.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.Data;
        var edidB = diff.RecordB?.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.Data;
        var edid = edidA ?? edidB;
        var edidStr = edid != null ? Encoding.ASCII.GetString(edid).TrimEnd('\0') : "(no EDID)";

        AnsiConsole.MarkupLine($"[bold cyan]═══ {diff.RecordType} {formIdStr} - {edidStr} ═══[/]");

        switch (diff.DiffType)
        {
            case SemdiffTypes.DiffType.OnlyInA:
                AnsiConsole.MarkupLine($"[yellow]Record only exists in {labelA}[/]");
                return;
            case SemdiffTypes.DiffType.OnlyInB:
                AnsiConsole.MarkupLine($"[yellow]Record only exists in {labelB}[/]");
                return;
        }

        if (diff.FieldDiffs == null || diff.FieldDiffs.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Records are identical[/]");
            return;
        }

        // Group diffs by subrecord
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn(new TableColumn("[bold]Subrecord[/]").Width(10));
        table.AddColumn(new TableColumn("[bold]Field[/]").Width(20));
        table.AddColumn(new TableColumn($"[bold]{labelA}[/]").Width(30));
        table.AddColumn(new TableColumn($"[bold]{labelB}[/]").Width(30));
        table.AddColumn(new TableColumn("[bold]Status[/]").Width(12));

        foreach (var fieldDiff in diff.FieldDiffs)
        {
            DisplayFieldDiff(table, fieldDiff);
        }

        AnsiConsole.Write(table);
    }

    private static void DisplayFieldDiff(Table table, SemdiffTypes.FieldDiff diff)
    {
        var schema = SubrecordSchemaRegistry.GetSchema(diff.Signature, diff.RecordType,
            diff.DataA?.Length ?? diff.DataB?.Length ?? 0);

        if (diff.Message != null)
        {
            // Only in A or only in B
            var valueStr = diff.DataA != null
                ? FormatSubrecordValue(diff.Signature, diff.DataA, diff.BigEndianA, diff.RecordType)
                : FormatSubrecordValue(diff.Signature, diff.DataB!, diff.BigEndianB, diff.RecordType);
            table.AddRow(
                $"[yellow]{diff.Signature}[/]",
                "-",
                diff.DataA != null ? EscapeMarkup(valueStr) : "[grey]-[/]",
                diff.DataB != null ? EscapeMarkup(valueStr) : "[grey]-[/]",
                $"[yellow]{EscapeMarkup(diff.Message)}[/]"
            );
            return;
        }

        // Both have data - decode fields
        if (schema != null && schema.Fields.Length > 0)
        {
            // Schema-based field-by-field comparison
            var fieldsA = DecodeSchemaFields(diff.DataA!, schema, diff.BigEndianA);
            var fieldsB = DecodeSchemaFields(diff.DataB!, schema, diff.BigEndianB);

            var allFields = fieldsA.Keys.Union(fieldsB.Keys).OrderBy(k => k).ToList();
            var isFirst = true;

            foreach (var fieldName in allFields)
            {
                var hasA = fieldsA.TryGetValue(fieldName, out var valA);
                var hasB = fieldsB.TryGetValue(fieldName, out var valB);

                var status = hasA && hasB && valA == valB ? "[green]MATCH[/]" : "[red]DIFF[/]";

                table.AddRow(
                    isFirst ? $"[yellow]{diff.Signature}[/]" : "",
                    EscapeMarkup(fieldName),
                    hasA ? EscapeMarkup(valA!) : "[grey]-[/]",
                    hasB ? EscapeMarkup(valB!) : "[grey]-[/]",
                    status
                );
                isFirst = false;
            }
        }
        else
        {
            // No schema - show raw values
            var valA = FormatSubrecordValue(diff.Signature, diff.DataA!, diff.BigEndianA, diff.RecordType);
            var valB = FormatSubrecordValue(diff.Signature, diff.DataB!, diff.BigEndianB, diff.RecordType);
            var status = valA == valB ? "[green]MATCH[/]" : "[red]DIFF[/]";

            table.AddRow(
                $"[yellow]{diff.Signature}[/]",
                $"({diff.DataA!.Length} bytes)",
                EscapeMarkup(valA),
                EscapeMarkup(valB),
                status
            );
        }
    }

    private static Dictionary<string, string> DecodeSchemaFields(byte[] data, SubrecordSchema schema, bool bigEndian)
    {
        var fields = new Dictionary<string, string>();
        var offset = 0;

        foreach (var field in schema.Fields)
        {
            if (offset >= data.Length)
            {
                break;
            }

            var fieldSize = GetFieldSize(field.Type, field.Size);
            if (offset + fieldSize > data.Length)
            {
                break;
            }

            var value = FieldValueDecoder.Decode(data.AsSpan(offset, fieldSize), field.Type, bigEndian);
            fields[field.Name] = value;
            offset += fieldSize;
        }

        return fields;
    }

    internal static int GetFieldSize(SubrecordFieldType type, int? explicitSize)
    {
        if (explicitSize.HasValue)
        {
            return explicitSize.Value;
        }

        return type switch
        {
            SubrecordFieldType.UInt8 or SubrecordFieldType.Int8 => 1,
            SubrecordFieldType.UInt16 or SubrecordFieldType.Int16 or SubrecordFieldType.UInt16LittleEndian => 2,
            SubrecordFieldType.UInt32 or SubrecordFieldType.Int32 or SubrecordFieldType.Float
                or SubrecordFieldType.FormId or SubrecordFieldType.FormIdLittleEndian
                or SubrecordFieldType.ColorRgba or SubrecordFieldType.ColorArgb => 4,
            SubrecordFieldType.UInt64 or SubrecordFieldType.Int64 or SubrecordFieldType.Double => 8,
            SubrecordFieldType.Vec3 => 12,
            SubrecordFieldType.Quaternion => 16,
            SubrecordFieldType.PosRot => 24,
            _ => 4
        };
    }

    internal static string FormatSubrecordValue(string sig, byte[] data, bool bigEndian, string recordType)
    {
        // Check for string subrecords
        if (SubrecordSchemaRegistry.IsStringSubrecord(sig, recordType))
        {
            return Encoding.ASCII.GetString(data).TrimEnd('\0');
        }

        // Check schema
        var schema = SubrecordSchemaRegistry.GetSchema(sig, recordType, data.Length);
        if (schema != null && schema.Fields.Length > 0)
        {
            // Return first field value for simple subrecords
            var firstField = schema.Fields[0];
            var size = GetFieldSize(firstField.Type, firstField.Size);
            if (size <= data.Length)
            {
                return FieldValueDecoder.Decode(data.AsSpan(0, size), firstField.Type, bigEndian);
            }
        }

        // Common simple types
        return data.Length switch
        {
            1 => data[0].ToString(),
            2 => BinaryUtils.ReadUInt16(data, 0, bigEndian).ToString(),
            4 when sig.EndsWith("ID", StringComparison.Ordinal) || sig == "NAME" || sig == "SCRI" || sig == "TPLT" =>
                $"0x{BinaryUtils.ReadUInt32(data, 0, bigEndian):X8}",
            4 => FormatAs4Bytes(data, bigEndian),
            _ => FormatBytes(data)
        };
    }

    private static string FormatAs4Bytes(byte[] data, bool bigEndian)
    {
        var u32 = BinaryUtils.ReadUInt32(data, 0, bigEndian);
        var f = BinaryUtils.ReadFloat(data, 0, bigEndian);

        // Heuristic: if it looks like a valid float, show as float
        if (!float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) < 1e10 && Math.Abs(f) > 1e-10)
        {
            return FieldValueDecoder.FormatFloat(f);
        }

        // Otherwise show as uint
        return u32.ToString();
    }

    private static string FormatBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 8)
        {
            return string.Join(" ", data.ToArray().Select(b => $"{b:X2}"));
        }

        return $"{data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2}...({data.Length} bytes)";
    }
}
