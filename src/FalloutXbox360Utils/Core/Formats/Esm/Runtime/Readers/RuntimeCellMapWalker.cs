using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     Walks NiTPointerMap&lt;int, TESObjectCELL*&gt; hash tables from TESWorldSpace runtime structs.
///     Traverses the bucket array and linked lists to extract cell-to-grid mappings.
/// </summary>
internal sealed class RuntimeCellMapWalker(
    RuntimeMemoryContext context,
    RuntimeCellObjectEnumerator cellEnumerator)
{
    private readonly RuntimeCellObjectEnumerator _cellEnumerator = cellEnumerator;
    private readonly RuntimeMemoryContext _context = context;

    /// <summary>
    ///     Follow the pCellMap pointer from the worldspace struct, then walk the
    ///     NiTPointerMap&lt;int, TESObjectCELL*&gt; hash table at the pointed-to location.
    /// </summary>
    internal List<RuntimeCellMapEntry> WalkCellMap(byte[] worldBuffer, int cellMapPtrOffset)
    {
        var cells = new List<RuntimeCellMapEntry>();

        // pCellMap is a POINTER (4 bytes) — follow it to the heap-allocated NiTPointerMap
        if (cellMapPtrOffset + 4 > worldBuffer.Length)
        {
            return cells;
        }

        var cellMapVa = BinaryUtils.ReadUInt32BE(worldBuffer, cellMapPtrOffset);
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

        // Read the bucket array (hashSize x 4 bytes of pointers)
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

        var snapshot = _cellEnumerator.ReadRuntimeCellProbeSnapshot(cellOffset.Value, null, null);
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
    internal uint? ReadCellFormIdFromPointer(byte[] buffer, int pointerOffset)
    {
        if (pointerOffset + 4 > buffer.Length)
        {
            return null;
        }

        return _context.FollowPointerToFormId(buffer, pointerOffset, 0x39);
    }

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
    private const int CellStructSize = 192;

    #endregion
}
