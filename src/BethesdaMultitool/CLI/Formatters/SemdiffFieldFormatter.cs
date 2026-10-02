using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Utils;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Formatters;

/// <summary>
///     Field formatting and display logic for the semantic diff command. Renders a
///     <see cref="SemdiffTypes.SemdiffResult" /> to an injected <see cref="IAnsiConsole" />; it decides
///     nothing about which records differ (<see cref="SemdiffComparer" /> does).
/// </summary>
internal static class SemdiffFieldFormatter
{
    /// <summary>Warnings of one kind for one file beyond this many are summarized in one line.</summary>
    private const int MaxWarningsShownPerKind = 3;

    private const string MatchStatus = "[green]MATCH[/]";
    private const string DiffStatus = "[red]DIFF[/]";

    /// <summary>
    ///     Escapes brackets for Spectre.Console markup.
    /// </summary>
    internal static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }

    /// <summary>
    ///     Writes the warnings, the count line and up to <paramref name="limit" /> records.
    /// </summary>
    internal static void DisplayResult(IAnsiConsole console, SemdiffTypes.SemdiffResult result,
        string labelA, string labelB, int limit, bool showAll)
    {
        DisplayWarnings(console, result.Warnings, labelA, labelB);

        var summary = result.Summary;
        if (result.Records.Count == 0)
        {
            console.MarkupLine(showAll ? "[green]No records matched the filter.[/]" : "[green]No differences found.[/]");
            DisplayHiddenCounts(console, summary, showAll);
            return;
        }

        console.MarkupLine(showAll
            ? $"[yellow]Showing {summary.Listed} record(s) matching the filter; {summary.WithDifferences} with differences[/]"
            : $"[yellow]Found {summary.WithDifferences} record(s) with differences[/]");
        DisplayHiddenCounts(console, summary, showAll);
        console.WriteLine();

        var shown = 0;
        foreach (var diff in result.Records.Take(limit))
        {
            DisplayRecordDiff(console, diff, labelA, labelB, showAll);
            shown++;
            if (shown < limit && shown < result.Records.Count)
            {
                console.WriteLine();
            }
        }

        if (result.Records.Count > limit)
        {
            console.MarkupLine($"[grey]... and {result.Records.Count - limit} more records[/]");
        }
    }

    /// <summary>
    ///     One line per warning, except that a kind repeated for one file (thousands of split-INFO
    ///     duplicates on an Xbox 360 master) prints its first few and a count.
    /// </summary>
    internal static void DisplayWarnings(IAnsiConsole console, IReadOnlyList<SemdiffTypes.SemdiffWarning> warnings,
        string labelA, string labelB)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        foreach (var group in warnings.GroupBy(w => (w.Code, w.Side)))
        {
            var kind = group.ToList();
            foreach (var warning in kind.Take(MaxWarningsShownPerKind))
            {
                console.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(warning.Message)}");
            }

            if (kind.Count > MaxWarningsShownPerKind)
            {
                var side = group.Key.Side switch
                {
                    SemdiffTypes.SemdiffSide.A => $" for {labelA}",
                    SemdiffTypes.SemdiffSide.B => $" for {labelB}",
                    _ => ""
                };
                console.MarkupLine(
                    $"[yellow]Warning:[/] ... and {kind.Count - MaxWarningsShownPerKind} more " +
                    $"{Markup.Escape(group.Key.Code)} warning(s){Markup.Escape(side)}");
            }
        }

        console.WriteLine();
    }

    internal static void DisplayRecordDiff(IAnsiConsole console, SemdiffTypes.RecordDiff diff,
        string labelA = "File A", string labelB = "File B", bool showAll = false)
    {
        switch (diff.DiffType)
        {
            case SemdiffTypes.DiffType.SignatureMismatch:
                DisplaySignatureMismatch(console, diff, labelA, labelB);
                return;
            case SemdiffTypes.DiffType.Ambiguous:
                DisplayAmbiguous(console, diff, labelA, labelB);
                return;
        }

        var editorIdA = diff.EditorIdA ?? diff.RecordA?.EditorId;
        var editorIdB = diff.EditorIdB ?? diff.RecordB?.EditorId;
        var edidStr = editorIdA != null && editorIdB != null &&
                      !string.Equals(editorIdA, editorIdB, StringComparison.Ordinal)
            ? $"{editorIdA} -> {editorIdB}"
            : editorIdA ?? editorIdB ?? "(no EDID)";

        console.MarkupLine(
            $"[bold cyan]═══ {Markup.Escape(diff.RecordType)} {FormatFormIds(diff)} - {Markup.Escape(edidStr)} ═══[/]");
        DisplayMatchedBy(console, diff);

        switch (diff.DiffType)
        {
            case SemdiffTypes.DiffType.OnlyInA:
                console.MarkupLine(
                    $"[yellow]Record only exists in {Markup.Escape(labelA)}{FormatOccurrence(diff.OccurrenceA)}[/]");
                DisplayEditorIdHint(console, diff);
                return;
            case SemdiffTypes.DiffType.OnlyInB:
                console.MarkupLine(
                    $"[yellow]Record only exists in {Markup.Escape(labelB)}{FormatOccurrence(diff.OccurrenceB)}[/]");
                DisplayEditorIdHint(console, diff);
                return;
        }

        // The form version is not counted on its own, but whenever a record is listed its row is shown.
        var header = diff.Header;
        if (header != null && (header.HasCountedDelta || header.HasFormVersionDelta || showAll))
        {
            DisplayHeaderTable(console, diff, header, labelA, labelB, showAll);
        }

        if (diff.FieldDiffs == null || diff.FieldDiffs.Count == 0)
        {
            console.MarkupLine(diff.DiffType switch
            {
                SemdiffTypes.DiffType.Identical => "[green]Records are identical (header and subrecords)[/]",
                SemdiffTypes.DiffType.FormVersionOnly =>
                    "[green]Subrecords and record flags identical; only the form version differs: " +
                    $"{Markup.Escape(DescribeFormVersion(header))}[/]",
                SemdiffTypes.DiffType.NonSemanticHeaderOnly =>
                    "[green]Subrecords and record flags identical; only version-control bookkeeping differs: " +
                    $"{Markup.Escape(DescribeBookkeeping(header))}[/]",
                _ when diff.SubrecordOrderNote != null => $"[yellow]{Markup.Escape(diff.SubrecordOrderNote)}[/]",
                _ when header is { HasCountedDelta: true } =>
                    "[yellow]Subrecords identical; record header differs (see above)[/]",
                _ => "[green]Subrecords identical[/]"
            });
            return;
        }

        // Group diffs by subrecord
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn(new TableColumn("[bold]Subrecord[/]").Width(10));
        table.AddColumn(new TableColumn("[bold]Field[/]").Width(20));
        table.AddColumn(new TableColumn($"[bold]{Markup.Escape(labelA)}[/]").Width(30));
        table.AddColumn(new TableColumn($"[bold]{Markup.Escape(labelB)}[/]").Width(30));
        table.AddColumn(new TableColumn("[bold]Status[/]").Width(12));

        foreach (var fieldDiff in diff.FieldDiffs)
        {
            DisplayFieldDiff(table, fieldDiff);
        }

        console.Write(table);
    }

    /// <summary>
    ///     The display rows of one subrecord difference: a single row for a subrecord present on one
    ///     side, one row per schema field when a schema decodes the subrecord, else one raw-value row.
    ///     The one place subrecord bytes become display strings: the table renders these rows and
    ///     <see cref="SemdiffJsonWriter" /> writes the same rows, so the two formats cannot drift. A display
    ///     string is rounded or truncated, so it never decides whether a subrecord differs (the comparer
    ///     does, on exact bytes); when every decoded row reads equal, one more row says where the bytes do
    ///     differ.
    /// </summary>
    internal static List<SemdiffTypes.FieldRow> BuildFieldRows(SemdiffTypes.FieldDiff diff)
    {
        var rows = new List<SemdiffTypes.FieldRow>();

        if (diff.Message != null)
        {
            // Only in A or only in B
            var valueStr = diff.DataA != null
                ? FormatSubrecordValue(diff.Signature, diff.DataA, diff.BigEndianA, diff.RecordType)
                : FormatSubrecordValue(diff.Signature, diff.DataB!, diff.BigEndianB, diff.RecordType);
            rows.Add(new SemdiffTypes.FieldRow(diff.Signature, "-",
                diff.DataA != null ? valueStr : null,
                diff.DataB != null ? valueStr : null,
                false, diff.Message));
            return rows;
        }

        if (diff is { ByteOrderUnresolved: true, DataA: { } unresolvedA, DataB: { } unresolvedB })
        {
            // No schema says how to swap it, so neither a value nor a verdict can be given for it.
            rows.Add(new SemdiffTypes.FieldRow(diff.Signature, $"({unresolvedA.Length}/{unresolvedB.Length} bytes)",
                FormatSubrecordValue(diff.Signature, unresolvedA, diff.BigEndianA, diff.RecordType),
                FormatSubrecordValue(diff.Signature, unresolvedB, diff.BigEndianB, diff.RecordType),
                false, "byte order unresolved (no schema)"));
            return rows;
        }

        var schema = SubrecordSchemaRegistry.GetSchema(diff.Signature, diff.RecordType,
            diff.DataA?.Length ?? diff.DataB?.Length ?? 0);

        // Both have data - decode fields
        if (schema != null && schema.Fields.Length > 0)
        {
            // Schema-based field-by-field comparison
            var fieldsA = DecodeSchemaFields(diff.DataA!, schema, diff.BigEndianA);
            var fieldsB = DecodeSchemaFields(diff.DataB!, schema, diff.BigEndianB);

            var allFields = fieldsA.Keys.Union(fieldsB.Keys).OrderBy(k => k).ToList();
            foreach (var fieldName in allFields)
            {
                var hasA = fieldsA.TryGetValue(fieldName, out var valA);
                var hasB = fieldsB.TryGetValue(fieldName, out var valB);
                rows.Add(new SemdiffTypes.FieldRow(diff.Signature, fieldName,
                    hasA ? valA : null,
                    hasB ? valB : null,
                    hasA && hasB && valA == valB, null));
            }
        }
        else
        {
            // No schema - show raw values
            var rawA = FormatSubrecordValue(diff.Signature, diff.DataA!, diff.BigEndianA, diff.RecordType);
            var rawB = FormatSubrecordValue(diff.Signature, diff.DataB!, diff.BigEndianB, diff.RecordType);
            rows.Add(new SemdiffTypes.FieldRow(diff.Signature, $"({diff.DataA!.Length} bytes)", rawA, rawB,
                rawA == rawB, null));
        }

        if (rows.TrueForAll(r => r.Equal) && diff.FirstDifferingOffset is { } offset)
        {
            rows.Add(new SemdiffTypes.FieldRow(diff.Signature, "(bytes)", null, null, false,
                $"bytes differ from offset {offset} (below display precision or outside the decoded fields)"));
        }

        return rows;
    }

    private static void DisplayFieldDiff(Table table, SemdiffTypes.FieldDiff diff)
    {
        var isFirst = true;
        foreach (var row in BuildFieldRows(diff))
        {
            table.AddRow(
                isFirst ? $"[yellow]{EscapeMarkup(row.Signature)}[/]" : "",
                EscapeMarkup(row.Field),
                row.ValueA != null ? EscapeMarkup(row.ValueA) : "[grey]-[/]",
                row.ValueB != null ? EscapeMarkup(row.ValueB) : "[grey]-[/]",
                row.Message != null
                    ? $"[yellow]{EscapeMarkup(row.Message)}[/]"
                    : row.Equal
                        ? MatchStatus
                        : DiffStatus
            );
            isFirst = false;
        }
    }

    /// <summary>
    ///     The record-header table: the flags (value and names), one row per changed bit, the form
    ///     version and, under <c>--all</c> only, the version-control words, which are shown but never
    ///     counted as a difference.
    /// </summary>
    private static void DisplayHeaderTable(IAnsiConsole console, SemdiffTypes.RecordDiff diff,
        SemdiffTypes.HeaderComparison header, string labelA, string labelB, bool showAll)
    {
        // The table holds only short values, so no cell breaks inside a hex number when output is redirected
        // (80 columns). Flag names and the per-bit deltas follow as whole lines, one fact per line.
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn(new TableColumn("[bold]Record header[/]"));
        table.AddColumn(new TableColumn($"[bold]{Markup.Escape(labelA)}[/]").NoWrap());
        table.AddColumn(new TableColumn($"[bold]{Markup.Escape(labelB)}[/]").NoWrap());
        table.AddColumn(new TableColumn("[bold]Status[/]"));

        table.AddRow(
            "Flags",
            $"0x{header.FlagsA:X8}",
            $"0x{header.FlagsB:X8}",
            header.FlagsA != header.FlagsB ? DiffStatus : MatchStatus);

        if (diff.RecordA is { } a && diff.RecordB is { } b)
        {
            table.AddRow("Form Version", a.FormVersion.ToString(CultureInfo.InvariantCulture),
                b.FormVersion.ToString(CultureInfo.InvariantCulture),
                a.FormVersion != b.FormVersion ? DiffStatus : MatchStatus);

            if (showAll)
            {
                table.AddRow("Version Control Info 1",
                    SemdiffRecordParser.FormatVersionControl1(a.VersionControl1),
                    SemdiffRecordParser.FormatVersionControl1(b.VersionControl1),
                    a.VersionControl1 != b.VersionControl1 ? "[grey]IGNORED (version control)[/]" : MatchStatus);
                table.AddRow("Version Control Info 2", a.VersionControl2.ToString(CultureInfo.InvariantCulture),
                    b.VersionControl2.ToString(CultureInfo.InvariantCulture),
                    a.VersionControl2 != b.VersionControl2 ? "[grey]IGNORED (version control)[/]" : MatchStatus);
            }
        }

        console.Write(table);

        console.MarkupLine($"  {Markup.Escape(labelA)} flags: {Markup.Escape(FormatFlags(header.FlagsA, diff.FlagNamesA))}");
        console.MarkupLine($"  {Markup.Escape(labelB)} flags: {Markup.Escape(FormatFlags(header.FlagsB, diff.FlagNamesB))}");
        foreach (var delta in header.Added)
        {
            console.MarkupLine(
                $"  {Markup.Escape(DescribeBitDelta('+', delta))}: clear -> set  {BitDeltaStatus("ADDED", delta)}");
        }

        foreach (var delta in header.Removed)
        {
            console.MarkupLine(
                $"  {Markup.Escape(DescribeBitDelta('-', delta))}: set -> clear  {BitDeltaStatus("REMOVED", delta)}");
        }
    }

    /// <summary>
    ///     A FormID reused by another record type: both identities, each side's inventory of
    ///     subrecords and header flags, and no decoded field values at all.
    /// </summary>
    private static void DisplaySignatureMismatch(IAnsiConsole console, SemdiffTypes.RecordDiff diff,
        string labelA, string labelB)
    {
        var typeA = diff.RecordA?.Type ?? diff.RecordType;
        var typeB = diff.RecordB?.Type ?? "?";
        var editorIdA = diff.EditorIdA ?? diff.RecordA?.EditorId ?? "(no EDID)";
        var editorIdB = diff.EditorIdB ?? diff.RecordB?.EditorId ?? "(no EDID)";

        console.MarkupLine(
            $"[bold cyan]═══ {FormatFormIds(diff)}  {Markup.Escape(labelA)}: {Markup.Escape(typeA)} " +
            $"{Markup.Escape(editorIdA)}  !=  {Markup.Escape(labelB)}: {Markup.Escape(typeB)} " +
            $"{Markup.Escape(editorIdB)} ═══[/]");
        console.MarkupLine(diff.MatchedBy == SemdiffTypes.MatchedBy.ExplicitMap
            ? "[red]The mapped records have different record types; typed field comparison refused.[/]"
            : "[red]FormID reused by a different record type; typed field comparison refused.[/]");

        DisplaySideSummary(console, labelA, typeA, diff.RecordA, diff.FlagNamesA, diff.InventoryA);
        DisplaySideSummary(console, labelB, typeB, diff.RecordB, diff.FlagNamesB, diff.InventoryB);
        DisplayEditorIdHint(console, diff);

        if (diff.MatchedBy != SemdiffTypes.MatchedBy.ExplicitMap)
        {
            console.MarkupLine("[grey]Use --match editorid or --map A=B to pair records explicitly.[/]");
        }
    }

    private static void DisplaySideSummary(IAnsiConsole console, string label, string type,
        SemdiffTypes.ParsedRecord? record, IReadOnlyList<string> flagNames,
        IReadOnlyList<SemdiffTypes.SubrecordInventoryEntry>? inventory)
    {
        if (record == null)
        {
            return;
        }

        var entries = inventory ?? SemdiffRecordParser.BuildSubrecordInventory(record);
        var subrecords = entries.Count == 0
            ? "(none)"
            : string.Join(", ", entries.Select(FormatInventoryEntry));
        console.MarkupLine(
            $"  {Markup.Escape(label)} {Markup.Escape(type)}: flags {Markup.Escape(FormatFlags(record.Flags, flagNames))}; " +
            $"subrecords {Markup.Escape(subrecords)}");
    }

    private static void DisplayAmbiguous(IAnsiConsole console, SemdiffTypes.RecordDiff diff,
        string labelA, string labelB)
    {
        var editorId = diff.EditorIdA ?? diff.EditorIdB ?? "(no EDID)";
        console.MarkupLine(
            $"[bold cyan]═══ {Markup.Escape(diff.RecordType)} {Markup.Escape(editorId)} - ambiguous EditorID ═══[/]");
        console.MarkupLine(
            $"[red]EditorID {Markup.Escape(editorId)} names more than one {Markup.Escape(diff.RecordType)} record; " +
            "not paired.[/]");
        console.MarkupLine($"  {Markup.Escape(labelA)}: {FormatFormIdList(diff.AmbiguousFormIdsA)}");
        console.MarkupLine($"  {Markup.Escape(labelB)}: {FormatFormIdList(diff.AmbiguousFormIdsB)}");
    }

    private static void DisplayMatchedBy(IAnsiConsole console, SemdiffTypes.RecordDiff diff)
    {
        var text = diff.MatchedBy switch
        {
            SemdiffTypes.MatchedBy.EditorId => "Matched by EditorID",
            SemdiffTypes.MatchedBy.FormIdFallback => "Matched by FormID (no EditorID)",
            SemdiffTypes.MatchedBy.ExplicitMap => "Matched by --map",
            _ => null
        };

        if (text != null)
        {
            console.MarkupLine($"[grey]{text}[/]");
        }
    }

    private static void DisplayEditorIdHint(IAnsiConsole console, SemdiffTypes.RecordDiff diff)
    {
        if (diff.EditorIdHint != null)
        {
            console.MarkupLine($"[grey]EditorID lookup:[/] {Markup.Escape(diff.EditorIdHint)}");
        }
    }

    /// <summary>The verdicts only <c>--all</c> lists that are still differences of a kind: one line each.</summary>
    private static void DisplayHiddenCounts(IAnsiConsole console, SemdiffTypes.SemdiffSummary summary,
        bool showAll)
    {
        if (showAll)
        {
            return;
        }

        if (summary.FormVersionOnly > 0)
        {
            console.MarkupLine(
                $"[grey]{summary.FormVersionOnly} record(s) differ only in form version (--all lists them)[/]");
        }

        if (summary.NonSemanticHeaderOnly > 0)
        {
            console.MarkupLine(
                $"[grey]{summary.NonSemanticHeaderOnly} more record(s) differ only in version-control " +
                "bookkeeping (not counted; --all lists them)[/]");
        }
    }

    private static string FormatFormIds(SemdiffTypes.RecordDiff diff)
    {
        return diff.FormIdB == diff.FormId
            ? $"0x{diff.FormId:X8}"
            : $"0x{diff.FormId:X8} -> 0x{diff.FormIdB:X8}";
    }

    private static string FormatOccurrence(int occurrence)
    {
        return occurrence > 0 ? $" (occurrence {occurrence + 1} of its FormID)" : "";
    }

    private static string FormatFormIdList(IReadOnlyList<uint> formIds)
    {
        return formIds.Count == 0 ? "(none)" : string.Join(", ", formIds.Select(id => $"0x{id:X8}"));
    }

    internal static string FormatFlags(uint flags, IReadOnlyList<string> names)
    {
        return names.Count == 0 ? $"0x{flags:X8}" : $"0x{flags:X8} ({string.Join(", ", names)})";
    }

    /// <summary>For example <c>+ bit 11 (0x00000800) Initially Disabled</c>, or <c>- bit 13 (0x00002000)</c> when unnamed.</summary>
    internal static string DescribeBitDelta(char sign, SemdiffTypes.FlagBitDelta delta)
    {
        var text = $"{sign} bit {delta.Bit} (0x{delta.Mask:X8})";
        return delta.Name != null ? $"{text} {delta.Name}" : text;
    }

    private static string BitDeltaStatus(string change, SemdiffTypes.FlagBitDelta delta)
    {
        return delta.Class == SemdiffTypes.HeaderDeltaClass.Storage
            ? $"[yellow]{change} (storage)[/]"
            : $"[red]{change}[/]";
    }

    private static string DescribeBookkeeping(SemdiffTypes.HeaderComparison? header)
    {
        if (header == null)
        {
            return "";
        }

        return string.Join(", ", header.Fields
            .Where(f => f.Class == SemdiffTypes.HeaderDeltaClass.Bookkeeping)
            .Select(f => $"{ShortHeaderFieldName(f.Field)} {f.ValueA} -> {f.ValueB}"));
    }

    private static string DescribeFormVersion(SemdiffTypes.HeaderComparison? header)
    {
        return header?.Fields.FirstOrDefault(f => f.Class == SemdiffTypes.HeaderDeltaClass.Format) is { } field
            ? $"{field.ValueA} -> {field.ValueB}"
            : "";
    }

    private static string ShortHeaderFieldName(string field)
    {
        return field switch
        {
            "Version Control Info 1" => "VCI1",
            "Version Control Info 2" => "VCI2",
            _ => field
        };
    }

    private static string FormatInventoryEntry(SemdiffTypes.SubrecordInventoryEntry entry)
    {
        var sizes = string.Join("/", entry.Sizes.Distinct());
        return entry.Count == 1 ? $"{entry.Signature} ({sizes} B)" : $"{entry.Signature} x{entry.Count} ({sizes} B)";
    }

    internal static Dictionary<string, string> DecodeSchemaFields(byte[] data, SubrecordSchema schema, bool bigEndian)
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
            SubrecordFieldType.UInt32 or SubrecordFieldType.Int32 or SubrecordFieldType.Int32LittleEndian
                or SubrecordFieldType.Float
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
