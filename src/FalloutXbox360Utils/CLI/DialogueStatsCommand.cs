using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for dialogue reconstruction statistics.
/// </summary>
internal static class DialogueStatsCommand
{
    internal static Command CreateStatsCommand()
    {
        var command = new Command("stats", "Show dialogue reconstruction statistics");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM file" };

        command.Arguments.Add(inputArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            await RunStatsAsync(input, cancellationToken);
        });

        return command;
    }

    private static async Task RunStatsAsync(string input, CancellationToken cancellationToken)
    {
        var loaded = await DialogueCommand.LoadAndReconstructAsync(input, cancellationToken);
        if (loaded == null)
        {
            return;
        }

        var (result, formIdMap) = loaded.Value;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Dialogue Reconstruction Statistics[/]").LeftJustified());
        AnsiConsole.WriteLine();

        // Record counts
        var countTable = new Table().Border(TableBorder.Rounded);
        countTable.AddColumn("Metric");
        countTable.AddColumn(new TableColumn("Count").RightAligned());

        countTable.AddRow("DIAL (Topic) records", $"{result.DialogTopics.Count:N0}");
        countTable.AddRow("INFO (Dialogue) records", $"{result.Dialogues.Count:N0}");
        countTable.AddRow("QUST (Quest) records", $"{result.Quests.Count:N0}");

        AnsiConsole.Write(countTable);
        AnsiConsole.WriteLine();

        // Linking statistics
        var linkedToTopic = result.Dialogues.Count(d => d.TopicFormId is > 0);
        var unlinked = result.Dialogues.Count - linkedToTopic;
        var withSpeaker = result.Dialogues.Count(d => d.SpeakerFormId is > 0);
        var withQuest = result.Dialogues.Count(d => d.QuestFormId is > 0);
        var withResponses = result.Dialogues.Count(d => d.Responses.Count > 0);

        var linkTable = new Table().Border(TableBorder.Rounded);
        linkTable.AddColumn("INFO Linking");
        linkTable.AddColumn(new TableColumn("Count").RightAligned());
        linkTable.AddColumn(new TableColumn("Percentage").RightAligned());

        var withFaction = result.Dialogues.Count(d => d.SpeakerFactionFormId is > 0);
        var withRace = result.Dialogues.Count(d => d.SpeakerRaceFormId is > 0);
        var withVoiceType = result.Dialogues.Count(d => d.SpeakerVoiceTypeFormId is > 0);
        var withAnySpeakerAttribution = result.Dialogues.Count(d =>
            d.SpeakerFormId is > 0 || d.SpeakerFactionFormId is > 0 ||
            d.SpeakerRaceFormId is > 0 || d.SpeakerVoiceTypeFormId is > 0);

        // Build topic type lookup for radio/generic categorization
        var topicTypeMap = result.DialogTopics
            .GroupBy(t => t.FormId)
            .ToDictionary(g => g.Key, g => g.First().TopicTypeName);

        var radioLines = result.Dialogues.Count(d =>
            !DialogueCommand.HasAnySpeakerAttribution(d) &&
            d.TopicFormId is > 0 &&
            topicTypeMap.GetValueOrDefault(d.TopicFormId.Value) == "Radio");
        var genericLines = result.Dialogues.Count(d =>
            !DialogueCommand.HasAnySpeakerAttribution(d) &&
            !(d.TopicFormId is > 0 && topicTypeMap.GetValueOrDefault(d.TopicFormId.Value) == "Radio"));

        var total = result.Dialogues.Count;
        linkTable.AddRow("Linked to topic", $"{linkedToTopic:N0}", DialogueCommand.FormatPct(linkedToTopic, total));
        linkTable.AddRow("[red]Unlinked[/]", $"{unlinked:N0}", DialogueCommand.FormatPct(unlinked, total));
        linkTable.AddRow("With speaker (NPC)", $"{withSpeaker:N0}", DialogueCommand.FormatPct(withSpeaker, total));
        linkTable.AddRow("With faction speaker", $"{withFaction:N0}", DialogueCommand.FormatPct(withFaction, total));
        linkTable.AddRow("With race speaker", $"{withRace:N0}", DialogueCommand.FormatPct(withRace, total));
        linkTable.AddRow("With voice type speaker", $"{withVoiceType:N0}",
            DialogueCommand.FormatPct(withVoiceType, total));
        linkTable.AddRow("\U0001F4FB Radio station", $"{radioLines:N0}",
            DialogueCommand.FormatPct(radioLines, total));
        linkTable.AddRow("[green]Any speaker attribution[/]", $"{withAnySpeakerAttribution:N0}",
            DialogueCommand.FormatPct(withAnySpeakerAttribution, total));
        linkTable.AddRow("[dim]Generic / unattributed[/]", $"{genericLines:N0}",
            DialogueCommand.FormatPct(genericLines, total));
        linkTable.AddRow("With quest", $"{withQuest:N0}", DialogueCommand.FormatPct(withQuest, total));
        linkTable.AddRow("With response text", $"{withResponses:N0}",
            DialogueCommand.FormatPct(withResponses, total));

        AnsiConsole.Write(linkTable);
        AnsiConsole.WriteLine();

        // Topic type distribution
        var topicsByType = result.DialogTopics
            .GroupBy(t => t.TopicTypeName)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (topicsByType.Count > 0)
        {
            var typeTable = new Table().Border(TableBorder.Rounded);
            typeTable.AddColumn("Topic Type");
            typeTable.AddColumn(new TableColumn("Count").RightAligned());
            typeTable.AddColumn(new TableColumn("With Speaker").RightAligned());
            typeTable.AddColumn(new TableColumn("With Quest").RightAligned());

            foreach (var group in topicsByType)
            {
                typeTable.AddRow(
                    group.Key,
                    $"{group.Count():N0}",
                    $"{group.Count(t => t.SpeakerFormId is > 0):N0}",
                    $"{group.Count(t => t.QuestFormId is > 0):N0}");
            }

            AnsiConsole.Write(typeTable);
            AnsiConsole.WriteLine();
        }

        // INFOs per topic distribution -- pre-build lookup to avoid O(n^2)
        var dialogueCountByTopic = result.Dialogues
            .Where(d => d.TopicFormId.HasValue)
            .GroupBy(d => d.TopicFormId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var infosPerTopic = result.DialogTopics
            .Select(t =>
            {
                dialogueCountByTopic.TryGetValue(t.FormId, out var count);
                return (Topic: t, InfoCount: count);
            })
            .Where(x => x.InfoCount > 0)
            .ToList();

        if (infosPerTopic.Count > 0)
        {
            var counts = infosPerTopic.Select(x => x.InfoCount).OrderBy(x => x).ToList();
            var distTable = new Table().Border(TableBorder.Rounded);
            distTable.AddColumn("INFOs per Topic");
            distTable.AddColumn(new TableColumn("Value").RightAligned());

            distTable.AddRow("Topics with INFOs", $"{infosPerTopic.Count:N0}");
            distTable.AddRow("Min", $"{counts[0]}");
            distTable.AddRow("Median", $"{counts[counts.Count / 2]}");
            distTable.AddRow("Average", $"{counts.Average():F1}");
            distTable.AddRow("Max", $"{counts[^1]}");
            distTable.AddRow("Total INFOs linked", $"{counts.Sum():N0}");

            AnsiConsole.Write(distTable);
            AnsiConsole.WriteLine();
        }

        // Dialogue tree stats
        if (result.DialogueTree != null)
        {
            var tree = result.DialogueTree;
            var treeTable = new Table().Border(TableBorder.Rounded);
            treeTable.AddColumn("Dialogue Tree");
            treeTable.AddColumn(new TableColumn("Count").RightAligned());

            treeTable.AddRow("Quests with dialogue", $"{tree.QuestTrees.Count:N0}");
            treeTable.AddRow("Total topics in quests",
                $"{tree.QuestTrees.Values.Sum(q => q.Topics.Count):N0}");
            treeTable.AddRow("Orphan topics (no quest)", $"{tree.OrphanTopics.Count:N0}");

            AnsiConsole.Write(treeTable);
            AnsiConsole.WriteLine();
        }

        // Speaker distribution (top 20)
        var speakerGroups = result.Dialogues
            .Where(d => d.SpeakerFormId is > 0)
            .GroupBy(d => d.SpeakerFormId!.Value)
            .OrderByDescending(g => g.Count())
            .Take(20)
            .ToList();

        if (speakerGroups.Count > 0)
        {
            var speakerTable = new Table().Border(TableBorder.Rounded);
            speakerTable.AddColumn("Speaker (Top 20)");
            speakerTable.AddColumn(new TableColumn("FormID").RightAligned());
            speakerTable.AddColumn(new TableColumn("Dialogues").RightAligned());

            foreach (var group in speakerGroups)
            {
                var name = formIdMap.GetValueOrDefault(group.Key) ??
                           result.FormIdToDisplayName?.GetValueOrDefault(group.Key);
                speakerTable.AddRow(
                    name ?? "(unknown)",
                    $"0x{group.Key:X8}",
                    $"{group.Count():N0}");
            }

            AnsiConsole.Write(speakerTable);
        }
    }
}
