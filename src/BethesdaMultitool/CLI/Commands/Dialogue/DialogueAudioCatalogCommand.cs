using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Media.Audio.Dialogue;
using BethesdaMultitool.Core.Semantic;

namespace BethesdaMultitool.CLI.Commands.Dialogue;

internal static class DialogueAudioCatalogCommand
{
    internal static Command Create()
    {
        var command = new Command("audio-catalog", "Catalog voice assets, original INFO matches, and orphan audio");
        var source = new Argument<string>("source") { Description = "Archive or game data directory" };
        var plugins = new Option<string[]>("--plugin-file")
        { Description = "Original ESM/ESP files to join (repeat for multiple files)", AllowMultipleArgumentsPerToken = true };
        var format = new Option<string>("--format") { DefaultValueFactory = _ => "json" };
        var output = new Option<string?>("--output", "-o");
        command.Arguments.Add(source);
        command.Options.Add(plugins);
        command.Options.Add(format);
        command.Options.Add(output);
        command.SetAction(async (result, token) =>
        {
            try
            {
                var outputFormat = result.GetValue(format)!.ToLowerInvariant();
                if (outputFormat is not ("json" or "csv")) throw new ArgumentException("--format must be json or csv.");
                var outputPath = result.GetValue(output);
                if (outputPath != null && File.Exists(outputPath)) throw new IOException("Output already exists.");
                var records = new List<AudioCatalogInfo>();
                var supplied = new List<string>();
                foreach (var path in result.GetValue(plugins) ?? [])
                {
                    token.ThrowIfCancellationRequested();
                    var owners = DialoguePluginIdentity.ReadOwners(path)
                        ?? throw new InvalidDataException($"Incomplete original plugin header: {path}");
                    supplied.Add(Path.GetFileName(path));
                    using var loaded = await SemanticFileLoader.LoadAsync(path, cancellationToken: token);
                    foreach (var info in loaded.Records.Dialogues)
                    {
                        var slot = info.FormId >> 24;
                        if (slot >= owners.Count) continue;
                        records.Add(new(owners[(int)slot], info.FormId, Path.GetFullPath(path), info.Offset,
                            info.Responses.Select(response => new AudioCatalogResponse(response.ResponseNumber, response.Text)).ToArray()));
                    }
                }
                await using var assets = BethesdaBrowseSource.Open(ExploreSourcePlanner.Create(result.GetValue(source)!, token));
                var rows = DialogueAudioCatalog.Build(assets.Session.FileSystem.EnumerateFiles("sound/voice/"),
                    records, supplied, token);
                if (outputPath == null)
                {
                    if (outputFormat == "csv") Console.Out.Write(ToCsv(rows));
                    else WriteJson(Console.OpenStandardOutput(), rows);
                }
                else
                {
                    var full = Path.GetFullPath(outputPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write);
                    if (outputFormat == "csv")
                    {
                        using var writer = new StreamWriter(file);
                        await writer.WriteAsync(ToCsv(rows).AsMemory(), token);
                    }
                    else WriteJson(file, rows);
                }
                return 0;
            }
            catch (OperationCanceledException) { return 130; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { Console.Error.WriteLine(ex.Message); return 1; }
        });
        return command;
    }

    internal static string ToCsv(IReadOnlyList<AudioCatalogRow> rows)
    {
        var text = new StringBuilder("Path,Source,Plugin,VoiceType,OriginalInfoFormID,ResponseNumber,State,AmbiguousAudio,RecordSource,RecordOffset,Text,LipPaths,ListeningReview\n");
        foreach (var row in rows)
        {
            IEnumerable<AudioCatalogMatch?> matches = row.Matches.Count == 0
                ? (IEnumerable<AudioCatalogMatch?>)[null] : row.Matches;
            foreach (var match in matches)
            {
                string[] cells = [row.Path, row.Source, row.Plugin ?? "", row.VoiceType ?? "",
                    row.OriginalInfoFormId?.ToString("X8", CultureInfo.InvariantCulture) ?? "",
                    row.ResponseNumber?.ToString(CultureInfo.InvariantCulture) ?? "", row.State,
                    row.AmbiguousAudio ? "true" : "false", match?.Source ?? "",
                    match?.Offset.ToString(CultureInfo.InvariantCulture) ?? "", match?.Text ?? "",
                    string.Join(';', row.LipPaths), "Pending"];
                text.AppendLine(string.Join(',', cells.Select(Fmt.CsvEscape)));
            }
        }
        return text.ToString();
    }

    private static void WriteJson(Stream output, IReadOnlyList<AudioCatalogRow> rows)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteStartArray("entries");
        foreach (var row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("path", row.Path);
            writer.WriteString("source", row.Source);
            writer.WriteString("plugin", row.Plugin);
            writer.WriteString("voiceType", row.VoiceType);
            writer.WriteString("originalInfoFormId", row.OriginalInfoFormId?.ToString("X8", CultureInfo.InvariantCulture));
            if (row.ResponseNumber.HasValue) writer.WriteNumber("responseNumber", row.ResponseNumber.Value);
            else writer.WriteNull("responseNumber");
            writer.WriteString("state", row.State);
            writer.WriteBoolean("ambiguousAudio", row.AmbiguousAudio);
            writer.WriteString("listeningReview", "Pending");
            writer.WriteStartArray("matches");
            foreach (var match in row.Matches)
            {
                writer.WriteStartObject();
                writer.WriteString("source", match.Source);
                writer.WriteNumber("offset", match.Offset);
                writer.WriteString("text", match.Text);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("lipPaths");
            foreach (var lip in row.LipPaths) writer.WriteStringValue(lip);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
