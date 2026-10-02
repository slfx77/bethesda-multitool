using System.CommandLine;
using System.Text.Json;
using BethesdaMultitool.Core.Media.Audio.Lip;

namespace BethesdaMultitool.CLI.Commands.Audio;

/// <summary>Inspects verified source LIP weights without retargeting or writing game files.</summary>
internal static class LipCommand
{
    /// <summary>Builds the standalone LIP inspection command.</summary>
    public static Command Create()
    {
        var root = new Command("lip", "Inspect Fallout 3/New Vegas facial animation samples");
        var inspect = new Command("inspect", "Decode a revision-one compressed little-endian LIP file");
        var input = new Argument<string>("input") { Description = "Complete .lip file path" };
        var json = new Option<bool>("--json") { Description = "Write machine-readable JSON to stdout" };
        var samples = new Option<bool>("--samples") { Description = "Include every decoded sample in JSON output (implies --json)" };
        inspect.Arguments.Add(input);
        inspect.Options.Add(json);
        inspect.Options.Add(samples);
        inspect.SetAction(async (result, cancellationToken) =>
        {
            try
            {
                var timeline = await LipDecoder.ReadFileAsync(result.GetValue(input)!, cancellationToken).ConfigureAwait(false);
                if (result.GetValue(json) || result.GetValue(samples))
                {
                    using var output = Console.OpenStandardOutput();
                    WriteJson(output, timeline, result.GetValue(samples), cancellationToken);
                }
                else
                {
                    Console.WriteLine($"LIP revision {timeline.Revision}, flags 0x{timeline.Flags:X}: {timeline.FrameCount} frames, {LipTimeline.Tracks.Count} tracks, {timeline.FramesPerSecond} Hz");
                    Console.WriteLine($"Starting frame: {timeline.StartingFrame}; encoded bytes: {timeline.EncodedSize}; declared size: {timeline.DeclaredSize}");
                    Console.WriteLine("Raw source weights; sample times are relative to the first sample. Actor morph application and audio alignment are not performed.");
                    foreach (var track in LipTimeline.Tracks) Console.WriteLine($"{track.Index}: {track.Group}/{track.Name}");
                }
                return 0;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"LIP inspection failed: {exception.Message}");
                return 1;
            }
        });
        root.Subcommands.Add(inspect);
        return root;
    }

    /// <summary>Streams JSON without reflection, retaining native track identities and unclamped values.</summary>
    internal static void WriteJson(Stream output, LipTimeline timeline, bool includeSamples, CancellationToken cancellationToken = default)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("revision", timeline.Revision);
        writer.WriteNumber("flags", timeline.Flags);
        writer.WriteNumber("encodedBytes", timeline.EncodedSize);
        writer.WriteNumber("declaredSize", timeline.DeclaredSize);
        writer.WriteNumber("frameCount", timeline.FrameCount);
        writer.WriteNumber("startingFrame", timeline.StartingFrame);
        writer.WriteNumber("framesPerSecond", timeline.FramesPerSecond);
        writer.WriteString("timeOrigin", "first decoded sample; audio alignment not applied");
        writer.WriteString("valueSpace", "raw source weights; engine settings and actor morph mapping not applied");
        writer.WriteStartArray("tracks");
        foreach (var track in LipTimeline.Tracks)
        {
            writer.WriteStartObject();
            writer.WriteNumber("index", track.Index);
            writer.WriteString("group", track.Group);
            writer.WriteString("name", track.Name);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (includeSamples)
        {
            writer.WriteStartArray("samples");
            for (var frame = 0; frame < timeline.FrameCount; frame++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject();
                writer.WriteNumber("frameIndex", frame);
                writer.WriteNumber("relativeTimeSeconds", timeline.GetRelativeTimeSeconds(frame));
                writer.WriteStartArray("values");
                for (var track = 0; track < LipTimeline.Tracks.Count; track++) writer.WriteNumberValue(timeline.GetValue(frame, track));
                writer.WriteEndArray();
                writer.WriteEndObject();
                if ((frame & 127) == 0) writer.Flush();
            }
            writer.WriteEndArray();
        }
        cancellationToken.ThrowIfCancellationRequested();
        writer.WriteEndObject();
        writer.Flush();
    }
}
