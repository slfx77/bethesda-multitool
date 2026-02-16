using System.Text;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Reporting;

/// <summary>
///     Generates markdown reports from version tracking results.
///     Per-record timeline tables show field values across builds where changes occurred.
/// </summary>
public static class MarkdownTimelineWriter
{
    private static readonly (string Label, Func<VersionDiffResult, List<RecordChange>> Selector,
        Func<VersionSnapshot, int> Counter)[] Categories =
    [
        ("Quests", d => d.QuestChanges, s => s.Quests.Count),
        ("NPCs", d => d.NpcChanges, s => s.Npcs.Count),
        ("Weapons", d => d.WeaponChanges, s => s.Weapons.Count),
        ("Armor", d => d.ArmorChanges, s => s.Armor.Count),
        ("Items", d => d.ItemChanges, s => s.Items.Count),
        ("Scripts", d => d.ScriptChanges, s => s.Scripts.Count),
        ("Dialogues", d => d.DialogueChanges, s => s.Dialogues.Count),
        ("Locations", d => d.LocationChanges, s => s.Locations.Count),
        ("Placements", d => d.PlacementChanges, s => s.Placements.Count),
        ("Creatures", d => d.CreatureChanges, s => s.Creatures.Count),
        ("Perks", d => d.PerkChanges, s => s.Perks.Count),
        ("Ammo", d => d.AmmoChanges, s => s.Ammo.Count),
        ("Leveled Lists", d => d.LeveledListChanges, s => s.LeveledLists.Count),
        ("Notes", d => d.NoteChanges, s => s.Notes.Count),
        ("Terminals", d => d.TerminalChanges, s => s.Terminals.Count)
    ];

    /// <summary>
    ///     Generates a full timeline report with per-record change tables.
    /// </summary>
    public static string WriteTimeline(
        List<VersionSnapshot> snapshots,
        List<VersionDiffResult> diffs,
        uint? trackFormId = null,
        HashSet<uint>? fo3LeftoverFormIds = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Fallout: New Vegas — Version Tracking Report");
        sb.AppendLine();
        WriteBuildTimeline(sb, snapshots);

        sb.AppendLine();
        WriteDiffOverview(sb, diffs);

        // Per-record timeline tables (the main content)
        WriteRecordTimelines(sb, snapshots, diffs);

        // Cut content section
        WriteCutContentSection(sb, snapshots, fo3LeftoverFormIds);

        // Per-FormID history if requested
        if (trackFormId.HasValue)
        {
            sb.AppendLine();
            WriteFormIdHistory(sb, snapshots, trackFormId.Value);
        }

        return sb.ToString();
    }

    #region Build Timeline

    private static void WriteBuildTimeline(StringBuilder sb, List<VersionSnapshot> snapshots)
    {
        sb.AppendLine("## Build Timeline");
        sb.AppendLine();
        sb.AppendLine("| # | Build | Date | Source | Quests | NPCs | Weapons | Armor | Items | Scripts | Dialogues | Locations | Total |");
        sb.AppendLine("|---|-------|------|--------|--------|------|---------|-------|-------|---------|-----------|-----------|-------|");

        for (var i = 0; i < snapshots.Count; i++)
        {
            var s = snapshots[i];
            var date = s.Build.BuildDate?.ToString("yyyy-MM-dd") ?? "Unknown";
            var source = s.Build.SourceType == BuildSourceType.Esm ? "ESM" : "DMP";

            sb.AppendLine($"| {i + 1} | {EscapePipes(s.Build.Label)} | {date} | {source} | " +
                          $"{s.Quests.Count:N0} | {s.Npcs.Count:N0} | {s.Weapons.Count:N0} | " +
                          $"{s.Armor.Count:N0} | {s.Items.Count:N0} | {s.Scripts.Count:N0} | " +
                          $"{s.Dialogues.Count:N0} | {s.Locations.Count:N0} | {s.TotalRecordCount:N0} |");
        }
    }

    #endregion

    #region Diff Overview

    private static void WriteDiffOverview(StringBuilder sb, List<VersionDiffResult> diffs)
    {
        sb.AppendLine("## Change Summary");
        sb.AppendLine();
        sb.AppendLine("| # | From → To | Added | Removed | Changed | Total |");
        sb.AppendLine("|---|-----------|-------|---------|---------|-------|");

        for (var i = 0; i < diffs.Count; i++)
        {
            var d = diffs[i];
            var total = d.TotalAdded + d.TotalRemoved + d.TotalChanged;
            if (total == 0)
            {
                continue;
            }

            sb.AppendLine($"| {i + 1} | {EscapePipes(d.FromBuild.Label)} → {EscapePipes(d.ToBuild.Label)} | " +
                          $"{d.TotalAdded:N0} | {d.TotalRemoved:N0} | {d.TotalChanged:N0} | {total:N0} |");
        }
    }

    #endregion

    #region Per-Record Timelines

    private static void WriteRecordTimelines(
        StringBuilder sb, List<VersionSnapshot> snapshots, List<VersionDiffResult> diffs)
    {
        // Build a map from build label → build number for compact column headers
        var buildIndex = new Dictionary<string, int>();
        for (var i = 0; i < snapshots.Count; i++)
        {
            buildIndex[snapshots[i].Build.Label] = i + 1;
        }

        // Collect all Changed records across all diffs, grouped by (category, FormID)
        var histories = new Dictionary<(string Category, uint FormId), RecordHistory>();

        for (var i = 0; i < diffs.Count; i++)
        {
            var toBuild = snapshots[i + 1].Build;

            foreach (var (category, selector, _) in Categories)
            {
                foreach (var change in selector(diffs[i]))
                {
                    if (change.ChangeType != ChangeType.Changed || change.FieldChanges.Count == 0)
                    {
                        continue;
                    }

                    var key = (category, change.FormId);
                    if (!histories.TryGetValue(key, out var history))
                    {
                        history = new RecordHistory(change.FormId, change.EditorId, change.FullName, category, []);
                        histories[key] = history;
                    }

                    history.ChangePoints.Add(new ChangePoint(toBuild.Label, toBuild.BuildDate, change.FieldChanges));
                }
            }
        }

        if (histories.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Record Change Timelines");
        sb.AppendLine();

        foreach (var categoryGroup in histories
                     .GroupBy(kvp => kvp.Key.Category)
                     .OrderByDescending(g => GetCategoryPriority(g.Key)))
        {
            sb.AppendLine($"### {categoryGroup.Key}");
            sb.AppendLine();

            if (categoryGroup.Key == "Dialogues")
            {
                WriteDialogueTimelinesByQuest(sb, categoryGroup, snapshots, buildIndex);
            }
            else
            {
                foreach (var ((_, _), history) in categoryGroup.OrderBy(kvp => kvp.Key.FormId))
                {
                    WriteRecordTable(sb, history, buildIndex);
                }
            }
        }
    }

    private static void WriteDialogueTimelinesByQuest(
        StringBuilder sb,
        IGrouping<string, KeyValuePair<(string Category, uint FormId), RecordHistory>> dialogueGroup,
        List<VersionSnapshot> snapshots,
        Dictionary<string, int> buildIndex)
    {
        // Look up quest info from all snapshots
        var questGroups = new Dictionary<uint, (string QuestName, List<RecordHistory> Histories)>();
        var orphans = new List<RecordHistory>();

        foreach (var ((_, _), history) in dialogueGroup)
        {
            // Find quest association from any snapshot
            uint? questId = null;
            foreach (var snapshot in snapshots)
            {
                if (snapshot.Dialogues.TryGetValue(history.FormId, out var dialogue) &&
                    dialogue.QuestFormId.HasValue)
                {
                    questId = dialogue.QuestFormId;
                    break;
                }
            }

            if (questId.HasValue)
            {
                if (!questGroups.TryGetValue(questId.Value, out var group))
                {
                    string? questName = null;
                    foreach (var snapshot in snapshots)
                    {
                        if (snapshot.Quests.TryGetValue(questId.Value, out var quest))
                        {
                            questName = quest.FullName ?? quest.EditorId;
                            break;
                        }
                    }

                    group = (questName ?? $"0x{questId.Value:X8}", []);
                    questGroups[questId.Value] = group;
                }

                group.Histories.Add(history);
            }
            else
            {
                orphans.Add(history);
            }
        }

        foreach (var (questId, (questName, histories)) in questGroups
                     .OrderByDescending(kvp => kvp.Value.Histories.Count))
        {
            sb.AppendLine($"#### {EscapePipes(questName)} (0x{questId:X8})");
            sb.AppendLine();

            foreach (var history in histories.OrderBy(h => h.FormId))
            {
                WriteRecordTable(sb, history, buildIndex, headingLevel: "#####");
            }
        }

        if (orphans.Count > 0)
        {
            sb.AppendLine("#### Other Dialogues");
            sb.AppendLine();

            foreach (var history in orphans.OrderBy(h => h.FormId))
            {
                WriteRecordTable(sb, history, buildIndex, headingLevel: "#####");
            }
        }
    }

    private static void WriteRecordTable(
        StringBuilder sb, RecordHistory history, Dictionary<string, int> buildIndex,
        string headingLevel = "####")
    {
        // TCRF heading: Name (FormID) — fallback EditorID → FormID
        var heading = GetTcrfHeading(history.FullName, history.EditorId, history.FormId);
        sb.AppendLine($"{headingLevel} {EscapePipes(heading)} (0x{history.FormId:X8})");
        sb.AppendLine();

        // Collect all field names across all change points, in order of first appearance
        var allFields = new List<string>();
        var seenFields = new HashSet<string>();
        foreach (var cp in history.ChangePoints)
        {
            foreach (var fc in cp.FieldChanges)
            {
                if (seenFields.Add(fc.FieldName))
                {
                    allFields.Add(fc.FieldName);
                }
            }
        }

        // Column headers: "#N (MMM d, yyyy)" format
        var columnHeaders = history.ChangePoints
            .Select(cp =>
            {
                var num = buildIndex.TryGetValue(cp.BuildLabel, out var idx) ? $"#{idx}" : "?";
                var date = cp.BuildDate?.ToString("MMM d, yyyy") ?? "Unknown";
                return $"{num} ({date})";
            })
            .ToList();

        // Table header row (first column empty for row headers)
        sb.Append("| |");
        foreach (var header in columnHeaders)
        {
            sb.Append($" {EscapePipes(header)} |");
        }

        sb.AppendLine();

        // Separator row
        sb.Append("|---|");
        foreach (var _ in columnHeaders)
        {
            sb.Append("---|");
        }

        sb.AppendLine();

        // Data rows: one per field
        foreach (var field in allFields)
        {
            sb.Append($"| **{EscapePipes(field)}** |");
            foreach (var cp in history.ChangePoints)
            {
                var fc = cp.FieldChanges.FirstOrDefault(f => f.FieldName == field);
                var value = fc != null ? (fc.NewValue ?? "(none)") : "-";
                sb.Append($" {EscapePipes(value)} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine();
    }

    #endregion

    #region Cut Content

    private static void WriteCutContentSection(StringBuilder sb, List<VersionSnapshot> snapshots,
        HashSet<uint>? fo3LeftoverFormIds)
    {
        var finalEsm = snapshots.LastOrDefault(s => s.Build.IsAuthoritative);
        if (finalEsm == null)
        {
            return;
        }

        var dmpSnapshots = snapshots.Where(s => !s.Build.IsAuthoritative).ToList();
        if (dmpSnapshots.Count == 0)
        {
            return;
        }

        var cutQuests = FindCutRecords(dmpSnapshots.SelectMany(s => s.Quests), finalEsm.Quests, snapshots, fo3LeftoverFormIds);
        var cutNpcs = FindCutRecords(dmpSnapshots.SelectMany(s => s.Npcs), finalEsm.Npcs, snapshots, fo3LeftoverFormIds);
        var cutWeapons = FindCutRecords(dmpSnapshots.SelectMany(s => s.Weapons), finalEsm.Weapons, snapshots, fo3LeftoverFormIds);
        var cutArmor = FindCutRecords(dmpSnapshots.SelectMany(s => s.Armor), finalEsm.Armor, snapshots, fo3LeftoverFormIds);
        var cutItems = FindCutRecords(dmpSnapshots.SelectMany(s => s.Items), finalEsm.Items, snapshots, fo3LeftoverFormIds);
        var cutScripts = FindCutRecords(dmpSnapshots.SelectMany(s => s.Scripts), finalEsm.Scripts, snapshots, fo3LeftoverFormIds);
        var cutDialogues = FindCutRecords(dmpSnapshots.SelectMany(s => s.Dialogues), finalEsm.Dialogues, snapshots, fo3LeftoverFormIds);
        var cutCreatures = FindCutRecords(dmpSnapshots.SelectMany(s => s.Creatures), finalEsm.Creatures, snapshots, fo3LeftoverFormIds);
        var cutPerks = FindCutRecords(dmpSnapshots.SelectMany(s => s.Perks), finalEsm.Perks, snapshots, fo3LeftoverFormIds);
        var cutAmmo = FindCutRecords(dmpSnapshots.SelectMany(s => s.Ammo), finalEsm.Ammo, snapshots, fo3LeftoverFormIds);
        var cutLeveledLists = FindCutRecords(dmpSnapshots.SelectMany(s => s.LeveledLists), finalEsm.LeveledLists, snapshots, fo3LeftoverFormIds);
        var cutNotes = FindCutRecords(dmpSnapshots.SelectMany(s => s.Notes), finalEsm.Notes, snapshots, fo3LeftoverFormIds);
        var cutTerminals = FindCutRecords(dmpSnapshots.SelectMany(s => s.Terminals), finalEsm.Terminals, snapshots, fo3LeftoverFormIds);

        if (cutQuests.Count == 0 && cutNpcs.Count == 0 && cutWeapons.Count == 0 &&
            cutArmor.Count == 0 && cutItems.Count == 0 && cutScripts.Count == 0 &&
            cutDialogues.Count == 0 && cutCreatures.Count == 0 && cutPerks.Count == 0 &&
            cutAmmo.Count == 0 && cutLeveledLists.Count == 0 && cutNotes.Count == 0 &&
            cutTerminals.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Potential Cut Content (In Early DMPs, Absent from Final ESM)");
        sb.AppendLine();

        WriteCutTable(sb, "Quests", cutQuests);
        WriteCutTable(sb, "NPCs", cutNpcs);
        WriteCutTable(sb, "Weapons", cutWeapons);
        WriteCutTable(sb, "Armor", cutArmor);
        WriteCutTable(sb, "Items", cutItems);
        WriteCutTable(sb, "Scripts", cutScripts);
        WriteCutDialoguesGroupedByQuest(sb, cutDialogues, snapshots);
        WriteCutTable(sb, "Creatures", cutCreatures);
        WriteCutTable(sb, "Perks", cutPerks);
        WriteCutTable(sb, "Ammo", cutAmmo);
        WriteCutTable(sb, "Leveled Lists", cutLeveledLists);
        WriteCutTable(sb, "Notes", cutNotes);
        WriteCutTable(sb, "Terminals", cutTerminals);
    }

    private static void WriteCutDialoguesGroupedByQuest(
        StringBuilder sb,
        List<(uint FormId, string? EditorId, string? Name, string FirstSeen, string LastSeen)> records,
        List<VersionSnapshot> snapshots)
    {
        if (records.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("### Dialogues");
        sb.AppendLine();

        // Group by quest
        var questGroups = new Dictionary<uint, (string QuestName, List<(uint FormId, string? EditorId, string? Name, string FirstSeen, string LastSeen)> Dialogues)>();
        var orphans = new List<(uint FormId, string? EditorId, string? Name, string FirstSeen, string LastSeen)>();

        foreach (var record in records)
        {
            uint? questId = null;
            foreach (var snapshot in snapshots)
            {
                if (snapshot.Dialogues.TryGetValue(record.FormId, out var dialogue) &&
                    dialogue.QuestFormId.HasValue)
                {
                    questId = dialogue.QuestFormId;
                    break;
                }
            }

            if (questId.HasValue)
            {
                if (!questGroups.TryGetValue(questId.Value, out var group))
                {
                    string? questName = null;
                    foreach (var snapshot in snapshots)
                    {
                        if (snapshot.Quests.TryGetValue(questId.Value, out var quest))
                        {
                            questName = quest.FullName ?? quest.EditorId;
                            break;
                        }
                    }

                    group = (questName ?? $"0x{questId.Value:X8}", []);
                    questGroups[questId.Value] = group;
                }

                group.Dialogues.Add(record);
            }
            else
            {
                orphans.Add(record);
            }
        }

        foreach (var (questId, (questName, dialogues)) in questGroups
                     .OrderByDescending(kvp => kvp.Value.Dialogues.Count))
        {
            sb.AppendLine($"#### {EscapePipes(questName)} (0x{questId:X8})");
            sb.AppendLine();
            sb.AppendLine("| Name | Editor ID | Form ID | First Seen | Last Seen |");
            sb.AppendLine("|------|-----------|---------|------------|-----------|");
            foreach (var (formId, editorId, name, firstSeen, lastSeen) in dialogues)
            {
                sb.AppendLine($"| {name ?? ""} | {editorId ?? ""} | 0x{formId:X8} | {EscapePipes(firstSeen)} | {EscapePipes(lastSeen)} |");
            }

            sb.AppendLine();
        }

        if (orphans.Count > 0)
        {
            sb.AppendLine("#### Other Dialogues");
            sb.AppendLine();
            sb.AppendLine("| Name | Editor ID | Form ID | First Seen | Last Seen |");
            sb.AppendLine("|------|-----------|---------|------------|-----------|");
            foreach (var (formId, editorId, name, firstSeen, lastSeen) in orphans)
            {
                sb.AppendLine($"| {name ?? ""} | {editorId ?? ""} | 0x{formId:X8} | {EscapePipes(firstSeen)} | {EscapePipes(lastSeen)} |");
            }

            sb.AppendLine();
        }
    }

    private static List<(uint FormId, string? EditorId, string? Name, string FirstSeen, string LastSeen)>
        FindCutRecords<T>(
            IEnumerable<KeyValuePair<uint, T>> dmpRecords,
            Dictionary<uint, T> finalEsmRecords,
            List<VersionSnapshot> allSnapshots,
            HashSet<uint>? fo3LeftoverFormIds)
    {
        var result = new List<(uint, string?, string?, string, string)>();
        var seen = new Dictionary<uint, (string? editorId, string? name, string firstSeen, string lastSeen)>();

        foreach (var (formId, record) in dmpRecords)
        {
            if (finalEsmRecords.ContainsKey(formId))
            {
                continue;
            }

            // Skip FO3 leftovers
            if (fo3LeftoverFormIds != null && fo3LeftoverFormIds.Contains(formId))
            {
                continue;
            }

            if (!seen.ContainsKey(formId))
            {
                var editorId = GetEditorId(record);
                var name = GetName(record);
                var firstSnapshot = allSnapshots
                    .FirstOrDefault(s => ContainsFormId(s, formId));
                var lastSnapshot = allSnapshots
                    .LastOrDefault(s => ContainsFormId(s, formId));

                seen[formId] = (editorId, name,
                    firstSnapshot?.Build.Label ?? "?",
                    lastSnapshot?.Build.Label ?? "?");
            }
        }

        foreach (var (formId, (editorId, name, firstSeen, lastSeen)) in seen.OrderBy(x => x.Key))
        {
            result.Add((formId, editorId, name, firstSeen, lastSeen));
        }

        return result;
    }

    private static bool ContainsFormId(VersionSnapshot snapshot, uint formId)
    {
        return snapshot.Quests.ContainsKey(formId) || snapshot.Npcs.ContainsKey(formId) ||
               snapshot.Weapons.ContainsKey(formId) || snapshot.Armor.ContainsKey(formId) ||
               snapshot.Items.ContainsKey(formId) || snapshot.Scripts.ContainsKey(formId) ||
               snapshot.Dialogues.ContainsKey(formId) || snapshot.Locations.ContainsKey(formId) ||
               snapshot.Placements.ContainsKey(formId) || snapshot.Creatures.ContainsKey(formId) ||
               snapshot.Perks.ContainsKey(formId) || snapshot.Ammo.ContainsKey(formId) ||
               snapshot.LeveledLists.ContainsKey(formId) || snapshot.Notes.ContainsKey(formId) ||
               snapshot.Terminals.ContainsKey(formId);
    }

    private static void WriteCutTable(StringBuilder sb, string category,
        List<(uint FormId, string? EditorId, string? Name, string FirstSeen, string LastSeen)> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine($"### {category}");
        sb.AppendLine();
        sb.AppendLine("| Name | Editor ID | Form ID | First Seen | Last Seen |");
        sb.AppendLine("|------|-----------|---------|------------|-----------|");
        foreach (var (formId, editorId, name, firstSeen, lastSeen) in records)
        {
            sb.AppendLine($"| {name ?? ""} | {editorId ?? ""} | 0x{formId:X8} | {EscapePipes(firstSeen)} | {EscapePipes(lastSeen)} |");
        }
    }

    #endregion

    #region FormID History

    private static void WriteFormIdHistory(StringBuilder sb, List<VersionSnapshot> snapshots, uint formId)
    {
        sb.AppendLine($"## FormID History: 0x{formId:X8}");
        sb.AppendLine();
        sb.AppendLine("| Build | Date | Present | Name | Details |");
        sb.AppendLine("|-------|------|---------|------|---------|");

        foreach (var snapshot in snapshots)
        {
            var date = snapshot.Build.BuildDate?.ToString("yyyy-MM-dd") ?? "?";

            if (snapshot.Quests.TryGetValue(formId, out var quest))
            {
                var stages = string.Join(", ", quest.Stages.Select(s => s.Index));
                var objectives = string.Join("; ", quest.Objectives.Select(o => o.DisplayText ?? "?"));
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | QUST | {quest.FullName ?? quest.EditorId ?? ""} | Stages: [{stages}] Objectives: [{objectives}] |");
            }
            else if (snapshot.Npcs.TryGetValue(formId, out var npc))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | NPC_ | {npc.FullName ?? npc.EditorId ?? ""} | Level: {npc.Level} |");
            }
            else if (snapshot.Weapons.TryGetValue(formId, out var weapon))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | WEAP | {weapon.FullName ?? weapon.EditorId ?? ""} | Dmg: {weapon.Damage}, Clip: {weapon.ClipSize} |");
            }
            else if (snapshot.Armor.TryGetValue(formId, out var armor))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | ARMO | {armor.FullName ?? armor.EditorId ?? ""} | DT: {armor.DamageThreshold:F1} |");
            }
            else if (snapshot.Items.TryGetValue(formId, out var item))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | {item.RecordType} | {item.FullName ?? item.EditorId ?? ""} | Value: {item.Value} |");
            }
            else if (snapshot.Scripts.TryGetValue(formId, out var script))
            {
                var hasSource = !string.IsNullOrEmpty(script.SourceText);
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | SCPT | {script.EditorId ?? ""} | {script.ScriptType}, Vars: {script.VariableCount}, Source: {(hasSource ? "Yes" : "No")} |");
            }
            else if (snapshot.Dialogues.TryGetValue(formId, out var dialogue))
            {
                var text = dialogue.ResponseTexts.FirstOrDefault() ?? "";
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | INFO | {dialogue.EditorId ?? ""} | {EscapePipes(text)} |");
            }
            else if (snapshot.Locations.TryGetValue(formId, out var location))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | {location.RecordType} | {location.FullName ?? location.EditorId ?? ""} | |");
            }
            else if (snapshot.Creatures.TryGetValue(formId, out var creature))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | CREA | {creature.FullName ?? creature.EditorId ?? ""} | Level: {creature.Level}, Dmg: {creature.AttackDamage} |");
            }
            else if (snapshot.Perks.TryGetValue(formId, out var perk))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | PERK | {perk.FullName ?? perk.EditorId ?? ""} | Ranks: {perk.Ranks}, MinLvl: {perk.MinLevel} |");
            }
            else if (snapshot.Ammo.TryGetValue(formId, out var ammo))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | AMMO | {ammo.FullName ?? ammo.EditorId ?? ""} | Value: {ammo.Value} |");
            }
            else if (snapshot.LeveledLists.TryGetValue(formId, out var leveledList))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | {leveledList.ListType} | {leveledList.EditorId ?? ""} | Entries: {leveledList.Entries.Count} |");
            }
            else if (snapshot.Notes.TryGetValue(formId, out var note))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | NOTE | {note.FullName ?? note.EditorId ?? ""} | |");
            }
            else if (snapshot.Terminals.TryGetValue(formId, out var terminal))
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | TERM | {terminal.FullName ?? terminal.EditorId ?? ""} | Items: {terminal.MenuItemCount} |");
            }
            else
            {
                sb.AppendLine($"| {EscapePipes(snapshot.Build.Label)} | {date} | - | (not found) | |");
            }
        }
    }

    #endregion

    #region Helpers

    private record ChangePoint(string BuildLabel, DateTimeOffset? BuildDate, List<FieldChange> FieldChanges);

    private record RecordHistory(
        uint FormId, string? EditorId, string? FullName, string Category,
        List<ChangePoint> ChangePoints);

    private static string? GetEditorId<T>(T record)
    {
        return record switch
        {
            TrackedQuest q => q.EditorId,
            TrackedNpc n => n.EditorId,
            TrackedWeapon w => w.EditorId,
            TrackedArmor a => a.EditorId,
            TrackedItem i => i.EditorId,
            TrackedScript s => s.EditorId,
            TrackedCreature c => c.EditorId,
            TrackedPerk p => p.EditorId,
            TrackedAmmo a => a.EditorId,
            TrackedLeveledList l => l.EditorId,
            TrackedNote n => n.EditorId,
            TrackedTerminal t => t.EditorId,
            _ => null
        };
    }

    private static string? GetName<T>(T record)
    {
        return record switch
        {
            TrackedQuest q => q.FullName,
            TrackedNpc n => n.FullName,
            TrackedWeapon w => w.FullName,
            TrackedArmor a => a.FullName,
            TrackedItem i => i.FullName,
            TrackedCreature c => c.FullName,
            TrackedPerk p => p.FullName,
            TrackedAmmo a => a.FullName,
            TrackedNote n => n.FullName,
            TrackedTerminal t => t.FullName,
            _ => null
        };
    }

    private static int GetCategoryPriority(string category)
    {
        return category switch
        {
            "Quests" => 9,
            "NPCs" => 8,
            "Weapons" => 7,
            "Armor" => 6,
            "Items" => 5,
            "Scripts" => 4,
            "Dialogues" => 3,
            "Locations" => 2,
            "Placements" => 1,
            "Creatures" => 7,
            "Perks" => 6,
            "Ammo" => 4,
            "Leveled Lists" => 3,
            "Notes" => 2,
            "Terminals" => 2,
            _ => 0
        };
    }

    private static string GetTcrfHeading(string? fullName, string? editorId, uint formId)
    {
        if (!string.IsNullOrEmpty(fullName))
        {
            return fullName;
        }

        if (!string.IsNullOrEmpty(editorId))
        {
            return editorId;
        }

        return $"0x{formId:X8}";
    }

    private static string EscapePipes(string text)
    {
        return text.Replace("|", "\\|");
    }

    #endregion
}
