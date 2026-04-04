using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using Spectre.Console;
using static FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers.RecordTraversalHelpers;

namespace FalloutXbox360Utils.CLI.Commands.Esm;

/// <summary>
///     CLI commands for dumping and tracing ESM records.
/// </summary>
public static class EsmDumpCommand
{
    public static Command CreateDumpCommand()
    {
        var command = new Command("dump", "Dump records of a specific type");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var typeArg = new Argument<string>("type") { Description = "Record type to dump (e.g., LAND, NPC_, WEAP)" };
        var limitOption = new Option<int>("-l", "--limit")
            { Description = "Maximum number of records to dump (0 = unlimited)", DefaultValueFactory = _ => 0 };
        var hexOption = new Option<bool>("-x", "--hex") { Description = "Show hex dump of record data" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(typeArg);
        command.Options.Add(limitOption);
        command.Options.Add(hexOption);

        command.SetAction(parseResult => Dump(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(typeArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(hexOption)));

        return command;
    }

    public static Command CreateTraceCommand()
    {
        var command = new Command("trace", "Trace record/GRUP structure at a specific offset");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var offsetOption = new Option<string?>("-o", "--offset")
            { Description = "Starting offset in hex (e.g., 0x1000)" };
        var stopOption = new Option<string?>("-s", "--stop") { Description = "Stop offset in hex" };
        var depthOption = new Option<int?>("-d", "--depth") { Description = "Filter to specific nesting depth" };
        var limitOption = new Option<int>("-l", "--limit")
            { Description = "Maximum number of records to trace (0 = unlimited)", DefaultValueFactory = _ => 0 };

        command.Arguments.Add(fileArg);
        command.Options.Add(offsetOption);
        command.Options.Add(stopOption);
        command.Options.Add(depthOption);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => Trace(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(offsetOption),
            parseResult.GetValue(stopOption),
            parseResult.GetValue(depthOption),
            parseResult.GetValue(limitOption)));

        return command;
    }

    private static int Dump(string filePath, string type, int limit, bool showHex)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null)
        {
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Dumping:[/] {type} records from {Path.GetFileName(filePath)}");
        AnsiConsole.WriteLine();

        List<AnalyzerRecordInfo> filtered = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning records...", ctx =>
            {
                var allOfType = EsmRecordParser.ScanForRecordType(esm.Data, esm.IsBigEndian, type.ToUpperInvariant());
                filtered = limit > 0 ? allOfType.Take(limit).ToList() : allOfType;
            });

        AnsiConsole.MarkupLine(
            $"Found [cyan]{filtered.Count}[/] {type} records{(limit > 0 ? $" (showing up to {limit})" : "")}");
        AnsiConsole.WriteLine();

        foreach (var rec in filtered)
        {
            EsmDisplayHelpers.DisplayRecord(rec, esm.Data, esm.IsBigEndian, showHex);
        }

        return 0;
    }

    private static int Trace(string filePath, string? offsetStr, string? stopStr, int? filterDepth, int limit)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null)
        {
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Tracing:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine($"File size: {esm.Data.Length:N0} bytes (0x{esm.Data.Length:X8})");
        AnsiConsole.MarkupLine(
            $"TES4 record: size={esm.Tes4Header.DataSize}, first GRUP at [cyan]0x{esm.FirstGrupOffset:X8}[/]");
        AnsiConsole.WriteLine();

        var startOffset = EsmFileLoader.ParseOffset(offsetStr) ?? esm.FirstGrupOffset;
        var stopOffset = EsmFileLoader.ParseOffset(stopStr) ?? esm.Data.Length;

        if (startOffset < esm.FirstGrupOffset)
        {
            startOffset = esm.FirstGrupOffset;
        }

        AnsiConsole.MarkupLine($"Tracing from [cyan]0x{startOffset:X8}[/] to [cyan]0x{stopOffset:X8}[/]");
        AnsiConsole.MarkupLine($"Limit: {(limit <= 0 ? "Unlimited" : limit.ToString())}");
        if (filterDepth.HasValue)
        {
            AnsiConsole.MarkupLine($"Depth filter: {filterDepth}");
        }

        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Offset[/]"))
            .AddColumn(new TableColumn("[bold]Sig[/]"))
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]End[/]"))
            .AddColumn(new TableColumn("[bold]Type/Label[/]"))
            .AddColumn(new TableColumn("[bold]Depth[/]").RightAligned());

        var recordCount = 0;
        TraceRecursive(esm.Data, esm.IsBigEndian, startOffset, stopOffset, filterDepth,
            ref recordCount, limit, 0, table);

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Traced [cyan]{recordCount}[/] records/groups");

        return 0;
    }
}
