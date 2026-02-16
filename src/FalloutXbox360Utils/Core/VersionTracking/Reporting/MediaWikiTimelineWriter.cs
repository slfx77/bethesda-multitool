using System.Text;
using FalloutXbox360Utils.Core.VersionTracking.Models;
using FalloutXbox360Utils.Core.VersionTracking.Processing;

namespace FalloutXbox360Utils.Core.VersionTracking.Reporting;

/// <summary>
///     Generates TCRF-formatted MediaWiki pages from version tracking data.
///     Produces .mw files suitable for the Cutting Room Floor wiki.
/// </summary>
public static class MediaWikiTimelineWriter
{
    private const int CollapsibleThreshold = 20;

    private static readonly (string Label, Func<VersionDiffResult, List<RecordChange>> Selector,
        Func<VersionSnapshot, int> Counter)[] Categories =
    [
        ("Quests", d => d.QuestChanges, s => s.Quests.Count),
        ("NPCs", d => d.NpcChanges, s => s.Npcs.Count),
        ("Dialogues", d => d.DialogueChanges, s => s.Dialogues.Count),
        ("Weapons", d => d.WeaponChanges, s => s.Weapons.Count),
        ("Armor", d => d.ArmorChanges, s => s.Armor.Count),
        ("Items", d => d.ItemChanges, s => s.Items.Count),
        ("Scripts", d => d.ScriptChanges, s => s.Scripts.Count),
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
    ///     Generates a TCRF prototype wiki page comparing a build against a baseline.
    ///     The diff direction is baseline → target, so:
    ///     - "Added" in diff = records in target but not baseline = present in this build, cut by final
    ///     - "Removed" in diff = records in baseline but not target = added after this build
    ///     - "Changed" = records in both with field-level differences
    /// </summary>
    public static string WriteBuildPage(
        VersionSnapshot targetBuild,
        VersionSnapshot baselineBuild,
        VersionDiffResult diff,
        string pageTitle,
        string introText,
        bool isDmpPage,
        HashSet<uint>? fo3LeftoverFormIds = null)
    {
        var sb = new StringBuilder();

        // Page header
        sb.AppendLine("{{subpage}}");
        sb.AppendLine("{{todo|This page was auto-generated from extracted game data and requires human review. " +
                       "Verify all entries, add context and screenshots where appropriate.}}");
        sb.AppendLine();
        sb.AppendLine(introText);
        sb.AppendLine();

        // Diff direction: we called Diff(baseline, target)
        // So "Added" = in target, not in baseline → these are records CUT from the final (present in this earlier build)
        // "Removed" = in baseline, not in target → these were ADDED after this build
        // "Changed" = present in both but different

        var cutRecords = GetRecordsByChangeType(diff, ChangeType.Added);
        var changedRecords = GetRecordsByChangeType(diff, ChangeType.Changed);
        var addedAfterRecords = GetRecordsByChangeType(diff, ChangeType.Removed);

        // Filter FO3 leftovers from cut content
        if (fo3LeftoverFormIds is { Count: > 0 })
        {
            FilterLeftovers(cutRecords, fo3LeftoverFormIds);
        }

        // Section 1: Cut Content (most interesting)
        WriteCutContentSection(sb, cutRecords, isDmpPage, targetBuild);

        // Section 2: Changed Content
        WriteChangedContentSection(sb, changedRecords, targetBuild);

        // Section 3: Added After This Build
        WriteAddedAfterSection(sb, addedAfterRecords);

        // Statistics table
        WriteStatisticsSection(sb, targetBuild, baselineBuild, diff);

        return sb.ToString();
    }

    /// <summary>
    ///     Generates a TCRF page comparing a build against a baseline using
    ///     SnapshotDiffer directly. Convenience overload.
    /// </summary>
    public static string WriteBuildPage(
        VersionSnapshot targetBuild,
        VersionSnapshot baselineBuild,
        string pageTitle,
        string introText,
        bool isDmpPage,
        HashSet<uint>? fo3LeftoverFormIds = null)
    {
        var diff = SnapshotDiffer.Diff(baselineBuild, targetBuild);
        return WriteBuildPage(targetBuild, baselineBuild, diff, pageTitle, introText, isDmpPage, fo3LeftoverFormIds);
    }

    private static Dictionary<string, List<RecordChange>> GetRecordsByChangeType(
        VersionDiffResult diff, ChangeType changeType)
    {
        var result = new Dictionary<string, List<RecordChange>>();

        foreach (var (label, selector, _) in Categories)
        {
            var records = selector(diff).Where(c => c.ChangeType == changeType).ToList();
            if (records.Count > 0)
            {
                result[label] = records;
            }
        }

        return result;
    }

    private static void FilterLeftovers(
        Dictionary<string, List<RecordChange>> records, HashSet<uint> fo3LeftoverFormIds)
    {
        foreach (var key in records.Keys.ToList())
        {
            records[key] = records[key].Where(r => !fo3LeftoverFormIds.Contains(r.FormId)).ToList();
            if (records[key].Count == 0)
            {
                records.Remove(key);
            }
        }
    }

    #region Cut Content Section

    private static void WriteCutContentSection(
        StringBuilder sb, Dictionary<string, List<RecordChange>> cutRecords, bool isDmpPage,
        VersionSnapshot targetBuild)
    {
        if (cutRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("==Cut Content==");
        if (isDmpPage)
        {
            sb.AppendLine("Records found in the memory dumps that are absent from the final release. " +
                           "Due to the incomplete nature of memory dumps, some entries may be fragments.");
        }
        else
        {
            sb.AppendLine("Content present in this build that was removed before the final release.");
        }

        sb.AppendLine();

        foreach (var (category, records) in cutRecords.OrderByDescending(kvp => GetCategoryPriority(kvp.Key)))
        {
            sb.AppendLine($"==={category}===");
            if (category == "Dialogues")
            {
                WriteCutDialoguesGroupedByQuest(sb, records, targetBuild);
            }
            else
            {
                WriteCutWikiTable(sb, records);
            }

            sb.AppendLine();
        }
    }

    private static void WriteCutWikiTable(StringBuilder sb, List<RecordChange> records)
    {
        var sorted = records.OrderBy(r => r.FormId).ToList();
        var isCollapsible = sorted.Count > CollapsibleThreshold;

        if (isCollapsible)
        {
            sb.AppendLine($"<div class=\"mw-collapsible mw-collapsed\">");
            sb.AppendLine($"{sorted.Count} entries");
            sb.AppendLine("<div class=\"mw-collapsible-content\">");
        }

        sb.AppendLine("{| class=\"wikitable\"");
        sb.AppendLine("! Name !! Editor ID !! Form ID");

        foreach (var record in sorted)
        {
            sb.AppendLine("|-");
            sb.AppendLine($"| {EscapeWiki(record.FullName ?? "")} || {EscapeWiki(record.EditorId ?? "")} || {FormatFormIdHex(record.FormId)}");
        }

        sb.AppendLine("|}");

        if (isCollapsible)
        {
            sb.AppendLine("</div>");
            sb.AppendLine("</div>");
        }
    }

    private static void WriteCutDialoguesGroupedByQuest(
        StringBuilder sb, List<RecordChange> records, VersionSnapshot targetBuild)
    {
        // Group dialogue records by their parent quest
        var questGroups = new Dictionary<uint, (string QuestName, List<RecordChange> Dialogues)>();
        var orphans = new List<RecordChange>();

        foreach (var record in records)
        {
            if (targetBuild.Dialogues.TryGetValue(record.FormId, out var dialogue) &&
                dialogue.QuestFormId.HasValue)
            {
                var questId = dialogue.QuestFormId.Value;
                if (!questGroups.TryGetValue(questId, out var group))
                {
                    var questName = targetBuild.Quests.TryGetValue(questId, out var quest)
                        ? quest.FullName ?? quest.EditorId ?? $"Quest {FormatFormIdHex(questId)}"
                        : $"Quest {FormatFormIdHex(questId)}";
                    group = (questName, []);
                    questGroups[questId] = group;
                }

                group.Dialogues.Add(record);
            }
            else
            {
                orphans.Add(record);
            }
        }

        // Write grouped dialogues
        foreach (var (questId, (questName, dialogues)) in questGroups
                     .OrderByDescending(kvp => kvp.Value.Dialogues.Count))
        {
            sb.AppendLine($"===={EscapeWiki(questName)}====");
            sb.AppendLine($"Form ID: {FormatFormIdHex(questId)}");
            sb.AppendLine();
            WriteCutWikiTable(sb, dialogues);
            sb.AppendLine();
        }

        // Write orphan dialogues
        if (orphans.Count > 0)
        {
            sb.AppendLine("====Other Dialogues====");
            WriteCutWikiTable(sb, orphans);
            sb.AppendLine();
        }
    }

    #endregion

    #region Changed Content Section

    private static void WriteChangedContentSection(
        StringBuilder sb, Dictionary<string, List<RecordChange>> changedRecords,
        VersionSnapshot targetBuild)
    {
        if (changedRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("==Changed Content==");
        sb.AppendLine("Records present in both this build and the final release, but with differences.");
        sb.AppendLine();

        foreach (var (category, records) in changedRecords.OrderByDescending(kvp => GetCategoryPriority(kvp.Key)))
        {
            // Separate "significant" changes (name, stages, objectives, content) from minor ones (flags, padding)
            var significant = records.Where(r => HasSignificantChanges(r)).ToList();
            var minor = records.Where(r => !HasSignificantChanges(r)).ToList();

            if (significant.Count > 0)
            {
                sb.AppendLine($"==={category}===");
                if (category == "Dialogues")
                {
                    WriteChangedDialoguesGroupedByQuest(sb, significant, targetBuild);
                }
                else
                {
                    WriteChangedRecordSections(sb, significant);
                }

                sb.AppendLine();
            }

            if (minor.Count > 0)
            {
                sb.AppendLine($"===Minor {category} Changes===");
                sb.AppendLine("<div class=\"mw-collapsible mw-collapsed\">");
                sb.AppendLine($"{minor.Count} minor field changes");
                sb.AppendLine("<div class=\"mw-collapsible-content\">");
                if (category == "Dialogues")
                {
                    WriteChangedDialoguesGroupedByQuest(sb, minor, targetBuild);
                }
                else
                {
                    WriteChangedRecordSections(sb, minor);
                }

                sb.AppendLine("</div>");
                sb.AppendLine("</div>");
                sb.AppendLine();
            }
        }
    }

    private static void WriteChangedRecordSections(StringBuilder sb, List<RecordChange> records)
    {
        foreach (var record in records.OrderBy(r => r.FormId))
        {
            // TCRF heading: Name-first, then FormID on next line
            var heading = GetTcrfHeading(record.FullName, record.EditorId, record.FormId);
            sb.AppendLine($"===={heading}====");
            sb.AppendLine($"Form ID: {FormatFormIdHex(record.FormId)}");
            if (!string.IsNullOrEmpty(record.EditorId))
            {
                sb.AppendLine($"Editor ID: {EscapeWiki(record.EditorId)}");
            }

            sb.AppendLine();

            // Per-field table: Field | This Build | Final
            sb.AppendLine("{| class=\"wikitable\"");
            sb.AppendLine("! Field !! This Build !! Final");

            foreach (var fc in record.FieldChanges)
            {
                sb.AppendLine("|-");
                var oldVal = EscapeWiki(fc.OldValue ?? "(none)");
                var newVal = EscapeWiki(fc.NewValue ?? "(none)");
                sb.AppendLine($"| '''{EscapeWiki(fc.FieldName)}''' || {newVal} || {oldVal}");
            }

            sb.AppendLine("|}");
            sb.AppendLine();
        }
    }

    private static void WriteChangedDialoguesGroupedByQuest(
        StringBuilder sb, List<RecordChange> records, VersionSnapshot targetBuild)
    {
        var questGroups = new Dictionary<uint, (string QuestName, List<RecordChange> Dialogues)>();
        var orphans = new List<RecordChange>();

        foreach (var record in records)
        {
            if (targetBuild.Dialogues.TryGetValue(record.FormId, out var dialogue) &&
                dialogue.QuestFormId.HasValue)
            {
                var questId = dialogue.QuestFormId.Value;
                if (!questGroups.TryGetValue(questId, out var group))
                {
                    var questName = targetBuild.Quests.TryGetValue(questId, out var quest)
                        ? quest.FullName ?? quest.EditorId ?? $"Quest {FormatFormIdHex(questId)}"
                        : $"Quest {FormatFormIdHex(questId)}";
                    group = (questName, []);
                    questGroups[questId] = group;
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
            sb.AppendLine($"===={EscapeWiki(questName)}====");
            sb.AppendLine($"Quest Form ID: {FormatFormIdHex(questId)}");
            sb.AppendLine();
            WriteChangedRecordSections(sb, dialogues);
        }

        if (orphans.Count > 0)
        {
            sb.AppendLine("====Other Dialogues====");
            WriteChangedRecordSections(sb, orphans);
        }
    }

    #endregion

    #region Added After Section

    private static void WriteAddedAfterSection(
        StringBuilder sb, Dictionary<string, List<RecordChange>> addedAfterRecords)
    {
        if (addedAfterRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("==Added After This Build==");
        sb.AppendLine("Records present in the final release that were not yet in this build.");
        sb.AppendLine();

        foreach (var (category, records) in addedAfterRecords.OrderByDescending(kvp => GetCategoryPriority(kvp.Key)))
        {
            sb.AppendLine($"==={category}===");
            WriteCutWikiTable(sb, records); // Same table format as cut content
            sb.AppendLine();
        }
    }

    #endregion

    #region Statistics Section

    private static void WriteStatisticsSection(
        StringBuilder sb, VersionSnapshot target, VersionSnapshot baseline, VersionDiffResult diff)
    {
        sb.AppendLine("==Statistics==");
        sb.AppendLine("{| class=\"wikitable\"");
        sb.AppendLine("! Category !! This Build !! Final !! Cut !! Added After !! Changed");

        foreach (var (label, selector, counter) in Categories)
        {
            var changes = selector(diff);
            if (changes.Count == 0 && counter(target) == 0 && counter(baseline) == 0)
            {
                continue;
            }

            // Remember: diff is Diff(baseline, target)
            // "Added" in diff = in target, not baseline = cut from final
            // "Removed" in diff = in baseline, not target = added after this build
            var cut = changes.Count(c => c.ChangeType == ChangeType.Added);
            var addedAfter = changes.Count(c => c.ChangeType == ChangeType.Removed);
            var changed = changes.Count(c => c.ChangeType == ChangeType.Changed);

            sb.AppendLine("|-");
            sb.AppendLine($"| {label} || {counter(target):N0} || {counter(baseline):N0} || " +
                          $"{cut} || {addedAfter} || {changed}");
        }

        sb.AppendLine("|}");
        sb.AppendLine();
    }

    #endregion

    #region Helpers

    private static bool HasSignificantChanges(RecordChange record)
    {
        // Consider a change "significant" if it involves name, text content, stages, objectives, or script changes
        return record.FieldChanges.Any(f =>
            f.FieldName is "FullName" or "PromptText" or "ResponseTexts" or "SourceText" ||
            f.FieldName.StartsWith("Stage ", StringComparison.Ordinal) ||
            f.FieldName.StartsWith("Objective ", StringComparison.Ordinal) ||
            f.FieldName is "Damage" or "Value" or "DamageThreshold" or "ClipSize" or "Level" ||
            f.FieldName is "WeaponType" or "AmmoFormId");
    }

    private static string GetTcrfHeading(string? fullName, string? editorId, uint formId)
    {
        if (!string.IsNullOrEmpty(fullName))
        {
            return EscapeWiki(fullName);
        }

        if (!string.IsNullOrEmpty(editorId))
        {
            return EscapeWiki(editorId);
        }

        return FormatFormIdHex(formId);
    }

    /// <summary>
    ///     Formats a FormID as TCRF {{hex|...}} template. Leading zeros stripped.
    ///     NOT passed through EscapeWiki since {{ }} is intentional template syntax.
    /// </summary>
    private static string FormatFormIdHex(uint formId)
    {
        return $"{{{{hex|{formId:X}}}}}";
    }

    private static string EscapeWiki(string text)
    {
        // Escape characters that have special meaning in MediaWiki tables
        return text
            .Replace("|", "&#124;")
            .Replace("{{", "&#123;&#123;")
            .Replace("}}", "&#125;&#125;")
            .Replace("[[", "&#91;&#91;")
            .Replace("]]", "&#93;&#93;");
    }

    private static int GetCategoryPriority(string category)
    {
        // Higher priority = listed first (most interesting content)
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

    #endregion
}
