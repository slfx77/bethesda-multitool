using System.Buffers.Binary;
using System.CommandLine;
using System.Text;
using EsmAnalyzer.Conversion.Schema;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for dumping and tracing ESM records.
/// </summary>
public static partial class DumpCommands
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

    public static Command CreateSearchCommand()
    {
        var command = new Command("search", "Search for ASCII string patterns in an ESM file");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var patternArg = new Argument<string>("pattern") { Description = "ASCII string to search for" };
        var limitOption = new Option<int>("-l", "--limit")
            { Description = "Maximum number of matches to show (0 = unlimited)", DefaultValueFactory = _ => 0 };
        var contextOption = new Option<int>("-c", "--context")
            { Description = "Bytes of context to show around matches", DefaultValueFactory = _ => 32 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(patternArg);
        command.Options.Add(limitOption);
        command.Options.Add(contextOption);

        command.SetAction(parseResult => Search(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(patternArg)!,
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

        command.SetAction(parseResult => HexDump(
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

        command.SetAction(parseResult => Locate(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(offsetArg)!));

        return command;
    }

    public static Command CreateValidateCommand()
    {
        var command = new Command("validate", "Validate top-level record/GRUP structure and report first failure");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var startOption = new Option<string?>("-o", "--offset") { Description = "Start offset in hex (optional)" };
        var stopOption = new Option<string?>("-s", "--stop") { Description = "Stop offset in hex (optional)" };

        command.Arguments.Add(fileArg);
        command.Options.Add(startOption);
        command.Options.Add(stopOption);

        command.SetAction(parseResult => Validate(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(startOption),
            parseResult.GetValue(stopOption)));

        return command;
    }

    public static Command CreateValidateDeepCommand()
    {
        var command = new Command("validate-deep",
            "Deep-validate record/GRUP structure and subrecord layout (reports first failure)");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var startOption = new Option<string?>("-o", "--offset") { Description = "Start offset in hex (optional)" };
        var stopOption = new Option<string?>("-s", "--stop") { Description = "Stop offset in hex (optional)" };
        var limitOption = new Option<int>("-l", "--limit")
            { Description = "Maximum number of records to validate (0 = unlimited)", DefaultValueFactory = _ => 0 };

        command.Arguments.Add(fileArg);
        command.Options.Add(startOption);
        command.Options.Add(stopOption);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => ValidateDeep(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(startOption),
            parseResult.GetValue(stopOption),
            parseResult.GetValue(limitOption)));

        return command;
    }

    public static Command CreateValidateRefsCommand()
    {
        var command = new Command("validate-refs",
            "Validate FormID references against existing records (skips compressed records)");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var typeOption = new Option<string?>("-t", "--type")
            { Description = "Filter by record type (e.g., NPC_, INFO, LAND)" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum number of missing references to show (0 = unlimited)",
            DefaultValueFactory = _ => 0
        };
        var outputOption = new Option<string?>("-o", "--output")
            { Description = "Write missing references to a TSV file" };

        command.Arguments.Add(fileArg);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(outputOption);

        command.SetAction(parseResult => ValidateRefs(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(typeOption),
            parseResult.GetValue(limitOption),
            parseResult.GetValue(outputOption)));

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

        command.SetAction(parseResult => FindFormId(
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

        command.SetAction(parseResult => FindCells(
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

        command.SetAction(parseResult => FindCellsByGrid(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(xArg),
            parseResult.GetValue(yArg),
            parseResult.GetValue(limitOption)));

        return command;
    }

    private static int Dump(string filePath, string type, int limit, bool showHex)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        AnsiConsole.MarkupLine($"[blue]Dumping:[/] {type} records from {Path.GetFileName(filePath)}");
        AnsiConsole.WriteLine();

        List<AnalyzerRecordInfo> filtered = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning records...", ctx =>
            {
                var allOfType = EsmHelpers.ScanForRecordType(esm.Data, esm.IsBigEndian, type.ToUpperInvariant());
                filtered = limit > 0 ? allOfType.Take(limit).ToList() : allOfType;
            });

        AnsiConsole.MarkupLine(
            $"Found [cyan]{filtered.Count}[/] {type} records{(limit > 0 ? $" (showing up to {limit})" : "")}");
        AnsiConsole.WriteLine();

        foreach (var rec in filtered) EsmDisplayHelpers.DisplayRecord(rec, esm.Data, esm.IsBigEndian, showHex);

        return 0;
    }

    private static int FindCells(string filePath, string pattern, int limit)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        var term = pattern.Trim();
        if (term.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Pattern must not be empty");
            return 1;
        }

        var matches = 0;
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("FormID")
            .AddColumn("Offset")
            .AddColumn("EDID")
            .AddColumn("FULL")
            .AddColumn("Grid");

        List<AnalyzerRecordInfo> cells = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning CELL records...",
                _ => { cells = EsmHelpers.ScanForRecordType(esm.Data, esm.IsBigEndian, "CELL"); });

        foreach (var cell in cells)
        {
            var recordData = EsmHelpers.GetRecordData(esm.Data, cell, esm.IsBigEndian);
            var subs = EsmHelpers.ParseSubrecords(recordData, esm.IsBigEndian);

            var edid = GetStringSubrecord(subs, "EDID");
            var full = GetStringSubrecord(subs, "FULL");

            if (!ContainsIgnoreCase(edid, term) && !ContainsIgnoreCase(full, term))
                continue;

            var gridText = GetCellGridText(subs, esm.IsBigEndian);

            table.AddRow(
                $"0x{cell.FormId:X8}",
                $"0x{cell.Offset:X8}",
                edid ?? string.Empty,
                full ?? string.Empty,
                gridText);

            matches++;
            if (limit > 0 && matches >= limit)
                break;
        }

        AnsiConsole.MarkupLine(
            $"Found [cyan]{matches}[/] CELL matches for '{Markup.Escape(term)}' in {Path.GetFileName(filePath)}");
        if (matches > 0)
            AnsiConsole.Write(table);

        return 0;
    }

    private static int FindCellsByGrid(string filePath, int targetX, int targetY, int limit)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        var matches = 0;
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("FormID")
            .AddColumn("Offset")
            .AddColumn("EDID")
            .AddColumn("FULL")
            .AddColumn("Grid");

        List<AnalyzerRecordInfo> cells = [];

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning CELL records...",
                _ => { cells = EsmHelpers.ScanForRecordType(esm.Data, esm.IsBigEndian, "CELL"); });

        foreach (var cell in cells)
        {
            var recordData = EsmHelpers.GetRecordData(esm.Data, cell, esm.IsBigEndian);
            var subs = EsmHelpers.ParseSubrecords(recordData, esm.IsBigEndian);

            var gridText = GetCellGridText(subs, esm.IsBigEndian);
            if (string.IsNullOrEmpty(gridText))
                continue;

            if (!TryParseGrid(gridText, out var gridX, out var gridY))
                continue;

            if (gridX != targetX || gridY != targetY)
                continue;

            var edid = GetStringSubrecord(subs, "EDID");
            var full = GetStringSubrecord(subs, "FULL");

            table.AddRow(
                $"0x{cell.FormId:X8}",
                $"0x{cell.Offset:X8}",
                edid ?? string.Empty,
                full ?? string.Empty,
                gridText);

            matches++;
            if (limit > 0 && matches >= limit)
                break;
        }

        AnsiConsole.MarkupLine(
            $"Found [cyan]{matches}[/] CELL records at {targetX},{targetY} in {Path.GetFileName(filePath)}");
        if (matches > 0)
            AnsiConsole.Write(table);

        return 0;
    }

    private static string? GetStringSubrecord(List<AnalyzerSubrecordInfo> subrecords, string signature)
    {
        var sub = subrecords.FirstOrDefault(s => s.Signature == signature);
        if (sub == null || sub.Data.Length == 0) return null;

        var text = Encoding.ASCII.GetString(sub.Data);
        var nullIndex = text.IndexOf('\0');
        return nullIndex >= 0 ? text[..nullIndex] : text;
    }

    private static string GetCellGridText(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian)
    {
        var sub = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
        if (sub == null || sub.Data.Length < 8) return "";

        var x = ReadInt32(sub.Data, 0, bigEndian);
        var y = ReadInt32(sub.Data, 4, bigEndian);
        return $"{x},{y}";
    }

    private static bool TryParseGrid(string gridText, out int x, out int y)
    {
        x = 0;
        y = 0;

        var parts = gridText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;

        return int.TryParse(parts[0], out x) && int.TryParse(parts[1], out y);
    }

    private static int ReadInt32(byte[] data, int offset, bool bigEndian)
    {
        if (offset + 4 > data.Length) return 0;
        return bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static bool ContainsIgnoreCase(string? value, string term)
    {
        return !string.IsNullOrEmpty(value) &&
               value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindFormId(string filePath, string formIdStr, string? filterType, bool showHex,
        string? comparePath)
    {
        var targetFormId = EsmFileLoader.ParseFormId(formIdStr);
        if (!targetFormId.HasValue)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID format: {formIdStr}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        // Load comparison file if specified
        EsmFileLoadResult? compareEsm = null;
        if (!string.IsNullOrEmpty(comparePath))
        {
            compareEsm = EsmFileLoader.Load(comparePath);
            if (compareEsm == null) return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Finding FormID:[/] 0x{targetFormId.Value:X8} in {Path.GetFileName(filePath)}");
        if (compareEsm != null)
            AnsiConsole.MarkupLine($"[blue]Comparing with:[/] {Path.GetFileName(comparePath!)}");
        if (!string.IsNullOrEmpty(filterType))
            AnsiConsole.MarkupLine($"Filter: [cyan]{filterType.ToUpperInvariant()}[/] records only");
        AnsiConsole.WriteLine();

        var matches = new List<AnalyzerRecordInfo>();
        var compareMatches = new List<AnalyzerRecordInfo>();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning for records...",
                ctx =>
                {
                    matches = ScanForFormId(esm.Data, esm.IsBigEndian, esm.FirstGrupOffset, targetFormId.Value,
                        filterType);
                    if (compareEsm != null)
                        compareMatches = ScanForFormId(compareEsm.Data, compareEsm.IsBigEndian,
                            compareEsm.FirstGrupOffset,
                            targetFormId.Value, filterType);
                });

        AnsiConsole.MarkupLine(
            $"Found [cyan]{matches.Count}[/] records with FormID 0x{targetFormId.Value:X8} in primary file");
        if (compareEsm != null)
            AnsiConsole.MarkupLine(
                $"Found [cyan]{compareMatches.Count}[/] records with FormID 0x{targetFormId.Value:X8} in comparison file");
        AnsiConsole.WriteLine();

        if (compareEsm != null)
            // Show comparison view
            DisplayRecordComparison(matches, esm, compareMatches, compareEsm, filePath, comparePath!, showHex);
        else
            // Standard view
            foreach (var rec in matches)
                EsmDisplayHelpers.DisplayRecord(rec, esm.Data, esm.IsBigEndian, showHex, true);

        return 0;
    }

    private static void DisplayRecordComparison(
        List<AnalyzerRecordInfo> primaryMatches, EsmFileLoadResult primaryEsm,
        List<AnalyzerRecordInfo> compareMatches, EsmFileLoadResult compareEsm,
        string primaryPath, string comparePath, bool showHex)
    {
        var primaryFileName = Path.GetFileName(primaryPath);
        var compareFileName = Path.GetFileName(comparePath);

        // If both files have the same name, use parent directory to distinguish
        string primaryName, compareName;
        if (primaryFileName.Equals(compareFileName, StringComparison.OrdinalIgnoreCase))
        {
            var primaryDir = Path.GetFileName(Path.GetDirectoryName(primaryPath)) ?? "primary";
            var compareDir = Path.GetFileName(Path.GetDirectoryName(comparePath)) ?? "compare";
            primaryName = $"{primaryDir}/{primaryFileName}";
            compareName = $"{compareDir}/{compareFileName}";
        }
        else
        {
            primaryName = primaryFileName;
            compareName = compareFileName;
        }

        // Get the first match from each (typically there's only one record per FormID per file)
        var primary = primaryMatches.FirstOrDefault();
        var compare = compareMatches.FirstOrDefault();

        if (primary == null && compare == null)
        {
            AnsiConsole.MarkupLine("[yellow]No records found in either file.[/]");
            return;
        }

        // Parse subrecords for both
        List<AnalyzerSubrecordInfo> primarySubs = [];
        List<AnalyzerSubrecordInfo> compareSubs = [];

        if (primary != null)
            try
            {
                var data = EsmHelpers.GetRecordData(primaryEsm.Data, primary, primaryEsm.IsBigEndian);
                primarySubs = EsmHelpers.ParseSubrecords(data, primaryEsm.IsBigEndian);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to parse primary record:[/] {ex.Message}");
            }

        if (compare != null)
            try
            {
                var data = EsmHelpers.GetRecordData(compareEsm.Data, compare, compareEsm.IsBigEndian);
                compareSubs = EsmHelpers.ParseSubrecords(data, compareEsm.IsBigEndian);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to parse compare record:[/] {ex.Message}");
            }

        // Display header comparison
        var headerTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Property").Centered())
            .AddColumn(new TableColumn(primaryName).Centered())
            .AddColumn(new TableColumn(compareName).Centered())
            .AddColumn(new TableColumn("Match").Centered());

        headerTable.AddRow(
            "Signature",
            primary?.Signature ?? "[dim]N/A[/]",
            compare?.Signature ?? "[dim]N/A[/]",
            primary?.Signature == compare?.Signature ? "[green]✓[/]" : "[red]✗[/]");

        headerTable.AddRow(
            "Data Size",
            primary != null ? $"{primary.DataSize:N0}" : "[dim]N/A[/]",
            compare != null ? $"{compare.DataSize:N0}" : "[dim]N/A[/]",
            primary?.DataSize == compare?.DataSize ? "[green]✓[/]" : "[yellow]≠[/]");

        headerTable.AddRow(
            "Flags",
            primary != null ? $"0x{primary.Flags:X8}" : "[dim]N/A[/]",
            compare != null ? $"0x{compare.Flags:X8}" : "[dim]N/A[/]",
            primary?.Flags == compare?.Flags ? "[green]✓[/]" : "[yellow]≠[/]");

        headerTable.AddRow(
            "Subrecord Count",
            primarySubs.Count.ToString(),
            compareSubs.Count.ToString(),
            primarySubs.Count == compareSubs.Count ? "[green]✓[/]" : "[yellow]≠[/]");

        AnsiConsole.MarkupLine("[bold]Record Header Comparison:[/]");
        AnsiConsole.Write(headerTable);
        AnsiConsole.WriteLine();

        // Display subrecord sequence comparison
        AnsiConsole.MarkupLine("[bold]Subrecord Sequence Comparison:[/]");

        var subTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("#").RightAligned())
            .AddColumn(new TableColumn("Group").Centered())
            .AddColumn(new TableColumn($"{primaryName}").Centered())
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn($"{compareName}").Centered())
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn("Diff").Centered())
            .AddColumn(new TableColumn("Match").Centered());

        var maxCount = Math.Max(primarySubs.Count, compareSubs.Count);
        var mismatchCount = 0;
        var contentMismatchCount = 0;
        var primaryBlockNum = 0;
        var compareBlockNum = 0;
        var primaryScriptDepth = 0;
        var compareScriptDepth = 0;
        var detailMismatches = new List<(int Index, string Signature, int Size, string PrimaryDetails,
            string CompareDetails, string? DiffSummary)>();

        for (var i = 0; i < maxCount; i++)
        {
            var pSub = i < primarySubs.Count ? primarySubs[i] : null;
            var cSub = i < compareSubs.Count ? compareSubs[i] : null;

            // Track script blocks: SCHR starts a block, NEXT separates blocks
            var pGroup = GetSubrecordGroup(pSub?.Signature, ref primaryBlockNum, ref primaryScriptDepth);
            var cGroup = GetSubrecordGroup(cSub?.Signature, ref compareBlockNum, ref compareScriptDepth);

            // Display group indicator (use primary, or compare if primary missing)
            var groupDisplay = pGroup ?? cGroup ?? "";
            if (pGroup != null && cGroup != null && pGroup != cGroup)
                groupDisplay = $"[yellow]{pGroup}|{cGroup}[/]";
            else if (!string.IsNullOrEmpty(groupDisplay))
                groupDisplay = $"[dim]{groupDisplay}[/]";

            var sigMatch = pSub?.Signature == cSub?.Signature;
            var sizeMatch = pSub?.Data.Length == cSub?.Data.Length;
            var contentMatch = sigMatch && sizeMatch && pSub != null && cSub != null &&
                               pSub.Data.SequenceEqual(cSub.Data);
            var fullMatch = contentMatch;

            if (!sigMatch || !sizeMatch)
                mismatchCount++;
            else if (!contentMatch) contentMismatchCount++;

            var matchIcon = fullMatch ? "[green]✓[/]" :
                sigMatch && sizeMatch ? "[yellow]≈[/]" : "[red]✗[/]";

            var diffDisplay = "-";
            string? diffSummary = null;
            string? previewRow = null;
            if (sigMatch && sizeMatch && pSub != null && cSub != null && !contentMatch)
            {
                diffDisplay = BuildDiffDisplay(pSub.Signature, primary?.Signature ?? compare?.Signature ?? string.Empty,
                    pSub.Data, cSub.Data, primaryEsm.IsBigEndian, compareEsm.IsBigEndian, out diffSummary);
                previewRow = BuildPreviewRowText(pSub.Data, cSub.Data, 16);

                var hasPrimaryDetails = EsmDisplayHelpers.TryFormatSubrecordDetails(pSub.Signature, pSub.Data,
                    primaryEsm.IsBigEndian, out var primaryDetails);
                var hasCompareDetails = EsmDisplayHelpers.TryFormatSubrecordDetails(cSub.Signature, cSub.Data,
                    compareEsm.IsBigEndian, out var compareDetails);

                if (hasPrimaryDetails || hasCompareDetails)
                    detailMismatches.Add((
                        i + 1,
                        pSub.Signature,
                        pSub.Data.Length,
                        hasPrimaryDetails ? primaryDetails : "(unparsed)",
                        hasCompareDetails ? compareDetails : "(unparsed)",
                        diffSummary));
            }

            // Color code the signatures
            var pSigDisplay = pSub != null ? $"[cyan]{pSub.Signature}[/]" : "[dim]---[/]";
            var cSigDisplay = cSub != null ? $"[cyan]{cSub.Signature}[/]" : "[dim]---[/]";

            if (!sigMatch)
            {
                pSigDisplay = pSub != null ? $"[red]{pSub.Signature}[/]" : "[dim]---[/]";
                cSigDisplay = cSub != null ? $"[red]{cSub.Signature}[/]" : "[dim]---[/]";
            }

            subTable.AddRow(
                (i + 1).ToString(),
                groupDisplay,
                pSigDisplay,
                pSub != null ? pSub.Data.Length.ToString() : "-",
                cSigDisplay,
                cSub != null ? cSub.Data.Length.ToString() : "-",
                diffDisplay,
                matchIcon);

            if (previewRow != null)
                subTable.AddRow(
                    "",
                    "",
                    "[dim]preview[/]",
                    "",
                    "",
                    "",
                    previewRow,
                    "");
        }

        AnsiConsole.Write(subTable);

        // Summary
        AnsiConsole.WriteLine();
        if (mismatchCount == 0 && contentMismatchCount == 0)
            AnsiConsole.MarkupLine($"[green]✓ All {maxCount} subrecords match in sequence, size, and content[/]");
        else
            AnsiConsole.MarkupLine(
                $"[yellow]⚠ {mismatchCount} position mismatches, {contentMismatchCount} content mismatches[/]");

        // Show unique subrecords in each file
        var primarySigCounts = primarySubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.Count());
        var compareSigCounts = compareSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.Count());

        var onlyInPrimary = primarySigCounts.Keys.Except(compareSigCounts.Keys).ToList();
        var onlyInCompare = compareSigCounts.Keys.Except(primarySigCounts.Keys).ToList();

        if (onlyInPrimary.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]Subrecords only in {primaryName}:[/] {string.Join(", ", onlyInPrimary)}");
        }

        if (onlyInCompare.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[yellow]Subrecords only in {compareName}:[/] {string.Join(", ", onlyInCompare)}");
        }

        // Count differences for shared subrecord types
        var sharedTypes = primarySigCounts.Keys.Intersect(compareSigCounts.Keys).ToList();
        var countDiffs = sharedTypes.Where(t => primarySigCounts[t] != compareSigCounts[t]).ToList();
        if (countDiffs.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Count differences for shared subrecord types:[/]");
            foreach (var sig in countDiffs)
                AnsiConsole.MarkupLine($"  [cyan]{sig}[/]: {primarySigCounts[sig]} vs {compareSigCounts[sig]}");
        }

        if (detailMismatches.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Content mismatch details (interpreted):[/]");

            foreach (var mismatch in detailMismatches)
            {
                var diffText = mismatch.DiffSummary != null ? $" (diff {mismatch.DiffSummary})" : string.Empty;
                AnsiConsole.MarkupLine(
                    $"[cyan]#{mismatch.Index} {mismatch.Signature}[/] ({mismatch.Size} bytes){diffText}");
                AnsiConsole.MarkupLine($"  {primaryName}: {EscapeMarkup(mismatch.PrimaryDetails)}");
                AnsiConsole.MarkupLine($"  {compareName}: {EscapeMarkup(mismatch.CompareDetails)}");
            }
        }

        if (showHex && primary != null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]Hex Dump ({primaryName}, first 256 bytes):[/]");
            var primaryData = EsmHelpers.GetRecordData(primaryEsm.Data, primary, primaryEsm.IsBigEndian);
            EsmDisplayHelpers.RenderHexDumpPanel(primaryData, 256);
        }

        if (showHex && compare != null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]Hex Dump ({compareName}, first 256 bytes):[/]");
            var compareData = EsmHelpers.GetRecordData(compareEsm.Data, compare, compareEsm.IsBigEndian);
            EsmDisplayHelpers.RenderHexDumpPanel(compareData, 256);
        }

        AnsiConsole.WriteLine();
    }

    private static List<AnalyzerRecordInfo> ScanForFormId(byte[] data, bool bigEndian, int startOffset,
        uint targetFormId, string? filterType)
    {
        var matches = new List<AnalyzerRecordInfo>();
        var offset = startOffset;

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var recHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recHeader == null) break;

            if (recHeader.Signature == "GRUP")
            {
                offset += EsmParser.MainRecordHeaderSize;
                continue;
            }

            if (recHeader.FormId == targetFormId)
                if (string.IsNullOrEmpty(filterType) ||
                    recHeader.Signature.Equals(filterType, StringComparison.OrdinalIgnoreCase))
                {
                    var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
                    matches.Add(new AnalyzerRecordInfo
                    {
                        Signature = recHeader.Signature,
                        FormId = recHeader.FormId,
                        Flags = recHeader.Flags,
                        DataSize = recHeader.DataSize,
                        Offset = (uint)offset,
                        TotalSize = (uint)(recordEnd - offset)
                    });
                }

            offset += EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize;
        }

        return matches;
    }

    /// <summary>
    ///     Determines the grouping/context of a subrecord for display purposes.
    ///     Tracks script blocks, response groups, etc.
    /// </summary>
    private static string? GetSubrecordGroup(string? signature, ref int blockNum, ref int scriptDepth)
    {
        if (signature == null) return null;

        return signature switch
        {
            // TRDT starts a response block (dialogue response)
            "TRDT" => $"Resp{++blockNum}",
            "NAM1" or "NAM2" => $"Resp{blockNum}",

            // Script blocks: SCHR starts, NEXT separates Begin/End
            "SCHR" when scriptDepth == 0 => BeginNewScriptBlock(ref scriptDepth, "Begin"),
            "SCHR" => $"Script{scriptDepth}",
            "NEXT" => BeginNewScriptBlock(ref scriptDepth, "End"),
            "SCDA" or "SCTX" or "SCRO" or "SCRV" or "SLSD" or "SCVR" => $"Script{scriptDepth}",

            // Condition groups
            "CTDA" => "Cond",

            // Topic choice links
            "TCLT" or "TCLF" => "Choice",

            _ => null
        };
    }

    private static string BeginNewScriptBlock(ref int scriptDepth, string name)
    {
        scriptDepth++;
        return $"{name}";
    }

    private static FirstDiff? FindFirstDiff(byte[] left, byte[] right)
    {
        var len = Math.Min(left.Length, right.Length);
        for (var i = 0; i < len; i++)
            if (left[i] != right[i])
                return new FirstDiff(i, left[i], right[i]);

        if (left.Length != right.Length)
            return new FirstDiff(len, len < left.Length ? left[len] : (byte)0,
                len < right.Length ? right[len] : (byte)0);

        return null;
    }

    private static string BuildDiffDisplay(string subSignature, string recordType, byte[] left, byte[] right,
        bool leftBigEndian, bool rightBigEndian, out string? diffSummary)
    {
        diffSummary = null;

        var first = FindFirstDiff(left, right);
        if (first == null)
            return "-";

        var diffCount = CountDiffBytes(left, right);

        var previewDisplay = BuildPreviewDisplay(left, right);
        var schema = SubrecordSchemaRegistry.FindSchema(subSignature, recordType, left.Length);
        var isFormId = schema?.IsFormId == true || schema?.IsFormIdArray == true;

        if (left.Length == 4 && right.Length == 4)
        {
            var leftValue = ReadUInt32Value(left, 0, leftBigEndian);
            var rightValue = ReadUInt32Value(right, 0, rightBigEndian);
            diffSummary = isFormId
                ? $"fid 0x{leftValue:X8}->0x{rightValue:X8}"
                : $"u32 {leftValue:X8}->{rightValue:X8}";
            return
                $"[yellow]{(isFormId ? "FormID" : "UInt32")}[/] [dim]first {(isFormId ? "0x" : string.Empty)}{leftValue:X8}, second {(isFormId ? "0x" : string.Empty)}{rightValue:X8}[/] [dim](delta {diffCount})[/]";
        }

        diffSummary =
            $"offset 0x{first.Value.Offset:X}, first {first.Value.Left:X2}, second {first.Value.Right:X2} (delta {diffCount})";
        return
            $"[yellow]offset 0x{first.Value.Offset:X}[/] [dim]first {first.Value.Left:X2}, second {first.Value.Right:X2}[/] [dim](delta {diffCount})[/]";
    }

    private static int CountDiffBytes(byte[] left, byte[] right)
    {
        var len = Math.Min(left.Length, right.Length);
        var count = 0;
        for (var i = 0; i < len; i++)
            if (left[i] != right[i])
                count++;

        count += Math.Abs(left.Length - right.Length);
        return count;
    }

    private static uint ReadUInt32Value(byte[] data, int offset, bool bigEndian)
    {
        if (offset + 4 > data.Length) return 0;
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static string BuildPreviewDisplay(byte[] left, byte[] right)
    {
        var leftPreview = FormatHexBytes(left, 4);
        var rightPreview = FormatHexBytes(right, 4);
        return $"[dim]b4 p:{leftPreview} c:{rightPreview}[/]";
    }

    private static string BuildPreviewRowText(byte[] left, byte[] right, int byteCount)
    {
        var first = FindFirstDiff(left, right);
        var leftPreview = FormatHexBytesWithHighlight(left, byteCount, first?.Offset, true);
        var rightPreview = FormatHexBytesWithHighlight(right, byteCount, first?.Offset, false);
        return $"[dim]first {byteCount} bytes[/]  first: {leftPreview}   second: {rightPreview}";
    }

    private static string FormatHexBytes(byte[] data, int count)
    {
        if (data.Length == 0) return "-";

        var max = Math.Min(count, data.Length);
        var builder = new StringBuilder(max * 3 - 1);
        for (var i = 0; i < max; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(data[i].ToString("X2"));
        }

        return builder.ToString();
    }

    private static string FormatHexBytesWithHighlight(byte[] data, int count, int? diffOffset, bool isPrimary)
    {
        if (data.Length == 0) return "-";

        var max = Math.Min(count, data.Length);
        var builder = new StringBuilder(max * 3 - 1);
        for (var i = 0; i < max; i++)
        {
            if (i > 0) builder.Append(' ');

            var hex = data[i].ToString("X2");
            if (diffOffset.HasValue && i == diffOffset.Value)
                builder.Append(isPrimary ? "[yellow]" : "[red]").Append(hex).Append("[/]");
            else
                builder.Append(hex);
        }

        return builder.ToString();
    }

    private static string EscapeMarkup(string value)
    {
        return Markup.Escape(value);
    }

    private readonly record struct FirstDiff(int Offset, byte Left, byte Right);
}