using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Analysis.FileAnalysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Helpers;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using Spectre.Console;
using static BethesdaMultitool.Core.Formats.Esm.Analysis.Helpers.RecordTraversalHelpers;

namespace BethesdaMultitool.CLI.Commands.Esm;

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
        var hexLimitOption = new Option<int>("--hex-limit")
            { Description = "Decoded payload bytes to display (0 = all)", DefaultValueFactory = _ => 256 };
        var formIdOption = new Option<string?>("--formid") { Description = "Select hexadecimal FormID" };
        var offsetOption = new Option<string?>("--offset") { Description = "Select hexadecimal record offset" };
        var outputOption = new Option<string?>("--output", "-o") { Description = "Write one payload and .json metadata" };
        var payloadOption = new Option<string>("--payload")
            { Description = "Binary payload representation: stored or decoded", DefaultValueFactory = _ => "decoded" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(typeArg);
        command.Options.Add(limitOption);
        command.Options.Add(hexOption);
        command.Options.Add(hexLimitOption);
        command.Options.Add(formIdOption);
        command.Options.Add(offsetOption);
        command.Options.Add(outputOption);
        command.Options.Add(payloadOption);

        command.SetAction(parseResult => Dump(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(typeArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(hexOption),
            parseResult.GetValue(hexLimitOption),
            parseResult.GetValue(formIdOption),
            parseResult.GetValue(offsetOption),
            parseResult.GetValue(outputOption),
            parseResult.GetValue(payloadOption)!));

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

    private static int Dump(string filePath, string type, int limit, bool showHex, int hexLimit,
        string? formIdText, string? offsetText, string? output, string payload)
    {
        if (limit < 0 || hexLimit < 0 || payload is not ("stored" or "decoded") ||
            !TryHex(formIdText, out var formId) || !TryHex(offsetText, out var offset))
        {
            Console.Error.WriteLine("Invalid limit, identifier, offset or payload representation.");
            return 1;
        }
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
                filtered = allOfType.Where(record => (!formId.HasValue || record.FormId == formId) &&
                    (!offset.HasValue || record.Offset == offset)).ToList();
            });

        if (output is not null)
        {
            if (filtered.Count != 1)
            {
                Console.Error.WriteLine($"Payload export requires one occurrence; matched {filtered.Count}. Use --formid and --offset.");
                return 1;
            }
            try
            {
                RecordPayloadExport.Write(filePath, esm.Data, filtered[0], esm.IsBigEndian, payload == "decoded", output);
                return 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }
        if (limit > 0) { filtered = filtered.Take(limit).ToList(); }

        AnsiConsole.MarkupLine(
            $"Found [cyan]{filtered.Count}[/] {type} records{(limit > 0 ? $" (showing up to {limit})" : "")}");
        AnsiConsole.WriteLine();

        foreach (var rec in filtered)
        {
            EsmDisplayHelpers.DisplayRecord(rec, esm.Data, esm.IsBigEndian, showHex, hexLimit: hexLimit);
        }

        return filtered.Count > 0 ? 0 : 1;
    }

    private static bool TryHex(string? text, out uint? result)
    {
        result = null;
        if (text is null) { return true; }
        var digits = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) { return false; }
        result = value;
        return true;
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
