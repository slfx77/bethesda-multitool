using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;

namespace BethesdaMultitool.Core.Formats.Esm;

internal sealed record CellWorldspaceAuthorityApplyResult(
    int Applied,
    int Added,
    int Overrode,
    int SynthesizedWorldspaces,
    int TerrainCellsAttached = 0,
    int ReferencesReattached = 0,
    int ReferenceCellsCreated = 0,
    int ReferenceWindowsApplied = 0,
    int ReferenceWindowAmbiguousMatches = 0);

internal static class CellWorldspaceAuthorityApplier
{
    private const string SourceAuthorityOffsetCluster = "AuthorityOffsetCluster";
    private const string SourceAuthorityRefWindow = "AuthorityRefWindow";
    private const int AuthorityOffsetClusterGapBytes = 0x1000;
    private const int AuthorityOffsetClusterWindowBytes = 0x20000;
    private const int AuthorityOffsetClusterNearestBandBytes = 0x4000;
    private const int AuthorityOffsetClusterGridExpansion = 2;
    private const int AuthorityOffsetClusterMinPlacements = 2;
    private const string SourceOffsetCluster = "OffsetCluster";
    private const string SourceVirtual = "Virtual";

    private readonly record struct ResolvedReferenceWindow(
        uint CellFormId,
        long MinOffset,
        long MaxOffset,
        string? Label);

    /// <summary>
    ///     Applies authoritative CELL metadata to the semantic record model and rebuilds
    ///     worldspace child lists so reports and map views see the same ownership that
    ///     DMP to ESP conversion uses.
    /// </summary>
    public static CellWorldspaceAuthorityApplyResult Apply(
        RecordCollection records,
        IReadOnlyDictionary<uint, uint>? authority,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null,
        EsmRecordScanResult? scanResult = null,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata = null,
        IReadOnlyDictionary<uint, uint>? refToCell = null,
        IReadOnlyList<CellReferenceParentWindow>? refWindows = null)
    {
        cellMetadata ??= authority?.ToDictionary(
            kv => kv.Key,
            kv => new CellAuthorityMetadata { WorldspaceFormId = kv.Value });
        var hasCellMetadata = cellMetadata is { Count: > 0 };
        var hasReferenceParents = refToCell is { Count: > 0 };
        var hasReferenceWindows = refWindows is { Count: > 0 };
        if ((!hasCellMetadata && !hasReferenceParents && !hasReferenceWindows) || records.Cells.Count == 0)
        {
            return new CellWorldspaceAuthorityApplyResult(0, 0, 0, 0);
        }

        var applied = 0;
        var overrode = 0;
        var added = 0;
        var matched = 0;

        for (var i = 0; hasCellMetadata && i < records.Cells.Count; i++)
        {
            var cell = records.Cells[i];
            if (!cellMetadata!.TryGetValue(cell.FormId, out var metadata))
            {
                continue;
            }

            matched++;
            var wsFid = metadata.IsInterior == true ? null : metadata.WorldspaceFormId;
            if (scanResult is not null && wsFid is > 0)
            {
                scanResult.CellToWorldspaceMap[cell.FormId] = wsFid.Value;
            }

            var flags = cell.Flags;
            if (metadata.IsInterior == true)
            {
                flags |= 0x01;
            }
            else if (metadata.IsInterior == false || wsFid is > 0)
            {
                flags = (byte)(flags & ~0x01);
            }

            if ((metadata.IsInterior == true && cell.WorldspaceFormId is > 0) ||
                (wsFid is > 0 && cell.WorldspaceFormId is { } prior && prior != 0u && prior != wsFid.Value))
            {
                overrode++;
            }
            else if (wsFid is > 0 && (cell.WorldspaceFormId is null || cell.WorldspaceFormId == 0u))
            {
                added++;
            }

            var gridX = metadata.IsInterior == true ? null : metadata.GridX ?? cell.GridX;
            var gridY = metadata.IsInterior == true ? null : metadata.GridY ?? cell.GridY;
            var updated = cell with
            {
                Flags = flags,
                GridX = gridX,
                GridY = gridY,
                WorldspaceFormId = metadata.IsInterior == true ? null : wsFid ?? cell.WorldspaceFormId,
                WorldspaceAssignmentSource = wsFid is > 0 ? "Authority" : cell.WorldspaceAssignmentSource,
                EditorId = string.IsNullOrWhiteSpace(cell.EditorId) ? metadata.EditorId : cell.EditorId,
                FullName = string.IsNullOrWhiteSpace(cell.FullName) ? metadata.FullName : cell.FullName
            };

            if (updated != cell)
            {
                records.Cells[i] = updated;
                applied++;
            }
        }

        var terrainBefore = CountCellsWithTerrain(records);
        var referenceMove = ReattachUnresolvedReferences(
            records,
            refToCell,
            cellMetadata,
            authority,
            scanResult);
        var windowMove = ReattachWindowedUnresolvedReferences(
            records,
            refWindows,
            cellMetadata,
            authority,
            scanResult);
        var offsetMove = ReattachOffsetClusteredUnresolvedReferences(records, scanResult);

        if (scanResult is not null &&
            (matched > 0 || referenceMove.Moved > 0 || windowMove.Moved > 0 || offsetMove.Moved > 0))
        {
            EsmLandEnricher.EnrichLandRecordsWithCellWorldspaces(scanResult, records.Cells);
            CellRecordHandler.AttachTerrainDataFromLandRecords(records.Cells, scanResult);
        }

        var synthesized = RebuildWorldspaceCellLists(records, worldspaceNames);
        var terrainAttached = Math.Max(0, CountCellsWithTerrain(records) - terrainBefore);
        return new CellWorldspaceAuthorityApplyResult(
            applied,
            added,
            overrode,
            synthesized,
            terrainAttached,
            referenceMove.Moved + windowMove.Moved + offsetMove.Moved,
            referenceMove.CreatedCells + windowMove.CreatedCells + offsetMove.CreatedCells,
            windowMove.AppliedWindows,
            windowMove.AmbiguousMatches);
    }

    private static (int Moved, int CreatedCells) ReattachUnresolvedReferences(
        RecordCollection records,
        IReadOnlyDictionary<uint, uint>? refToCell,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
        IReadOnlyDictionary<uint, uint>? authority,
        EsmRecordScanResult? scanResult)
    {
        if (refToCell is not { Count: > 0 })
        {
            return (0, 0);
        }

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var moved = 0;
        var createdCells = 0;
        var originalCellCount = records.Cells.Count;

        for (var i = 0; i < originalCellCount; i++)
        {
            var source = records.Cells[i];
            if (!CanReattachAuthorityMappedReferencesFrom(source) || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var kept = new List<PlacedReference>(source.PlacedObjects.Count);
            var sourceChanged = false;

            foreach (var placed in source.PlacedObjects)
            {
                if (!refToCell.TryGetValue(placed.FormId, out var targetCellFormId) ||
                    targetCellFormId == 0 ||
                    targetCellFormId == source.FormId ||
                    !TryGetOrCreateAuthorityCell(
                        records,
                        cellIndexByFormId,
                        targetCellFormId,
                        cellMetadata,
                        authority,
                        scanResult,
                        out var targetIndex,
                        out var created))
                {
                    kept.Add(placed);
                    continue;
                }

                if (created)
                {
                    createdCells++;
                }

                var target = records.Cells[targetIndex];
                if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                {
                    target.PlacedObjects.Add(placed with { AssignmentSource = "AuthorityRefParent" });
                }

                MoveScanResultRefLink(scanResult, source.FormId, targetCellFormId, placed.FormId);
                moved++;
                sourceChanged = true;
            }

            if (sourceChanged)
            {
                records.Cells[i] = source with { PlacedObjects = kept };
            }
        }

        if (moved == 0)
        {
            return (0, 0);
        }

        records.Cells.RemoveAll(c => c.PlacedObjects.Count == 0 && CanRemoveEmptyAuthorityReferenceSource(c));
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells);
    }

    private static bool CanReattachAuthorityMappedReferencesFrom(CellRecord cell)
    {
        if (cell.IsUnresolvedBucket)
        {
            return true;
        }

        if (!cell.IsVirtual || cell.IsPersistentCell)
        {
            return false;
        }

        return cell.PlacedObjects.Any(placed =>
            placed.AssignmentSource is SourceOffsetCluster or SourceVirtual or SourceAuthorityOffsetCluster or
                SourceAuthorityRefWindow);
    }

    private static bool CanRemoveEmptyAuthorityReferenceSource(CellRecord cell)
    {
        if (cell.IsUnresolvedBucket)
        {
            return true;
        }

        return cell.IsVirtual &&
               !cell.IsPersistentCell &&
               cell.WorldspaceAssignmentSource is SourceOffsetCluster or SourceVirtual or SourceAuthorityOffsetCluster or
                   SourceAuthorityRefWindow;
    }

    private static (int Moved, int CreatedCells, int AppliedWindows, int AmbiguousMatches)
        ReattachWindowedUnresolvedReferences(
            RecordCollection records,
            IReadOnlyList<CellReferenceParentWindow>? refWindows,
            IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
            IReadOnlyDictionary<uint, uint>? authority,
            EsmRecordScanResult? scanResult)
    {
        if (refWindows is not { Count: > 0 })
        {
            return (0, 0, 0, 0);
        }

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var createdCells = 0;
        var targetIndexByCellFormId = new Dictionary<uint, int>();
        var resolvedWindows = new List<ResolvedReferenceWindow>();
        foreach (var window in refWindows)
        {
            if (window.CellFormId == 0 ||
                !window.TryResolveRange(
                    anchorReferenceFormId => TryFindAnchorOffset(records, window.CellFormId, anchorReferenceFormId),
                    out var minOffset,
                    out var maxOffset))
            {
                continue;
            }

            resolvedWindows.Add(new ResolvedReferenceWindow(
                window.CellFormId,
                minOffset,
                maxOffset,
                window.Label));
        }

        if (resolvedWindows.Count == 0)
        {
            return (0, createdCells, 0, 0);
        }

        var moved = 0;
        var ambiguousMatches = 0;
        for (var i = 0; i < records.Cells.Count; i++)
        {
            var source = records.Cells[i];
            if (!CanReattachAuthorityMappedReferencesFrom(source) || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var kept = new List<PlacedReference>(source.PlacedObjects.Count);
            var sourceChanged = false;
            foreach (var placed in source.PlacedObjects)
            {
                if (placed.Offset <= 0)
                {
                    kept.Add(placed);
                    continue;
                }

                if (!TrySelectWindowForOffset(resolvedWindows, placed.Offset, out var window, out var ambiguous))
                {
                    if (ambiguous)
                    {
                        ambiguousMatches++;
                    }

                    kept.Add(placed);
                    continue;
                }

                if (window.CellFormId == source.FormId)
                {
                    kept.Add(placed);
                    continue;
                }

                if (!targetIndexByCellFormId.TryGetValue(window.CellFormId, out var targetIndex))
                {
                    if (!TryGetOrCreateAuthorityCell(
                            records,
                            cellIndexByFormId,
                            window.CellFormId,
                            cellMetadata,
                            authority,
                            scanResult,
                            out targetIndex,
                            out var created))
                    {
                        kept.Add(placed);
                        continue;
                    }

                    if (created)
                    {
                        createdCells++;
                    }

                    targetIndexByCellFormId[window.CellFormId] = targetIndex;
                }

                var target = records.Cells[targetIndex];
                if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                {
                    target.PlacedObjects.Add(placed with { AssignmentSource = SourceAuthorityRefWindow });
                }

                MoveScanResultRefLink(scanResult, source.FormId, window.CellFormId, placed.FormId);
                moved++;
                sourceChanged = true;
            }

            if (sourceChanged)
            {
                records.Cells[i] = source with { PlacedObjects = kept };
            }
        }

        if (moved == 0)
        {
            return (0, createdCells, resolvedWindows.Count, ambiguousMatches);
        }

        records.Cells.RemoveAll(c => c.PlacedObjects.Count == 0 && CanRemoveEmptyAuthorityReferenceSource(c));
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells, resolvedWindows.Count, ambiguousMatches);
    }

    private static long? TryFindAnchorOffset(
        RecordCollection records,
        uint targetCellFormId,
        uint anchorReferenceFormId)
    {
        var targetOffsets = records.Cells
            .Where(cell => cell.FormId == targetCellFormId)
            .SelectMany(cell => cell.PlacedObjects)
            .Where(placed => placed.FormId == anchorReferenceFormId && placed.Offset > 0)
            .Select(placed => placed.Offset)
            .Distinct()
            .ToList();
        if (targetOffsets.Count == 1)
        {
            return targetOffsets[0];
        }

        var offsets = records.Cells
            .SelectMany(cell => cell.PlacedObjects)
            .Where(placed => placed.FormId == anchorReferenceFormId && placed.Offset > 0)
            .Select(placed => placed.Offset)
            .Distinct()
            .ToList();
        return offsets.Count == 1 ? offsets[0] : null;
    }

    private static bool TrySelectWindowForOffset(
        IReadOnlyList<ResolvedReferenceWindow> windows,
        long offset,
        out ResolvedReferenceWindow window,
        out bool ambiguous)
    {
        window = default;
        ambiguous = false;
        var matched = windows
            .Where(candidate => offset >= candidate.MinOffset && offset <= candidate.MaxOffset)
            .GroupBy(candidate => candidate.CellFormId)
            .ToList();
        if (matched.Count == 0)
        {
            return false;
        }

        if (matched.Count > 1)
        {
            ambiguous = true;
            return false;
        }

        window = matched[0].First();
        return true;
    }

    private static (int Moved, int CreatedCells) ReattachOffsetClusteredUnresolvedReferences(
        RecordCollection records,
        EsmRecordScanResult? scanResult)
    {
        var offsetAnchors = records.Cells
            .Where(cell => !cell.IsInterior &&
                           !cell.IsVirtual &&
                           !cell.IsUnresolvedBucket &&
                           cell.WorldspaceFormId is > 0 &&
                           cell.GridX.HasValue &&
                           cell.GridY.HasValue &&
                           cell.Offset > 0)
            .OrderBy(cell => cell.Offset)
            .ToList();

        if (offsetAnchors.Count == 0)
        {
            return (0, 0);
        }

        var cellIndexByFormId = BuildCellIndex(records.Cells);
        var moved = 0;
        var createdCells = 0;
        var nextVirtualFormId = NextAvailableSyntheticCellFormId(records, 0xFE900001u);

        for (var i = 0; i < records.Cells.Count; i++)
        {
            var source = records.Cells[i];
            if (!source.IsUnresolvedBucket || source.PlacedObjects.Count == 0)
            {
                continue;
            }

            var movedFormIds = new HashSet<uint>();
            foreach (var cluster in BuildPlacedOffsetClusters(source.PlacedObjects))
            {
                if (!TryInferAuthorityOffsetClusterWorldspace(cluster, offsetAnchors, out var worldspaceFormId))
                {
                    continue;
                }

                foreach (var placed in cluster)
                {
                    var (gx, gy) = CellUtils.WorldToCellCoordinates(placed.X, placed.Y);
                    var targetIndex = GetOrCreateAuthorityOffsetVirtualCell(
                        records,
                        cellIndexByFormId,
                        worldspaceFormId,
                        gx,
                        gy,
                        placed.IsBigEndian,
                        ref nextVirtualFormId,
                        out var created);
                    if (created)
                    {
                        createdCells++;
                    }

                    var target = records.Cells[targetIndex];
                    if (!target.PlacedObjects.Any(p => p.FormId == placed.FormId))
                    {
                        target.PlacedObjects.Add(placed with { AssignmentSource = SourceAuthorityOffsetCluster });
                    }

                    MoveScanResultRefLink(scanResult, source.FormId, target.FormId, placed.FormId);
                    movedFormIds.Add(placed.FormId);
                    moved++;
                }
            }

            if (movedFormIds.Count > 0)
            {
                records.Cells[i] = source with
                {
                    PlacedObjects = source.PlacedObjects
                        .Where(placed => !movedFormIds.Contains(placed.FormId))
                        .ToList()
                };
            }
        }

        if (moved == 0)
        {
            return (0, 0);
        }

        records.Cells.RemoveAll(c => c.IsUnresolvedBucket && c.PlacedObjects.Count == 0);
        CellRecordHandler.ResolveDoorLinks(records.Cells);
        return (moved, createdCells);
    }

    private static IEnumerable<List<PlacedReference>> BuildPlacedOffsetClusters(
        IReadOnlyList<PlacedReference> placedReferences)
    {
        List<PlacedReference>? cluster = null;
        long lastOffset = 0;
        foreach (var placed in placedReferences
                     .Where(placed => placed.Offset > 0)
                     .OrderBy(placed => placed.Offset))
        {
            if (cluster == null || placed.Offset - lastOffset > AuthorityOffsetClusterGapBytes)
            {
                if (cluster is { Count: > 0 })
                {
                    yield return cluster;
                }

                cluster = [];
            }

            cluster.Add(placed);
            lastOffset = placed.Offset;
        }

        if (cluster is { Count: > 0 })
        {
            yield return cluster;
        }
    }

    private static bool TryInferAuthorityOffsetClusterWorldspace(
        List<PlacedReference> cluster,
        List<CellRecord> offsetAnchors,
        out uint worldspaceFormId)
    {
        worldspaceFormId = 0;
        if (cluster.Count < AuthorityOffsetClusterMinPlacements)
        {
            return false;
        }

        var minOffset = cluster.Min(placed => placed.Offset);
        var maxOffset = cluster.Max(placed => placed.Offset);
        var nearbyAnchors = offsetAnchors
            .Select(cell => new
            {
                Cell = cell,
                Distance = DistanceToOffsetRange(cell.Offset, minOffset, maxOffset)
            })
            .Where(anchor => anchor.Distance <= AuthorityOffsetClusterWindowBytes)
            .ToList();

        if (nearbyAnchors.Count == 0)
        {
            return false;
        }

        var minDistance = nearbyAnchors.Min(anchor => anchor.Distance);
        var selectedAnchors = nearbyAnchors
            .Where(anchor => anchor.Distance <= minDistance + AuthorityOffsetClusterNearestBandBytes)
            .Select(anchor => anchor.Cell)
            .ToList();

        var worldspaces = selectedAnchors
            .Select(cell => cell.WorldspaceFormId!.Value)
            .Distinct()
            .ToList();
        if (worldspaces.Count != 1)
        {
            return false;
        }

        var anchorMinX = selectedAnchors.Min(cell => cell.GridX!.Value) - AuthorityOffsetClusterGridExpansion;
        var anchorMaxX = selectedAnchors.Max(cell => cell.GridX!.Value) + AuthorityOffsetClusterGridExpansion;
        var anchorMinY = selectedAnchors.Min(cell => cell.GridY!.Value) - AuthorityOffsetClusterGridExpansion;
        var anchorMaxY = selectedAnchors.Max(cell => cell.GridY!.Value) + AuthorityOffsetClusterGridExpansion;

        foreach (var placed in cluster)
        {
            var (gx, gy) = CellUtils.WorldToCellCoordinates(placed.X, placed.Y);
            if (gx < anchorMinX || gx > anchorMaxX || gy < anchorMinY || gy > anchorMaxY)
            {
                return false;
            }
        }

        worldspaceFormId = worldspaces[0];
        return true;
    }

    private static long DistanceToOffsetRange(long offset, long minOffset, long maxOffset)
    {
        if (offset < minOffset)
        {
            return minOffset - offset;
        }

        return offset > maxOffset ? offset - maxOffset : 0;
    }

    private static int GetOrCreateAuthorityOffsetVirtualCell(
        RecordCollection records,
        Dictionary<uint, int> cellIndexByFormId,
        uint worldspaceFormId,
        int gridX,
        int gridY,
        bool isBigEndian,
        ref uint nextVirtualFormId,
        out bool created)
    {
        for (var i = 0; i < records.Cells.Count; i++)
        {
            var existingCell = records.Cells[i];
            if (!existingCell.IsUnresolvedBucket &&
                existingCell.WorldspaceFormId == worldspaceFormId &&
                existingCell.GridX == gridX &&
                existingCell.GridY == gridY &&
                !existingCell.IsPersistentCell)
            {
                created = false;
                return i;
            }
        }

        while (cellIndexByFormId.ContainsKey(nextVirtualFormId))
        {
            nextVirtualFormId++;
        }

        var cellFormId = nextVirtualFormId++;
        var newCell = new CellRecord
        {
            FormId = cellFormId,
            GridX = gridX,
            GridY = gridY,
            WorldspaceFormId = worldspaceFormId,
            WorldspaceAssignmentSource = SourceAuthorityOffsetCluster,
            EditorId = $"[Virtual {gridX},{gridY}]",
            PlacedObjects = [],
            IsVirtual = true,
            IsBigEndian = isBigEndian
        };

        records.Cells.Add(newCell);
        var index = records.Cells.Count - 1;
        cellIndexByFormId[cellFormId] = index;
        created = true;
        return index;
    }

    private static uint NextAvailableSyntheticCellFormId(RecordCollection records, uint start)
    {
        var used = records.Cells.Select(cell => cell.FormId).ToHashSet();
        while (used.Contains(start))
        {
            start++;
        }

        return start;
    }

    private static Dictionary<uint, int> BuildCellIndex(List<CellRecord> cells)
    {
        var index = new Dictionary<uint, int>();
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.FormId == 0)
            {
                continue;
            }

            if (!index.TryGetValue(cell.FormId, out var existingIndex) ||
                cells[existingIndex].IsUnresolvedBucket)
            {
                index[cell.FormId] = i;
            }
        }

        return index;
    }

    private static bool TryGetOrCreateAuthorityCell(
        RecordCollection records,
        Dictionary<uint, int> cellIndexByFormId,
        uint cellFormId,
        IReadOnlyDictionary<uint, CellAuthorityMetadata>? cellMetadata,
        IReadOnlyDictionary<uint, uint>? authority,
        EsmRecordScanResult? scanResult,
        out int index,
        out bool created)
    {
        if (cellIndexByFormId.TryGetValue(cellFormId, out index))
        {
            created = false;
            return true;
        }

        CellAuthorityMetadata? metadata = null;
        cellMetadata?.TryGetValue(cellFormId, out metadata);
        uint? worldspaceFormId = metadata?.WorldspaceFormId;
        if ((worldspaceFormId is null or 0u) &&
            authority is not null &&
            authority.TryGetValue(cellFormId, out var authorityWorldspaceFormId))
        {
            worldspaceFormId = authorityWorldspaceFormId;
        }

        if (metadata is null && worldspaceFormId is not > 0)
        {
            created = false;
            return false;
        }

        var isInterior = metadata?.IsInterior ?? (worldspaceFormId is not > 0u);
        var cell = new CellRecord
        {
            FormId = cellFormId,
            Flags = (byte)(isInterior ? 0x01 : 0),
            GridX = isInterior ? null : metadata?.GridX,
            GridY = isInterior ? null : metadata?.GridY,
            WorldspaceFormId = isInterior ? null : worldspaceFormId,
            WorldspaceAssignmentSource = !isInterior && worldspaceFormId is > 0 ? "AuthorityRefParent" : null,
            EditorId = metadata?.EditorId,
            FullName = metadata?.FullName
        };

        records.Cells.Add(cell);
        index = records.Cells.Count - 1;
        cellIndexByFormId[cellFormId] = index;
        created = true;

        if (scanResult is not null && !isInterior && worldspaceFormId is > 0)
        {
            scanResult.CellToWorldspaceMap[cellFormId] = worldspaceFormId.Value;
        }

        return true;
    }

    private static void MoveScanResultRefLink(
        EsmRecordScanResult? scanResult,
        uint sourceCellFormId,
        uint targetCellFormId,
        uint referenceFormId)
    {
        if (scanResult is null || referenceFormId == 0)
        {
            return;
        }

        if (scanResult.CellToRefrMap.TryGetValue(sourceCellFormId, out var sourceRefs))
        {
            sourceRefs.Remove(referenceFormId);
        }

        if (!scanResult.CellToRefrMap.TryGetValue(targetCellFormId, out var targetRefs))
        {
            targetRefs = [];
            scanResult.CellToRefrMap[targetCellFormId] = targetRefs;
        }

        if (!targetRefs.Contains(referenceFormId))
        {
            targetRefs.Add(referenceFormId);
        }
    }

    private static int CountCellsWithTerrain(RecordCollection records)
    {
        return records.Cells.Count(c =>
            c.Heightmap is not null ||
            c.LandVisualData?.HasAny == true ||
            c.RuntimeTerrainMesh is not null);
    }

    private static int RebuildWorldspaceCellLists(
        RecordCollection records,
        IReadOnlyDictionary<uint, string>? worldspaceNames)
    {
        foreach (var (worldspaceFormId, name) in worldspaceNames ?? new Dictionary<uint, string>())
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                records.FormIdToEditorId.TryAdd(worldspaceFormId, name);
            }
        }

        var cellsByWorldspace = records.Cells
            .Where(c => !c.IsInterior && c.WorldspaceFormId is > 0)
            .GroupBy(c => c.WorldspaceFormId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var worldspaceIds = new HashSet<uint>(records.Worldspaces.Select(w => w.FormId));
        var synthesized = 0;
        foreach (var worldspaceFormId in cellsByWorldspace.Keys.OrderBy(id => id))
        {
            if (worldspaceIds.Contains(worldspaceFormId))
            {
                continue;
            }

            var name = worldspaceNames is not null &&
                       worldspaceNames.TryGetValue(worldspaceFormId, out var resolvedName)
                ? resolvedName
                : null;
            records.Worldspaces.Add(new WorldspaceRecord
            {
                FormId = worldspaceFormId,
                EditorId = string.IsNullOrWhiteSpace(name) ? null : name,
                Cells = []
            });
            worldspaceIds.Add(worldspaceFormId);
            synthesized++;
        }

        for (var i = 0; i < records.Worldspaces.Count; i++)
        {
            var ws = records.Worldspaces[i];
            cellsByWorldspace.TryGetValue(ws.FormId, out var cells);
            records.Worldspaces[i] = ws with { Cells = cells ?? [] };
        }

        return synthesized;
    }
}
