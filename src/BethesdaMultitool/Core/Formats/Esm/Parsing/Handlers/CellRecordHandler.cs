using System.Buffers;
using System.Buffers.Binary;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal sealed class CellRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    private const string AssignmentSourceCellGrup = "CellGrup";
    private const string AssignmentSourceRuntimeCellList = "RuntimeCellList";
    private const string AssignmentSourceProximity = "Proximity";

    #region Cells

    /// <summary>
    ///     Parse all Cell records from the scan result.
    /// </summary>
    internal List<CellRecord> ParseCells()
    {
        var cellRecords = Context.GetRecordListByType("CELL");
        var cells = new List<CellRecord>(cellRecords.Count);

        var refrRecords = Context.ScanResult.RefrRecords;
        var cellWorldMap = Context.ScanResult.CellToWorldspaceMap;
        var cellToRefrMap = Context.ScanResult.CellToRefrMap;
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
        var landWorldMap = Context.ScanResult.LandToWorldspaceMap;
        var heightmapByCellFormId = new Dictionary<uint, LandHeightmap>();
        var capturedHeightmapByCellFormId = new Dictionary<uint, LandHeightmap>();
        var visualDataByCellFormId = new Dictionary<uint, LandVisualData>();
        var terrainMeshByCellFormId = new Dictionary<uint, RuntimeTerrainMesh>();
        var heightmapByGrid = new Dictionary<(uint, int, int), LandHeightmap>();
        var capturedHeightmapByGrid = new Dictionary<(uint, int, int), LandHeightmap>();
        var visualDataByGrid = new Dictionary<(uint, int, int), LandVisualData>();
        var terrainMeshByGrid = new Dictionary<(uint, int, int), RuntimeTerrainMesh>();
        // TES4-era: 579 of retail Oblivion.esm's 31,823 LANDs are pre-release relics — UNCOMPRESSED
        // and carrying NO shipped BTXT/ATXT/VTXT layers (29 with the legacy VTEX grid, 550 bare
        // VHGT/VNML sculpts), all in the 22 Tamriel-parented city/IC/test child worldspaces. The
        // engine ignores every one of them: Anvil (-45,-8) shows the parent's terrain (user-verified
        // in-game A/B 2026-07-21); ICMarketDistrict's flora is planted ~500 units ABOVE its relic
        // sculpts at the parent platform heights; Chorrol's relics would float +26,900 units. Skip
        // attaching them so the parent-terrain inheritance pass
        // (CellLinkageHandler.InheritTerrainFromParentWorldspaces) fills the slot the way the engine
        // does. The !IsCompressed condition is load-bearing: retail's 9,338 COMPRESSED layer-less
        // LANDs (Tamriel/SEWorld ocean + plane cells rendered with the engine-default texture) are
        // genuine and must attach — release-era CS re-saves compress LAND; the relics never were.
        // The gate DROPS data, so it requires an AFFIRMATIVE game identity — only Oblivion authors
        // these relics. "Not FO3+" would also sweep Unknown-game scans (synthetic fixtures, partial
        // DMP carves whose LANDs are decompressed in memory and often layer-less) into losing
        // terrain they legitimately own.
        var skipRelicLands = Context.Game == BethesdaGame.Oblivion;
        foreach (var land in Context.ScanResult.LandRecords)
        {
            if (skipRelicLands && !land.Header.IsCompressed &&
                land.BtxtCount == 0 && land.AtxtCount == 0 && land.VtxtCount == 0)
            {
                continue;
            }

            StampTerrainSourceParent(land);
            if (land.ParentCellFormId is uint parentCellFormId)
            {
                if (land.Heightmap != null)
                {
                    heightmapByCellFormId.TryAdd(parentCellFormId, land.Heightmap);
                }

                if (land.ParsedHeightmap != null)
                {
                    capturedHeightmapByCellFormId.TryAdd(parentCellFormId, land.ParsedHeightmap);
                }

                if (land.VisualData != null)
                {
                    visualDataByCellFormId.TryAdd(parentCellFormId, land.VisualData);
                }

                if (land.RuntimeTerrainMesh != null)
                {
                    terrainMeshByCellFormId.TryAdd(parentCellFormId, land.RuntimeTerrainMesh);
                }
            }

            if (land.BestCellX.HasValue && land.BestCellY.HasValue)
            {
                var ws = land.WorldspaceFormId ??
                         (landWorldMap.TryGetValue(land.Header.FormId, out var mappedWorldspace)
                             ? mappedWorldspace
                             : 0u);
                var gridKey = (ws, land.BestCellX.Value, land.BestCellY.Value);
                if (land.Heightmap != null)
                {
                    heightmapByGrid.TryAdd(gridKey, land.Heightmap);
                }

                if (land.ParsedHeightmap != null)
                {
                    capturedHeightmapByGrid.TryAdd(gridKey, land.ParsedHeightmap);
                }

                if (land.VisualData != null)
                {
                    visualDataByGrid.TryAdd(gridKey, land.VisualData);
                }

                if (land.RuntimeTerrainMesh != null)
                {
                    terrainMeshByGrid.TryAdd(gridKey, land.RuntimeTerrainMesh);
                }
            }
        }

        if (Context.Accessor == null)
        {
            foreach (var record in cellRecords)
            {
                cellWorldMap.TryGetValue(record.FormId, out var cellWs);
                var cell = ParseCellFromScanResult(record, refrByFormId,
                    hasGrupMapping ? cellToRefrMap : null, refrOffsetIndex, refrSortedByOffset,
                    runtimeCellMapEntries.GetValueOrDefault(record.FormId), cellWs);
                if (cell != null)
                {
                    if (cellWs > 0)
                    {
                        cell = cell with
                        {
                            WorldspaceFormId = cellWs,
                            WorldspaceAssignmentSource = "CellGrup"
                        };
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
                        heightmapByCellFormId, capturedHeightmapByCellFormId,
                        visualDataByCellFormId, terrainMeshByCellFormId,
                        heightmapByGrid, capturedHeightmapByGrid,
                        visualDataByGrid, terrainMeshByGrid, cellWs, buffer,
                        runtimeCellMapEntries.GetValueOrDefault(record.FormId));
                    if (cell != null)
                    {
                        if (cellWs > 0)
                        {
                            cell = cell with
                            {
                                WorldspaceFormId = cellWs,
                                WorldspaceAssignmentSource = "CellGrup"
                            };
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

        MergeRuntimeCells(
            cells,
            heightmapByCellFormId,
            capturedHeightmapByCellFormId,
            visualDataByCellFormId,
            terrainMeshByCellFormId,
            heightmapByGrid,
            capturedHeightmapByGrid,
            visualDataByGrid,
            terrainMeshByGrid,
            runtimeCellMapEntries,
            refrByFormId);

        // Post-processing: resolve door destinations to linked cells
        ResolveDoorLinks(cells);

        return cells;
    }

    /// <summary>
    ///     Builds REFR->Cell reverse lookup and populates LinkedCellFormIds on each cell.
    /// </summary>
    internal static void ResolveDoorLinks(List<CellRecord> cells)
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
            var updatedObjects = new List<PlacedReference>(cells[i].PlacedObjects.Count);
            var anyChanged = false;

#pragma warning disable S3267 // Loop body has conditional + TryGetValue that makes LINQ impractical
            foreach (var obj in cells[i].PlacedObjects)
#pragma warning restore S3267
            {
                uint? resolvedDestinationCellFormId = null;
                if (obj.DestinationDoorFormId is > 0 &&
                    refrToCell.TryGetValue(obj.DestinationDoorFormId.Value, out var destCellFormId) &&
                    destCellFormId != cells[i].FormId) // Exclude self-links
                {
                    resolvedDestinationCellFormId = destCellFormId;
                    linkedCells.Add(destCellFormId);
                }

                if (obj.DestinationCellFormId != resolvedDestinationCellFormId)
                {
                    updatedObjects.Add(obj with { DestinationCellFormId = resolvedDestinationCellFormId });
                    anyChanged = true;
                }
                else
                {
                    updatedObjects.Add(obj);
                }
            }

            var linkedCellList = linkedCells.Order().ToList();
            var linkedCellsChanged = !cells[i].LinkedCellFormIds.SequenceEqual(linkedCellList);

            if (anyChanged || linkedCellsChanged)
            {
                cells[i] = cells[i] with
                {
                    PlacedObjects = anyChanged ? updatedObjects : cells[i].PlacedObjects,
                    LinkedCellFormIds = linkedCellList
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
        Dictionary<uint, LandHeightmap> heightmapByCellFormId,
        Dictionary<uint, LandHeightmap> capturedHeightmapByCellFormId,
        Dictionary<uint, LandVisualData> visualDataByCellFormId,
        Dictionary<uint, RuntimeTerrainMesh> terrainMeshByCellFormId,
        Dictionary<(uint, int, int), LandHeightmap> heightmapByGrid,
        Dictionary<(uint, int, int), LandHeightmap> capturedHeightmapByGrid,
        Dictionary<(uint, int, int), LandVisualData> visualDataByGrid,
        Dictionary<(uint, int, int), RuntimeTerrainMesh> terrainMeshByGrid,
        uint cellWorldspace,
        byte[] buffer,
        RuntimeCellMapEntry? runtimeCellMapEntry)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseCellFromScanResult(record, refrByFormId, cellToRefrMap,
                refrOffsetIndex, refrSortedByOffset, runtimeCellMapEntry, cellWorldspace);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        int? gridX = null;
        int? gridY = null;
        byte flags = 0;
        float? waterHeight = null;
        uint? waterFormId = null;
        uint? encounterZoneFormId = null;
        uint? musicTypeFormId = null;
        uint? acousticSpaceFormId = null;
        uint? imageSpaceFormId = null;
        uint? climateFormId = null;
        uint? lightingTemplateFormId = null;
        uint? lightingTemplateInheritanceFlags = null;
        Dictionary<string, object?>? lightingData = null;
        List<uint>? radiationRegionFormIds = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = Context.ReadFullName(subData);
                    break;
                case "DATA" when sub.DataLength >= 1:
                    flags = subData[0];
                    break;
                case "XCLC" when sub.DataLength >= 8:
                {
                    // XCLC is 8 bytes in Fallout 3 (X, Y int32) and 12 bytes in Fallout NV (X, Y,
                    // forceHideLand). Read the two grid int32s directly so the FNV-only trailing field
                    // is optional — gating on >= 12 (and the size-12-keyed schema) silently dropped the
                    // grid on every FO3 exterior cell, filtering its terrain out of the world viewer.
                    gridX = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    gridY = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    break;
                }
                case "XCLW" when sub.DataLength >= 4:
                    var rawWaterHeight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    waterHeight = WorldHeightNormalizer.PreserveSentinelOrNormalize(rawWaterHeight);
                    break;
                case "XCWT" when sub.DataLength == 4:
                    waterFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
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
                case "XCCM" when sub.DataLength == 4
                                      && Context.Game is BethesdaGame.Oblivion
                                          or BethesdaGame.Fallout3
                                          or BethesdaGame.FalloutNewVegas:
                    // XCCM is a CLMT FormID only in the classic families. Skyrim/FO4/FO76 reuse
                    // this signature for a REGN sky/weather source and must not populate this field.
                    climateFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "LTMP" when sub.DataLength == 4:
                    lightingTemplateFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "LNAM" when sub.DataLength == 4:
                    lightingTemplateInheritanceFlags = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    break;
                // 40 = FO3/FNV+, 36 = TES4 (same layout without the trailing FogPow float). The gate
                // was 40-exact, which silently discarded EVERY Oblivion cell's authored lighting
                // (all 1,770 XCLLs in Oblivion.esm are 36 bytes) and left interiors on engine
                // fallback defaults. SubrecordSchemaView resolves the right layout by length.
                case "XCLL" when sub.DataLength is 40 or 36:
                {
                    if (SubrecordSchemaView.TryRead("XCLL", "CELL", subData, record.IsBigEndian) is { } v)
                    {
                        lightingData = v.Raw;
                    }

                    break;
                }
                case "XCLR" when sub.DataLength >= 4 && sub.DataLength % 4 == 0:
                    // XCLR is a candidate-region FormID array: every 4 bytes is one REGN
                    // reference. Region polygons can cover only part of this CELL, so consumers
                    // must still evaluate RPLI/RPLD geometry at their world position.
                    radiationRegionFormIds ??= new List<uint>(sub.DataLength / 4);
                    for (var i = 0; i < sub.DataLength; i += 4)
                    {
                        radiationRegionFormIds.Add(
                            RecordParserContext.ReadFormId(subData.Slice(i, 4), record.IsBigEndian));
                    }

                    break;
            }
        }

        var isPersistentCell = ApplyStructuralCellClassification(
            record.FormId, cellWorldspace, ref gridX, ref gridY, flags);

        var cellRefs = ResolveCellRefs(record, refrByFormId, cellToRefrMap,
            refrOffsetIndex, refrSortedByOffset, runtimeCellMapEntry);

        // Find associated heightmap and terrain mesh via O(1) dictionary lookups.
        // Exact parent CELL FormID wins; grid lookups are only a fallback for runtime-only captures.
        // Grid keys include worldspace to prevent cross-worldspace pollution.
        // Falls back to worldspace 0 for DMP mode (no GRUP hierarchy).
        heightmapByCellFormId.TryGetValue(record.FormId, out var heightmap);
        capturedHeightmapByCellFormId.TryGetValue(record.FormId, out var capturedHeightmap);
        visualDataByCellFormId.TryGetValue(record.FormId, out var visualData);
        terrainMeshByCellFormId.TryGetValue(record.FormId, out var terrainMesh);
        if (gridX.HasValue && gridY.HasValue)
        {
            var gridKey = (cellWorldspace, gridX.Value, gridY.Value);
            if (cellWorldspace != 0 && heightmap == null)
            {
                heightmapByGrid.TryGetValue(gridKey, out heightmap);
            }

            if (cellWorldspace != 0 && capturedHeightmap == null)
            {
                capturedHeightmapByGrid.TryGetValue(gridKey, out capturedHeightmap);
            }

            if (cellWorldspace != 0 && visualData == null)
            {
                visualDataByGrid.TryGetValue(gridKey, out visualData);
            }

            if (cellWorldspace != 0 && terrainMesh == null)
            {
                terrainMeshByGrid.TryGetValue(gridKey, out terrainMesh);
            }
        }

        return new CellRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            GridX = gridX,
            GridY = gridY,
            WorldspaceFormId = cellWorldspace > 0 ? cellWorldspace : null,
            WorldspaceAssignmentSource = cellWorldspace > 0 ? "CellGrup" : null,
            Flags = flags,
            WaterHeight = waterHeight,
            WaterFormId = waterFormId,
            EncounterZoneFormId = encounterZoneFormId,
            MusicTypeFormId = musicTypeFormId,
            AcousticSpaceFormId = acousticSpaceFormId,
            ImageSpaceFormId = imageSpaceFormId,
            ClimateFormId = climateFormId,
            LightingTemplateFormId = lightingTemplateFormId,
            LightingTemplateInheritanceFlags = lightingTemplateInheritanceFlags,
            LightingData = lightingData,
            RadiationRegionFormIds = radiationRegionFormIds ?? (IReadOnlyList<uint>)[],
            PlacedObjects = cellRefs,
            HasPersistentObjects = isPersistentCell || cellRefs.Exists(r => r.IsPersistent),
            IsPersistentCell = isPersistentCell,
            Heightmap = heightmap,
            CapturedLandHeightmap = capturedHeightmap,
            LandVisualData = visualData,
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
        RuntimeCellMapEntry? runtimeCellMapEntry,
        uint cellWorldspace = 0)
    {
        // Find XCLC near this CELL record
        var cellGrid = Context.ScanResult.CellGrids
            .FirstOrDefault(g => Math.Abs(g.Offset - record.Offset) < 200);

        var cellRefs = ResolveCellRefs(record, refrByFormId, cellToRefrMap,
            refrOffsetIndex, refrSortedByOffset, runtimeCellMapEntry);

        int? gridX = cellGrid?.GridX;
        int? gridY = cellGrid?.GridY;
        var isPersistentCell = ApplyStructuralCellClassification(
            record.FormId, cellWorldspace, ref gridX, ref gridY, flags: 0);

        return new CellRecord
        {
            FormId = record.FormId,
            EditorId = Context.GetEditorId(record.FormId),
            GridX = gridX,
            GridY = gridY,
            WorldspaceFormId = cellWorldspace > 0 ? cellWorldspace : null,
            WorldspaceAssignmentSource = cellWorldspace > 0 ? "CellGrup" : null,
            PlacedObjects = cellRefs,
            HasPersistentObjects = isPersistentCell || cellRefs.Exists(r => r.IsPersistent),
            IsPersistentCell = isPersistentCell,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Classifies a worldspace cell as the persistent dummy container vs a real exterior cell and
    ///     normalizes its grid accordingly. The GRUP-structural signal
    ///     (<see cref="EsmRecordScanResult.PersistentCellContainerFormIds" />) is authoritative when
    ///     present: a TES4 dummy can carry XCLC (0,0) — keeping it would collide with the real (0,0)
    ///     cell in the spatial index (SETheFringe) — and a TES4 real exterior cell can omit XCLC,
    ///     which the engine zero-defaults to grid (0,0) (Toddland's only cell). Structure-less inputs
    ///     (memory dumps) fall back to the grid-presence heuristic unchanged.
    /// </summary>
    private bool ApplyStructuralCellClassification(
        uint formId, uint cellWorldspace, ref int? gridX, ref int? gridY, byte flags)
    {
        var containers = Context.ScanResult.PersistentCellContainerFormIds;
        if (containers.Count > 0 && cellWorldspace > 0)
        {
            if (containers.Contains(formId))
            {
                // The engine ignores the dummy's grid; its refs route through the persistent shunt.
                gridX = null;
                gridY = null;
                return true;
            }

            if (gridX is null || gridY is null)
            {
                // Real exterior cell (inside a Type-4/5 block) with no XCLC: the engine's grid
                // field is zero-initialized, so the cell loads at (0,0).
                gridX = 0;
                gridY = 0;
            }

            return false;
        }

        return IsPersistentCellContainer(flags, gridX, gridY, cellWorldspace);
    }

    private static bool IsPersistentCellContainer(byte flags, int? gridX, int? gridY, uint cellWorldspace)
    {
        var isInterior = (flags & 0x01) != 0;
        return cellWorldspace > 0 && !isInterior && (!gridX.HasValue || !gridY.HasValue);
    }

    private void MergeRuntimeCells(
        List<CellRecord> cells,
        Dictionary<uint, LandHeightmap> heightmapByCellFormId,
        Dictionary<uint, LandHeightmap> capturedHeightmapByCellFormId,
        Dictionary<uint, LandVisualData> visualDataByCellFormId,
        Dictionary<uint, RuntimeTerrainMesh> terrainMeshByCellFormId,
        Dictionary<(uint, int, int), LandHeightmap> heightmapByGrid,
        Dictionary<(uint, int, int), LandHeightmap> capturedHeightmapByGrid,
        Dictionary<(uint, int, int), LandVisualData> visualDataByGrid,
        Dictionary<(uint, int, int), RuntimeTerrainMesh> terrainMeshByGrid,
        Dictionary<uint, RuntimeCellMapEntry> runtimeCellMapEntries,
        Dictionary<uint, ExtractedRefrRecord> refrByFormId)
    {
        if (Context.RuntimeReader == null)
        {
            return;
        }

        Context.MergeRuntimeOverlayRecords(
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
                var displayName = Context.FormIdToFullName.GetValueOrDefault(cellFormId);
                var mapRuntimeCell = BuildRuntimeCellRecord(
                    Context.RuntimeReader,
                    mapEntry,
                    refrByFormId,
                    Context.GetEditorId(cellFormId),
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
            cells[i] = AttachTerrainData(
                cells[i],
                heightmapByCellFormId,
                capturedHeightmapByCellFormId,
                visualDataByCellFormId,
                terrainMeshByCellFormId,
                heightmapByGrid,
                capturedHeightmapByGrid,
                visualDataByGrid,
                terrainMeshByGrid);
        }
    }

    internal static void AttachTerrainDataFromLandRecords(
        List<CellRecord> cells,
        EsmRecordScanResult scanResult)
    {
        if (cells.Count == 0 || scanResult.LandRecords.Count == 0)
        {
            return;
        }

        var heightmapByCellFormId = new Dictionary<uint, LandHeightmap>();
        var capturedHeightmapByCellFormId = new Dictionary<uint, LandHeightmap>();
        var visualDataByCellFormId = new Dictionary<uint, LandVisualData>();
        var terrainMeshByCellFormId = new Dictionary<uint, RuntimeTerrainMesh>();
        var heightmapByGrid = new Dictionary<(uint, int, int), LandHeightmap>();
        var capturedHeightmapByGrid = new Dictionary<(uint, int, int), LandHeightmap>();
        var visualDataByGrid = new Dictionary<(uint, int, int), LandVisualData>();
        var terrainMeshByGrid = new Dictionary<(uint, int, int), RuntimeTerrainMesh>();

        // Same pre-release-relic gate as the instance attach loop in ParseCells (uncompressed +
        // layer-less LANDs the engine ignores; the parent-terrain inheritance pass fills the slot
        // instead — see the full rationale there, incl. why the DROP needs an affirmative game).
        var skipRelicLands = scanResult.Game == BethesdaGame.Oblivion;
        foreach (var land in scanResult.LandRecords)
        {
            if (skipRelicLands && !land.Header.IsCompressed &&
                land.BtxtCount == 0 && land.AtxtCount == 0 && land.VtxtCount == 0)
            {
                continue;
            }

            StampTerrainSourceParent(land);
            if (land.ParentCellFormId is uint parentCellFormId)
            {
                if (land.Heightmap != null)
                {
                    heightmapByCellFormId.TryAdd(parentCellFormId, land.Heightmap);
                }

                if (land.ParsedHeightmap != null)
                {
                    capturedHeightmapByCellFormId.TryAdd(parentCellFormId, land.ParsedHeightmap);
                }

                if (land.VisualData != null)
                {
                    visualDataByCellFormId.TryAdd(parentCellFormId, land.VisualData);
                }

                if (land.RuntimeTerrainMesh != null)
                {
                    terrainMeshByCellFormId.TryAdd(parentCellFormId, land.RuntimeTerrainMesh);
                }
            }

            if (land.WorldspaceFormId is not uint worldspaceFormId ||
                !land.BestCellX.HasValue ||
                !land.BestCellY.HasValue)
            {
                continue;
            }

            var key = (worldspaceFormId, land.BestCellX.Value, land.BestCellY.Value);
            if (land.Heightmap != null)
            {
                heightmapByGrid.TryAdd(key, land.Heightmap);
            }

            if (land.ParsedHeightmap != null)
            {
                capturedHeightmapByGrid.TryAdd(key, land.ParsedHeightmap);
            }

            if (land.VisualData != null)
            {
                visualDataByGrid.TryAdd(key, land.VisualData);
            }

            if (land.RuntimeTerrainMesh != null)
            {
                terrainMeshByGrid.TryAdd(key, land.RuntimeTerrainMesh);
            }
        }

        for (var i = 0; i < cells.Count; i++)
        {
            cells[i] = AttachTerrainData(
                cells[i],
                heightmapByCellFormId,
                capturedHeightmapByCellFormId,
                visualDataByCellFormId,
                terrainMeshByCellFormId,
                heightmapByGrid,
                capturedHeightmapByGrid,
                visualDataByGrid,
                terrainMeshByGrid);
        }
    }

    private Dictionary<uint, RuntimeCellMapEntry> BuildRuntimeCellMapIndex()
    {
        var entries = new Dictionary<uint, RuntimeCellMapEntry>();
        var owningWorldspaceByCell = new Dictionary<uint, uint>();
        if (Context.RuntimeWorldspaceCellMaps is not { Count: > 0 })
        {
            return entries;
        }

        foreach (var (_, worldData) in Context.RuntimeWorldspaceCellMaps)
        {
            foreach (var entry in worldData.Cells)
            {
                AddOrReplaceRuntimeCellMapEntry(entries, owningWorldspaceByCell, entry, worldData.FormId);
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
                owningWorldspaceByCell[worldData.PersistentCellFormId.Value] = worldData.FormId;
            }
        }

        return entries;
    }

    private static void AddOrReplaceRuntimeCellMapEntry(
        Dictionary<uint, RuntimeCellMapEntry> entries,
        Dictionary<uint, uint> owningWorldspaceByCell,
        RuntimeCellMapEntry entry,
        uint owningWorldspaceFormId)
    {
        if (!entries.TryGetValue(entry.CellFormId, out var existing))
        {
            entries[entry.CellFormId] = entry;
            owningWorldspaceByCell[entry.CellFormId] = owningWorldspaceFormId;
            return;
        }

        owningWorldspaceByCell.TryGetValue(entry.CellFormId, out var existingOwningWorldspace);
        var newIsSelfConsistent = entry.WorldspaceFormId == owningWorldspaceFormId;
        var existingIsSelfConsistent = existing.WorldspaceFormId == existingOwningWorldspace;
        if (newIsSelfConsistent && !existingIsSelfConsistent)
        {
            entries[entry.CellFormId] = entry;
            owningWorldspaceByCell[entry.CellFormId] = owningWorldspaceFormId;
            return;
        }

        if (entry.WorldspaceFormId.HasValue && !existing.WorldspaceFormId.HasValue)
        {
            entries[entry.CellFormId] = entry;
            owningWorldspaceByCell[entry.CellFormId] = owningWorldspaceFormId;
        }
    }

    private static List<PlacedReference> ResolvePlacedObjects(CellRecord esm, CellRecord runtime)
    {
        if (ShouldUseRuntimePlacedObjects(esm, runtime))
        {
            return runtime.PlacedObjects;
        }

        return esm.PlacedObjects.Count > 0 ? esm.PlacedObjects : runtime.PlacedObjects;
    }

    private static CellRecord MergeCell(CellRecord esm, CellRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            FullName = esm.FullName ?? runtime.FullName,
            GridX = esm.GridX ?? runtime.GridX,
            GridY = esm.GridY ?? runtime.GridY,
            WorldspaceFormId = runtime.WorldspaceFormId ?? esm.WorldspaceFormId,
            WorldspaceAssignmentSource = runtime.WorldspaceFormId.HasValue
                ? runtime.WorldspaceAssignmentSource ?? "Runtime"
                : esm.WorldspaceAssignmentSource,
            CandidateWorldspaceFormIds = runtime.CandidateWorldspaceFormIds.Count > 0
                ? runtime.CandidateWorldspaceFormIds
                : esm.CandidateWorldspaceFormIds,
            Flags = esm.Flags != 0 ? esm.Flags : runtime.Flags,
            WaterHeight = WorldHeightNormalizer.PreserveSentinelOrNormalize(esm.WaterHeight ?? runtime.WaterHeight),
            WaterFormId = esm.WaterFormId ?? runtime.WaterFormId,
            EncounterZoneFormId = esm.EncounterZoneFormId ?? runtime.EncounterZoneFormId,
            MusicTypeFormId = esm.MusicTypeFormId ?? runtime.MusicTypeFormId,
            AcousticSpaceFormId = esm.AcousticSpaceFormId ?? runtime.AcousticSpaceFormId,
            ImageSpaceFormId = esm.ImageSpaceFormId ?? runtime.ImageSpaceFormId,
            ClimateFormId = esm.ClimateFormId ?? runtime.ClimateFormId,
            LightingTemplateFormId = esm.LightingTemplateFormId ?? runtime.LightingTemplateFormId,
            LightingTemplateInheritanceFlags =
            esm.LightingTemplateInheritanceFlags ?? runtime.LightingTemplateInheritanceFlags,
            LightingData = esm.LightingData ?? runtime.LightingData,
            PlacedObjects = ResolvePlacedObjects(esm, runtime),
            LinkedCellFormIds = esm.LinkedCellFormIds.Count > 0 ? esm.LinkedCellFormIds : runtime.LinkedCellFormIds,
            Heightmap = esm.Heightmap ?? runtime.Heightmap,
            CapturedLandHeightmap = esm.CapturedLandHeightmap ?? runtime.CapturedLandHeightmap,
            LandVisualData = esm.LandVisualData ?? runtime.LandVisualData,
            RuntimeTerrainMesh = esm.RuntimeTerrainMesh ?? runtime.RuntimeTerrainMesh,
            HasPersistentObjects = esm.HasPersistentObjects || runtime.HasPersistentObjects,
            IsPersistentCell = esm.IsPersistentCell || runtime.IsPersistentCell,
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

        CellRecord? mergedCell;
        if (runtimeCell != null)
        {
            mergedCell = mapCell != null ? MergeCell(runtimeCell, mapCell) : runtimeCell;
        }
        else
        {
            mergedCell = mapCell;
        }

        if (mergedCell == null)
        {
            return null;
        }

        mergedCell = mergedCell with
        {
            WorldspaceAssignmentSource = mapEntry.WorldspaceFormId.HasValue
                ? "RuntimeCellMap"
                : mergedCell.WorldspaceAssignmentSource
        };

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
            .Select(r => CellLinkageHandler.ToPlacedReference(r, Context, assignmentSource))
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

    private static void StampTerrainSourceParent(ExtractedLandRecord land)
    {
        if (land.ParentCellFormId is not uint parentCellFormId)
        {
            return;
        }

        if (land.Heightmap is { } heightmap)
        {
            heightmap.SourceParentCellFormId = parentCellFormId;
        }

        if (land.ParsedHeightmap is { } capturedHeightmap)
        {
            capturedHeightmap.SourceParentCellFormId = parentCellFormId;
        }

        if (land.VisualData is { } visualData)
        {
            visualData.SourceParentCellFormId = parentCellFormId;
        }

        if (land.RuntimeTerrainMesh is { } runtimeTerrainMesh)
        {
            runtimeTerrainMesh.SourceParentCellFormId = parentCellFormId;
        }
    }

    private static CellRecord AttachTerrainData(
        CellRecord cell,
        Dictionary<uint, LandHeightmap> heightmapByCellFormId,
        Dictionary<uint, LandHeightmap> capturedHeightmapByCellFormId,
        Dictionary<uint, LandVisualData> visualDataByCellFormId,
        Dictionary<uint, RuntimeTerrainMesh> terrainMeshByCellFormId,
        Dictionary<(uint, int, int), LandHeightmap> heightmapByGrid,
        Dictionary<(uint, int, int), LandHeightmap> capturedHeightmapByGrid,
        Dictionary<(uint, int, int), LandVisualData> visualDataByGrid,
        Dictionary<(uint, int, int), RuntimeTerrainMesh> terrainMeshByGrid)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            return cell;
        }

        var heightmap = cell.Heightmap;
        var capturedHeightmap = cell.CapturedLandHeightmap;
        var visualData = cell.LandVisualData;
        var terrainMesh = cell.RuntimeTerrainMesh;

        if (heightmap == null)
        {
            heightmapByCellFormId.TryGetValue(cell.FormId, out heightmap);
        }

        if (capturedHeightmap == null)
        {
            capturedHeightmapByCellFormId.TryGetValue(cell.FormId, out capturedHeightmap);
        }

        if (visualData == null)
        {
            visualDataByCellFormId.TryGetValue(cell.FormId, out visualData);
        }

        if (terrainMesh == null)
        {
            terrainMeshByCellFormId.TryGetValue(cell.FormId, out terrainMesh);
        }

        var worldspaceFormId = cell.WorldspaceFormId ?? 0;
        var key = (worldspaceFormId, cell.GridX.Value, cell.GridY.Value);
        if (worldspaceFormId != 0 && heightmap == null)
        {
            heightmapByGrid.TryGetValue(key, out heightmap);
        }

        if (worldspaceFormId != 0 && capturedHeightmap == null)
        {
            capturedHeightmapByGrid.TryGetValue(key, out capturedHeightmap);
        }

        if (worldspaceFormId != 0 && visualData == null)
        {
            visualDataByGrid.TryGetValue(key, out visualData);
        }

        if (worldspaceFormId != 0 && terrainMesh == null)
        {
            terrainMeshByGrid.TryGetValue(key, out terrainMesh);
        }

        if (heightmap == null && capturedHeightmap == null && visualData == null && terrainMesh == null)
        {
            return cell;
        }

        return cell with
        {
            Heightmap = heightmap ?? cell.Heightmap,
            CapturedLandHeightmap = capturedHeightmap ?? cell.CapturedLandHeightmap,
            LandVisualData = visualData ?? cell.LandVisualData,
            RuntimeTerrainMesh = terrainMesh ?? cell.RuntimeTerrainMesh
        };
    }

    #endregion
}
