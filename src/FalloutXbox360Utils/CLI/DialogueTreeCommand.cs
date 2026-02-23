using System.CommandLine;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI commands for dialogue tree and NPC browsing.
/// </summary>
internal static class DialogueTreeCommand
{
    internal static Command CreateTreeCommand()
    {
        var command = new Command("tree", "Show dialogue tree hierarchy");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM file" };
        var limitOpt = new Option<int?>("-l", "--limit") { Description = "Limit number of quests shown" };
        var questOpt = new Option<string?>("-q", "--quest")
            { Description = "Filter by quest FormID (hex, e.g. 0x12345)" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Output file path" };

        command.Arguments.Add(inputArg);
        command.Options.Add(limitOpt);
        command.Options.Add(questOpt);
        command.Options.Add(outputOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var limit = parseResult.GetValue(limitOpt);
            var quest = parseResult.GetValue(questOpt);
            var output = parseResult.GetValue(outputOpt);
            await RunTreeAsync(input, limit, quest, output, cancellationToken);
        });

        return command;
    }

    internal static Command CreateNpcCommand()
    {
        var command = new Command("npc", "Browse dialogue by NPC");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM file" };
        var npcArg = new Argument<string?>("npc")
            { Description = "NPC FormID (hex) or partial name. Omit for --list.", Arity = ArgumentArity.ZeroOrOne };
        var listOpt = new Option<bool>("--list") { Description = "List all NPCs with dialogue" };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(npcArg);
        command.Options.Add(listOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var npc = parseResult.GetValue(npcArg);
            var list = parseResult.GetValue(listOpt);
            await RunNpcAsync(input, npc, list, cancellationToken);
        });

        return command;
    }

    private static async Task RunTreeAsync(string input, int? limit, string? questFilter, string? output,
        CancellationToken cancellationToken)
    {
        var loaded = await DialogueCommand.LoadAndReconstructAsync(input, cancellationToken);
        if (loaded == null)
        {
            return;
        }

        var (result, formIdMap) = loaded.Value;

        if (result.DialogueTree == null)
        {
            AnsiConsole.MarkupLine("[yellow]No dialogue tree found.[/]");
            return;
        }

        // Build lookup
        var lookup = new Dictionary<uint, string>(formIdMap);
        if (result.FormIdToEditorId != null)
        {
            foreach (var (k, v) in result.FormIdToEditorId)
            {
                lookup.TryAdd(k, v);
            }
        }

        // Filter by quest if specified
        var tree = result.DialogueTree;
        if (!string.IsNullOrEmpty(questFilter))
        {
            var questFormId = CliHelpers.ParseFormId(questFilter) ?? 0;
            if (questFormId == 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Invalid FormID: {0}", questFilter);
                return;
            }

            if (tree.QuestTrees.TryGetValue(questFormId, out var questNode))
            {
                tree = new DialogueTreeResult
                {
                    QuestTrees = new Dictionary<uint, QuestDialogueNode> { [questFormId] = questNode },
                    OrphanTopics = []
                };
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]Quest 0x{0:X8} not found in dialogue tree.[/]", questFormId);
                return;
            }
        }

        // Apply limit
        if (limit.HasValue && tree.QuestTrees.Count > limit.Value)
        {
            var limited = tree.QuestTrees.Take(limit.Value).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            tree = new DialogueTreeResult
            {
                QuestTrees = limited,
                OrphanTopics = tree.OrphanTopics
            };
        }

        var resolver = result.CreateResolver(lookup);
        var report = GeckReportGenerator.GenerateDialogueTreeReport(tree, resolver);

        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, report, cancellationToken);
            AnsiConsole.MarkupLine("[green]Dialogue tree written to:[/] {0}", output);
        }
        else
        {
            Console.Write(report);
        }
    }

    private static async Task RunNpcAsync(string input, string? npcFilter, bool listMode,
        CancellationToken cancellationToken)
    {
        var loaded = await DialogueCommand.LoadAndReconstructAsync(input, cancellationToken);
        if (loaded == null)
        {
            return;
        }

        var (result, formIdMap) = loaded.Value;

        // Build lookup: FormID -> display name
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

        // Group all dialogue by speaker
        var bySpeaker = result.Dialogues
            .Where(d => d.SpeakerFormId is > 0)
            .GroupBy(d => d.SpeakerFormId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (listMode || string.IsNullOrEmpty(npcFilter))
        {
            RunNpcList(bySpeaker, lookup);
            return;
        }

        // Find the NPC -- try hex FormID first, then partial name match
        uint targetFormId = 0;
        if (npcFilter.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            uint.TryParse(npcFilter, System.Globalization.NumberStyles.HexNumber, null, out _))
        {
            targetFormId = CliHelpers.ParseFormId(npcFilter) ?? 0;
        }

        if (targetFormId == 0)
        {
            // Partial name match
            var matches = bySpeaker.Keys
                .Where(id =>
                {
                    var name = lookup.GetValueOrDefault(id);
                    return name != null &&
                           name.Contains(npcFilter, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (matches.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No NPCs found matching '{0}'.[/]", Markup.Escape(npcFilter));
                return;
            }

            if (matches.Count > 1)
            {
                AnsiConsole.MarkupLine("[yellow]Multiple NPCs match '{0}':[/]", Markup.Escape(npcFilter));
                foreach (var id in matches.Take(10))
                {
                    AnsiConsole.MarkupLine("  0x{0:X8}  {1}  ({2} lines)", id,
                        lookup.GetValueOrDefault(id, "?"), bySpeaker[id].Count);
                }

                return;
            }

            targetFormId = matches[0];
        }

        if (!bySpeaker.TryGetValue(targetFormId, out var npcDialogues))
        {
            AnsiConsole.MarkupLine("[yellow]NPC 0x{0:X8} has no dialogue lines.[/]", targetFormId);
            return;
        }

        var npcName = lookup.GetValueOrDefault(targetFormId, "?");
        RunNpcConversationTree(targetFormId, npcName, npcDialogues, result, lookup);
    }

    private static void RunNpcList(Dictionary<uint, List<DialogueRecord>> bySpeaker,
        Dictionary<uint, string> lookup)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]NPCs with Dialogue[/]").LeftJustified());
        AnsiConsole.WriteLine();

        // Count topics per speaker
        var rows = bySpeaker
            .Select(kvp =>
            {
                var topics = kvp.Value
                    .Where(d => d.TopicFormId is > 0)
                    .Select(d => d.TopicFormId!.Value)
                    .Distinct()
                    .Count();

                return new
                {
                    FormId = kvp.Key,
                    Name = lookup.GetValueOrDefault(kvp.Key, "(unknown)"),
                    Topics = topics,
                    Lines = kvp.Value.Count
                };
            })
            .OrderByDescending(r => r.Lines)
            .ToList();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("NPC Name");
        table.AddColumn(new TableColumn("FormID").RightAligned());
        table.AddColumn(new TableColumn("Topics").RightAligned());
        table.AddColumn(new TableColumn("Lines").RightAligned());

        foreach (var row in rows)
        {
            table.AddRow(Markup.Escape(row.Name), $"0x{row.FormId:X8}", $"{row.Topics:N0}", $"{row.Lines:N0}");
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[dim]{rows.Count} NPCs total[/]");
    }

    private static void RunNpcConversationTree(uint npcFormId, string npcName,
        List<DialogueRecord> npcDialogues, RecordCollection result, Dictionary<uint, string> lookup)
    {
        // Group by quest -> topic
        var questGroups = npcDialogues
            .GroupBy(d => d.QuestFormId ?? 0)
            .OrderByDescending(g => g.Count())
            .ToList();

        var topicCount = npcDialogues
            .Where(d => d.TopicFormId is > 0)
            .Select(d => d.TopicFormId!.Value)
            .Distinct()
            .Count();

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[blue]NPC: {Markup.Escape(npcName)} (0x{npcFormId:X8})[/]").LeftJustified());
        AnsiConsole.MarkupLine(
            $"Found in [green]{questGroups.Count}[/] quest(s), [green]{topicCount}[/] topic(s), [green]{npcDialogues.Count}[/] dialogue line(s)");
        AnsiConsole.WriteLine();

        // Build topic type lookup (GroupBy handles duplicate FormIds in dump scenarios)
        var topicTypeMap = result.DialogTopics
            .GroupBy(t => t.FormId)
            .ToDictionary(g => g.Key, g => g.First().TopicTypeName);
        var topicNameMap = result.DialogTopics
            .GroupBy(t => t.FormId)
            .ToDictionary(g => g.Key, g => g.First().FullName ?? g.First().EditorId ?? $"0x{g.Key:X8}");

        foreach (var questGroup in questGroups)
        {
            var questFormId = questGroup.Key;
            var questName = questFormId > 0
                ? lookup.GetValueOrDefault(questFormId, $"0x{questFormId:X8}")
                : "(No Quest)";

            AnsiConsole.Write(new Rule(
                $"[yellow]Quest: {Markup.Escape(questName)} (0x{questFormId:X8})[/]").LeftJustified());
            AnsiConsole.WriteLine();

            // Group by topic within quest
            var topicGroups = questGroup
                .GroupBy(d => d.TopicFormId ?? 0)
                .OrderBy(g =>
                {
                    // Sort by topic type: Conversation first, then Topic, then others
                    var typeName = g.Key > 0 ? topicTypeMap.GetValueOrDefault(g.Key, "Unknown") : "Unknown";
                    return typeName switch
                    {
                        "Conversation" => 0,
                        "Topic" => 1,
                        _ => 2
                    };
                })
                .ThenBy(g => g.Key)
                .ToList();

            foreach (var topicGroup in topicGroups)
            {
                var topicFormId = topicGroup.Key;
                var topicName = topicFormId > 0
                    ? topicNameMap.GetValueOrDefault(topicFormId, $"0x{topicFormId:X8}")
                    : "(Unlinked)";
                var topicType = topicFormId > 0
                    ? topicTypeMap.GetValueOrDefault(topicFormId, "?")
                    : "?";

                AnsiConsole.MarkupLine(
                    $"  [cyan]Topic: {Markup.Escape(topicName)} [[{Markup.Escape(topicType)}]] (0x{topicFormId:X8})[/]");

                // Order INFOs by PNAM chain or InfoIndex
                var infos = DialogueCommand.OrderInfoChain(topicGroup.ToList());
                var index = 1;

                foreach (var info in infos)
                {
                    var (speakerLabel, speakerColor) = DialogueCommand.ResolveSpeakerDisplay(info, lookup, topicTypeMap);
                    // In NPC tree context, fallback to the NPC name if no specific speaker
                    if (info.SpeakerFormId is not > 0 && speakerColor == "dim")
                    {
                        speakerLabel = npcName;
                        speakerColor = "green";
                    }

                    foreach (var resp in info.Responses)
                    {
                        var text = resp.Text ?? "(no text)";
                        var flags = DialogueCommand.BuildFlagString(info);
                        AnsiConsole.MarkupLine(
                            $"    [dim][[{index}]][/] [{speakerColor}]{Markup.Escape(speakerLabel)}:[/] \"{Markup.Escape(text)}\"{flags}");
                    }

                    if (info.Responses.Count == 0)
                    {
                        var flags = DialogueCommand.BuildFlagString(info);
                        AnsiConsole.MarkupLine(
                            $"    [dim][[{index}]][/] [{speakerColor}]{Markup.Escape(speakerLabel)}:[/] [dim](no response text)[/]{flags}");
                    }

                    // Show links
                    if (info.LinkToTopics.Count > 0)
                    {
                        var linkNames = info.LinkToTopics
                            .Select(id => topicNameMap.GetValueOrDefault(id, $"0x{id:X8}"))
                            .ToList();
                        AnsiConsole.MarkupLine(
                            $"        [dim]-> Links to: {Markup.Escape(string.Join(", ", linkNames))}[/]");
                    }

                    if (info.AddTopics.Count > 0)
                    {
                        var addNames = info.AddTopics
                            .Select(id => topicNameMap.GetValueOrDefault(id, $"0x{id:X8}"))
                            .ToList();
                        AnsiConsole.MarkupLine(
                            $"        [dim]+ Unlocks: {Markup.Escape(string.Join(", ", addNames))}[/]");
                    }

                    index++;
                }

                AnsiConsole.WriteLine();
            }
        }
    }
}
