using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

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
    /// </summary>
    /// <returns>Number of placed references that had their positions resolved.</returns>
    public static int ResolveSpawnPositions(
        List<CellRecord> cells,
        List<PackageRecord> packages,
        List<NpcRecord> npcs,
        List<CreatureRecord> creatures)
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

        if (baseToPackages.Count == 0)
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
            var modified = false;

            for (var objIdx = 0; objIdx < cell.PlacedObjects.Count; objIdx++)
            {
                var obj = cell.PlacedObjects[objIdx];

                if (obj.RecordType is not ("ACHR" or "ACRE") || !IsAtOrigin(obj))
                {
                    continue;
                }

                if (!baseToPackages.TryGetValue(obj.BaseFormId, out var pkgIds))
                {
                    continue;
                }

                var resolved = TryResolvePosition(
                    pkgIds, packageById, refrPositions, cellCenters, baseObjectPositions);

                if (resolved == null)
                {
                    continue;
                }

                var (rx, ry, rz) = resolved.Value;
                cell.PlacedObjects[objIdx] = obj with { X = rx, Y = ry, Z = rz };
                modified = true;
                resolvedCount++;
            }

            if (modified)
            {
                // CellRecord is immutable record type — but PlacedObjects is a mutable List,
                // so in-place replacement of list elements is sufficient; no need to replace the cell.
            }
        }

        return resolvedCount;
    }

    private static (float X, float Y, float Z)? TryResolvePosition(
        List<uint> packageIds,
        Dictionary<uint, PackageRecord> packageById,
        Dictionary<uint, (float X, float Y, float Z)> refrPositions,
        Dictionary<uint, (float X, float Y, float Z)> cellCenters,
        Dictionary<uint, (float X, float Y, float Z)> baseObjectPositions)
    {
        foreach (var pkgId in packageIds)
        {
            if (!packageById.TryGetValue(pkgId, out var pkg) || pkg.Location == null)
            {
                continue;
            }

            var loc = pkg.Location;

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

                    // Types 2 (Near Current), 3 (Near Editor), 5 (Object Type), 12 (Near Linked Ref)
                    // cannot be resolved from static ESM data
            }
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
