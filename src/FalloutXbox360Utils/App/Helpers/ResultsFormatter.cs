using System.Text;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Recovery;

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
        CoverageResult coverage,
        Func<long, string> formatSize,
        IReadOnlyList<DmpGapRecoveryCandidate>? recoverableCandidates = null)
    {
        var candidatesByGap = recoverableCandidates?
            .GroupBy(c => (c.GapFileOffset, c.GapSize))
            .ToDictionary(g => g.Key, g => g.ToList()) ??
                              new Dictionary<(long GapFileOffset, long GapSize), List<DmpGapRecoveryCandidate>>();
        var entries = new List<CoverageGapEntry>(coverage.Gaps.Count);
        for (var i = 0; i < coverage.Gaps.Count; i++)
        {
            var gap = coverage.Gaps[i];
            candidatesByGap.TryGetValue((gap.FileOffset, gap.Size), out var candidates);
            var gapDisplayName = FileTypeColors.GapDisplayNames.GetValueOrDefault(
                gap.Classification, gap.Classification.ToString());
            entries.Add(new CoverageGapEntry
            {
                Index = i + 1,
                FileOffset = $"0x{gap.FileOffset:X8}",
                Size = formatSize(gap.Size),
                Classification = gapDisplayName,
                Recoverable = FormatRecoverableSummary(candidates),
                Evidence = FormatRecoverableEvidence(candidates),
                Context = gap.Context,
                RawFileOffset = gap.FileOffset,
                RawSize = gap.Size
            });
        }

        return entries;
    }

    private static string FormatRecoverableSummary(IReadOnlyList<DmpGapRecoveryCandidate>? candidates)
    {
        if (candidates is not { Count: > 0 })
        {
            return "";
        }

        var promoted = candidates.Count(c => c.Disposition != DmpGapRecoveryDisposition.AuditOnly);
        return promoted > 0
            ? $"{candidates.Count:N0} ({promoted:N0} eligible)"
            : $"{candidates.Count:N0}";
    }

    private static string FormatRecoverableEvidence(IReadOnlyList<DmpGapRecoveryCandidate>? candidates)
    {
        if (candidates is not { Count: > 0 })
        {
            return "";
        }

        return string.Join(", ",
            candidates
                .GroupBy(c => c.DisplayFamily)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(4)
                .Select(g => $"{g.Key} x{g.Count():N0}"));
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
    ///     Builds the record breakdown category definitions from a <see cref="Core.Formats.Esm.Models.RecordCollection" />.
    ///     Returns an array of (category name, record label/count pairs).
    /// </summary>
    internal static (string Name, (string Label, int Count)[] Records)[] BuildRecordBreakdownCategories(
        Core.Formats.Esm.Models.RecordCollection r)
    {
        // Games whose records have no typed list (Morrowind/TES3) land in GenericRecords. Count by
        // type and prefer the typed-list count, falling back to the generic group — so the breakdown
        // shows the same categories for every game. Consumed types are tracked for the catch-all.
        var byType = r.GenericRecords
            .GroupBy(x => x.RecordType, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        int C(int typed, params string[] types)
        {
            if (typed > 0)
            {
                return typed;
            }

            var sum = 0;
            foreach (var t in types)
            {
                if (byType.TryGetValue(t, out var n))
                {
                    sum += n;
                    consumed.Add(t);
                }
            }

            return sum;
        }

        var categories = new List<(string Name, (string Label, int Count)[] Records)>
        {
            ("Characters",
            [
                ("NPCs", C(r.Npcs.Count, "NPC_")), ("Creatures", C(r.Creatures.Count, "CREA")),
                ("Races", C(r.Races.Count, "RACE")), ("Factions", C(r.Factions.Count, "FACT")),
                ("Birthsigns", C(0, "BSGN")), ("Skills", C(0, "SKIL")), ("Body Parts", C(0, "BODY"))
            ]),
            ("Character Appearance",
            [
                ("Eyes", r.Eyes.Count), ("Hair", r.Hair.Count), ("Head Parts", r.HeadParts.Count),
                ("Voice Types", r.VoiceTypes.Count)
            ]),
            ("AI",
            [
                ("AI Packages", r.Packages.Count)
            ]),
            ("Quests & Dialogue",
            [
                ("Quests", r.Quests.Count), ("Dialog Topics", C(r.DialogTopics.Count, "DIAL")),
                ("Dialogue", C(r.Dialogues.Count, "INFO")),
                ("Notes", r.Notes.Count), ("Books", C(r.Books.Count, "BOOK")), ("Terminals", r.Terminals.Count),
                ("Scripts", C(r.Scripts.Count, "SCPT"))
            ]),
            ("Items",
            [
                ("Weapons", C(r.Weapons.Count, "WEAP")), ("Armor", C(r.Armor.Count, "ARMO")),
                ("Clothing", C(0, "CLOT")), ("Ammo", r.Ammo.Count), ("Consumables", r.Consumables.Count),
                ("Potions", C(0, "ALCH")), ("Ingredients", C(r.Ingredients.Count, "INGR")),
                ("Apparatus", C(0, "APPA")), ("Tools", C(0, "REPA", "PROB", "LOCK")),
                ("Misc Items", C(r.MiscItems.Count, "MISC")), ("Keys", r.Keys.Count),
                ("Containers", C(r.Containers.Count, "CONT")), ("Leveled Lists", C(r.LeveledLists.Count, "LEVI", "LEVC"))
            ]),
            ("Abilities",
            [
                ("Perks", r.Perks.Count), ("Spells", C(r.Spells.Count, "SPEL")),
                ("Enchantments", C(r.Enchantments.Count, "ENCH")), ("Magic Effects", C(0, "MGEF")),
                ("Base Effects", r.BaseEffects.Count)
            ]),
            ("World",
            [
                ("Cells", r.Cells.Count), ("Worldspaces", r.Worldspaces.Count),
                ("Map Markers", r.MapMarkers.Count), ("Land Textures", C(r.LandTextures.Count, "LTEX")),
                ("Regions", C(r.Regions.Count, "REGN")),
                ("Statics", C(r.Statics.Count, "STAT")), ("Doors", C(r.Doors.Count, "DOOR")),
                ("Lights", C(r.Lights.Count, "LIGH")),
                ("Furniture", r.Furniture.Count), ("Activators", C(r.Activators.Count, "ACTI"))
            ]),
            ("Gameplay",
            [
                ("Globals", C(r.Globals.Count, "GLOB")), ("Game Settings", C(r.GameSettings.Count, "GMST")),
                ("Classes", C(r.Classes.Count, "CLAS")),
                ("Challenges", r.Challenges.Count), ("Reputations", r.Reputations.Count),
                ("Messages", r.Messages.Count), ("Form Lists", r.FormLists.Count)
            ]),
            ("Audio",
            [
                ("Sounds", C(r.Sounds.Count, "SOUN")), ("Sound Generators", C(0, "SNDG"))
            ]),
            ("Crafting & Combat",
            [
                ("Weapon Mods", r.WeaponMods.Count), ("Recipes", r.Recipes.Count),
                ("Recipe Categories", r.RecipeCategories.Count),
                ("Constructible Objects", r.ConstructibleObjects.Count),
                ("Projectiles", r.Projectiles.Count),
                ("Explosions", r.Explosions.Count)
            ])
        };

        // Any generic types not mapped above (e.g. PGRD/SSCR) — surface them so the breakdown is complete.
        var other = byType
            .Where(kv => !consumed.Contains(kv.Key) && kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToArray();
        if (other.Length > 0)
        {
            categories.Add(("Other Records", other));
        }

        return categories.ToArray();
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
