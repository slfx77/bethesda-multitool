using FalloutXbox360Utils.Core.VersionTracking.Models;

namespace FalloutXbox360Utils.Core.VersionTracking.Processing;

/// <summary>
///     Merges multiple DMP snapshots with the same build timestamp into a single coalesced snapshot.
///     ESM snapshots pass through unchanged.
/// </summary>
public static class SnapshotCoalescer
{
    /// <summary>
    ///     Groups snapshots by build timestamp and coalesces DMP groups.
    ///     Returns one snapshot per distinct build, ordered by date.
    /// </summary>
    public static List<VersionSnapshot> Coalesce(List<VersionSnapshot> snapshots)
    {
        var result = new List<VersionSnapshot>();

        // ESM snapshots pass through unchanged
        var esmSnapshots = snapshots.Where(s => s.Build.SourceType == BuildSourceType.Esm).ToList();
        result.AddRange(esmSnapshots);

        // Group DMP snapshots by PeTimestamp
        var dmpSnapshots = snapshots.Where(s => s.Build.SourceType == BuildSourceType.Dmp).ToList();
        var groups = dmpSnapshots
            .GroupBy(s => s.Build.PeTimestamp ?? 0)
            .ToList();

        foreach (var group in groups)
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                result.Add(members[0]);
            }
            else
            {
                result.Add(MergeGroup(members));
            }
        }

        return result
            .OrderBy(s => s.Build.BuildDate ?? DateTimeOffset.MaxValue)
            .ToList();
    }

    /// <summary>
    ///     Merges all provided snapshots into a single combined snapshot by unioning records by FormID.
    ///     Useful for creating a single "all memory dumps" snapshot from multiple coalesced builds.
    /// </summary>
    public static VersionSnapshot MergeAll(List<VersionSnapshot> snapshots, string label)
    {
        if (snapshots.Count == 0)
        {
            throw new ArgumentException("Cannot merge empty list of snapshots.", nameof(snapshots));
        }

        if (snapshots.Count == 1)
        {
            return snapshots[0] with
            {
                Build = snapshots[0].Build with { Label = label }
            };
        }

        // Use the earliest build date and collect all source paths
        var earliest = snapshots
            .Where(s => s.Build.BuildDate.HasValue)
            .MinBy(s => s.Build.BuildDate!.Value);

        var baseBuild = (earliest ?? snapshots[0]).Build;

        // Count total original DMP files (expand coalesced source paths)
        var allSourceFiles = snapshots
            .SelectMany(s => s.Build.SourcePath.Split(';', StringSplitOptions.TrimEntries))
            .Distinct()
            .ToList();

        var mergedBuild = baseBuild with
        {
            Label = label,
            SourcePath = string.Join("; ", allSourceFiles)
        };

        return new VersionSnapshot
        {
            Build = mergedBuild,
            Quests = MergeDicts(snapshots.Select(s => s.Quests)),
            Npcs = MergeDicts(snapshots.Select(s => s.Npcs)),
            Dialogues = MergeDicts(snapshots.Select(s => s.Dialogues)),
            Weapons = MergeDicts(snapshots.Select(s => s.Weapons)),
            Armor = MergeDicts(snapshots.Select(s => s.Armor)),
            Items = MergeDicts(snapshots.Select(s => s.Items)),
            Scripts = MergeDicts(snapshots.Select(s => s.Scripts)),
            Locations = MergeDicts(snapshots.Select(s => s.Locations)),
            Placements = MergeDicts(snapshots.Select(s => s.Placements)),
            Creatures = MergeDicts(snapshots.Select(s => s.Creatures)),
            Perks = MergeDicts(snapshots.Select(s => s.Perks)),
            Ammo = MergeDicts(snapshots.Select(s => s.Ammo)),
            LeveledLists = MergeDicts(snapshots.Select(s => s.LeveledLists)),
            Notes = MergeDicts(snapshots.Select(s => s.Notes)),
            Terminals = MergeDicts(snapshots.Select(s => s.Terminals)),
            ExtractedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    ///     Merges multiple snapshots into one by unioning records by FormID.
    ///     On conflict (same FormID in multiple snapshots), keeps the record with more data.
    /// </summary>
    private static VersionSnapshot MergeGroup(List<VersionSnapshot> group)
    {
        var first = group[0];
        var label = BuildRangeLabel(group);

        var mergedBuild = first.Build with
        {
            Label = label,
            SourcePath = string.Join("; ", group.Select(s => Path.GetFileName(s.Build.SourcePath)))
        };

        return new VersionSnapshot
        {
            Build = mergedBuild,
            Quests = MergeDicts(group.Select(s => s.Quests)),
            Npcs = MergeDicts(group.Select(s => s.Npcs)),
            Dialogues = MergeDicts(group.Select(s => s.Dialogues)),
            Weapons = MergeDicts(group.Select(s => s.Weapons)),
            Armor = MergeDicts(group.Select(s => s.Armor)),
            Items = MergeDicts(group.Select(s => s.Items)),
            Scripts = MergeDicts(group.Select(s => s.Scripts)),
            Locations = MergeDicts(group.Select(s => s.Locations)),
            Placements = MergeDicts(group.Select(s => s.Placements)),
            Creatures = MergeDicts(group.Select(s => s.Creatures)),
            Perks = MergeDicts(group.Select(s => s.Perks)),
            Ammo = MergeDicts(group.Select(s => s.Ammo)),
            LeveledLists = MergeDicts(group.Select(s => s.LeveledLists)),
            Notes = MergeDicts(group.Select(s => s.Notes)),
            Terminals = MergeDicts(group.Select(s => s.Terminals)),
            ExtractedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    ///     Builds a range label from DMP filenames using natural sort order,
    ///     e.g. "Fallout_Release_Beta.xex3.dmp – xex5.dmp".
    /// </summary>
    private static string BuildRangeLabel(List<VersionSnapshot> group)
    {
        // Natural sort: extract trailing number from filename for numeric ordering
        var sorted = group
            .Select(s => s.Build.Label)
            .OrderBy(l => l, NaturalStringComparer.Instance)
            .ToList();

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var firstName = sorted[0];
        var lastName = sorted[^1];

        // Find common prefix, but don't cut into a digit sequence
        var commonPrefix = 0;
        var minLen = Math.Min(firstName.Length, lastName.Length);
        for (var i = 0; i < minLen; i++)
        {
            if (firstName[i] == lastName[i])
            {
                commonPrefix = i + 1;
            }
            else
            {
                break;
            }
        }

        // Back up to before any digit run we might have split
        while (commonPrefix > 0 && char.IsDigit(lastName[commonPrefix - 1]))
        {
            commonPrefix--;
        }

        if (commonPrefix > 0 && commonPrefix < lastName.Length)
        {
            return $"{firstName} – {lastName[commonPrefix..]}";
        }

        return $"{firstName} – {lastName}";
    }

    /// <summary>
    ///     Natural string comparer that sorts embedded numbers numerically
    ///     (e.g., "xex2" before "xex10").
    /// </summary>
    private sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x == y) { return 0; }
            if (x == null) { return -1; }
            if (y == null) { return 1; }

            var ix = 0;
            var iy = 0;

            while (ix < x.Length && iy < y.Length)
            {
                if (char.IsDigit(x[ix]) && char.IsDigit(y[iy]))
                {
                    // Compare digit runs numerically
                    var nx = ParseDigitRun(x, ref ix);
                    var ny = ParseDigitRun(y, ref iy);
                    var cmp = nx.CompareTo(ny);
                    if (cmp != 0) { return cmp; }
                }
                else
                {
                    var cmp = char.ToLowerInvariant(x[ix]).CompareTo(char.ToLowerInvariant(y[iy]));
                    if (cmp != 0) { return cmp; }
                    ix++;
                    iy++;
                }
            }

            return x.Length.CompareTo(y.Length);
        }

        private static long ParseDigitRun(string s, ref int pos)
        {
            var start = pos;
            while (pos < s.Length && char.IsDigit(s[pos]))
            {
                pos++;
            }

            return long.Parse(s.AsSpan(start, pos - start));
        }
    }

    /// <summary>
    ///     Merges multiple dictionaries by FormID. First non-null value wins.
    /// </summary>
    private static Dictionary<uint, T> MergeDicts<T>(IEnumerable<Dictionary<uint, T>> dicts)
    {
        var merged = new Dictionary<uint, T>();
        foreach (var dict in dicts)
        {
            foreach (var (key, value) in dict)
            {
                merged.TryAdd(key, value);
            }
        }

        return merged;
    }
}
