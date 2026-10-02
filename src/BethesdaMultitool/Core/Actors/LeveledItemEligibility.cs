using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Actors;

/// <summary>Selects authored leveled-item entries using the browser's explicit preview level.</summary>
internal static class LeveledItemEligibility
{
    /// <summary>Retains positive-count entries at the highest eligible tier or at every eligible tier.</summary>
    /// <param name="entries">Entries in authored order; duplicate entries remain independent choices.</param>
    /// <param name="flags">Bit zero allows all lower tiers; bit two optionally selects every level.</param>
    /// <param name="level">Explicit preview level; actor scaling and game-setting cutoffs are not inferred.</param>
    /// <param name="useAll">Whether the caller supports the Use All override.</param>
    /// <returns>A new ordered list containing the eligible entries.</returns>
    internal static List<LeveledEntry> Select(IReadOnlyList<LeveledEntry> entries, byte flags,
        ushort level, bool useAll = false)
    {
        var all = useAll && (flags & 4) != 0;
        var eligible = entries.Where(entry => entry.Count > 0 && (all || entry.Level <= level)).ToList();
        if (!all && (flags & 1) == 0 && eligible.Count > 0)
        {
            var highest = eligible.Max(static entry => entry.Level);
            eligible.RemoveAll(entry => entry.Level != highest);
        }
        return eligible;
    }
}
