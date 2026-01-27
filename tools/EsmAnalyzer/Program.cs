using EsmAnalyzer.Commands;
using Spectre.Console;
using System.CommandLine;

namespace EsmAnalyzer;

// =====================================================================
// ESM Analyzer - Xbox 360 / PC ESM File Analysis Tool
// =====================================================================

internal sealed class Program
{
    private Program()
    {
    }

    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("ESM Analyzer - Analyze and compare Xbox 360 and PC ESM files");

        // ===== Top-level commands =====
        rootCommand.Subcommands.Add(StatsCommands.CreateStatsCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateDumpCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateTraceCommand());
        rootCommand.Subcommands.Add(ConvertCommands.CreateConvertCommand());
        rootCommand.Subcommands.Add(GrupCommands.CreateGrupsCommand());
        rootCommand.Subcommands.Add(ToftCommands.CreateToftCommand());
        rootCommand.Subcommands.Add(LandCommands.CreateLandSummaryCommand());
        rootCommand.Subcommands.Add(ExportCommands.CreateExportLandCommand());
        rootCommand.Subcommands.Add(ExportCommands.CreateWorldmapCommand());

        // ===== Backward compatibility aliases =====
        // Keep old command names for scripts that use them
        // Note: Avoid adding commands whose names conflict with command groups (diff, search, validate)
        rootCommand.Subcommands.Add(DiffCommands.CreateDiff3Command());  // "diff3" at root (shortcut for "diff three")
        rootCommand.Subcommands.Add(SemanticDiffCommands.CreateSemanticDiffCommand());  // "semdiff" at root
        rootCommand.Subcommands.Add(FormIdAuditCommands.CreateFormIdAuditCommand());  // "formid-audit" at root
        rootCommand.Subcommands.Add(RecordDiffCommands.CreateRecordDiffCommand());  // "record-diff" at root
        rootCommand.Subcommands.Add(CompareCommands.CreateCompareLandCommand());  // "compare-land" at root
        rootCommand.Subcommands.Add(CompareCommands.CreateCompareCellsCommand()); // "compare-cells" at root
        rootCommand.Subcommands.Add(DumpCommands.CreateHexCommand());    // "hex" at root
        rootCommand.Subcommands.Add(DumpCommands.CreateLocateCommand()); // "locate" at root
        rootCommand.Subcommands.Add(DumpCommands.CreateValidateDeepCommand()); // "validate-deep" at root
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstCommand());   // "ofst" at root
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstCompareCommand()); // "ofst-compare" at root
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstImageCommand()); // "ofst-image" at root

        // ===== compare subcommands =====
        var compareCommand = new Command("compare", "Compare ESM files (land, cells, heightmaps)");
        compareCommand.Subcommands.Add(CompareCommands.CreateLandCommand());       // "land" instead of "compare-land"
        compareCommand.Subcommands.Add(CompareCommands.CreateCellsCommand());      // "cells" instead of "compare-cells"
        compareCommand.Subcommands.Add(CompareCommands.CreateHeightmapsCommand()); // "heightmaps" instead of "compare-heightmaps"
        rootCommand.Subcommands.Add(compareCommand);

        // ===== search subcommands =====
        var searchCommand = new Command("search", "Search and locate data within ESM files");
        searchCommand.Subcommands.Add(DumpCommands.CreateTextSearchCommand());  // "text" instead of "search"
        searchCommand.Subcommands.Add(DumpCommands.CreateHexCommand());
        searchCommand.Subcommands.Add(DumpCommands.CreateLocateCommand());
        searchCommand.Subcommands.Add(DumpCommands.CreateLocateFormIdCommand());
        searchCommand.Subcommands.Add(DumpCommands.CreateFindFormIdCommand());
        searchCommand.Subcommands.Add(DumpCommands.CreateFindCellCommand());
        searchCommand.Subcommands.Add(DumpCommands.CreateFindCellGridCommand());
        searchCommand.Subcommands.Add(HeuristicCommands.CreateGeomSearchCommand());
        rootCommand.Subcommands.Add(searchCommand);

        // ===== validate subcommands =====
        var validateCommand = new Command("validate", "Validate ESM file structure and integrity");
        validateCommand.Subcommands.Add(DumpCommands.CreateStructureValidateCommand());  // "structure" instead of "validate"
        validateCommand.Subcommands.Add(DumpCommands.CreateDeepValidateCommand());       // "deep" instead of "validate-deep"
        rootCommand.Subcommands.Add(validateCommand);

        // ===== diff subcommands =====
        // Unified 'diff' command: use --xbox, --converted, --pc (at least 2 required)
        rootCommand.Subcommands.Add(DiffCommands.CreateUnifiedDiffCommand());
        // Note: diff3 alias already added above for backward compatibility

        // ===== wrld subcommands (WRLD OFST streaming data) =====
        var wrldCommand = new Command("wrld", "Analyze WRLD worldspace data (OFST offset tables, streaming)");
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstCompareCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstLocateCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstCellCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstPatternCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstOrderCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstBlocksCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstTileOrderCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstDeltasCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstValidateCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstImageCommand());
        wrldCommand.Subcommands.Add(OfstCommands.CreateOfstQuadtreeCommand());
        rootCommand.Subcommands.Add(wrldCommand);

        // Default action: show help
        rootCommand.SetAction(parseResult =>
        {
            AnsiConsole.Write(new FigletText("ESM Analyzer")
                .LeftJustified()
                .Color(Color.Cyan1));

            AnsiConsole.MarkupLine("[bold]Xbox 360 / PC ESM File Analysis Tool[/]");
            AnsiConsole.WriteLine();

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("[bold]Command[/]")
                .AddColumn("[bold]Description[/]");

            _ = table.AddRow("[cyan]stats[/]", "Display record type statistics for an ESM file");
            _ = table.AddRow("[cyan]dump[/]", "Dump records of a specific type");
            _ = table.AddRow("[cyan]trace[/]", "Trace record/GRUP structure at a specific offset");
            _ = table.AddRow("[cyan]convert[/]", "Convert Xbox 360 ESM to PC little-endian format");
            _ = table.AddRow("[cyan]grups[/]", "Analyze GRUP structure, nesting, and find duplicates");
            _ = table.AddRow("[cyan]toft[/]", "Analyze the Xbox 360 TOFT streaming cache region");
            _ = table.AddRow("[cyan]land-summary[/]", "Summarize LAND subrecords with human-readable interpretation");
            _ = table.AddRow("[cyan]export-land[/]", "Export LAND records as images and JSON");
            _ = table.AddRow("[cyan]worldmap[/]", "Generate worldspace heightmap by stitching LAND records");
            _ = table.AddRow("", "");
            _ = table.AddRow("[bold yellow]compare[/]", "[bold]Compare ESM files[/]");
            _ = table.AddRow("  [cyan]compare land[/]", "Compare LAND records between Xbox 360 and PC");
            _ = table.AddRow("  [cyan]compare cells[/]", "Compare CELL FormIDs using flat scan");
            _ = table.AddRow("  [cyan]compare heightmaps[/]", "Compare terrain heightmaps between two ESM files");
            _ = table.AddRow("", "");
            _ = table.AddRow("[bold yellow]search[/]", "[bold]Search and locate data[/]");
            _ = table.AddRow("  [cyan]search text[/]", "Search for ASCII string patterns in file");
            _ = table.AddRow("  [cyan]search hex[/]", "Hex dump of raw bytes at a specific offset");
            _ = table.AddRow("  [cyan]search locate[/]", "Locate which record/GRUP contains a file offset");
            _ = table.AddRow("  [cyan]search find-formid[/]", "Find all records with a specific FormID");
            _ = table.AddRow("  [cyan]search locate-formid[/]", "Show GRUP ancestry for a FormID");
            _ = table.AddRow("  [cyan]search find-cell[/]", "Find CELL records by EDID or FULL name");
            _ = table.AddRow("  [cyan]search find-cell-grid[/]", "Find CELL records by grid coordinates");
            _ = table.AddRow("  [cyan]search geom-search[/]", "Heuristic search for PC geometry data inside Xbox ESM");
            _ = table.AddRow("", "");
            _ = table.AddRow("[bold yellow]validate[/]", "[bold]Validate ESM structure[/]");
            _ = table.AddRow("  [cyan]validate structure[/]", "Validate record/GRUP structure (first failure)");
            _ = table.AddRow("  [cyan]validate deep[/]", "Deep-validate records and subrecords");
            _ = table.AddRow("", "");
            _ = table.AddRow("[bold yellow]diff[/]", "[bold]Compare and diff ESM files[/]");
            _ = table.AddRow("  [cyan]diff two[/]", "Compare two ESM files (stats, byte-level, headers)");
            _ = table.AddRow("  [cyan]diff three[/]", "Three-way diff: Xbox 360 → Converted → PC reference");
            _ = table.AddRow("  [cyan]diff semdiff[/]", "Semantic diff - human-readable field differences");
            _ = table.AddRow("", "");
            _ = table.AddRow("[bold yellow]wrld[/]", "[bold]WRLD worldspace analysis (OFST streaming)[/]");
            _ = table.AddRow("  [cyan]wrld ofst[/]", "Extract WRLD OFST offset table for a worldspace");
            _ = table.AddRow("  [cyan]wrld ofst-compare[/]", "Compare WRLD OFST offset tables (Xbox vs PC)");
            _ = table.AddRow("  [cyan]wrld ofst-locate[/]", "Locate records referenced by WRLD OFST offsets");
            _ = table.AddRow("  [cyan]wrld ofst-cell[/]", "Resolve a CELL FormID to its WRLD OFST entry");
            _ = table.AddRow("  [cyan]wrld ofst-validate[/]", "Validate OFST offsets vs CELL grid data");
            _ = table.AddRow("  [cyan]wrld ofst-image[/]", "Visualize WRLD OFST as an image");

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Use [cyan]--help[/] with any command for more information.");
            AnsiConsole.MarkupLine("Example: [grey]dotnet run -- stats FalloutNV.esm[/]");
            AnsiConsole.MarkupLine("Example: [grey]dotnet run -- diff three --xbox x.esm --converted c.esm --pc p.esm[/]");
            AnsiConsole.MarkupLine("");
            AnsiConsole.MarkupLine("[dim]Backward compat aliases: diff3, semdiff, compare-land, compare-cells, hex, locate, validate-deep, ofst, ofst-compare, ofst-image[/]");
        });

        return rootCommand.Parse(args).Invoke();
    }
}