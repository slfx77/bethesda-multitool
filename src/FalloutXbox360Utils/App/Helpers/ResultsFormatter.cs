using System.Text;
using FalloutXbox360Utils.Core.Coverage;

namespace FalloutXbox360Utils;

/// <summary>
///     Pure-computation helpers for formatting coverage results, record breakdowns,
///     and filter text. All methods are static and free of UI dependencies.
/// </summary>
internal static class ResultsFormatter
{
    /// <summary>
    ///     Builds the coverage summary text from a <see cref="CoverageResult" />.
    /// </summary>
    internal static string BuildCoverageSummaryText(CoverageResult coverage)
    {
        var totalRegion = coverage.TotalRegionBytes;

        var sb = new StringBuilder();
        sb.Append($"File size:           {coverage.FileSize,15:N0} bytes\n");
        sb.Append($"Memory regions:      {coverage.TotalMemoryRegions,6:N0}   (total: {totalRegion:N0} bytes)\n");
        sb.Append($"Minidump overhead:   {coverage.MinidumpOverhead,15:N0} bytes\n\n");
        sb.Append(
            $"Recognized data:     {coverage.TotalRecognizedBytes,15:N0} bytes  ({coverage.RecognizedPercent:F1}%)\n");

        foreach (var (cat, bytes) in coverage.CategoryBytes.OrderByDescending(kv => kv.Value))
        {
            var pct = totalRegion > 0 ? bytes * 100.0 / totalRegion : 0;
            var label = cat switch
            {
                CoverageCategory.Header => "Minidump header",
                CoverageCategory.Module => "Modules",
                CoverageCategory.CarvedFile => "Carved files",
                CoverageCategory.EsmRecord => "ESM records",
                _ => cat.ToString()
            };
            sb.Append($"  {label + ":",-19}{bytes,15:N0} bytes  ({pct,5:F1}%)\n");
        }

        sb.Append($"\nUncovered:           {coverage.TotalGapBytes,15:N0} bytes  ({coverage.GapPercent:F1}%)");
        return sb.ToString();
    }

    /// <summary>
    ///     Builds the coverage classification breakdown text from a <see cref="CoverageResult" />.
    /// </summary>
    internal static string BuildCoverageClassificationText(CoverageResult coverage)
    {
        var totalGap = coverage.TotalGapBytes;
        if (totalGap <= 0)
        {
            return "No gaps detected - 100% coverage!";
        }

        var byClass = coverage.Gaps
            .GroupBy(g => g.Classification)
            .Select(g => new { Classification = g.Key, TotalBytes = g.Sum(x => x.Size), Count = g.Count() })
            .OrderByDescending(x => x.TotalBytes);

        var sb = new StringBuilder();
        foreach (var entry in byClass)
        {
            var pct = totalGap > 0 ? entry.TotalBytes * 100.0 / totalGap : 0;
            var displayName = FileTypeColors.GapDisplayNames.GetValueOrDefault(
                entry.Classification, entry.Classification.ToString());
            sb.Append(
                $"{displayName + ":",-18}{entry.TotalBytes,15:N0} bytes  ({pct,5:F1}%)  - {entry.Count:N0} regions\n");
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Builds the list of <see cref="CoverageGapEntry" /> view models from coverage gaps.
    /// </summary>
    internal static List<CoverageGapEntry> BuildCoverageGapEntries(
        CoverageResult coverage, Func<long, string> formatSize)
    {
        var entries = new List<CoverageGapEntry>(coverage.Gaps.Count);
        for (var i = 0; i < coverage.Gaps.Count; i++)
        {
            var gap = coverage.Gaps[i];
            var gapDisplayName = FileTypeColors.GapDisplayNames.GetValueOrDefault(
                gap.Classification, gap.Classification.ToString());
            entries.Add(new CoverageGapEntry
            {
                Index = i + 1,
                FileOffset = $"0x{gap.FileOffset:X8}",
                Size = formatSize(gap.Size),
                Classification = gapDisplayName,
                Context = gap.Context,
                RawFileOffset = gap.FileOffset,
                RawSize = gap.Size
            });
        }

        return entries;
    }

    /// <summary>
    ///     Sorts coverage gap entries by the specified column and direction.
    /// </summary>
    internal static IEnumerable<CoverageGapEntry> SortCoverageGaps(
        List<CoverageGapEntry> gaps, CoverageGapSortColumn column, bool ascending)
    {
        return column switch
        {
            CoverageGapSortColumn.Offset => ascending
                ? gaps.OrderBy(g => g.RawFileOffset)
                : gaps.OrderByDescending(g => g.RawFileOffset),
            CoverageGapSortColumn.Size => ascending
                ? gaps.OrderBy(g => g.RawSize)
                : gaps.OrderByDescending(g => g.RawSize),
            CoverageGapSortColumn.Classification => ascending
                ? gaps.OrderBy(g => g.Classification, StringComparer.OrdinalIgnoreCase)
                : gaps.OrderByDescending(g => g.Classification, StringComparer.OrdinalIgnoreCase),
            _ => ascending
                ? gaps.OrderBy(g => g.Index)
                : gaps.OrderByDescending(g => g.Index)
        };
    }

    /// <summary>
    ///     Advances the coverage gap sort state (column + direction) through the three-state cycle:
    ///     ascending -> descending -> reset to default (Index ascending).
    /// </summary>
    internal static (CoverageGapSortColumn Column, bool Ascending) CycleCoverageSortState(
        CoverageGapSortColumn currentColumn, bool currentAscending, CoverageGapSortColumn clickedColumn)
    {
        if (currentColumn == clickedColumn)
        {
            if (currentAscending)
            {
                return (clickedColumn, false);
            }

            return (CoverageGapSortColumn.Index, true);
        }

        return (clickedColumn, true);
    }

    /// <summary>
    ///     Builds the record breakdown category definitions from a <see cref="RecordCollection" />.
    ///     Returns an array of (category name, record label/count pairs).
    /// </summary>
    internal static (string Name, (string Label, int Count)[] Records)[] BuildRecordBreakdownCategories(
        Core.Formats.Esm.Models.RecordCollection r)
    {
        return
        [
            ("Characters",
            [
                ("NPCs", r.Npcs.Count), ("Creatures", r.Creatures.Count), ("Races", r.Races.Count),
                ("Factions", r.Factions.Count)
            ]),
            ("AI",
            [
                ("AI Packages", r.Packages.Count)
            ]),
            ("Quests & Dialogue",
            [
                ("Quests", r.Quests.Count), ("Dialog Topics", r.DialogTopics.Count),
                ("Dialogue", r.Dialogues.Count),
                ("Notes", r.Notes.Count), ("Books", r.Books.Count), ("Terminals", r.Terminals.Count),
                ("Scripts", r.Scripts.Count)
            ]),
            ("Items",
            [
                ("Weapons", r.Weapons.Count), ("Armor", r.Armor.Count), ("Ammo", r.Ammo.Count),
                ("Consumables", r.Consumables.Count), ("Misc Items", r.MiscItems.Count), ("Keys", r.Keys.Count),
                ("Containers", r.Containers.Count), ("Leveled Lists", r.LeveledLists.Count)
            ]),
            ("Abilities",
            [
                ("Perks", r.Perks.Count), ("Spells", r.Spells.Count), ("Enchantments", r.Enchantments.Count),
                ("Base Effects", r.BaseEffects.Count)
            ]),
            ("World",
            [
                ("Cells", r.Cells.Count), ("Worldspaces", r.Worldspaces.Count),
                ("Map Markers", r.MapMarkers.Count),
                ("Statics", r.Statics.Count), ("Doors", r.Doors.Count), ("Lights", r.Lights.Count),
                ("Furniture", r.Furniture.Count), ("Activators", r.Activators.Count)
            ]),
            ("Gameplay",
            [
                ("Globals", r.Globals.Count), ("Game Settings", r.GameSettings.Count),
                ("Classes", r.Classes.Count),
                ("Challenges", r.Challenges.Count), ("Reputations", r.Reputations.Count),
                ("Messages", r.Messages.Count), ("Form Lists", r.FormLists.Count)
            ]),
            ("Crafting & Combat",
            [
                ("Weapon Mods", r.WeaponMods.Count), ("Recipes", r.Recipes.Count),
                ("Projectiles", r.Projectiles.Count),
                ("Explosions", r.Explosions.Count)
            ])
        ];
    }

    /// <summary>
    ///     Returns unparsed record types sorted by count descending,
    ///     as (type name, count) tuples suitable for UI display.
    /// </summary>
    internal static (string Label, int Count)[] GetUnparsedRecords(
        Dictionary<string, int> unparsedTypeCounts)
    {
        return unparsedTypeCounts
            .OrderByDescending(x => x.Value)
            .Select(x => (x.Key, x.Value))
            .ToArray();
    }

    /// <summary>
    ///     Computes the display text for the results type filter dropdown button.
    /// </summary>
    internal static string ComputeFilterButtonText(int checkedCount, int total)
    {
        return (checkedCount, total) switch
        {
            _ when checkedCount == total => "Filter: All types",
            (0, _) => "Filter: None",
            _ => $"Filter: {checkedCount} of {total} types"
        };
    }
}
