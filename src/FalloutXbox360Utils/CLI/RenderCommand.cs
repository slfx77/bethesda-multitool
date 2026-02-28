using System.CommandLine;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Parent command grouping all rendering subcommands.
/// </summary>
public static class RenderCommand
{
    public static Command Create()
    {
        var command = new Command("render", "Render NIF models to PNG sprites");
        command.Subcommands.Add(SpriteGenCommand.Create());
        command.Subcommands.Add(NpcSpriteGenCommand.Create());
        return command;
    }
}
