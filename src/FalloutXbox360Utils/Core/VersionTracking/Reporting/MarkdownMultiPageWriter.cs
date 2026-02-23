using System.Text;
using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Reporting;

/// <summary>
///     Generates a multi-page markdown report: hub/index page, build timeline page,
///     and one page per record category with logical sub-groupings.
/// </summary>
public static class MarkdownMultiPageWriter
{
    #region Types

    internal sealed record ChangePoint(string BuildLabel, DateTimeOffset? BuildDate, List<FieldChange> FieldChanges);

    internal sealed record RecordHistory(
        uint FormId,
        string? EditorId,
        string? FullName,
        string Category,
        List<ChangePoint> ChangePoints);

    internal sealed record CutRecord(uint FormId, string? EditorId, string? Name, string FirstSeen, string LastSeen);

    /// <summary>Defines a record category with accessors for snapshots and diffs.</summary>
    internal sealed record CategoryDef(
        string Name,
        string FileName,
        string Description,
        Func<VersionDiffResult, List<RecordChange>> DiffSelector,
        Func<VersionSnapshot, int> Counter,
        Func<VersionSnapshot, IEnumerable<KeyValuePair<uint, object>>> RecordSelector,
        Func<object, List<VersionSnapshot>, string> GroupClassifier);

    #endregion

    #region Category Definitions

    private static readonly CategoryDef[] Categories =
    [
        new("Quests", "quests.md", "Quest records including stages and objectives",
            d => d.QuestChanges, s => s.Quests.Count,
            s => s.Quests.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.ClassifyQuest(r)),
        new("NPCs", "npcs.md", "Non-player characters",
            d => d.NpcChanges, s => s.Npcs.Count,
            s => s.Npcs.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (_, _) => "NPCs"),
        new("Weapons", "weapons.md", "Weapons grouped by type",
            d => d.WeaponChanges, s => s.Weapons.Count,
            s => s.Weapons.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetWeaponTypeName(r)),
        new("Armor", "armor.md", "Armor and clothing grouped by slot",
            d => d.ArmorChanges, s => s.Armor.Count,
            s => s.Armor.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetArmorSlotGroup(r)),
        new("Items", "items.md", "Consumables, miscellaneous items, and keys",
            d => d.ItemChanges, s => s.Items.Count,
            s => s.Items.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetItemSubtype(r)),
        new("Scripts", "scripts.md", "Game scripts",
            d => d.ScriptChanges, s => s.Scripts.Count,
            s => s.Scripts.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetScriptGroup(r)),
        new("Dialogues", "dialogue.md", "Dialogue lines grouped by parent quest",
            d => d.DialogueChanges, s => s.Dialogues.Count,
            s => s.Dialogues.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, snaps) => MarkdownGroupClassifier.GetDialogueQuestGroup(r, snaps)),
        new("Locations", "locations.md", "Cells and worldspaces",
            d => d.LocationChanges, s => s.Locations.Count,
            s => s.Locations.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetLocationGroup(r)),
        new("Placements", "placements.md", "Placed references and map markers",
            d => d.PlacementChanges, s => s.Placements.Count,
            s => s.Placements.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetPlacementGroup(r)),
        new("Creatures", "creatures.md", "Creatures grouped by type",
            d => d.CreatureChanges, s => s.Creatures.Count,
            s => s.Creatures.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetCreatureTypeGroup(r)),
        new("Perks", "perks.md", "Perks and traits",
            d => d.PerkChanges, s => s.Perks.Count,
            s => s.Perks.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetPerkGroup(r)),
        new("Ammo", "ammo.md", "Ammunition types",
            d => d.AmmoChanges, s => s.Ammo.Count,
            s => s.Ammo.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (_, _) => "Ammo"),
        new("Leveled Lists", "leveled_lists.md", "Leveled lists for creatures, NPCs, and items",
            d => d.LeveledListChanges, s => s.LeveledLists.Count,
            s => s.LeveledLists.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetLeveledListGroup(r)),
        new("Notes", "notes.md", "Notes and holotapes",
            d => d.NoteChanges, s => s.Notes.Count,
            s => s.Notes.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (r, _) => MarkdownGroupClassifier.GetNoteGroup(r)),
        new("Terminals", "terminals.md", "Computer terminals",
            d => d.TerminalChanges, s => s.Terminals.Count,
            s => s.Terminals.Select(kvp => new KeyValuePair<uint, object>(kvp.Key, kvp.Value)),
            (_, _) => "Terminals")
    ];

    #endregion

    #region Public API

    /// <summary>
    ///     Generates a multi-page markdown report.
    ///     Returns a dictionary of filename -> content for the caller to write to disk.
    /// </summary>
    public static Dictionary<string, string> WritePages(
        List<VersionSnapshot> snapshots,
        List<VersionDiffResult> diffs,
        uint? trackFormId = null,
        HashSet<uint>? fo3LeftoverFormIds = null)
    {
        var pages = new Dictionary<string, string>();

        // Pre-compute shared data
        var finalEsm = snapshots.LastOrDefault(s => s.Build.IsAuthoritative);
        var dmpSnapshots = snapshots.Where(s => !s.Build.IsAuthoritative).ToList();
        var histories = BuildAllHistories(snapshots, diffs);
        var cutData = finalEsm != null && dmpSnapshots.Count > 0
            ? MarkdownSectionWriter.ComputeAllCutRecords(snapshots, dmpSnapshots, finalEsm, fo3LeftoverFormIds)
            : new Dictionary<string, List<CutRecord>>();

        // Build index with stats
        var categoryStats = ComputeCategoryStats(finalEsm, histories, cutData);
        pages["index.md"] = WriteIndexPage(snapshots, categoryStats);
        pages["build_timeline.md"] = WriteBuildTimelinePage(snapshots, diffs, trackFormId);

        // Per-category pages
        foreach (var cat in Categories)
        {
            var catHistories = histories
                .Where(kvp => kvp.Key.Category == cat.Name)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            cutData.TryGetValue(cat.Name, out var catCutRecords);
            catCutRecords ??= [];

            var page = WriteCategoryPage(cat, snapshots, catHistories, catCutRecords);
            pages[cat.FileName] = page;
        }

        return pages;
    }

    #endregion

    #region Index Page

    private static string WriteIndexPage(
        List<VersionSnapshot> snapshots,
        List<(string Name, string FileName, int Total, int Cut, int Changed)> stats)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Fallout: New Vegas — Version Tracking Report");
        sb.AppendLine();

        // Overview
        var dates = snapshots
            .Where(s => s.Build.BuildDate.HasValue)
            .Select(s => s.Build.BuildDate!.Value)
            .OrderBy(d => d)
            .ToList();

        var dateRange = dates.Count >= 2
            ? $"{dates[0]:MMMM d, yyyy} to {dates[^1]:MMMM d, yyyy}"
            : "Unknown";

        sb.AppendLine($"Analysis of **{snapshots.Count} builds** spanning {dateRange}.");
        sb.AppendLine();

        // Summary table
        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine("| Category | In Final ESM | Cut | Changed |");
        sb.AppendLine("|----------|-------------|-----|---------|");

        var totalFinal = 0;
        var totalCut = 0;
        var totalChanged = 0;

        foreach (var (name, _, total, cut, changed) in stats)
        {
            sb.AppendLine(
                $"| [{name}]({Categories.First(c => c.Name == name).FileName}) | {total:N0} | {cut:N0} | {changed:N0} |");
            totalFinal += total;
            totalCut += cut;
            totalChanged += changed;
        }

        sb.AppendLine($"| **Total** | **{totalFinal:N0}** | **{totalCut:N0}** | **{totalChanged:N0}** |");
        sb.AppendLine();

        // Sub-page links
        sb.AppendLine("## Pages");
        sb.AppendLine();
        sb.AppendLine($"- [Build Timeline](build_timeline.md) — {snapshots.Count} builds, chronological progression");

        foreach (var (name, fileName, _, cut, changed) in stats)
        {
            var parts = new List<string>();
            if (cut > 0)
            {
                parts.Add($"{cut} cut");
            }

            if (changed > 0)
            {
                parts.Add($"{changed} changed");
            }

            var desc = parts.Count > 0 ? string.Join(", ", parts) : "no changes detected";
            sb.AppendLine($"- [{name}]({fileName}) — {desc}");
        }

        sb.AppendLine();
        return sb.ToString();
    }

    #endregion

    #region Build Timeline Page

    private static string WriteBuildTimelinePage(
        List<VersionSnapshot> snapshots, List<VersionDiffResult> diffs, uint? trackFormId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Build Timeline");
        sb.AppendLine();
        sb.AppendLine("[Back to Index](index.md)");
        sb.AppendLine();

        // Build progression table
        sb.AppendLine("## Build Progression");
        sb.AppendLine();
        sb.AppendLine(
            "| # | Build | Date | Source | Quests | NPCs | Weapons | Armor | Items | Scripts | Dialogues | Locations | Total |");
        sb.AppendLine(
            "|---|-------|------|--------|--------|------|---------|-------|-------|---------|-----------|-----------|-------|");

        for (var i = 0; i < snapshots.Count; i++)
        {
            var s = snapshots[i];
            var date = s.Build.BuildDate?.ToString("yyyy-MM-dd") ?? "Unknown";
            var source = s.Build.SourceType == BuildSourceType.Esm ? "ESM" : "DMP";

            sb.AppendLine($"| {i + 1} | {Esc(s.Build.Label)} | {date} | {source} | " +
                          $"{s.Quests.Count:N0} | {s.Npcs.Count:N0} | {s.Weapons.Count:N0} | " +
                          $"{s.Armor.Count:N0} | {s.Items.Count:N0} | {s.Scripts.Count:N0} | " +
                          $"{s.Dialogues.Count:N0} | {s.Locations.Count:N0} | {s.TotalRecordCount:N0} |");
        }

        sb.AppendLine();

        // Change summary
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

            sb.AppendLine($"| {i + 1} | {Esc(d.FromBuild.Label)} → {Esc(d.ToBuild.Label)} | " +
                          $"{d.TotalAdded:N0} | {d.TotalRemoved:N0} | {d.TotalChanged:N0} | {total:N0} |");
        }

        // FormID history
        if (trackFormId.HasValue)
        {
            sb.AppendLine();
            MarkdownSectionWriter.WriteFormIdHistory(sb, snapshots, trackFormId.Value);
        }

        sb.AppendLine();
        return sb.ToString();
    }

    #endregion

    #region Category Pages

    private static string WriteCategoryPage(
        CategoryDef cat,
        List<VersionSnapshot> snapshots,
        Dictionary<(string Category, uint FormId), RecordHistory> catHistories,
        List<CutRecord> cutRecords)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {cat.Name}");
        sb.AppendLine();
        sb.AppendLine($"> {cat.Description} | [Back to Index](index.md)");
        sb.AppendLine();

        // Overview stats
        var finalEsm = snapshots.LastOrDefault(s => s.Build.IsAuthoritative);
        var totalInFinal = finalEsm != null ? cat.Counter(finalEsm) : 0;

        sb.AppendLine("## Overview");
        sb.AppendLine();
        sb.AppendLine($"- **Total in final ESM**: {totalInFinal:N0}");
        sb.AppendLine($"- **Cut content**: {cutRecords.Count:N0} records");
        sb.AppendLine($"- **Changed across builds**: {catHistories.Count:N0} records");
        sb.AppendLine();

        // Section 1: Cut Content
        MarkdownSectionWriter.WriteCutContentSection(sb, cat, cutRecords, snapshots);

        // Section 2: Changed Content
        MarkdownSectionWriter.WriteChangedContentSection(sb, cat, catHistories, snapshots);

        return sb.ToString();
    }

    #endregion

    #region Data Computation

    private static Dictionary<(string Category, uint FormId), RecordHistory> BuildAllHistories(
        List<VersionSnapshot> snapshots, List<VersionDiffResult> diffs)
    {
        var histories = new Dictionary<(string Category, uint FormId), RecordHistory>();

        for (var i = 0; i < diffs.Count; i++)
        {
            var toBuild = snapshots[i + 1].Build;

            foreach (var cat in Categories)
            {
                foreach (var change in cat.DiffSelector(diffs[i]))
                {
                    if (change.ChangeType != ChangeType.Changed || change.FieldChanges.Count == 0)
                    {
                        continue;
                    }

                    var key = (cat.Name, change.FormId);
                    if (!histories.TryGetValue(key, out var history))
                    {
                        history = new RecordHistory(change.FormId, change.EditorId, change.FullName, cat.Name, []);
                        histories[key] = history;
                    }

                    history.ChangePoints.Add(new ChangePoint(toBuild.Label, toBuild.BuildDate, change.FieldChanges));
                }
            }
        }

        return histories;
    }

    internal static List<CutRecord> FindCutRecords<T>(
        IEnumerable<KeyValuePair<uint, T>> dmpRecords,
        Dictionary<uint, T> finalEsmRecords,
        List<VersionSnapshot> allSnapshots,
        HashSet<uint>? fo3LeftoverFormIds)
    {
        var seen = new Dictionary<uint, CutRecord>();

        foreach (var (formId, record) in dmpRecords)
        {
            if (finalEsmRecords.ContainsKey(formId))
            {
                continue;
            }

            if (fo3LeftoverFormIds != null && fo3LeftoverFormIds.Contains(formId))
            {
                continue;
            }

            if (!seen.ContainsKey(formId))
            {
                var editorId = GetEditorId(record);
                var name = GetName(record);
                var firstSnapshot = allSnapshots.FirstOrDefault(s => ContainsFormId(s, formId));
                var lastSnapshot = allSnapshots.LastOrDefault(s => ContainsFormId(s, formId));

                seen[formId] = new CutRecord(formId, editorId, name,
                    firstSnapshot?.Build.Label ?? "?",
                    lastSnapshot?.Build.Label ?? "?");
            }
        }

        return seen.Values.OrderBy(r => r.FormId).ToList();
    }

    private static List<(string Name, string FileName, int Total, int Cut, int Changed)>
        ComputeCategoryStats(
            VersionSnapshot? finalEsm,
            Dictionary<(string Category, uint FormId), RecordHistory> histories,
            Dictionary<string, List<CutRecord>> cutData)
    {
        var stats = new List<(string, string, int, int, int)>();

        foreach (var cat in Categories)
        {
            var total = finalEsm != null ? cat.Counter(finalEsm) : 0;
            cutData.TryGetValue(cat.Name, out var cutRecords);
            var cut = cutRecords?.Count ?? 0;
            var changed = histories.Count(kvp => kvp.Key.Category == cat.Name);

            stats.Add((cat.Name, cat.FileName, total, cut, changed));
        }

        return stats;
    }

    #endregion

    #region Helpers

    internal static string GetTcrfHeading(string? fullName, string? editorId, uint formId)
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
            TrackedDialogue d => d.EditorId,
            TrackedLocation l => l.EditorId,
            TrackedPlacement p => p.EditorId,
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
            TrackedLocation l => l.FullName,
            TrackedPlacement p => p.MarkerName,
            TrackedCreature c => c.FullName,
            TrackedPerk p => p.FullName,
            TrackedAmmo a => a.FullName,
            TrackedNote n => n.FullName,
            TrackedTerminal t => t.FullName,
            _ => null
        };
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

    /// <summary>Escape pipe characters for markdown table cells.</summary>
    internal static string Esc(string text)
    {
        return text.Replace("|", "\\|");
    }

    #endregion
}
