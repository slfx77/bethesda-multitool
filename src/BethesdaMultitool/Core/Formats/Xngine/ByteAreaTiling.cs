namespace BethesdaMultitool.Core.Formats.Xngine;

/// <summary>One named half-open byte range <c>[Start, End)</c> inside a record or file.</summary>
/// <param name="Name">The area's name (for example <c>points</c>, <c>plane-data</c>, <c>frame:3:normals</c>).</param>
/// <param name="Start">First byte of the area.</param>
/// <param name="End">One past the last byte of the area.</param>
internal readonly record struct ByteArea(string Name, int Start, int End)
{
    /// <summary>Bytes in the area (zero for an empty area, negative when <see cref="End" /> precedes <see cref="Start" />).</summary>
    public int Length => End - Start;
}

/// <summary>
///     Two declared areas that claim the same bytes: <paramref name="Name" /> starts at
///     <paramref name="Start" /> although the areas before it in start order already cover the bytes up
///     to <paramref name="CoveredTo" />.
/// </summary>
/// <param name="Name">The later area's name.</param>
/// <param name="Start">Where the later area starts.</param>
/// <param name="CoveredTo">How far the earlier areas reach; the bytes <c>[Start, min(End, CoveredTo))</c> are claimed twice.</param>
internal readonly record struct ByteAreaOverlap(string Name, int Start, int CoveredTo);

/// <summary>
///     Tiles a byte length with the areas a record declares and reports what is left: every uncovered
///     range as its own <c>unclaimed:{start}-{end}</c> area, every overlap, and every area that does not
///     fit the length. This is the area-tiling helper the cut-1c plan names (sections 3.3, 4 and 8, slice 4):
///     an <c>XnGineMesh</c>'s header areas tile its record with the residue defining <c>unclaimed:*</c>, and a
///     <c>Redguard3DcFile</c>'s frame blocks tile its file leaving the one <c>unaccounted</c> region.
///     <para>
///         The rule is the gate-1c oracle's M-T tiling (<c>tools/scripts/gate1c/xngine_probe.py</c>,
///         <c>area_tiling</c>), restated: areas are visited in start order with a cursor from 0; an area
///         that starts past the cursor leaves a gap, one that starts before it overlaps, and the cursor
///         advances to the furthest end seen; bytes after the last area are a trailing gap. An empty area
///         (<see cref="ByteArea.Length" /> 0) claims nothing and is neither a gap nor an overlap. An area that
///         starts before 0, ends past the length or ends before it starts is reported under
///         <see cref="OutOfRange" /> and takes no part in the walk.
///     </para>
///     <para>
///         Measured 2026-09-28 by the oracle over the 19,725 static retail meshes: header, point list,
///         normal list, walked plane list and plane-data area tile with ZERO overlaps on every one, and
///         every mesh but the 3 ARCH3D records with an object-data count of 0 leaves exactly one gap, always
///         ending at the end of the record (slice-2 receipt <c>m_t_tiling.json</c>).
///     </para>
/// </summary>
internal sealed class ByteAreaTiling
{
    /// <summary>The name prefix of a gap area: <c>unclaimed:{start}-{end}</c>.</summary>
    public const string UnclaimedPrefix = "unclaimed:";

    private ByteAreaTiling(
        int length,
        IReadOnlyList<ByteArea> areas,
        IReadOnlyList<ByteArea> gaps,
        IReadOnlyList<ByteAreaOverlap> overlaps,
        IReadOnlyList<ByteArea> outOfRange)
    {
        Length = length;
        Areas = areas;
        Gaps = gaps;
        Overlaps = overlaps;
        OutOfRange = outOfRange;
    }

    /// <summary>The byte length that was tiled.</summary>
    public int Length { get; }

    /// <summary>Every declared area that fits, in start order (ties by end, then by name).</summary>
    public IReadOnlyList<ByteArea> Areas { get; }

    /// <summary>The uncovered ranges in file order, each named <c>unclaimed:{start}-{end}</c>.</summary>
    public IReadOnlyList<ByteArea> Gaps { get; }

    /// <summary>Every area that starts before the areas ahead of it have ended.</summary>
    public IReadOnlyList<ByteAreaOverlap> Overlaps { get; }

    /// <summary>Every declared area that does not fit inside <c>[0, Length)</c> or ends before it starts.</summary>
    public IReadOnlyList<ByteArea> OutOfRange { get; }

    /// <summary>Bytes covered by no area.</summary>
    public int UnclaimedBytes
    {
        get
        {
            var total = 0;
            foreach (var gap in Gaps)
            {
                total += gap.Length;
            }

            return total;
        }
    }

    /// <summary>True when the areas cover the length exactly: no gap, no overlap, nothing out of range.</summary>
    public bool TilesExactly => Gaps.Count == 0 && Overlaps.Count == 0 && OutOfRange.Count == 0;

    /// <summary>Tiles <paramref name="length" /> bytes with <paramref name="areas" />.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length" /> is negative.</exception>
    public static ByteAreaTiling Compute(int length, IEnumerable<ByteArea> areas)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentNullException.ThrowIfNull(areas);

        var fitting = new List<ByteArea>();
        var outOfRange = new List<ByteArea>();
        foreach (var area in areas)
        {
            if (area.Start < 0 || area.End > length || area.End < area.Start)
            {
                outOfRange.Add(area);
            }
            else
            {
                fitting.Add(area);
            }
        }

        fitting.Sort(static (a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            if (byStart != 0)
            {
                return byStart;
            }

            var byEnd = a.End.CompareTo(b.End);
            return byEnd != 0 ? byEnd : string.CompareOrdinal(a.Name, b.Name);
        });

        var gaps = new List<ByteArea>();
        var overlaps = new List<ByteAreaOverlap>();
        var cursor = 0;
        foreach (var area in fitting)
        {
            if (area.Length == 0)
            {
                continue;
            }

            if (area.Start > cursor)
            {
                gaps.Add(Gap(cursor, area.Start));
            }
            else if (area.Start < cursor)
            {
                overlaps.Add(new ByteAreaOverlap(area.Name, area.Start, cursor));
            }

            cursor = Math.Max(cursor, area.End);
        }

        if (cursor < length)
        {
            gaps.Add(Gap(cursor, length));
        }

        return new ByteAreaTiling(length, fitting, gaps, overlaps, outOfRange);
    }

    private static ByteArea Gap(int start, int end)
    {
        return new ByteArea($"{UnclaimedPrefix}{start}-{end}", start, end);
    }
}
