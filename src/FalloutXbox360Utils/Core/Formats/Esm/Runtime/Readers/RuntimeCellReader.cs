using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     Reader for TESWorldSpace and TESObjectCELL runtime structs from Xbox 360 memory dumps.
///     Walks the worldspace's pCellMap (NiTPointerMap&lt;int, TESObjectCELL*&gt;) hash table
///     to extract cell-to-grid mappings and persistent cell identification.
/// </summary>
internal sealed class RuntimeCellReader
{
    #region TESObjectCELL Struct Layout

    private const int CellShiftStartOffset = 52;

    #endregion

    private readonly bool _allowStructuralReads;
    private readonly RuntimeCellObjectEnumerator _cellEnumerator;
    private readonly RuntimeCellMapWalker _cellMapWalker;
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimePdbFieldAccessor _fields;
    private readonly RuntimeWorldCellLayout _layout;

    internal RuntimeCellReader(
        RuntimeMemoryContext context,
        bool useProtoOffsets = false,
        RuntimeWorldCellLayoutProbeResult? layoutProbe = null)
        : this(
            context,
            ResolveLayout(useProtoOffsets, layoutProbe),
            ResolveAllowStructuralReads(useProtoOffsets, layoutProbe))
    {
    }

    internal RuntimeCellReader(RuntimeMemoryContext context, RuntimeWorldCellLayout layout)
        : this(context, layout, true)
    {
    }

    private RuntimeCellReader(
        RuntimeMemoryContext context,
        RuntimeWorldCellLayout layout,
        bool allowStructuralReads)
    {
        _context = context;
        _fields = new RuntimePdbFieldAccessor(context);
        _layout = layout;
        _allowStructuralReads = allowStructuralReads;
        _cellEnumerator = new RuntimeCellObjectEnumerator(context, _fields, AdjustCellFieldOffset);
        _cellMapWalker = new RuntimeCellMapWalker(context, _cellEnumerator);
    }

    /// <summary>
    ///     Resolves the world/cell layout from probe results and build-era knowledge.
    ///     High-confidence probes win outright. For low-confidence probes on early builds,
    ///     uses the RTTI-derived worldShift (-4) while keeping the probe's cellShift
    ///     (which the probe determines reliably even at low confidence).
    /// </summary>
    private static RuntimeWorldCellLayout ResolveLayout(
        bool useProtoOffsets, RuntimeWorldCellLayoutProbeResult? probe)
    {
        if (probe is not { WinnerScore: > 0 })
        {
            return RuntimeWorldCellLayout.CreateDefault(useProtoOffsets);
        }

        if (probe.IsHighConfidence)
        {
            return probe.Layout;
        }

        // Low-confidence probe on early build: the probe can't reliably determine worldShift
        // (all pointer fields null in sparse memory), but RTTI confirms TESWorldSpace is
        // 240 bytes in Nov 2009 vs 244 in final → worldShift = -4.
        // Keep probe's cellShift, which has clear differentiation even at low confidence.
        if (useProtoOffsets)
        {
            var rttiWorldShift = RuntimeBuildOffsets.GetWorldCellFieldShift(true);
            return new RuntimeWorldCellLayout(rttiWorldShift, probe.Layout.CellShift);
        }

        return probe.Layout;
    }

    /// <summary>
    ///     Structural reads (cell map walking) require either high-confidence probe results
    ///     or RTTI-backed knowledge of the correct layout (early builds).
    ///     Low-confidence probes on non-early builds disable structural reads to avoid
    ///     walking memory with potentially wrong offsets.
    /// </summary>
    private static bool ResolveAllowStructuralReads(
        bool useProtoOffsets, RuntimeWorldCellLayoutProbeResult? probe)
    {
        if (probe is { IsHighConfidence: true })
        {
            return true;
        }

        // Early builds: RTTI provides the correct worldShift, so structural reads are safe
        if (useProtoOffsets && probe is { WinnerScore: > 0 })
        {
            return true;
        }

        return probe?.IsHighConfidence != false;
    }

    public WorldspaceRecord? ReadRuntimeWorldspace(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != 0x41 || !entry.TesFormOffset.HasValue)
        {
            return null;
        }

        var worldspace = ReadRuntimeWorldspaceCore(entry);
        var cellMapData = ReadWorldspaceCellMap(entry);
        if (worldspace != null)
        {
            return cellMapData == null
                ? worldspace
                : MergeRuntimeWorldspace(worldspace, ToWorldspaceRecord(cellMapData));
        }

        if (cellMapData == null)
        {
            return null;
        }

        return ToWorldspaceRecord(cellMapData);
    }

    public CellRecord? ReadRuntimeCell(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != 0x39 || !entry.TesFormOffset.HasValue)
        {
            return null;
        }

        return RuntimeCellObjectEnumerator.BuildCellRecord(
            ReadRuntimeCellProbeSnapshot(entry),
            entry.TesFormOffset.Value,
            entry.EditorId,
            entry.DisplayName);
    }

    public CellRecord? ReadRuntimeCell(RuntimeCellMapEntry entry, string? editorId = null, string? displayName = null)
    {
        CellRecord? cell = null;
        if (entry.CellPointer.HasValue)
        {
            var fileOffset = _context.VaToFileOffset(entry.CellPointer.Value);
            if (fileOffset.HasValue)
            {
                cell = RuntimeCellObjectEnumerator.BuildCellRecord(
                    _cellEnumerator.ReadRuntimeCellProbeSnapshot(fileOffset.Value, entry.CellFormId, displayName),
                    fileOffset.Value,
                    editorId,
                    displayName);
            }
        }

        if (cell == null)
        {
            return new CellRecord
            {
                FormId = entry.CellFormId,
                EditorId = RuntimeCellObjectEnumerator.NormalizeString(editorId),
                FullName = RuntimeCellObjectEnumerator.NormalizeString(displayName),
                GridX = entry.GridX,
                GridY = entry.GridY,
                WorldspaceFormId = entry.WorldspaceFormId,
                Flags = entry.IsInterior ? (byte)0x01 : (byte)0x00,
                HasPersistentObjects = entry.IsPersistent,
                IsBigEndian = true
            };
        }

        return cell with
        {
            GridX = cell.GridX ?? entry.GridX,
            GridY = cell.GridY ?? entry.GridY,
            WorldspaceFormId = cell.WorldspaceFormId ?? entry.WorldspaceFormId,
            HasPersistentObjects = cell.HasPersistentObjects || entry.IsPersistent
        };
    }

    internal RuntimeCellObjectEnumerator.RuntimeCellProbeSnapshot? ReadRuntimeCellProbeSnapshot(
        RuntimeEditorIdEntry entry)
    {
        return _cellEnumerator.ReadRuntimeCellProbeSnapshot(entry, ReadStructBuffer);
    }

    /// <summary>
    ///     Read all worldspace cell maps from the given WRLD form entries.
    /// </summary>
    public Dictionary<uint, RuntimeWorldspaceData> ReadAllWorldspaceCellMaps(
        IEnumerable<RuntimeEditorIdEntry> worldEntries)
    {
        var result = new Dictionary<uint, RuntimeWorldspaceData>();

        foreach (var entry in worldEntries)
        {
            var data = ReadWorldspaceCellMap(entry);
            if (data != null)
            {
                result[data.FormId] = data;
            }
        }

        return result;
    }

    /// <summary>
    ///     Read a TESWorldSpace struct and walk its pCellMap hash table.
    /// </summary>
    public RuntimeWorldspaceData? ReadWorldspaceCellMap(RuntimeEditorIdEntry entry)
    {
        if (!_allowStructuralReads || entry.TesFormOffset == null)
        {
            return null;
        }

        var buffer = ReadStructBuffer(entry, WorldStructSize);
        if (buffer == null || buffer.Length < 16)
        {
            return null;
        }

        // Validate FormType byte (0x41 = WRLD, or original pre-drift value)
        var formType = buffer[4];
        if (formType != 0x41 && formType != (entry.OriginalFormType ?? 0x41))
        {
            return null;
        }

        // Validate FormID
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId || formId == 0)
        {
            return null;
        }

        WorldspaceRecord? worldspaceMetadata = null;
        var layout = PdbStructLayouts.Get(0x41);
        if (layout != null)
        {
            worldspaceMetadata = BuildRuntimeWorldspaceRecord(entry, buffer, layout);
        }

        // Read pPersistentCell pointer
        var persistentCellFormId = _cellMapWalker.ReadCellFormIdFromPointer(buffer, WorldPersistentCellPtrOffset);

        // Read pParentWorld pointer -> FormID
        uint? parentWorldFormId = null;
        if (WorldParentWorldPtrOffset + 4 <= buffer.Length)
        {
            parentWorldFormId = _context.FollowPointerToFormId(buffer, WorldParentWorldPtrOffset, 0x41);
        }

        // Follow pCellMap pointer and walk the NiTPointerMap
        var cells = _cellMapWalker.WalkCellMap(buffer, WorldCellMapPtrOffset);

        // Mark the persistent cell
        if (persistentCellFormId.HasValue)
        {
            for (var i = 0; i < cells.Count; i++)
            {
                if (cells[i].CellFormId == persistentCellFormId.Value)
                {
                    cells[i] = cells[i] with { IsPersistent = true };
                }
            }
        }

        return new RuntimeWorldspaceData
        {
            FormId = formId,
            PersistentCellFormId = persistentCellFormId,
            EditorId = worldspaceMetadata?.EditorId
                       ?? RuntimeCellObjectEnumerator.NormalizeString(entry.EditorId),
            FullName = worldspaceMetadata?.FullName
                       ?? RuntimeCellObjectEnumerator.NormalizeString(entry.DisplayName),
            ParentWorldFormId = worldspaceMetadata?.ParentWorldspaceFormId ?? parentWorldFormId,
            ClimateFormId = worldspaceMetadata?.ClimateFormId,
            WaterFormId = worldspaceMetadata?.WaterFormId,
            DefaultLandHeight = worldspaceMetadata?.DefaultLandHeight,
            DefaultWaterHeight = worldspaceMetadata?.DefaultWaterHeight,
            MapUsableWidth = worldspaceMetadata?.MapUsableWidth,
            MapUsableHeight = worldspaceMetadata?.MapUsableHeight,
            MapNWCellX = worldspaceMetadata?.MapNWCellX,
            MapNWCellY = worldspaceMetadata?.MapNWCellY,
            MapSECellX = worldspaceMetadata?.MapSECellX,
            MapSECellY = worldspaceMetadata?.MapSECellY,
            BoundsMinX = worldspaceMetadata?.BoundsMinX,
            BoundsMinY = worldspaceMetadata?.BoundsMinY,
            BoundsMaxX = worldspaceMetadata?.BoundsMaxX,
            BoundsMaxY = worldspaceMetadata?.BoundsMaxY,
            EncounterZoneFormId = worldspaceMetadata?.EncounterZoneFormId,
            Offset = entry.TesFormOffset.Value,
            Cells = cells
        };
    }

    /// <summary>
    ///     Score how well pointer fields at shifted offsets look like real pointer slots.
    ///     Used by the layout probe to discriminate correct vs wrong world shifts.
    ///     <para>
    ///         Returns (plausible, garbage): plausible = count of valid VAs or nulls (expected for
    ///         pointer fields), garbage = count of non-zero values that are NOT valid VAs (strong
    ///         signal of misaligned read — correct shifts don't produce garbage in pointer slots).
    ///     </para>
    /// </summary>
    internal (int Plausible, int Garbage) ProbeWorldPointerPlausibility(RuntimeEditorIdEntry entry)
    {
        var buffer = ReadStructBuffer(entry, WorldStructSize);
        if (buffer == null || !IsExpectedForm(buffer, 0x41, entry.FormId))
        {
            return (0, 0);
        }

        // PDB offsets for key pointer fields (all >= WorldShiftStartOffset, so shift applies)
        ReadOnlySpan<int>
            pdbPointerOffsets =
                [64, 68, 80, 128, 132]; // pCellMap, pPersistentCell, pClimate, pParentWorld, pWorldWater

        var plausible = 0;
        var garbage = 0;

        foreach (var pdbOffset in pdbPointerOffsets)
        {
            var adjusted = pdbOffset + _layout.WorldShift;
            if (adjusted < 0 || adjusted + 4 > buffer.Length)
            {
                continue;
            }

            var ptrValue = BinaryUtils.ReadUInt32BE(buffer, adjusted);
            if (ptrValue == 0)
            {
                plausible++; // Null pointer — expected for unloaded data
            }
            else if (_context.IsValidPointer(ptrValue))
            {
                plausible++; // Valid VA — strong positive signal
            }
            else
            {
                garbage++; // Non-zero, not a valid VA — likely misaligned read
            }
        }

        return (plausible, garbage);
    }

    #region Worldspace Reading

    private WorldspaceRecord? ReadRuntimeWorldspaceCore(RuntimeEditorIdEntry entry)
    {
        var layout = PdbStructLayouts.Get(0x41);
        if (layout == null)
        {
            return null;
        }

        var buffer = ReadStructBuffer(entry, layout.StructSize);
        if (buffer == null || !IsExpectedForm(buffer, 0x41, entry.FormId, entry.OriginalFormType))
        {
            return null;
        }

        var mapDataOffset =
            AdjustWorldFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "WorldMapData", "TESWorldSpace"));
        var minimumCoordsOffset =
            AdjustWorldFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "MinimumCoords", "TESWorldSpace"));
        var maximumCoordsOffset =
            AdjustWorldFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "MaximumCoords", "TESWorldSpace"));

        int? mapUsableWidth = null;
        int? mapUsableHeight = null;
        short? mapNWCellX = null;
        short? mapNWCellY = null;
        short? mapSECellX = null;
        short? mapSECellY = null;

        if (mapDataOffset.HasValue && mapDataOffset.Value + 16 <= buffer.Length)
        {
            mapUsableWidth = RuntimePdbFieldAccessor.ReadInt32(buffer, mapDataOffset.Value);
            mapUsableHeight = RuntimePdbFieldAccessor.ReadInt32(buffer, mapDataOffset.Value + 4);
            mapNWCellX = unchecked((short)RuntimePdbFieldAccessor.ReadUInt16(buffer, mapDataOffset.Value + 8));
            mapNWCellY = unchecked((short)RuntimePdbFieldAccessor.ReadUInt16(buffer, mapDataOffset.Value + 10));
            mapSECellX = unchecked((short)RuntimePdbFieldAccessor.ReadUInt16(buffer, mapDataOffset.Value + 12));
            mapSECellY = unchecked((short)RuntimePdbFieldAccessor.ReadUInt16(buffer, mapDataOffset.Value + 14));

            if (mapUsableWidth == 0 && mapUsableHeight == 0 &&
                mapNWCellX == 0 && mapNWCellY == 0 &&
                mapSECellX == 0 && mapSECellY == 0)
            {
                mapUsableWidth = null;
                mapUsableHeight = null;
                mapNWCellX = null;
                mapNWCellY = null;
                mapSECellX = null;
                mapSECellY = null;
            }
        }

        float? boundsMinX = null;
        float? boundsMinY = null;
        if (minimumCoordsOffset.HasValue && minimumCoordsOffset.Value + 8 <= buffer.Length)
        {
            boundsMinX = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer, minimumCoordsOffset.Value);
            boundsMinY = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer, minimumCoordsOffset.Value + 4);
        }

        float? boundsMaxX = null;
        float? boundsMaxY = null;
        if (maximumCoordsOffset.HasValue && maximumCoordsOffset.Value + 8 <= buffer.Length)
        {
            boundsMaxX = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer, maximumCoordsOffset.Value);
            boundsMaxY = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer, maximumCoordsOffset.Value + 4);
        }

        if (boundsMinX is 0f && boundsMinY is 0f && boundsMaxX is 0f && boundsMaxY is 0f)
        {
            boundsMinX = null;
            boundsMinY = null;
            boundsMaxX = null;
            boundsMaxY = null;
        }

        return BuildRuntimeWorldspaceRecord(
            entry, buffer, layout,
            mapUsableWidth, mapUsableHeight,
            mapNWCellX, mapNWCellY, mapSECellX, mapSECellY,
            boundsMinX, boundsMinY, boundsMaxX, boundsMaxY);
    }

    private WorldspaceRecord BuildRuntimeWorldspaceRecord(
        RuntimeEditorIdEntry entry,
        byte[] buffer,
        PdbTypeLayout layout,
        int? mapUsableWidth,
        int? mapUsableHeight,
        short? mapNWCellX,
        short? mapNWCellY,
        short? mapSECellX,
        short? mapSECellY,
        float? boundsMinX,
        float? boundsMinY,
        float? boundsMaxX,
        float? boundsMaxY)
    {
        var ext = ReadWorldExtendedFields(buffer, layout);

        return new WorldspaceRecord
        {
            FormId = entry.FormId,
            EditorId = RuntimeCellObjectEnumerator.NormalizeString(entry.EditorId),
            FullName = RuntimeCellObjectEnumerator.NormalizeString(entry.DisplayName)
                       ?? _fields.ReadBsString(entry.TesFormOffset!.Value, layout, "cFullName", "TESFullName"),
            ParentWorldspaceFormId = ReadWorldFormIdPointer(buffer, layout, "pParentWorld", "TESWorldSpace", 0x41),
            ClimateFormId = ReadWorldFormIdPointer(buffer, layout, "pClimate", "TESWorldSpace", 0x36),
            WaterFormId = ReadWorldFormIdPointer(buffer, layout, "pWorldWater", "TESWorldSpace", 0x4E),
            DefaultLandHeight = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer,
                AdjustWorldFieldOffset(
                    RuntimePdbFieldAccessor.FindFieldOffset(layout, "fDefaultLandHeight", "TESWorldSpace"))),
            DefaultWaterHeight = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer,
                AdjustWorldFieldOffset(
                    RuntimePdbFieldAccessor.FindFieldOffset(layout, "fDefaultWaterHeight", "TESWorldSpace"))),
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
            EncounterZoneFormId = ReadWorldFormIdPointer(buffer, layout, "pEncounterZone", "TESWorldSpace", 0x61),
            Flags = ext.Flags,
            ParentUseFlags = ext.ParentUseFlags,
            ImageSpaceFormId = ext.ImageSpaceFormId,
            MusicTypeFormId = ext.MusicTypeFormId,
            MapOffsetScaleX = ext.MapOffsetScaleX,
            MapOffsetScaleY = ext.MapOffsetScaleY,
            MapOffsetZ = ext.MapOffsetZ,
            Offset = entry.TesFormOffset!.Value,
            IsBigEndian = true
        };
    }

    private WorldspaceRecord BuildRuntimeWorldspaceRecord(
        RuntimeEditorIdEntry entry,
        byte[] buffer,
        PdbTypeLayout layout)
    {
        var mapDataOffset =
            AdjustWorldFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "WorldMapData", "TESWorldSpace"));
        var minimumCoordsOffset =
            AdjustWorldFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "MinimumCoords", "TESWorldSpace"));
        var maximumCoordsOffset =
            AdjustWorldFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "MaximumCoords", "TESWorldSpace"));

        int? mapUsableWidth = null;
        int? mapUsableHeight = null;
        short? mapNWCellX = null;
        short? mapNWCellY = null;
        short? mapSECellX = null;
        short? mapSECellY = null;

        if (mapDataOffset.HasValue && mapDataOffset.Value + 16 <= buffer.Length)
        {
            mapUsableWidth = RuntimePdbFieldAccessor.ReadInt32(buffer, mapDataOffset.Value);
            mapUsableHeight = RuntimePdbFieldAccessor.ReadInt32(buffer, mapDataOffset.Value + 4);
            mapNWCellX = unchecked((short)RuntimePdbFieldAccessor.ReadUInt16(buffer, mapDataOffset.Value + 8));
            mapNWCellY = unchecked((short)RuntimePdbFieldAccessor.ReadUInt16(buffer, mapDataOffset.Value + 10));
            mapSECellX = unchecked((short)RuntimePdbFieldAccessor.ReadUInt16(buffer, mapDataOffset.Value + 12));
            mapSECellY = unchecked((short)RuntimePdbFieldAccessor.ReadUInt16(buffer, mapDataOffset.Value + 14));

            if (mapUsableWidth == 0 && mapUsableHeight == 0 &&
                mapNWCellX == 0 && mapNWCellY == 0 &&
                mapSECellX == 0 && mapSECellY == 0)
            {
                mapUsableWidth = null;
                mapUsableHeight = null;
                mapNWCellX = null;
                mapNWCellY = null;
                mapSECellX = null;
                mapSECellY = null;
            }
        }

        float? boundsMinX = null;
        float? boundsMinY = null;
        if (minimumCoordsOffset.HasValue && minimumCoordsOffset.Value + 8 <= buffer.Length)
        {
            boundsMinX = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer, minimumCoordsOffset.Value);
            boundsMinY = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer, minimumCoordsOffset.Value + 4);
        }

        float? boundsMaxX = null;
        float? boundsMaxY = null;
        if (maximumCoordsOffset.HasValue && maximumCoordsOffset.Value + 8 <= buffer.Length)
        {
            boundsMaxX = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer, maximumCoordsOffset.Value);
            boundsMaxY = RuntimeCellObjectEnumerator.ReadNormalFloat(buffer, maximumCoordsOffset.Value + 4);
        }

        if (boundsMinX is 0f && boundsMinY is 0f && boundsMaxX is 0f && boundsMaxY is 0f)
        {
            boundsMinX = null;
            boundsMinY = null;
            boundsMaxX = null;
            boundsMaxY = null;
        }

        return BuildRuntimeWorldspaceRecord(
            entry, buffer, layout,
            mapUsableWidth, mapUsableHeight,
            mapNWCellX, mapNWCellY, mapSECellX, mapSECellY,
            boundsMinX, boundsMinY, boundsMaxX, boundsMaxY);
    }

    #endregion

    #region Worldspace Helpers

    private static WorldspaceRecord ToWorldspaceRecord(RuntimeWorldspaceData worldData)
    {
        return new WorldspaceRecord
        {
            FormId = worldData.FormId,
            EditorId = worldData.EditorId,
            FullName = worldData.FullName,
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

    private static WorldspaceRecord MergeRuntimeWorldspace(WorldspaceRecord preferred, WorldspaceRecord fallback)
    {
        return preferred with
        {
            EditorId = preferred.EditorId ?? fallback.EditorId,
            FullName = preferred.FullName ?? fallback.FullName,
            ParentWorldspaceFormId = preferred.ParentWorldspaceFormId ?? fallback.ParentWorldspaceFormId,
            ClimateFormId = preferred.ClimateFormId ?? fallback.ClimateFormId,
            WaterFormId = preferred.WaterFormId ?? fallback.WaterFormId,
            DefaultLandHeight = preferred.DefaultLandHeight ?? fallback.DefaultLandHeight,
            DefaultWaterHeight = preferred.DefaultWaterHeight ?? fallback.DefaultWaterHeight,
            MapUsableWidth = preferred.MapUsableWidth ?? fallback.MapUsableWidth,
            MapUsableHeight = preferred.MapUsableHeight ?? fallback.MapUsableHeight,
            MapNWCellX = preferred.MapNWCellX ?? fallback.MapNWCellX,
            MapNWCellY = preferred.MapNWCellY ?? fallback.MapNWCellY,
            MapSECellX = preferred.MapSECellX ?? fallback.MapSECellX,
            MapSECellY = preferred.MapSECellY ?? fallback.MapSECellY,
            BoundsMinX = preferred.BoundsMinX ?? fallback.BoundsMinX,
            BoundsMinY = preferred.BoundsMinY ?? fallback.BoundsMinY,
            BoundsMaxX = preferred.BoundsMaxX ?? fallback.BoundsMaxX,
            BoundsMaxY = preferred.BoundsMaxY ?? fallback.BoundsMaxY,
            EncounterZoneFormId = preferred.EncounterZoneFormId ?? fallback.EncounterZoneFormId,
            Flags = preferred.Flags ?? fallback.Flags,
            ParentUseFlags = preferred.ParentUseFlags ?? fallback.ParentUseFlags,
            ImageSpaceFormId = preferred.ImageSpaceFormId ?? fallback.ImageSpaceFormId,
            MusicTypeFormId = preferred.MusicTypeFormId ?? fallback.MusicTypeFormId,
            MapOffsetScaleX = preferred.MapOffsetScaleX ?? fallback.MapOffsetScaleX,
            MapOffsetScaleY = preferred.MapOffsetScaleY ?? fallback.MapOffsetScaleY,
            MapOffsetZ = preferred.MapOffsetZ ?? fallback.MapOffsetZ,
            Offset = preferred.Offset != 0 ? preferred.Offset : fallback.Offset,
            IsBigEndian = preferred.IsBigEndian || fallback.IsBigEndian
        };
    }

    private (byte? Flags, ushort? ParentUseFlags, uint? ImageSpaceFormId, uint? MusicTypeFormId,
        float? MapOffsetScaleX, float? MapOffsetScaleY, float? MapOffsetZ) ReadWorldExtendedFields(
            byte[] buffer, PdbTypeLayout layout)
    {
        var flagsOffset =
            AdjustWorldFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "cFlags", "TESWorldSpace"));
        byte? flags = flagsOffset.HasValue && flagsOffset.Value < buffer.Length
            ? buffer[flagsOffset.Value]
            : null;

        var parentUseFlagsOffset =
            AdjustWorldFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, "sParentUseFlags", "TESWorldSpace"));
        ushort? parentUseFlags = parentUseFlagsOffset.HasValue && parentUseFlagsOffset.Value + 2 <= buffer.Length
            ? RuntimePdbFieldAccessor.ReadUInt16(buffer, parentUseFlagsOffset.Value)
            : null;

        var imageSpaceFormId = ReadWorldFormIdPointer(buffer, layout, "pImageSpace", "TESWorldSpace", 0x56);
        var musicTypeFormId = ReadWorldFormIdPointer(buffer, layout, "pMusicType", "TESWorldSpace", 0x6B);

        var offsetDataOffset =
            AdjustWorldFieldOffset(
                RuntimePdbFieldAccessor.FindFieldOffset(layout, "WorldMapOffsetData", "TESWorldSpace"));
        float? mapOffsetScaleX = null;
        float? mapOffsetScaleY = null;
        float? mapOffsetZ = null;

        if (offsetDataOffset.HasValue && offsetDataOffset.Value + 12 <= buffer.Length)
        {
            mapOffsetScaleX = BinaryUtils.ReadFloatBE(buffer, offsetDataOffset.Value);
            mapOffsetScaleY = BinaryUtils.ReadFloatBE(buffer, offsetDataOffset.Value + 4);
            mapOffsetZ = BinaryUtils.ReadFloatBE(buffer, offsetDataOffset.Value + 8);

            if (mapOffsetScaleX is 0f && mapOffsetScaleY is 0f && mapOffsetZ is 0f)
            {
                mapOffsetScaleX = null;
                mapOffsetScaleY = null;
                mapOffsetZ = null;
            }
        }

        return (flags, parentUseFlags, imageSpaceFormId, musicTypeFormId,
            mapOffsetScaleX, mapOffsetScaleY, mapOffsetZ);
    }

    private uint? ReadWorldFormIdPointer(
        byte[] buffer,
        PdbTypeLayout layout,
        string name,
        string? owner = null,
        byte? expectedFormType = null)
    {
        var fieldOffset = AdjustWorldFieldOffset(RuntimePdbFieldAccessor.FindFieldOffset(layout, name, owner));
        return fieldOffset.HasValue
            ? _fields.ReadPointerToFormId(buffer, fieldOffset.Value, expectedFormType)
            : null;
    }

    #endregion

    #region Buffer and Offset Helpers

    private byte[]? ReadStructBuffer(RuntimeEditorIdEntry entry, int size)
    {
        if (entry.TesFormPointer.HasValue)
        {
            return _context.ReadBytesAtVa(entry.TesFormPointer.Value, size);
        }

        var offset = entry.TesFormOffset!.Value;
        if (offset + size > _context.FileSize)
        {
            return null;
        }

        return _context.ReadBytes(offset, size);
    }

    private int? AdjustWorldFieldOffset(int? offset)
    {
        if (!offset.HasValue)
        {
            return null;
        }

        return offset.Value >= WorldShiftStartOffset
            ? offset.Value + _layout.WorldShift
            : offset.Value;
    }

    private int? AdjustCellFieldOffset(int? offset)
    {
        if (!offset.HasValue)
        {
            return null;
        }

        return offset.Value >= CellShiftStartOffset
            ? offset.Value + _layout.CellShift
            : offset.Value;
    }

    private static bool IsExpectedForm(byte[] buffer, byte expectedFormType, uint expectedFormId,
        byte? originalFormType = null)
    {
        if (buffer.Length < 16)
        {
            return false;
        }

        // Accept both the final-build FormType and the original pre-drift value
        if (buffer[4] != expectedFormType && buffer[4] != (originalFormType ?? expectedFormType))
        {
            return false;
        }

        return BinaryUtils.ReadUInt32BE(buffer, 12) == expectedFormId && expectedFormId != 0;
    }

    #endregion

    #region TESWorldSpace Struct Layout

    private const int WorldStructSize = 244;
    private const int WorldShiftStartOffset = 64;

    private int WorldCellMapPtrOffset => 64 + _layout.WorldShift;
    private int WorldPersistentCellPtrOffset => 68 + _layout.WorldShift;
    private int WorldParentWorldPtrOffset => 128 + _layout.WorldShift;

    #endregion
}
