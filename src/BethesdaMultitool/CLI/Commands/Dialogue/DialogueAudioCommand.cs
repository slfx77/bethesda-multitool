using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Media.Audio.Dialogue;

namespace BethesdaMultitool.CLI.Commands.Dialogue;

/// <summary>Inspects original Fallout voice bindings and exports unambiguous audio through the asset service.</summary>
internal static class DialogueAudioCommand
{
    /// <summary>Creates explicit identity options without inferring a plugin from the opened archive name.</summary>
    internal static Command Create()
    {
        var command = new Command("audio", "Resolve Fallout 3/New Vegas voice audio by original plugin/INFO/response identity");
        var source = new Argument<string>("source") { Description = "Folder, game data folder, or archive" };
        var plugin = new Option<string>("--plugin") { Required = true, Description = "Original plugin filename, including extension" };
        var info = new Option<string>("--info") { Required = true, Description = "Original INFO FormID in hexadecimal" };
        var response = new Option<byte>("--response") { Required = true, Description = "Original TRDT response number" };
        var voice = new Option<string?>("--voice") { Description = "Exact voice-type EditorID; omitted leaves all voices visible" };
        var provenance = new Option<string?>("--provenance") { Description = "Exact source-layer identity from the catalog" };
        var json = new Option<bool>("--json") { Description = "Write invariant JSON" };
        var output = new Option<string?>("--output", "-o") { Description = "Extract a uniquely resolved audio entry to this directory" };
        var overwrite = new Option<bool>("--force") { Description = "Replace existing output files" };
        command.Arguments.Add(source);
        command.Options.Add(plugin);
        command.Options.Add(info);
        command.Options.Add(response);
        command.Options.Add(voice);
        command.Options.Add(provenance);
        command.Options.Add(json);
        command.Options.Add(output);
        command.Options.Add(overwrite);
        command.SetAction(async (result, token) =>
        {
            try
            {
                var text = result.GetValue(info)!;
                if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { text = text[2..]; }
                if (!uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var formId))
                { throw new ArgumentException("--info must be an unsigned hexadecimal FormID."); }
                await using var assets = BethesdaBrowseSource.Open(ExploreSourcePlanner.Create(result.GetValue(source)!, token));
                var filesystem = assets.Session.FileSystem;
                var index = DialogueAudioIndex.Create(filesystem.EnumerateFiles("sound/voice/"), token);
                var identity = new DialogueAudioIdentity(result.GetValue(plugin)!, formId, result.GetValue(response),
                    result.GetValue(voice), result.GetValue(provenance));
                var resolution = index.Resolve(identity);
                IReadOnlyList<AssetExportResult> exports = [];
                if (result.GetValue(output) is { } directory && resolution.State == DialogueAudioMatchState.Resolved)
                {
                    exports = await AssetExportService.ExportAsync(filesystem, [resolution.Candidates[0].Audio.Path],
                        directory, AssetExportMode.Original, result.GetValue(overwrite),
                        expectedProvenance: new Dictionary<string, string>
                        {
                            [resolution.Candidates[0].Audio.Path] = resolution.Candidates[0].Audio.Source
                        }, cancellationToken: token);
                }
                if (result.GetValue(json)) { WriteJson(resolution, exports); }
                else
                {
                    Console.WriteLine(resolution.State.ToString().ToLowerInvariant());
                    foreach (var candidate in resolution.Candidates)
                        Console.WriteLine($"{candidate.VoiceType}\t{candidate.Audio.Path}\t{candidate.Audio.Source}");
                    foreach (var export in exports)
                        Console.WriteLine(export.Success ? export.OutputPath : export.Error);
                }
                return resolution.State switch
                {
                    DialogueAudioMatchState.Missing => 3,
                    DialogueAudioMatchState.Ambiguous => 4,
                    _ => exports.All(item => item.Success) ? 0 : 1
                };
            }
            catch (OperationCanceledException) { return 130; }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { Console.Error.WriteLine(exception.Message); return 1; }
        });
        return command;
    }

    /// <summary>Writes stable machine fields without requiring reflection-based serialization.</summary>
    private static void WriteJson(DialogueAudioResolution result, IReadOnlyList<AssetExportResult> exports)
    {
        using var output = Console.OpenStandardOutput();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("state", result.State.ToString().ToLowerInvariant());
        writer.WriteString("plugin", result.Identity.Plugin);
        writer.WriteString("originalInfoFormId", result.Identity.OriginalInfoFormId.ToString("X8", CultureInfo.InvariantCulture));
        writer.WriteNumber("response", result.Identity.ResponseNumber);
        writer.WriteStartArray("candidates");
        foreach (var candidate in result.Candidates)
        {
            writer.WriteStartObject();
            writer.WriteString("voiceType", candidate.VoiceType);
            writer.WriteString("path", candidate.Audio.Path.Replace('\\', '/'));
            writer.WriteString("provenance", candidate.Audio.Source);
            writer.WriteStartArray("lipCompanions");
            foreach (var lip in candidate.LipCompanions)
            {
                writer.WriteStartObject();
                writer.WriteString("path", lip.Path.Replace('\\', '/'));
                writer.WriteString("provenance", lip.Source);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("exports");
        foreach (var export in exports)
        {
            writer.WriteStartObject();
            writer.WriteString("path", export.OutputPath);
            writer.WriteBoolean("success", export.Success);
            writer.WriteString("error", export.Error);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
