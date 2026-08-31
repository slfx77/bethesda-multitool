namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

internal enum OpaqueSubmissionLane
{
    Ordinary,
    Decal,
    Grass
}

/// <summary>
///     Stable publication-time ordering for opaque batches. Ordinary geometry is grouped by PSO;
///     order-sensitive decals and the grass-last correctness lane retain their relative order.
/// </summary>
internal static class OpaqueSubmissionOrderPolicy
{
    internal static void Order<T, TGroup>(
        List<T> items,
        List<T> itemScratch,
        List<TGroup> groupScratch,
        Func<T, OpaqueSubmissionLane> laneSelector,
        Func<T, TGroup> groupSelector,
        IEqualityComparer<TGroup> groupComparer,
        IComparer<T>? ordinaryGroupComparer = null)
    {
        if (items.Count < 2)
        {
            return;
        }

        itemScratch.Clear();
        groupScratch.Clear();

        // Preserve first-seen PSO order, and preserve first-touch order within each PSO group.
        // This makes a second ordering pass idempotent and avoids an unstable comparison sort.
        foreach (var item in items)
        {
            if (laneSelector(item) != OpaqueSubmissionLane.Ordinary)
            {
                continue;
            }

            var group = groupSelector(item);
            var seen = false;
            foreach (var existing in groupScratch)
            {
                if (!groupComparer.Equals(existing, group)) continue;
                seen = true;
                break;
            }

            if (!seen)
            {
                groupScratch.Add(group);
            }
        }

        foreach (var group in groupScratch)
        {
            var groupStart = itemScratch.Count;
            foreach (var item in items)
            {
                if (laneSelector(item) == OpaqueSubmissionLane.Ordinary &&
                    groupComparer.Equals(groupSelector(item), group))
                {
                    itemScratch.Add(item);
                }
            }

            var groupCount = itemScratch.Count - groupStart;
            if (ordinaryGroupComparer is not null && groupCount > 1)
            {
                // The caller supplies an original-ordinal tiebreaker, making this total order stable
                // even though List.Sort itself is not stable. Only this contiguous PSO group moves.
                itemScratch.Sort(groupStart, groupCount, ordinaryGroupComparer);
            }
        }

        AppendLane(items, itemScratch, laneSelector, OpaqueSubmissionLane.Decal);
        AppendLane(items, itemScratch, laneSelector, OpaqueSubmissionLane.Grass);

        if (itemScratch.Count != items.Count)
        {
            throw new InvalidOperationException("Opaque submission ordering lost a batch.");
        }

        for (var i = 0; i < items.Count; i++)
        {
            items[i] = itemScratch[i];
        }

        itemScratch.Clear();
        groupScratch.Clear();
    }

    private static void AppendLane<T>(
        List<T> items,
        List<T> scratch,
        Func<T, OpaqueSubmissionLane> laneSelector,
        OpaqueSubmissionLane lane)
    {
        foreach (var item in items)
        {
            if (laneSelector(item) == lane)
            {
                scratch.Add(item);
            }
        }
    }
}
