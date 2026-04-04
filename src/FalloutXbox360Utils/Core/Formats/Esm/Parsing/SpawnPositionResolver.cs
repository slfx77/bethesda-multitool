using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Resolves NPC/creature spawn positions from AI package (PACK) location data.
///     Persistent ACHR/ACRE records often have (0,0,0) positions because their actual
///     spawn locations are determined by their first AI package's PLDT subrecord.
/// </summary>
internal static class SpawnPositionResolver
{
    private const float OriginThreshold = 1.0f;
    private const float CellWorldSize = 4096f;

    /// <summary>
    ///     Resolves spawn positions for ACHR/ACRE records at the origin by looking up
    ///     their base NPC/creature's AI packages and extracting PLDT location data.
    ///     Supports direct NPC_/CREA base actors and LVLN/LVLC leveled list resolution.
    /// </summary>
    /// <returns>Number of placed references that had their positions resolved.</returns>
    public static int ResolveSpawnPositions(
        List<CellRecord> cells,
        List<PackageRecord> packages,
        List<NpcRecord> npcs,
        List<CreatureRecord> creatures,
        List<LeveledListRecord> leveledLists)
    {
        if (packages.Count == 0)
        {
            return 0;
        }

        // Build package FormID → PackageRecord lookup
        var packageById = new Dictionary<uint, PackageRecord>(packages.Count);
        foreach (var pkg in packages)
        {
            packageById.TryAdd(pkg.FormId, pkg);
        }

        // Build base actor FormID → package list lookup
        var baseToPackages = new Dictionary<uint, List<uint>>();
        foreach (var npc in npcs)
        {
            if (npc.Packages.Count > 0)
            {
                baseToPackages.TryAdd(npc.FormId, npc.Packages);
            }
        }

        foreach (var creature in creatures)
        {
            if (creature.Packages.Count > 0)
            {
                baseToPackages.TryAdd(creature.FormId, creature.Packages);
            }
        }

        // Build leveled list → leaf actor resolution for LVLN/LVLC
        var leveledListById = new Dictionary<uint, LeveledListRecord>();
        foreach (var ll in leveledLists)
        {
            if (ll.ListType is "LVLN" or "LVLC")
            {
                leveledListById.TryAdd(ll.FormId, ll);
            }
        }

        if (baseToPackages.Count == 0 && leveledListById.Count == 0)
        {
            return 0;
        }

        // Build placed reference FormID → position index (for PLDT Type 0: Near Reference)
        var refrPositions = new Dictionary<uint, (float X, float Y, float Z)>();

        // Build base object FormID → first position index (for PLDT Type 4: Object ID)
        var baseObjectPositions = new Dictionary<uint, (float X, float Y, float Z)>();

        // Build cell FormID → grid center position (for PLDT Type 1: In Cell)
        var cellCenters = new Dictionary<uint, (float X, float Y, float Z)>();

        foreach (var cell in cells)
        {
            if (cell.GridX.HasValue && cell.GridY.HasValue)
            {
                cellCenters.TryAdd(cell.FormId,
                    (cell.GridX.Value * CellWorldSize + CellWorldSize * 0.5f,
                        cell.GridY.Value * CellWorldSize + CellWorldSize * 0.5f,
                        0f));
            }

            foreach (var obj in cell.PlacedObjects)
            {
                if (!IsAtOrigin(obj))
                {
                    refrPositions.TryAdd(obj.FormId, (obj.X, obj.Y, obj.Z));
                    baseObjectPositions.TryAdd(obj.BaseFormId, (obj.X, obj.Y, obj.Z));
                }
            }
        }

        // Resolve positions for ACHR/ACRE at origin
        var resolvedCount = 0;

        for (var cellIdx = 0; cellIdx < cells.Count; cellIdx++)
        {
            var cell = cells[cellIdx];

            for (var objIdx = 0; objIdx < cell.PlacedObjects.Count; objIdx++)
            {
                var obj = cell.PlacedObjects[objIdx];

                if (obj.RecordType is not ("ACHR" or "ACRE") || !IsAtOrigin(obj))
                {
                    continue;
                }

                // Try direct actor lookup first
                var pkgIds = FindPackageIds(obj.BaseFormId, baseToPackages, leveledListById);
                if (pkgIds == null)
                {
                    continue;
                }

                var resolved = TryResolvePosition(
                    pkgIds, packageById, refrPositions, cellCenters, baseObjectPositions, obj);

                if (resolved == null)
                {
                    continue;
                }

                var (rx, ry, rz) = resolved.Value;
                cell.PlacedObjects[objIdx] = obj with { X = rx, Y = ry, Z = rz };
                resolvedCount++;
            }
        }

        return resolvedCount;
    }

    /// <summary>
    ///     Finds package IDs for a base actor, handling both direct NPC_/CREA and leveled lists.
    /// </summary>
    private static List<uint>? FindPackageIds(
        uint baseFormId,
        Dictionary<uint, List<uint>> baseToPackages,
        Dictionary<uint, LeveledListRecord> leveledListById)
    {
        // Direct lookup — base is a NPC_ or CREA with PKID subrecords
        if (baseToPackages.TryGetValue(baseFormId, out var pkgIds))
        {
            return pkgIds;
        }

        // Leveled list fallback — resolve LVLN/LVLC to leaf actors and try their packages
        if (!leveledListById.ContainsKey(baseFormId))
        {
            return null;
        }

        var leafActors = new List<uint>();
        var visited = new HashSet<uint>();
        ResolveLeveledListLeaves(baseFormId, leveledListById, leafActors, visited, 8);

        foreach (var leafFormId in leafActors)
        {
            if (baseToPackages.TryGetValue(leafFormId, out var leafPkgIds))
            {
                return leafPkgIds;
            }
        }

        return null;
    }

    /// <summary>
    ///     Recursively resolves a leveled list to its leaf actor FormIDs.
    /// </summary>
    private static void ResolveLeveledListLeaves(
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
                ResolveLeveledListLeaves(entry.FormId, leveledListById, resolved, visited, maxDepth - 1);
            }
            else if (entry.FormId != 0)
            {
                resolved.Add(entry.FormId);
            }
        }
    }

    private static (float X, float Y, float Z)? TryResolvePosition(
        List<uint> packageIds,
        Dictionary<uint, PackageRecord> packageById,
        Dictionary<uint, (float X, float Y, float Z)> refrPositions,
        Dictionary<uint, (float X, float Y, float Z)> cellCenters,
        Dictionary<uint, (float X, float Y, float Z)> baseObjectPositions,
        PlacedReference actor)
    {
        foreach (var pkgId in packageIds)
        {
            if (!packageById.TryGetValue(pkgId, out var pkg))
            {
                continue;
            }

            // Try primary location (PLDT), then secondary (PLD2)
            var result = TryResolveFromLocation(
                pkg.Location, refrPositions, cellCenters, baseObjectPositions, actor);
            if (result != null)
            {
                return result;
            }

            result = TryResolveFromLocation(
                pkg.Location2, refrPositions, cellCenters, baseObjectPositions, actor);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static (float X, float Y, float Z)? TryResolveFromLocation(
        PackageLocation? loc,
        Dictionary<uint, (float X, float Y, float Z)> refrPositions,
        Dictionary<uint, (float X, float Y, float Z)> cellCenters,
        Dictionary<uint, (float X, float Y, float Z)> baseObjectPositions,
        PlacedReference actor)
    {
        if (loc == null)
        {
            return null;
        }

        switch (loc.Type)
        {
            case 0: // Near Reference — Union is a placed object FormID
                if (refrPositions.TryGetValue(loc.Union, out var refPos))
                {
                    return refPos;
                }

                break;

            case 1: // In Cell — Union is a cell FormID
                if (cellCenters.TryGetValue(loc.Union, out var cellPos))
                {
                    return cellPos;
                }

                break;

            case 4: // Object ID — Union is a base object FormID
                if (baseObjectPositions.TryGetValue(loc.Union, out var objPos))
                {
                    return objPos;
                }

                break;

            case 12: // Near Linked Ref — resolve via actor's XLKR subrecord
                if (actor.LinkedRefFormId.HasValue &&
                    refrPositions.TryGetValue(actor.LinkedRefFormId.Value, out var linkedPos))
                {
                    return linkedPos;
                }

                break;

            // Types 2 (Near Current), 3 (Near Editor), 5 (Object Type)
            // cannot be resolved from static ESM data
        }

        return null;
    }

    private static bool IsAtOrigin(PlacedReference obj)
    {
        return Math.Abs(obj.X) < OriginThreshold &&
               Math.Abs(obj.Y) < OriginThreshold &&
               Math.Abs(obj.Z) < OriginThreshold;
    }
}
