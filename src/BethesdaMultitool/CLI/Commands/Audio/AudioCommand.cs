using System.CommandLine;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Redguard;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Audio;

/// <summary>
///     <c>audio</c> command group — decodes the classic games' audio containers to standard files.
///     Creative Voice (<c>.VOC</c>), Daggerfall's <c>DAGGER.SND</c> and Redguard's <c>MAIN.SFX</c>
///     to WAV today, plus HMI music-container inspection; the other DOS-era containers (ACM, XMI)
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
            Description = "A .VOC file, DAGGER.SND, MAIN.SFX, ENGLISH.RTX, or an archive when --entry or --all is given"
        };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of one entry inside the archive input (a sound id for DAGGER.SND, a 0-based index for MAIN.SFX, a 4-char tag for ENGLISH.RTX)"
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
        var inputArg = new Argument<string>("input") { Description = "A .VOC file, DAGGER.SND, MAIN.SFX, ENGLISH.RTX, or an archive with --entry" };
        var entryOption = new Option<string?>("--entry", "-e")
        {
            Description = "Virtual path of the entry inside the archive input (a sound id for DAGGER.SND, a 0-based index for MAIN.SFX)"
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

    /// <summary>True for Redguard's effect bank — by the <c>.SFX</c> extension, then confirmed by its tag.</summary>
    private static bool IsRedguardSfxBank(string input)
    {
        if (!Path.GetExtension(input).Equals(".sfx", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var stream = File.OpenRead(input);
        Span<byte> head = stackalloc byte[RedguardSfxFile.ChunkHeaderLength];
        return stream.Read(head) == head.Length && RedguardSfxFile.IsSfxFile(head);
    }

    /// <summary>True for Redguard's text database, whose voiced lines carry the same PCM records as the effect bank.</summary>
    private static bool IsRedguardRtx(string input)
    {
        return Path.GetExtension(input).Equals(".rtx", StringComparison.OrdinalIgnoreCase) && RedguardRtxFile.IsRtxFile(input);
    }

    /// <summary>A tag as a file-name stem: retail tags use '#', '?' and '$', which Windows will not take.</summary>
    private static string SafeTag(RedguardRtxEntry entry)
    {
        var chars = entry.Tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c).ToArray();
        return $"rtx_{entry.Index:D4}_{new string(chars)}";
    }

    /// <summary>Resolves a <c>--entry</c> value, which is a 0-based index, for a bank that stores no names.</summary>
    private static int ResolveSfxIndex(RedguardSfxFile bank, string entryName)
    {
        if (!int.TryParse(entryName, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            || index >= bank.Sounds.Count)
        {
            throw new InvalidOperationException(
                $"'{entryName}' is not a sound index; {bank.Name} holds {bank.Sounds.Count} unnamed sounds, 0-{bank.Sounds.Count - 1}.");
        }

        return index;
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

        if (IsRedguardSfxBank(input))
        {
            DecodeRedguardSfx(input, entryName, all, outputDir);
            return;
        }

        if (IsRedguardRtx(input))
        {
            DecodeRedguardRtx(input, entryName, all, outputDir);
            return;
        }

        if (!all && entryName is null)
        {
            var bytes = File.ReadAllBytes(input);
            if (XmidiFile.IsXmidi(bytes))
            {
                WriteMidi(XmidiFile.Parse(bytes, Path.GetFileName(input)), outputDir);
                return;
            }

            if (RiffWaveFile.IsRiffWave(bytes))
            {
                WriteRiff(RiffWaveFile.Parse(bytes, Path.GetFileName(input)), outputDir);
                return;
            }

            WriteWav(VocFile.Parse(bytes, Path.GetFileName(input)), outputDir);
            return;
        }

        using var archive = ArchiveReader.Open(input);
        if (entryName is not null)
        {
            var bytes = archive.ReadFile(entryName)
                        ?? throw new FileNotFoundException(
                            $"Entry '{entryName}' not found in {Path.GetFileName(input)} " +
                            $"({archive.FormatName}, {archive.TotalFiles} files).");
            var logicalName = Path.GetFileName(entryName.Replace('/', '\\'));
            if (XmidiFile.IsXmidi(bytes))
            {
                WriteMidi(XmidiFile.Parse(bytes, logicalName), outputDir);
                return;
            }

            if (RiffWaveFile.IsRiffWave(bytes))
            {
                WriteRiff(RiffWaveFile.Parse(bytes, logicalName), outputDir);
                return;
            }

            WriteWav(VocFile.Parse(bytes, logicalName), outputDir);
            return;
        }

        var written = 0;
        var skipped = 0;
        foreach (var entry in archive.ListFiles())
        {
            var isVoc = entry.Name.EndsWith(".VOC", StringComparison.OrdinalIgnoreCase);
            var isXmidi = XmidiFile.IsXmidiFileName(entry.Name);

            // Battlespire's SPIRE.SND names every entry by NUMBER, so extension routing cannot see
            // its 370 RIFF/WAVE sounds — content has to decide.
            var bytes = archive.ReadFile(entry.FullPath);
            if (bytes is null)
            {
                if (isVoc || isXmidi)
                {
                    skipped++;
                }

                continue;
            }

            var isRiff = RiffWaveFile.IsRiffWave(bytes);
            if (!isVoc && !isXmidi && !isRiff)
            {
                continue;
            }

            try
            {
                if (isVoc)
                {
                    WriteWav(VocFile.Parse(bytes, entry.Name), outputDir, quiet: true);
                }
                else if (isRiff)
                {
                    WriteRiff(RiffWaveFile.Parse(bytes, entry.Name), outputDir, quiet: true);
                }
                else
                {
                    WriteMidi(XmidiFile.Parse(bytes, entry.Name), outputDir, quiet: true);
                }

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

    /// <summary>
    ///     Decodes Redguard's <c>MAIN.SFX</c>: <c>--entry</c> is a 0-based index (the bank stores no
    ///     names), <c>--all</c> writes every sound. Samples are mono at each record's own depth
    ///     (8-bit unsigned or 16-bit signed — WAV's conventions) and rate, and go in verbatim.
    /// </summary>
    private static void DecodeRedguardSfx(string input, string? entryName, bool all, string outputDir)
    {
        var bank = RedguardSfxFile.Parse(File.ReadAllBytes(input), Path.GetFileName(input));
        if (!all && entryName is null)
        {
            throw new InvalidOperationException(
                $"{bank.Name} holds {bank.Sounds.Count} sounds — pass --entry <index> or --all.");
        }

        var indices = entryName is null
            ? Enumerable.Range(0, bank.Sounds.Count).ToList()
            : [ResolveSfxIndex(bank, entryName)];

        foreach (var index in indices)
        {
            var sound = bank.Sounds[index];
            var path = Path.Combine(outputDir, $"sfx_{index:D3}.wav");
            WavWriter.SavePcm(sound.Samples.Span, sound.SampleRate, sound.BitsPerSample, 1, path);

            if (indices.Count == 1)
            {
                AnsiConsole.MarkupLine("[green]Wrote[/] {0}  [grey]{1} Hz, {2}, mono, {3:F2}s[/]",
                    Markup.Escape(path), sound.SampleRate, DescribeDepth(sound), sound.DurationSeconds);
            }
        }

        if (indices.Count > 1)
        {
            AnsiConsole.MarkupLine("[green]Decoded {0} sound(s)[/] to {1}", indices.Count, Markup.Escape(outputDir));
        }
    }

    /// <summary>
    ///     Decodes the voice acting in <c>ENGLISH.RTX</c>: <c>--entry</c> is a 4-character tag,
    ///     <c>--all</c> writes every voiced line. Files are <c>rtx_NNNN_tag.wav</c> — the index keeps
    ///     names unique and readable after the tag's punctuation is replaced.
    /// </summary>
    private static void DecodeRedguardRtx(string input, string? entryName, bool all, string outputDir)
    {
        using var database = RedguardRtxFile.Open(input);
        if (!all && entryName is null)
        {
            throw new InvalidOperationException(
                $"{database.Name} holds {database.Entries.Count(e => e.IsVoiced)} voiced lines — pass --entry <tag> or --all.");
        }

        List<RedguardRtxEntry> entries;
        if (entryName is null)
        {
            entries = database.Entries.Where(e => e.IsVoiced).ToList();
        }
        else
        {
            var entry = database.Find(entryName)
                        ?? throw new InvalidOperationException($"No record tagged '{entryName}' in {database.Name}.");
            if (!entry.IsVoiced)
            {
                throw new InvalidOperationException($"'{entryName}' is text only: \"{entry.Text}\".");
            }

            entries = [entry];
        }

        foreach (var entry in entries)
        {
            var sound = entry.Sound!.Value;
            var path = Path.Combine(outputDir, SafeTag(entry) + ".wav");
            WavWriter.SavePcm(database.ReadSamples(entry), sound.SampleRate, sound.BitsPerSample, 1, path);
            if (entries.Count == 1)
            {
                AnsiConsole.MarkupLine("[green]Wrote[/] {0}  [grey]{1} Hz, {2}, mono, {3:F2}s[/]  {4}",
                    Markup.Escape(path), sound.SampleRate, sound.DepthDescription, sound.DurationSeconds, Markup.Escape(entry.Text));
            }
        }

        if (entries.Count > 1)
        {
            AnsiConsole.MarkupLine("[green]Decoded {0} voiced line(s)[/] to {1}", entries.Count, Markup.Escape(outputDir));
        }
    }

    /// <summary>
    ///     Writes a RIFF/WAVE sound through unchanged apart from a corrected declared length.
    ///     <para>
    ///         Entry names in a numbered archive are bare integers, so the extension is added here
    ///         rather than assumed to exist.
    ///     </para>
    /// </summary>
    private static void WriteRiff(RiffWaveFile wave, string outputDir, bool quiet = false)
    {
        var stem = Path.GetFileNameWithoutExtension(wave.Name);
        if (string.IsNullOrEmpty(stem))
        {
            stem = wave.Name;
        }

        var path = Path.Combine(outputDir, stem + ".wav");
        File.WriteAllBytes(path, wave.Riff);
        if (!quiet)
        {
            AnsiConsole.MarkupLine(
                "[green]Wrote[/] {0} [grey]({1} Hz, {2}-bit, {3} ch, {4:F2}s)[/]",
                Markup.Escape(path), wave.SampleRate, wave.BitsPerSample, wave.Channels, wave.DurationSeconds);
        }
    }

    /// <summary>
    ///     Writes one XMIDI sequence as a standard MIDI file.
    ///     <para>
    ///         The stem keeps the source extension's identity — <c>.XMI</c> and <c>.XFM</c> are the
    ///         General-MIDI and FM-synth arrangements of the SAME track, so writing both as
    ///         <c>NAME.mid</c> would have one silently overwrite the other.
    ///     </para>
    /// </summary>
    private static void WriteMidi(XmidiFile xmidi, string outputDir, bool quiet = false)
    {
        var stem = Path.GetFileNameWithoutExtension(xmidi.Name);
        var variant = Path.GetExtension(xmidi.Name).TrimStart('.').ToLowerInvariant();
        for (var i = 0; i < xmidi.Sequences.Count; i++)
        {
            var suffix = xmidi.Sequences.Count == 1 ? string.Empty : $"_{i:D2}";
            var path = Path.Combine(outputDir, $"{stem}_{variant}{suffix}.mid");
            File.WriteAllBytes(path, xmidi.ToStandardMidi(i));
            if (!quiet)
            {
                AnsiConsole.MarkupLine("[green]Wrote[/] {0}", Markup.Escape(path));
            }
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

        if (IsRedguardSfxBank(input))
        {
            ShowRedguardSfx(input, entryName);
            return;
        }

        if (IsRedguardRtx(input))
        {
            ShowRedguardRtx(input, entryName);
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

    private static string DescribeDepth(RedguardSfxSound sound)
    {
        return sound.BitsPerSample == 16 ? "16-bit signed" : "8-bit unsigned";
    }

    private static void ShowRedguardSfx(string input, string? entryName)
    {
        var bank = RedguardSfxFile.Parse(File.ReadAllBytes(input), Path.GetFileName(input));
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (entryName is not null)
        {
            var sound = bank.Sounds[ResolveSfxIndex(bank, entryName)];
            var single = new Table { Border = TableBorder.Rounded };
            single.AddColumn("Property");
            single.AddColumn("Value");
            single.AddRow("Index", sound.Index.ToString(culture));
            single.AddRow("Sample rate", $"{sound.SampleRate} Hz");
            single.AddRow("Sample bytes", sound.Samples.Length.ToString("N0", culture));
            single.AddRow("Duration", $"{sound.DurationSeconds:F3} s");
            single.AddRow("Volume", sound.Volume.ToString(culture));
            single.AddRow("Flags", sound.Flags.ToString(culture));
            single.AddRow("Format", $"{DescribeDepth(sound)}, mono (format word {sound.Format})");
            AnsiConsole.Write(single);
            return;
        }

        var rates = bank.Sounds.GroupBy(s => s.SampleRate).OrderBy(g => g.Key)
            .Select(g => $"{g.Key} Hz x{g.Count()}");
        var depths = bank.Sounds.GroupBy(s => s.BitsPerSample).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}-bit x{g.Count()}");
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Banner", Markup.Escape(bank.Banner));
        table.AddRow("Sounds", bank.Sounds.Count.ToString("N0", culture));
        table.AddRow("Sample rates", string.Join(", ", rates));
        table.AddRow("Total audio", $"{bank.Sounds.Sum(s => s.DurationSeconds):F1} s");
        table.AddRow("Total bytes", bank.Sounds.Sum(s => (long)s.Samples.Length).ToString("N0", culture));
        table.AddRow("Depths", string.Join(", ", depths));
        table.AddRow("Format", "mono; 8-bit unsigned or 16-bit signed per record");
        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(bank.Name));
        AnsiConsole.Write(table);
    }

    private static void ShowRedguardRtx(string input, string? entryName)
    {
        using var database = RedguardRtxFile.Open(input);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (entryName is not null)
        {
            var entry = database.Find(entryName)
                        ?? throw new InvalidOperationException($"No record tagged '{entryName}' in {database.Name}.");
            var single = new Table { Border = TableBorder.Rounded };
            single.AddColumn("Property");
            single.AddColumn("Value");
            single.AddRow("Tag", Markup.Escape(entry.Tag));
            single.AddRow("Index", entry.Index.ToString(culture));
            single.AddRow("Text", Markup.Escape(entry.Text));
            if (entry.Sound is { } sound)
            {
                single.AddRow("Sample rate", $"{sound.SampleRate} Hz");
                single.AddRow("Format", $"{sound.DepthDescription}, mono");
                single.AddRow("Sample bytes", sound.ByteLength.ToString("N0", culture));
                single.AddRow("Duration", $"{sound.DurationSeconds:F3} s");
            }
            else
            {
                single.AddRow("Voice", "(text only)");
            }

            AnsiConsole.Write(single);
            return;
        }

        var voiced = database.Entries.Where(e => e.IsVoiced).ToList();
        var rates = voiced.GroupBy(e => e.Sound!.Value.SampleRate).OrderBy(g => g.Key).Select(g => $"{g.Key} Hz x{g.Count()}");
        var depths = voiced.GroupBy(e => e.Sound!.Value.BitsPerSample).OrderBy(g => g.Key).Select(g => $"{g.Key}-bit x{g.Count()}");
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Records", database.Entries.Count.ToString("N0", culture));
        table.AddRow("Voiced", voiced.Count.ToString("N0", culture));
        table.AddRow("Text only", (database.Entries.Count - voiced.Count).ToString("N0", culture));
        table.AddRow("Sample rates", string.Join(", ", rates));
        table.AddRow("Depths", string.Join(", ", depths));
        table.AddRow("Total voice", $"{voiced.Sum(e => e.Sound!.Value.DurationSeconds) / 60:F1} min");
        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(database.Name));
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
