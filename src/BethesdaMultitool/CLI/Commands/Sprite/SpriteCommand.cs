using System.CommandLine;
using BethesdaMultitool.CLI.Rendering.Sprite;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Sprite;

/// <summary>
///     <c>sprite</c> command group — render/inspect classic-game palettized 2D art (the counterpart
///     of <c>render</c> for NIFs). Arena IMG/MNU/SET/CIF/DFA today; further classic families join as
///     their game verticals land.
/// </summary>
public static class SpriteCommand
{
    public static Command Create()
    {
        var command = new Command("sprite", "Render or inspect classic-game 2D sprites/images (PNG output)");
        command.Subcommands.Add(CreateRenderCommand());
        command.Subcommands.Add(CreateInfoCommand());
        return command;
    }

    private static Command CreateRenderCommand()
    {
        var command = new Command("render", "Decode a sprite/image (loose file or archive entry) to PNG frames");
        var inputArg = new Argument<string>("input")
        {
            Description = "Loose image file (IMG/MNU/SET/CIF/DFA), or an archive when --entry is given"
        };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the entry inside the archive input"
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for PNG frames",
            DefaultValueFactory = _ => "TestOutput/sprites"
        };
        var paletteOption = new Option<string?>("--palette")
        {
            Description = "Palette file (776-byte Arena COL or raw 768-byte 6-bit RGB); " +
                          "default: embedded palette, else PAL.COL beside the source"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.Options.Add(paletteOption);
        command.SetAction((parseResult, _) =>
        {
            try
            {
                var result = SpriteRenderPipeline.Render(
                    parseResult.GetValue(inputArg)!,
                    parseResult.GetValue(entryOption),
                    parseResult.GetValue(outputOption)!,
                    parseResult.GetValue(paletteOption));

                AnsiConsole.MarkupLine(
                    "[green]Wrote {0} frame(s)[/] (palette: {1})",
                    result.Frames.Count,
                    Markup.Escape(result.PaletteSource));
                foreach (var frame in result.Frames)
                {
                    AnsiConsole.MarkupLine(
                        "  {0}  [grey]{1}x{2} @ ({3},{4})[/]",
                        Markup.Escape(frame.Path),
                        frame.Width,
                        frame.Height,
                        frame.XOffset,
                        frame.YOffset);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
                return Task.FromResult(1);
            }

            return Task.FromResult(0);
        });
        return command;
    }

    private static Command CreateInfoCommand()
    {
        var command = new Command("info", "Show frame/palette metadata for a sprite/image without writing PNGs");
        var inputArg = new Argument<string>("input") { Description = "Loose image file or archive (with --entry)" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the entry inside the archive input"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) =>
        {
            try
            {
                var frames = SpriteRenderPipeline.Inspect(
                    parseResult.GetValue(inputArg)!,
                    parseResult.GetValue(entryOption),
                    out var logicalName);

                var table = new Table { Border = TableBorder.Rounded };
                table.AddColumn("Frame");
                table.AddColumn("Size");
                table.AddColumn("Offset");
                for (var i = 0; i < frames.Count; i++)
                {
                    table.AddRow(
                        i.ToString(),
                        $"{frames[i].Width}x{frames[i].Height}",
                        $"({frames[i].XOffset},{frames[i].YOffset})");
                }

                AnsiConsole.MarkupLine("[bold cyan]{0}[/] — {1} frame(s)", Markup.Escape(logicalName), frames.Count);
                AnsiConsole.Write(table);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
                return Task.FromResult(1);
            }

            return Task.FromResult(0);
        });
        return command;
    }
}
