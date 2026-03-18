using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for TESWorldSpace and TESObjectCELL runtime structs from Xbox 360 memory dumps.
///     Walks the worldspace's pCellMap (NiTPointerMap&lt;int, TESObjectCELL*&gt;) hash table
///     to extract cell-to-grid mappings and persistent cell identification.
/// </summary>
internal sealed class RuntimeCellReader
{
    private readonly bool _allowStructuralReads;
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimePdbFieldAccessor _fields;
    private readonly RuntimeWorldCellLayout _layout;

    internal RuntimeCellReader(
        RuntimeMemoryContext context,
        bool useProtoOffsets = false,
        RuntimeWorldCellLayoutProbeResult? layoutProbe = null)
        : this(
            context,
            layoutProbe is { IsHighConfidence: true }
                ? layoutProbe.Layout
                : RuntimeWorldCellLayout.CreateDefault(useProtoOffsets),
            layoutProbe?.IsHighConfidence != false)
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

        return BuildCellRecord(
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
                cell = BuildCellRecord(
                    ReadRuntimeCellProbeSnapshot(fileOffset.Value, entry.CellFormId, displayName),
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
                EditorId = NormalizeString(editorId),
                FullName = NormalizeString(displayName),
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

    internal RuntimeCellProbeSnapshot? ReadRuntimeCellProbeSnapshot(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != 0x39 || !entry.TesFormOffset.HasValue)
        {
            return null;
        }

        var layout = PdbStructLayouts.Get(0x39);
        if (layout == null)
        {
            return null;
        }

        var buffer = ReadStructBuffer(entry, layout.StructSize);
        if (buffer == null)
        {
            return null;
        }

        return ReadRuntimeCellProbeSnapshotFromBuffer(
            buffer,
            entry.TesFormOffset.Value,
            entry.FormId,
            entry.DisplayName,
            layout);
    }

    /// <summary>
    ///     Read all worldspace cell maps from the given WRLD form entries.
    ///     Returns a dictionary mapping worldspace FormID to its cell map data.
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
    ///     Returns null if the struct is invalid or the cell map is empty/unreadable.
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

        // Validate FormType byte (0x41 = WRLD)
        var formType = buffer[4];
        if (formType != 0x41)
        {
            return null;
        }

        // Validate FormID (always at offset 12 in TESForm header for all builds)
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
        var persistentCellFormId = ReadCellFormIdFromPointer(buffer, WorldPersistentCellPtrOffset);

        // Read pParentWorld pointer → FormID
        uint? parentWorldFormId = null;
        if (WorldParentWorldPtrOffset + 4 <= buffer.Length)
        {
            parentWorldFormId = _context.FollowPointerToFormId(buffer, WorldParentWorldPtrOffset, 0x41);
        }

        // Follow pCellMap pointer and walk the NiTPointerMap
        var cells = WalkCellMap(buffer);

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
            EditorId = worldspaceMetadata?.EditorId ?? NormalizeString(entry.EditorId),
            FullName = worldspaceMetadata?.FullName ?? NormalizeString(entry.DisplayName),
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

    private WorldspaceRecord? ReadRuntimeWorldspaceCore(RuntimeEditorIdEntry entry)
    {
        var layout = PdbStructLayouts.Get(0x41);
        if (layout == null)
        {
            return null;
        }

        var buffer = ReadStructBuffer(entry, layout.StructSize);
        if (buffer == null || !IsExpectedForm(buffer, 0x41, entry.FormId))
        {
            return null;
        }

        var mapDataOffset = AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "WorldMapData", "TESWorldSpace"));
        var minimumCoordsOffset =
            AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "MinimumCoords", "TESWorldSpace"));
        var maximumCoordsOffset =
            AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "MaximumCoords", "TESWorldSpace"));

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
            boundsMinX = ReadNormalFloat(buffer, minimumCoordsOffset.Value);
            boundsMinY = ReadNormalFloat(buffer, minimumCoordsOffset.Value + 4);
        }

        float? boundsMaxX = null;
        float? boundsMaxY = null;
        if (maximumCoordsOffset.HasValue && maximumCoordsOffset.Value + 8 <= buffer.Length)
        {
            boundsMaxX = ReadNormalFloat(buffer, maximumCoordsOffset.Value);
            boundsMaxY = ReadNormalFloat(buffer, maximumCoordsOffset.Value + 4);
        }

        if (boundsMinX == 0 && boundsMinY == 0 && boundsMaxX == 0 && boundsMaxY == 0)
        {
            boundsMinX = null;
            boundsMinY = null;
            boundsMaxX = null;
            boundsMaxY = null;
        }

        return BuildRuntimeWorldspaceRecord(
            entry,
            buffer,
            layout,
            mapUsableWidth,
            mapUsableHeight,
            mapNWCellX,
            mapNWCellY,
            mapSECellX,
            mapSECellY,
            boundsMinX,
            boundsMinY,
            boundsMaxX,
            boundsMaxY);
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
            EditorId = NormalizeString(entry.EditorId),
            FullName = NormalizeString(entry.DisplayName)
                       ?? _fields.ReadBsString(entry.TesFormOffset!.Value, layout, "cFullName", "TESFullName"),
            ParentWorldspaceFormId = ReadWorldFormIdPointer(buffer, layout, "pParentWorld", "TESWorldSpace", 0x41),
            ClimateFormId = ReadWorldFormIdPointer(buffer, layout, "pClimate", "TESWorldSpace", 0x36),
            WaterFormId = ReadWorldFormIdPointer(buffer, layout, "pWorldWater", "TESWorldSpace", 0x4E),
            DefaultLandHeight = ReadNormalFloat(buffer,
                AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "fDefaultLandHeight", "TESWorldSpace"))),
            DefaultWaterHeight = ReadNormalFloat(buffer,
                AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "fDefaultWaterHeight", "TESWorldSpace"))),
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
        var mapDataOffset = AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "WorldMapData", "TESWorldSpace"));
        var minimumCoordsOffset =
            AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "MinimumCoords", "TESWorldSpace"));
        var maximumCoordsOffset =
            AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "MaximumCoords", "TESWorldSpace"));

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
            boundsMinX = ReadNormalFloat(buffer, minimumCoordsOffset.Value);
            boundsMinY = ReadNormalFloat(buffer, minimumCoordsOffset.Value + 4);
        }

        float? boundsMaxX = null;
        float? boundsMaxY = null;
        if (maximumCoordsOffset.HasValue && maximumCoordsOffset.Value + 8 <= buffer.Length)
        {
            boundsMaxX = ReadNormalFloat(buffer, maximumCoordsOffset.Value);
            boundsMaxY = ReadNormalFloat(buffer, maximumCoordsOffset.Value + 4);
        }

        if (boundsMinX == 0 && boundsMinY == 0 && boundsMaxX == 0 && boundsMaxY == 0)
        {
            boundsMinX = null;
            boundsMinY = null;
            boundsMaxX = null;
            boundsMaxY = null;
        }

        return BuildRuntimeWorldspaceRecord(
            entry,
            buffer,
            layout,
            mapUsableWidth,
            mapUsableHeight,
            mapNWCellX,
            mapNWCellY,
            mapSECellX,
            mapSECellY,
            boundsMinX,
            boundsMinY,
            boundsMaxX,
            boundsMaxY);
    }

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

    /// <summary>
    ///     Follow the pCellMap pointer from the worldspace struct, then walk the
    ///     NiTPointerMap&lt;int, TESObjectCELL*&gt; hash table at the pointed-to location.
    /// </summary>
    private List<RuntimeCellMapEntry> WalkCellMap(byte[] worldBuffer)
    {
        var cells = new List<RuntimeCellMapEntry>();

        // pCellMap is a POINTER (4 bytes) — follow it to the heap-allocated NiTPointerMap
        if (WorldCellMapPtrOffset + 4 > worldBuffer.Length)
        {
            return cells;
        }

        var cellMapVa = BinaryUtils.ReadUInt32BE(worldBuffer, WorldCellMapPtrOffset);
        if (cellMapVa == 0 || !_context.IsValidPointer(cellMapVa))
        {
            return cells;
        }

        // Read the NiTPointerMap header (16 bytes) from the pointed-to location
        var cellMapVaLong = Xbox360MemoryUtils.VaToLong(cellMapVa);
        var mapBuffer = _context.ReadBytesAtVa(cellMapVaLong, MapHeaderSize);
        if (mapBuffer == null)
        {
            return cells;
        }

        var hashSize = BinaryUtils.ReadUInt32BE(mapBuffer, MapHashSizeOffset);
        var bucketArrayVa = BinaryUtils.ReadUInt32BE(mapBuffer, MapBucketArrayPtrOffset);
        var entryCount = BinaryUtils.ReadUInt32BE(mapBuffer, MapEntryCountOffset);

        if (hashSize == 0 || hashSize > MaxBuckets || entryCount == 0 || !_context.IsValidPointer(bucketArrayVa))
        {
            return cells;
        }

        // Read the bucket array (hashSize × 4 bytes of pointers)
        var bucketArraySize = (int)hashSize * 4;
        var bucketArrayOffset = _context.VaToFileOffset(bucketArrayVa);
        if (bucketArrayOffset == null)
        {
            return cells;
        }

        var bucketArray = _context.ReadBytes(bucketArrayOffset.Value, bucketArraySize);
        if (bucketArray == null)
        {
            return cells;
        }

        // Walk each bucket's linked list
        for (var i = 0; i < (int)hashSize; i++)
        {
            var itemVa = BinaryUtils.ReadUInt32BE(bucketArray, i * 4);
            WalkBucketChain(itemVa, cells);
        }

        return cells;
    }

    /// <summary>
    ///     Walk a single bucket's NiTMapItem linked list, extracting cell entries.
    /// </summary>
    private void WalkBucketChain(uint itemVa, List<RuntimeCellMapEntry> cells)
    {
        var visited = new HashSet<uint>();
        var depth = 0;

        while (itemVa != 0 && depth < MaxChainDepth && visited.Add(itemVa))
        {
            var itemVaLong = Xbox360MemoryUtils.VaToLong(itemVa);
            if (!_context.MinidumpInfo.IsVaRangeCaptured(itemVaLong, ItemSize))
            {
                break;
            }

            var itemOffset = _context.VaToFileOffset(itemVa);
            if (itemOffset == null)
            {
                break;
            }

            var itemBuffer = _context.ReadBytes(itemOffset.Value, ItemSize);
            if (itemBuffer == null)
            {
                break;
            }

            var nextVa = BinaryUtils.ReadUInt32BE(itemBuffer);
            var key = (int)BinaryUtils.ReadUInt32BE(itemBuffer, ItemKeyOffset);
            var cellVa = BinaryUtils.ReadUInt32BE(itemBuffer, ItemValueOffset);

            // Decode grid coordinates from packed key
            var gridX = key >> 16; // Arithmetic shift preserves sign
            var gridY = (short)(key & 0xFFFF); // Cast to short for sign extension

            // Follow cell pointer to read FormID and flags
            if (cellVa != 0 && _context.IsValidPointer(cellVa))
            {
                var cellEntry = ReadCellFromPointer(cellVa, gridX, gridY);
                if (cellEntry != null)
                {
                    cells.Add(cellEntry);
                }
            }

            itemVa = nextVa;
            depth++;
        }
    }

    /// <summary>
    ///     Read a TESObjectCELL at the given VA and extract key fields.
    /// </summary>
    private RuntimeCellMapEntry? ReadCellFromPointer(uint cellVa, int gridX, int gridY)
    {
        var cellVaLong = Xbox360MemoryUtils.VaToLong(cellVa);
        if (!_context.MinidumpInfo.IsVaRangeCaptured(cellVaLong, CellStructSize))
        {
            return null;
        }

        var cellOffset = _context.VaToFileOffset(cellVa);
        if (cellOffset == null)
        {
            return null;
        }

        var snapshot = ReadRuntimeCellProbeSnapshot(cellOffset.Value, null, null);
        if (snapshot == null || snapshot.FormId == 0)
        {
            return null;
        }

        return new RuntimeCellMapEntry
        {
            CellFormId = snapshot.FormId,
            CellPointer = cellVa,
            GridX = gridX,
            GridY = gridY,
            IsInterior = (snapshot.Flags & 0x01) != 0,
            WorldspaceFormId = snapshot.WorldspaceFormId,
            LandFormId = snapshot.LandFormId,
            ReferenceFormIds = snapshot.ReferenceFormIds.ToList()
        };
    }

    /// <summary>
    ///     Follow a pointer from the worldspace buffer to a TESObjectCELL and return its FormID.
    /// </summary>
    private uint? ReadCellFormIdFromPointer(byte[] buffer, int pointerOffset)
    {
        if (pointerOffset + 4 > buffer.Length)
        {
            return null;
        }

        return _context.FollowPointerToFormId(buffer, pointerOffset, 0x39);
    }

    private RuntimeCellProbeSnapshot? ReadRuntimeCellProbeSnapshot(long fileOffset, uint? expectedFormId,
        string? displayName)
    {
        var layout = PdbStructLayouts.Get(0x39);
        if (layout == null)
        {
            return null;
        }

        var buffer = _context.ReadBytes(fileOffset, layout.StructSize);
        if (buffer == null)
        {
            return null;
        }

        return ReadRuntimeCellProbeSnapshotFromBuffer(
            buffer,
            fileOffset,
            expectedFormId,
            displayName,
            layout);
    }

    private RuntimeCellProbeSnapshot? ReadRuntimeCellProbeSnapshotFromBuffer(
        byte[] buffer,
        long fileOffset,
        uint? expectedFormId,
        string? displayName,
        PdbTypeLayout layout)
    {
        if (buffer.Length < 16 || buffer[4] != 0x39)
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        if (expectedFormId.HasValue && formId != expectedFormId.Value)
        {
            return null;
        }

        var flagsOffset = AdjustCellFieldOffset(_fields.FindFieldOffset(layout, "cCellFlags", "TESObjectCELL"));
        var waterHeightOffset = AdjustCellFieldOffset(_fields.FindFieldOffset(layout, "fWaterHeight", "TESObjectCELL"));
        var worldspaceOffset = AdjustCellFieldOffset(_fields.FindFieldOffset(layout, "pWorldSpace", "TESObjectCELL"));
        var landOffset = AdjustCellFieldOffset(_fields.FindFieldOffset(layout, "pCellLand", "TESObjectCELL"));
        var referenceListOffset =
            AdjustCellFieldOffset(_fields.FindFieldOffset(layout, "listReferences", "TESObjectCELL"));

        var flags = flagsOffset.HasValue && flagsOffset.Value < buffer.Length
            ? buffer[flagsOffset.Value]
            : (byte)0;

        // pLightingTemplate — BGSLightingTemplate pointer (FormType 0x67)
        var lightingTemplateOffset =
            AdjustCellFieldOffset(_fields.FindFieldOffset(layout, "pLightingTemplate", "TESObjectCELL"));
        var lightingTemplateFormId = lightingTemplateOffset.HasValue
            ? _fields.ReadPointerToFormId(buffer, lightingTemplateOffset.Value, 0x67)
            : null;

        // iLightingTemplateInheritanceFlags (uint32)
        var inheritFlagsOffset = AdjustCellFieldOffset(
            _fields.FindFieldOffset(layout, "iLightingTemplateInheritanceFlags", "TESObjectCELL"));
        uint? lightingInheritanceFlags = inheritFlagsOffset.HasValue && inheritFlagsOffset.Value + 4 <= buffer.Length
            ? RuntimePdbFieldAccessor.ReadUInt32(buffer, inheritFlagsOffset.Value)
            : null;

        // Walk the BSExtraData linked list for encounter zone, music, acoustic, image space
        var cellExtras = ReadCellExtraData(buffer, layout);

        return new RuntimeCellProbeSnapshot(
            formId,
            NormalizeString(displayName)
            ?? _fields.ReadBsString(fileOffset, layout, "cFullName", "TESFullName"),
            flags,
            ReadNormalFloat(buffer, waterHeightOffset),
            worldspaceOffset.HasValue
                ? _fields.ReadPointerToFormId(buffer, worldspaceOffset.Value, 0x41)
                : null,
            landOffset.HasValue
                ? _fields.ReadPointerToFormId(buffer, landOffset.Value)
                : null,
            referenceListOffset.HasValue
                ? ReadCellReferenceFormIds(buffer, referenceListOffset.Value)
                : [],
            lightingTemplateFormId,
            lightingInheritanceFlags,
            cellExtras.EncounterZoneFormId,
            cellExtras.MusicTypeFormId,
            cellExtras.AcousticSpaceFormId,
            cellExtras.ImageSpaceFormId);
    }

    private static CellRecord? BuildCellRecord(
        RuntimeCellProbeSnapshot? snapshot,
        long fileOffset,
        string? editorId,
        string? displayName)
    {
        if (snapshot == null)
        {
            return null;
        }

        return new CellRecord
        {
            FormId = snapshot.FormId,
            EditorId = NormalizeString(editorId),
            FullName = snapshot.FullName ?? NormalizeString(displayName),
            Flags = snapshot.Flags,
            WaterHeight = snapshot.WaterHeight,
            WorldspaceFormId = snapshot.WorldspaceFormId,
            LightingTemplateFormId = snapshot.LightingTemplateFormId,
            LightingTemplateInheritanceFlags = snapshot.LightingTemplateInheritanceFlags,
            EncounterZoneFormId = snapshot.EncounterZoneFormId,
            MusicTypeFormId = snapshot.MusicTypeFormId,
            AcousticSpaceFormId = snapshot.AcousticSpaceFormId,
            ImageSpaceFormId = snapshot.ImageSpaceFormId,
            Offset = fileOffset,
            IsBigEndian = true
        };
    }

    private List<uint> ReadCellReferenceFormIds(byte[] cellBuffer, int listHeadOffset)
    {
        if (listHeadOffset + 8 > cellBuffer.Length)
        {
            return [];
        }

        var formIds = _fields.ReadFormIdSimpleList(cellBuffer, listHeadOffset);
        if (formIds.Count <= 1)
        {
            return formIds;
        }

        var seen = new HashSet<uint>();
        var deduped = new List<uint>(formIds.Count);
        foreach (var formId in formIds)
        {
            if (formId != 0 && seen.Add(formId))
            {
                deduped.Add(formId);
            }
        }

        return deduped;
    }

    /// <summary>
    ///     Read struct buffer using VA-based region validation when available.
    /// </summary>
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

    private (byte? Flags, ushort? ParentUseFlags, uint? ImageSpaceFormId, uint? MusicTypeFormId,
        float? MapOffsetScaleX, float? MapOffsetScaleY, float? MapOffsetZ) ReadWorldExtendedFields(
            byte[] buffer, PdbTypeLayout layout)
    {
        // cFlags (uint8) — worldspace flags
        var flagsOffset = AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "cFlags", "TESWorldSpace"));
        byte? flags = flagsOffset.HasValue && flagsOffset.Value < buffer.Length
            ? buffer[flagsOffset.Value]
            : null;

        // sParentUseFlags (uint16) — parent inheritance flags
        var parentUseFlagsOffset =
            AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "sParentUseFlags", "TESWorldSpace"));
        ushort? parentUseFlags = parentUseFlagsOffset.HasValue && parentUseFlagsOffset.Value + 2 <= buffer.Length
            ? RuntimePdbFieldAccessor.ReadUInt16(buffer, parentUseFlagsOffset.Value)
            : null;

        // pImageSpace — TESImageSpace pointer (FormType 0x56)
        var imageSpaceFormId = ReadWorldFormIdPointer(buffer, layout, "pImageSpace", "TESWorldSpace", 0x56);

        // pMusicType — BGSMusicType pointer (FormType 0x6B)
        var musicTypeFormId = ReadWorldFormIdPointer(buffer, layout, "pMusicType", "TESWorldSpace", 0x6B);

        // WorldMapOffsetData (12 bytes: 3 floats — scaleX, scaleY, offsetZ)
        var offsetDataOffset =
            AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, "WorldMapOffsetData", "TESWorldSpace"));
        float? mapOffsetScaleX = null;
        float? mapOffsetScaleY = null;
        float? mapOffsetZ = null;

        if (offsetDataOffset.HasValue && offsetDataOffset.Value + 12 <= buffer.Length)
        {
            mapOffsetScaleX = BinaryUtils.ReadFloatBE(buffer, offsetDataOffset.Value);
            mapOffsetScaleY = BinaryUtils.ReadFloatBE(buffer, offsetDataOffset.Value + 4);
            mapOffsetZ = BinaryUtils.ReadFloatBE(buffer, offsetDataOffset.Value + 8);

            // All zeros = unpopulated
            if (mapOffsetScaleX == 0 && mapOffsetScaleY == 0 && mapOffsetZ == 0)
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
        var fieldOffset = AdjustWorldFieldOffset(_fields.FindFieldOffset(layout, name, owner));
        return fieldOffset.HasValue
            ? _fields.ReadPointerToFormId(buffer, fieldOffset.Value, expectedFormType)
            : null;
    }

    /// <summary>
    ///     Walk the BSExtraData linked list from a CELL's ExtraDataList and extract
    ///     encounter zone, music type, acoustic space, and image space FormIDs.
    /// </summary>
    private (uint? EncounterZoneFormId, uint? MusicTypeFormId, uint? AcousticSpaceFormId, uint? ImageSpaceFormId)
        ReadCellExtraData(byte[] cellBuffer, PdbTypeLayout layout)
    {
        // ExtraDataList is an embedded struct in TESObjectCELL; pHead is at +4 within it.
        var extraDataOffset = AdjustCellFieldOffset(
            _fields.FindFieldOffset(layout, "ExtraData", "TESObjectCELL"));
        if (!extraDataOffset.HasValue || extraDataOffset.Value + 8 > cellBuffer.Length)
        {
            return (null, null, null, null);
        }

        // pHead is at ExtraDataList+4 (first 4 bytes are vfptr)
        var pHead = BinaryUtils.ReadUInt32BE(cellBuffer, extraDataOffset.Value + 4);
        if (pHead == 0 || !_context.IsValidPointer(pHead))
        {
            return (null, null, null, null);
        }

        uint? encounterZoneFormId = null;
        uint? musicTypeFormId = null;
        uint? acousticSpaceFormId = null;
        uint? imageSpaceFormId = null;

        var visited = new HashSet<uint>();
        var currentVa = pHead;

        for (var i = 0; i < MaxCellExtraListNodes; i++)
        {
            if (currentVa == 0 || !visited.Add(currentVa))
            {
                break;
            }

            var nodeFileOffset = _context.VaToFileOffset(currentVa);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, CellExtraNodeReadSize);
            if (nodeBuffer == null)
            {
                break;
            }

            var eType = nodeBuffer[CellExtraEtypeOffset];
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraNextOffset);

            switch (eType)
            {
                case ExtraEncounterZoneCode:
                {
                    var zoneVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    encounterZoneFormId ??= _context.FollowPointerVaToFormId(zoneVa, 0x61);
                    break;
                }
                case ExtraCellMusicTypeCode:
                {
                    var musicVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    musicTypeFormId ??= _context.FollowPointerVaToFormId(musicVa, 0x6B);
                    break;
                }
                case ExtraCellAcousticSpaceCode:
                {
                    var acousticVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    acousticSpaceFormId ??= _context.FollowPointerVaToFormId(acousticVa);
                    break;
                }
                case ExtraCellImageSpaceCode:
                {
                    var imageVa = BinaryUtils.ReadUInt32BE(nodeBuffer, CellExtraPayloadOffset);
                    imageSpaceFormId ??= _context.FollowPointerVaToFormId(imageVa, 0x56);
                    break;
                }
            }

            // Early exit if all four found
            if (encounterZoneFormId.HasValue && musicTypeFormId.HasValue &&
                acousticSpaceFormId.HasValue && imageSpaceFormId.HasValue)
            {
                break;
            }

            currentVa = nextVa;
        }

        return (encounterZoneFormId, musicTypeFormId, acousticSpaceFormId, imageSpaceFormId);
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

    private static bool IsExpectedForm(byte[] buffer, byte expectedFormType, uint expectedFormId)
    {
        if (buffer.Length < 16 || buffer[4] != expectedFormType)
        {
            return false;
        }

        return BinaryUtils.ReadUInt32BE(buffer, 12) == expectedFormId && expectedFormId != 0;
    }

    private static string? NormalizeString(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static float? ReadNormalFloat(byte[] buffer, int? offset)
    {
        if (!offset.HasValue || offset.Value + 4 > buffer.Length)
        {
            return null;
        }

        var value = RuntimePdbFieldAccessor.ReadFloat(buffer, offset.Value);
        return RuntimeMemoryContext.IsNormalFloat(value) ? value : null;
    }

    internal sealed record RuntimeCellProbeSnapshot(
        uint FormId,
        string? FullName,
        byte Flags,
        float? WaterHeight,
        uint? WorldspaceFormId,
        uint? LandFormId,
        IReadOnlyList<uint> ReferenceFormIds,
        uint? LightingTemplateFormId = null,
        uint? LightingTemplateInheritanceFlags = null,
        uint? EncounterZoneFormId = null,
        uint? MusicTypeFormId = null,
        uint? AcousticSpaceFormId = null,
        uint? ImageSpaceFormId = null);

    #region TESWorldSpace Struct Layout

    // Final PDB: TESWorldSpace = 244 bytes.
    private const int WorldStructSize = 244;
    private const int WorldShiftStartOffset = 64;

    private int WorldCellMapPtrOffset => 64 + _layout.WorldShift;
    private int WorldPersistentCellPtrOffset => 68 + _layout.WorldShift;
    private int WorldParentWorldPtrOffset => 128 + _layout.WorldShift;

    #endregion

    #region TESObjectCELL Struct Layout

    private const int CellStructSize = 192;
    private const int CellShiftStartOffset = 52;

    #endregion

    #region BSExtraData Linked List (Cell ExtraDataList at +56)

    // BSExtraData node layout: vfptr(4) + cEtype(1) + pad(3) + pNext(4) = 12 bytes header
    private const int CellExtraEtypeOffset = 4;
    private const int CellExtraNextOffset = 8;
    private const int CellExtraPayloadOffset = 12; // pointer payload starts after header
    private const int CellExtraNodeReadSize = 16; // header(12) + pointer(4)
    private const int MaxCellExtraListNodes = 64;

    // BSExtraData type codes for CELL-specific extras (from NVSE GameExtraData.h)
    private const byte ExtraCellMusicTypeCode = 0x07; // 7
    private const byte ExtraCellImageSpaceCode = 0x59; // 89
    private const byte ExtraEncounterZoneCode = 0x74; // 116
    private const byte ExtraCellAcousticSpaceCode = 0x81; // 129

    #endregion

    #region NiTPointerMap Layout (heap-allocated, no shift — standalone data structure)

    // NiTPointerMap<int, TESObjectCELL*>: vfptr(4) + hashSize(4) + pBuckets(4) + count(4) = 16 bytes
    private const int MapHashSizeOffset = 4;
    private const int MapBucketArrayPtrOffset = 8;
    private const int MapEntryCountOffset = 12;
    private const int MapHeaderSize = 16;

    // NiTMapItem<int, TESObjectCELL*>: pNext(4) + key(4) + val(4) = 12 bytes
    private const int ItemKeyOffset = 4;
    private const int ItemValueOffset = 8;
    private const int ItemSize = 12;

    private const int MaxBuckets = 4096;
    private const int MaxChainDepth = 200;

    #endregion
}
