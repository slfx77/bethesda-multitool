using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Shared infrastructure for scanning memory dumps for runtime C++ objects.
///     Parallelizes across ContiguousRegionGroups, uses 16-byte alignment
///     (Xbox 360 XMemAlloc heap alignment), and excludes module code ranges.
///     Used by RuntimeGeometryScanner (NiTriShapeData/NiTriStripsData) and future
///     scanners for NiPixelData, XAudio2 voice objects, etc.
/// </summary>
internal sealed class RuntimeObjectScanner(RuntimeMemoryContext context)
{
    /// <summary>Xbox 360 XMemAlloc guarantees 16-byte aligned heap allocations.</summary>
    private const int HeapAlignment = 16;

    /// <summary>Chunk size for scanning within a region group.</summary>
    private const int ChunkSize = 4 * 1024 * 1024;

    /// <summary>Overlap between chunks to catch structs straddling chunk boundaries.</summary>
    private const int ChunkOverlap = 256;

    private readonly RuntimeMemoryContext _context = context;

    /// <summary>
    ///     Scan the dump in parallel across contiguous memory region groups.
    ///     Applies candidateTest at each 16-byte aligned heap offset, skipping module code ranges.
    ///     When candidateTest returns true, processCandidate is invoked.
    ///     Returns the total number of candidates processed (for diagnostics).
    /// </summary>
    /// <param name="candidateTest">Fast filter: returns true if offset looks like a candidate.</param>
    /// <param name="processCandidate">
    ///     Called for each candidate. Must be thread-safe — may be called concurrently.
    ///     Parameters: (chunk buffer, offset within chunk, absolute file offset).
    /// </param>
    /// <param name="minStructSize">Minimum bytes needed from offset to validate a struct.</param>
    /// <param name="progress">Optional progress reporter (scanned bytes, total bytes).</param>
    public void ScanAligned(
        Func<byte[], int, bool> candidateTest,
        Action<byte[], int, long> processCandidate,
        int minStructSize = 88,
        IProgress<(long Scanned, long Total)>? progress = null)
    {
        var minidump = _context.MinidumpInfo;
        var log = Logger.Instance;

        // If not a valid minidump, fall back to sequential full-file scan
        if (minidump == null || !minidump.IsValid || minidump.MemoryRegions.Count == 0)
        {
            log.Debug("RuntimeObjectScanner: no minidump regions, falling back to sequential scan");
            ScanSequential(candidateTest, processCandidate, minStructSize, progress);
            return;
        }

        var regionGroups = minidump.GetContiguousRegionGroups();
        var totalBytes = regionGroups.Sum(g => g.TotalSize);
        var moduleRanges = BuildModuleFileRanges(minidump);
        var bytesScanned = 0L;

        log.Info("RuntimeObjectScanner: {0} region groups, {1:N0} MB scannable, " +
                 "{2} module ranges excluded, {3}-byte alignment",
            regionGroups.Count, totalBytes / (1024 * 1024), moduleRanges.Length, HeapAlignment);

        Parallel.ForEach(regionGroups,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            group =>
            {
                ScanRegionGroup(group, moduleRanges, candidateTest, processCandidate, minStructSize);

                var scanned = Interlocked.Add(ref bytesScanned, group.TotalSize);
                progress?.Report((scanned, totalBytes));
            });
    }

    /// <summary>
    ///     Scan a single contiguous region group in chunks.
    /// </summary>
    private void ScanRegionGroup(
        ContiguousRegionGroup group,
        (long start, long end)[] moduleRanges,
        Func<byte[], int, bool> candidateTest,
        Action<byte[], int, long> processCandidate,
        int minStructSize)
    {
        foreach (var region in group.Regions)
        {
            ScanRegion(region, moduleRanges, candidateTest, processCandidate, minStructSize);
        }
    }

    /// <summary>
    ///     Scan a single memory region in chunks with 16-byte aligned offsets.
    /// </summary>
    private void ScanRegion(
        MinidumpMemoryRegion region,
        (long start, long end)[] moduleRanges,
        Func<byte[], int, bool> candidateTest,
        Action<byte[], int, long> processCandidate,
        int minStructSize)
    {
        var regionFileStart = region.FileOffset;
        var regionSize = region.Size;

        for (long chunkStart = 0; chunkStart < regionSize; chunkStart += ChunkSize)
        {
            var readSize = (int)Math.Min(ChunkSize + ChunkOverlap, regionSize - chunkStart);
            if (readSize < minStructSize)
            {
                break;
            }

            var fileOffset = regionFileStart + chunkStart;
            var chunk = _context.ReadBytes(fileOffset, readSize);
            if (chunk == null)
            {
                continue;
            }

            var scanLimit = Math.Min(ChunkSize, readSize - minStructSize);

            for (var offset = 0; offset < scanLimit; offset += HeapAlignment)
            {
                var absoluteFileOffset = fileOffset + offset;

                if (IsInModuleRange(absoluteFileOffset, moduleRanges))
                {
                    continue;
                }

                if (candidateTest(chunk, offset))
                {
                    processCandidate(chunk, offset, absoluteFileOffset);
                }
            }
        }
    }

    /// <summary>
    ///     Fallback: sequential scan of the entire file (for non-minidump files).
    /// </summary>
    private void ScanSequential(
        Func<byte[], int, bool> candidateTest,
        Action<byte[], int, long> processCandidate,
        int minStructSize,
        IProgress<(long Scanned, long Total)>? progress)
    {
        var fileSize = _context.FileSize;
        var effectiveChunkSize = ChunkSize + ChunkOverlap;

        for (long chunkStart = 0; chunkStart < fileSize; chunkStart += ChunkSize)
        {
            var readSize = (int)Math.Min(effectiveChunkSize, fileSize - chunkStart);
            if (readSize < minStructSize)
            {
                break;
            }

            var chunk = _context.ReadBytes(chunkStart, readSize);
            if (chunk == null)
            {
                continue;
            }

            var scanLimit = Math.Min(ChunkSize, readSize - minStructSize);
            for (var offset = 0; offset < scanLimit; offset += HeapAlignment)
            {
                if (candidateTest(chunk, offset))
                {
                    processCandidate(chunk, offset, chunkStart + offset);
                }
            }

            progress?.Report((chunkStart + readSize, fileSize));
        }
    }

    /// <summary>
    ///     Build sorted array of module file offset ranges for binary search exclusion.
    /// </summary>
    private static (long start, long end)[] BuildModuleFileRanges(MinidumpInfo minidumpInfo)
    {
        var ranges = new List<(long start, long end)>();
        foreach (var module in minidumpInfo.Modules)
        {
            var fileRange = minidumpInfo.GetModuleFileRange(module);
            if (fileRange.HasValue && fileRange.Value.size > 0)
            {
                ranges.Add((fileRange.Value.fileOffset, fileRange.Value.fileOffset + fileRange.Value.size));
            }
        }

        // Sort by start offset for binary search
        ranges.Sort((a, b) => a.start.CompareTo(b.start));
        return ranges.ToArray();
    }

    /// <summary>
    ///     Check if a file offset falls within a module code range using binary search.
    /// </summary>
    private static bool IsInModuleRange(long fileOffset, (long start, long end)[] moduleRanges)
    {
        if (moduleRanges.Length == 0)
        {
            return false;
        }

        // Binary search for the range that might contain this offset
        var lo = 0;
        var hi = moduleRanges.Length - 1;

        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (fileOffset < moduleRanges[mid].start)
            {
                hi = mid - 1;
            }
            else if (fileOffset >= moduleRanges[mid].end)
            {
                lo = mid + 1;
            }
            else
            {
                return true; // fileOffset is within [start, end)
            }
        }

        return false;
    }
}
