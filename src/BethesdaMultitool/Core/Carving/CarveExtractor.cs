using System.Buffers;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Minidump;

namespace BethesdaMultitool.Core.Carving;

/// <summary>
///     Handles the extraction and preparation of carved file data.
/// </summary>
internal static class CarveExtractor
{
    /// <summary>
    ///     Prepare extraction data for a single match.
    /// </summary>
    public static ExtractionData? PrepareExtraction(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        long offset,
        string signatureId,
        IFileFormat format,
        string outputPath,
        MinidumpInfo? minidumpInfo = null)
    {
        // Read data before and after the signature for context
        const int preReadSize = 512;
        var actualPreRead = (int)Math.Min(preReadSize, offset);
        var readStart = offset - actualPreRead;

        var headerScanSize = Math.Min(format.MaxSize, format.ParseWindowSize);

        var headerSize = (int)Math.Min(headerScanSize, fileSize - offset);

        // Clamp the parse window to the flat-readable run of memory regions: a flat read
        // is only VA-correct while successive regions are contiguous in BOTH file offset
        // and virtual address. Past that point the file bytes belong to a different VA
        // range and would feed cross-region garbage to the boundary scanners. Cross-region
        // payload reassembly still happens later in ReadFileData; this only bounds the
        // header/boundary scan. (Containing-region-only clamping regressed real DDX
        // boundaries: dumps commonly store small regions back-to-back in file order.)
        if (minidumpInfo is { IsValid: true })
        {
            headerSize = ClampToFlatReadableRun(minidumpInfo, offset, headerSize);
        }

        var totalRead = actualPreRead + headerSize;
        var buffer = ArrayPool<byte>.Shared.Rent(totalRead);

        try
        {
            // Pooled buffer: bound the span by the ACTUAL read count so stale bytes from a previous
            // rental can never reach a parser.
            var bytesRead = accessor.ReadArray(readStart, buffer, 0, totalRead);
            var span = buffer.AsSpan(0, bytesRead);

            var sigOffset = actualPreRead;

            var parseResult = format.Parse(span, sigOffset);
            if (parseResult == null)
            {
                return null;
            }

            var extractionInfo = BuildExtractionInfo(parseResult, actualPreRead);
            var (leadingBytes, customFilename, originalPath, metadata) = extractionInfo;

            // Adjust for leading bytes (e.g., comments before script signature)
            var adjustedOffset = offset - leadingBytes;
            var adjustedSize = parseResult.EstimatedSize + leadingBytes;

            if (adjustedSize < format.MinSize || adjustedSize > format.MaxSize)
            {
                return null;
            }

            adjustedSize = (int)Math.Min(adjustedSize, fileSize - adjustedOffset);

            var outputFile = BuildOutputPath(outputPath, signatureId, format, customFilename, offset,
                parseResult.OutputFolderOverride, parseResult.ExtensionOverride);

            // Read the actual file data (including any leading bytes)
            // For minidumps, reassemble from VA-mapped regions; otherwise flat read
            var (fileData, residency) =
                ReadFileData(accessor, adjustedOffset, adjustedSize, minidumpInfo);

            return new ExtractionData(outputFile, fileData, adjustedSize, originalPath, metadata, residency);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Clamp a flat read starting at <paramref name="offset" /> to the run of memory
    ///     regions that are contiguous in both file offset and virtual address, so the
    ///     bytes handed to Parse match the VA view. Returns <paramref name="headerSize" />
    ///     unchanged when the offset is not inside any region.
    /// </summary>
    private static int ClampToFlatReadableRun(MinidumpInfo minidumpInfo, long offset, int headerSize)
    {
        var region = minidumpInfo.FindRegionByFileOffset(offset);
        if (region == null)
        {
            return headerSize;
        }

        var flatFileEnd = region.FileOffset + region.Size;
        var vaEnd = region.VirtualAddress + region.Size;
        var windowEnd = offset + headerSize;

        while (flatFileEnd < windowEnd)
        {
            var next = minidumpInfo.GetRegionsInRange(vaEnd, vaEnd + 1).FirstOrDefault();
            if (next == null || next.VirtualAddress != vaEnd || next.FileOffset != flatFileEnd)
            {
                break; // VA gap or file discontinuity - a flat read stops being VA-correct here
            }

            flatFileEnd += next.Size;
            vaEnd += next.Size;
        }

        return (int)Math.Min(headerSize, flatFileEnd - offset);
    }

    /// <summary>
    ///     Read file data, using VA-aware reassembly for minidumps or flat read otherwise, and
    ///     report exactly which byte runs were missing rather than only how many there were.
    /// </summary>
    private static (byte[] data, CarveResidency residency) ReadFileData(
        MemoryMappedViewAccessor accessor,
        long adjustedOffset,
        int adjustedSize,
        MinidumpInfo? minidumpInfo)
    {
        if (minidumpInfo is { IsValid: true, MemoryRegions.Count: > 0 })
        {
            var matchVa = minidumpInfo.FileOffsetToVirtualAddress(adjustedOffset);
            if (matchVa != null)
            {
                var startVa = matchVa.Value;
                var endVa = startVa + adjustedSize;
                var regions = minidumpInfo.GetRegionsInRange(startVa, endVa);

                var fileData = new byte[adjustedSize]; // zero-filled for unmapped gaps
                var presentRuns = new List<CarveHole>(regions.Count);

                foreach (var region in regions)
                {
                    var overlapStart = Math.Max(region.VirtualAddress, startVa);
                    var overlapEnd = Math.Min(region.VirtualAddress + region.Size, endVa);
                    var overlapSize = (int)(overlapEnd - overlapStart);
                    var bufferOffset = (int)(overlapStart - startVa);
                    var regionFileOffset = region.FileOffset + (overlapStart - region.VirtualAddress);

                    // GetRegionsInRange returns VA-sorted regions, so the runs come out ascending
                    // and the complement over [0, size) is the hole set with no extra sorting.
                    var read = accessor.ReadArray(regionFileOffset, fileData, bufferOffset, overlapSize);
                    if (read > 0)
                    {
                        presentRuns.Add(new CarveHole(bufferOffset, read));
                    }
                }

                return (fileData, CarveResidency.FromPresentRuns(presentRuns, adjustedSize));
            }
        }

        // Not a valid minidump or VA lookup failed - flat read (current behavior). A short read
        // here means end-of-file, i.e. a single trailing hole; there are no interior gaps to find.
        var flatData = new byte[adjustedSize];
        var flatRead = accessor.ReadArray(adjustedOffset, flatData, 0, adjustedSize);
        return (flatData, CarveResidency.FromPresentRuns([new CarveHole(0, flatRead)], adjustedSize));
    }

    private static (int leadingBytes, string? customFilename, string? originalPath, Dictionary<string, object>? metadata
        )
        BuildExtractionInfo(ParseResult parseResult, int actualPreRead)
    {
        string? customFilename = null;
        string? originalPath = null;
        var leadingBytes = 0;

        // Get the safe filename for extraction
        if (parseResult.Metadata.TryGetValue("safeName", out var safeName))
        {
            customFilename = safeName.ToString();
        }

        // Get the original path for the manifest (DDX textures)
        if (parseResult.Metadata.TryGetValue("texturePath", out var pathObj) && pathObj is string path)
        {
            originalPath = path;
        }

        // Get embedded path for XMA files
        if (parseResult.Metadata.TryGetValue("embeddedPath", out var embeddedPathObj) &&
            embeddedPathObj is string embeddedPath)
        {
            originalPath ??= embeddedPath;
        }

        // Check for leading comments (scripts with comments before the scn keyword)
        if (parseResult.Metadata.TryGetValue("leadingCommentSize", out var leadingObj) &&
            leadingObj is int leading)
        {
            leadingBytes = Math.Min(leading, actualPreRead);
        }

        return (leadingBytes, customFilename, originalPath, parseResult.Metadata);
    }

    private static string BuildOutputPath(string outputPath, string signatureId, IFileFormat format,
        string? customFilename, long offset, string? outputFolderOverride = null, string? extensionOverride = null)
    {
        // Use override if provided, otherwise fall back to format default
        var typeFolder = outputFolderOverride ??
                         (string.IsNullOrEmpty(format.OutputFolder) ? signatureId : format.OutputFolder);
        var typePath = Path.Combine(outputPath, typeFolder);
        Directory.CreateDirectory(typePath);

        var extension = extensionOverride ?? format.Extension;
        var filename = customFilename ?? $"{offset:X8}";
        var outputFile = Path.Combine(typePath, $"{filename}{extension}");

        // Reserve the name ATOMICALLY. A check-then-use `while (File.Exists(...))` loop races
        // under parallel extraction: two workers both observe "_2 is free", both claim it, one
        // write loses, and the writer's retry path renames the loser to a GUID — while the
        // manifest has already recorded the name the caller asked for. Manifest and disk then
        // disagree. Whoever wins CreateNew owns the name; the real write overwrites the
        // zero-byte placeholder.
        const int maxDedupeAttempts = 10_000;
        for (var counter = 1; counter <= maxDedupeAttempts; counter++)
        {
            try
            {
                using var reservation = new FileStream(
                    outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return outputFile;
            }
            catch (IOException)
            {
                outputFile = Path.Combine(typePath, $"{filename}_{counter}{extension}");
            }
        }

        // Pathological collision count: fall back to a name that cannot collide. Still returned
        // to the caller, so the manifest records what actually gets written.
        return Path.Combine(typePath, $"{filename}_{Guid.NewGuid():N}{extension}");
    }
}
