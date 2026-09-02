using System.CommandLine;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Audio;

/// <summary>
///     <c>audio</c> command group — decodes the classic games' audio containers to standard files.
///     Creative Voice (<c>.VOC</c>) to WAV today; the other DOS-era containers (ACM, SND, XMI)
///     join as their game verticals land.
/// </summary>
public static class AudioCommand
{
    public static Command Create()
    {
        var command = new Command("audio", "Decode classic-game audio to standard formats");
        command.Subcommands.Add(CreateDecodeCommand());
        command.Subcommands.Add(CreateInfoCommand());
        return command;
    }

    private static Command CreateDecodeCommand()
    {
        var command = new Command("decode", "Decode an audio file (or a whole archive) to WAV");
        var inputArg = new Argument<string>("input")
        {
            Description = "A .VOC file, or an archive when --entry or --all is given"
        };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of one entry inside the archive input"
        };
        var allOption = new Option<bool>("--all")
        {
            Description = "Decode every supported audio file in the archive input"
        };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output directory",
            DefaultValueFactory = _ => "TestOutput/classic-audio"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(allOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, _) => Guarded(() => RunDecode(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption),
            parseResult.GetValue(allOption),
            parseResult.GetValue(outputOption)!)));
        return command;
    }

    private static Command CreateInfoCommand()
    {
        var command = new Command("info", "Show sample rate, depth and duration without writing files");
        var inputArg = new Argument<string>("input") { Description = "A .VOC file, or an archive with --entry" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the entry inside the archive input"
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.SetAction((parseResult, _) => Guarded(() => RunInfo(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption))));
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

    private static void RunDecode(string input, string? entryName, bool all, string outputDir)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        Directory.CreateDirectory(outputDir);

        if (!all && entryName is null)
        {
            var voc = VocFile.Parse(File.ReadAllBytes(input), Path.GetFileName(input));
            WriteWav(voc, outputDir);
            return;
        }

        using var archive = ArchiveReader.Open(input);
        if (entryName is not null)
        {
            var bytes = archive.ReadFile(entryName)
                        ?? throw new FileNotFoundException(
                            $"Entry '{entryName}' not found in {Path.GetFileName(input)} " +
                            $"({archive.FormatName}, {archive.TotalFiles} files).");
            WriteWav(VocFile.Parse(bytes, Path.GetFileName(entryName.Replace('/', '\\'))), outputDir);
            return;
        }

        var written = 0;
        var skipped = 0;
        foreach (var entry in archive.ListFiles())
        {
            if (!entry.Name.EndsWith(".VOC", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null)
            {
                skipped++;
                continue;
            }

            try
            {
                WriteWav(VocFile.Parse(bytes, entry.Name), outputDir, quiet: true);
                written++;
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                AnsiConsole.MarkupLine("  [yellow]skipped[/] {0}: {1}",
                    Markup.Escape(entry.Name), Markup.Escape(ex.Message));
                skipped++;
            }
        }

        AnsiConsole.MarkupLine("[green]Decoded {0} file(s)[/] to {1}{2}",
            written,
            Markup.Escape(outputDir),
            skipped > 0 ? $" ({skipped} skipped)" : string.Empty);
    }

    private static void WriteWav(VocFile voc, string outputDir, bool quiet = false)
    {
        var path = Path.Combine(outputDir, Path.ChangeExtension(voc.Name, ".wav"));
        WavWriter.SavePcm(voc.Samples, voc.SampleRate, voc.BitsPerSample, voc.Channels, path);

        if (!quiet)
        {
            AnsiConsole.MarkupLine(
                "[green]Wrote[/] {0}  [grey]{1} Hz, {2}-bit, {3} channel(s), {4:F2}s[/]",
                Markup.Escape(path),
                voc.SampleRate,
                voc.BitsPerSample,
                voc.Channels,
                voc.DurationSeconds);
        }
    }

    private static void RunInfo(string input, string? entryName)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        byte[] bytes;
        string name;
        if (entryName is null)
        {
            bytes = File.ReadAllBytes(input);
            name = Path.GetFileName(input);
        }
        else
        {
            using var archive = ArchiveReader.Open(input);
            bytes = archive.ReadFile(entryName)
                    ?? throw new FileNotFoundException($"Entry '{entryName}' not found in {Path.GetFileName(input)}.");
            name = Path.GetFileName(entryName.Replace('/', '\\'));
        }

        var voc = VocFile.Parse(bytes, name);
        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(voc.Name));

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Sample rate", $"{voc.SampleRate} Hz");
        table.AddRow("Bit depth", $"{voc.BitsPerSample}-bit");
        table.AddRow("Channels", voc.Channels.ToString());
        table.AddRow("Frames", voc.FrameCount.ToString());
        table.AddRow("Duration", $"{voc.DurationSeconds:F3} s");
        if (voc.RepeatCount is { } repeat)
        {
            table.AddRow("Loops", repeat == 0xFFFF ? "forever" : repeat.ToString());
        }

        foreach (var text in voc.Texts)
        {
            table.AddRow("Text", Markup.Escape(text));
        }

        AnsiConsole.Write(table);
    }
}
