using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Audio;
using BethesdaMultitool.Core.Formats.Bink;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Interplay;
using BethesdaMultitool.Core.Formats.Smacker;
using BethesdaMultitool.Core.Formats.Xngine.Flic;
using BethesdaMultitool.Core.Imaging;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Video;

/// <summary>
///     <c>video</c> command group — decodes the classic games' animation containers: Autodesk FLIC
///     (<c>.FLC</c> / <c>.CEL</c>) for the Arena-era cutscenes and Daggerfall's <c>.VID</c> movies,
///     whose interleaved audio exports beside the frames. The Fallout <c>.MVE</c> identification
///     path joins with that vertical.
/// </summary>
public static class VideoCommand
{
    public static Command Create()
    {
        var command = new Command("video", "Decode classic-game animations (FLIC, Daggerfall VID, Bink, Smacker, Interplay MVE)");
        command.Subcommands.Add(CreateInfoCommand());
        command.Subcommands.Add(CreateExportCommand());
        return command;
    }

    private static Command CreateInfoCommand()
    {
        var command = new Command("info", "Show an animation's geometry, frame count and duration");
        var inputArg = new Argument<string>("input") { Description = "A .FLC, .CEL, .VID, .BIK, .SMK or .MVE file (every format is detected by content, so Van Buren's Bink-under-.mve and Fallout's Interplay MVE both work)" };
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
        var inputArg = new Argument<string>("input") { Description = "A .FLC, .CEL, .VID, .BIK, .SMK or .MVE file (every format is detected by content, so Van Buren's Bink-under-.mve and Fallout's Interplay MVE both work)" };
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
            Description = "Write only every Nth frame in the range (default 1 = all)",
            DefaultValueFactory = _ => 1
        };
        var startOption = new Option<int>("--start")
        {
            Description = "First frame to export (default 0)",
            DefaultValueFactory = _ => 0
        };
        var endOption = new Option<int>("--end")
        {
            Description = "Last frame to export, inclusive (default -1 = to the end)",
            DefaultValueFactory = _ => -1
        };
        command.Arguments.Add(inputArg);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.Options.Add(everyOption);
        command.Options.Add(startOption);
        command.Options.Add(endOption);
        command.SetAction((parseResult, _) => Guarded(() => RunExport(
            parseResult.GetValue(inputArg)!,
            parseResult.GetValue(entryOption),
            parseResult.GetValue(outputOption)!,
            parseResult.GetValue(everyOption),
            parseResult.GetValue(startOption),
            parseResult.GetValue(endOption))));
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
        if (BinkFile.IsBink(bytes))
        {
            ShowBink(BinkFile.Parse(bytes, name));
            return;
        }

        if (SmackerFile.IsSmacker(bytes))
        {
            ShowSmacker(SmackerFile.Parse(bytes, name));
            return;
        }

        if (InterplayMveFile.IsMveFile(bytes))
        {
            ShowMve(new InterplayMveDecoder(bytes, name));
            return;
        }

        if (DaggerfallVidFile.IsVid(bytes))
        {
            ShowVid(DaggerfallVidFile.Parse(bytes, name));
            return;
        }

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

    private static void ShowBink(BinkFile bink)
    {
        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(bink.Name));

        var keyFrames = 0;
        for (var i = 0; i < bink.FrameCount; i++)
        {
            if (bink.IsKeyFrame(i))
            {
                keyFrames++;
            }
        }

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Container", $"{bink.Magic} (flags 0x{bink.Flags:X8})");
        table.AddRow("Size", $"{bink.Width}x{bink.Height}");
        table.AddRow("Frames", $"{bink.FrameCount:N0} ({keyFrames:N0} keyframe(s))");
        table.AddRow("Frame rate",
            $"{bink.FramesPerSecond:F3} fps ({bink.FrameRateDividend}/{bink.FrameRateDivisor})");
        table.AddRow("Duration", $"{bink.DurationSeconds:F2} s");
        table.AddRow("Largest frame", $"{bink.LargestFrameSize:N0} bytes");
        table.AddRow("Video decode", bink.IsDecodableVideo ? "supported" : "not implemented for this variant");

        if (bink.AudioTracks.Count == 0)
        {
            table.AddRow("Audio", "none");
        }
        else
        {
            // Audio DECODING is out of scope: the tracks are described and their packets skipped.
            for (var t = 0; t < bink.AudioTracks.Count; t++)
            {
                var track = bink.AudioTracks[t];
                table.AddRow($"Audio track {t}",
                    $"{track.SampleRate} Hz, {(track.Channels == 2 ? "stereo" : "mono")}, " +
                    $"{track.BitsPerSample}-bit, id {track.Id} (descriptor 0x{track.Descriptor:X8}) — not decoded");
            }
        }

        AnsiConsole.Write(table);
    }

    private static void ExportBink(
        BinkFile bink, string outputDir, int every, int start, int end)
    {
        if (!bink.IsDecodableVideo)
        {
            AnsiConsole.MarkupLine(
                "[yellow]{0} is {1} with flags 0x{2:X8}; only BIKi video without the alpha or " +
                "grayscale flags is implemented.[/]",
                Markup.Escape(bink.Name), bink.Magic, bink.Flags);
            return;
        }

        Directory.CreateDirectory(outputDir);
        var clip = BinkVideoClip.Open(bink);
        var baseName = Path.GetFileNameWithoutExtension(bink.Name);
        var written = 0;
        var last = end < 0 ? clip.FrameCount - 1 : Math.Min(end, clip.FrameCount - 1);

        // ⚠ Decoding is sequential — every frame is coded against its predecessor, and all 59
        // retail movies carry exactly ONE keyframe (frame 0) — so reaching frame N costs N decodes
        // however few frames are written. What --end buys is the tail: the loop STOPS there rather
        // than running to the end of the movie, which is the difference between
        // `--start 100 --end 100` costing 101 decodes and costing 8,864.
        for (var i = start; i <= last; i++)
        {
            var texture = clip.GetFrame(i);
            if ((i - start) % every != 0)
            {
                continue;
            }

            PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height,
                Path.Combine(outputDir, $"{baseName}_f{i:D5}.png"));
            written++;
        }

        AnsiConsole.MarkupLine(
            "[green]Wrote {0} of {1} frame(s)[/] to {2}  [grey]{3}x{4}, {5:F2}s total, decoded 0-{6}[/]",
            written, clip.FrameCount, Markup.Escape(outputDir), clip.Width, clip.Height,
            bink.DurationSeconds, Math.Max(last, 0));
    }

    private static void ShowSmacker(SmackerFile smk)
    {
        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(smk.Name));

        var keyFrames = 0;
        var paletteChunks = 0;
        for (var i = 0; i < smk.FrameCount; i++)
        {
            if (smk.IsKeyFrame(i))
            {
                keyFrames++;
            }

            if (smk.HasPalette(i))
            {
                paletteChunks++;
            }
        }

        var layout = (smk.IsYDoubled, smk.IsYInterlaced) switch
        {
            (true, _) => "Y-doubled",
            (_, true) => "Y-interlaced",
            _ => "as stored"
        };
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Container", $"{smk.Magic} (flags 0x{smk.Flags:X8}{(smk.HasRingFrame ? ", ring frame" : string.Empty)})");
        table.AddRow("Stored size", $"{smk.Width}x{smk.Height}");
        table.AddRow("Displayed size", $"{smk.Width}x{smk.DisplayHeight} ({layout})");
        table.AddRow("Frames", $"{smk.FrameCount:N0} ({keyFrames:N0} keyframe flag(s), {paletteChunks:N0} palette chunk(s))");
        table.AddRow("Frame rate", $"{smk.FramesPerSecond:F3} fps (field {smk.FrameRateField})");
        table.AddRow("Duration", $"{smk.DurationSeconds:F2} s");
        table.AddRow("Largest frame", $"{smk.RawFrameSizes.Max(s => s & ~3u):N0} bytes");
        table.AddRow("Tree data", $"{smk.TreesSize:N0} bytes ({smk.TreeBitsUsed:N0} bits used)");

        var anyAudio = false;
        for (var t = 0; t < SmackerFile.TrackCount; t++)
        {
            var track = smk.AudioTracks[t];
            if (!track.IsPresent)
            {
                continue;
            }

            anyAudio = true;
            var chunks = 0;
            for (var i = 0; i < smk.FrameCount; i++)
            {
                if (smk.HasAudio(i, t))
                {
                    chunks++;
                }
            }

            table.AddRow($"Audio track {t}",
                $"{track.SampleRate} Hz, {(track.IsStereo ? "stereo" : "mono")}, {track.BitsPerSample}-bit, " +
                $"{(track.IsCompressed ? "compressed" : "raw PCM")}, {chunks:N0} chunk(s), " +
                $"largest {track.LargestUnpackedChunk:N0} bytes unpacked (descriptor 0x{track.Descriptor:X8})");
        }

        if (!anyAudio)
        {
            table.AddRow("Audio", "none");
        }

        AnsiConsole.Write(table);
    }

    private static void ExportSmacker(
        SmackerFile smk, string outputDir, int every, int start, int end)
    {
        Directory.CreateDirectory(outputDir);
        var decoder = new SmackerVideoDecoder(smk);
        var baseName = Path.GetFileNameWithoutExtension(smk.Name);
        var written = 0;
        var last = end < 0 ? smk.FrameCount - 1 : Math.Min(end, smk.FrameCount - 1);

        // Sequential like Bink, and for the same reason: every frame is coded against the canvas
        // the frames before it left behind (void runs keep pixels, a missing palette chunk keeps
        // the palette), and no retail frame carries the keyframe flag. The PNGs are the STORED
        // frames — 640x240 for a Y-doubled movie — which is what the ffmpeg oracle matched.
        for (var i = start; i <= last; i++)
        {
            decoder.DecodeFrame(i);
            if ((i - start) % every != 0)
            {
                continue;
            }

            var bitmap = new IndexedBitmap(smk.Width, smk.Height, decoder.GetFrameIndices());
            var texture = bitmap.ToDecodedTexture(Palette.FromRgb8(decoder.PaletteRgb));
            PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height,
                Path.Combine(outputDir, $"{baseName}_f{i:D5}.png"));
            written++;
        }

        var wrote = new List<string>();
        for (var t = 0; t < SmackerFile.TrackCount; t++)
        {
            if (!smk.AudioTracks[t].IsPresent)
            {
                continue;
            }

            var pcm = SmackerAudioDecoder.DecodeTrack(smk, t);
            if (pcm.Pcm.Length == 0)
            {
                continue;
            }

            var audioPath = Path.Combine(outputDir, t == 0 ? baseName + ".wav" : $"{baseName}_track{t}.wav");
            File.WriteAllBytes(audioPath, WavWriter.BuildPcm(pcm.Pcm, pcm.SampleRate, pcm.BitsPerSample, pcm.Channels));
            wrote.Add(Markup.Escape($"{Path.GetFileName(audioPath)} ({pcm.Seconds:F2}s, {pcm.SampleRate} Hz {(pcm.Channels == 2 ? "stereo" : "mono")} {pcm.BitsPerSample}-bit)"));
        }

        AnsiConsole.MarkupLine(
            "[green]Wrote {0} of {1} frame(s)[/] to {2}  [grey]stored {3}x{4}, displayed {3}x{5}, {6:F2}s total, decoded 0-{7}[/]{8}",
            written, smk.FrameCount, Markup.Escape(outputDir), smk.Width, smk.Height, smk.DisplayHeight,
            smk.DurationSeconds, Math.Max(last, 0),
            wrote.Count > 0 ? $" [grey]+ {string.Join(", ", wrote)}[/]" : string.Empty);
    }

    private static void ShowMve(InterplayMveDecoder mve)
    {
        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(mve.Name));

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Format", "Interplay MVE (8-bit, block-coded)");
        table.AddRow("Size", $"{mve.Width}x{mve.Height}");
        table.AddRow("Decoded frames", mve.DecodedFrameCount.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Display ticks", mve.DisplayTicks.Count.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Tick", $"{mve.SecondsPerFrame * 1000:F3} ms");
        table.AddRow("Frame rate", $"{(mve.SecondsPerFrame > 0 ? 1 / mve.SecondsPerFrame : 0):F3} fps");
        table.AddRow("Duration", $"{mve.DisplayTicks.Count * mve.SecondsPerFrame:F2} s");
        table.AddRow("Chunks", mve.File.Chunks.Count.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Palette opcodes", mve.File.PaletteChunks.ToString(CultureInfo.InvariantCulture));
        if (mve.Audio is { } audio)
        {
            table.AddRow("Audio",
                $"{audio.SampleRate} Hz, {audio.Channels} ch, {audio.BitsPerSample}-bit, " +
                (audio.Compressed ? "DPCM" : "PCM"));
        }
        else
        {
            table.AddRow("Audio", "none");
        }

        AnsiConsole.Write(table);
    }

    private static void ExportMve(
        InterplayMveDecoder mve, string outputDir, int every, int start, int end)
    {
        Directory.CreateDirectory(outputDir);
        var baseName = Path.GetFileNameWithoutExtension(mve.Name);
        var written = 0;
        var last = end < 0 ? mve.DecodedFrameCount - 1 : Math.Min(end, mve.DecodedFrameCount - 1);

        // Frames here are DECODES (video-data opcodes), numbered as ffmpeg numbers them, so a
        // frame can be checked against the oracle by index; display ticks repeat frames and are
        // what the GUI clip steps through. Decoding is sequential with no keyframes, so --end
        // bounds the work exactly as it does for Bink.
        for (var i = start; i <= last; i++)
        {
            mve.SeekToFrame(i);
            if ((i - start) % every != 0)
            {
                continue;
            }

            var rgba = mve.CurrentFrameRgba();
            PngWriter.SaveRgba(rgba, mve.Width, mve.Height,
                Path.Combine(outputDir, $"{baseName}_f{i:D5}.png"));
            written++;
        }

        var wrote = new List<string>();
        if (mve.Audio is { } audio)
        {
            var pcm = mve.DecodeAudioPcm();
            if (pcm.Length > 0)
            {
                var audioPath = Path.Combine(outputDir, baseName + ".wav");
                File.WriteAllBytes(audioPath,
                    WavWriter.BuildPcm(pcm, audio.SampleRate, audio.BitsPerSample, audio.Channels));
                wrote.Add(Markup.Escape(Path.GetFileName(audioPath)));
            }
        }

        AnsiConsole.MarkupLine(
            "[green]Wrote {0} of {1} frame(s)[/] to {2}  [grey]{3}x{4}, {5} display ticks, {6:F2}s, decoded 0-{7}[/]{8}",
            written, mve.DecodedFrameCount, Markup.Escape(outputDir), mve.Width, mve.Height,
            mve.DisplayTicks.Count, mve.DisplayTicks.Count * mve.SecondsPerFrame, Math.Max(last, 0),
            wrote.Count > 0 ? $" [grey]+ {string.Join(", ", wrote)}[/]" : string.Empty);
    }

    private static void ShowVid(DaggerfallVidFile vid)
    {
        AnsiConsole.MarkupLine("[bold cyan]{0}[/]", Markup.Escape(vid.Name));

        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.AddRow("Size", $"{vid.Width}x{vid.Height}");
        table.AddRow("Frames", $"{vid.FrameCount} (header declares {vid.DeclaredFrameCount})");
        table.AddRow("Header delay", vid.GlobalDelay.ToString(CultureInfo.InvariantCulture));
        table.AddRow("Audio",
            $"{vid.Audio.Length:N0} samples, {vid.AudioSeconds:F2} s at {DaggerfallVidFile.SampleRate} Hz");
        table.AddRow("Frame rate",
            $"{(vid.AudioSeconds > 0 ? vid.FrameCount / vid.AudioSeconds : 0):F1} fps (from the audio track)");
        table.AddRow("Ends cleanly", vid.EndOfFileSeen ? "yes" : "no");
        table.AddRow("Blocks",
            string.Join(", ", vid.BlockCounts.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}")));
        AnsiConsole.Write(table);
    }

    private static void ExportVid(
        DaggerfallVidFile vid, string outputDir, int every, int start, int end)
    {
        Directory.CreateDirectory(outputDir);
        var baseName = Path.GetFileNameWithoutExtension(vid.Name);
        var written = 0;
        foreach (var frame in vid.EnumerateFrames())
        {
            // A VID paints onto ONE persistent canvas, so frames before the range still have to be
            // replayed to build it; only the PNG write is skipped.
            if (frame.Index < start || (end >= 0 && frame.Index > end)
                                    || (frame.Index - start) % every != 0)
            {
                continue;
            }

            var texture = frame.Bitmap.ToDecodedTexture(frame.Palette);
            PngWriter.SaveRgba(texture.Pixels, texture.Width, texture.Height,
                Path.Combine(outputDir, $"{baseName}_f{frame.Index:D4}.png"));
            written++;
        }

        var wrote = new List<string>();
        if (vid.Audio.Length > 0)
        {
            var audioPath = Path.Combine(outputDir, baseName + ".wav");
            File.WriteAllBytes(audioPath, WavWriter.BuildPcm(vid.Audio.Span, DaggerfallVidFile.SampleRate, 8, 1));
            wrote.Add(Markup.Escape(Path.GetFileName(audioPath)));
        }

        AnsiConsole.MarkupLine(
            "[green]Wrote {0} of {1} frame(s)[/] to {2}  [grey]{3}x{4}, {5:F2}s audio[/]{6}",
            written, vid.FrameCount, Markup.Escape(outputDir), vid.Width, vid.Height, vid.AudioSeconds,
            wrote.Count > 0 ? $" [grey]+ {string.Join(", ", wrote)}[/]" : string.Empty);
    }

    private static void RunExport(
        string input, string? entryName, string outputDir, int every, int start, int end)
    {
        every = Math.Max(1, every);
        start = Math.Max(0, start);
        var (bytes, name) = Load(input, entryName);
        if (BinkFile.IsBink(bytes))
        {
            ExportBink(BinkFile.Parse(bytes, name), outputDir, every, start, end);
            return;
        }

        if (SmackerFile.IsSmacker(bytes))
        {
            ExportSmacker(SmackerFile.Parse(bytes, name), outputDir, every, start, end);
            return;
        }

        if (InterplayMveFile.IsMveFile(bytes))
        {
            ExportMve(new InterplayMveDecoder(bytes, name), outputDir, every, start, end);
            return;
        }

        if (DaggerfallVidFile.IsVid(bytes))
        {
            ExportVid(DaggerfallVidFile.Parse(bytes, name), outputDir, every, start, end);
            return;
        }

        var flic = FlicFile.Parse(bytes, name);

        Directory.CreateDirectory(outputDir);
        var baseName = Path.GetFileNameWithoutExtension(flic.Name);
        var written = 0;

        var flicLast = end < 0 ? flic.Frames.Count - 1 : Math.Min(end, flic.Frames.Count - 1);
        for (var i = start; i <= flicLast; i++)
        {
            if ((i - start) % every != 0)
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
