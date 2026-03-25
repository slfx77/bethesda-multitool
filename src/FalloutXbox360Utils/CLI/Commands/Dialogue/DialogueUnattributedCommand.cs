using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dialogue;

/// <summary>
///     CLI command for analyzing unattributed dialogue lines.
/// </summary>
internal static class DialogueUnattributedCommand
{
    internal static Command CreateUnattributedCommand()
    {
        var command = new Command("unattributed", "Analyze dialogue lines with no speaker attribution");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM file" };
        var limitOpt = new Option<int?>("--limit") { Description = "Max sample lines to show" };
        var verboseOpt = new Option<bool>("--verbose") { Description = "Show per-line detail" };

        command.Arguments.Add(inputArg);
        command.Options.Add(limitOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var limit = parseResult.GetValue(limitOpt) ?? 20;
            var verbose = parseResult.GetValue(verboseOpt);
            await RunUnattributedAsync(input, limit, verbose, cancellationToken);
        });

        return command;
    }

    private static async Task RunUnattributedAsync(string input, int limit, bool verbose,
        CancellationToken cancellationToken)
    {
        var loaded = await DialogueCommand.LoadAndParseAsync(input, cancellationToken);
        if (loaded == null)
        {
            return;
        }

        var (result, formIdMap) = loaded.Value;

        // Build lookup
        var lookup = new Dictionary<uint, string>(formIdMap);
        if (result.FormIdToEditorId != null)
        {
            foreach (var (k, v) in result.FormIdToEditorId)
            {
                lookup.TryAdd(k, v);
            }
        }

        if (result.FormIdToDisplayName != null)
        {
            foreach (var (k, v) in result.FormIdToDisplayName)
            {
                lookup.TryAdd(k, v);
            }
        }

        // Find unattributed lines (no NPC, no faction, no race, no voice type)
        var unattributed = result.Dialogues
            .Where(d =>
                d.SpeakerFormId is not > 0 &&
                d.SpeakerFactionFormId is not > 0 &&
                d.SpeakerRaceFormId is not > 0 &&
                d.SpeakerVoiceTypeFormId is not > 0)
            .ToList();

        var total = result.Dialogues.Count;

        // Section 1: Summary
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Unattributed Dialogue Analysis[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"\nUnattributed: [yellow]{unattributed.Count:N0}[/] / {total:N0} ([yellow]{DialogueCommand.FormatPct(unattributed.Count, total)}[/])");
        AnsiConsole.WriteLine();

        // Build topic lookups (GroupBy handles duplicate FormIds in dump scenarios)
        var topicTypeMap = result.DialogTopics
            .GroupBy(t => t.FormId)
            .ToDictionary(g => g.Key, g => g.First().TopicTypeName);
        var topicNameMap = result.DialogTopics
            .GroupBy(t => t.FormId)
            .ToDictionary(g => g.Key, g => g.First().FullName ?? g.First().EditorId ?? $"0x{g.Key:X8}");

        // Section 2: By Topic Type
        var byTopicType = unattributed
            .GroupBy(d =>
                d.TopicFormId is > 0 ? topicTypeMap.GetValueOrDefault(d.TopicFormId.Value, "Unknown") : "Unlinked")
            .OrderByDescending(g => g.Count())
            .ToList();

        var typeTable = new Table().Border(TableBorder.Rounded);
        typeTable.AddColumn("Topic Type");
        typeTable.AddColumn(new TableColumn("Count").RightAligned());
        typeTable.AddColumn(new TableColumn("% of Unattributed").RightAligned());

        foreach (var g in byTopicType)
        {
            typeTable.AddRow(g.Key, $"{g.Count():N0}", DialogueCommand.FormatPct(g.Count(), unattributed.Count));
        }

        AnsiConsole.Write(typeTable);
        AnsiConsole.WriteLine();

        // Section 3: By Condition Presence
        var noConditions = unattributed.Count(d => d.ConditionFunctions.Count == 0);
        var hasConditions = unattributed.Where(d => d.ConditionFunctions.Count > 0).ToList();

        // Known speaker-like function indices
        HashSet<ushort> speakerLikeFunctions = [0x48, 0x47, 0x45, 0x1AB, 0x40, 0x44, 0x46, 0x1B6];
        var hasSpeakerLike = hasConditions.Count(d => d.ConditionFunctions.Any(f => speakerLikeFunctions.Contains(f)));
        var hasConditionsNoSpeaker = hasConditions.Count - hasSpeakerLike;

        var condTable = new Table().Border(TableBorder.Rounded);
        condTable.AddColumn("Condition Category");
        condTable.AddColumn(new TableColumn("Count").RightAligned());
        condTable.AddColumn(new TableColumn("% of Unattributed").RightAligned());

        condTable.AddRow("No CTDA conditions at all", $"{noConditions:N0}",
            DialogueCommand.FormatPct(noConditions, unattributed.Count));
        condTable.AddRow("Has CTDA, no speaker-like functions", $"{hasConditionsNoSpeaker:N0}",
            DialogueCommand.FormatPct(hasConditionsNoSpeaker, unattributed.Count));
        condTable.AddRow("Has unhandled speaker-like conditions", $"{hasSpeakerLike:N0}",
            DialogueCommand.FormatPct(hasSpeakerLike, unattributed.Count));

        AnsiConsole.Write(condTable);
        AnsiConsole.WriteLine();

        // Section 4: Top Condition Functions on unattributed lines
        var functionCounts = hasConditions
            .SelectMany(d => d.ConditionFunctions)
            .GroupBy(f => f)
            .Select(g => new { FunctionIndex = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(20)
            .ToList();

        if (functionCounts.Count > 0)
        {
            var funcTable = new Table().Border(TableBorder.Rounded);
            funcTable.AddColumn("Function");
            funcTable.AddColumn(new TableColumn("Index").RightAligned());
            funcTable.AddColumn(new TableColumn("Occurrences").RightAligned());

            foreach (var fc in functionCounts)
            {
                var name = ScriptFunctionTable.GetName((ushort)(0x1000 + fc.FunctionIndex));
                funcTable.AddRow(Markup.Escape(name), $"0x{fc.FunctionIndex:X3}", $"{fc.Count:N0}");
            }

            AnsiConsole.Write(funcTable);
            AnsiConsole.WriteLine();
        }

        // Section 5: Top Quests
        var byQuest = unattributed
            .Where(d => d.QuestFormId is > 0)
            .GroupBy(d => d.QuestFormId!.Value)
            .OrderByDescending(g => g.Count())
            .Take(15)
            .ToList();

        if (byQuest.Count > 0)
        {
            var questTable = new Table().Border(TableBorder.Rounded);
            questTable.AddColumn("Quest");
            questTable.AddColumn(new TableColumn("FormID").RightAligned());
            questTable.AddColumn(new TableColumn("Unattributed").RightAligned());

            foreach (var g in byQuest)
            {
                var questName = lookup.GetValueOrDefault(g.Key, $"0x{g.Key:X8}");
                questTable.AddRow(Markup.Escape(questName), $"0x{g.Key:X8}", $"{g.Count():N0}");
            }

            AnsiConsole.Write(questTable);
            AnsiConsole.WriteLine();
        }

        // Section 6: Sample Lines
        var samples = verbose ? unattributed.Take(limit * 5).ToList() : unattributed.Take(limit).ToList();
        if (samples.Count > 0)
        {
            AnsiConsole.Write(new Rule("[blue]Sample Unattributed Lines[/]").LeftJustified());
            AnsiConsole.WriteLine();

            foreach (var info in samples)
            {
                var topicName = info.TopicFormId is > 0
                    ? topicNameMap.GetValueOrDefault(info.TopicFormId.Value, "?")
                    : "(unlinked)";
                var topicType = info.TopicFormId is > 0
                    ? topicTypeMap.GetValueOrDefault(info.TopicFormId.Value, "?")
                    : "?";
                var questName = info.QuestFormId is > 0
                    ? lookup.GetValueOrDefault(info.QuestFormId.Value, "?")
                    : "(none)";
                var text = info.Responses.Count > 0
                    ? info.Responses[0].Text ?? "(no text)"
                    : "(no response)";
                if (text.Length > 80)
                {
                    text = text[..77] + "...";
                }

                var (speakerLabel, speakerColor) = DialogueCommand.ResolveSpeakerDisplay(info, lookup, topicTypeMap);

                AnsiConsole.MarkupLine(
                    $"  [dim]0x{info.FormId:X8}[/] [[{Markup.Escape(topicType)}]] [cyan]{Markup.Escape(topicName)}[/]");
                AnsiConsole.MarkupLine(
                    $"    [{speakerColor}]{Markup.Escape(speakerLabel)}[/] | Quest: {Markup.Escape(questName)}");
                AnsiConsole.MarkupLine(
                    $"    \"{Markup.Escape(text)}\"");
                AnsiConsole.WriteLine();
            }

            if (unattributed.Count > samples.Count)
            {
                AnsiConsole.MarkupLine(
                    $"[dim]... and {unattributed.Count - samples.Count:N0} more (use --limit or --verbose to see more)[/]");
            }
        }
    }
}
