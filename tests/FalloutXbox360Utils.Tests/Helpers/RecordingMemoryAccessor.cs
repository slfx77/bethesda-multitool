using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;

namespace FalloutXbox360Utils.Tests.Helpers;

/// <summary>
///     Wraps a <see cref="MemoryMappedViewAccessor" /> and records every <c>ReadArray</c> call
///     (file offset + length). Used by <see cref="DmpSnippetExtractor" /> to capture the sparse
///     byte ranges that tests actually access.
/// </summary>
internal sealed class RecordingMemoryAccessor(MemoryMappedViewAccessor inner) : IMemoryAccessor
{
    private readonly Lock _lock = new();
    private readonly List<(long Offset, int Count)> _reads = [];

    public IReadOnlyList<(long Offset, int Count)> GetRecordedReads()
    {
        lock (_lock)
        {
            return _reads.ToList();
        }
    }

    public int ReadArray(long position, byte[] array, int offset, int count)
    {
        lock (_lock)
        {
            _reads.Add((position, count));
        }

        return inner.ReadArray(position, array, offset, count);
    }

    /// <summary>
    ///     Coalesce overlapping/adjacent recorded ranges into a minimal set,
    ///     adding <paramref name="padding" /> bytes around each range.
    /// </summary>
    public List<(long Offset, int Length)> GetCoalescedRanges(long fileSize, int padding = 64)
    {
        lock (_lock)
        {
            if (_reads.Count == 0)
            {
                return [];
            }

            // Expand each range by padding, clamp to file size
            var expanded = _reads
                .Select(r =>
                {
                    var start = Math.Max(0, r.Offset - padding);
                    var end = Math.Min(fileSize, r.Offset + r.Count + padding);
                    return (Start: start, End: end);
                })
                .OrderBy(r => r.Start)
                .ToList();

            // Merge overlapping ranges
            var merged = new List<(long Offset, int Length)>();
            var (curStart, curEnd) = expanded[0];

            for (var i = 1; i < expanded.Count; i++)
            {
                if (expanded[i].Start <= curEnd)
                {
                    curEnd = Math.Max(curEnd, expanded[i].End);
                }
                else
                {
                    merged.Add((curStart, (int)(curEnd - curStart)));
                    (curStart, curEnd) = expanded[i];
                }
            }

            merged.Add((curStart, (int)(curEnd - curStart)));
            return merged;
        }
    }
}
