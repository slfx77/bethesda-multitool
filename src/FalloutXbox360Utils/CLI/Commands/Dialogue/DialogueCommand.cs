// Pending migration: unattributed/debug/verify commands will move to analysis tools

#pragma warning disable IDE0051, S1144

using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.CLI.Shared;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dialogue;

/// <summary>
///     CLI command for dialogue parsing diagnostics.
///     Entry point and shared utilities used by subcommand classes.
/// </summary>
public static class DialogueCommand
{
    public static Command Create()
    {
        var command = new Command("dialogue", "Dialogue parsing and diagnostics");

        command.Subcommands.Add(DialogueStatsCommand.CreateStatsCommand());
        command.Subcommands.Add(DialogueTreeCommand.CreateTreeCommand());
        command.Subcommands.Add(CreateTopicCommand());
        command.Subcommands.Add(DialogueTreeCommand.CreateNpcCommand());
        command.Subcommands.Add(DialogueProvenanceCommand.CreateProvenanceCommand());

        return command;
    }

    #region Shared Utilities

    internal static async Task<(RecordCollection result, Dictionary<uint, string> formIdMap)?> LoadAndParseAsync(
        string input, CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return null;
        }

        AnsiConsole.MarkupLine("[blue]Loading:[/] {0}", Path.GetFileName(input));

        var isDump = Path.GetExtension(input).Equals(".dmp", StringComparison.OrdinalIgnoreCase);

        var taskLabel = isDump ? "Analyzing memory dump..." : "Analyzing ESM file...";
        var analysisResult = await CliProgressRunner.RunWithProgressAsync(
            taskLabel,
            async (progress, ct) =>
            {
                if (isDump)
                {
                    var analyzer = new MinidumpAnalyzer();
                    return await analyzer.AnalyzeAsync(input, progress, true, false, ct);
                }

                return await EsmFileAnalyzer.AnalyzeAsync(input, progress, ct);
            },
            cancellationToken);

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ESM records found in file.");
            return null;
        }

        AnsiConsole.MarkupLine("[blue]Parsing dialogue...[/]");

        var fileInfo = new FileInfo(input);
        RecordCollection semanticResult;
        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(
                analysisResult.EsmRecords, analysisResult.FormIdMap, accessor, fileInfo.Length,
                analysisResult.MinidumpInfo);
            semanticResult = parser.ParseAll();
        }

        return (semanticResult, analysisResult.FormIdMap);
    }

    internal static string FormatPct(int count, int total)
    {
        return total > 0 ? $"{100.0 * count / total:F1}%" : "N/A";
    }

    internal static bool HasAnySpeakerAttribution(DialogueRecord d)
    {
        return d.SpeakerFormId is > 0 || d.SpeakerFactionFormId is > 0 ||
               d.SpeakerRaceFormId is > 0 || d.SpeakerVoiceTypeFormId is > 0;
    }

    /// <summary>
    ///     Resolve the best display label for a dialogue line's speaker.
    ///     Returns (label, spectre_color) for rendering.
    /// </summary>
    internal static (string Label, string Color) ResolveSpeakerDisplay(
        DialogueRecord info,
        Dictionary<uint, string> lookup,
        Dictionary<uint, string> topicTypeMap)
    {
        // Named NPC speaker
        if (info.SpeakerFormId is > 0)
        {
            var name = lookup.GetValueOrDefault(info.SpeakerFormId.Value, $"0x{info.SpeakerFormId.Value:X8}");
            return (name, "green");
        }

        // Radio topic -> radio glyph + station name
        if (info.TopicFormId is > 0 &&
            topicTypeMap.GetValueOrDefault(info.TopicFormId.Value) == "Radio")
        {
            var stationName = info.QuestFormId is > 0
                ? lookup.GetValueOrDefault(info.QuestFormId.Value, "Radio Station")
                : "Radio Station";
            return ($"\U0001F4FB {stationName}", "yellow");
        }

        // Faction/voice type/race speaker -> generic response
        if (info.SpeakerFactionFormId is > 0 || info.SpeakerVoiceTypeFormId is > 0 ||
            info.SpeakerRaceFormId is > 0)
        {
            return ("Generic Response", "dim");
        }

        // No speaker data at all
        return ("Generic Response", "dim");
    }

    internal static string BuildFlagString(DialogueRecord info)
    {
        var flags = new List<string>();
        if (info.IsGoodbye)
        {
            flags.Add("Goodbye");
        }

        if (info.IsSayOnce)
        {
            flags.Add("SayOnce");
        }

        if (info.IsSpeechChallenge)
        {
            flags.Add($"Speech {info.DifficultyName}");
        }

        return flags.Count > 0 ? $" [dim]({string.Join(", ", flags)})[/]" : "";
    }

    internal static List<DialogueRecord> OrderInfoChain(List<DialogueRecord> infos)
    {
        if (infos.Count <= 1)
        {
            return infos;
        }

        // Build PNAM chain: find the head (no PreviousInfo or PreviousInfo not in this group)
        var byFormId = infos.ToDictionary(i => i.FormId);
        var heads = infos.Where(i => i.PreviousInfo is not > 0 || !byFormId.ContainsKey(i.PreviousInfo!.Value))
            .ToList();

        if (heads.Count == 0)
        {
            return infos.OrderBy(i => i.InfoIndex).ToList();
        }

        // Walk each chain
        var ordered = new List<DialogueRecord>();
        var visited = new HashSet<uint>();

        foreach (var head in heads)
        {
            var current = head;
            while (current != null && visited.Add(current.FormId))
            {
                ordered.Add(current);
                // Find next: the INFO whose PreviousInfo points to current
                current = infos.FirstOrDefault(i =>
                    i.PreviousInfo == current.FormId && !visited.Contains(i.FormId));
            }
        }

        // Add any remaining that weren't in chains
        ordered.AddRange(infos.Where(info => visited.Add(info.FormId)));

        return ordered;
    }

    #endregion

    #region Topic Command

    private static Command CreateTopicCommand()
    {
        var command = new Command("topic", "Show details for a specific topic");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM file" };
        var formIdArg = new Argument<string>("formid") { Description = "Topic FormID (hex, e.g. 0x12345)" };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(formIdArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var formIdStr = parseResult.GetValue(formIdArg)!;
            await RunTopicAsync(input, formIdStr, cancellationToken);
        });

        return command;
    }

    private static async Task RunTopicAsync(string input, string formIdStr, CancellationToken cancellationToken)
    {
        var formId = CliHelpers.ParseFormId(formIdStr) ?? 0;
        if (formId == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid FormID: {0}", formIdStr);
            return;
        }

        var loaded = await LoadAndParseAsync(input, cancellationToken);
        if (loaded == null)
        {
            return;
        }

        var (result, formIdMap) = loaded.Value;

        var topic = result.DialogTopics.FirstOrDefault(t => t.FormId == formId);
        if (topic == null)
        {
            AnsiConsole.MarkupLine("[yellow]Topic 0x{0:X8} not found.[/]", formId);
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]Topic 0x{formId:X8}[/]").LeftJustified());
        AnsiConsole.WriteLine();

        // Topic details
        var detailTable = new Table().Border(TableBorder.Rounded).HideHeaders();
        detailTable.AddColumn("Property");
        detailTable.AddColumn("Value");

        detailTable.AddRow("FormID", $"0x{topic.FormId:X8}");
        detailTable.AddRow("EditorID", topic.EditorId ?? "(none)");
        detailTable.AddRow("Display Name", topic.FullName ?? "(none)");
        detailTable.AddRow("Type", topic.TopicTypeName);
        detailTable.AddRow("Flags",
            $"0x{topic.Flags:X2}" +
            (topic.IsRumors ? " [Rumors]" : "") +
            (topic.IsTopLevel ? " [TopLevel]" : ""));
        detailTable.AddRow("Speaker",
            topic.SpeakerFormId is > 0
                ? $"0x{topic.SpeakerFormId:X8} ({formIdMap.GetValueOrDefault(topic.SpeakerFormId.Value, "?")})"
                : "(none)");
        detailTable.AddRow("Quest",
            topic.QuestFormId is > 0
                ? $"0x{topic.QuestFormId:X8} ({formIdMap.GetValueOrDefault(topic.QuestFormId.Value, "?")})"
                : "(none)");
        detailTable.AddRow("Priority", $"{topic.Priority:F1}");
        detailTable.AddRow("Response Count", $"{topic.ResponseCount}");
        detailTable.AddRow("Offset", $"0x{topic.Offset:X}");
        detailTable.AddRow("Endianness", topic.IsBigEndian ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)");

        AnsiConsole.Write(detailTable);
        AnsiConsole.WriteLine();

        // Child INFO records
        var childInfos = result.Dialogues
            .Where(d => d.TopicFormId == formId)
            .OrderBy(d => d.InfoIndex)
            .ToList();

        if (childInfos.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No INFO records linked to this topic.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]{childInfos.Count} INFO record(s):[/]");
        AnsiConsole.WriteLine();

        foreach (var info in childInfos)
        {
            var infoTable = new Table().Border(TableBorder.Simple).HideHeaders();
            infoTable.AddColumn("Property");
            infoTable.AddColumn("Value");

            infoTable.AddRow("FormID", $"0x{info.FormId:X8}");
            infoTable.AddRow("EditorID", info.EditorId ?? "(none)");

            if (info.SpeakerFormId is > 0)
            {
                infoTable.AddRow("Speaker",
                    $"0x{info.SpeakerFormId:X8} ({formIdMap.GetValueOrDefault(info.SpeakerFormId.Value, "?")})");
            }

            if (!string.IsNullOrEmpty(info.PromptText))
            {
                infoTable.AddRow("Prompt", info.PromptText);
            }

            foreach (var resp in info.Responses)
            {
                var emotionStr = resp.EmotionType != 0
                    ? $" [dim](Emotion: {resp.EmotionType}, Value: {resp.EmotionValue})[/]"
                    : "";
                infoTable.AddRow($"Response #{resp.ResponseNumber}", (resp.Text ?? "(empty)") + emotionStr);
            }

            if (info.PreviousInfo is > 0)
            {
                infoTable.AddRow("Previous INFO", $"0x{info.PreviousInfo:X8}");
            }

            if (info.LinkToTopics.Count > 0)
            {
                infoTable.AddRow("Link-To Topics (TCLT)",
                    string.Join(", ", info.LinkToTopics.Select(id => $"0x{id:X8}")));
            }

            if (info.LinkFromTopics.Count > 0)
            {
                infoTable.AddRow("Link-From Topics (TCLF)",
                    string.Join(", ", info.LinkFromTopics.Select(id => $"0x{id:X8}")));
            }

            if (info.AddTopics.Count > 0)
            {
                infoTable.AddRow("Add Topics (NAME)",
                    string.Join(", ", info.AddTopics.Select(id => $"0x{id:X8}")));
            }

            if (info.FollowUpInfos.Count > 0)
            {
                infoTable.AddRow("Follow-Up INFOs (runtime)",
                    string.Join(", ", info.FollowUpInfos.Select(id => $"0x{id:X8}")));
            }

            if (info.Difficulty > 0)
            {
                infoTable.AddRow("Difficulty", $"{info.Difficulty}");
            }

            AnsiConsole.Write(infoTable);
            AnsiConsole.WriteLine();
        }
    }

    #endregion
}
