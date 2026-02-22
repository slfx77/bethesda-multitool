using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for TESWorldSpace and TESObjectCELL runtime structs from Xbox 360 memory dumps.
///     Walks the worldspace's pCellMap (NiTPointerMap&lt;int, TESObjectCELL*&gt;) hash table
///     to extract cell-to-grid mappings and persistent cell identification.
///     WRLD and CELL do NOT inherit from TESChildCell, so the early-era -4 REFR shift
///     does not apply. Uses final offsets for both eras until early WRLD/CELL layout is
///     confirmed via Ghidra analysis.
/// </summary>
internal sealed class RuntimeCellReader(RuntimeMemoryContext context, bool useProtoOffsets = false)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly int _shift = RuntimeBuildOffsets.GetWorldCellFieldShift(useProtoOffsets);

    #region TESWorldSpace Struct Layout

    // Final PDB: TESWorldSpace = 244 bytes, TESForm = 40 bytes
    // WRLD/CELL don't inherit TESChildCell — no shift needed for early builds.
    private const int WorldStructSize = 244;

    // Field offsets (final layout — same for early builds until confirmed otherwise)
    private int WorldCellMapPtrOffset => 64 + _shift;
    private int WorldPersistentCellPtrOffset => 68 + _shift;
    private int WorldParentWorldPtrOffset => 128 + _shift;

    #endregion

    #region TESObjectCELL Struct Layout

    // Final PDB: TESObjectCELL = 192 bytes
    // FormID is always at offset 12 (TESForm header, no shift)
    private int CellFlagsOffset => 52 + _shift;
    private int CellLandPtrOffset => 92 + _shift;
    private int CellWorldSpacePtrOffset => 160 + _shift;

    #endregion

    #region NiTPointerMap Layout (heap-allocated, no shift — standalone data structure)

    // NiTPointerMap<int, TESObjectCELL*>: vfptr(4) + hashSize(4) + pBuckets(4) + count(4) = 16 bytes
    private const int MapHashSizeOffset = 4;
    private const int MapBucketArrayPtrOffset = 8;
    private const int MapEntryCountOffset = 12;
    private const int MapHeaderSize = 16;

    // NiTMapItem<int, TESObjectCELL*>: pNext(4) + key(4) + val(4) = 12 bytes
    private const int ItemNextOffset = 0;
    private const int ItemKeyOffset = 4;
    private const int ItemValueOffset = 8;
    private const int ItemSize = 12;

    private const int MaxBuckets = 4096;
    private const int MaxChainDepth = 200;

    #endregion

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
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        // Read enough of the worldspace struct for the fields we need
        var readSize = Math.Min(WorldStructSize, WorldPersistentCellPtrOffset + 4);
        var buffer = ReadStructBuffer(entry, readSize);
        if (buffer == null)
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
            ParentWorldFormId = parentWorldFormId,
            Cells = cells
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

            var nextVa = BinaryUtils.ReadUInt32BE(itemBuffer, ItemNextOffset);
            var key = (int)BinaryUtils.ReadUInt32BE(itemBuffer, ItemKeyOffset);
            var cellVa = BinaryUtils.ReadUInt32BE(itemBuffer, ItemValueOffset);

            // Decode grid coordinates from packed key
            var gridX = key >> 16;           // Arithmetic shift preserves sign
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

        // Read enough for all cell fields we need (up to pWorldSpace at +160)
        var readSize = CellWorldSpacePtrOffset + 4;
        if (!_context.MinidumpInfo.IsVaRangeCaptured(cellVaLong, readSize))
        {
            return null;
        }

        var cellBuffer = _context.ReadBytesAtVa(cellVaLong, readSize);
        if (cellBuffer == null)
        {
            return null;
        }

        // Validate FormType (0x39 = CELL)
        var formType = cellBuffer[4];
        if (formType != 0x39)
        {
            return null;
        }

        var cellFormId = BinaryUtils.ReadUInt32BE(cellBuffer, 12);
        if (cellFormId == 0)
        {
            return null;
        }

        var cellFlags = cellBuffer[CellFlagsOffset];
        var isInterior = (cellFlags & 0x01) != 0;

        // Follow pCellLand → LAND FormID (optional, may fail)
        uint? landFormId = null;
        if (CellLandPtrOffset + 4 <= readSize)
        {
            landFormId = _context.FollowPointerToFormId(cellBuffer, CellLandPtrOffset);
        }

        // Follow pWorldSpace → worldspace FormID (optional)
        uint? worldspaceFormId = null;
        if (CellWorldSpacePtrOffset + 4 <= readSize)
        {
            worldspaceFormId = _context.FollowPointerToFormId(cellBuffer, CellWorldSpacePtrOffset, 0x41);
        }

        return new RuntimeCellMapEntry
        {
            CellFormId = cellFormId,
            GridX = gridX,
            GridY = gridY,
            IsInterior = isInterior,
            WorldspaceFormId = worldspaceFormId,
            LandFormId = landFormId
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
}
