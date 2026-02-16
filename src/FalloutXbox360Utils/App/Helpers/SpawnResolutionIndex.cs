using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     NearRef package location: a reference FormID with a search radius.
/// </summary>
internal record PackageRefLocation(uint RefFormId, int Radius);

/// <summary>
///     Resolves leveled lists and AI packages to determine NPC/Creature spawn information.
///     Built from RecordCollection on a background thread.
/// </summary>
internal sealed class SpawnResolutionIndex
{
    /// <summary>Leveled list FormID → resolved actor FormIDs (recursive, flattened).</summary>
    public Dictionary<uint, List<uint>> LeveledListEntries { get; } = [];

    /// <summary>Leveled list FormID → list type ("LVLN" or "LVLC").</summary>
    public Dictionary<uint, string> LeveledListTypes { get; } = [];

    /// <summary>Actor FormID → cell FormIDs from InCell (type 1) AI packages.</summary>
    public Dictionary<uint, List<uint>> ActorToPackageCells { get; } = [];

    /// <summary>Actor FormID → NearRef locations from NearRef (type 0) AI packages.</summary>
    public Dictionary<uint, List<PackageRefLocation>> ActorToPackageRefs { get; } = [];

    /// <summary>
    ///     Builds the spawn resolution index from a record collection.
    ///     Can be called from a background thread.
    /// </summary>
    public static SpawnResolutionIndex Build(RecordCollection records)
    {
        var index = new SpawnResolutionIndex();

        // Index packages by FormID for lookup
        var packageById = new Dictionary<uint, PackageRecord>();
        foreach (var pkg in records.Packages)
        {
            packageById.TryAdd(pkg.FormId, pkg);
        }

        // Index leveled lists by FormID
        var leveledListById = new Dictionary<uint, LeveledListRecord>();
        foreach (var ll in records.LeveledLists)
        {
            if (ll.ListType is "LVLN" or "LVLC")
            {
                leveledListById.TryAdd(ll.FormId, ll);
                index.LeveledListTypes.TryAdd(ll.FormId, ll.ListType);
            }
        }

        // Resolve leveled lists recursively
        foreach (var ll in records.LeveledLists)
        {
            if (ll.ListType is not ("LVLN" or "LVLC"))
            {
                continue;
            }

            var resolved = new List<uint>();
            var visited = new HashSet<uint>();
            ResolveLeveledList(ll.FormId, leveledListById, resolved, visited, maxDepth: 8);
            if (resolved.Count > 0)
            {
                index.LeveledListEntries[ll.FormId] = resolved;
            }
        }

        // Build actor → package location mappings
        ProcessActorPackages(records.Npcs, n => (n.FormId, n.Packages), packageById, index);
        ProcessActorPackages(records.Creatures, c => (c.FormId, c.Packages), packageById, index);

        return index;
    }

    private static void ResolveLeveledList(
        uint formId,
        Dictionary<uint, LeveledListRecord> leveledListById,
        List<uint> resolved,
        HashSet<uint> visited,
        int maxDepth)
    {
        if (maxDepth <= 0 || !visited.Add(formId))
        {
            return;
        }

        if (!leveledListById.TryGetValue(formId, out var ll))
        {
            return;
        }

        foreach (var entry in ll.Entries)
        {
            if (leveledListById.ContainsKey(entry.FormId))
            {
                // Nested leveled list — recurse
                ResolveLeveledList(entry.FormId, leveledListById, resolved, visited, maxDepth - 1);
            }
            else if (entry.FormId != 0)
            {
                // Leaf actor
                resolved.Add(entry.FormId);
            }
        }
    }

    private static void ProcessActorPackages<T>(
        List<T> actors,
        Func<T, (uint FormId, List<uint> Packages)> selector,
        Dictionary<uint, PackageRecord> packageById,
        SpawnResolutionIndex index)
    {
        foreach (var actor in actors)
        {
            var (actorFormId, packages) = selector(actor);
            if (actorFormId == 0 || packages.Count == 0)
            {
                continue;
            }

            foreach (var pkgFormId in packages)
            {
                if (!packageById.TryGetValue(pkgFormId, out var pkg) || pkg.Location == null)
                {
                    continue;
                }

                switch (pkg.Location.Type)
                {
                    case 0: // NearRef
                        if (pkg.Location.Union != 0)
                        {
                            if (!index.ActorToPackageRefs.TryGetValue(actorFormId, out var refs))
                            {
                                refs = [];
                                index.ActorToPackageRefs[actorFormId] = refs;
                            }

                            refs.Add(new PackageRefLocation(pkg.Location.Union, pkg.Location.Radius));
                        }

                        break;

                    case 1: // InCell
                        if (pkg.Location.Union != 0)
                        {
                            if (!index.ActorToPackageCells.TryGetValue(actorFormId, out var cells))
                            {
                                cells = [];
                                index.ActorToPackageCells[actorFormId] = cells;
                            }

                            cells.Add(pkg.Location.Union);
                        }

                        break;
                }
            }
        }
    }
}
