using System.CommandLine;
using EgtAnalyzer.Commands;

var rootCommand = new RootCommand("EGT texture verification and comparison tool");
rootCommand.Subcommands.Add(VerifyEgtCommand.Create());
rootCommand.Subcommands.Add(InspectTriCommand.Create());
rootCommand.Subcommands.Add(CompareRuntimeCommand.Create());
rootCommand.Subcommands.Add(CompareCommand.Create());
rootCommand.Subcommands.Add(DxtRoundtripCommand.Create());
rootCommand.Subcommands.Add(D3dxCompareCommand.Create());

return rootCommand.Parse(args).Invoke();
