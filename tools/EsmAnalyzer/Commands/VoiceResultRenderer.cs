using Spectre.Console;
using static EsmAnalyzer.Commands.VoiceFileMatcher;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Renders voice heuristics results using Spectre.Console tables.
/// </summary>
internal static class VoiceResultRenderer
{
    internal static void RenderEsmIndex(
        Dictionary<uint, InfoData> infoEntries,
        Dictionary<string, DialData> dialEntries,
        HashSet<uint> npcFormIds,
        HashSet<uint> questFormIds,
        Dictionary<uint, string> vtypEditorIds,
        Dictionary<uint, uint> npcVoiceTypes)
    {
        var esmTable = new Table().Border(TableBorder.Rounded).Title("[bold]ESM Index[/]");
        esmTable.AddColumn("Record Type");
        esmTable.AddColumn(new TableColumn("Count").RightAligned());
        esmTable.AddRow("INFO records", $"{infoEntries.Count:N0}");
        esmTable.AddRow("DIAL topics", $"{dialEntries.Count:N0}");
        esmTable.AddRow("NPC_ records", $"{npcFormIds.Count:N0}");
        esmTable.AddRow("QUST records", $"{questFormIds.Count:N0}");
        esmTable.AddRow("VTYP records", $"{vtypEditorIds.Count:N0}");
        esmTable.AddRow("NPCs with VTCK", $"{npcVoiceTypes.Count:N0}");
        AnsiConsole.Write(esmTable);
        AnsiConsole.WriteLine();
    }

    internal static void RenderVoiceFilesSummary(
        List<VoiceFile> voiceFiles,
        HashSet<uint> uniqueFormIds,
        HashSet<string> uniqueTopics)
    {
        var uniqueVoiceTypeNames = voiceFiles.Select(v => v.VoiceType)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var voiceTable = new Table().Border(TableBorder.Rounded).Title("[bold]Voice Files[/]");
        voiceTable.AddColumn("Metric");
        voiceTable.AddColumn(new TableColumn("Count").RightAligned());
        voiceTable.AddRow("Total voice files", $"{voiceFiles.Count:N0}");
        voiceTable.AddRow("Unique FormIDs", $"{uniqueFormIds.Count:N0}");
        voiceTable.AddRow("Unique TopicEditorIds", $"{uniqueTopics.Count:N0}");
        voiceTable.AddRow("Unique VoiceType folders", $"{uniqueVoiceTypeNames.Count:N0}");
        AnsiConsole.Write(voiceTable);
        AnsiConsole.WriteLine();
    }

    internal static void RenderMatchResults(
        MatchResults results,
        int total,
        HashSet<string> uniqueTopics,
        List<KeyValuePair<string, List<string>>> uniqueVoiceTypes,
        List<KeyValuePair<string, List<string>>> sharedVoiceTypes)
    {
        var matchTable = new Table().Border(TableBorder.Rounded).Title("[bold]Matching Results[/]");
        matchTable.AddColumn("Metric");
        matchTable.AddColumn(new TableColumn("Result").RightAligned());

        matchTable.AddRow("[bold]FormID -> INFO match[/]", Frac(results.FormIdMatched, total));
        matchTable.AddRow("  with NAM1 (subtitle)", $"{results.FormIdWithNam1:N0}");
        matchTable.AddRow("  with ANAM (speaker ref)", $"{results.FormIdWithAnam:N0}");
        matchTable.AddRow("  with QSTI (quest ref)", $"{results.FormIdWithQsti:N0}");
        matchTable.AddRow("", "");

        var topicTotal = uniqueTopics.Count;
        matchTable.AddRow("[bold]TopicEditorId -> DIAL match[/]", "");
        matchTable.AddRow("  [red]Case-sensitive[/]", Frac(results.TopicsCaseSensitive, topicTotal));
        matchTable.AddRow("  [green]Case-insensitive[/]", Frac(results.TopicsCaseInsensitive, topicTotal));
        matchTable.AddRow("  with QSTI (quest ref)", $"{results.TopicsWithQsti:N0}");
        matchTable.AddRow("  with TNAM (speaker ref)", $"{results.TopicsWithTnam:N0}");
        matchTable.AddRow("", "");

        matchTable.AddRow("[bold]VoiceType folder -> VTYP match[/]", "");
        matchTable.AddRow("  [green]Unique NPC (1:1)[/]", Frac(results.VtypMatchedUnique, total));
        matchTable.AddRow("  Shared (multiple NPCs)", Frac(results.VtypMatchedShared, total));
        matchTable.AddRow("  Unmatched", $"{results.VtypUnmatched:N0}");
        matchTable.AddRow($"  Unique voice types: {uniqueVoiceTypes.Count}", "");
        matchTable.AddRow($"  Shared voice types: {sharedVoiceTypes.Count}", "");
        matchTable.AddRow("", "");

        matchTable.AddRow("[bold]Combined enrichment[/]", "");
        matchTable.AddRow("  Subtitle (NAM1)", Frac(results.EnrichedSubtitle, total));
        matchTable.AddRow("  Speaker (ANAM/TNAM/VTYP->NPC_)", Frac(results.EnrichedSpeaker, total));
        matchTable.AddRow("    via VTYP unique speaker", $"+{results.EnrichedSpeakerViaVtyp:N0}");
        matchTable.AddRow("  Quest (QSTI/filename prefix->QUST)", Frac(results.EnrichedQuest, total));
        matchTable.AddRow("    via filename prefix", $"+{results.EnrichedQuestViaPrefix:N0}");

        AnsiConsole.Write(matchTable);
        AnsiConsole.WriteLine();
    }

    internal static void RenderUnmatchedSamples(MatchResults results, int limit)
    {
        if (limit > 0 && results.UnmatchedFormIds.Count > 0)
        {
            var fmTable = new Table().Border(TableBorder.Rounded)
                .Title($"[bold]Unmatched FormIDs[/] (first {Math.Min(limit, results.UnmatchedFormIds.Count)})");
            fmTable.AddColumn("FormID");
            fmTable.AddColumn("TopicEditorId");

            foreach (var (fid, tid) in results.UnmatchedFormIds.Distinct().Take(limit))
            {
                fmTable.AddRow($"{fid:X8}", tid);
            }

            AnsiConsole.Write(fmTable);
            AnsiConsole.MarkupLine($"[dim]Total unmatched: {results.UnmatchedFormIds.Distinct().Count():N0}[/]");
            AnsiConsole.WriteLine();
        }

        if (limit > 0 && results.UnmatchedTopics.Count > 0)
        {
            var tmTable = new Table().Border(TableBorder.Rounded)
                .Title(
                    $"[bold]Unmatched TopicEditorIds[/] (first {Math.Min(limit, results.UnmatchedTopics.Count)})");
            tmTable.AddColumn("TopicEditorId (from BSA filename)");

            foreach (var topic in results.UnmatchedTopics.Take(limit))
            {
                tmTable.AddRow(topic);
            }

            AnsiConsole.Write(tmTable);
            AnsiConsole.MarkupLine($"[dim]Total unmatched: {results.UnmatchedTopics.Count:N0}[/]");
            AnsiConsole.WriteLine();
        }

        if (results.UnmatchedFormIds.Count == 0 && results.UnmatchedTopics.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]All voice files matched![/]");
        }
    }

    internal static void RenderDiagnostics(
        List<VoiceFile> voiceFiles,
        HashSet<string> uniqueTopics,
        Dictionary<string, DialData> dialEntries,
        Dictionary<uint, InfoData> infoEntries,
        HashSet<uint> npcFormIds,
        HashSet<uint> questFormIds,
        Dictionary<string, string> questEdidToName,
        Dictionary<string, List<string>> voiceTypeToNpcs,
        List<KeyValuePair<string, List<string>>> uniqueVoiceTypes,
        List<KeyValuePair<string, List<string>>> sharedVoiceTypes,
        int limit)
    {
        // Sample DIAL EDIDs
        var diagTable = new Table().Border(TableBorder.Rounded)
            .Title("[bold]Sample DIAL EDIDs from ESM[/]");
        diagTable.AddColumn("DIAL EDID");
        diagTable.AddColumn("Has QSTI");
        diagTable.AddColumn("Has TNAM");

        foreach (var (edid, data) in dialEntries.Take(limit))
        {
            diagTable.AddRow(
                edid,
                data.QstiFormId.HasValue ? "Yes" : "No",
                data.TnamFormId.HasValue ? "Yes" : "No");
        }

        AnsiConsole.Write(diagTable);
        AnsiConsole.WriteLine();

        // Sample TopicEditorIds
        var topicSample = new Table().Border(TableBorder.Rounded)
            .Title("[bold]Sample TopicEditorIds from BSA filenames[/]");
        topicSample.AddColumn("TopicEditorId");
        topicSample.AddColumn("Sample Filename");

        foreach (var topic in uniqueTopics.Take(limit))
        {
            var sample = voiceFiles.First(v =>
                v.TopicEditorId.Equals(topic, StringComparison.OrdinalIgnoreCase));
            topicSample.AddRow(topic, sample.FileName);
        }

        AnsiConsole.Write(topicSample);
        AnsiConsole.WriteLine();

        // Unique voice types (1:1 NPC mapping)
        RenderUniqueVoiceTypes(voiceFiles, uniqueVoiceTypes, limit);

        // Top shared voice types
        RenderSharedVoiceTypes(voiceFiles, sharedVoiceTypes, limit);

        // Files without speaker or quest
        RenderNoMatchFiles(voiceFiles, infoEntries, npcFormIds, questFormIds,
            questEdidToName, voiceTypeToNpcs, limit);
    }

    private static void RenderUniqueVoiceTypes(
        List<VoiceFile> voiceFiles,
        List<KeyValuePair<string, List<string>>> uniqueVoiceTypes,
        int limit)
    {
        var vtypTable = new Table().Border(TableBorder.Rounded)
            .Title(
                $"[bold]Unique Voice Types (1:1 NPC)[/] (first {Math.Min(limit, uniqueVoiceTypes.Count)})");
        vtypTable.AddColumn("VTYP EDID");
        vtypTable.AddColumn("NPC Name");
        vtypTable.AddColumn(new TableColumn("Voice Files").RightAligned());

        foreach (var (edid, npcs) in uniqueVoiceTypes
                     .OrderByDescending(kv =>
                         voiceFiles.Count(v =>
                             v.VoiceType.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)))
                     .Take(limit))
        {
            var fileCount = voiceFiles.Count(v =>
                v.VoiceType.Equals(edid, StringComparison.OrdinalIgnoreCase));
            vtypTable.AddRow(edid, npcs[0], $"{fileCount:N0}");
        }

        AnsiConsole.Write(vtypTable);
        AnsiConsole.MarkupLine($"[dim]Total unique voice types: {uniqueVoiceTypes.Count}[/]");
        AnsiConsole.WriteLine();
    }

    private static void RenderSharedVoiceTypes(
        List<VoiceFile> voiceFiles,
        List<KeyValuePair<string, List<string>>> sharedVoiceTypes,
        int limit)
    {
        if (sharedVoiceTypes.Count > 0)
        {
            var sharedTable = new Table().Border(TableBorder.Rounded)
                .Title(
                    $"[bold]Top Shared Voice Types[/] (first {Math.Min(limit, sharedVoiceTypes.Count)})");
            sharedTable.AddColumn("VTYP EDID");
            sharedTable.AddColumn(new TableColumn("NPCs").RightAligned());
            sharedTable.AddColumn(new TableColumn("Voice Files").RightAligned());

            foreach (var (edid, npcs) in sharedVoiceTypes
                         .OrderByDescending(kv => kv.Value.Count)
                         .Take(limit))
            {
                var fileCount = voiceFiles.Count(v =>
                    v.VoiceType.Equals(edid, StringComparison.OrdinalIgnoreCase));
                sharedTable.AddRow(edid, $"{npcs.Count:N0}", $"{fileCount:N0}");
            }

            AnsiConsole.Write(sharedTable);
        }

        AnsiConsole.WriteLine();
    }

    private static void RenderNoMatchFiles(
        List<VoiceFile> voiceFiles,
        Dictionary<uint, InfoData> infoEntries,
        HashSet<uint> npcFormIds,
        HashSet<uint> questFormIds,
        Dictionary<string, string> questEdidToName,
        Dictionary<string, List<string>> voiceTypeToNpcs,
        int limit)
    {
        var noMatchFiles = new List<(uint FormId, string FileName, string VoiceType, string Missing)>();
        foreach (var vf in voiceFiles)
        {
            var hasSpeaker = false;
            var hasQuest = false;

            if (infoEntries.TryGetValue(vf.FormId, out var inf))
            {
                if (inf.AnamFormId.HasValue && npcFormIds.Contains(inf.AnamFormId.Value))
                {
                    hasSpeaker = true;
                }

                if (inf.QstiFormId.HasValue && questFormIds.Contains(inf.QstiFormId.Value))
                {
                    hasQuest = true;
                }
            }

            // VTYP fallback for speaker
            if (!hasSpeaker && vf.VoiceType.Length > 0 &&
                voiceTypeToNpcs.TryGetValue(vf.VoiceType, out var vtNpcs) && vtNpcs.Count == 1)
            {
                hasSpeaker = true;
            }

            // Quest EDID prefix fallback from filename
            if (!hasQuest && vf.TopicEditorId.Length > 0)
            {
                var usIdx = vf.TopicEditorId.IndexOf('_');
                if (usIdx > 0)
                {
                    var prefix = vf.TopicEditorId[..usIdx];
                    if (questEdidToName.ContainsKey(prefix))
                    {
                        hasQuest = true;
                    }
                }
            }

            if (!hasSpeaker || !hasQuest)
            {
                var missing = (!hasSpeaker && !hasQuest) ? "speaker+quest"
                    : !hasSpeaker ? "speaker"
                    : "quest";
                noMatchFiles.Add((vf.FormId, vf.FileName, vf.VoiceType, missing));
            }
        }

        // Summary counts
        var noSpeakerCount = noMatchFiles.Count(x => x.Missing.Contains("speaker"));
        var noQuestCount = noMatchFiles.Count(x => x.Missing.Contains("quest"));
        var noBothCount = noMatchFiles.Count(x => x.Missing == "speaker+quest");
        AnsiConsole.MarkupLine(
            $"[bold]Unmatched files:[/] {noSpeakerCount:N0} missing speaker, {noQuestCount:N0} missing quest, {noBothCount:N0} missing both");
        AnsiConsole.WriteLine();

        // Show files missing both first, then speaker-only, then quest-only
        var noMatchTable = new Table().Border(TableBorder.Rounded)
            .Title(
                $"[bold]Files Without Speaker or Quest[/] (first {Math.Min(limit, noMatchFiles.Count)} of {noMatchFiles.Count:N0})");
        noMatchTable.AddColumn("FormID");
        noMatchTable.AddColumn("Filename");
        noMatchTable.AddColumn("VoiceType");
        noMatchTable.AddColumn("Missing");

        foreach (var ns in noMatchFiles
                     .OrderByDescending(x => x.Missing == "speaker+quest")
                     .ThenByDescending(x => x.Missing == "quest")
                     .Take(limit))
        {
            var vtInfo = voiceTypeToNpcs.TryGetValue(ns.VoiceType, out var vtList)
                ? $"{ns.VoiceType} ({vtList.Count} NPCs)"
                : ns.VoiceType;
            noMatchTable.AddRow($"{ns.FormId:X8}", ns.FileName, vtInfo, ns.Missing);
        }

        AnsiConsole.Write(noMatchTable);
    }
}
