using System.CommandLine;
using TerrainAnalyzer.Commands;

namespace TerrainAnalyzer;

internal sealed class Program
{
    private Program() { }

    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("Terrain heightmap analysis and export tool for Xbox 360 memory dumps");

        rootCommand.Subcommands.Add(ExportCommands.CreateExportCommand());
        rootCommand.Subcommands.Add(DiagCommands.CreateDiagCommand());
        rootCommand.Subcommands.Add(BatchCommands.CreateBatchCommand());
        rootCommand.Subcommands.Add(CompareCommands.CreateCompareCommand());
        rootCommand.Subcommands.Add(ScanCommands.CreateScanCommand());

        return rootCommand.Parse(args).Invoke();
    }
}
