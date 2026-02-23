using Spectre.Console;
using System.CommandLine;
using System.Globalization;
using static EsmAnalyzer.Commands.OfstDataLoader;
using static EsmAnalyzer.Commands.OfstMathUtils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     OFST locate/resolve subcommands: ofst-locate and ofst-cell.
/// </summary>
public static class OfstLocateCommand
{
    public static Command CreateOfstLocateCommand()
    {
        var command = new Command("ofst-locate", "Locate records referenced by WRLD OFST offsets");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
        { Description = "Maximum number of results to display (0 = unlimited)", DefaultValueFactory = _ => 50 };
        var nonZeroOption = new Option<bool>("--nonzero") { Description = "Only show non-zero offsets" };
        var startOption = new Option<int>("--start")
        { Description = "Start index in the OFST table", DefaultValueFactory = _ => 0 };
        var baseOption = new Option<string>("--base")
        { Description = "Base offset mode: file|wrld|grup|world", DefaultValueFactory = _ => "wrld" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);
        command.Options.Add(nonZeroOption);
        command.Options.Add(startOption);
        command.Options.Add(baseOption);

        command.SetAction(parseResult => LocateOfstOffsets(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(nonZeroOption),
            parseResult.GetValue(startOption),
            parseResult.GetValue(baseOption) ?? "wrld"));

        return command;
    }

    public static Command CreateOfstCellCommand()
    {
        var command = new Command("ofst-cell", "Resolve a CELL FormID to its WRLD OFST entry");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var cellArg = new Argument<string>("cell") { Description = "CELL FormID (hex, e.g., 0x00000000)" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Arguments.Add(cellArg);

        command.SetAction(parseResult => ResolveOfstCell(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(cellArg)!));

        return command;
    }

    private static int LocateOfstOffsets(string filePath, string formIdText, int limit, bool nonZeroOnly,
        int startIndex, string baseMode)
    {
        if (startIndex < 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Start index must be >= 0");
            return 1;
        }

        if (!TryLoadWorldRecord(filePath, formIdText, out var esm, out var record, out var recordData, out var formId))
        {
            return 1;
        }

        var baseOffset = ResolveBaseOffset(esm.Data, esm.IsBigEndian, record.Offset, record.FormId, baseMode);
        if (baseOffset < 0)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid base mode: {baseMode}");
            return 1;
        }

        var ofst = GetOfstData(recordData, esm.IsBigEndian);
        if (ofst == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST subrecord not found for WRLD 0x{formId:X8}");
            return 1;
        }

        var offsets = ParseOffsets(ofst, esm.IsBigEndian);
        if (startIndex >= offsets.Count)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Start index {startIndex} is out of range (0-{offsets.Count - 1})");
            return 1;
        }

        var records = EsmRecordParser.ScanAllRecords(esm.Data, esm.IsBigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        AnsiConsole.MarkupLine($"[cyan]WRLD:[/] 0x{formId:X8}  [cyan]OFST entries:[/] {offsets.Count:N0}");
        AnsiConsole.MarkupLine($"[cyan]Base:[/] {baseMode} (0x{baseOffset:X8})");
        AnsiConsole.MarkupLine(
            $"[cyan]Locating:[/] start={startIndex:N0}, limit={limit:N0}, nonzero={(nonZeroOnly ? "yes" : "no")}");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(ColumnIndexLabel)
            .AddColumn(new TableColumn("Offset").RightAligned())
            .AddColumn("Record")
            .AddColumn(new TableColumn("FormID").RightAligned());

        var displayed = 0;
        for (var i = startIndex; i < offsets.Count && displayed < limit; i++)
        {
            var offset = offsets[i];
            if (nonZeroOnly && offset == 0)
            {
                continue;
            }

            var resolvedOffset = (uint)(offset + baseOffset);

            var match = FindRecordAtOffset(records, resolvedOffset);
            var recordLabel = match != null ? match.Signature : "(none)";
            var formIdLabel = match != null ? $"0x{match.FormId:X8}" : "-";

            _ = table.AddRow(
                i.ToString("N0", CultureInfo.InvariantCulture),
                $"0x{resolvedOffset:X8}",
                recordLabel,
                formIdLabel);

            displayed++;
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static int ResolveOfstCell(string filePath, string worldFormIdText, string cellFormIdText)
    {
        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return 1;
        }

        if (!TryParseFormId(cellFormIdText, out var cellFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid CELL FormID: {cellFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null)
        {
            return 1;
        }

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
        {
            return 1;
        }

        if (!TryResolveCellGrid(esm.Data, esm.IsBigEndian, cellFormId, out var gridX, out var gridY))
        {
            return 1;
        }

        if (!TryGetOfstIndex(context, gridX, gridY, out var index))
        {
            return 1;
        }

        var records = EsmRecordParser.ScanAllRecords(esm.Data, esm.IsBigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        var result = BuildResolveOfstCellResult(context, records, worldFormId, cellFormId, gridX, gridY, index);
        WriteResolveOfstCellTable(context, result);
        return 0;
    }

    private static ResolveOfstCellResult BuildResolveOfstCellResult(WorldContext context,
        List<AnalyzerRecordInfo> records, uint worldFormId, uint cellFormId, int gridX, int gridY, int index)
    {
        var entry = context.Offsets[index];
        var resolvedOffset = entry == 0 ? 0u : context.WrldRecord.Offset + entry;
        var match = entry == 0 ? null : FindRecordAtOffset(records, resolvedOffset);

        return new ResolveOfstCellResult(worldFormId, cellFormId, gridX, gridY, index, entry, resolvedOffset, match);
    }

    private static void WriteResolveOfstCellTable(WorldContext context, ResolveOfstCellResult result)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Field")
            .AddColumn("Value");

        _ = table.AddRow("WRLD", Markup.Escape($"0x{result.WorldFormId:X8}"));
        _ = table.AddRow("CELL", Markup.Escape($"0x{result.CellFormId:X8}"));
        _ = table.AddRow("Grid", Markup.Escape($"{result.GridX},{result.GridY}"));
        _ = table.AddRow("Bounds", Markup.Escape(
            $"X[{context.MinX},{context.MaxX}] Y[{context.MinY},{context.MaxY}] cols={context.Columns} rows={context.Rows}"));
        _ = table.AddRow("OFST Index", Markup.Escape(result.Index.ToString()));
        _ = table.AddRow("OFST Entry", Markup.Escape($"0x{result.Entry:X8}"));
        _ = table.AddRow("Resolved Offset", Markup.Escape(result.Entry == 0 ? "(zero)" : $"0x{result.ResolvedOffset:X8}"));
        _ = table.AddRow("Resolved Record",
            Markup.Escape(result.Match != null
                ? $"{result.Match.Signature} 0x{result.Match.FormId:X8}"
                : "(none)"));

        AnsiConsole.Write(table);
    }
}
