using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     An <see cref="IMemoryAccessor" /> backed by a sparse set of captured byte ranges.
///     Reads within captured ranges succeed; reads outside return zeros (matching the behavior
///     of a dump where unmapped regions read as zero).
/// </summary>
internal sealed class SparseMemoryAccessor : IMemoryAccessor
{
    private readonly SortedList<long, byte[]> _ranges = new();

    public int ReadArray(long position, byte[] array, int offset, int count)
    {
        // Find the range that could contain this position
        // Binary search for the largest key <= position
        var keys = _ranges.Keys;
        var idx = BinarySearchFloor(keys, position);

        if (idx >= 0)
        {
            var rangeOffset = keys[idx];
            var rangeData = _ranges.Values[idx];
            var rangeEnd = rangeOffset + rangeData.Length;

            if (position >= rangeOffset && position + count <= rangeEnd)
            {
                // Fully within a captured range
                var srcOffset = (int)(position - rangeOffset);
                Array.Copy(rangeData, srcOffset, array, offset, count);
                return count;
            }
        }

        // Not in a captured range — fill with zeros
        Array.Clear(array, offset, count);
        return count;
    }

    /// <summary>
    ///     Add a captured byte range at the given file offset.
    /// </summary>
    public void AddRange(long offset, byte[] data)
    {
        _ranges[offset] = data;
    }

    private static int BinarySearchFloor(IList<long> keys, long target)
    {
        var lo = 0;
        var hi = keys.Count - 1;
        var result = -1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (keys[mid] <= target)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return result;
    }
}