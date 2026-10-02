using System.Collections.Concurrent;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Scanning;

/// <summary>Retains one canonical physical occurrence per existing scan deduplication key.</summary>
internal static class RuntimeCandidateSelection
{
    /// <summary>
    ///     Retains the lowest file offset regardless of worker arrival order. This is a stable
    ///     representative, not engine priority. Only one payload per key remains retained.
    ///     Returns true only when adding a previously unseen key, for unique-item progress counts.
    /// </summary>
    internal static bool KeepLowestOffset<T>(
        ConcurrentDictionary<long, T> candidates, long key, T candidate, Func<T, long> getOffset)
        where T : class
    {
        while (true)
        {
            if (candidates.TryGetValue(key, out var current))
            {
                if (getOffset(candidate) >= getOffset(current)) return false;
                if (candidates.TryUpdate(key, candidate, current)) return false;
            }
            else if (candidates.TryAdd(key, candidate))
            {
                return true;
            }
        }
    }
}
