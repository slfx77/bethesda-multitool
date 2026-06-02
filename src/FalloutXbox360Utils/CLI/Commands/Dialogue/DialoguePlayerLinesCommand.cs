using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dialogue;

/// <summary>
///     CLI command that lists every dialogue line the player can say TO a given NPC.
///     Player choices come from parent DIAL FULL (menu prompt) and INFO PromptText (per-INFO override).
/// </summary>
internal static class DialoguePlayerLinesCommand
{
    internal static Command CreatePlayerLinesCommand()
    {
        var command = new Command("player-lines",
            "List the lines the player can say to an NPC (topic prompts + INFO overrides)");

        var inputArg = new Argument<string>("input") { Description = "Path to ESM or DMP file" };
        var npcArg = new Argument<string>("npc")
            { Description = "NPC FormID (hex) or partial name (e.g. Ulysses)" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Write plain-text report to file" };
        var includePromptlessOpt = new Option<bool>("--include-promptless")
            { Description = "Include topics even when no INFO has a player-prompt override" };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(npcArg);
        command.Options.Add(outputOpt);
        command.Options.Add(includePromptlessOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var npc = parseResult.GetValue(npcArg)!;
            var output = parseResult.GetValue(outputOpt);
            var includePromptless = parseResult.GetValue(includePromptlessOpt);
            await RunAsync(input, npc, output, includePromptless, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(string input, string npcFilter, string? outputPath,
        bool includePromptless, CancellationToken cancellationToken)
    {
        var loaded = await DialogueCommand.LoadAndParseAsync(input, cancellationToken);
        if (loaded == null)
        {
            return;
        }

        var (result, formIdMap) = loaded.Value;

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

        var bySpeaker = result.Dialogues
            .Where(d => d.SpeakerFormId is > 0)
            .GroupBy(d => d.SpeakerFormId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var npcFormId = ResolveNpcFormId(npcFilter, bySpeaker, lookup);
        if (npcFormId == 0)
        {
            return;
        }

        if (!bySpeaker.TryGetValue(npcFormId, out var npcDialogues))
        {
            AnsiConsole.MarkupLine("[yellow]NPC 0x{0:X8} has no dialogue lines in this file.[/]", npcFormId);
            return;
        }

        var npcName = lookup.GetValueOrDefault(npcFormId, $"0x{npcFormId:X8}");

        var topicById = result.DialogTopics
            .GroupBy(t => t.FormId)
            .ToDictionary(g => g.Key, g => g.First());

        if (string.IsNullOrEmpty(outputPath))
        {
            Render(npcFormId, npcName, npcDialogues, topicById, lookup, includePromptless, AnsiConsole.Console);
            return;
        }

        // -o: render straight to a file-backed wide console (no double render, no terminal wrapping).
        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream);
        var fileConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        fileConsole.Profile.Width = 240;
        Render(npcFormId, npcName, npcDialogues, topicById, lookup, includePromptless, fileConsole);
        await writer.FlushAsync(cancellationToken);
        AnsiConsole.MarkupLine("[green]Report written to:[/] {0}", outputPath);
    }

    private static uint ResolveNpcFormId(string npcFilter,
        Dictionary<uint, List<DialogueRecord>> bySpeaker, Dictionary<uint, string> lookup)
    {
        if (npcFilter.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            uint.TryParse(npcFilter, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            var parsed = CliHelpers.ParseFormId(npcFilter) ?? 0;
            if (parsed != 0)
            {
                return parsed;
            }
        }

        var matches = bySpeaker.Keys
            .Where(id =>
            {
                var name = lookup.GetValueOrDefault(id);
                return name != null && name.Contains(npcFilter, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (matches.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No NPCs found matching '{0}'.[/]", Markup.Escape(npcFilter));
            return 0;
        }

        if (matches.Count > 1)
        {
            AnsiConsole.MarkupLine("[yellow]Multiple NPCs match '{0}':[/]", Markup.Escape(npcFilter));
            foreach (var id in matches.Take(10))
            {
                AnsiConsole.MarkupLine("  0x{0:X8}  {1}  ({2} lines)", id,
                    lookup.GetValueOrDefault(id, "?"), bySpeaker[id].Count);
            }

            return 0;
        }

        return matches[0];
    }

    private static void Render(uint npcFormId, string npcName,
        List<DialogueRecord> npcDialogues,
        Dictionary<uint, DialogTopicRecord> topicById,
        Dictionary<uint, string> lookup,
        bool includePromptless,
        IAnsiConsole console)
    {
        console.WriteLine();
        console.Write(new Rule($"[blue]Player lines to: {Markup.Escape(npcName)} (0x{npcFormId:X8})[/]")
            .LeftJustified());
        console.WriteLine();

        var totalTopics = 0;
        var totalPromptOverrides = 0;

        var questGroups = npcDialogues
            .GroupBy(d => d.QuestFormId ?? 0)
            .OrderByDescending(g => g.Select(d => d.TopicFormId ?? 0).Distinct().Count())
            .ToList();

        foreach (var questGroup in questGroups)
        {
            var questFormId = questGroup.Key;
            var questName = questFormId > 0
                ? lookup.GetValueOrDefault(questFormId, $"0x{questFormId:X8}")
                : "(No Quest)";

            // Group this quest's INFOs by parent topic.
            var topicGroups = questGroup
                .Where(d => d.TopicFormId is > 0)
                .GroupBy(d => d.TopicFormId!.Value)
                .OrderBy(g =>
                {
                    var t = topicById.GetValueOrDefault(g.Key);
                    return t?.TopicTypeName switch
                    {
                        "Topic" => 0,
                        "Conversation" => 1,
                        _ => 2
                    };
                })
                .ThenBy(g =>
                {
                    var t = topicById.GetValueOrDefault(g.Key);
                    return t?.FullName ?? t?.EditorId ?? $"0x{g.Key:X8}";
                }, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Filter to topics that actually surface a player-choosable line.
            var renderable = new List<(DialogTopicRecord? Topic, uint TopicFormId, List<DialogueRecord> Infos,
                string? MenuText, List<string> PromptOverrides)>();
            foreach (var tg in topicGroups)
            {
                var topic = topicById.GetValueOrDefault(tg.Key);
                var menuText = topic?.FullName;
                if (string.IsNullOrEmpty(menuText))
                {
                    menuText = topic?.DummyPrompt;
                }

                var promptOverrides = tg
                    .Select(i => i.PromptText)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p!.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var hasPlayerLine = !string.IsNullOrWhiteSpace(menuText) || promptOverrides.Count > 0;
                if (!hasPlayerLine && !includePromptless)
                {
                    continue;
                }

                renderable.Add((topic, tg.Key, tg.ToList(), menuText, promptOverrides));
            }

            if (renderable.Count == 0)
            {
                continue;
            }

            console.Write(new Rule(
                $"[yellow]Quest: {Markup.Escape(questName)} (0x{questFormId:X8})[/]").LeftJustified());
            console.WriteLine();

            foreach (var (topic, topicFormId, _, menuText, promptOverrides) in renderable)
            {
                totalTopics++;
                totalPromptOverrides += promptOverrides.Count;

                var editorId = topic?.EditorId ?? "(no edid)";
                var typeName = topic?.TopicTypeName ?? "?";
                var display = string.IsNullOrWhiteSpace(menuText) ? "(no menu text)" : $"\"{menuText}\"";

                console.MarkupLine($"  [cyan]{Markup.Escape(display)}[/]");
                console.MarkupLine(
                    $"      [dim][[{Markup.Escape(typeName)}]] {Markup.Escape(editorId)}  0x{topicFormId:X8}[/]");

                foreach (var prompt in promptOverrides)
                {
                    console.MarkupLine($"      [white]→[/] \"{Markup.Escape(prompt)}\"  [dim](prompt)[/]");
                }
            }

            console.WriteLine();
        }

        console.MarkupLine(
            $"[green]{totalTopics}[/] topic(s), [green]{totalPromptOverrides}[/] prompt override(s) " +
            "where the player can speak to this NPC.");
    }
}
