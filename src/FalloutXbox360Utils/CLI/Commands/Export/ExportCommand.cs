using System.CommandLine;

namespace FalloutXbox360Utils.CLI.Commands.Export;

internal static class ExportCommand
{
    public static Command Create()
    {
        var command = new Command("export", "Export game assets to interchange formats");
        command.Subcommands.Add(ExportNifCommand.Create());
        command.Subcommands.Add(ExportNpcCommand.Create());
        return command;
    }
}
