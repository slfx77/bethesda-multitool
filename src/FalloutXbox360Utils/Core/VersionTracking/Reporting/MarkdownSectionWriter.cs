using System.Text;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Reporting;

/// <summary>
///     Renders individual sections of the multi-page markdown report:
///     cut content tables, changed content tables, record history tables,
///     and FormID history tracking.
/// </summary>
internal static class MarkdownSectionWriter
{
    #region Cut Content Section

    internal static void WriteCutContentSection(
        StringBuilder sb, MarkdownMultiPageWriter.CategoryDef cat,
        List<MarkdownMultiPageWriter.CutRecord> cutRecords, List<VersionSnapshot> snapshots)
    {
        if (cutRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Cut Content");
        sb.AppendLine();
        sb.AppendLine("Records present in development builds but absent from the final ESM.");
        sb.AppendLine();

        // Group by sub-type
        var grouped = GroupCutRecords(cat, cutRecords, snapshots);

        foreach (var (groupName, records) in grouped.OrderByDescending(g => g.Records.Count))
        {
            if (grouped.Count > 1)
            {
                sb.AppendLine($"### {groupName}");
                sb.AppendLine();
            }

            sb.AppendLine("| Name | Editor ID | Form ID | First Seen | Last Seen |");
            sb.AppendLine("|------|-----------|---------|------------|-----------|");

            foreach (var r in records.OrderBy(r => r.Name ?? r.EditorId ?? $"0x{r.FormId:X8}"))
            {
                var heading = MarkdownMultiPageWriter.GetTcrfHeading(r.Name, r.EditorId, r.FormId);
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(heading)} | {r.EditorId ?? ""} | 0x{r.FormId:X8} | {MarkdownMultiPageWriter.Esc(r.FirstSeen)} | {MarkdownMultiPageWriter.Esc(r.LastSeen)} |");
            }

            sb.AppendLine();
        }
    }

    private static List<(string GroupName, List<MarkdownMultiPageWriter.CutRecord> Records)> GroupCutRecords(
        MarkdownMultiPageWriter.CategoryDef cat, List<MarkdownMultiPageWriter.CutRecord> records,
        List<VersionSnapshot> snapshots)
    {
        var groups = new Dictionary<string, List<MarkdownMultiPageWriter.CutRecord>>();

        foreach (var record in records)
        {
            var groupName = MarkdownGroupClassifier.LookupGroupForFormId(cat, record.FormId, snapshots);
            if (!groups.TryGetValue(groupName, out var list))
            {
                list = [];
                groups[groupName] = list;
            }

            list.Add(record);
        }

        return groups.Select(kvp => (kvp.Key, kvp.Value)).ToList();
    }

    #endregion

    #region Changed Content Section

    internal static void WriteChangedContentSection(
        StringBuilder sb, MarkdownMultiPageWriter.CategoryDef cat,
        Dictionary<(string Category, uint FormId), MarkdownMultiPageWriter.RecordHistory> catHistories,
        List<VersionSnapshot> snapshots)
    {
        if (catHistories.Count == 0)
        {
            return;
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Changed Content");
        sb.AppendLine();
        sb.AppendLine("Records that changed across development builds.");
        sb.AppendLine();

        // Build index
        var buildIndex = new Dictionary<string, int>();
        for (var i = 0; i < snapshots.Count; i++)
        {
            buildIndex[snapshots[i].Build.Label] = i + 1;
        }

        // Separate significant vs minor
        var significantHistories = new List<(string Group, MarkdownMultiPageWriter.RecordHistory History)>();
        var minorHistories = new List<(string Group, MarkdownMultiPageWriter.RecordHistory History)>();

        foreach (var ((_, formId), history) in catHistories)
        {
            var group = MarkdownGroupClassifier.LookupGroupForFormId(cat, formId, snapshots);
            if (HasSignificantChanges(history))
            {
                significantHistories.Add((group, history));
            }
            else
            {
                minorHistories.Add((group, history));
            }
        }

        // Write significant changes grouped
        var significantGroups = significantHistories
            .GroupBy(x => x.Group)
            .OrderByDescending(g => g.Count());

        foreach (var group in significantGroups)
        {
            var groupHistories = group.OrderBy(x => x.History.FullName ?? x.History.EditorId ?? "").ToList();

            if (significantGroups.Count() > 1 || minorHistories.Count > 0)
            {
                sb.AppendLine($"### {group.Key} ({groupHistories.Count})");
                sb.AppendLine();
            }

            foreach (var (_, history) in groupHistories)
            {
                WriteRecordTable(sb, history, buildIndex);
            }
        }

        // Write minor changes in collapsible block
        if (minorHistories.Count > 0)
        {
            sb.AppendLine("<details>");
            sb.AppendLine(
                $"<summary>{minorHistories.Count} records with minor changes (flags, padding, etc.)</summary>");
            sb.AppendLine();

            var minorGroups = minorHistories
                .GroupBy(x => x.Group)
                .OrderByDescending(g => g.Count());

            foreach (var group in minorGroups)
            {
                if (minorGroups.Count() > 1)
                {
                    sb.AppendLine($"### {group.Key}");
                    sb.AppendLine();
                }

                foreach (var (_, history) in group.OrderBy(x => x.History.FormId))
                {
                    WriteRecordTable(sb, history, buildIndex);
                }
            }

            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static void WriteRecordTable(
        StringBuilder sb, MarkdownMultiPageWriter.RecordHistory history,
        Dictionary<string, int> buildIndex, string headingLevel = "####")
    {
        var heading = MarkdownMultiPageWriter.GetTcrfHeading(history.FullName, history.EditorId, history.FormId);
        sb.AppendLine($"{headingLevel} {MarkdownMultiPageWriter.Esc(heading)} (0x{history.FormId:X8})");
        sb.AppendLine();

        // Collect all field names in order of first appearance
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

        // Pre-compute Before/Final values for each field
        var beforeValues = new Dictionary<string, string?>();
        var finalValues = new Dictionary<string, string?>();
        foreach (var field in allFields)
        {
            var first = history.ChangePoints.FirstOrDefault(cp => cp.FieldChanges.Any(f => f.FieldName == field));
            var last = history.ChangePoints.LastOrDefault(cp => cp.FieldChanges.Any(f => f.FieldName == field));
            beforeValues[field] = first?.FieldChanges.First(f => f.FieldName == field).OldValue;
            finalValues[field] = last?.FieldChanges.First(f => f.FieldName == field).NewValue;
        }

        // Determine if Final column is needed (skip if every field's last change is the last column)
        var lastCp = history.ChangePoints[^1];
        var needsFinalColumn = allFields.Any(field =>
        {
            var lastForField = history.ChangePoints.LastOrDefault(cp => cp.FieldChanges.Any(f => f.FieldName == field));
            return lastForField != lastCp;
        });

        // Column headers
        var columnHeaders = history.ChangePoints
            .Select(cp =>
            {
                var num = buildIndex.TryGetValue(cp.BuildLabel, out var idx) ? $"#{idx}" : "?";
                var date = cp.BuildDate?.ToString("MMM d, yyyy") ?? "Unknown";
                return $"{num} ({date})";
            })
            .ToList();

        sb.Append("| | Before |");
        foreach (var header in columnHeaders)
        {
            sb.Append($" {MarkdownMultiPageWriter.Esc(header)} |");
        }

        if (needsFinalColumn)
        {
            sb.Append(" Final |");
        }

        sb.AppendLine();

        sb.Append("|---|---|");
        foreach (var _ in columnHeaders)
        {
            sb.Append("---|");
        }

        if (needsFinalColumn)
        {
            sb.Append("---|");
        }

        sb.AppendLine();

        // Data rows
        foreach (var field in allFields)
        {
            var before = beforeValues[field] ?? "(none)";
            sb.Append($"| **{MarkdownMultiPageWriter.Esc(field)}** | {MarkdownMultiPageWriter.Esc(before)} |");

            foreach (var cp in history.ChangePoints)
            {
                var fc = cp.FieldChanges.FirstOrDefault(f => f.FieldName == field);
                var value = fc != null ? fc.NewValue ?? "(none)" : "-";
                sb.Append($" {MarkdownMultiPageWriter.Esc(value)} |");
            }

            if (needsFinalColumn)
            {
                var final_ = finalValues[field] ?? "(none)";
                sb.Append($" {MarkdownMultiPageWriter.Esc(final_)} |");
            }

            sb.AppendLine();
        }

        sb.AppendLine();
    }

    #endregion

    #region FormID History

    internal static void WriteFormIdHistory(StringBuilder sb, List<VersionSnapshot> snapshots, uint formId)
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
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | QUST | {quest.FullName ?? quest.EditorId ?? ""} | Stages: [{stages}] Objectives: [{objectives}] |");
            }
            else if (snapshot.Npcs.TryGetValue(formId, out var npc))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | NPC_ | {npc.FullName ?? npc.EditorId ?? ""} | Level: {npc.Level} |");
            }
            else if (snapshot.Weapons.TryGetValue(formId, out var weapon))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | WEAP | {weapon.FullName ?? weapon.EditorId ?? ""} | Dmg: {weapon.Damage}, Clip: {weapon.ClipSize} |");
            }
            else if (snapshot.Armor.TryGetValue(formId, out var armor))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | ARMO | {armor.FullName ?? armor.EditorId ?? ""} | DT: {armor.DamageThreshold:F1} |");
            }
            else if (snapshot.Items.TryGetValue(formId, out var item))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | {item.RecordType} | {item.FullName ?? item.EditorId ?? ""} | Value: {item.Value} |");
            }
            else if (snapshot.Scripts.TryGetValue(formId, out var script))
            {
                var hasSource = !string.IsNullOrEmpty(script.SourceText);
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | SCPT | {script.EditorId ?? ""} | {script.ScriptType}, Vars: {script.VariableCount}, Source: {(hasSource ? "Yes" : "No")} |");
            }
            else if (snapshot.Dialogues.TryGetValue(formId, out var dialogue))
            {
                var text = dialogue.ResponseTexts.FirstOrDefault() ?? "";
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | INFO | {dialogue.EditorId ?? ""} | {MarkdownMultiPageWriter.Esc(text)} |");
            }
            else if (snapshot.Locations.TryGetValue(formId, out var location))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | {location.RecordType} | {location.FullName ?? location.EditorId ?? ""} | |");
            }
            else if (snapshot.Creatures.TryGetValue(formId, out var creature))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | CREA | {creature.FullName ?? creature.EditorId ?? ""} | Level: {creature.Level}, Dmg: {creature.AttackDamage} |");
            }
            else if (snapshot.Perks.TryGetValue(formId, out var perk))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | PERK | {perk.FullName ?? perk.EditorId ?? ""} | Ranks: {perk.Ranks}, MinLevel: {perk.MinLevel} |");
            }
            else if (snapshot.Ammo.TryGetValue(formId, out var ammo))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | AMMO | {ammo.FullName ?? ammo.EditorId ?? ""} | Value: {ammo.Value}, Speed: {ammo.Speed:F1} |");
            }
            else if (snapshot.LeveledLists.TryGetValue(formId, out var leveledList))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | {leveledList.ListType} | {leveledList.EditorId ?? ""} | Entries: {leveledList.Entries.Count}, ChanceNone: {leveledList.ChanceNone} |");
            }
            else if (snapshot.Notes.TryGetValue(formId, out var note))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | NOTE | {note.FullName ?? note.EditorId ?? ""} | Type: {note.NoteType} |");
            }
            else if (snapshot.Terminals.TryGetValue(formId, out var terminal))
            {
                sb.AppendLine(
                    $"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | TERM | {terminal.FullName ?? terminal.EditorId ?? ""} | MenuItems: {terminal.MenuItemCount} |");
            }
            else
            {
                sb.AppendLine($"| {MarkdownMultiPageWriter.Esc(snapshot.Build.Label)} | {date} | - | (not found) | |");
            }
        }
    }

    #endregion

    #region Cut Record Data Computation

    internal static Dictionary<string, List<MarkdownMultiPageWriter.CutRecord>> ComputeAllCutRecords(
        List<VersionSnapshot> allSnapshots,
        List<VersionSnapshot> dmpSnapshots,
        VersionSnapshot finalEsm,
        HashSet<uint>? fo3LeftoverFormIds)
    {
        var result = new Dictionary<string, List<MarkdownMultiPageWriter.CutRecord>>();

        result["Quests"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Quests), finalEsm.Quests, allSnapshots, fo3LeftoverFormIds);
        result["NPCs"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Npcs), finalEsm.Npcs, allSnapshots, fo3LeftoverFormIds);
        result["Weapons"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Weapons), finalEsm.Weapons, allSnapshots, fo3LeftoverFormIds);
        result["Armor"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Armor), finalEsm.Armor, allSnapshots, fo3LeftoverFormIds);
        result["Items"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Items), finalEsm.Items, allSnapshots, fo3LeftoverFormIds);
        result["Scripts"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Scripts), finalEsm.Scripts, allSnapshots, fo3LeftoverFormIds);
        result["Dialogues"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Dialogues), finalEsm.Dialogues, allSnapshots, fo3LeftoverFormIds);
        result["Locations"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Locations), finalEsm.Locations, allSnapshots, fo3LeftoverFormIds);
        result["Placements"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Placements), finalEsm.Placements, allSnapshots, fo3LeftoverFormIds);
        result["Creatures"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Creatures), finalEsm.Creatures, allSnapshots, fo3LeftoverFormIds);
        result["Perks"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Perks), finalEsm.Perks, allSnapshots, fo3LeftoverFormIds);
        result["Ammo"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Ammo), finalEsm.Ammo, allSnapshots, fo3LeftoverFormIds);
        result["Leveled Lists"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.LeveledLists), finalEsm.LeveledLists, allSnapshots, fo3LeftoverFormIds);
        result["Notes"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Notes), finalEsm.Notes, allSnapshots, fo3LeftoverFormIds);
        result["Terminals"] = MarkdownMultiPageWriter.FindCutRecords(
            dmpSnapshots.SelectMany(s => s.Terminals), finalEsm.Terminals, allSnapshots, fo3LeftoverFormIds);

        return result;
    }

    #endregion

    #region Significance Classification

    internal static bool HasSignificantChanges(MarkdownMultiPageWriter.RecordHistory history)
    {
        return history.ChangePoints.Any(cp => cp.FieldChanges.Any(f =>
            f.FieldName is "FullName" or "PromptText" or "ResponseTexts" or "SourceText" ||
            f.FieldName.StartsWith("Stage ", StringComparison.Ordinal) ||
            f.FieldName.StartsWith("Objective ", StringComparison.Ordinal) ||
            f.FieldName is "Damage" or "Value" or "DamageThreshold" or "ClipSize" or "Level" ||
            f.FieldName is "WeaponType" or "AmmoFormId" or "MarkerName" ||
            f.FieldName is "AttackDamage" or "CreatureType" or "Description" or "Ranks" or "MinLevel" ||
            f.FieldName is "Speed" or "ChanceNone" or "EntryCount" or "NoteType" or "Text" ||
            f.FieldName is "Difficulty" or "Password" or "HeaderText" or "MenuItemCount"));
    }

    #endregion
}
