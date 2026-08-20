namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     CPU-only planning for a back-to-front blended list when the transient draw budget cannot hold
///     every entry. The input order is farthest to nearest; the selected range is therefore a suffix,
///     preserving blend order while giving the nearest drawable entries priority.
/// </summary>
internal static class BlendedDrawPriority
{
    /// <summary>
    ///     Plans the nearest drawable suffix. Zero entries represent authored quiet particle frames;
    ///     they consume no capacity and count as neither selected nor truncated draws.
    /// </summary>
    public static Plan PlanNearestBackToFront(ReadOnlySpan<byte> drawable, int capacity)
    {
        capacity = Math.Max(capacity, 0);
        var firstSelected = drawable.Length;
        var selected = 0;

        for (var i = drawable.Length - 1; i >= 0; i--)
        {
            if (drawable[i] == 0) continue;
            if (selected == capacity)
            {
                var truncated = 1;
                for (var j = 0; j < i; j++)
                {
                    if (drawable[j] != 0) truncated++;
                }

                return new Plan(firstSelected, selected, truncated);
            }

            selected++;
            firstSelected = i;
        }

        return new Plan(firstSelected, selected, 0);
    }

    /// <summary>Exact count of repeated bump allocations that fit after alignment.</summary>
    public static int CountAlignedAllocations(
        uint currentOffset,
        uint totalBytes,
        uint allocationSize,
        uint alignment)
    {
        ArgumentOutOfRangeException.ThrowIfZero(allocationSize);
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a power of two.");
        }

        var mask = (ulong)alignment - 1;
        var first = (currentOffset + mask) & ~mask;
        if (first + allocationSize > totalBytes) return 0;

        var stride = (allocationSize + mask) & ~mask;
        var count = 1UL + ((ulong)totalBytes - allocationSize - first) / stride;
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    internal readonly record struct Plan(int FirstSelected, int SelectedCount, int TruncatedCount);
}
