using System.Text;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Reporting;

/// <summary>
///     Renders the "Potential Cut Content" section of the markdown timeline report.
///     Records that appear in early DMP snapshots but are absent from the final ESM
///     are identified and written as categorized tables.
/// </summary>
internal static class CutContentTimelineWriter
{
    internal static void WriteCutContentSection(StringBuilder sb, List<VersionSnapshot> snapshots,
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

        var cutQuests = FindCutRecords(dmpSnapshots.SelectMany(s => s.Quests), finalEsm.Quests, snapshots,
            fo3LeftoverFormIds);
        var cutNpcs = FindCutRecords(dmpSnapshots.SelectMany(s => s.Npcs), finalEsm.Npcs, snapshots,
            fo3LeftoverFormIds);
        var cutWeapons = FindCutRecords(dmpSnapshots.SelectMany(s => s.Weapons), finalEsm.Weapons, snapshots,
            fo3LeftoverFormIds);
        var cutArmor = FindCutRecords(dmpSnapshots.SelectMany(s => s.Armor), finalEsm.Armor, snapshots,
            fo3LeftoverFormIds);
        var cutItems = FindCutRecords(dmpSnapshots.SelectMany(s => s.Items), finalEsm.Items, snapshots,
            fo3LeftoverFormIds);
        var cutScripts = FindCutRecords(dmpSnapshots.SelectMany(s => s.Scripts), finalEsm.Scripts, snapshots,
            fo3LeftoverFormIds);
        var cutDialogues = FindCutRecords(dmpSnapshots.SelectMany(s => s.Dialogues), finalEsm.Dialogues, snapshots,
            fo3LeftoverFormIds);
        var cutCreatures = FindCutRecords(dmpSnapshots.SelectMany(s => s.Creatures), finalEsm.Creatures, snapshots,
            fo3LeftoverFormIds);
        var cutPerks = FindCutRecords(dmpSnapshots.SelectMany(s => s.Perks), finalEsm.Perks, snapshots,
            fo3LeftoverFormIds);
        var cutAmmo = FindCutRecords(dmpSnapshots.SelectMany(s => s.Ammo), finalEsm.Ammo, snapshots,
            fo3LeftoverFormIds);
        var cutLeveledLists = FindCutRecords(dmpSnapshots.SelectMany(s => s.LeveledLists), finalEsm.LeveledLists,
            snapshots, fo3LeftoverFormIds);
        var cutNotes = FindCutRecords(dmpSnapshots.SelectMany(s => s.Notes), finalEsm.Notes, snapshots,
            fo3LeftoverFormIds);
        var cutTerminals = FindCutRecords(dmpSnapshots.SelectMany(s => s.Terminals), finalEsm.Terminals, snapshots,
            fo3LeftoverFormIds);

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
        var questGroups =
            new Dictionary<uint, (string QuestName,
                List<(uint FormId, string? EditorId, string? Name, string FirstSeen, string LastSeen)> Dialogues)>();
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
            sb.AppendLine($"#### {MarkdownTimelineWriter.EscapePipes(questName)} (0x{questId:X8})");
            sb.AppendLine();
            sb.AppendLine("| Name | Editor ID | Form ID | First Seen | Last Seen |");
            sb.AppendLine("|------|-----------|---------|------------|-----------|");
            foreach (var (formId, editorId, name, firstSeen, lastSeen) in dialogues)
            {
                sb.AppendLine(
                    $"| {name ?? ""} | {editorId ?? ""} | 0x{formId:X8} | {MarkdownTimelineWriter.EscapePipes(firstSeen)} | {MarkdownTimelineWriter.EscapePipes(lastSeen)} |");
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
                sb.AppendLine(
                    $"| {name ?? ""} | {editorId ?? ""} | 0x{formId:X8} | {MarkdownTimelineWriter.EscapePipes(firstSeen)} | {MarkdownTimelineWriter.EscapePipes(lastSeen)} |");
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
            sb.AppendLine(
                $"| {name ?? ""} | {editorId ?? ""} | 0x{formId:X8} | {MarkdownTimelineWriter.EscapePipes(firstSeen)} | {MarkdownTimelineWriter.EscapePipes(lastSeen)} |");
        }
    }

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
}
