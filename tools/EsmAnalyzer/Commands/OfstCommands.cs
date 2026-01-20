using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for extracting and comparing WRLD OFST offset tables.
/// </summary>
public static class OfstCommands
{
    public static Command CreateOfstCommand()
    {
        var command = new Command("ofst", "Extract WRLD OFST offset table for a worldspace");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var formIdArg = new Argument<string>("formid") { Description = "WRLD FormID (hex, e.g., 0x000DA726)" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum number of offsets to display",
            DefaultValueFactory = _ => 20
        };
        var nonZeroOption = new Option<bool>("-n", "--nonzero") { Description = "Show only non-zero offsets" };
        var summaryOnlyOption = new Option<bool>("-s", "--summary") { Description = "Show summary only" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(formIdArg);
        command.Options.Add(limitOption);
        command.Options.Add(nonZeroOption);
        command.Options.Add(summaryOnlyOption);

        command.SetAction(parseResult => ExtractOfst(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(formIdArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(nonZeroOption),
            parseResult.GetValue(summaryOnlyOption)));

        return command;
    }

    public static Command CreateOfstCompareCommand()
    {
        var command = new Command("ofst-compare", "Compare WRLD OFST offset tables between Xbox 360 and PC");

        var xboxArg = new Argument<string>("xbox") { Description = "Path to the Xbox 360 ESM file" };
        var pcArg = new Argument<string>("pc") { Description = "Path to the PC ESM file" };
        var formIdArg = new Argument<string>("formid") { Description = "WRLD FormID (hex, e.g., 0x000DA726)" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum number of differences to display",
            DefaultValueFactory = _ => 50
        };

        command.Arguments.Add(xboxArg);
        command.Arguments.Add(pcArg);
        command.Arguments.Add(formIdArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => CompareOfst(
            parseResult.GetValue(xboxArg)!,
            parseResult.GetValue(pcArg)!,
            parseResult.GetValue(formIdArg)!,
            parseResult.GetValue(limitOption)));

        return command;
    }

    public static Command CreateOfstLocateCommand()
    {
        var command = new Command("ofst-locate", "Locate records referenced by WRLD OFST offsets");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var formIdArg = new Argument<string>("formid") { Description = "WRLD FormID (hex, e.g., 0x000DA726)" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum number of offsets to locate",
            DefaultValueFactory = _ => 50
        };
        var nonZeroOption = new Option<bool>("-n", "--nonzero") { Description = "Locate only non-zero offsets" };
        var startIndexOption = new Option<int>("-s", "--start-index")
        {
            Description = "Start index in the OFST table",
            DefaultValueFactory = _ => 0
        };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(formIdArg);
        command.Options.Add(limitOption);
        command.Options.Add(nonZeroOption);
        command.Options.Add(startIndexOption);

        command.SetAction(parseResult => LocateOfstOffsets(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(formIdArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(nonZeroOption),
            parseResult.GetValue(startIndexOption)));

        return command;
    }

    private static int ExtractOfst(string filePath, string formIdText, int limit, bool nonZeroOnly, bool summaryOnly)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return 1;
        }

        if (!TryParseFormId(formIdText, out var formId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdText}");
            return 1;
        }

        var data = File.ReadAllBytes(filePath);
        var header = EsmParser.ParseFileHeader(data);
        if (header == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM header");
            return 1;
        }

        var (record, recordData) = FindWorldspaceRecord(data, header.IsBigEndian, formId);
        if (record == null || recordData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] WRLD record not found for FormID 0x{formId:X8}");
            return 1;
        }

        var ofst = GetOfstData(recordData, header.IsBigEndian);
        if (ofst == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST subrecord not found for WRLD 0x{formId:X8}");
            return 1;
        }

        var offsets = ParseOffsets(ofst, header.IsBigEndian);

        AnsiConsole.MarkupLine(
            $"[cyan]WRLD:[/] 0x{formId:X8}  [cyan]OFST bytes:[/] {ofst.Length:N0}  [cyan]Offsets:[/] {offsets.Count:N0}");

        var nonZeroCount = offsets.Count(o => o != 0);
        var min = offsets.Count > 0 ? offsets.Min() : 0u;
        var max = offsets.Count > 0 ? offsets.Max() : 0u;

        AnsiConsole.MarkupLine(
            $"[cyan]Non-zero:[/] {nonZeroCount:N0}  [cyan]Min:[/] 0x{min:X8}  [cyan]Max:[/] 0x{max:X8}");

        if (summaryOnly) return 0;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Index")
            .AddColumn(new TableColumn("Offset").RightAligned());

        var displayed = 0;
        for (var i = 0; i < offsets.Count && displayed < limit; i++)
        {
            var value = offsets[i];
            if (nonZeroOnly && value == 0) continue;

            table.AddRow(i.ToString("N0", CultureInfo.InvariantCulture), $"0x{value:X8}");
            displayed++;
        }

        AnsiConsole.Write(table);

        return 0;
    }

    private static int CompareOfst(string xboxPath, string pcPath, string formIdText, int limit)
    {
        if (!File.Exists(xboxPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {xboxPath}");
            return 1;
        }

        if (!File.Exists(pcPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {pcPath}");
            return 1;
        }

        if (!TryParseFormId(formIdText, out var formId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdText}");
            return 1;
        }

        var xboxData = File.ReadAllBytes(xboxPath);
        var pcData = File.ReadAllBytes(pcPath);

        var xboxHeader = EsmParser.ParseFileHeader(xboxData);
        var pcHeader = EsmParser.ParseFileHeader(pcData);
        if (xboxHeader == null || pcHeader == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM headers");
            return 1;
        }

        var (xboxRecord, xboxRecordData) = FindWorldspaceRecord(xboxData, xboxHeader.IsBigEndian, formId);
        var (pcRecord, pcRecordData) = FindWorldspaceRecord(pcData, pcHeader.IsBigEndian, formId);

        if (xboxRecord == null || xboxRecordData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Xbox WRLD record not found for FormID 0x{formId:X8}");
            return 1;
        }

        if (pcRecord == null || pcRecordData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] PC WRLD record not found for FormID 0x{formId:X8}");
            return 1;
        }

        var xboxOfst = GetOfstData(xboxRecordData, xboxHeader.IsBigEndian);
        var pcOfst = GetOfstData(pcRecordData, pcHeader.IsBigEndian);

        if (xboxOfst == null || pcOfst == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] OFST subrecord not found in one or both files");
            return 1;
        }

        var xboxOffsets = ParseOffsets(xboxOfst, xboxHeader.IsBigEndian);
        var pcOffsets = ParseOffsets(pcOfst, pcHeader.IsBigEndian);

        AnsiConsole.MarkupLine($"[cyan]WRLD:[/] 0x{formId:X8}");
        AnsiConsole.MarkupLine($"[cyan]Xbox OFST:[/] {xboxOfst.Length:N0} bytes, {xboxOffsets.Count:N0} entries");
        AnsiConsole.MarkupLine($"[cyan]PC   OFST:[/] {pcOfst.Length:N0} bytes, {pcOffsets.Count:N0} entries");

        var minCount = Math.Min(xboxOffsets.Count, pcOffsets.Count);
        var mismatchCount = 0;

        var diffTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Index")
            .AddColumn(new TableColumn("Xbox").RightAligned())
            .AddColumn(new TableColumn("PC").RightAligned());

        for (var i = 0; i < minCount; i++)
        {
            if (xboxOffsets[i] == pcOffsets[i]) continue;

            mismatchCount++;
            if (diffTable.Rows.Count < limit)
                diffTable.AddRow(
                    i.ToString("N0", CultureInfo.InvariantCulture),
                    $"0x{xboxOffsets[i]:X8}",
                    $"0x{pcOffsets[i]:X8}");
        }

        AnsiConsole.MarkupLine($"[cyan]Mismatches:[/] {mismatchCount:N0} (compared {minCount:N0})");
        if (diffTable.Rows.Count > 0) AnsiConsole.Write(diffTable);

        return 0;
    }

    private static int LocateOfstOffsets(string filePath, string formIdText, int limit, bool nonZeroOnly,
        int startIndex)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return 1;
        }

        if (!TryParseFormId(formIdText, out var formId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdText}");
            return 1;
        }

        if (startIndex < 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Start index must be >= 0");
            return 1;
        }

        var data = File.ReadAllBytes(filePath);
        var header = EsmParser.ParseFileHeader(data);
        if (header == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Failed to parse ESM header");
            return 1;
        }

        var (record, recordData) = FindWorldspaceRecord(data, header.IsBigEndian, formId);
        if (record == null || recordData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] WRLD record not found for FormID 0x{formId:X8}");
            return 1;
        }

        var ofst = GetOfstData(recordData, header.IsBigEndian);
        if (ofst == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST subrecord not found for WRLD 0x{formId:X8}");
            return 1;
        }

        var offsets = ParseOffsets(ofst, header.IsBigEndian);
        if (startIndex >= offsets.Count)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Start index {startIndex} is out of range (0-{offsets.Count - 1})");
            return 1;
        }

        var records = EsmHelpers.ScanAllRecords(data, header.IsBigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        AnsiConsole.MarkupLine($"[cyan]WRLD:[/] 0x{formId:X8}  [cyan]OFST entries:[/] {offsets.Count:N0}");
        AnsiConsole.MarkupLine(
            $"[cyan]Locating:[/] start={startIndex:N0}, limit={limit:N0}, nonzero={(nonZeroOnly ? "yes" : "no")}");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Index")
            .AddColumn(new TableColumn("Offset").RightAligned())
            .AddColumn("Record")
            .AddColumn(new TableColumn("FormID").RightAligned());

        var displayed = 0;
        for (var i = startIndex; i < offsets.Count && displayed < limit; i++)
        {
            var offset = offsets[i];
            if (nonZeroOnly && offset == 0) continue;

            var match = FindRecordAtOffset(records, offset);
            var recordLabel = match != null ? match.Signature : "(none)";
            var formIdLabel = match != null ? $"0x{match.FormId:X8}" : "-";

            table.AddRow(
                i.ToString("N0", CultureInfo.InvariantCulture),
                $"0x{offset:X8}",
                recordLabel,
                formIdLabel);

            displayed++;
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static (AnalyzerRecordInfo? Record, byte[]? RecordData) FindWorldspaceRecord(byte[] data, bool bigEndian,
        uint formId)
    {
        var records = EsmHelpers.ScanForRecordType(data, bigEndian, "WRLD");
        var match = records.FirstOrDefault(r => r.FormId == formId);
        if (match == null) return (null, null);

        try
        {
            var recordData = EsmHelpers.GetRecordData(data, match, bigEndian);
            return (match, recordData);
        }
        catch
        {
            return (null, null);
        }
    }

    private static byte[]? GetOfstData(byte[] recordData, bool bigEndian)
    {
        var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);
        var ofst = subrecords.FirstOrDefault(s => s.Signature == "OFST");
        return ofst?.Data;
    }

    private static List<uint> ParseOffsets(byte[] ofstData, bool bigEndian)
    {
        var offsets = new List<uint>(ofstData.Length / 4);
        var count = ofstData.Length / 4;

        for (var i = 0; i < count; i++)
        {
            var value = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(ofstData.AsSpan(i * 4, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(ofstData.AsSpan(i * 4, 4));
            offsets.Add(value);
        }

        return offsets;
    }

    private static AnalyzerRecordInfo? FindRecordAtOffset(List<AnalyzerRecordInfo> records, uint offset)
    {
        // Simple linear search; caller can keep limit small.
        foreach (var record in records)
        {
            var start = record.Offset;
            var end = record.Offset + record.TotalSize;
            if (offset >= start && offset < end) return record;
        }

        return null;
    }

    private static bool TryParseFormId(string text, out uint formId)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];

        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }
}