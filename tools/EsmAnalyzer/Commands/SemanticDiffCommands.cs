using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using EsmAnalyzer.Conversion.Schema;
using EsmAnalyzer.Core;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Semantic diff command - shows human-readable field-by-field differences.
/// </summary>
public static class SemanticDiffCommands
{
    /// <summary>
    ///     Escapes brackets for Spectre.Console markup.
    /// </summary>
    private static string EscapeMarkup(string text) =>
        text.Replace("[", "[[").Replace("]", "]]");

    /// <summary>
    ///     Creates the 'semdiff' command for semantic comparison.
    /// </summary>
    public static Command CreateSemanticDiffCommand()
    {
        var command = new Command("semdiff", "Semantic diff - shows human-readable field differences (like TES5Edit)");

        var fileAArg = new Argument<string>("fileA") { Description = "First ESM file" };
        var fileBArg = new Argument<string>("fileB") { Description = "Second ESM file" };
        var formIdOption = new Option<string?>("-f", "--formid")
        { Description = "Specific FormID to compare (hex, e.g., 0x0017B37C)" };
        var typeOption = new Option<string?>("-t", "--type")
        { Description = "Record type to filter (e.g., PROJ, WEAP, NPC_)" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Max records to show (default: 10)", DefaultValueFactory = _ => 10 };
        var showAllOption = new Option<bool>("--all")
        { Description = "Show all fields, not just differences" };
        var formatOption = new Option<string>("--format")
        { Description = "Output format: table (default), tree, json", DefaultValueFactory = _ => "table" };

        command.Arguments.Add(fileAArg);
        command.Arguments.Add(fileBArg);
        command.Options.Add(formIdOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(showAllOption);
        command.Options.Add(formatOption);

        command.SetAction(parseResult =>
        {
            var fileA = parseResult.GetValue(fileAArg)!;
            var fileB = parseResult.GetValue(fileBArg)!;
            var formIdStr = parseResult.GetValue(formIdOption);
            var recordType = parseResult.GetValue(typeOption);
            var limit = parseResult.GetValue(limitOption);
            var showAll = parseResult.GetValue(showAllOption);
            var format = parseResult.GetValue(formatOption);

            return RunSemanticDiff(fileA, fileB, formIdStr, recordType, limit, showAll, format);
        });

        return command;
    }

    /// <summary>
    ///     Public entry point for semantic diff with custom labels (called by unified diff command).
    /// </summary>
    public static int RunSemanticDiffLabeled(string fileAPath, string fileBPath, string labelA, string labelB,
        string? formIdStr, string? recordType, int limit, bool showAll, string format = "table", bool skipHeader = false)
    {
        return RunSemanticDiffCore(fileAPath, fileBPath, labelA, labelB, formIdStr, recordType, limit, showAll, format, skipHeader);
    }

    private static int RunSemanticDiff(string fileAPath, string fileBPath, string? formIdStr,
        string? recordType, int limit, bool showAll, string format)
    {
        return RunSemanticDiffCore(fileAPath, fileBPath, "File A", "File B", formIdStr, recordType, limit, showAll, format, skipHeader: false);
    }

    private static int RunSemanticDiffCore(string fileAPath, string fileBPath, string labelA, string labelB,
        string? formIdStr, string? recordType, int limit, bool showAll, string format, bool skipHeader)
    {
        if (!File.Exists(fileAPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {fileAPath}");
            return 1;
        }

        if (!File.Exists(fileBPath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {fileBPath}");
            return 1;
        }

        var dataA = File.ReadAllBytes(fileAPath);
        var dataB = File.ReadAllBytes(fileBPath);
        var bigEndianA = EsmParser.IsBigEndian(dataA);
        var bigEndianB = EsmParser.IsBigEndian(dataB);

        if (!skipHeader)
        {
            AnsiConsole.MarkupLine($"[bold]Semantic ESM Diff[/]");
            AnsiConsole.MarkupLine($"{labelA}: [cyan]{Path.GetFileName(fileAPath)}[/] ({(bigEndianA ? "Big-endian" : "Little-endian")})");
            AnsiConsole.MarkupLine($"{labelB}: [cyan]{Path.GetFileName(fileBPath)}[/] ({(bigEndianB ? "Big-endian" : "Little-endian")})");
            AnsiConsole.WriteLine();
        }

        // Parse specific FormID
        uint? targetFormId = null;
        if (!string.IsNullOrEmpty(formIdStr))
        {
            if (formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                targetFormId = Convert.ToUInt32(formIdStr, 16);
            }
            else if (uint.TryParse(formIdStr, out var parsed))
            {
                targetFormId = parsed;
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Invalid FormID format: {formIdStr}");
                return 1;
            }
        }

        // Parse records from both files
        var recordsA = ParseRecordsWithSubrecords(dataA, bigEndianA, recordType, targetFormId);
        var recordsB = ParseRecordsWithSubrecords(dataB, bigEndianB, recordType, targetFormId);

        // Build lookup by FormID
        var lookupA = recordsA.ToDictionary(r => r.FormId);
        var lookupB = recordsB.ToDictionary(r => r.FormId);

        // Find differences
        var differences = new List<RecordDiff>();
        var allFormIds = lookupA.Keys.Union(lookupB.Keys).OrderBy(x => x).ToList();

        foreach (var formId in allFormIds)
        {
            var hasA = lookupA.TryGetValue(formId, out var recA);
            var hasB = lookupB.TryGetValue(formId, out var recB);

            if (!hasA && hasB)
            {
                differences.Add(new RecordDiff(formId, recB!.Type, DiffType.OnlyInB, null, recB));
            }
            else if (hasA && !hasB)
            {
                differences.Add(new RecordDiff(formId, recA!.Type, DiffType.OnlyInA, recA, null));
            }
            else if (hasA && hasB)
            {
                var fieldDiffs = CompareRecordFields(recA!, recB!, bigEndianA, bigEndianB);
                if (fieldDiffs.Count > 0 || showAll)
                {
                    differences.Add(new RecordDiff(formId, recA!.Type, DiffType.Different, recA, recB, fieldDiffs));
                }
            }
        }

        // Display results
        if (differences.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No differences found.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[yellow]Found {differences.Count} record(s) with differences[/]");
        AnsiConsole.WriteLine();

        var shown = 0;
        foreach (var diff in differences.Take(limit))
        {
            DisplayRecordDiff(diff, showAll, format, bigEndianA, bigEndianB, labelA, labelB);
            shown++;
            if (shown < limit && shown < differences.Count)
            {
                AnsiConsole.WriteLine();
            }
        }

        if (differences.Count > limit)
        {
            AnsiConsole.MarkupLine($"[grey]... and {differences.Count - limit} more records[/]");
        }

        return 0;
    }

    private static List<ParsedRecord> ParseRecordsWithSubrecords(byte[] data, bool bigEndian,
        string? typeFilter, uint? formIdFilter)
    {
        var records = new List<ParsedRecord>();
        var offset = 0;

        while (offset + 24 <= data.Length)
        {
            var sig = bigEndian
                ? new string([(char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]])
                : Encoding.ASCII.GetString(data, offset, 4);

            if (sig == "GRUP")
            {
                offset += 24;
                continue;
            }

            // Parse record header
            var dataSize = EsmBinary.ReadUInt32(data, offset + 4, bigEndian);
            var flags = EsmBinary.ReadUInt32(data, offset + 8, bigEndian);
            var formId = EsmBinary.ReadUInt32(data, offset + 12, bigEndian);

            var headerSize = 24; // FNV uses 24-byte headers
            var recordStart = offset;
            var recordEnd = offset + headerSize + (int)dataSize;

            // Filter checks
            var matchesType = string.IsNullOrEmpty(typeFilter) ||
                              sig.Equals(typeFilter, StringComparison.OrdinalIgnoreCase);
            var matchesFormId = formIdFilter == null || formId == formIdFilter;

            if (matchesType && matchesFormId)
            {
                // Parse subrecords
                var compressed = (flags & 0x00040000) != 0;
                byte[] recordData;
                int subOffset;

                if (compressed)
                {
                    // Decompress
                    var decompSize = EsmBinary.ReadUInt32(data, offset + headerSize, bigEndian);
                    var compData = data.AsSpan(offset + headerSize + 4, (int)dataSize - 4);
                    recordData = EsmHelpers.DecompressZlib(compData.ToArray(), (int)decompSize);
                    subOffset = 0;
                }
                else
                {
                    recordData = data;
                    subOffset = offset + headerSize;
                }

                var subrecords = ParseSubrecords(recordData, subOffset, compressed ? recordData.Length : (int)dataSize, bigEndian);
                records.Add(new ParsedRecord(sig, formId, flags, recordStart, subrecords));
            }

            offset = recordEnd;
        }

        return records;
    }

    private static List<ParsedSubrecord> ParseSubrecords(byte[] data, int startOffset, int length, bool bigEndian)
    {
        var subrecords = new List<ParsedSubrecord>();
        var offset = startOffset;
        var endOffset = startOffset + length;

        while (offset + 6 <= endOffset)
        {
            var sig = bigEndian
                ? new string([(char)data[offset + 3], (char)data[offset + 2], (char)data[offset + 1], (char)data[offset]])
                : Encoding.ASCII.GetString(data, offset, 4);
            var size = EsmBinary.ReadUInt16(data, offset + 4, bigEndian);

            if (offset + 6 + size > endOffset)
            {
                break;
            }

            var subData = new byte[size];
            Array.Copy(data, offset + 6, subData, 0, size);
            subrecords.Add(new ParsedSubrecord(sig, subData, offset));

            offset += 6 + size;
        }

        return subrecords;
    }

    private static List<FieldDiff> CompareRecordFields(ParsedRecord recA, ParsedRecord recB,
        bool bigEndianA, bool bigEndianB)
    {
        var diffs = new List<FieldDiff>();

        // Build subrecord lookup for both records
        var subsA = recA.Subrecords.GroupBy(s => s.Signature)
            .ToDictionary(g => g.Key, g => g.ToList());
        var subsB = recB.Subrecords.GroupBy(s => s.Signature)
            .ToDictionary(g => g.Key, g => g.ToList());

        var allSigs = subsA.Keys.Union(subsB.Keys).OrderBy(x => x).ToList();

        foreach (var sig in allSigs)
        {
            var hasA = subsA.TryGetValue(sig, out var listA);
            var hasB = subsB.TryGetValue(sig, out var listB);

            if (!hasA && hasB)
            {
                foreach (var sub in listB!)
                {
                    diffs.Add(new FieldDiff(sig, null, sub.Data, "Only in B", bigEndianA, bigEndianB, recA.Type));
                }
            }
            else if (hasA && !hasB)
            {
                foreach (var sub in listA!)
                {
                    diffs.Add(new FieldDiff(sig, sub.Data, null, "Only in A", bigEndianA, bigEndianB, recA.Type));
                }
            }
            else
            {
                // Both have this subrecord - compare each instance
                var maxCount = Math.Max(listA!.Count, listB!.Count);
                for (var i = 0; i < maxCount; i++)
                {
                    var subA = i < listA.Count ? listA[i] : null;
                    var subB = i < listB.Count ? listB[i] : null;

                    if (subA == null)
                    {
                        diffs.Add(new FieldDiff(sig, null, subB!.Data, $"Only in B (index {i})", bigEndianA, bigEndianB, recA.Type));
                    }
                    else if (subB == null)
                    {
                        diffs.Add(new FieldDiff(sig, subA.Data, null, $"Only in A (index {i})", bigEndianA, bigEndianB, recA.Type));
                    }
                    else if (!subA.Data.SequenceEqual(subB.Data))
                    {
                        diffs.Add(new FieldDiff(sig, subA.Data, subB.Data, null, bigEndianA, bigEndianB, recA.Type));
                    }
                }
            }
        }

        return diffs;
    }

    private static void DisplayRecordDiff(RecordDiff diff, bool showAll, string format, bool bigEndianA, bool bigEndianB,
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
            case DiffType.OnlyInA:
                AnsiConsole.MarkupLine($"[yellow]Record only exists in {labelA}[/]");
                return;
            case DiffType.OnlyInB:
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

    private static void DisplayFieldDiff(Table table, FieldDiff diff)
    {
        var schema = SubrecordSchemaRegistry.GetSchema(diff.Signature, diff.RecordType, diff.DataA?.Length ?? diff.DataB?.Length ?? 0);

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

            var value = DecodeFieldValue(data.AsSpan(offset, fieldSize), field.Type, bigEndian);
            fields[field.Name] = value;
            offset += fieldSize;
        }

        return fields;
    }

    private static int GetFieldSize(SubrecordFieldType type, int? explicitSize)
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

    private static string DecodeFieldValue(ReadOnlySpan<byte> data, SubrecordFieldType type, bool bigEndian) =>
        FieldValueDecoder.Decode(data, type, bigEndian);

    private static string FormatFloat(float f) => FieldValueDecoder.FormatFloat(f);

    private static string FormatDouble(double d) => FieldValueDecoder.FormatDouble(d);

    private static string FormatBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length <= 8)
        {
            return string.Join(" ", data.ToArray().Select(b => $"{b:X2}"));
        }

        return $"{data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2}...({data.Length} bytes)";
    }

    private static string FormatSubrecordValue(string sig, byte[] data, bool bigEndian, string recordType)
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
                return DecodeFieldValue(data.AsSpan(0, size), firstField.Type, bigEndian);
            }
        }

        // Common simple types
        return data.Length switch
        {
            1 => data[0].ToString(),
            2 => EsmBinary.ReadUInt16(data, bigEndian).ToString(),
            4 when sig.EndsWith("ID", StringComparison.Ordinal) || sig == "NAME" || sig == "SCRI" || sig == "TPLT" =>
                $"0x{EsmBinary.ReadUInt32(data, bigEndian):X8}",
            4 => FormatAs4Bytes(data, bigEndian),
            _ => FormatBytes(data)
        };
    }

    private static string FormatAs4Bytes(byte[] data, bool bigEndian)
    {
        var u32 = EsmBinary.ReadUInt32(data, 0, bigEndian);
        var f = EsmBinary.ReadSingle(data, 0, bigEndian);

        // Heuristic: if it looks like a valid float, show as float
        if (!float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) < 1e10 && Math.Abs(f) > 1e-10)
        {
            return FormatFloat(f);
        }

        // Otherwise show as uint
        return u32.ToString();
    }

    // Data types
    private sealed record ParsedRecord(string Type, uint FormId, uint Flags, int Offset, List<ParsedSubrecord> Subrecords);
    private sealed record ParsedSubrecord(string Signature, byte[] Data, int Offset);
    private sealed record RecordDiff(uint FormId, string RecordType, DiffType DiffType,
        ParsedRecord? RecordA, ParsedRecord? RecordB, List<FieldDiff>? FieldDiffs = null);
    private sealed record FieldDiff(string Signature, byte[]? DataA, byte[]? DataB, string? Message,
        bool BigEndianA, bool BigEndianB, string RecordType);
    private enum DiffType { OnlyInA, OnlyInB, Different }
}
