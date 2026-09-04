using System.CommandLine;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Audio;

/// <summary>
///     <c>audio</c> command group — decodes the classic games' audio containers to standard files.
///     Creative Voice (<c>.VOC</c>) and Daggerfall's <c>DAGGER.SND</c> to WAV today, plus HMI
///     music-container inspection; the other DOS-era containers (ACM, XMI) join as their game
///     verticals land.
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
            Description = "A .VOC file, DAGGER.SND, or an archive when --entry or --all is given"
        };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of one entry inside the archive input (a sound id for DAGGER.SND)"
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
        var inputArg = new Argument<string>("input") { Description = "A .VOC file, DAGGER.SND, or an archive with --entry" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the entry inside the archive input (a sound id for DAGGER.SND)"
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

    /// <summary>Resolves a <c>--entry</c> value, which names a sound id, to an archive index.</summary>
    private static int ResolveSoundIndex(DaggerfallSoundFile sounds, string entryName)
    {
        if (!uint.TryParse(entryName, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var soundId))
        {
            throw new InvalidOperationException($"'{entryName}' is not a sound id; {DaggerfallSoundFile.FileName} records are numbered.");
        }

        var index = sounds.IndexOf(soundId);
        return index >= 0 ? index : throw new InvalidOperationException($"No sound with id {soundId}.");
    }

    /// <summary>True for Daggerfall's sound archive, whose records are headerless PCM.</summary>
    private static bool IsDaggerfallSoundArchive(string input)
    {
        return Path.GetFileName(input).Equals(DaggerfallSoundFile.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static void RunDecode(string input, string? entryName, bool all, string outputDir)
    {
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"Input not found: {input}", input);
        }

        Directory.CreateDirectory(outputDir);

        if (IsDaggerfallSoundArchive(input))
        {
            DecodeDaggerfallSounds(input, entryName, all, outputDir);
            return;
        }

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

    /// <summary>
    ///     Decodes <c>DAGGER.SND</c>: <c>--entry</c> names one sound id, <c>--all</c> writes every
    ///     record. Files are named by id, with the archive index appended when an id repeats (retail
    ///     uses id 220 twice), so no record silently overwrites another.
    /// </summary>
    private static void DecodeDaggerfallSounds(string input, string? entryName, bool all, string outputDir)
    {
        var sounds = DaggerfallSoundFile.Open(input);
        if (!all && entryName is null)
        {
            throw new InvalidOperationException(
                $"{DaggerfallSoundFile.FileName} holds {sounds.Count} sounds — pass --entry <sound id> or --all.");
        }

        var indices = entryName is null
            ? [.. Enumerable.Range(0, sounds.Count)]
            : new List<int> { ResolveSoundIndex(sounds, entryName) };

        var duplicated = Enumerable.Range(0, sounds.Count)
            .GroupBy(sounds.SoundId)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .ToHashSet();

        var written = 0;
        var empty = 0;
        foreach (var index in indices)
        {
            if (sounds.Samples(index).Length == 0)
            {
                empty++;
                continue;
            }

            var id = sounds.SoundId(index);
            var name = duplicated.Contains(index)
                ? $"sound_{id}_{index}.wav"
                : $"sound_{id}.wav";
            var path = Path.Combine(outputDir, name);
            File.WriteAllBytes(path, sounds.ToWav(index));
            written++;

            if (indices.Count == 1)
            {
                AnsiConsole.MarkupLine("[green]Wrote[/] {0}  [grey]{1} Hz, {2}-bit, {3} channel(s), {4:F2}s[/]",
                    Markup.Escape(path), DaggerfallSoundFile.SampleRate, DaggerfallSoundFile.BitsPerSample,
                    DaggerfallSoundFile.Channels, sounds.DurationSeconds(index));
            }
        }

        if (indices.Count > 1)
        {
            AnsiConsole.MarkupLine("[green]Decoded {0} sound(s)[/] to {1}{2}",
                written, Markup.Escape(outputDir), empty > 0 ? $" ({empty} empty record(s) skipped)" : string.Empty);
        }
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

        if (IsDaggerfallSoundArchive(input))
        {
            ShowDaggerfallSounds(input, entryName);
            return;
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

        if (HmiFile.IsHmi(bytes))
        {
            ShowHmi(HmiFile.Parse(bytes, name));
            return;
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

    private static void ShowDaggerfallSounds(string input, string? entryName)
    {
        var sounds = DaggerfallSoundFile.Open(input);
        if (entryName is not null)
        {
            var index = ResolveSoundIndex(sounds, entryName);
            var single = new Table { Border = TableBorder.Rounded };
            single.AddColumn("Property");
            single.AddColumn("Value");
            single.AddRow("Sound id", sounds.SoundId(index).ToString(System.Globalization.CultureInfo.InvariantCulture));
            single.AddRow("Archive index", index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            single.AddRow("Samples", sounds.Samples(index).Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
            single.AddRow("Duration", $"{sounds.DurationSeconds(index):F3} s");
            single.AddRow("Format", $"{DaggerfallSoundFile.SampleRate} Hz, {DaggerfallSoundFile.BitsPerSample}-bit unsigned, mono");
            AnsiConsole.Write(single);
            return;
        }

        var ids = Enumerable.Range(0, sounds.Count).Select(sounds.SoundId).ToList();
        var totalSamples = Enumerable.Range(0, sounds.Count).Sum(i => (long)sounds.Samples(i).Length);
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Records", sounds.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("Distinct ids", ids.Distinct().Count().ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("Id range", $"{ids.Min()}-{ids.Max()}");
        table.AddRow("Empty records", Enumerable.Range(0, sounds.Count).Count(i => sounds.Samples(i).Length == 0).ToString(System.Globalization.CultureInfo.InvariantCulture));
        table.AddRow("Total audio", $"{totalSamples / (double)DaggerfallSoundFile.SampleRate:F1} s");
        table.AddRow("Format", $"{DaggerfallSoundFile.SampleRate} Hz, {DaggerfallSoundFile.BitsPerSample}-bit unsigned, mono");
        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(Path.GetFileName(input)));
        AnsiConsole.Write(table);
    }

    private static void ShowHmi(HmiFile song)
    {
        AnsiConsole.MarkupLine("[bold cyan]{0}[/] — [grey]{1}[/]", Markup.Escape(song.Name), Markup.Escape(song.Tag));
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Track", c => c.RightAligned());
        table.AddColumn("Offset", c => c.RightAligned());
        table.AddColumn("Bytes", c => c.RightAligned());
        for (var i = 0; i < song.Tracks.Count; i++)
        {
            table.AddRow(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                song.TrackOffsets[i].ToString("N0", System.Globalization.CultureInfo.InvariantCulture),
                song.Tracks[i].Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            "[grey]The container is read; the HMI event stream is not converted to a standard MIDI file " +
            "(no specification for its extensions is available here).[/]");
    }
}
