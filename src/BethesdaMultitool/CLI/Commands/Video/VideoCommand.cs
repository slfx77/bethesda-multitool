using System.CommandLine;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Xngine.Flic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Video;

/// <summary>
///     <c>video</c> command group — decodes the classic games' animation containers. Autodesk
///     FLIC (<c>.FLC</c> / <c>.CEL</c>) today, covering the XnGine-era cutscenes; Daggerfall
///     <c>.VID</c> and the Fallout <c>.MVE</c> identification path join per game vertical.
/// </summary>
public static class VideoCommand
{
    public static Command Create()
    {
        var command = new Command("video", "Decode classic-game animations (FLIC cutscenes)");
        command.Subcommands.Add(CreateInfoCommand());
        command.Subcommands.Add(CreateExportCommand());
        return command;
    }

    private static Command CreateInfoCommand()
    {
        var command = new Command("info", "Show an animation's geometry, frame count and duration");
        var inputArg = new Argument<string>("input") { Description = "A .FLC or .CEL file" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the animation inside an archive input"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) => Guarded(() => RunInfo(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption))));
        return command;
    }

    private static Command CreateExportCommand()
    {
        var command = new Command("export", "Render an animation's frames to PNG");
        var inputArg = new Argument<string>("input") { Description = "A .FLC or .CEL file" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the animation inside an archive input"
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory for PNG frames",
            DefaultValueFactory = _ => "TestOutput/classic-video"
        };
        var everyOption = new Option<int>("--every")
        {
            Description = "Write only every Nth frame (default 1 = all)",
            DefaultValueFactory = _ => 1
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.Options.Add(everyOption);
        command.SetAction((parseResult, _) => Guarded(() => RunExport(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption),
            parseResult.GetValue(outputOption)!,
            parseResult.GetValue(everyOption))));
        return command;
    }

    private static Task<int> Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException
                                       or InvalidOperationException)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] {0}", Markup.Escape(ex.Message));
            return Task.FromResult(1);
        }

        return Task.FromResult(0);
    }

    private static (byte[] Bytes, string Name) Load(string input, string? entryName)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        if (entryName is null)
        {
            return (File.ReadAllBytes(input), Path.GetFileName(input));
        }

        using var archive = ArchiveReader.Open(input);
        var bytes = archive.ReadFile(entryName)
                    ?? throw new FileNotFoundException(
                        $"Entry '{entryName}' not found in {Path.GetFileName(input)} " +
                        $"({archive.FormatName}, {archive.TotalFiles} files).");
        return (bytes, Path.GetFileName(entryName.Replace('/', '\\')));
    }

    private static void RunInfo(string input, string? entryName)
    {
        var (bytes, name) = Load(input, entryName);
        var flic = FlicFile.Parse(bytes, name);

        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(flic.Name));

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Size", $"{flic.Width}x{flic.Height}");
        table.AddRow("Frames", flic.Frames.Count.ToString());
        table.AddRow("Frame time", $"{flic.SecondsPerFrame * 1000:F0} ms");
        table.AddRow("Duration", $"{flic.DurationSeconds:F2} s");
        table.AddRow("Frame rate", $"{(flic.SecondsPerFrame > 0 ? 1 / flic.SecondsPerFrame : 0):F1} fps");

        // A palette change mid-animation is how these cutscenes fade and flash.
        var paletteChanges = 0;
        for (var i = 1; i < flic.Frames.Count; i++)
        {
            if (!ReferenceEquals(flic.Frames[i].Palette, flic.Frames[i - 1].Palette))
            {
                paletteChanges++;
            }
        }

        table.AddRow("Palette switches", paletteChanges.ToString());

        AnsiConsole.Write(table);
    }

    private static void RunExport(string input, string? entryName, string outputDir, int every)
    {
        every = Math.Max(1, every);
        var (bytes, name) = Load(input, entryName);
        var flic = FlicFile.Parse(bytes, name);

        Directory.CreateDirectory(outputDir);
        var baseName = Path.GetFileNameWithoutExtension(flic.Name);
        var written = 0;

        for (var i = 0; i < flic.Frames.Count; i++)
        {
            if (i % every != 0)
            {
                continue;
            }

            var frame = flic.Frames[i];
            var texture = frame.Image.ToDecodedTexture(frame.Palette);
            var path = Path.Combine(outputDir, $"{baseName}_f{i:D3}.png");
            PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height, path);
            written++;
        }

        AnsiConsole.MarkupLine(
            "[green]Wrote {0} of {1} frame(s)[/] to {2}  [grey]{3}x{4}, {5:F2}s total[/]",
            written,
            flic.Frames.Count,
            Markup.Escape(outputDir),
            flic.Width,
            flic.Height,
            flic.DurationSeconds);
    }
}
