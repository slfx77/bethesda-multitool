using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Scans memory-mapped dump files for known file signatures using parallel
///     region-aware scanning. Also provides file size estimation for matched signatures.
/// </summary>
internal sealed class MinidumpFileScanner
{
    private readonly SignatureMatcher _signatureMatcher;

    public MinidumpFileScanner()
    {
        _signatureMatcher = new SignatureMatcher();

        // Register all signatures from the format registry for analysis
        // (includes all formats for visualization, even those with scanning disabled)
        foreach (var format in FormatRegistry.All)
        {
            foreach (var sig in format.Signatures)
            {
                _signatureMatcher.AddPattern(sig.Id, sig.MagicBytes);
            }
        }

        _signatureMatcher.Build();
    }

    /// <summary>
    ///     Find all signature matches using parallel region-aware scanning.
    ///     Processes contiguous memory regions in parallel for better performance.
    /// </summary>
    public List<(string SignatureId, long Offset)> FindAllMatchesParallel(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        IProgress<AnalysisProgress>? progress)
    {
        var log = Logger.Instance;
        var sw = Stopwatch.StartNew();

        // If not a valid minidump, fall back to sequential scanning
        if (!minidumpInfo.IsValid || minidumpInfo.MemoryRegions.Count == 0)
        {
            log.Debug("Not a valid minidump - using sequential scan");
            return FindAllMatches(accessor, fileSize, progress);
        }

        var regionGroups = minidumpInfo.GetContiguousRegionGroups();
        log.Debug("Found {0} contiguous region groups from {1} total regions",
            regionGroups.Count, minidumpInfo.MemoryRegions.Count);

        // Calculate total bytes to scan (only memory regions, not headers/gaps)
        var totalRegionBytes = regionGroups.Sum(g => g.TotalSize);
        log.Debug("Total scannable: {0:N0} MB (vs {1:N0} MB file size)",
            totalRegionBytes / (1024 * 1024), fileSize / (1024 * 1024));

        var allMatches = new ConcurrentBag<(string SignatureId, long Offset)>();
        var bytesScanned = 0L;
        var maxPatternLength = _signatureMatcher.MaxPatternLength;

        // Process region groups in parallel
        Parallel.ForEach(regionGroups,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            group =>
            {
                var groupMatches = ScanRegionGroup(accessor, group, maxPatternLength);

                foreach (var match in groupMatches)
                {
                    allMatches.Add(match);
                }

                // Update progress
                var scanned = Interlocked.Add(ref bytesScanned, group.TotalSize);
                progress?.Report(new AnalysisProgress
                {
                    Phase = "Scanning",
                    BytesProcessed = scanned,
                    TotalBytes = totalRegionBytes,
                    FilesFound = allMatches.Count
                });
            });

        sw.Stop();
        log.Debug("Parallel scan complete: {0:N0} matches in {1:N0} ms",
            allMatches.Count, sw.ElapsedMilliseconds);

        // Sort by offset and deduplicate
        return allMatches
            .DistinctBy(m => m.Offset)
            .OrderBy(m => m.Offset)
            .ToList();
    }

    /// <summary>
    ///     Find all signature matches using sequential chunk-based scanning.
    ///     Used as fallback when minidump region information is unavailable.
    /// </summary>
    public List<(string SignatureId, long Offset)> FindAllMatches(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        IProgress<AnalysisProgress>? progress)
    {
        const int chunkSize = 64 * 1024 * 1024; // 64MB chunks
        var maxPatternLength = _signatureMatcher.MaxPatternLength;

        var allMatches = new List<(string SignatureId, long Offset)>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + maxPatternLength);
        var progressData = new AnalysisProgress { TotalBytes = fileSize };

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                var toRead = (int)Math.Min(chunkSize + maxPatternLength, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                var span = buffer.AsSpan(0, toRead);
                var matches = _signatureMatcher.Search(span, offset);

                foreach (var (name, _, position) in matches)
                {
                    allMatches.Add((name, position));
                }

                offset += chunkSize;

                progressData.BytesProcessed = Math.Min(offset, fileSize);
                progressData.FilesFound = allMatches.Count;
                progress?.Report(progressData);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Sort by offset and deduplicate
        return allMatches
            .DistinctBy(m => m.Offset)
            .OrderBy(m => m.Offset)
            .ToList();
    }

    /// <summary>
    ///     Scan a contiguous region group for signature matches.
    /// </summary>
    private List<(string SignatureId, long Offset)> ScanRegionGroup(
        MemoryMappedViewAccessor accessor,
        ContiguousRegionGroup group,
        int maxPatternLength)
    {
        const int chunkSize = 16 * 1024 * 1024; // 16MB chunks (smaller for better parallelism)
        var matches = new List<(string SignatureId, long Offset)>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + maxPatternLength);

        try
        {
            // Scan each region in the group sequentially (they're contiguous in file)
            foreach (var region in group.Regions)
            {
                var regionOffset = region.FileOffset;
                var regionEnd = region.FileOffset + region.Size;

                while (regionOffset < regionEnd)
                {
                    var toRead = (int)Math.Min(chunkSize + maxPatternLength, regionEnd - regionOffset);
                    accessor.ReadArray(regionOffset, buffer, 0, toRead);

                    var span = buffer.AsSpan(0, toRead);
                    var chunkMatches = _signatureMatcher.Search(span, regionOffset);

                    foreach (var (name, _, position) in chunkMatches)
                    {
                        // Only include matches within this region's bounds
                        if (position >= region.FileOffset && position < regionEnd)
                        {
                            matches.Add((name, position));
                        }
                    }

                    regionOffset += chunkSize;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return matches;
    }

    /// <summary>
    ///     Parse all signature matches, validate them, estimate sizes, and add to the analysis result.
    /// </summary>
    public static async Task ParseMatchesAsync(
        MemoryMappedViewAccessor accessor,
        AnalysisResult result,
        List<(string signatureId, long offset)> matches,
        HashSet<long> moduleOffsets,
        MinidumpInfo minidumpInfo,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            var processed = 0;
            foreach (var (signatureId, offset) in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryParseMatch(accessor, result, signatureId, offset, moduleOffsets, minidumpInfo))
                {
                    result.TypeCounts.TryGetValue(signatureId, out var count);
                    result.TypeCounts[signatureId] = count + 1;
                }

                processed++;
                ReportParsingProgress(progress, processed, matches.Count, result.CarvedFiles.Count);
            }
        }, cancellationToken);
    }

    /// <summary>
    ///     Try to parse a single matched signature at the given offset,
    ///     estimating file size and adding a CarvedFileInfo entry on success.
    /// </summary>
    public static bool TryParseMatch(
        MemoryMappedViewAccessor accessor,
        AnalysisResult result,
        string signatureId,
        long offset,
        HashSet<long> moduleOffsets,
        MinidumpInfo minidumpInfo)
    {
        // Skip signatures at module offsets (modules are added from minidump metadata)
        if (moduleOffsets.Contains(offset))
        {
            return false;
        }

        var format = FormatRegistry.GetBySignatureId(signatureId);
        if (format == null)
        {
            return false;
        }

        var signature =
            format.Signatures.FirstOrDefault(s => s.Id.Equals(signatureId, StringComparison.OrdinalIgnoreCase));
        if (signature == null)
        {
            return false;
        }

        var (length, fileName) = EstimateFileSizeAndExtractName(accessor, result.FileSize, offset, signatureId, format);
        if (length <= 0)
        {
            return false;
        }

        // Detect truncation: check if file extends past contiguous memory region boundary
        var isTruncated = false;
        if (minidumpInfo.IsValid && minidumpInfo.MemoryRegions.Count > 0)
        {
            var contiguousBytes = minidumpInfo.GetContiguousBytesFromFileOffset(offset);
            if (contiguousBytes > 0 && length > contiguousBytes)
            {
                isTruncated = true;
            }
        }

        result.CarvedFiles.Add(new CarvedFileInfo
        {
            Offset = offset,
            Length = length,
            FileType = signature.Description,
            FileName = fileName,
            SignatureId = signatureId,
            Category = format.Category,
            IsTruncated = isTruncated
        });

        return true;
    }

    /// <summary>
    ///     Report parsing progress at regular intervals.
    /// </summary>
    public static void ReportParsingProgress(
        IProgress<AnalysisProgress>? progress, int processed, int total, int filesFound)
    {
        if (progress == null || processed % 100 != 0)
        {
            return;
        }

        var parsePercent = 50 + processed * 20.0 / total;
        progress.Report(new AnalysisProgress
        {
            Phase = "Parsing",
            FilesFound = filesFound,
            PercentComplete = parsePercent
        });
    }

    /// <summary>
    ///     Sort all carved files in the result by their file offset.
    /// </summary>
    public static void SortCarvedFilesByOffset(AnalysisResult result)
    {
        result.CarvedFiles.Sort((a, b) => a.Offset.CompareTo(b.Offset));
    }

    /// <summary>
    ///     Estimate the file size and extract the display name for a matched signature.
    ///     Uses the format registry to parse the header and determine the actual file boundaries.
    /// </summary>
    public static (long length, string? fileName) EstimateFileSizeAndExtractName(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        long offset,
        string signatureId,
        IFileFormat format)
    {
        // For DDX files, we need to read some data before the signature to find the path
        // Read up to 512 bytes before and the header after
        const int preReadSize = 512;
        var actualPreRead = (int)Math.Min(preReadSize, offset);
        var readStart = offset - actualPreRead;

        // Use format-specific buffer sizes for boundary scanning
        // DDX files need larger buffers to find RIFF/DDS boundaries (XMA files may be 100KB+ into texture data)
        var headerScanSize = signatureId.StartsWith("ddx", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(format.MaxSize, 512 * 1024) // 512KB for DDX boundary scanning
            : Math.Min(format.MaxSize, 64 * 1024); // 64KB for other types

        var headerSize = (int)Math.Min(headerScanSize, fileSize - offset);
        var totalRead = actualPreRead + headerSize;

        var buffer = ArrayPool<byte>.Shared.Rent(totalRead);
        try
        {
            accessor.ReadArray(readStart, buffer, 0, totalRead);
            var span = buffer.AsSpan(0, totalRead);

            // The signature starts at actualPreRead offset in our buffer
            var sigOffset = actualPreRead;

            // Use format module for accurate size estimation
            var parseResult = format.Parse(span, sigOffset);
            if (parseResult != null)
            {
                var estimatedSize = parseResult.EstimatedSize;
                if (estimatedSize >= format.MinSize && estimatedSize <= format.MaxSize)
                {
                    var length = Math.Min(estimatedSize, (int)(fileSize - offset));

                    // Extract filename for display in the file table
                    // Priority: fileName > scriptName > texturePath filename portion
                    string? fileName = null;

                    if (parseResult.Metadata.TryGetValue("fileName", out var fileNameObj) &&
                        fileNameObj is string fn && !string.IsNullOrEmpty(fn))
                    {
                        fileName = fn;
                    }
                    else if (parseResult.Metadata.TryGetValue("scriptName", out var scriptNameObj) &&
                             scriptNameObj is string sn && !string.IsNullOrEmpty(sn))
                    {
                        fileName = sn;
                    }
                    else if (parseResult.Metadata.TryGetValue("texturePath", out var pathObj) &&
                             pathObj is string texturePath)
                        // Fall back to extracting filename from path
                    {
                        fileName = Path.GetFileName(texturePath);
                    }

                    return (length, fileName);
                }
            }

            // Format returned null - invalid file, skip it
            return (0, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
