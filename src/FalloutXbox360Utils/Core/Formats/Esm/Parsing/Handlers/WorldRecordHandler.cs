using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class WorldRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    private readonly CellRecordHandler _cellHandler = new(context);

    #region Map Markers

    /// <summary>
    ///     Extract map markers from REFR records that have the XMRK subrecord.
    /// </summary>
    internal List<PlacedReference> ExtractMapMarkers()
    {
        var markers = new List<PlacedReference>();

        // Map markers come from REFR records with XMRK subrecord
        foreach (var refr in Context.ScanResult.RefrRecords)
        {
            if (!refr.IsMapMarker)
            {
                continue;
            }

            var marker = new PlacedReference
            {
                FormId = refr.Header.FormId,
                BaseFormId = refr.BaseFormId,
                BaseEditorId = refr.BaseEditorId ?? Context.GetEditorId(refr.BaseFormId),
                RecordType = refr.Header.RecordType,
                X = refr.Position?.X ?? 0,
                Y = refr.Position?.Y ?? 0,
                Z = refr.Position?.Z ?? 0,
                RotX = refr.Position?.RotX ?? 0,
                RotY = refr.Position?.RotY ?? 0,
                RotZ = refr.Position?.RotZ ?? 0,
                Scale = refr.Scale,
                Radius = refr.Radius,
                OwnerFormId = refr.OwnerFormId,
                EncounterZoneFormId = refr.EncounterZoneFormId,
                LockLevel = refr.LockLevel,
                LockKeyFormId = refr.LockKeyFormId,
                LockFlags = refr.LockFlags,
                LockNumTries = refr.LockNumTries,
                LockTimesUnlocked = refr.LockTimesUnlocked,
                EnableParentFormId = refr.EnableParentFormId,
                EnableParentFlags = refr.EnableParentFlags,
                PersistentCellFormId = refr.PersistentCellFormId,
                StartingPosition = refr.StartingPosition,
                StartingWorldOrCellFormId = refr.StartingWorldOrCellFormId,
                PackageStartLocation = refr.PackageStartLocation,
                MerchantContainerFormId = refr.MerchantContainerFormId,
                LeveledCreatureOriginalBaseFormId = refr.LeveledCreatureOriginalBaseFormId,
                LeveledCreatureTemplateFormId = refr.LeveledCreatureTemplateFormId,
                IsPersistent = refr.Header.IsPersistent,
                IsInitiallyDisabled = refr.Header.IsInitiallyDisabled,
                IsMapMarker = true,
                MarkerType = refr.MarkerType.HasValue ? (MapMarkerType)refr.MarkerType.Value : null,
                MarkerName = refr.MarkerName,
                LinkedRefKeywordFormId = refr.LinkedRefKeywordFormId,
                LinkedRefFormId = refr.LinkedRefFormId,
                LinkedRefChildrenFormIds = refr.LinkedRefChildrenFormIds,
                Offset = refr.Header.Offset,
                IsBigEndian = refr.Header.IsBigEndian
            };

            markers.Add(marker);
        }

        return markers;
    }

    #endregion

    #region Enrichment

    /// <summary>
    ///     Enrich placed references in cells with base object bounds and model paths.
    ///     Joins PlacedReference.BaseFormId to pre-built indexes from parsed base objects.
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

    #region Cells

    /// <summary>
    ///     Parse all Cell records from the scan result.
    ///     Delegates to <see cref="CellRecordHandler" />.
    /// </summary>
    internal List<CellRecord> ParseCells()
    {
        return _cellHandler.ParseCells();
    }

    /// <summary>
    ///     DMP fallback: infer worldspace membership for exterior cells.
    ///     Delegates to <see cref="CellLinkageHandler" />.
    /// </summary>
    internal static void InferCellWorldspaces(List<CellRecord> cells, List<WorldspaceRecord> worldspaces)
    {
        CellLinkageHandler.InferCellWorldspaces(cells, worldspaces);
    }

    /// <summary>
    ///     Links parsed cells to their parent worldspace's Cells list.
    ///     Delegates to <see cref="CellLinkageHandler" />.
    /// </summary>
    internal static void LinkCellsToWorldspaces(List<CellRecord> cells, List<WorldspaceRecord> worldspaces)
    {
        CellLinkageHandler.LinkCellsToWorldspaces(cells, worldspaces);
    }

    /// <summary>
    ///     DMP fallback: create partial worldspace stubs for cells that already know their parent WRLD
    ///     FormID even when no WRLD record or runtime cell-map-backed worldspace was parsed.
    /// </summary>
    internal static void EnsureWorldspacesForCells(
        List<CellRecord> cells,
        List<WorldspaceRecord> worldspaces,
        RecordParserContext context)
    {
        if (cells.Count == 0)
        {
            return;
        }

        var worldspaceIndexByFormId = new Dictionary<uint, int>(worldspaces.Count);
        for (var i = 0; i < worldspaces.Count; i++)
        {
            worldspaceIndexByFormId.TryAdd(worldspaces[i].FormId, i);
        }

        Dictionary<uint, RuntimeEditorIdEntry>? runtimeWorldEntries = null;
        if (context.RuntimeReader != null)
        {
            runtimeWorldEntries = new Dictionary<uint, RuntimeEditorIdEntry>();
            foreach (var entry in context.ScanResult.RuntimeEditorIds)
            {
                if (entry.FormType == 0x41 && entry.FormId != 0)
                {
                    runtimeWorldEntries.TryAdd(entry.FormId, entry);
                }
            }
        }

        var added = 0;
        var enriched = 0;
        foreach (var group in cells
                     .Where(cell => cell.WorldspaceFormId is > 0)
                     .GroupBy(cell => cell.WorldspaceFormId!.Value))
        {
            var relatedCells = group.ToList();
            var cellBackedWorldspace = BuildCellBackedWorldspaceStub(group.Key, relatedCells, context);
            var runtimeCellMapWorldspace = context.RuntimeWorldspaceCellMaps is { Count: > 0 } &&
                                           context.RuntimeWorldspaceCellMaps.TryGetValue(group.Key,
                                               out var runtimeWorldData)
                ? BuildRuntimeCellMapWorldspace(runtimeWorldData, context)
                : null;
            if (worldspaceIndexByFormId.TryGetValue(group.Key, out var existingIndex))
            {
                if (runtimeCellMapWorldspace != null)
                {
                    var merged = MergeWorldspace(worldspaces[existingIndex], runtimeCellMapWorldspace);
                    if (!Equals(merged, worldspaces[existingIndex]))
                    {
                        worldspaces[existingIndex] = merged;
                        enriched++;
                    }
                }

                if (cellBackedWorldspace != null)
                {
                    var merged = MergeWorldspace(worldspaces[existingIndex], cellBackedWorldspace);
                    if (!Equals(merged, worldspaces[existingIndex]))
                    {
                        worldspaces[existingIndex] = merged;
                        enriched++;
                    }
                }

                continue;
            }

            WorldspaceRecord? worldspace = null;
            if (context.RuntimeReader != null &&
                runtimeWorldEntries != null &&
                runtimeWorldEntries.TryGetValue(group.Key, out var runtimeWorldEntry))
            {
                worldspace = context.RuntimeReader.ReadRuntimeWorldspace(runtimeWorldEntry);
            }

            if (runtimeCellMapWorldspace != null)
            {
                worldspace = worldspace != null
                    ? MergeWorldspace(worldspace, runtimeCellMapWorldspace)
                    : runtimeCellMapWorldspace;
            }

            worldspace = cellBackedWorldspace != null
                ? worldspace != null ? MergeWorldspace(worldspace, cellBackedWorldspace) : cellBackedWorldspace
                : worldspace;
            if (worldspace == null)
            {
                continue;
            }

            worldspaces.Add(worldspace);
            worldspaceIndexByFormId[group.Key] = worldspaces.Count - 1;
            added++;
        }

        if (added > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Added {added} partial worldspaces from cell-backed worldspace signals");
        }

        if (enriched > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enriched} existing worldspaces from linked cell coverage");
        }
    }

    /// <summary>
    ///     DMP fallback: create virtual cells for orphan placed references.
    ///     Delegates to <see cref="CellLinkageHandler" />.
    /// </summary>
    internal static List<CellRecord> CreateVirtualCells(
        List<CellRecord> existingCells,
        IReadOnlyList<ExtractedRefrRecord> allRefrs,
        RecordParserContext context)
    {
        return CellLinkageHandler.CreateVirtualCells(existingCells, allRefrs, context);
    }

    #endregion

    #region Worldspaces

    /// <summary>
    ///     Parse all Worldspace records from the scan result.
    /// </summary>
    internal List<WorldspaceRecord> ParseWorldspaces()
    {
        var worldspaces = new List<WorldspaceRecord>();
        var wrldRecords = Context.GetRecordsByType("WRLD").ToList();

        if (Context.Accessor == null)
        {
            foreach (var record in wrldRecords)
            {
                var worldspace = ParseWorldspaceFromScanResult(record);
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
                    var worldspace = ParseWorldspaceFromAccessor(record, buffer);
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

        Context.MergeRuntimeOverlayRecords(
            worldspaces,
            [0x41],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeWorldspace(entry),
            MergeWorldspace,
            "worldspaces");

        if (Context.RuntimeWorldspaceCellMaps is { Count: > 0 })
        {
            var indexedWorldspaces = new Dictionary<uint, int>(worldspaces.Count);
            for (var i = 0; i < worldspaces.Count; i++)
            {
                indexedWorldspaces.TryAdd(worldspaces[i].FormId, i);
            }

            var cellMapOnlyAdded = 0;
            foreach (var (worldspaceFormId, worldData) in Context.RuntimeWorldspaceCellMaps)
            {
                var runtimeFallback = BuildRuntimeCellMapWorldspace(worldData, Context);

                if (indexedWorldspaces.TryGetValue(worldspaceFormId, out var existingIndex))
                {
                    worldspaces[existingIndex] = MergeWorldspace(worldspaces[existingIndex], runtimeFallback);
                }
                else
                {
                    worldspaces.Add(runtimeFallback);
                    indexedWorldspaces[worldspaceFormId] = worldspaces.Count - 1;
                    cellMapOnlyAdded++;
                }
            }

            if (cellMapOnlyAdded > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {cellMapOnlyAdded} partial worldspaces from runtime cell maps");
            }
        }

        return worldspaces;
    }

    private WorldspaceRecord? ParseWorldspaceFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseWorldspaceFromScanResult(record);
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
        byte? flags = null;
        ushort? parentUseFlags = null;
        uint? imageSpace = null;
        uint? musicType = null;
        float? mapOffsetScaleX = null;
        float? mapOffsetScaleY = null;
        float? mapOffsetZ = null;

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
                case "DATA" when sub.DataLength >= 1:
                    flags = subData[0];
                    break;
                case "PNAM" when sub.DataLength >= 2:
                    parentUseFlags = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                    break;
                case "INAM" when sub.DataLength == 4:
                    imageSpace = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ZNAM" when sub.DataLength == 4:
                    musicType = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ONAM" when sub.DataLength >= 12:
                    mapOffsetScaleX = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    mapOffsetScaleY = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    mapOffsetZ = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    break;
            }
        }

        return new WorldspaceRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
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
            Flags = flags,
            ParentUseFlags = parentUseFlags,
            ImageSpaceFormId = imageSpace,
            MusicTypeFormId = musicType,
            MapOffsetScaleX = mapOffsetScaleX,
            MapOffsetScaleY = mapOffsetScaleY,
            MapOffsetZ = mapOffsetZ,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private WorldspaceRecord? ParseWorldspaceFromScanResult(DetectedMainRecord record)
    {
        return new WorldspaceRecord
        {
            FormId = record.FormId,
            EditorId = Context.GetEditorId(record.FormId),
            FullName = Context.FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static WorldspaceRecord? BuildCellBackedWorldspaceStub(
        uint worldspaceFormId,
        List<CellRecord> relatedCells,
        RecordParserContext context)
    {
        if (worldspaceFormId == 0)
        {
            return null;
        }

        short? mapNwCellX = null;
        short? mapNwCellY = null;
        short? mapSeCellX = null;
        short? mapSeCellY = null;
        float? boundsMinX = null;
        float? boundsMinY = null;
        float? boundsMaxX = null;
        float? boundsMaxY = null;
        var encounterZoneFormId = GetConsensusFormId(
            relatedCells.Select(cell => cell.EncounterZoneFormId));

        var griddedCells = relatedCells
            .Where(cell => cell.GridX.HasValue && cell.GridY.HasValue)
            .ToList();
        if (griddedCells.Count > 0)
        {
            var minX = griddedCells.Min(cell => cell.GridX!.Value);
            var maxX = griddedCells.Max(cell => cell.GridX!.Value);
            var minY = griddedCells.Min(cell => cell.GridY!.Value);
            var maxY = griddedCells.Max(cell => cell.GridY!.Value);

            mapNwCellX = checked((short)minX);
            mapNwCellY = checked((short)maxY);
            mapSeCellX = checked((short)maxX);
            mapSeCellY = checked((short)minY);
            boundsMinX = minX * 4096f;
            boundsMinY = minY * 4096f;
            boundsMaxX = (maxX + 1) * 4096f;
            boundsMaxY = (maxY + 1) * 4096f;
        }

        return new WorldspaceRecord
        {
            FormId = worldspaceFormId,
            EditorId = context.GetEditorId(worldspaceFormId),
            FullName = context.FormIdToFullName.GetValueOrDefault(worldspaceFormId),
            MapNWCellX = mapNwCellX,
            MapNWCellY = mapNwCellY,
            MapSECellX = mapSeCellX,
            MapSECellY = mapSeCellY,
            BoundsMinX = boundsMinX,
            BoundsMinY = boundsMinY,
            BoundsMaxX = boundsMaxX,
            BoundsMaxY = boundsMaxY,
            EncounterZoneFormId = encounterZoneFormId,
            IsBigEndian = true
        };
    }

    private static WorldspaceRecord BuildRuntimeCellMapWorldspace(
        RuntimeWorldspaceData worldData,
        RecordParserContext context)
    {
        return new WorldspaceRecord
        {
            FormId = worldData.FormId,
            EditorId = worldData.EditorId ?? context.GetEditorId(worldData.FormId),
            FullName = worldData.FullName ?? context.FormIdToFullName.GetValueOrDefault(worldData.FormId),
            ParentWorldspaceFormId = worldData.ParentWorldFormId,
            ClimateFormId = worldData.ClimateFormId,
            WaterFormId = worldData.WaterFormId,
            DefaultLandHeight = worldData.DefaultLandHeight,
            DefaultWaterHeight = worldData.DefaultWaterHeight,
            MapUsableWidth = worldData.MapUsableWidth,
            MapUsableHeight = worldData.MapUsableHeight,
            MapNWCellX = worldData.MapNWCellX,
            MapNWCellY = worldData.MapNWCellY,
            MapSECellX = worldData.MapSECellX,
            MapSECellY = worldData.MapSECellY,
            BoundsMinX = worldData.BoundsMinX,
            BoundsMinY = worldData.BoundsMinY,
            BoundsMaxX = worldData.BoundsMaxX,
            BoundsMaxY = worldData.BoundsMaxY,
            EncounterZoneFormId = worldData.EncounterZoneFormId,
            Offset = worldData.Offset,
            IsBigEndian = true
        };
    }

    private static uint? GetConsensusFormId(IEnumerable<uint?> formIds)
    {
        uint? consensus = null;
        foreach (var formId in formIds)
        {
            if (formId is not > 0)
            {
                continue;
            }

            if (consensus == null)
            {
                consensus = formId.Value;
                continue;
            }

            if (consensus.Value != formId.Value)
            {
                return null;
            }
        }

        return consensus;
    }

    private static WorldspaceRecord MergeWorldspace(WorldspaceRecord esm, WorldspaceRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            FullName = esm.FullName ?? runtime.FullName,
            ParentWorldspaceFormId = esm.ParentWorldspaceFormId ?? runtime.ParentWorldspaceFormId,
            ClimateFormId = esm.ClimateFormId ?? runtime.ClimateFormId,
            WaterFormId = esm.WaterFormId ?? runtime.WaterFormId,
            DefaultLandHeight = esm.DefaultLandHeight ?? runtime.DefaultLandHeight,
            DefaultWaterHeight = esm.DefaultWaterHeight ?? runtime.DefaultWaterHeight,
            MapUsableWidth = esm.MapUsableWidth ?? runtime.MapUsableWidth,
            MapUsableHeight = esm.MapUsableHeight ?? runtime.MapUsableHeight,
            MapNWCellX = esm.MapNWCellX ?? runtime.MapNWCellX,
            MapNWCellY = esm.MapNWCellY ?? runtime.MapNWCellY,
            MapSECellX = esm.MapSECellX ?? runtime.MapSECellX,
            MapSECellY = esm.MapSECellY ?? runtime.MapSECellY,
            BoundsMinX = esm.BoundsMinX ?? runtime.BoundsMinX,
            BoundsMinY = esm.BoundsMinY ?? runtime.BoundsMinY,
            BoundsMaxX = esm.BoundsMaxX ?? runtime.BoundsMaxX,
            BoundsMaxY = esm.BoundsMaxY ?? runtime.BoundsMaxY,
            EncounterZoneFormId = esm.EncounterZoneFormId ?? runtime.EncounterZoneFormId,
            Flags = esm.Flags ?? runtime.Flags,
            ParentUseFlags = esm.ParentUseFlags ?? runtime.ParentUseFlags,
            ImageSpaceFormId = esm.ImageSpaceFormId ?? runtime.ImageSpaceFormId,
            MusicTypeFormId = esm.MusicTypeFormId ?? runtime.MusicTypeFormId,
            MapOffsetScaleX = esm.MapOffsetScaleX ?? runtime.MapOffsetScaleX,
            MapOffsetScaleY = esm.MapOffsetScaleY ?? runtime.MapOffsetScaleY,
            MapOffsetZ = esm.MapOffsetZ ?? runtime.MapOffsetZ,
            Cells = esm.Cells.Count > 0 ? esm.Cells : runtime.Cells,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    #endregion
}
