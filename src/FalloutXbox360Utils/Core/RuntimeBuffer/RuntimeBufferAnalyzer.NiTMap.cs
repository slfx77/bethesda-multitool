using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Pdb;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

internal sealed partial class RuntimeBufferAnalyzer
{
    #region NiTMapBase Walking

    /// <summary>
    ///     Walk a NiTMapBase hash table and extract string keys.
    /// </summary>
    private (int hashSize, int entryCount, List<string> strings) WalkNiTMapBaseStrings(
        uint va, int maxEntries = 10_000)
    {
        var strings = new List<string>();

        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + 16 > _fileSize)
        {
            return (0, 0, strings);
        }

        var header = new byte[16];
        _accessor.ReadArray(fileOffset.Value, header, 0, 16);

        var vfptr = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        var hashSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        var bucketArrayVa = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));

        if (hashSize < 2 || hashSize > 1_000_000)
        {
            return (0, 0, strings);
        }

        if (!IsValidPointer(vfptr) || !IsValidPointer(bucketArrayVa))
        {
            return ((int)hashSize, (int)entryCount, strings);
        }

        var bucketFileOffset = VaToFileOffset(bucketArrayVa);
        if (bucketFileOffset == null)
        {
            return ((int)hashSize, (int)entryCount, strings);
        }

        var visited = new HashSet<uint>();
        var extracted = 0;
        var bucketBuf = new byte[4];
        var itemBuf = new byte[12];

        for (uint i = 0; i < hashSize && extracted < maxEntries; i++)
        {
            var bOffset = bucketFileOffset.Value + i * 4;
            if (bOffset + 4 > _fileSize)
            {
                break;
            }

            _accessor.ReadArray(bOffset, bucketBuf, 0, 4);
            var itemVa = BinaryPrimitives.ReadUInt32BigEndian(bucketBuf);

            while (itemVa != 0 && extracted < maxEntries && visited.Add(itemVa))
            {
                if (!IsValidPointer(itemVa))
                {
                    break;
                }

                var itemFileOffset = VaToFileOffset(itemVa);
                if (itemFileOffset == null || itemFileOffset.Value + 12 > _fileSize)
                {
                    break;
                }

                _accessor.ReadArray(itemFileOffset.Value, itemBuf, 0, 12);
                var nextVa = BinaryPrimitives.ReadUInt32BigEndian(itemBuf.AsSpan(0, 4));
                var keyVa = BinaryPrimitives.ReadUInt32BigEndian(itemBuf.AsSpan(4, 4));

                var str = TryReadCString(keyVa);
                if (str != null)
                {
                    strings.Add(str);
                }

                extracted++;
                itemVa = nextVa;
            }
        }

        return ((int)hashSize, (int)entryCount, strings);
    }

    /// <summary>
    ///     Walk a global pointer that points to a NiTMapBase with string keys.
    /// </summary>
    private void WalkMapAsStrings(BufferExplorationResult result, ResolvedGlobal global)
    {
        var (hashSize, entryCount, strings) = WalkNiTMapBaseStrings(global.PointerValue);

        // Even if empty, report if we can confirm it's a real NiTMapBase (valid vfptr)
        if (hashSize == 0)
        {
            var isRealMap = TryConfirmNiTMapBase(global.PointerValue);
            if (!isRealMap)
            {
                return;
            }

            result.ManagerResults.Add(new ManagerWalkResult
            {
                GlobalName = global.Global.Name,
                PointerValue = global.PointerValue,
                TargetType = "NiTMapBase",
                Summary = "empty (not populated at crash time)"
            });
            return;
        }

        var walkResult = new ManagerWalkResult
        {
            GlobalName = global.Global.Name,
            PointerValue = global.PointerValue,
            TargetType = "NiTMapBase",
            WalkableEntries = entryCount,
            Summary = $"hashSize={hashSize:N0}, entries={entryCount:N0}, " +
                      $"extracted {strings.Count:N0} strings"
        };
        walkResult.ExtractedStrings.AddRange(strings.Take(MaxSampleStrings));
        result.ManagerResults.Add(walkResult);
    }

    /// <summary>
    ///     Check if a VA points to a valid NiTMapBase by reading the vfptr.
    /// </summary>
    private bool TryConfirmNiTMapBase(uint va)
    {
        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + 4 > _fileSize)
        {
            return false;
        }

        var buf = new byte[4];
        _accessor.ReadArray(fileOffset.Value, buf, 0, 4);
        var vfptr = BinaryPrimitives.ReadUInt32BigEndian(buf);

        // Valid vfptr should point into module code range
        return vfptr >= _moduleStart && vfptr < _moduleEnd;
    }

    /// <summary>
    ///     Walk a memory pool map (NiTMapBase with pool entries).
    /// </summary>
    private void WalkMemoryPoolMap(BufferExplorationResult result, ResolvedGlobal global)
    {
        var fileOffset = VaToFileOffset(global.PointerValue);
        if (fileOffset == null || fileOffset.Value + 16 > _fileSize)
        {
            return;
        }

        var header = new byte[16];
        _accessor.ReadArray(fileOffset.Value, header, 0, 16);

        var vfptr = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
        var hashSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        var entryCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));

        // Confirm it's a real NiTMapBase via vtable pointer
        var isRealMap = vfptr >= _moduleStart && vfptr < _moduleEnd;
        if (!isRealMap)
        {
            return;
        }

        var summary = hashSize >= 2 && hashSize <= 1_000_000
            ? $"hashSize={hashSize:N0}, pools={entryCount:N0}"
            : "empty (not populated at crash time)";

        result.ManagerResults.Add(new ManagerWalkResult
        {
            GlobalName = global.Global.Name,
            PointerValue = global.PointerValue,
            TargetType = "NiTMapBase (MemoryPool)",
            WalkableEntries = (int)entryCount,
            Summary = summary
        });
    }

    #endregion
}
