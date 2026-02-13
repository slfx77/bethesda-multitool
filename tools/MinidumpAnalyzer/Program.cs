using System.CommandLine;
using MinidumpAnalyzer.Commands;

var rootCommand = new RootCommand("Xbox 360 minidump analysis tool");

rootCommand.Subcommands.Add(RegionCommands.CreateRegionsCommand());
rootCommand.Subcommands.Add(RegionCommands.CreateModulesCommand());
rootCommand.Subcommands.Add(RegionCommands.CreateVa2OffsetCommand());
rootCommand.Subcommands.Add(RegionCommands.CreateHexDumpCommand());
rootCommand.Subcommands.Add(FaceGenCommands.CreateGenFaceGenCommand());
rootCommand.Subcommands.Add(ScriptCommands.CreateScriptsCommand());

return rootCommand.Parse(args).Invoke();
