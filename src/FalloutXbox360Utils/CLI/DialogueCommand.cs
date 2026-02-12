using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command for dialogue reconstruction diagnostics.
/// </summary>
public static class DialogueCommand
{
    public static Command Create()
    {
        var command = new Command("dialogue", "Dialogue reconstruction and diagnostics");

        command.Subcommands.Add(CreateStatsCommand());
        command.Subcommands.Add(CreateTreeCommand());
        command.Subcommands.Add(CreateTopicCommand());
        command.Subcommands.Add(CreateNpcCommand());
        command.Subcommands.Add(CreateUnattributedCommand());
        command.Subcommands.Add(CreateDebugCommand());

        return command;
    }

    private static Command CreateStatsCommand()
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

    private static Command CreateTreeCommand()
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

    private static async Task<(RecordCollection result, Dictionary<uint, string> formIdMap)?> LoadAndReconstructAsync(
        string input, CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return null;
        }

        AnsiConsole.MarkupLine("[blue]Loading:[/] {0}", Path.GetFileName(input));

        var isDump = Path.GetExtension(input).Equals(".dmp", StringComparison.OrdinalIgnoreCase);

        var analysisResult = await AnsiConsole.Progress()
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var taskLabel = isDump ? "Analyzing memory dump..." : "Analyzing ESM file...";
                var task = ctx.AddTask(taskLabel, maxValue: 100);
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Description = p.Phase;
                    task.Value = p.PercentComplete;
                });

                if (isDump)
                {
                    var analyzer = new MinidumpAnalyzer();
                    return await analyzer.AnalyzeAsync(input, progress, true, false, cancellationToken);
                }

                return await EsmFileAnalyzer.AnalyzeAsync(input, progress, cancellationToken);
            });

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ESM records found in file.");
            return null;
        }

        AnsiConsole.MarkupLine("[blue]Reconstructing dialogue...[/]");

        var fileInfo = new FileInfo(input);
        RecordCollection semanticResult;
        using (var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0,
                   MemoryMappedFileAccess.Read))
        using (var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read))
        {
            var parser = new RecordParser(
                analysisResult.EsmRecords, analysisResult.FormIdMap, accessor, fileInfo.Length,
                analysisResult.MinidumpInfo);
            semanticResult = parser.ReconstructAll();
        }

        return (semanticResult, analysisResult.FormIdMap);
    }

    private static async Task RunStatsAsync(string input, CancellationToken cancellationToken)
    {
        var loaded = await LoadAndReconstructAsync(input, cancellationToken);
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
            !HasAnySpeakerAttribution(d) &&
            d.TopicFormId is > 0 &&
            topicTypeMap.GetValueOrDefault(d.TopicFormId.Value) == "Radio");
        var genericLines = result.Dialogues.Count(d =>
            !HasAnySpeakerAttribution(d) &&
            !(d.TopicFormId is > 0 && topicTypeMap.GetValueOrDefault(d.TopicFormId.Value) == "Radio"));

        var total = result.Dialogues.Count;
        linkTable.AddRow("Linked to topic", $"{linkedToTopic:N0}", FormatPct(linkedToTopic, total));
        linkTable.AddRow("[red]Unlinked[/]", $"{unlinked:N0}", FormatPct(unlinked, total));
        linkTable.AddRow("With speaker (NPC)", $"{withSpeaker:N0}", FormatPct(withSpeaker, total));
        linkTable.AddRow("With faction speaker", $"{withFaction:N0}", FormatPct(withFaction, total));
        linkTable.AddRow("With race speaker", $"{withRace:N0}", FormatPct(withRace, total));
        linkTable.AddRow("With voice type speaker", $"{withVoiceType:N0}", FormatPct(withVoiceType, total));
        linkTable.AddRow("\U0001F4FB Radio station", $"{radioLines:N0}", FormatPct(radioLines, total));
        linkTable.AddRow("[green]Any speaker attribution[/]", $"{withAnySpeakerAttribution:N0}",
            FormatPct(withAnySpeakerAttribution, total));
        linkTable.AddRow("[dim]Generic / unattributed[/]", $"{genericLines:N0}", FormatPct(genericLines, total));
        linkTable.AddRow("With quest", $"{withQuest:N0}", FormatPct(withQuest, total));
        linkTable.AddRow("With response text", $"{withResponses:N0}", FormatPct(withResponses, total));

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

        // INFOs per topic distribution
        var infosPerTopic = result.DialogTopics
            .Select(t =>
            {
                var count = result.Dialogues.Count(d => d.TopicFormId == t.FormId);
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

    private static async Task RunTreeAsync(string input, int? limit, string? questFilter, string? output,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAndReconstructAsync(input, cancellationToken);
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
            var questFormId = ParseFormId(questFilter);
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

        var report = GeckReportGenerator.GenerateDialogueTreeReport(tree, lookup, result.FormIdToDisplayName);

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

    private static async Task RunTopicAsync(string input, string formIdStr, CancellationToken cancellationToken)
    {
        var formId = ParseFormId(formIdStr);
        if (formId == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid FormID: {0}", formIdStr);
            return;
        }

        var loaded = await LoadAndReconstructAsync(input, cancellationToken);
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

            if (info.AddTopics.Count > 0)
            {
                infoTable.AddRow("Add Topics (NAME)",
                    string.Join(", ", info.AddTopics.Select(id => $"0x{id:X8}")));
            }

            if (info.Difficulty > 0)
            {
                infoTable.AddRow("Difficulty", $"{info.Difficulty}");
            }

            AnsiConsole.Write(infoTable);
            AnsiConsole.WriteLine();
        }
    }

    private static Command CreateNpcCommand()
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

    private static async Task RunNpcAsync(string input, string? npcFilter, bool listMode,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAndReconstructAsync(input, cancellationToken);
        if (loaded == null)
        {
            return;
        }

        var (result, formIdMap) = loaded.Value;

        // Build lookup: FormID → display name
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

        // Find the NPC — try hex FormID first, then partial name match
        uint targetFormId = 0;
        if (npcFilter.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            uint.TryParse(npcFilter, NumberStyles.HexNumber, null, out _))
        {
            targetFormId = ParseFormId(npcFilter);
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
        // Group by quest → topic
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
                var infos = OrderInfoChain(topicGroup.ToList());
                var index = 1;

                foreach (var info in infos)
                {
                    var (speakerLabel, speakerColor) = ResolveSpeakerDisplay(info, lookup, topicTypeMap);
                    // In NPC tree context, fallback to the NPC name if no specific speaker
                    if (info.SpeakerFormId is not > 0 && speakerColor == "dim")
                    {
                        speakerLabel = npcName;
                        speakerColor = "green";
                    }

                    foreach (var resp in info.Responses)
                    {
                        var text = resp.Text ?? "(no text)";
                        var flags = BuildFlagString(info);
                        AnsiConsole.MarkupLine(
                            $"    [dim][[{index}]][/] [{speakerColor}]{Markup.Escape(speakerLabel)}:[/] \"{Markup.Escape(text)}\"{flags}");
                    }

                    if (info.Responses.Count == 0)
                    {
                        var flags = BuildFlagString(info);
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
                            $"        [dim]→ Links to: {Markup.Escape(string.Join(", ", linkNames))}[/]");
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

    private static string BuildFlagString(DialogueRecord info)
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

    private static List<DialogueRecord> OrderInfoChain(List<DialogueRecord> infos)
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

    private static string FormatPct(int count, int total)
    {
        return total > 0 ? $"{100.0 * count / total:F1}%" : "N/A";
    }

    private static Command CreateUnattributedCommand()
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
        var loaded = await LoadAndReconstructAsync(input, cancellationToken);
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
            $"\nUnattributed: [yellow]{unattributed.Count:N0}[/] / {total:N0} ([yellow]{FormatPct(unattributed.Count, total)}[/])");
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
            typeTable.AddRow(g.Key, $"{g.Count():N0}", FormatPct(g.Count(), unattributed.Count));
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
            FormatPct(noConditions, unattributed.Count));
        condTable.AddRow("Has CTDA, no speaker-like functions", $"{hasConditionsNoSpeaker:N0}",
            FormatPct(hasConditionsNoSpeaker, unattributed.Count));
        condTable.AddRow("Has unhandled speaker-like conditions", $"{hasSpeakerLike:N0}",
            FormatPct(hasSpeakerLike, unattributed.Count));

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

                var (speakerLabel, speakerColor) = ResolveSpeakerDisplay(info, lookup, topicTypeMap);

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

    private static Command CreateDebugCommand()
    {
        var command = new Command("debug", "Hex-dump TESTopicInfo struct bytes for offset debugging");

        var inputArg = new Argument<string>("input") { Description = "Path to memory dump (.dmp) file" };
        var countOpt = new Option<int?>("--count") { Description = "Number of records to dump (default 20)" };

        command.Arguments.Add(inputArg);
        command.Options.Add(countOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var count = parseResult.GetValue(countOpt) ?? 20;
            await RunDebugAsync(input, count, cancellationToken);
        });

        return command;
    }

    private static async Task RunDebugAsync(string input, int count, CancellationToken cancellationToken)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        AnsiConsole.MarkupLine("[blue]Loading dump for TESTopicInfo debug...[/]");

        // Use MinidumpAnalyzer directly — this command is dump-only
        var analyzer = new MinidumpAnalyzer();
        var analysisResult = await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Analyzing memory dump...", maxValue: 100);
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Description = p.Phase;
                    task.Value = p.PercentComplete;
                });
                return await analyzer.AnalyzeAsync(input, progress, true, false, cancellationToken);
            });

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ESM records found in dump.");
            return;
        }

        // Use RuntimeEditorIds directly — entries with DialogueLine set are confirmed INFO entries
        // (the hash table walker already detected INFO FormType and read prompts in pass 2)
        var infoEntries = analysisResult.EsmRecords.RuntimeEditorIds
            .Where(e => e.TesFormOffset.HasValue && e.DialogueLine != null)
            .Take(count)
            .ToList();

        // Also collect ALL INFO entries (with or without DialogueLine) for flag distribution
        // Detect the INFO FormType from entries that have DialogueLine
        byte? infoFormType = infoEntries.Count > 0 ? infoEntries[0].FormType : null;
        var allInfoEntries = infoFormType.HasValue
            ? analysisResult.EsmRecords.RuntimeEditorIds
                .Where(e => e.TesFormOffset.HasValue && e.FormType == infoFormType.Value)
                .ToList()
            : [];

        var fileInfo = new FileInfo(input);
        using var mmf = MemoryMappedFile.CreateFromFile(input, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        AnsiConsole.MarkupLine($"[dim]Runtime entries total: {analysisResult.EsmRecords.RuntimeEditorIds.Count}[/]");
        AnsiConsole.MarkupLine($"[dim]INFO entries (with prompt): {infoEntries.Count}[/]");
        AnsiConsole.MarkupLine(
            $"[dim]INFO entries (all, FormType 0x{infoFormType ?? 0:X2}): {allInfoEntries.Count}[/]");

        if (infoEntries.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No INFO entries with DialogueLine found in RuntimeEditorIds.");
            return;
        }

        // PART 1: Full 80-byte struct dump for first 3 entries (field identification)
        AnsiConsole.MarkupLine("\n[blue]Full struct dump (first 3 entries):[/]");
        AnsiConsole.WriteLine();

        foreach (var entry in infoEntries.Take(3))
        {
            var offset = entry.TesFormOffset!.Value;
            if (offset + 80 > fileInfo.Length)
            {
                continue;
            }

            var fullBuf = new byte[80];
            accessor.ReadArray(offset, fullBuf, 0, 80);

            AnsiConsole.MarkupLine(
                $"[yellow]0x{entry.FormId:X8}[/] ({Markup.Escape(entry.EditorId)}) at file+0x{offset:X}");

            // Dump in rows of 16 bytes with field annotations
            for (var row = 0; row < 80; row += 16)
            {
                var rowLen = Math.Min(16, 80 - row);
                var hex = string.Join(" ", Enumerable.Range(row, rowLen).Select(i => $"{fullBuf[i]:X2}"));
                var ascii = string.Concat(Enumerable.Range(row, rowLen).Select(i =>
                    fullBuf[i] >= 32 && fullBuf[i] < 127 ? (char)fullBuf[i] : '.'));

                AnsiConsole.MarkupLine($"  [dim]+{row,2:D2}[/]: {hex,-48} {Markup.Escape(ascii)}");
            }

            // Annotate known fields
            var fid = BinaryUtils.ReadUInt32BE(fullBuf, 12);
            var idx36 = BinaryUtils.ReadUInt16BE(fullBuf, 36);
            var prompt44 = BinaryUtils.ReadUInt32BE(fullBuf, 44);
            var speaker64 = BinaryUtils.ReadUInt32BE(fullBuf, 64);
            var diff72 = BinaryUtils.ReadUInt32BE(fullBuf, 72);
            var quest76 = BinaryUtils.ReadUInt32BE(fullBuf, 76);

            AnsiConsole.MarkupLine($"  [dim]Fields: FormType=0x{fullBuf[4]:X2} FormID=0x{fid:X8}[/]");
            AnsiConsole.MarkupLine($"  [dim]  +36: 0x{idx36:X4} (iInfoIndex?)  +44: 0x{prompt44:X8} (cPrompt ptr)[/]");
            AnsiConsole.MarkupLine($"  [dim]  +64: 0x{speaker64:X8} (pSpeaker?)  +72: 0x{diff72:X8} (difficulty?)[/]");
            AnsiConsole.MarkupLine($"  [dim]  +76: 0x{quest76:X8} (pQuest?)[/]");
            AnsiConsole.MarkupLine($"  [dim]  Prompt: \"{Markup.Escape(entry.DialogueLine ?? "")}\"[/]");
            AnsiConsole.WriteLine();
        }

        // PART 2: Find bytes that VARY across entries (to distinguish per-record data from constants)
        AnsiConsole.Write(new Rule("[blue]Byte Variance Analysis (across all INFO entries)[/]").LeftJustified());

        var varianceTracker = new HashSet<byte>[80];
        for (var i = 0; i < 80; i++)
        {
            varianceTracker[i] = [];
        }

        var sampledCount = 0;
        foreach (var entry in allInfoEntries.Take(500))
        {
            var offset = entry.TesFormOffset!.Value;
            if (offset + 80 > fileInfo.Length)
            {
                continue;
            }

            var buf = new byte[80];
            accessor.ReadArray(offset, buf, 0, 80);
            for (var i = 0; i < 80; i++)
            {
                varianceTracker[i].Add(buf[i]);
            }

            sampledCount++;
        }

        AnsiConsole.MarkupLine($"  Sampled {sampledCount} entries. Showing byte positions by uniqueness:");
        AnsiConsole.WriteLine();

        // Group bytes by category: constant (1 value), low variance (2-5), per-record (6+)
        var constant = new List<string>();
        var lowVar = new List<string>();
        var perRecord = new List<string>();

        for (var i = 0; i < 80; i++)
        {
            var uniqueCount = varianceTracker[i].Count;
            var sampleVal = varianceTracker[i].First();

            if (uniqueCount == 1)
            {
                constant.Add($"+{i}=0x{sampleVal:X2}");
            }
            else if (uniqueCount <= 5)
            {
                lowVar.Add($"+{i}({uniqueCount} vals)");
            }
            else
            {
                perRecord.Add($"+{i}");
            }
        }

        AnsiConsole.MarkupLine($"  [dim]Constant (1 value):[/] {string.Join(", ", constant)}");
        AnsiConsole.MarkupLine($"  [yellow]Low variance (2-5 values):[/] {string.Join(", ", lowVar)}");
        AnsiConsole.MarkupLine($"  [green]Per-record (6+ values):[/] {string.Join(", ", perRecord)}");

        // PART 3: For low-variance bytes, show the actual value distribution (these are likely flags)
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Low-Variance Byte Details (likely flags/enums)[/]").LeftJustified());

        for (var i = 0; i < 80; i++)
        {
            var uniqueCount = varianceTracker[i].Count;
            if (uniqueCount >= 2 && uniqueCount <= 10)
            {
                // Count each value
                var valCounts = new Dictionary<byte, int>();
                foreach (var entry in allInfoEntries.Take(500))
                {
                    var offset = entry.TesFormOffset!.Value;
                    if (offset + 80 > fileInfo.Length)
                    {
                        continue;
                    }

                    var val = accessor.ReadByte(offset + i);
                    valCounts.TryGetValue(val, out var c);
                    valCounts[val] = c + 1;
                }

                var distStr = string.Join(", ",
                    valCounts.OrderByDescending(kv => kv.Value)
                        .Select(kv => $"0x{kv.Key:X2}={kv.Value}"));
                AnsiConsole.MarkupLine($"  +{i}: {distStr}");
            }
        }

        // Flag distribution for ALL INFO entries (not just the sampled ones)
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Flag Distribution — ALL INFO entries (current offset 39)[/]")
            .LeftJustified());
        ShowFlagDistribution(allInfoEntries, accessor, fileInfo.Length, 39);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Flag Distribution — ALL INFO entries (alternative offset 40)[/]")
            .LeftJustified());
        ShowFlagDistribution(allInfoEntries, accessor, fileInfo.Length, 40);
    }

    private static string DecodeFlagNames(byte flags)
    {
        var names = new List<string>();
        if ((flags & 0x01) != 0)
        {
            names.Add("Goodbye");
        }

        if ((flags & 0x02) != 0)
        {
            names.Add("Random");
        }

        if ((flags & 0x04) != 0)
        {
            names.Add("RandEnd");
        }

        if ((flags & 0x10) != 0)
        {
            names.Add("SayOnce");
        }

        if ((flags & 0x80) != 0)
        {
            names.Add("Speech");
        }

        return names.Count > 0 ? string.Join("|", names) : "none";
    }

    private static void ShowFlagDistribution(
        List<RuntimeEditorIdEntry> entries,
        MemoryMappedViewAccessor accessor,
        long fileSize,
        int dataOffset)
    {
        var flagCounts = new Dictionary<byte, int>();
        var total = 0;
        foreach (var entry in entries)
        {
            var offset = entry.TesFormOffset!.Value;
            if (offset + dataOffset + 4 > fileSize)
            {
                continue;
            }

            var flags = accessor.ReadByte(offset + dataOffset + 2); // +2 to get flags within TOPIC_INFO_DATA
            flagCounts.TryGetValue(flags, out var c);
            flagCounts[flags] = c + 1;
            total++;
        }

        foreach (var (flags, cnt) in flagCounts.OrderByDescending(kv => kv.Value))
        {
            var pct = 100.0 * cnt / total;
            AnsiConsole.MarkupLine(
                $"  0x{flags:X2} ({DecodeFlagNames(flags),-30}): {cnt,5} ({pct:F1}%)");
        }
    }

    private static uint ParseFormId(string str)
    {
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            str = str[2..];
        }

        return uint.TryParse(str, NumberStyles.HexNumber, null, out var result)
            ? result
            : 0;
    }

    private static bool HasAnySpeakerAttribution(DialogueRecord d)
    {
        return d.SpeakerFormId is > 0 || d.SpeakerFactionFormId is > 0 ||
               d.SpeakerRaceFormId is > 0 || d.SpeakerVoiceTypeFormId is > 0;
    }

    /// <summary>
    ///     Resolve the best display label for a dialogue line's speaker.
    ///     Returns (label, spectre_color) for rendering.
    /// </summary>
    private static (string Label, string Color) ResolveSpeakerDisplay(
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

        // Radio topic → radio glyph + station name
        if (info.TopicFormId is > 0 &&
            topicTypeMap.GetValueOrDefault(info.TopicFormId.Value) == "Radio")
        {
            var stationName = info.QuestFormId is > 0
                ? lookup.GetValueOrDefault(info.QuestFormId.Value, "Radio Station")
                : "Radio Station";
            return ($"\U0001F4FB {stationName}", "yellow");
        }

        // Faction/voice type/race speaker → generic response
        if (info.SpeakerFactionFormId is > 0 || info.SpeakerVoiceTypeFormId is > 0 ||
            info.SpeakerRaceFormId is > 0)
        {
            return ("Generic Response", "dim");
        }

        // No speaker data at all
        return ("Generic Response", "dim");
    }
}
