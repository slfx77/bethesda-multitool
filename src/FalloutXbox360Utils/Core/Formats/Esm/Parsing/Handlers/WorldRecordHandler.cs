using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class WorldRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Map Markers

    /// <summary>
    ///     Extract map markers from REFR records that have the XMRK subrecord.
    /// </summary>
    internal List<PlacedReference> ExtractMapMarkers()
    {
        var markers = new List<PlacedReference>();

        // Map markers come from REFR records with XMRK subrecord
        foreach (var refr in _context.ScanResult.RefrRecords)
        {
            if (!refr.IsMapMarker)
            {
                continue;
            }

            var marker = new PlacedReference
            {
                FormId = refr.Header.FormId,
                BaseFormId = refr.BaseFormId,
                BaseEditorId = refr.BaseEditorId ?? _context.GetEditorId(refr.BaseFormId),
                RecordType = refr.Header.RecordType,
                X = refr.Position?.X ?? 0,
                Y = refr.Position?.Y ?? 0,
                Z = refr.Position?.Z ?? 0,
                RotX = refr.Position?.RotX ?? 0,
                RotY = refr.Position?.RotY ?? 0,
                RotZ = refr.Position?.RotZ ?? 0,
                Scale = refr.Scale,
                OwnerFormId = refr.OwnerFormId,
                EnableParentFormId = refr.EnableParentFormId,
                EnableParentFlags = refr.EnableParentFlags,
                IsInitiallyDisabled = refr.Header.IsInitiallyDisabled,
                IsMapMarker = true,
                MarkerType = refr.MarkerType.HasValue ? (MapMarkerType)refr.MarkerType.Value : null,
                MarkerName = refr.MarkerName,
                Offset = refr.Header.Offset,
                IsBigEndian = refr.Header.IsBigEndian
            };

            markers.Add(marker);
        }

        return markers;
    }

    #endregion

    #region Cells

    /// <summary>
    ///     Reconstruct all Cell records from the scan result.
    /// </summary>
    internal List<CellRecord> ReconstructCells()
    {
        var cells = new List<CellRecord>();
        var cellRecords = _context.GetRecordsByType("CELL").ToList();

        var refrRecords = _context.ScanResult.RefrRecords;
        var cellWorldMap = _context.ScanResult.CellToWorldspaceMap;
        var cellToRefrMap = _context.ScanResult.CellToRefrMap;

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

        Logger.Instance.Debug($"  [Semantic] Cell reconstruction: {cellRecords.Count} cells, " +
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
                var cell = ReconstructCellFromScanResult(record, refrByFormId,
                    hasGrupMapping ? cellToRefrMap : null, refrOffsetIndex, refrSortedByOffset);
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
                    var cell = ReconstructCellFromAccessor(record, refrByFormId,
                        hasGrupMapping ? cellToRefrMap : null, refrOffsetIndex, refrSortedByOffset,
                        heightmapByGrid, terrainMeshByGrid, cellWs, buffer);
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
    ///     DMP fallback: infer worldspace membership for exterior cells by testing grid coordinates
    ///     against worldspace bounds (NAM0/NAM9 or MNAM cell ranges). Called when no GRUP hierarchy
    ///     is available to provide exact cell-to-worldspace mapping.
    /// </summary>
    internal static void InferCellWorldspaces(List<CellRecord> cells, List<WorldspaceRecord> worldspaces)
    {
        if (worldspaces.Count == 0)
        {
            return;
        }

        // Build bounding boxes in cell grid coordinates from each worldspace's bounds data.
        // Prefer MNAM (explicit cell ranges) over NAM0/NAM9 (world unit coordinates).
        var worldspaceBounds = new List<(WorldspaceRecord Ws, int MinCellX, int MinCellY, int MaxCellX, int MaxCellY)>();
        foreach (var ws in worldspaces)
        {
            int minCellX, minCellY, maxCellX, maxCellY;

            if (ws.MapNWCellX.HasValue && ws.MapSECellX.HasValue)
            {
                // MNAM: explicit cell ranges (NW = top-left, SE = bottom-right)
                minCellX = Math.Min(ws.MapNWCellX.Value, ws.MapSECellX.Value);
                maxCellX = Math.Max(ws.MapNWCellX.Value, ws.MapSECellX.Value);
                minCellY = Math.Min(ws.MapNWCellY!.Value, ws.MapSECellY!.Value);
                maxCellY = Math.Max(ws.MapNWCellY.Value, ws.MapSECellY.Value);
            }
            else if (ws.BoundsMinX.HasValue && ws.BoundsMaxX.HasValue)
            {
                // NAM0/NAM9: world unit coordinates, convert to cell grid (4096 units per cell)
                minCellX = (int)MathF.Floor(ws.BoundsMinX.Value / 4096f);
                maxCellX = (int)MathF.Floor(ws.BoundsMaxX.Value / 4096f);
                minCellY = (int)MathF.Floor(ws.BoundsMinY!.Value / 4096f);
                maxCellY = (int)MathF.Floor(ws.BoundsMaxY!.Value / 4096f);
            }
            else
            {
                continue; // No bounds data available
            }

            worldspaceBounds.Add((ws, minCellX, minCellY, maxCellX, maxCellY));
        }

        if (worldspaceBounds.Count == 0)
        {
            // No worldspaces have bounds data; assign all exterior cells to the first worldspace
            // as a best-effort fallback.
            var fallback = worldspaces[0];
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if (!cell.IsInterior && cell.GridX.HasValue && cell.WorldspaceFormId is null or 0)
                {
                    cells[i] = cell with { WorldspaceFormId = fallback.FormId };
                }
            }

            Logger.Instance.Debug(
                $"  [Semantic] InferCellWorldspaces: no bounds data, assigned all exterior cells to {fallback.EditorId ?? $"0x{fallback.FormId:X8}"}");
            return;
        }

        // Sort by area descending so the largest worldspace is used as tiebreaker
        worldspaceBounds.Sort((a, b) =>
        {
            var areaA = (long)(a.MaxCellX - a.MinCellX) * (a.MaxCellY - a.MinCellY);
            var areaB = (long)(b.MaxCellX - b.MinCellX) * (b.MaxCellY - b.MinCellY);
            return areaB.CompareTo(areaA);
        });

        var inferredCount = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.IsInterior || !cell.GridX.HasValue || cell.WorldspaceFormId is > 0)
            {
                continue;
            }

            var gx = cell.GridX.Value;
            var gy = cell.GridY!.Value;

            // Find matching worldspace (prefer largest by area = first in sorted list)
            foreach (var (ws, minX, minY, maxX, maxY) in worldspaceBounds)
            {
                if (gx >= minX && gx <= maxX && gy >= minY && gy <= maxY)
                {
                    cells[i] = cell with { WorldspaceFormId = ws.FormId };
                    inferredCount++;
                    break;
                }
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] InferCellWorldspaces: inferred {inferredCount} cells across {worldspaceBounds.Count} worldspaces");
    }

    /// <summary>
    ///     Links reconstructed cells to their parent worldspace's Cells list
    ///     based on CellRecord.WorldspaceFormId.
    /// </summary>
    internal static void LinkCellsToWorldspaces(List<CellRecord> cells, List<WorldspaceRecord> worldspaces)
    {
        var worldspaceByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < worldspaces.Count; i++)
        {
            worldspaceByFormId.TryAdd(worldspaces[i].FormId, i);
        }

        var cellsByWorldspace = new Dictionary<uint, List<CellRecord>>();
        foreach (var cell in cells)
        {
            if (cell.WorldspaceFormId is > 0 &&
                worldspaceByFormId.ContainsKey(cell.WorldspaceFormId.Value))
            {
                if (!cellsByWorldspace.TryGetValue(cell.WorldspaceFormId.Value, out var list))
                {
                    list = [];
                    cellsByWorldspace[cell.WorldspaceFormId.Value] = list;
                }

                list.Add(cell);
            }
        }

        foreach (var (worldFormId, worldCells) in cellsByWorldspace)
        {
            if (worldspaceByFormId.TryGetValue(worldFormId, out var idx))
            {
                worldspaces[idx] = worldspaces[idx] with { Cells = worldCells };
            }
        }
    }

    /// <summary>
    ///     DMP fallback: create virtual cells for orphan REFR/ACHR/ACRE records that were not
    ///     assigned to any real cell. Groups orphans by their XY-derived grid position and
    ///     creates synthetic CellRecord entries so they appear on the world map.
    /// </summary>
    internal static List<CellRecord> CreateVirtualCells(
        List<CellRecord> existingCells,
        IReadOnlyList<ExtractedRefrRecord> allRefrs,
        RecordParserContext context)
    {
        // Collect FormIDs of all refs already placed in cells
        var placedFormIds = new HashSet<uint>();
        foreach (var cell in existingCells)
        {
            foreach (var obj in cell.PlacedObjects)
            {
                placedFormIds.Add(obj.FormId);
            }
        }

        // Find orphan refs (have a valid position but not in any cell)
        var orphans = allRefrs
            .Where(r => !placedFormIds.Contains(r.Header.FormId) && r.Position != null
                        && (MathF.Abs(r.Position.X) > 1f || MathF.Abs(r.Position.Y) > 1f))
            .ToList();

        if (orphans.Count == 0)
        {
            return [];
        }

        // Group by grid cell derived from position (4096 world units per cell)
        var groups = orphans
            .GroupBy(r => ((int)MathF.Floor(r.Position!.X / 4096f), (int)MathF.Floor(r.Position.Y / 4096f)))
            .Where(g => g.Count() >= 1)
            .ToList();

        var virtualCells = new List<CellRecord>();
        var syntheticFormId = 0xFF000001u; // Synthetic FormIDs in a high range unlikely to collide

        foreach (var group in groups)
        {
            var (gridX, gridY) = group.Key;
            var refs = group.Select(r => new PlacedReference
            {
                FormId = r.Header.FormId,
                BaseFormId = r.BaseFormId,
                BaseEditorId = r.BaseEditorId ?? context.GetEditorId(r.BaseFormId),
                RecordType = r.Header.RecordType,
                X = r.Position?.X ?? 0,
                Y = r.Position?.Y ?? 0,
                Z = r.Position?.Z ?? 0,
                RotX = r.Position?.RotX ?? 0,
                RotY = r.Position?.RotY ?? 0,
                RotZ = r.Position?.RotZ ?? 0,
                Scale = r.Scale,
                OwnerFormId = r.OwnerFormId,
                EnableParentFormId = r.EnableParentFormId,
                EnableParentFlags = r.EnableParentFlags,
                IsInitiallyDisabled = r.Header.IsInitiallyDisabled,
                DestinationDoorFormId = r.DestinationDoorFormId,
                IsMapMarker = r.IsMapMarker,
                MarkerType = r.MarkerType.HasValue ? (MapMarkerType)r.MarkerType.Value : null,
                MarkerName = r.MarkerName,
                Offset = r.Header.Offset,
                IsBigEndian = r.Header.IsBigEndian
            }).ToList();

            virtualCells.Add(new CellRecord
            {
                FormId = syntheticFormId++,
                EditorId = $"[Virtual {gridX},{gridY}]",
                GridX = gridX,
                GridY = gridY,
                PlacedObjects = refs,
                IsVirtual = true,
                IsBigEndian = refs[0].IsBigEndian
            });
        }

        Logger.Instance.Debug(
            $"  [Semantic] CreateVirtualCells: {orphans.Count} orphan refs → {virtualCells.Count} virtual cells");

        return virtualCells;
    }

    /// <summary>
    ///     Resolve placed references for a cell using GRUP-based mapping (O(1)) with
    ///     proximity heuristic fallback for DMP mode (no GRUP hierarchy).
    /// </summary>
    private List<PlacedReference> ResolveCellRefs(DetectedMainRecord record,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId,
        Dictionary<uint, List<uint>>? cellToRefrMap,
        long[]? refrOffsetIndex,
        ExtractedRefrRecord[]? refrSortedByOffset)
    {
        IEnumerable<ExtractedRefrRecord> sourceRefs;

        if (cellToRefrMap != null && cellToRefrMap.TryGetValue(record.FormId, out var refrFormIds))
        {
            // GRUP-based: exact O(1) lookup per REFR
            sourceRefs = refrFormIds
                .Select(fid => refrByFormId.GetValueOrDefault(fid))
                .Where(r => r != null)!;
        }
        else if (refrOffsetIndex != null && refrSortedByOffset != null)
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

            sourceRefs = results;
        }
        else
        {
            sourceRefs = [];
        }

        return sourceRefs
            .Select(r => new PlacedReference
            {
                FormId = r.Header.FormId,
                BaseFormId = r.BaseFormId,
                BaseEditorId = r.BaseEditorId ?? _context.GetEditorId(r.BaseFormId),
                RecordType = r.Header.RecordType,
                X = r.Position?.X ?? 0,
                Y = r.Position?.Y ?? 0,
                Z = r.Position?.Z ?? 0,
                RotX = r.Position?.RotX ?? 0,
                RotY = r.Position?.RotY ?? 0,
                RotZ = r.Position?.RotZ ?? 0,
                Scale = r.Scale,
                OwnerFormId = r.OwnerFormId,
                EnableParentFormId = r.EnableParentFormId,
                EnableParentFlags = r.EnableParentFlags,
                IsInitiallyDisabled = r.Header.IsInitiallyDisabled,
                DestinationDoorFormId = r.DestinationDoorFormId,
                IsMapMarker = r.IsMapMarker,
                MarkerType = r.MarkerType.HasValue ? (MapMarkerType)r.MarkerType.Value : null,
                MarkerName = r.MarkerName,
                Offset = r.Header.Offset,
                IsBigEndian = r.Header.IsBigEndian
            })
            .ToList();
    }

    private CellRecord? ReconstructCellFromAccessor(DetectedMainRecord record,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId,
        Dictionary<uint, List<uint>>? cellToRefrMap,
        long[]? refrOffsetIndex,
        ExtractedRefrRecord[]? refrSortedByOffset,
        Dictionary<(uint, int, int), LandHeightmap> heightmapByGrid,
        Dictionary<(uint, int, int), RuntimeTerrainMesh> terrainMeshByGrid,
        uint cellWorldspace,
        byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructCellFromScanResult(record, refrByFormId, cellToRefrMap,
                refrOffsetIndex, refrSortedByOffset);
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
            refrOffsetIndex, refrSortedByOffset);

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
            Heightmap = heightmap,
            RuntimeTerrainMesh = terrainMesh,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private CellRecord? ReconstructCellFromScanResult(DetectedMainRecord record,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId,
        Dictionary<uint, List<uint>>? cellToRefrMap,
        long[]? refrOffsetIndex,
        ExtractedRefrRecord[]? refrSortedByOffset)
    {
        // Find XCLC near this CELL record
        var cellGrid = _context.ScanResult.CellGrids
            .FirstOrDefault(g => Math.Abs(g.Offset - record.Offset) < 200);

        var cellRefs = ResolveCellRefs(record, refrByFormId, cellToRefrMap,
            refrOffsetIndex, refrSortedByOffset);

        return new CellRecord
        {
            FormId = record.FormId,
            EditorId = _context.GetEditorId(record.FormId),
            GridX = cellGrid?.GridX,
            GridY = cellGrid?.GridY,
            PlacedObjects = cellRefs,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Worldspaces

    /// <summary>
    ///     Reconstruct all Worldspace records from the scan result.
    /// </summary>
    internal List<WorldspaceRecord> ReconstructWorldspaces()
    {
        var worldspaces = new List<WorldspaceRecord>();
        var wrldRecords = _context.GetRecordsByType("WRLD").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in wrldRecords)
            {
                var worldspace = ReconstructWorldspaceFromScanResult(record);
                if (worldspace != null)
                {
                    worldspaces.Add(worldspace);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in wrldRecords)
                {
                    var worldspace = ReconstructWorldspaceFromAccessor(record, buffer);
                    if (worldspace != null)
                    {
                        worldspaces.Add(worldspace);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return worldspaces;
    }

    private WorldspaceRecord? ReconstructWorldspaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructWorldspaceFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        uint? parentWorldspace = null;
        uint? climate = null;
        uint? water = null;
        float? defaultLandHeight = null;
        float? defaultWaterHeight = null;
        int? mapUsableWidth = null;
        int? mapUsableHeight = null;
        short? mapNWCellX = null;
        short? mapNWCellY = null;
        short? mapSECellX = null;
        short? mapSECellY = null;
        float? boundsMinX = null;
        float? boundsMinY = null;
        float? boundsMaxX = null;
        float? boundsMaxY = null;
        uint? encounterZone = null;

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
                case "WNAM" when sub.DataLength == 4:
                    parentWorldspace = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNAM" when sub.DataLength == 4:
                    climate = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM2" when sub.DataLength == 4:
                    water = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DNAM" when sub.DataLength >= 8:
                    defaultLandHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    defaultWaterHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
                case "MNAM" when sub.DataLength >= 16:
                    mapUsableWidth = (int)(record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData));
                    mapUsableHeight = (int)(record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]));
                    mapNWCellX = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[8..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[8..]);
                    mapNWCellY = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[10..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[10..]);
                    mapSECellX = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[12..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[12..]);
                    mapSECellY = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[14..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[14..]);
                    break;
                case "NAM0" when sub.DataLength >= 8:
                    boundsMinX = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    boundsMinY = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
                case "NAM9" when sub.DataLength >= 8:
                    boundsMaxX = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    boundsMaxY = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
                case "XEZN" when sub.DataLength == 4:
                    encounterZone = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new WorldspaceRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            ParentWorldspaceFormId = parentWorldspace,
            ClimateFormId = climate,
            WaterFormId = water,
            DefaultLandHeight = defaultLandHeight,
            DefaultWaterHeight = defaultWaterHeight,
            MapUsableWidth = mapUsableWidth,
            MapUsableHeight = mapUsableHeight,
            MapNWCellX = mapNWCellX,
            MapNWCellY = mapNWCellY,
            MapSECellX = mapSECellX,
            MapSECellY = mapSECellY,
            BoundsMinX = boundsMinX,
            BoundsMinY = boundsMinY,
            BoundsMaxX = boundsMaxX,
            BoundsMaxY = boundsMaxY,
            EncounterZoneFormId = encounterZone,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private WorldspaceRecord? ReconstructWorldspaceFromScanResult(DetectedMainRecord record)
    {
        return new WorldspaceRecord
        {
            FormId = record.FormId,
            EditorId = _context.GetEditorId(record.FormId),
            FullName = _context.FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Enrichment

    /// <summary>
    ///     Enrich placed references in cells with base object bounds and model paths.
    ///     Joins PlacedReference.BaseFormId to pre-built indexes from reconstructed base objects.
    /// </summary>
    internal static void EnrichPlacedReferences(
        List<CellRecord> cells,
        Dictionary<uint, ObjectBounds> boundsIndex,
        Dictionary<uint, string> modelIndex)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (!cell.PlacedObjects.Any(obj =>
                    boundsIndex.ContainsKey(obj.BaseFormId) || modelIndex.ContainsKey(obj.BaseFormId)))
            {
                continue;
            }

            var enriched = cell.PlacedObjects.Select(obj =>
            {
                boundsIndex.TryGetValue(obj.BaseFormId, out var bounds);
                modelIndex.TryGetValue(obj.BaseFormId, out var modelPath);
                return bounds != null || modelPath != null
                    ? obj with { Bounds = bounds ?? obj.Bounds, ModelPath = modelPath ?? obj.ModelPath }
                    : obj;
            }).ToList();

            cells[i] = cell with { PlacedObjects = enriched };
        }
    }

    #endregion
}
