using Spectre.Console;
using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for dumping and tracing ESM records.
///     Implementation methods are delegated to standalone classes.
/// </summary>
public static class DumpCommands
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

        command.SetAction(parseResult => DumpCommandsTrace.Trace(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(offsetOption),
            parseResult.GetValue(stopOption),
            parseResult.GetValue(depthOption),
            parseResult.GetValue(limitOption)));

        return command;
    }

    public static Command CreateSearchCommand() => CreateSearchCommandCore("search", "Search for ASCII string patterns in an ESM file");

    /// <summary>Creates a search command named "text" for use as a subcommand.</summary>
    public static Command CreateTextSearchCommand() => CreateSearchCommandCore("text", "Search for ASCII string patterns in an ESM file");

    private static Command CreateSearchCommandCore(string name, string description)
    {
        var command = new Command(name, description);

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var patternArg = new Argument<string>("pattern") { Description = "ASCII string to search for" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Maximum number of matches to show (0 = unlimited)", DefaultValueFactory = _ => 0 };
        var contextOption = new Option<int>("-c", "--context")
        { Description = "Bytes of context to show around matches", DefaultValueFactory = _ => 32 };
        var locateOption = new Option<bool>("--locate")
        { Description = "Also locate the record/GRUP containing each match" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(patternArg);
        command.Options.Add(limitOption);
        command.Options.Add(contextOption);
        command.Options.Add(locateOption);

        command.SetAction(parseResult => DumpCommandsSearchHex.Search(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(patternArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(contextOption),
            parseResult.GetValue(locateOption)));

        return command;
    }

    public static Command CreateRawHexSearchCommand()
    {
        var command = new Command("raw-hex",
            "Walk records, decompress bodies, and search for a hex pattern (sees inside compressed records)");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var patternArg = new Argument<string>("pattern")
        { Description = "Hex pattern (e.g. \"07 07 05 01 03 07 06\" or \"07070501030706\")" };
        var typeOption = new Option<string?>("-t", "--type")
        { Description = "Restrict to a single record type (e.g. NPC_)" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Maximum hits to render (0 = unlimited). Total count is always reported.", DefaultValueFactory = _ => 0 };
        var contextOption = new Option<int>("-c", "--context")
        { Description = "Bytes of context around each hit", DefaultValueFactory = _ => 32 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(patternArg);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(contextOption);

        command.SetAction(parseResult => DumpCommandsRawHexSearch.Run(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(patternArg)!,
            parseResult.GetValue(typeOption),
            parseResult.GetValue(limitOption),
            parseResult.GetValue(contextOption)));

        return command;
    }

    public static Command CreateHexCommand()
    {
        var command = new Command("hex", "Hex dump of raw bytes at a specific offset");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var offsetArg = new Argument<string>("offset") { Description = "Starting offset in hex (e.g., 0x1000)" };
        var lengthOption = new Option<int>("-l", "--length")
        { Description = "Number of bytes to dump", DefaultValueFactory = _ => 256 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(offsetArg);
        command.Options.Add(lengthOption);

        command.SetAction(parseResult => DumpCommandsSearchHex.HexDump(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(offsetArg)!,
            parseResult.GetValue(lengthOption)));

        return command;
    }

    public static Command CreateLocateCommand()
    {
        var command = new Command("locate", "Locate which record/GRUP contains a file offset");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var offsetArg = new Argument<string>("offset") { Description = "Target offset in hex (e.g., 0x1000)" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(offsetArg);

        command.SetAction(parseResult => DumpCommandsLocate.Locate(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(offsetArg)!));

        return command;
    }

    public static Command CreateLocateFormIdCommand()
    {
        var command = new Command("locate-formid",
            "Locate a FormID and print its GRUP ancestry (e.g., World Children / Cell Persistent vs Temporary)");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var formidArg = new Argument<string>("formid") { Description = "FormID to locate (hex, e.g., 0x000A471E)" };
        var typeOption = new Option<string?>("-t", "--type") { Description = "Filter by record type (e.g., REFR, CELL)" };
        var compareOption = new Option<string?>("-c", "--compare") { Description = "Compare ancestry with a second ESM" };
        var allOption = new Option<bool>("-a", "--all") { Description = "Show ancestry for all matches" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(formidArg);
        command.Options.Add(typeOption);
        command.Options.Add(compareOption);
        command.Options.Add(allOption);

        command.SetAction(parseResult => DumpCommandsLocate.LocateFormId(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(formidArg)!,
            parseResult.GetValue(typeOption),
            parseResult.GetValue(compareOption),
            parseResult.GetValue(allOption)));

        return command;
    }

    public static Command CreateValidateCommand() => CreateValidateCommandCore("validate", "Validate top-level record/GRUP structure and report first failure");

    /// <summary>Creates a validate command named "structure" for use as a subcommand.</summary>
    public static Command CreateStructureValidateCommand() => CreateValidateCommandCore("structure", "Validate top-level record/GRUP structure and report first failure");

    private static Command CreateValidateCommandCore(string name, string description)
    {
        var command = new Command(name, description);

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var startOption = new Option<string?>("-o", "--offset") { Description = "Start offset in hex (optional)" };
        var stopOption = new Option<string?>("-s", "--stop") { Description = "Stop offset in hex (optional)" };

        command.Arguments.Add(fileArg);
        command.Options.Add(startOption);
        command.Options.Add(stopOption);

        command.SetAction(parseResult => DumpCommandsTrace.Validate(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(startOption),
            parseResult.GetValue(stopOption)));

        return command;
    }

    public static Command CreateValidateDeepCommand() => CreateValidateDeepCommandCore("validate-deep", "Deep-validate record/GRUP structure and subrecord layout (reports first failure)");

    /// <summary>Creates a validate-deep command named "deep" for use as a subcommand.</summary>
    public static Command CreateDeepValidateCommand() => CreateValidateDeepCommandCore("deep", "Deep-validate record/GRUP structure and subrecord layout");

    private static Command CreateValidateDeepCommandCore(string name, string description)
    {
        var command = new Command(name, description);

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var startOption = new Option<string?>("-o", "--offset") { Description = "Start offset in hex (optional)" };
        var stopOption = new Option<string?>("-s", "--stop") { Description = "Stop offset in hex (optional)" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Maximum number of records to validate (0 = unlimited)", DefaultValueFactory = _ => 0 };

        command.Arguments.Add(fileArg);
        command.Options.Add(startOption);
        command.Options.Add(stopOption);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => DumpCommandsTrace.ValidateDeep(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(startOption),
            parseResult.GetValue(stopOption),
            parseResult.GetValue(limitOption)));

        return command;
    }

    public static Command CreateFindFormIdCommand()
    {
        var command = new Command("find-formid", "Find all records with a specific FormID and show their structure");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var formidArg = new Argument<string>("formid") { Description = "FormID to search for (hex, e.g., 0x000A471E)" };
        var typeOption = new Option<string?>("-t", "--type") { Description = "Filter by record type (e.g., INFO)" };
        var hexOption = new Option<bool>("-x", "--hex") { Description = "Show hex dump of record data" };
        var compareOption = new Option<string?>("-c", "--compare")
        { Description = "Compare with record from another ESM file" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(formidArg);
        command.Options.Add(typeOption);
        command.Options.Add(hexOption);
        command.Options.Add(compareOption);

        command.SetAction(parseResult => DumpCommandsFormIdSearch.FindFormId(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(formidArg)!,
            parseResult.GetValue(typeOption),
            parseResult.GetValue(hexOption),
            parseResult.GetValue(compareOption)));

        return command;
    }

    public static Command CreateFindCellCommand()
    {
        var command = new Command("find-cell", "Find CELL records by EDID or FULL name");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var patternArg = new Argument<string>("pattern") { Description = "Search term (case-insensitive)" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Maximum number of matches to show (0 = unlimited)", DefaultValueFactory = _ => 20 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(patternArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => DumpCommandsCellSearch.FindCells(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(patternArg)!,
            parseResult.GetValue(limitOption)));

        return command;
    }

    public static Command CreateFindCellGridCommand()
    {
        var command = new Command("find-cell-grid", "Find CELL records by grid coordinates (XCLC)");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var xArg = new Argument<int>("x") { Description = "Grid X coordinate (e.g., -32)" };
        var yArg = new Argument<int>("y") { Description = "Grid Y coordinate (e.g., -32)" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Maximum number of matches to show (0 = unlimited)", DefaultValueFactory = _ => 20 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(xArg);
        command.Arguments.Add(yArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => DumpCommandsCellSearch.FindCellsByGrid(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(xArg),
            parseResult.GetValue(yArg),
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
}
