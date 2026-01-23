using System.CommandLine;
using EsmAnalyzer.Commands;
using Spectre.Console;

namespace EsmAnalyzer;

// =====================================================================
// ESM Analyzer - Xbox 360 / PC ESM File Analysis Tool
// =====================================================================

internal sealed class Program
{
    private Program() { }

    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("ESM Analyzer - Analyze and compare Xbox 360 and PC ESM files");

        // Add commands
        rootCommand.Subcommands.Add(StatsCommands.CreateStatsCommand());
        rootCommand.Subcommands.Add(CompareCommands.CreateCompareCommand());
        rootCommand.Subcommands.Add(CompareCommands.CreateCompareLandCommand());
        rootCommand.Subcommands.Add(CompareCommands.CreateCompareCellsCommand());
        rootCommand.Subcommands.Add(CompareCommands.CreateCompareFullCommand());
        rootCommand.Subcommands.Add(CompareCommands.CreateCompareHeightmapsCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateDumpCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateTraceCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateSearchCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateHexCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateLocateCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateValidateCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateValidateDeepCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateFindFormIdCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateFindCellCommand());
        rootCommand.Subcommands.Add(DumpCommands.CreateFindCellGridCommand());
        rootCommand.Subcommands.Add(ExportCommands.CreateExportLandCommand());
        rootCommand.Subcommands.Add(ExportCommands.CreateWorldmapCommand());
        rootCommand.Subcommands.Add(DiffCommands.CreateDiffCommand());
        rootCommand.Subcommands.Add(DiffCommands.CreateDiffHeaderCommand());
        rootCommand.Subcommands.Add(ConvertCommands.CreateConvertCommand());
        rootCommand.Subcommands.Add(GrupCommands.CreateGrupsCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstCompareCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstLocateCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstCellCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstPatternCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstOrderCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstBlocksCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstTileOrderCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstDeltasCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstValidateCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstImageCommand());
        rootCommand.Subcommands.Add(OfstCommands.CreateOfstQuadtreeCommand());
        rootCommand.Subcommands.Add(ToftCommands.CreateToftCommand());
        rootCommand.Subcommands.Add(HeuristicCommands.CreateGeomSearchCommand());
        rootCommand.Subcommands.Add(LandCommands.CreateLandSummaryCommand());

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

            table.AddRow("[cyan]stats[/]", "Display record type statistics for an ESM file");
            table.AddRow("[cyan]compare[/]", "Compare two ESM files (Xbox 360 vs PC)");
            table.AddRow("[cyan]compare-cells[/]", "Compare CELL FormIDs using flat scan");
            table.AddRow("[cyan]compare-land[/]", "Compare LAND records between Xbox 360 and PC");
            table.AddRow("[cyan]dump[/]", "Dump records of a specific type");
            table.AddRow("[cyan]trace[/]", "Trace record/GRUP structure at a specific offset");
            table.AddRow("[cyan]search[/]", "Search for ASCII string patterns in file");
            table.AddRow("[cyan]hex[/]", "Hex dump of raw bytes at a specific offset");
            table.AddRow("[cyan]locate[/]", "Locate which record/GRUP contains a file offset");
            table.AddRow("[cyan]validate[/]", "Validate record/GRUP structure and report first failure");
            table.AddRow("[cyan]validate-deep[/]", "Deep-validate records and subrecords");
            table.AddRow("[cyan]validate-refs[/]",
                "Validate FormID references against existing records (optional TSV output)");
            table.AddRow("[cyan]find-formid[/]", "Find all records with a specific FormID");
            table.AddRow("[cyan]find-cell[/]", "Find CELL records by EDID or FULL name");
            table.AddRow("[cyan]find-cell-grid[/]", "Find CELL records by grid coordinates (XCLC)");
            table.AddRow("[cyan]export-land[/]", "Export LAND records as images and JSON");
            table.AddRow("[cyan]worldmap[/]", "Generate worldspace heightmap by stitching LAND records");
            table.AddRow("[cyan]diff[/]", "Show byte-level differences between Xbox 360 and PC records");
            table.AddRow("[cyan]diff-header[/]", "Show byte-level differences in ESM file headers");
            table.AddRow("[cyan]convert[/]", "Convert Xbox 360 ESM to PC little-endian format");
            table.AddRow("[cyan]grups[/]", "Analyze GRUP structure, nesting, and find duplicates");
            table.AddRow("[cyan]ofst[/]", "Extract WRLD OFST offset table for a worldspace");
            table.AddRow("[cyan]ofst-compare[/]", "Compare WRLD OFST offset tables between Xbox 360 and PC");
            table.AddRow("[cyan]ofst-locate[/]", "Locate records referenced by WRLD OFST offsets");
            table.AddRow("[cyan]ofst-cell[/]", "Resolve a CELL FormID to its WRLD OFST entry");
            table.AddRow("[cyan]ofst-pattern[/]", "Analyze WRLD OFST layout ordering");
            table.AddRow("[cyan]ofst-order[/]", "Score WRLD OFST layout ordering patterns");
            table.AddRow("[cyan]ofst-blocks[/]", "Summarize WRLD OFST block visitation and inner order");
            table.AddRow("[cyan]ofst-tile[/]", "Dump per-tile inner order matrix from WRLD OFST");
            table.AddRow("[cyan]ofst-validate[/]", "Validate OFST offsets vs CELL grid data");
            table.AddRow("[cyan]toft[/]", "Analyze the Xbox 360 TOFT streaming cache region");
            table.AddRow("[cyan]geom-search[/]", "Heuristic search for PC geometry data inside Xbox ESM");
            table.AddRow("[cyan]land-summary[/]", "Summarize LAND subrecords with human-readable interpretation");

            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("Use [cyan]--help[/] with any command for more information.");
            AnsiConsole.MarkupLine("Example: [grey]dotnet run -- stats FalloutNV.esm[/]");
        });

        return rootCommand.Parse(args).Invoke();
    }
}