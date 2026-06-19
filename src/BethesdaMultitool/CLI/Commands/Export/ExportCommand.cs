using System.CommandLine;

namespace BethesdaMultitool.CLI.Commands.Export;

internal static class ExportCommand
{
    public static Command Create()
    {
        var command = new Command("export", "Export NIF models, NPCs, and creatures to GLB/glTF");
        command.Subcommands.Add(ExportNifCommand.Create());
        command.Subcommands.Add(ExportNpcCommand.Create());
        command.Subcommands.Add(ExportCreatureCommand.Create());
        return command;
    }
}
