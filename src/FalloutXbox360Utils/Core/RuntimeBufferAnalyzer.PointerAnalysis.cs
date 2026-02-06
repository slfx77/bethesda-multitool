using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core;

internal sealed partial class RuntimeBufferAnalyzer
{
    #region Pointer Graph Analysis

    /// <summary>
    ///     Analyze pointer-dense gaps to classify data structures.
    /// </summary>
    private void RunPointerGraphAnalysis(BufferExplorationResult result)
    {
        var summary = new PointerGraphSummary();
        var vtableCounts = new Dictionary<uint, int>();

        var pointerGaps = _coverage.Gaps
            .Where(g => g.Classification == GapClassification.PointerDense)
            .ToList();

        summary.TotalPointerDenseGaps = pointerGaps.Count;
        summary.TotalPointerDenseBytes = pointerGaps.Sum(g => g.Size);

        foreach (var gap in pointerGaps)
        {
            var sampleSize = (int)Math.Min(gap.Size, 256);
            sampleSize = sampleSize / 4 * 4; // Align to 4 bytes
            if (sampleSize < 4)
            {
                continue;
            }

            var buffer = new byte[sampleSize];
            _accessor.ReadArray(gap.FileOffset, buffer, 0, sampleSize);

            var vtableCount = 0;
            var heapCount = 0;
            var nullCount = 0;
            var slots = sampleSize / 4;

            for (var i = 0; i < sampleSize; i += 4)
            {
                var val = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));

                if (val == 0)
                {
                    nullCount++;
                    continue;
                }

                if (val >= _moduleStart && val < _moduleEnd)
                {
                    vtableCount++;
                    vtableCounts.TryGetValue(val, out var c);
                    vtableCounts[val] = c + 1;
                    summary.TotalVtablePointersFound++;
                }
                else if (IsValidPointer(val))
                {
                    heapCount++;
                }
            }

            // Classify gap based on pointer distribution
            if (vtableCount > 0 && vtableCount >= slots * 0.15)
            {
                summary.ObjectArrayGaps++;
            }
            else if (heapCount > slots * 0.4 && nullCount > slots * 0.15)
            {
                summary.HashTableGaps++;
            }
            else if (heapCount > slots * 0.5)
            {
                summary.LinkedListGaps++;
            }
            else
            {
                summary.MixedStructureGaps++;
            }
        }

        // Top vtable addresses (most frequently referenced)
        foreach (var (addr, count) in vtableCounts.OrderByDescending(kv => kv.Value).Take(10))
        {
            summary.TopVtableAddresses[addr] = count;
        }

        result.PointerGraph = summary;
    }

    #endregion

    #region Pointer Utilities

    /// <summary>
    ///     Count valid pointers in a buffer (for structure analysis).
    /// </summary>
    private int CountValidPointers(byte[] buffer)
    {
        var count = 0;
        for (var i = 0; i <= buffer.Length - 4; i += 4)
        {
            var ptr = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
            if (ptr != 0 && IsValidPointer(ptr))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    ///     Check if a 32-bit value is a valid pointer in the minidump.
    /// </summary>
    private bool IsValidPointer(uint va)
    {
        return va != 0 && EsmRecordFormat.IsValidPointerInDump(va, _minidumpInfo);
    }

    /// <summary>
    ///     Convert a 32-bit Xbox 360 virtual address to file offset.
    /// </summary>
    private long? VaToFileOffset(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        return _minidumpInfo.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
    }

    #endregion
}
