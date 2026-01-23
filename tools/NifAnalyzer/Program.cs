using System.CommandLine;
using NifAnalyzer.Commands;

namespace NifAnalyzer;

/// <summary>
///     NIF Analyzer - Standalone tool for analyzing NIF files.
///     Useful for debugging Xbox 360 to PC NIF conversion.
/// </summary>
internal sealed class Program
{
    private Program() { }

    private static int Main(string[] args)
    {
        var rootCommand = new RootCommand("NIF Analyzer - Xbox 360 NIF debugging tool");

        // Register all commands
        rootCommand.Subcommands.Add(InfoCommands.CreateInfoCommand());
        rootCommand.Subcommands.Add(InfoCommands.CreateBlocksCommand());
        rootCommand.Subcommands.Add(InfoCommands.CreateBlockCommand());
        rootCommand.Subcommands.Add(InfoCommands.CreateCompareCommand());
        rootCommand.Subcommands.Add(StringCommands.CreateStringsCommand());
        rootCommand.Subcommands.Add(StringCommands.CreateStringCompareCommand());
        rootCommand.Subcommands.Add(StringCommands.CreateControllerCommand());
        rootCommand.Subcommands.Add(StringCommands.CreateDiffBlockCommand());
        rootCommand.Subcommands.Add(StringCommands.CreatePaletteCommand());
        rootCommand.Subcommands.Add(NodeCommands.CreateNodeCommand());
        rootCommand.Subcommands.Add(NodeCommands.CreateNodeCompareCommand());
        rootCommand.Subcommands.Add(GeometryCommands.CreateGeometryCommand());
        rootCommand.Subcommands.Add(GeometryCommands.CreateGeomCompareCommand());
        rootCommand.Subcommands.Add(GeometryCommands.CreateVerticesCommand());
        rootCommand.Subcommands.Add(GeometryCommands.CreateColorCompareCommand());
        rootCommand.Subcommands.Add(PackedCommands.CreateSkinPartCommand());
        rootCommand.Subcommands.Add(PackedCommands.CreateSkinPartCompareCommand());
        rootCommand.Subcommands.Add(PackedCommands.CreatePackedCommand());
        rootCommand.Subcommands.Add(PackedCommands.CreateStreamDumpCommand());
        rootCommand.Subcommands.Add(PackedCommands.CreateAnalyzeStreamsCommand());
        rootCommand.Subcommands.Add(PackedCommands.CreateNormalCompareCommand());
        rootCommand.Subcommands.Add(HexCommands.CreateHexCommand());
        rootCommand.Subcommands.Add(HavokCommands.CreateHavokCommand());
        rootCommand.Subcommands.Add(HavokCommands.CreateHavokCompareCommand());
        rootCommand.Subcommands.Add(ScanCommands.CreateScanCommand());
        rootCommand.Subcommands.Add(ScanCommands.CreateAnimatedCommand());

        return rootCommand.Parse(args).Invoke();
    }
}