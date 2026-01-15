using System.CommandLine;
using TextureAnalyzer.Commands;

namespace TextureAnalyzer;

/// <summary>
///     Texture Analyzer - Standalone tool for analyzing DDX/DDS texture files.
///     Useful for debugging Xbox 360 to PC texture conversion (3XDO/3XDR formats).
/// </summary>
internal class Program
{
    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("Texture Analyzer - Xbox 360 DDX/DDS debugging tool");

        // Register all commands
        rootCommand.Subcommands.Add(InfoCommands.CreateInfoCommand());
        rootCommand.Subcommands.Add(ScanCommands.CreateScanCommand());
        rootCommand.Subcommands.Add(ScanCommands.CreateStatsCommand());
        rootCommand.Subcommands.Add(CompareCommands.CreateCompareCommand());
        rootCommand.Subcommands.Add(CompareCommands.CreateDataCompareCommand());
        rootCommand.Subcommands.Add(HexCommands.CreateHexCommand());
        rootCommand.Subcommands.Add(DecompressCommands.CreateDecompressCommand());
        rootCommand.Subcommands.Add(ConvertCommands.CreateConvertCommand());
        rootCommand.Subcommands.Add(BlockMapCommands.CreateBlockMapCommand());

        return rootCommand.Parse(args).Invoke();
    }
}
