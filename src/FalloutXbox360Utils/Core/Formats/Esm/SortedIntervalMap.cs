using System.Buffers.Binary;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     A binary-search interval map over non-overlapping GRUP ranges.
///     Provides O(log n) lookup for finding which GRUP contains a given file offset,
///     replacing O(n) linear scans in the Build*Map methods.
/// </summary>
internal readonly struct SortedIntervalMap
{
    private readonly long[] _starts;
    private readonly long[] _ends;
    private readonly byte[][] _labels;

    /// <summary>
    ///     Builds a sorted interval map from filtered GRUP headers.
    ///     The caller should pre-filter by GroupType before constructing.
    /// </summary>
    public SortedIntervalMap(List<GrupHeaderInfo> groups)
    {
        if (groups.Count == 0)
        {
            _starts = [];
            _ends = [];
            _labels = [];
            return;
        }

        groups.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        _starts = new long[groups.Count];
        _ends = new long[groups.Count];
        _labels = new byte[groups.Count][];

        for (var i = 0; i < groups.Count; i++)
        {
            _starts[i] = groups[i].Offset;
            _ends[i] = groups[i].Offset + groups[i].GroupSize;
            _labels[i] = groups[i].Label;
        }
    }

    public int Count => _starts?.Length ?? 0;

    /// <summary>
    ///     Finds the index of the interval containing the given offset using binary search.
    ///     Uses exclusive bounds (offset must be strictly inside the interval) to match
    ///     the original Build*Map logic where records start after the GRUP header.
    ///     Returns -1 if no interval contains the offset.
    /// </summary>
    public int FindContainingInterval(long offset)
    {
        if (_starts == null || _starts.Length == 0)
        {
            return -1;
        }

        // Binary search for the rightmost start that is strictly less than offset
        int lo = 0, hi = _starts.Length - 1;
        var candidate = -1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_starts[mid] < offset)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Verify the candidate interval actually contains the offset
        if (candidate >= 0 && offset < _ends[candidate])
        {
            return candidate;
        }

        return -1;
    }

    /// <summary>
    ///     Gets the GRUP label at the given index interpreted as a little-endian uint32 FormID.
    ///     Labels are already normalized to LE order by ParseGroupHeader.
    /// </summary>
    public uint GetLabelAsFormId(int index)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(_labels[index]);
    }
}
