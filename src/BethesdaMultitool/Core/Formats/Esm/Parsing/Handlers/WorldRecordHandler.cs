using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal sealed class WorldRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    private readonly CellRecordHandler _cellHandler = new(context);
    private readonly WorldspaceRecordHandler _worldspaceHandler = new(context);

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
                RadioData = refr.RadioData,
                OwnerFormId = refr.OwnerFormId,
                EncounterZoneFormId = refr.EncounterZoneFormId,
                MaterialSwapFormId = refr.MaterialSwapFormId,
                EmittanceFormId = refr.EmittanceFormId,
                LockLevel = refr.LockLevel,
                LockKeyFormId = refr.LockKeyFormId,
                LockFlags = refr.LockFlags,
                LockNumTries = refr.LockNumTries,
                LockTimesUnlocked = refr.LockTimesUnlocked,
                EnableParentFormId = refr.EnableParentFormId,
                EnableParentFlags = refr.EnableParentFlags,
                SpecialRenderingFlags = refr.SpecialRenderingFlags,
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
                // Resolve the marker's FULL through the localized .STRINGS table (loaded after the scan).
                // For a localized plugin (Skyrim/FO4) the raw FULL is a 4-byte string ID; ReadFullName turns
                // it into real text and falls back to the inline string for non-localized FNV/Xbox plugins.
                MarkerName = refr.MarkerNameRaw is { Length: > 0 } rawName
                    ? Context.ReadFullName(rawName)
                    : refr.MarkerName,
                LinkedRefKeywordFormId = refr.LinkedRefKeywordFormId,
                LinkedRefFormId = refr.LinkedRefFormId,
                LinkedRefChildrenFormIds = refr.LinkedRefChildrenFormIds,
                StructuralData = refr.StructuralData,
                EditorId = refr.EditorId,
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
    ///     <para>
    ///         Mutates IN PLACE (per-slot assignment into the existing <c>PlacedObjects</c> list; the
    ///         house style — see SpawnPositionResolver). The previous shape rebuilt every cell's list
    ///         and cloned the cell — and because <c>LinkCellsToWorldspaces</c> aliases the SAME
    ///         <c>CellRecord</c> instances into <c>WorldspaceRecord.Cells</c>, replacing
    ///         <c>cells[i]</c> silently FORKED the graph: the worldspace lists kept the stale
    ///         pre-enrichment cells, which a second full enrichment pass then cloned AGAIN (~10.2M
    ///         clones ≈ 2.5 GB of garbage on Fallout 76). In-place mutation keeps the aliasing intact,
    ///         so one pass enriches both views and the divergence class is gone.
    ///     </para>
    /// </summary>
    internal static void EnrichPlacedReferences(
        List<CellRecord> cells,
        Dictionary<uint, ObjectBounds> boundsIndex,
        Dictionary<uint, string> modelIndex)
    {
        foreach (var cell in cells)
        {
            var placed = cell.PlacedObjects;
            for (var j = 0; j < placed.Count; j++)
            {
                var obj = placed[j];
                boundsIndex.TryGetValue(obj.BaseFormId, out var bounds);
                modelIndex.TryGetValue(obj.BaseFormId, out var modelPath);
                if (bounds != null || modelPath != null)
                {
                    placed[j] = obj with
                    {
                        Bounds = bounds ?? obj.Bounds,
                        ModelPath = modelPath ?? obj.ModelPath
                    };
                }
            }
        }
    }

    #endregion

    #region Worldspaces

    /// <summary>
    ///     Parse all Worldspace records from the scan result.
    ///     Delegates to <see cref="WorldspaceRecordHandler" />.
    /// </summary>
    internal List<WorldspaceRecord> ParseWorldspaces()
    {
        return _worldspaceHandler.ParseWorldspaces();
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

    internal static int ResolveRuntimeAnchoredCellRuns(
        List<CellRecord> cells,
        List<WorldspaceRecord> worldspaces,
        IReadOnlyDictionary<uint, RuntimeWorldspaceData>? runtimeWorldspaceMaps)
    {
        return CellLinkageHandler.ResolveRuntimeAnchoredCellRuns(cells, worldspaces, runtimeWorldspaceMaps);
    }

    /// <summary>
    ///     DMP fallback: assign worldspace ownership directly from the runtime pCellMap to any
    ///     exterior cell still lacking one. Delegates to <see cref="CellLinkageHandler" />.
    /// </summary>
    internal static int AssignRuntimeCellMapOwners(
        List<CellRecord> cells,
        IReadOnlyDictionary<uint, RuntimeWorldspaceData>? runtimeWorldspaceMaps)
    {
        return CellLinkageHandler.AssignRuntimeCellMapOwners(cells, runtimeWorldspaceMaps);
    }

    /// <summary>
    ///     DMP fallback: reclassify worldspace-less, grid-less exterior cells as interior.
    ///     Delegates to <see cref="CellLinkageHandler" />.
    /// </summary>
    internal static int NormalizeStructurallyInteriorCells(List<CellRecord> cells)
    {
        return CellLinkageHandler.NormalizeStructurallyInteriorCells(cells);
    }

    /// <summary>
    ///     DMP fallback: merge orphan-recovery virtual cells into a co-located non-virtual cell of
    ///     the same worldspace and grid so their refs are not lost to grid-collision dedup.
    ///     Delegates to <see cref="CellLinkageHandler" />.
    /// </summary>
    internal static int MergeColocatedVirtualOrphanCells(
        List<CellRecord> cells,
        IReadOnlySet<uint>? masterFormIds = null)
    {
        return CellLinkageHandler.MergeColocatedVirtualOrphanCells(cells, masterFormIds);
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
    ///     Delegates to <see cref="WorldspaceRecordHandler" />.
    /// </summary>
    internal static void EnsureWorldspacesForCells(
        List<CellRecord> cells,
        List<WorldspaceRecord> worldspaces,
        RecordParserContext context)
    {
        WorldspaceRecordHandler.EnsureWorldspacesForCells(cells, worldspaces, context);
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

    #region Regions

    /// <summary>
    ///     Parse all Region (REGN) records.
    /// </summary>
    internal List<RegionRecord> ParseRegions()
    {
        var regions = ParseAccessorOnly("REGN", 4096, ParseRegionFromAccessor);

        // MergeRuntimeRecords is intentionally add-only: an ESM-backed FormID remains in the
        // list, so a runtime reader (which cannot recover RPLI/RPLD or RDAT) cannot replace and
        // discard its static Areas/WeatherTypes. Runtime-only regions are added with no geometry
        // and therefore fail closed in RegionWeatherSelectionResolver.
        Context.MergeRuntimeRecords(regions, 0x37, r => r.FormId,
            (reader, entry) => reader.ReadRuntimeRegion(entry), "regions");

        return regions;
    }

    private RegionRecord? ParseRegionFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        uint worldspaceFormId = 0;
        byte r = 0, g = 0, b = 0;
        var areas = new List<RegionArea>();
        var dataBlocks = new List<RegionDataBlock>();

        // Region geometry precedes the RDAT blocks as repeated RPLI/RPLD pairs. XCLR on a
        // CELL is only a coarse candidate list, so retaining these polygons is required before
        // a viewer can safely apply a region's weather at the camera position.
        uint? currentAreaEdgeFalloff = null;

        // RDAT iteration state: once we see an RDAT header, every subsequent non-RDAT
        // subrecord (until the next RDAT or end-of-record) is part of that block's
        // typed payload. Each block can have one or more payload subrecords
        // (RDOT/RDMP/RDGS/RDMD/RDSD/RDWT — preserving boundaries matters for round-trip).
        uint currentType = 0;
        uint currentFlags = 0;
        List<RegionSubrecord>? currentPayload = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "WNAM" when sub.DataLength >= 4:
                    worldspaceFormId = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "RCLR" when sub.DataLength >= 3:
                    // RCLR is a 4-byte color (R,G,B,unused). Xbox stores it as a byte-reversed uint32
                    // (the conversion schema registers it Simple4Byte/UInt32, whole-dword swap), so on
                    // big-endian the RGBA bytes are reversed and a raw [0][1][2] read yields (unused,B,G).
                    if (record.IsBigEndian && sub.DataLength >= 4)
                    {
                        r = data[sub.DataOffset + 3];
                        g = data[sub.DataOffset + 2];
                        b = data[sub.DataOffset + 1];
                    }
                    else
                    {
                        r = data[sub.DataOffset];
                        g = data[sub.DataOffset + 1];
                        b = data[sub.DataOffset + 2];
                    }

                    break;
                case "RPLI" when sub.DataLength >= 4:
                    currentAreaEdgeFalloff =
                        BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "RPLD" when currentAreaEdgeFalloff is { } edgeFalloff:
                {
                    var points = DecodeRegionPoints(
                        data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                    if (points.Count > 0)
                    {
                        areas.Add(new RegionArea(edgeFalloff, points));
                    }

                    currentAreaEdgeFalloff = null;
                    break;
                }
                case "RDAT" when sub.DataLength >= 8:
                    // Finalize any open block before starting a new one.
                    if (currentPayload is not null)
                    {
                        dataBlocks.Add(new RegionDataBlock(currentType, currentFlags, currentPayload));
                    }

                    currentType = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    // RDAT header after Type is Override(u8) + Priority(u8) + Padding(2) (xEdit /
                    // conversion schema SubrecordCellAndMiscSchemas). Those are single/unused bytes
                    // stored in-place — identical on Xbox and PC — so read the group little-endian on
                    // both; a big-endian read on Xbox scrambled the two flag bytes into the high word.
                    currentFlags = BinaryUtils.ReadUInt32LE(data, sub.DataOffset + 4);
                    currentPayload = [];
                    break;
                default:
                    // Per-type payload (RDOT/RDMP/RDGS/RDMD/RDSD/RDWT) following the open
                    // RDAT. Capture verbatim; RPLI/RPLD geometry was handled above as typed data.
                    if (currentPayload is not null)
                    {
                        var bytes = new byte[sub.DataLength];
                        Buffer.BlockCopy(data, sub.DataOffset, bytes, 0, sub.DataLength);
                        currentPayload.Add(new RegionSubrecord(sub.Signature, bytes));
                    }

                    break;
            }
        }

        // Finalize the final open block.
        if (currentPayload is not null)
        {
            dataBlocks.Add(new RegionDataBlock(currentType, currentFlags, currentPayload));
        }

        // Derive typed weather/grass projections from the captured (opaque) RDAT payloads.
        // The DataBlocks bytes are still round-tripped verbatim by RegnEncoder; these lists are
        // read-only viewer/UI data (per-region weather types and grass FormIDs).
        var weatherTypes = new List<RegionWeatherType>();
        var grassFormIds = new List<uint>();
        foreach (var block in dataBlocks)
        {
            foreach (var payload in block.Payload)
            {
                switch (payload.Signature)
                {
                    case "RDWT":
                        DecodeRegionWeatherTypes(payload.Bytes, record.IsBigEndian, weatherTypes);
                        break;
                    case "RDGS":
                        DecodeRegionGrasses(payload.Bytes, record.IsBigEndian, grassFormIds);
                        break;
                }
            }
        }

        return new RegionRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            WorldspaceFormId = worldspaceFormId,
            EmittanceColorR = r,
            EmittanceColorG = g,
            EmittanceColorB = b,
            Areas = areas,
            DataBlockCount = dataBlocks.Count,
            DataBlocks = dataBlocks,
            WeatherTypes = weatherTypes,
            GrassFormIds = grassFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     RDWT region weather-types: 12 bytes/entry (Weather FormID, Chance u32, Global FormID).
    ///     Stride confirmed by the Xbox→PC converter's RDWT byte-swap schema.
    /// </summary>
    private const int RdwtEntrySize = 12;

    /// <summary>
    ///     RDGS region grasses: 8 bytes/entry (Grass FormID + 4 unused). fopdoc layout —
    ///     PROVISIONAL (no converter schema to confirm; verify in P6). Decoded only when the payload
    ///     length divides evenly, so a wrong-stride reality leaves the list empty rather than garbage.
    /// </summary>
    private const int RdgsEntrySize = 8;

    internal static void DecodeRegionWeatherTypes(byte[] bytes, bool isBigEndian, List<RegionWeatherType> into)
    {
        for (var off = 0; off + RdwtEntrySize <= bytes.Length; off += RdwtEntrySize)
        {
            var weather = BinaryUtils.ReadUInt32(bytes, off, isBigEndian);
            var chance = BinaryUtils.ReadUInt32(bytes, off + 4, isBigEndian);
            var global = BinaryUtils.ReadUInt32(bytes, off + 8, isBigEndian);
            into.Add(new RegionWeatherType(weather, chance, global));
        }
    }

    /// <summary>
    ///     RPLD is an array of world-space XY float pairs. Malformed/truncated payloads are
    ///     rejected atomically so callers never mistake a partial polygon for authoritative
    ///     region coverage.
    /// </summary>
    internal static IReadOnlyList<RegionPoint> DecodeRegionPoints(
        ReadOnlySpan<byte> bytes,
        bool isBigEndian)
    {
        const int pointStride = 8;
        if (bytes.Length < pointStride * 3 || bytes.Length % pointStride != 0)
        {
            return [];
        }

        var points = new RegionPoint[bytes.Length / pointStride];
        for (var i = 0; i < points.Length; i++)
        {
            var offset = i * pointStride;
            var x = BinaryUtils.ReadFloat(bytes, offset, isBigEndian);
            var y = BinaryUtils.ReadFloat(bytes, offset + 4, isBigEndian);
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                return [];
            }

            points[i] = new RegionPoint(x, y);
        }

        return points;
    }

    internal static void DecodeRegionGrasses(byte[] bytes, bool isBigEndian, List<uint> into)
    {
        if (bytes.Length == 0 || bytes.Length % RdgsEntrySize != 0)
        {
            return;
        }

        for (var off = 0; off + RdgsEntrySize <= bytes.Length; off += RdgsEntrySize)
        {
            into.Add(BinaryUtils.ReadUInt32(bytes, off, isBigEndian));
        }
    }

    #endregion

    #region Placed Grenades

    /// <summary>
    ///     Parse all Placed Grenade (PGRE) records. ESM-side only — runtime grenades
    ///     are transient projectiles not persisted in TESForm hash tables.
    /// </summary>
    internal List<PlacedGrenadeRecord> ParsePlacedGrenades()
    {
        return ParseAccessorOnly("PGRE", 1024, ParsePlacedGrenadeFromAccessor);
    }

    private PlacedGrenadeRecord? ParsePlacedGrenadeFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        uint baseFormId = 0;
        float x = 0, y = 0, z = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "NAME" when sub.DataLength >= 4:
                    baseFormId = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 24:
                    // 6 floats: posX, posY, posZ, rotX, rotY, rotZ
                    x = BinaryUtils.ReadFloat(data, sub.DataOffset, record.IsBigEndian);
                    y = BinaryUtils.ReadFloat(data, sub.DataOffset + 4, record.IsBigEndian);
                    z = BinaryUtils.ReadFloat(data, sub.DataOffset + 8, record.IsBigEndian);
                    break;
            }
        }

        return new PlacedGrenadeRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            BaseFormId = baseFormId,
            PositionX = x,
            PositionY = y,
            PositionZ = z,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Camera Paths

    /// <summary>
    ///     Parse all Camera Path (CPTH) records.
    /// </summary>
    internal List<CameraPathRecord> ParseCameraPaths()
    {
        var paths = ParseAccessorOnly("CPTH", 1024, ParseCameraPathFromAccessor);

        Context.MergeRuntimeRecords(paths, 0x5C, p => p.FormId,
            (reader, entry) => reader.ReadRuntimeCameraPath(entry), "camera paths");

        return paths;
    }

    private CameraPathRecord? ParseCameraPathFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        byte flags = 0;
        uint parentPath = 0;
        var conditions = new List<DialogueCondition>();
        var shotFormIds = new List<uint>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "ANAM" when sub.DataLength >= 4:
                    parentPath = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 1:
                    flags = data[sub.DataOffset];
                    break;
                case "SNAM" when sub.DataLength >= 4:
                    shotFormIds.Add(BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian));
                    break;
                case "CTDA":
                    if (CtdaParser.TryDecode(
                            data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian,
                            out var cameraCondition, out _))
                    {
                        conditions.Add(cameraCondition);
                    }

                    break;
            }
        }

        return new CameraPathRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Flags = flags,
            ParentPathFormId = parentPath,
            CameraShotFormIds = shotFormIds,
            ConditionCount = conditions.Count,
            Conditions = conditions,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
