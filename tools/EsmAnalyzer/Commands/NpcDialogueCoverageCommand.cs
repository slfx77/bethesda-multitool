using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Expected-versus-resolved dialogue coverage fixture for one NPC (the Ahdarji acceptance check).
///     EXPECTED is recomputed from raw INFO conditions: every INFO carrying a positive GetIsID
///     condition naming the NPC, independent of the single-slot SpeakerFormId attribution (which is
///     first-condition-wins and can lock onto a different NPC on shared INFOs). RESOLVED replays the
///     viewer's own path: DialogueTree → DialogueMetadataBuilder.BuildTopicsBySpeaker →
///     DialogueViewerHelper.FilterInfoChain. The report lists per-topic counts and every
///     expected-but-unresolved INFO with a reason, so a nonzero result is never mistaken for
///     complete coverage.
/// </summary>
internal static class NpcDialogueCoverageCommand
{
    private const ushort GetIsIdFunction = 0x48;
    private const ushort GetIsRaceFunction = 0x45;
    private const ushort GetInFactionFunction = 0x47;

    internal static Command Create()
    {
        var command = new Command("npc-dialogue",
            "Expected-vs-resolved dialogue coverage for one NPC (viewer-path replay + raw-condition ground truth)");
        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var npcArg = new Argument<string>("npc") { Description = "NPC EditorID or 0xFormID (e.g. Ahdarji)" };
        var detailOpt = new Option<int>("--detail", "-n")
        {
            Description = "How many missing/extra INFOs to list in detail",
            DefaultValueFactory = _ => 20
        };
        command.Arguments.Add(fileArg);
        command.Arguments.Add(npcArg);
        command.Options.Add(detailOpt);
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(npcArg)!,
            parseResult.GetValue(detailOpt)));
        return command;
    }

    private static int Execute(string filePath, string npcText, int detail)
    {
        using var result = UnifiedAnalyzer.AnalyzeAsync(filePath).GetAwaiter().GetResult();
        var records = result.Records;

        var npc = ResolveNpc(records, npcText);
        if (npc is null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] NPC '{Markup.Escape(npcText)}' not found");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[cyan]File:[/] {Path.GetFileName(filePath)}  [cyan]NPC:[/] 0x{npc.Value.FormId:X8} " +
            $"{Markup.Escape(npc.Value.EditorId ?? "?")} \"{Markup.Escape(npc.Value.FullName ?? "")}\"");
        AnsiConsole.WriteLine();

        // EXPECTED — ground truth from raw INFO conditions, not the single-slot attribution.
        var expected = new Dictionary<uint, DialogueRecord>();
        var speakerSlotElsewhere = 0;
        foreach (var info in records.Dialogues)
        {
            if (!HasPositiveGetIsId(info, npc.Value.FormId))
            {
                continue;
            }

            expected.TryAdd(info.FormId, info);
            if (info.SpeakerFormId is { } slot && slot != npc.Value.FormId)
            {
                speakerSlotElsewhere++;
            }
        }

        var expectedTopics = expected.Values
            .Select(i => i.TopicFormId ?? 0)
            .Where(t => t != 0)
            .ToHashSet();
        var expectedResponses = expected.Values.Sum(i => i.Responses.Count);

        AnsiConsole.MarkupLine(
            $"[green]EXPECTED (positive GetIsID conditions):[/] topics={expectedTopics.Count} " +
            $"INFOs={expected.Count} responses={expectedResponses} " +
            $"(speaker-slot locked to another NPC on {speakerSlotElsewhere} INFOs)");

        // RESOLVED — replay the viewer path exactly.
        if (records.DialogueTree is not { } tree)
        {
            AnsiConsole.MarkupLine("[red]No dialogue tree built for this file.[/]");
            return 1;
        }

        var npcsWithFullName = records.Npcs
            .Where(n => !string.IsNullOrEmpty(n.FullName))
            .Select(n => n.FormId)
            .Concat(records.GenericRecords
                .Where(g => g.RecordType == "NPC_" && !string.IsNullOrEmpty(g.FullName))
                .Select(g => g.FormId))
            .ToHashSet();
        var topicsBySpeaker =
            global::BethesdaMultitool.DialogueMetadataBuilder.BuildTopicsBySpeaker(tree, npcsWithFullName);
        var pickerTopics = topicsBySpeaker.GetValueOrDefault(npc.Value.FormId) ?? [];

        var resolved = new Dictionary<uint, (TopicDialogueNode Topic, DialogueRecord Info)>();
        foreach (var topic in pickerTopics)
        {
            var filtered = DialogueViewerHelper.FilterInfoChain(
                topic.InfoChain, questFilter: null, speakerFilter: npc.Value.FormId);
            foreach (var node in filtered)
            {
                resolved.TryAdd(node.Info.FormId, (topic, node.Info));
            }
        }

        var resolvedResponses = resolved.Values.Sum(v => v.Info.Responses.Count);
        AnsiConsole.MarkupLine(
            $"[green]RESOLVED (viewer path):[/] picker topics={pickerTopics.Count} " +
            $"INFOs={resolved.Count} responses={resolvedResponses}");
        AnsiConsole.WriteLine();

        // Deltas — the acceptance signal.
        var missing = expected.Keys.Where(id => !resolved.ContainsKey(id)).ToList();
        var extra = resolved.Keys.Where(id => !expected.ContainsKey(id)).ToList();
        var verdict = missing.Count == 0 ? "[green]COMPLETE[/]" : $"[red]MISSING {missing.Count}[/]";
        AnsiConsole.MarkupLine(
            $"[cyan]Coverage verdict:[/] {verdict}  " +
            $"(extra-shown INFOs from faction/race/unconditioned attribution: {extra.Count})");

        var topicById = new Dictionary<uint, TopicDialogueNode>();
        void IndexAll(IEnumerable<TopicDialogueNode> topics)
        {
            foreach (var t in topics) topicById.TryAdd(t.TopicFormId, t);
        }

        foreach (var quest in tree.QuestTrees.Values) IndexAll(quest.Topics);
        IndexAll(tree.OrphanTopics);

        foreach (var id in missing.Take(detail))
        {
            var info = expected[id];
            var topicId = info.TopicFormId ?? 0;
            var reason = topicId == 0
                ? "INFO has no parent topic"
                : !topicById.ContainsKey(topicId)
                    ? "topic absent from dialogue tree"
                    : pickerTopics.All(t => t.TopicFormId != topicId)
                        ? "topic not indexed for this speaker"
                        : info.SpeakerFormId != npc.Value.FormId
                            ? $"speaker slot locked to 0x{info.SpeakerFormId ?? 0:X8} (shared INFO)"
                            : "filtered out of the info chain";
            var topicName = topicById.TryGetValue(topicId, out var tn)
                ? tn.Topic?.EditorId ?? $"0x{topicId:X8}"
                : $"0x{topicId:X8}";
            AnsiConsole.MarkupLine(
                $"  [red]MISSING[/] INFO 0x{id:X8} topic={Markup.Escape(topicName)} " +
                $"responses={info.Responses.Count}: {reason}");
        }

        if (missing.Count > detail)
        {
            AnsiConsole.MarkupLine($"  … and {missing.Count - detail} more");
        }

        // Shared-eligibility tier: lines the NPC can SPEAK in-game via race/faction conditions.
        // These are shared across many NPCs and attribute to the per-NPC picker only through
        // fallback paths — reported as context so "COMPLETE" above is never read as "every line
        // the NPC utters in-game", per the acceptance rail.
        var (raceFormId, factionFormIds) = ResolveRaceAndFactions(records, npc.Value.FormId);
        if (raceFormId is > 0 || factionFormIds.Count > 0)
        {
            var raceEligible = raceFormId is { } race and > 0
                ? records.Dialogues.Count(i => HasPositiveCondition(i, GetIsRaceFunction, race))
                : 0;
            var factionEligible = factionFormIds.Count > 0
                ? records.Dialogues.Count(i =>
                    factionFormIds.Any(f => HasPositiveCondition(i, GetInFactionFunction, f)))
                : 0;
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[cyan]Shared eligibility (context, not per-NPC picker content):[/] " +
                $"race-conditioned INFOs={raceEligible} (race 0x{raceFormId ?? 0:X8}), " +
                $"faction-conditioned INFOs={factionEligible} ({factionFormIds.Count} factions)");
        }

        // Per-topic breakdown for the resolved side (what the picker actually shows).
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[cyan]Resolved per-topic breakdown:[/]");
        foreach (var group in resolved.Values
                     .GroupBy(v => v.Topic.TopicFormId)
                     .OrderByDescending(g => g.Count()))
        {
            var t = group.First().Topic;
            AnsiConsole.MarkupLine(
                $"  {Markup.Escape(t.Topic?.EditorId ?? $"0x{t.TopicFormId:X8}")}: " +
                $"{group.Count()} INFOs, {group.Sum(v => v.Info.Responses.Count)} responses");
        }

        return missing.Count == 0 ? 0 : 2;
    }

    private static bool HasPositiveGetIsId(DialogueRecord info, uint npcFormId) =>
        HasPositiveCondition(info, GetIsIdFunction, npcFormId);

    private static bool HasPositiveCondition(DialogueRecord info, ushort function, uint parameter)
    {
        foreach (var c in info.Conditions)
        {
            if (c.FunctionIndex != function || c.Parameter1 != parameter)
            {
                continue;
            }

            // Mirror OblivionDialogueExtractor.ParseCondition's positivity rule.
            var compOp = (c.Type >> 5) & 0x7;
            var isPositive = (compOp is 0 or 3 && c.ComparisonValue >= 0.99f) ||
                             (compOp is 1 or 2 && c.ComparisonValue < 0.01f);
            if (isPositive)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Pulls the NPC's race (RNAM) and faction memberships (SNAM) from the schema-decoded NPC_
    ///     tree (Oblivion path) or the typed NpcRecord when present.
    /// </summary>
    private static (uint? Race, List<uint> Factions) ResolveRaceAndFactions(
        BethesdaMultitool.Core.Formats.Esm.Models.RecordCollection records, uint npcFormId)
    {
        var generic = records.GenericRecords.FirstOrDefault(g =>
            g.RecordType == "NPC_" && g.FormId == npcFormId);
        if (generic?.DecodedTree is not { } tree)
        {
            return (null, []);
        }

        uint? race = null;
        var factions = new List<uint>();

        void Walk(IEnumerable<BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding.DecodedNode> nodes)
        {
            foreach (var node in nodes)
            {
                switch (node.Signature)
                {
                    case "RNAM":
                        race ??= node.FormId ?? node.Children.Select(c => c.FormId).FirstOrDefault(f => f is > 0);
                        break;
                    case "SNAM":
                    {
                        var factionId = node.FormId
                            ?? node.Children.Select(c => c.FormId).FirstOrDefault(f => f is > 0);
                        if (factionId is { } f and > 0)
                        {
                            factions.Add(f);
                        }

                        break;
                    }
                }

                if (node.Children.Count > 0)
                {
                    Walk(node.Children);
                }
            }
        }

        Walk(tree);
        return (race, factions);
    }

    /// <summary>
    ///     Resolves the NPC from either the typed Npcs list (FNV-family) or the schema-decoded
    ///     GenericRecords (Oblivion and other schema-first games route NPC_ there).
    /// </summary>
    private static (uint FormId, string? EditorId, string? FullName)? ResolveNpc(
        BethesdaMultitool.Core.Formats.Esm.Models.RecordCollection records, string text)
    {
        uint? wantedFormId = null;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            wantedFormId = parsed;
        }

        bool Matches(uint formId, string? editorId) => wantedFormId is { } id
            ? formId == id
            : string.Equals(editorId, text, StringComparison.OrdinalIgnoreCase);

        var typed = records.Npcs.FirstOrDefault(n => Matches(n.FormId, n.EditorId));
        if (typed is not null)
        {
            return (typed.FormId, typed.EditorId, typed.FullName);
        }

        var generic = records.GenericRecords.FirstOrDefault(g =>
            g.RecordType == "NPC_" && Matches(g.FormId, g.EditorId));
        return generic is not null ? (generic.FormId, generic.EditorId, generic.FullName) : null;
    }
}
