using Spectre.Console;
using System.CommandLine;
using System.Globalization;
using static EsmAnalyzer.Commands.OfstDataLoader;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Main entry point for all OFST commands. Registers subcommands from split files
///     and contains the core ofst (extract) and ofst-compare commands.
/// </summary>
public static class OfstCommands
{
    // ─── Delegating factory methods (preserve original public API) ──────────

    public static Command CreateOfstCommand()
    {
        var command = new Command("ofst", "Extract WRLD OFST offset table for a worldspace");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
        { Description = "Maximum number of offsets to display (0 = unlimited)", DefaultValueFactory = _ => 50 };
        var nonZeroOption = new Option<bool>("--nonzero") { Description = "Only show non-zero offsets" };
        var summaryOption = new Option<bool>("--summary") { Description = "Only print summary statistics" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);
        command.Options.Add(nonZeroOption);
        command.Options.Add(summaryOption);

        command.SetAction(parseResult => ExtractOfst(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(nonZeroOption),
            parseResult.GetValue(summaryOption)));

        return command;
    }

    public static Command CreateOfstCompareCommand()
    {
        var command = new Command("ofst-compare", "Compare WRLD OFST offset tables between Xbox 360 and PC");

        var xboxArg = new Argument<string>("xbox") { Description = "Path to the Xbox 360 ESM file" };
        var pcArg = new Argument<string>("pc") { Description = "Path to the PC ESM file" };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
        { Description = "Maximum mismatches to display (0 = unlimited)", DefaultValueFactory = _ => 50 };

        command.Arguments.Add(xboxArg);
        command.Arguments.Add(pcArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => CompareOfst(
            parseResult.GetValue(xboxArg)!,
            parseResult.GetValue(pcArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption)));

        return command;
    }

    // Delegated to OfstLocateCommand
    public static Command CreateOfstLocateCommand() => OfstLocateCommand.CreateOfstLocateCommand();
    public static Command CreateOfstCellCommand() => OfstLocateCommand.CreateOfstCellCommand();

    // Delegated to OfstPatternCommand
    public static Command CreateOfstPatternCommand() => OfstPatternCommand.CreateOfstPatternCommand();
    public static Command CreateOfstOrderCommand() => OfstPatternCommand.CreateOfstOrderCommand();
    public static Command CreateOfstDeltasCommand() => OfstPatternCommand.CreateOfstDeltasCommand();

    // Delegated to OfstBlocksCommand
    public static Command CreateOfstBlocksCommand() => OfstBlocksCommand.CreateOfstBlocksCommand();
    public static Command CreateOfstTileOrderCommand() => OfstBlocksCommand.CreateOfstTileOrderCommand();
    public static Command CreateOfstQuadtreeCommand() => OfstBlocksCommand.CreateOfstQuadtreeCommand();

    // Delegated to OfstRebuildCommand
    public static Command CreateOfstValidateCommand() => OfstRebuildCommand.CreateOfstValidateCommand();
    public static Command CreateOfstImageCommand() => OfstRebuildCommand.CreateOfstImageCommand();

    // ─── ExtractOfst command implementation ────────────────────────────────────

    private static int ExtractOfst(string filePath, string formIdText, int limit, bool nonZeroOnly, bool summaryOnly)
    {
        if (!TryLoadWorldRecord(filePath, formIdText, out var esm, out _, out var recordData, out var formId))
        {
            return 1;
        }

        var ofst = GetOfstData(recordData, esm.IsBigEndian);
        if (ofst == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST subrecord not found for WRLD 0x{formId:X8}");
            return 1;
        }

        var offsets = ParseOffsets(ofst, esm.IsBigEndian);

        AnsiConsole.MarkupLine(
            $"[cyan]WRLD:[/] 0x{formId:X8}  [cyan]OFST bytes:[/] {ofst.Length:N0}  [cyan]Offsets:[/] {offsets.Count:N0}");

        var nonZeroCount = offsets.Count(o => o != 0);
        var min = offsets.Count > 0 ? offsets.Min() : 0u;
        var max = offsets.Count > 0 ? offsets.Max() : 0u;

        AnsiConsole.MarkupLine(
            $"[cyan]Non-zero:[/] {nonZeroCount:N0}  [cyan]Min:[/] 0x{min:X8}  [cyan]Max:[/] 0x{max:X8}");

        if (summaryOnly)
        {
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(ColumnIndexLabel)
            .AddColumn(new TableColumn("Offset").RightAligned());

        var displayed = 0;
        for (var i = 0; i < offsets.Count && displayed < limit; i++)
        {
            var value = offsets[i];
            if (nonZeroOnly && value == 0)
            {
                continue;
            }

            _ = table.AddRow(i.ToString("N0", CultureInfo.InvariantCulture), $"0x{value:X8}");
            displayed++;
        }

        AnsiConsole.Write(table);

        return 0;
    }

    // ─── CompareOfst command implementation ────────────────────────────────────

    private static int CompareOfst(string xboxPath, string pcPath, string formIdText, int limit)
    {
        if (!TryParseFormId(formIdText, out var formId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdText}");
            return 1;
        }

        var (xbox, pc) = EsmFileLoader.LoadPair(xboxPath, pcPath, false);
        if (xbox == null || pc == null)
        {
            return 1;
        }

        var (xboxRecord, xboxRecordData) = FindWorldspaceRecord(xbox.Data, xbox.IsBigEndian, formId);
        var (pcRecord, pcRecordData) = FindWorldspaceRecord(pc.Data, pc.IsBigEndian, formId);

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

        var xboxOfst = GetOfstData(xboxRecordData, xbox.IsBigEndian);
        var pcOfst = GetOfstData(pcRecordData, pc.IsBigEndian);

        if (xboxOfst == null || pcOfst == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] OFST subrecord not found in one or both files");
            return 1;
        }

        var xboxOffsets = ParseOffsets(xboxOfst, xbox.IsBigEndian);
        var pcOffsets = ParseOffsets(pcOfst, pc.IsBigEndian);

        AnsiConsole.MarkupLine($"[cyan]WRLD:[/] 0x{formId:X8}");
        AnsiConsole.MarkupLine($"[cyan]Xbox OFST:[/] {xboxOfst.Length:N0} bytes, {xboxOffsets.Count:N0} entries");
        AnsiConsole.MarkupLine($"[cyan]PC   OFST:[/] {pcOfst.Length:N0} bytes, {pcOffsets.Count:N0} entries");

        var minCount = Math.Min(xboxOffsets.Count, pcOffsets.Count);
        var mismatchCount = 0;

        var diffTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(ColumnIndexLabel)
            .AddColumn(new TableColumn("Xbox").RightAligned())
            .AddColumn(new TableColumn("PC").RightAligned());

        for (var i = 0; i < minCount; i++)
        {
            if (xboxOffsets[i] == pcOffsets[i])
            {
                continue;
            }

            mismatchCount++;
            if (diffTable.Rows.Count < limit)
            {
                _ = diffTable.AddRow(
                    i.ToString("N0", CultureInfo.InvariantCulture),
                    $"0x{xboxOffsets[i]:X8}",
                    $"0x{pcOffsets[i]:X8}");
            }
        }

        AnsiConsole.MarkupLine($"[cyan]Mismatches:[/] {mismatchCount:N0} (compared {minCount:N0})");
        if (diffTable.Rows.Count > 0)
        {
            AnsiConsole.Write(diffTable);
        }

        return 0;
    }
}
