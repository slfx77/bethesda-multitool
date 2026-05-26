using FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CrossDumpPlacementIndexBuilder
{
    internal static Dictionary<uint, PlacedReferenceLocation> BuildPlacedReferenceLocations(
        IEnumerable<CellRecord> cells,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        var map = new Dictionary<uint, PlacedReferenceLocation>();
        foreach (var cell in cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.FormId == 0)
                {
                    continue;
                }

                map[obj.FormId] = new PlacedReferenceLocation(obj, cellInfo.FormId);
            }
        }

        return map;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>
        BuildNpcPlacementIndexes(IEnumerable<(string FilePath, RecordCollection Records)> sources)
    {
        var sourceList = sources.ToList();
        var virtualCellCanonicalFormIds = VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(sourceList.Select(source => source.Records));
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, records) in sourceList)
        {
            result[filePath] = BuildNpcPlacementIndex(records, virtualCellCanonicalFormIds);
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>>
        BuildKeyLockedDoorIndexes(
            IEnumerable<(string FilePath, RecordCollection Records)> sources,
            IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate>? virtualCellCanonicalFormIds = null)
    {
        var sourceList = sources.ToList();
        virtualCellCanonicalFormIds ??= VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(sourceList.Select(source => source.Records));
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, records) in sourceList)
        {
            result[filePath] = BuildKeyLockedDoorIndex(records, virtualCellCanonicalFormIds);
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>>
        BuildContainerPlacementIndexes(
            IEnumerable<(string FilePath, RecordCollection Records)> sources,
            IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate>? virtualCellCanonicalFormIds = null)
    {
        var sourceList = sources.ToList();
        virtualCellCanonicalFormIds ??= VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(sourceList.Select(source => source.Records));
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, records) in sourceList)
        {
            result[filePath] = BuildContainerPlacementIndex(records, virtualCellCanonicalFormIds);
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>>
        BuildNpcScriptReferenceIndexes(IEnumerable<(string FilePath, RecordCollection Records)> sources)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (filePath, records) in sources)
        {
            result[filePath] = BuildNpcScriptReferenceIndex(records);
        }

        return result;
    }

    internal static Dictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>> BuildNpcScriptReferenceIndex(
        RecordCollection records)
    {
        var npcFormIds = records.Npcs.Select(npc => npc.FormId).ToHashSet();
        if (npcFormIds.Count == 0 || records.Scripts.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>();
        }

        var map = new Dictionary<uint, List<NpcScriptReferenceInfo>>();
        foreach (var script in records.Scripts)
        {
            if (script.ReferencedObjects.Count == 0)
            {
                continue;
            }

            var reference = new NpcScriptReferenceInfo(
                script.FormId,
                script.EditorId,
                script.ScriptType,
                script.OwnerQuestFormId);
            foreach (var referencedFormId in script.ReferencedObjects.Distinct())
            {
                if (!npcFormIds.Contains(referencedFormId))
                {
                    continue;
                }

                if (!map.TryGetValue(referencedFormId, out var scripts))
                {
                    scripts = [];
                    map[referencedFormId] = scripts;
                }

                scripts.Add(reference);
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<NpcScriptReferenceInfo>)entry.Value
                .OrderBy(reference => reference.ScriptEditorId ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.ScriptFormId)
                .ToList());
    }

    internal static Dictionary<uint, IReadOnlyList<NpcPlacementInfo>> BuildNpcPlacementIndex(
        RecordCollection records,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        var npcFormIds = records.Npcs.Select(npc => npc.FormId).ToHashSet();
        if (npcFormIds.Count == 0 || records.Cells.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<NpcPlacementInfo>>();
        }

        var map = new Dictionary<uint, List<NpcPlacementInfo>>();
        foreach (var cell in records.Cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (!string.Equals(obj.RecordType, "ACHR", StringComparison.OrdinalIgnoreCase) ||
                    obj.BaseFormId == 0 ||
                    !npcFormIds.Contains(obj.BaseFormId))
                {
                    continue;
                }

                if (!map.TryGetValue(obj.BaseFormId, out var placements))
                {
                    placements = [];
                    map[obj.BaseFormId] = placements;
                }

                placements.Add(new NpcPlacementInfo(
                    obj,
                    cellInfo.FormId,
                    cellInfo.EditorId,
                    cellInfo.DisplayName,
                    cellInfo.WorldspaceFormId,
                    cellInfo.GridX,
                    cellInfo.GridY));
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<NpcPlacementInfo>)entry.Value
                .OrderBy(placement => ResolveCellSortName(placement), StringComparer.OrdinalIgnoreCase)
                .ThenBy(placement => placement.GridY ?? int.MaxValue)
                .ThenBy(placement => placement.GridX ?? int.MaxValue)
                .ThenBy(placement => placement.Ref.FormId)
                .ToList());
    }

    internal static Dictionary<uint, IReadOnlyList<KeyLockedDoorInfo>> BuildKeyLockedDoorIndex(
        RecordCollection records,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        if (records.Keys.Count == 0 || records.Cells.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>();
        }

        var keyFormIds = records.Keys.Select(key => key.FormId).ToHashSet();
        var map = new Dictionary<uint, List<KeyLockedDoorInfo>>();
        foreach (var cell in records.Cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.LockKeyFormId is not > 0 ||
                    !keyFormIds.Contains(obj.LockKeyFormId.Value))
                {
                    continue;
                }

                if (!map.TryGetValue(obj.LockKeyFormId.Value, out var doors))
                {
                    doors = [];
                    map[obj.LockKeyFormId.Value] = doors;
                }

                doors.Add(new KeyLockedDoorInfo(
                    obj,
                    cellInfo.FormId,
                    cellInfo.EditorId,
                    cellInfo.DisplayName,
                    cellInfo.WorldspaceFormId,
                    cellInfo.GridX,
                    cellInfo.GridY));
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<KeyLockedDoorInfo>)entry.Value
                .OrderBy(info => ResolveCellSortName(info), StringComparer.OrdinalIgnoreCase)
                .ThenBy(info => info.GridY ?? int.MaxValue)
                .ThenBy(info => info.GridX ?? int.MaxValue)
                .ThenBy(info => info.Ref.FormId)
                .ToList());
    }

    internal static Dictionary<uint, IReadOnlyList<ContainerPlacementInfo>> BuildContainerPlacementIndex(
        RecordCollection records,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        if (records.Containers.Count == 0 || records.Cells.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<ContainerPlacementInfo>>();
        }

        var containerFormIds = records.Containers.Select(container => container.FormId).ToHashSet();
        var map = new Dictionary<uint, List<ContainerPlacementInfo>>();
        foreach (var cell in records.Cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (!string.Equals(obj.RecordType, "REFR", StringComparison.OrdinalIgnoreCase) ||
                    obj.BaseFormId == 0 ||
                    !containerFormIds.Contains(obj.BaseFormId))
                {
                    continue;
                }

                if (!map.TryGetValue(obj.BaseFormId, out var placements))
                {
                    placements = [];
                    map[obj.BaseFormId] = placements;
                }

                placements.Add(new ContainerPlacementInfo(
                    obj,
                    cellInfo.FormId,
                    cellInfo.EditorId,
                    cellInfo.DisplayName,
                    cellInfo.WorldspaceFormId,
                    cellInfo.GridX,
                    cellInfo.GridY));
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<ContainerPlacementInfo>)entry.Value
                .OrderBy(placement => ResolveCellSortName(placement), StringComparer.OrdinalIgnoreCase)
                .ThenBy(placement => placement.GridY ?? int.MaxValue)
                .ThenBy(placement => placement.GridX ?? int.MaxValue)
                .ThenBy(placement => placement.Ref.FormId)
                .ToList());
    }

    internal static string ResolveCellSortName(NpcPlacementInfo placement)
    {
        if (!string.IsNullOrWhiteSpace(placement.CellEditorId))
        {
            return placement.CellEditorId;
        }

        if (!string.IsNullOrWhiteSpace(placement.CellName))
        {
            return placement.CellName;
        }

        return "";
    }

    internal static string ResolveCellSortName(KeyLockedDoorInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.CellEditorId))
        {
            return info.CellEditorId;
        }

        if (!string.IsNullOrWhiteSpace(info.CellName))
        {
            return info.CellName;
        }

        return "";
    }

    internal static string ResolveCellSortName(ContainerPlacementInfo placement)
    {
        if (!string.IsNullOrWhiteSpace(placement.CellEditorId))
        {
            return placement.CellEditorId;
        }

        if (!string.IsNullOrWhiteSpace(placement.CellName))
        {
            return placement.CellName;
        }

        return "";
    }

    internal static PlacementCellInfo ResolvePlacementCellInfo(
        CellRecord cell,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        if (VirtualCellCanonicalizer.TryGetVirtualCellCanonicalFormId(cell, virtualCellCanonicalFormIds, out var canonicalCell))
        {
            return new PlacementCellInfo(
                canonicalCell.FormId,
                canonicalCell.IsSyntheticVirtual ? null : canonicalCell.EditorId ?? cell.EditorId,
                canonicalCell.IsSyntheticVirtual ? null : canonicalCell.DisplayName ?? cell.FullName,
                cell.WorldspaceFormId,
                cell.GridX,
                cell.GridY);
        }

        return new PlacementCellInfo(
            cell.FormId,
            cell.EditorId,
            cell.FullName,
            cell.WorldspaceFormId,
            cell.GridX,
            cell.GridY);
    }

    internal readonly record struct PlacementCellInfo(
        uint FormId,
        string? EditorId,
        string? DisplayName,
        uint? WorldspaceFormId,
        int? GridX,
        int? GridY);

    // ---------- Skeleton overloads (Phase 2: streaming-pipeline support) ----------
    //
    // Mirror of the RecordCollection-based builders that consume the lightweight
    // CellSkeleton / NpcSkeleton / KeySkeleton / ContainerSkeleton / ScriptSkeleton
    // projections built by CrossDumpSourceProjector. The originating PlacedReference
    // is still passed to NpcPlacementInfo / KeyLockedDoorInfo / ContainerPlacementInfo
    // via PlacedObjectSkeleton.Ref so the downstream Info shape doesn't change.
    //
    // Cross-source variants (BuildXxxIndexes) take CrossDumpSourceProjection bundles
    // so Phase 5's wire-flip can pass projection lists directly.

    internal static Dictionary<uint, PlacedReferenceLocation> BuildPlacedReferenceLocations(
        IEnumerable<CellSkeleton> cells,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        var map = new Dictionary<uint, PlacedReferenceLocation>();
        foreach (var cell in cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.FormId == 0)
                {
                    continue;
                }

                map[obj.FormId] = new PlacedReferenceLocation(obj.Ref, cellInfo.FormId);
            }
        }

        return map;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>
        BuildNpcPlacementIndexes(
            IEnumerable<CrossDumpSourceProjection> projections,
            IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate>? virtualCellCanonicalFormIds = null)
    {
        var projectionList = projections.ToList();
        virtualCellCanonicalFormIds ??= VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(
            projectionList.Select(p => p.CellSkeletons));
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcPlacementInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var projection in projectionList)
        {
            result[projection.FilePath] = BuildNpcPlacementIndex(
                projection.NpcSkeletons, projection.CellSkeletons, virtualCellCanonicalFormIds);
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>>
        BuildKeyLockedDoorIndexes(
            IEnumerable<CrossDumpSourceProjection> projections,
            IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate>? virtualCellCanonicalFormIds = null)
    {
        var projectionList = projections.ToList();
        virtualCellCanonicalFormIds ??= VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(
            projectionList.Select(p => p.CellSkeletons));
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var projection in projectionList)
        {
            result[projection.FilePath] = BuildKeyLockedDoorIndex(
                projection.KeySkeletons, projection.CellSkeletons, virtualCellCanonicalFormIds);
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>>
        BuildContainerPlacementIndexes(
            IEnumerable<CrossDumpSourceProjection> projections,
            IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate>? virtualCellCanonicalFormIds = null)
    {
        var projectionList = projections.ToList();
        virtualCellCanonicalFormIds ??= VirtualCellCanonicalizer.BuildVirtualCellCanonicalFormIds(
            projectionList.Select(p => p.CellSkeletons));
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<ContainerPlacementInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var projection in projectionList)
        {
            result[projection.FilePath] = BuildContainerPlacementIndex(
                projection.ContainerSkeletons, projection.CellSkeletons, virtualCellCanonicalFormIds);
        }

        return result;
    }

    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>>
        BuildNpcScriptReferenceIndexes(IEnumerable<CrossDumpSourceProjection> projections)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var projection in projections)
        {
            result[projection.FilePath] = BuildNpcScriptReferenceIndex(
                projection.NpcSkeletons, projection.ScriptSkeletons);
        }

        return result;
    }

    internal static Dictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>> BuildNpcScriptReferenceIndex(
        IReadOnlyList<NpcSkeleton> npcs,
        IReadOnlyList<ScriptSkeleton> scripts)
    {
        var npcFormIds = npcs.Select(npc => npc.FormId).ToHashSet();
        if (npcFormIds.Count == 0 || scripts.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<NpcScriptReferenceInfo>>();
        }

        var map = new Dictionary<uint, List<NpcScriptReferenceInfo>>();
        foreach (var script in scripts)
        {
            if (script.ReferencedObjects.Count == 0)
            {
                continue;
            }

            var reference = new NpcScriptReferenceInfo(
                script.FormId,
                script.EditorId,
                script.ScriptType,
                script.OwnerQuestFormId);
            foreach (var referencedFormId in script.ReferencedObjects)
            {
                // Skeleton ReferencedObjects are already deduped at projection time.
                if (!npcFormIds.Contains(referencedFormId))
                {
                    continue;
                }

                if (!map.TryGetValue(referencedFormId, out var scriptRefs))
                {
                    scriptRefs = [];
                    map[referencedFormId] = scriptRefs;
                }

                scriptRefs.Add(reference);
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<NpcScriptReferenceInfo>)entry.Value
                .OrderBy(reference => reference.ScriptEditorId ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(reference => reference.ScriptFormId)
                .ToList());
    }

    internal static Dictionary<uint, IReadOnlyList<NpcPlacementInfo>> BuildNpcPlacementIndex(
        IReadOnlyList<NpcSkeleton> npcs,
        IReadOnlyList<CellSkeleton> cells,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        var npcFormIds = npcs.Select(npc => npc.FormId).ToHashSet();
        if (npcFormIds.Count == 0 || cells.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<NpcPlacementInfo>>();
        }

        var map = new Dictionary<uint, List<NpcPlacementInfo>>();
        foreach (var cell in cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (!string.Equals(obj.RecordType, "ACHR", StringComparison.OrdinalIgnoreCase) ||
                    obj.BaseFormId == 0 ||
                    !npcFormIds.Contains(obj.BaseFormId))
                {
                    continue;
                }

                if (!map.TryGetValue(obj.BaseFormId, out var placements))
                {
                    placements = [];
                    map[obj.BaseFormId] = placements;
                }

                placements.Add(new NpcPlacementInfo(
                    obj.Ref,
                    cellInfo.FormId,
                    cellInfo.EditorId,
                    cellInfo.DisplayName,
                    cellInfo.WorldspaceFormId,
                    cellInfo.GridX,
                    cellInfo.GridY));
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<NpcPlacementInfo>)entry.Value
                .OrderBy(placement => ResolveCellSortName(placement), StringComparer.OrdinalIgnoreCase)
                .ThenBy(placement => placement.GridY ?? int.MaxValue)
                .ThenBy(placement => placement.GridX ?? int.MaxValue)
                .ThenBy(placement => placement.Ref.FormId)
                .ToList());
    }

    internal static Dictionary<uint, IReadOnlyList<KeyLockedDoorInfo>> BuildKeyLockedDoorIndex(
        IReadOnlyList<KeySkeleton> keys,
        IReadOnlyList<CellSkeleton> cells,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        if (keys.Count == 0 || cells.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<KeyLockedDoorInfo>>();
        }

        var keyFormIds = keys.Select(key => key.FormId).ToHashSet();
        var map = new Dictionary<uint, List<KeyLockedDoorInfo>>();
        foreach (var cell in cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.LockKeyFormId is not > 0 ||
                    !keyFormIds.Contains(obj.LockKeyFormId.Value))
                {
                    continue;
                }

                if (!map.TryGetValue(obj.LockKeyFormId.Value, out var doors))
                {
                    doors = [];
                    map[obj.LockKeyFormId.Value] = doors;
                }

                doors.Add(new KeyLockedDoorInfo(
                    obj.Ref,
                    cellInfo.FormId,
                    cellInfo.EditorId,
                    cellInfo.DisplayName,
                    cellInfo.WorldspaceFormId,
                    cellInfo.GridX,
                    cellInfo.GridY));
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<KeyLockedDoorInfo>)entry.Value
                .OrderBy(info => ResolveCellSortName(info), StringComparer.OrdinalIgnoreCase)
                .ThenBy(info => info.GridY ?? int.MaxValue)
                .ThenBy(info => info.GridX ?? int.MaxValue)
                .ThenBy(info => info.Ref.FormId)
                .ToList());
    }

    internal static Dictionary<uint, IReadOnlyList<ContainerPlacementInfo>> BuildContainerPlacementIndex(
        IReadOnlyList<ContainerSkeleton> containers,
        IReadOnlyList<CellSkeleton> cells,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        if (containers.Count == 0 || cells.Count == 0)
        {
            return new Dictionary<uint, IReadOnlyList<ContainerPlacementInfo>>();
        }

        var containerFormIds = containers.Select(container => container.FormId).ToHashSet();
        var map = new Dictionary<uint, List<ContainerPlacementInfo>>();
        foreach (var cell in cells)
        {
            var cellInfo = ResolvePlacementCellInfo(cell, virtualCellCanonicalFormIds);

            foreach (var obj in cell.PlacedObjects)
            {
                if (!string.Equals(obj.RecordType, "REFR", StringComparison.OrdinalIgnoreCase) ||
                    obj.BaseFormId == 0 ||
                    !containerFormIds.Contains(obj.BaseFormId))
                {
                    continue;
                }

                if (!map.TryGetValue(obj.BaseFormId, out var placements))
                {
                    placements = [];
                    map[obj.BaseFormId] = placements;
                }

                placements.Add(new ContainerPlacementInfo(
                    obj.Ref,
                    cellInfo.FormId,
                    cellInfo.EditorId,
                    cellInfo.DisplayName,
                    cellInfo.WorldspaceFormId,
                    cellInfo.GridX,
                    cellInfo.GridY));
            }
        }

        return map.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlyList<ContainerPlacementInfo>)entry.Value
                .OrderBy(placement => ResolveCellSortName(placement), StringComparer.OrdinalIgnoreCase)
                .ThenBy(placement => placement.GridY ?? int.MaxValue)
                .ThenBy(placement => placement.GridX ?? int.MaxValue)
                .ThenBy(placement => placement.Ref.FormId)
                .ToList());
    }

    internal static PlacementCellInfo ResolvePlacementCellInfo(
        CellSkeleton cell,
        IReadOnlyDictionary<CellCoordinateKey, RealCellCandidate> virtualCellCanonicalFormIds)
    {
        if (VirtualCellCanonicalizer.TryGetVirtualCellCanonicalFormId(cell, virtualCellCanonicalFormIds, out var canonicalCell))
        {
            return new PlacementCellInfo(
                canonicalCell.FormId,
                canonicalCell.IsSyntheticVirtual ? null : canonicalCell.EditorId ?? cell.EditorId,
                canonicalCell.IsSyntheticVirtual ? null : canonicalCell.DisplayName ?? cell.FullName,
                cell.WorldspaceFormId,
                cell.GridX,
                cell.GridY);
        }

        return new PlacementCellInfo(
            cell.FormId,
            cell.EditorId,
            cell.FullName,
            cell.WorldspaceFormId,
            cell.GridX,
            cell.GridY);
    }
}
