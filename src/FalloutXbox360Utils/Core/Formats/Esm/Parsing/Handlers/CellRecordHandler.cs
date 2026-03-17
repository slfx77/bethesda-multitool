using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class CellRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Cells

    /// <summary>
    ///     Parse all Cell records from the scan result.
    /// </summary>
    internal List<CellRecord> ParseCells()
    {
        var cells = new List<CellRecord>();
        var cellRecords = _context.GetRecordsByType("CELL").ToList();

        var refrRecords = _context.ScanResult.RefrRecords;
        var cellWorldMap = _context.ScanResult.CellToWorldspaceMap;
        var cellToRefrMap = _context.ScanResult.CellToRefrMap;
        var runtimeCellMapEntries = BuildRuntimeCellMapIndex();

        // Pre-build REFR FormID -> ExtractedRefrRecord lookup for O(1) access
        // Use GroupBy to handle duplicates (same REFR can appear in multiple memory regions in dumps)
        var refrByFormId = refrRecords
            .GroupBy(r => r.Header.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var hasGrupMapping = cellToRefrMap.Count > 0;

        // Pre-sort REFR records by offset for O(log N) binary search in DMP fallback
        long[]? refrOffsetIndex = null;
        ExtractedRefrRecord[]? refrSortedByOffset = null;
        if (!hasGrupMapping && refrRecords.Count > 0)
        {
            refrSortedByOffset = refrRecords.OrderBy(r => r.Header.Offset).ToArray();
            refrOffsetIndex = refrSortedByOffset.Select(r => r.Header.Offset).ToArray();
        }

        Logger.Instance.Debug($"  [Semantic] Cell parsing: {cellRecords.Count} cells, " +
                              $"{refrRecords.Count} REFRs, GRUP mapping: {hasGrupMapping} ({cellToRefrMap.Count} entries)");

        // Pre-build heightmap lookup by cell grid coordinates for O(1) access.
        // Key includes worldspace FormID to prevent cross-worldspace pollution
        // (different worldspaces can share the same cell grid coordinates).
        var landWorldMap = _context.ScanResult.LandToWorldspaceMap;
        var heightmapByGrid = new Dictionary<(uint, int, int), LandHeightmap>();
        var terrainMeshByGrid = new Dictionary<(uint, int, int), RuntimeTerrainMesh>();
        foreach (var land in _context.ScanResult.LandRecords)
        {
            if (land.BestCellX.HasValue && land.BestCellY.HasValue)
            {
                landWorldMap.TryGetValue(land.Header.FormId, out var ws);
                var gridKey = (ws, land.BestCellX.Value, land.BestCellY.Value);
                if (land.Heightmap != null)
                {
                    heightmapByGrid.TryAdd(gridKey, land.Heightmap);
                }

                if (land.RuntimeTerrainMesh != null)
                {
                    terrainMeshByGrid.TryAdd(gridKey, land.RuntimeTerrainMesh);
                }
            }
        }

        if (_context.Accessor == null)
        {
            foreach (var record in cellRecords)
            {
                var cell = ParseCellFromScanResult(record, refrByFormId,
                    hasGrupMapping ? cellToRefrMap : null, refrOffsetIndex, refrSortedByOffset,
                    runtimeCellMapEntries.GetValueOrDefault(record.FormId));
                if (cell != null)
                {
                    if (cellWorldMap.TryGetValue(cell.FormId, out var worldFormId))
                    {
                        cell = cell with { WorldspaceFormId = worldFormId };
                    }

                    cells.Add(cell);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in cellRecords)
                {
                    cellWorldMap.TryGetValue(record.FormId, out var cellWs);
                    var cell = ParseCellFromAccessor(record, refrByFormId,
                        hasGrupMapping ? cellToRefrMap : null, refrOffsetIndex, refrSortedByOffset,
                        heightmapByGrid, terrainMeshByGrid, cellWs, buffer,
                        runtimeCellMapEntries.GetValueOrDefault(record.FormId));
                    if (cell != null)
                    {
                        if (cellWs > 0)
                        {
                            cell = cell with { WorldspaceFormId = cellWs };
                        }

                        cells.Add(cell);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        MergeRuntimeCells(cells, heightmapByGrid, terrainMeshByGrid, runtimeCellMapEntries, refrByFormId);

        // Post-processing: resolve door destinations to linked cells
        ResolveDoorLinks(cells);

        return cells;
    }

    /// <summary>
    ///     Builds REFR->Cell reverse lookup and populates LinkedCellFormIds on each cell.
    /// </summary>
    private static void ResolveDoorLinks(List<CellRecord> cells)
    {
        // Build reverse map: door REFR FormID -> parent Cell FormID
        var refrToCell = new Dictionary<uint, uint>();
        foreach (var cell in cells)
        {
            foreach (var obj in cell.PlacedObjects)
            {
                refrToCell.TryAdd(obj.FormId, cell.FormId);
            }
        }

        // For each cell, find destination cells via door teleports and annotate doors
        for (var i = 0; i < cells.Count; i++)
        {
            var linkedCells = new HashSet<uint>();
            var updatedObjects = new List<PlacedReference>();
            var anyChanged = false;

#pragma warning disable S3267 // Loop body has conditional + TryGetValue that makes LINQ impractical
            foreach (var obj in cells[i].PlacedObjects)
#pragma warning restore S3267
            {
                if (obj.DestinationDoorFormId is > 0 &&
                    refrToCell.TryGetValue(obj.DestinationDoorFormId.Value, out var destCellFormId) &&
                    destCellFormId != cells[i].FormId) // Exclude self-links
                {
                    linkedCells.Add(destCellFormId);
                    updatedObjects.Add(obj with { DestinationCellFormId = destCellFormId });
                    anyChanged = true;
                }
                else
                {
                    updatedObjects.Add(obj);
                }
            }

            if (anyChanged || linkedCells.Count > 0)
            {
                cells[i] = cells[i] with
                {
                    PlacedObjects = anyChanged ? updatedObjects : cells[i].PlacedObjects,
                    LinkedCellFormIds = linkedCells.Count > 0 ? linkedCells.ToList() : cells[i].LinkedCellFormIds
                };
            }
        }
    }

    /// <summary>
    ///     Resolve placed references for a cell using GRUP-based mapping (O(1)) with
    ///     proximity heuristic fallback for DMP mode (no GRUP hierarchy).
    /// </summary>
    private List<PlacedReference> ResolveCellRefs(DetectedMainRecord record,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId,
        Dictionary<uint, List<uint>>? cellToRefrMap,
        long[]? refrOffsetIndex,
        ExtractedRefrRecord[]? refrSortedByOffset,
        RuntimeCellMapEntry? runtimeCellMapEntry)
    {
        if (cellToRefrMap != null && cellToRefrMap.TryGetValue(record.FormId, out var refrFormIds))
        {
            return ResolvePlacedReferencesByFormIds(refrFormIds, refrByFormId, AssignmentSourceCellGrup);
        }

        if (runtimeCellMapEntry is { ReferenceFormIds.Count: > 0 })
        {
            return ResolvePlacedReferencesByFormIds(
                runtimeCellMapEntry.ReferenceFormIds,
                refrByFormId,
                AssignmentSourceRuntimeCellList);
        }

        if (refrOffsetIndex != null && refrSortedByOffset != null)
        {
            // DMP fallback with binary search: O(log N + K) instead of O(N)
            var startOffset = record.Offset;
            var endOffset = record.Offset + 500_000; // CELL GRUPs can span hundreds of KB
            var startIdx = Array.BinarySearch(refrOffsetIndex, startOffset);
            if (startIdx < 0) startIdx = ~startIdx; // First element >= startOffset

            var results = new List<ExtractedRefrRecord>();
            for (var i = startIdx; i < refrSortedByOffset.Length; i++)
            {
                var refr = refrSortedByOffset[i];
                if (refr.Header.Offset >= endOffset) break;
                if (refr.Header.Offset > startOffset)
                {
                    results.Add(refr);
                }
            }

            if (results.Count > 500)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Cell 0x{record.FormId:X8}: DMP proximity found {results.Count} REFRs (may include neighbors)");
            }

            return ToPlacedReferences(results, AssignmentSourceProximity);
        }

        return [];
    }

    private CellRecord? ParseCellFromAccessor(DetectedMainRecord record,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId,
        Dictionary<uint, List<uint>>? cellToRefrMap,
        long[]? refrOffsetIndex,
        ExtractedRefrRecord[]? refrSortedByOffset,
        Dictionary<(uint, int, int), LandHeightmap> heightmapByGrid,
        Dictionary<(uint, int, int), RuntimeTerrainMesh> terrainMeshByGrid,
        uint cellWorldspace,
        byte[] buffer,
        RuntimeCellMapEntry? runtimeCellMapEntry)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseCellFromScanResult(record, refrByFormId, cellToRefrMap,
                refrOffsetIndex, refrSortedByOffset, runtimeCellMapEntry);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        int? gridX = null;
        int? gridY = null;
        byte flags = 0;
        float? waterHeight = null;
        uint? encounterZoneFormId = null;
        uint? musicTypeFormId = null;
        uint? acousticSpaceFormId = null;
        uint? imageSpaceFormId = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 1:
                    flags = subData[0];
                    break;
                case "XCLC" when sub.DataLength >= 12:
                {
                    var fields = SubrecordDataReader.ReadFields("XCLC", null, subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        gridX = SubrecordDataReader.GetInt32(fields, "X");
                        gridY = SubrecordDataReader.GetInt32(fields, "Y");
                    }
                }

                    break;
                case "XCLW" when sub.DataLength >= 4:
                    waterHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    break;
                case "XEZN" when sub.DataLength == 4:
                    encounterZoneFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "XCMO" when sub.DataLength == 4:
                    musicTypeFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "XCAS" when sub.DataLength == 4:
                    acousticSpaceFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "XCIM" when sub.DataLength == 4:
                    imageSpaceFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        var cellRefs = ResolveCellRefs(record, refrByFormId, cellToRefrMap,
            refrOffsetIndex, refrSortedByOffset, runtimeCellMapEntry);

        // Find associated heightmap and terrain mesh via O(1) dictionary lookups.
        // Key includes worldspace to prevent cross-worldspace pollution.
        // Falls back to worldspace 0 for DMP mode (no GRUP hierarchy).
        LandHeightmap? heightmap = null;
        RuntimeTerrainMesh? terrainMesh = null;
        if (gridX.HasValue && gridY.HasValue)
        {
            var gridKey = (cellWorldspace, gridX.Value, gridY.Value);
            if (!heightmapByGrid.TryGetValue(gridKey, out heightmap) && cellWorldspace != 0)
            {
                heightmapByGrid.TryGetValue((0u, gridX.Value, gridY.Value), out heightmap);
            }

            if (!terrainMeshByGrid.TryGetValue(gridKey, out terrainMesh) && cellWorldspace != 0)
            {
                terrainMeshByGrid.TryGetValue((0u, gridX.Value, gridY.Value), out terrainMesh);
            }
        }

        return new CellRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            GridX = gridX,
            GridY = gridY,
            Flags = flags,
            WaterHeight = waterHeight,
            EncounterZoneFormId = encounterZoneFormId,
            MusicTypeFormId = musicTypeFormId,
            AcousticSpaceFormId = acousticSpaceFormId,
            ImageSpaceFormId = imageSpaceFormId,
            PlacedObjects = cellRefs,
            HasPersistentObjects = cellRefs.Exists(r => r.IsPersistent),
            Heightmap = heightmap,
            RuntimeTerrainMesh = terrainMesh,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private CellRecord? ParseCellFromScanResult(DetectedMainRecord record,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId,
        Dictionary<uint, List<uint>>? cellToRefrMap,
        long[]? refrOffsetIndex,
        ExtractedRefrRecord[]? refrSortedByOffset,
        RuntimeCellMapEntry? runtimeCellMapEntry)
    {
        // Find XCLC near this CELL record
        var cellGrid = _context.ScanResult.CellGrids
            .FirstOrDefault(g => Math.Abs(g.Offset - record.Offset) < 200);

        var cellRefs = ResolveCellRefs(record, refrByFormId, cellToRefrMap,
            refrOffsetIndex, refrSortedByOffset, runtimeCellMapEntry);

        return new CellRecord
        {
            FormId = record.FormId,
            EditorId = _context.GetEditorId(record.FormId),
            GridX = cellGrid?.GridX,
            GridY = cellGrid?.GridY,
            PlacedObjects = cellRefs,
            HasPersistentObjects = cellRefs.Exists(r => r.IsPersistent),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private void MergeRuntimeCells(
        List<CellRecord> cells,
        Dictionary<(uint, int, int), LandHeightmap> heightmapByGrid,
        Dictionary<(uint, int, int), RuntimeTerrainMesh> terrainMeshByGrid,
        Dictionary<uint, RuntimeCellMapEntry> runtimeCellMapEntries,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId)
    {
        if (_context.RuntimeReader == null)
        {
            return;
        }

        _context.MergeRuntimeOverlayRecords(
            cells,
            [0x39],
            record => record.FormId,
            (reader, entry) =>
            {
                if (runtimeCellMapEntries.TryGetValue(entry.FormId, out var mapEntry))
                {
                    return BuildRuntimeCellRecord(
                        reader,
                        mapEntry,
                        refrByFormId,
                        entry.EditorId,
                        entry.DisplayName,
                        entry);
                }

                return reader.ReadRuntimeCell(entry);
            },
            MergeCell,
            "cells");

        if (runtimeCellMapEntries.Count > 0)
        {
            var cellIndexByFormId = new Dictionary<uint, int>(cells.Count);
            for (var i = 0; i < cells.Count; i++)
            {
                cellIndexByFormId.TryAdd(cells[i].FormId, i);
            }

            var mapOnlyAdded = 0;
            foreach (var (cellFormId, mapEntry) in runtimeCellMapEntries)
            {
                var displayName = _context.FormIdToFullName.GetValueOrDefault(cellFormId);
                var mapRuntimeCell = BuildRuntimeCellRecord(
                    _context.RuntimeReader,
                    mapEntry,
                    refrByFormId,
                    _context.GetEditorId(cellFormId),
                    displayName);
                if (mapRuntimeCell == null)
                {
                    continue;
                }

                if (cellIndexByFormId.TryGetValue(cellFormId, out var existingIndex))
                {
                    cells[existingIndex] = MergeCell(cells[existingIndex], mapRuntimeCell);
                }
                else
                {
                    cells.Add(mapRuntimeCell);
                    cellIndexByFormId[cellFormId] = cells.Count - 1;
                    mapOnlyAdded++;
                }
            }

            if (mapOnlyAdded > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {mapOnlyAdded} partial cells from runtime worldspace maps");
            }
        }

        for (var i = 0; i < cells.Count; i++)
        {
            cells[i] = AttachTerrainData(cells[i], heightmapByGrid, terrainMeshByGrid);
        }
    }

    private Dictionary<uint, RuntimeCellMapEntry> BuildRuntimeCellMapIndex()
    {
        var entries = new Dictionary<uint, RuntimeCellMapEntry>();
        if (_context.RuntimeWorldspaceCellMaps is not { Count: > 0 })
        {
            return entries;
        }

        foreach (var (_, worldData) in _context.RuntimeWorldspaceCellMaps)
        {
            foreach (var entry in worldData.Cells)
            {
                entries.TryAdd(entry.CellFormId, entry);
            }

            if (worldData.PersistentCellFormId is > 0 &&
                !entries.ContainsKey(worldData.PersistentCellFormId.Value))
            {
                entries[worldData.PersistentCellFormId.Value] = new RuntimeCellMapEntry
                {
                    CellFormId = worldData.PersistentCellFormId.Value,
                    GridX = 0,
                    GridY = 0,
                    IsPersistent = true,
                    WorldspaceFormId = worldData.FormId
                };
            }
        }

        return entries;
    }

    private static CellRecord MergeCell(CellRecord esm, CellRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            FullName = esm.FullName ?? runtime.FullName,
            GridX = esm.GridX ?? runtime.GridX,
            GridY = esm.GridY ?? runtime.GridY,
            WorldspaceFormId = esm.WorldspaceFormId ?? runtime.WorldspaceFormId,
            Flags = esm.Flags != 0 ? esm.Flags : runtime.Flags,
            WaterHeight = esm.WaterHeight ?? runtime.WaterHeight,
            EncounterZoneFormId = esm.EncounterZoneFormId ?? runtime.EncounterZoneFormId,
            MusicTypeFormId = esm.MusicTypeFormId ?? runtime.MusicTypeFormId,
            AcousticSpaceFormId = esm.AcousticSpaceFormId ?? runtime.AcousticSpaceFormId,
            ImageSpaceFormId = esm.ImageSpaceFormId ?? runtime.ImageSpaceFormId,
            PlacedObjects = ShouldUseRuntimePlacedObjects(esm, runtime)
                ? runtime.PlacedObjects
                : esm.PlacedObjects.Count > 0 ? esm.PlacedObjects : runtime.PlacedObjects,
            LinkedCellFormIds = esm.LinkedCellFormIds.Count > 0 ? esm.LinkedCellFormIds : runtime.LinkedCellFormIds,
            Heightmap = esm.Heightmap ?? runtime.Heightmap,
            RuntimeTerrainMesh = esm.RuntimeTerrainMesh ?? runtime.RuntimeTerrainMesh,
            HasPersistentObjects = esm.HasPersistentObjects || runtime.HasPersistentObjects,
            IsVirtual = esm.IsVirtual || runtime.IsVirtual,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    private CellRecord? BuildRuntimeCellRecord(
        RuntimeStructReader reader,
        RuntimeCellMapEntry mapEntry,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId,
        string? editorId = null,
        string? displayName = null,
        RuntimeEditorIdEntry? runtimeEntry = null)
    {
        var mapCell = reader.ReadRuntimeCell(mapEntry, editorId, displayName);
        var runtimeCell = runtimeEntry != null
            ? reader.ReadRuntimeCell(runtimeEntry)
            : null;

        var mergedCell = runtimeCell != null
            ? mapCell != null ? MergeCell(runtimeCell, mapCell) : runtimeCell
            : mapCell;
        if (mergedCell == null)
        {
            return null;
        }

        var placedObjects = ResolvePlacedReferencesByFormIds(
            mapEntry.ReferenceFormIds,
            refrByFormId,
            AssignmentSourceRuntimeCellList);
        if (placedObjects.Count == 0)
        {
            return mergedCell;
        }

        return mergedCell with
        {
            PlacedObjects = placedObjects,
            HasPersistentObjects = mergedCell.HasPersistentObjects || placedObjects.Exists(obj => obj.IsPersistent)
        };
    }

    private List<PlacedReference> ResolvePlacedReferencesByFormIds(
        IEnumerable<uint> formIds,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId,
        string assignmentSource)
    {
        var seen = new HashSet<uint>();
        var sourceRefs = new List<ExtractedRefrRecord>();

        foreach (var formId in formIds)
        {
            if (formId == 0 || !seen.Add(formId))
            {
                continue;
            }

            if (refrByFormId.TryGetValue(formId, out var refr))
            {
                sourceRefs.Add(refr);
            }
        }

        return ToPlacedReferences(sourceRefs, assignmentSource);
    }

    private List<PlacedReference> ToPlacedReferences(
        IEnumerable<ExtractedRefrRecord> sourceRefs,
        string assignmentSource)
    {
        return sourceRefs
            .Select(r => CellLinkageHandler.ToPlacedReference(r, _context, assignmentSource))
            .ToList();
    }

    private static bool ShouldUseRuntimePlacedObjects(CellRecord existing, CellRecord runtime)
    {
        if (runtime.PlacedObjects.Count == 0)
        {
            return false;
        }

        if (existing.PlacedObjects.Count == 0)
        {
            return true;
        }

        return runtime.PlacedObjects.TrueForAll(obj => obj.AssignmentSource == AssignmentSourceRuntimeCellList) &&
               existing.PlacedObjects.TrueForAll(obj => obj.AssignmentSource == AssignmentSourceProximity);
    }

    private static CellRecord AttachTerrainData(
        CellRecord cell,
        Dictionary<(uint, int, int), LandHeightmap> heightmapByGrid,
        Dictionary<(uint, int, int), RuntimeTerrainMesh> terrainMeshByGrid)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            return cell;
        }

        LandHeightmap? heightmap = cell.Heightmap;
        RuntimeTerrainMesh? terrainMesh = cell.RuntimeTerrainMesh;

        var worldspaceFormId = cell.WorldspaceFormId ?? 0;
        var key = (worldspaceFormId, cell.GridX.Value, cell.GridY.Value);
        if (heightmap == null)
        {
            if (!heightmapByGrid.TryGetValue(key, out heightmap) && worldspaceFormId != 0)
            {
                heightmapByGrid.TryGetValue((0u, cell.GridX.Value, cell.GridY.Value), out heightmap);
            }
        }

        if (terrainMesh == null)
        {
            if (!terrainMeshByGrid.TryGetValue(key, out terrainMesh) && worldspaceFormId != 0)
            {
                terrainMeshByGrid.TryGetValue((0u, cell.GridX.Value, cell.GridY.Value), out terrainMesh);
            }
        }

        if (heightmap == null && terrainMesh == null)
        {
            return cell;
        }

        return cell with
        {
            Heightmap = heightmap ?? cell.Heightmap,
            RuntimeTerrainMesh = terrainMesh ?? cell.RuntimeTerrainMesh
        };
    }

    #endregion

    private const string AssignmentSourceCellGrup = "CellGrup";
    private const string AssignmentSourceRuntimeCellList = "RuntimeCellList";
    private const string AssignmentSourceProximity = "Proximity";
}
