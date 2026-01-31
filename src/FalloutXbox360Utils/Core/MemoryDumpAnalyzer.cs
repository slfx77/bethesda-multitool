using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.Scda;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Unified analyzer for memory dumps. Provides both file carving analysis
///     (for GUI visualization) and metadata extraction (for CLI reporting).
/// </summary>
public sealed partial class MemoryDumpAnalyzer
{
    private readonly SignatureMatcher _signatureMatcher;

    public MemoryDumpAnalyzer()
    {
        _signatureMatcher = new SignatureMatcher();

        // Register all signatures from the format registry for analysis
        // (includes all formats for visualization, even those with scanning disabled)
        foreach (var format in FormatRegistry.All)
        foreach (var sig in format.Signatures)
        {
            _signatureMatcher.AddPattern(sig.Id, sig.MagicBytes);
        }

        _signatureMatcher.Build();
    }

    /// <summary>
    ///     Analyze a memory dump file to identify all extractable files.
    ///     This is the unified analysis method used by both GUI and CLI.
    /// </summary>
    /// <param name="filePath">Path to the memory dump file.</param>
    /// <param name="progress">Optional progress callback for scan progress.</param>
    /// <param name="includeMetadata">Whether to include SCDA/ESM metadata extraction (default: true).</param>
    /// <param name="verbose">Whether to output verbose timing/progress info.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<AnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<AnalysisProgress>? progress = null,
        bool includeMetadata = true,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new AnalysisResult { FilePath = filePath };

        var fileInfo = new FileInfo(filePath);
        result.FileSize = fileInfo.Length;

        // Parse minidump to get module information and memory mappings (quick operation)
        var minidumpInfo = MinidumpParser.Parse(filePath);
        result.MinidumpInfo = minidumpInfo;

        ProcessMinidumpInfo(result, minidumpInfo);

        // Build set of module file offsets to exclude from signature scanning
        var moduleOffsets = BuildModuleOffsetSet(minidumpInfo);

        // Use a single memory-mapped file for all operations
        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);

        // Phase 1: Signature scanning (0-50%)
        var scanProgress = CreateScanProgress(progress);
        var matches =
            await Task.Run(() => FindAllMatchesParallel(accessor, result.FileSize, minidumpInfo, scanProgress),
                cancellationToken);

        // Phase 2: Parsing matches (50-70%)
        progress?.Report(new AnalysisProgress { Phase = "Parsing", FilesFound = matches.Count, PercentComplete = 50 });
        await ParseMatchesAsync(accessor, result, matches, moduleOffsets, minidumpInfo, progress, cancellationToken);

        // Sort all results by offset
        SortCarvedFilesByOffset(result);

        // Extract metadata (SCDA records, ESM records, FormID mapping) using memory-mapped access
        if (includeMetadata)
        {
            // Build module ranges for ESM exclusion (modules may contain ESM-like data)
            var moduleRanges = BuildModuleRanges(minidumpInfo);
            await ExtractMetadataAsync(accessor, result, moduleRanges, minidumpInfo, progress, verbose,
                cancellationToken);
        }

        progress?.Report(new AnalysisProgress
            { Phase = "Complete", FilesFound = result.CarvedFiles.Count, PercentComplete = 100 });

        stopwatch.Stop();
        result.AnalysisTime = stopwatch.Elapsed;

        return result;
    }

    private static void ProcessMinidumpInfo(AnalysisResult result, MinidumpInfo minidumpInfo)
    {
        if (!minidumpInfo.IsValid)
        {
            return;
        }

        result.BuildType = DetectBuildType(minidumpInfo);
        Console.WriteLine(
            $"[Minidump] {minidumpInfo.Modules.Count} modules, {minidumpInfo.MemoryRegions.Count} memory regions, Xbox 360: {minidumpInfo.IsXbox360}");

        // Add minidump header as a colored region
        if (minidumpInfo.HeaderSize > 0)
        {
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = 0,
                Length = minidumpInfo.HeaderSize,
                FileType = "Minidump Header",
                FileName = "minidump_header",
                SignatureId = "minidump_header",
                Category = FileCategory.Header
            });
        }

        // Add modules directly to results
        AddModulesFromMinidump(result, minidumpInfo);
    }

    private static HashSet<long> BuildModuleOffsetSet(MinidumpInfo minidumpInfo)
    {
        return
        [
            .. minidumpInfo.Modules
                .Select(m => minidumpInfo.GetModuleFileRange(m))
                .Where(r => r.HasValue)
                .Select(r => r!.Value.fileOffset)
        ];
    }

    /// <summary>
    ///     Builds a list of module file offset ranges for exclusion during ESM scanning.
    ///     Returns (start, end) tuples representing the full memory range of each module.
    /// </summary>
    private static List<(long start, long end)> BuildModuleRanges(MinidumpInfo minidumpInfo)
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

        return ranges;
    }

    private static Progress<AnalysisProgress>? CreateScanProgress(IProgress<AnalysisProgress>? progress)
    {
        if (progress == null)
        {
            return null;
        }

        return new Progress<AnalysisProgress>(p =>
        {
            // Scale scan progress to 0-50%
            var scanPercent = p.TotalBytes > 0 ? p.BytesProcessed * 50.0 / p.TotalBytes : 0;
            progress.Report(new AnalysisProgress
            {
                Phase = "Scanning",
                FilesFound = p.FilesFound,
                BytesProcessed = p.BytesProcessed,
                TotalBytes = p.TotalBytes,
                PercentComplete = scanPercent
            });
        });
    }

    private static async Task ParseMatchesAsync(
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

    private static bool TryParseMatch(
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

    private static void ReportParsingProgress(
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

    private static void SortCarvedFilesByOffset(AnalysisResult result)
    {
        result.CarvedFiles.Sort((a, b) => a.Offset.CompareTo(b.Offset));
    }

    private static async Task ExtractMetadataAsync(
        MemoryMappedViewAccessor accessor,
        AnalysisResult result,
        List<(long start, long end)>? moduleRanges,
        MinidumpInfo minidumpInfo,
        IProgress<AnalysisProgress>? progress,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var log = Logger.Instance;
        log.Debug("Metadata: Starting extraction...");

        // Phase 3: SCDA scan (70-80%) - now using memory-mapped access
        progress?.Report(new AnalysisProgress
            { Phase = "Scripts", FilesFound = result.CarvedFiles.Count, PercentComplete = 70 });
        log.Debug("Metadata: Phase 3 - SCDA scan");
        await Task.Run(() =>
        {
            var scdaScanResult = ScdaFormat.ScanForRecordsMemoryMapped(accessor, result.FileSize);
            foreach (var record in scdaScanResult.Records)
            {
                record.ScriptName = ScdaExtractor.ExtractScriptNameFromSourcePublic(record.SourceText);
            }

            result.ScdaRecords = scdaScanResult.Records;
        }, cancellationToken);
        log.Debug("Metadata: Phase 3 complete - {0} SCDA records", result.ScdaRecords?.Count ?? 0);

        // Phase 4: ESM scan (80-88%) - now using memory-mapped access
        // Pass module ranges to exclude ESM detection inside module memory
        progress?.Report(new AnalysisProgress
            { Phase = "ESM Records", FilesFound = result.CarvedFiles.Count, PercentComplete = 80 });
        log.Debug("Metadata: Phase 4 - ESM scan");
        await Task.Run(() =>
        {
            var esmRecords = EsmRecordFormat.ScanForRecordsMemoryMapped(accessor, result.FileSize, moduleRanges);
            result.EsmRecords = esmRecords;
            log.Debug("Metadata:   ESM records: {0}", esmRecords.MainRecords.Count);

            // Extract full LAND records with heightmaps
            progress?.Report(new AnalysisProgress
                { Phase = "LAND Records", FilesFound = result.CarvedFiles.Count, PercentComplete = 85 });
            log.Debug("Metadata:   Extracting LAND records...");
            EsmRecordFormat.ExtractLandRecords(accessor, result.FileSize, esmRecords);

            // Extract full REFR records with positions
            progress?.Report(new AnalysisProgress
                { Phase = "REFR Records", FilesFound = result.CarvedFiles.Count, PercentComplete = 86 });
            log.Debug("Metadata:   Extracting REFR records...");
            EsmRecordFormat.ExtractRefrRecords(accessor, result.FileSize, esmRecords);
            log.Debug("Metadata:   REFR complete: {0} records", esmRecords.RefrRecords.Count);

            // Scan for runtime asset string pools
            progress?.Report(new AnalysisProgress
                { Phase = "Asset Strings", FilesFound = result.CarvedFiles.Count, PercentComplete = 87 });
            log.Debug("Metadata:   Scanning for asset strings...");
            EsmRecordFormat.ScanForAssetStrings(accessor, result.FileSize, esmRecords, verbose);
            log.Debug("Metadata:   Asset strings complete: {0} paths", esmRecords.AssetStrings.Count);

            // Extract runtime Editor IDs with FormID associations via pointer following
            progress?.Report(new AnalysisProgress
                { Phase = "Runtime EditorIDs", FilesFound = result.CarvedFiles.Count, PercentComplete = 88 });
            log.Debug("Metadata:   Extracting runtime EditorIDs...");
            EsmRecordFormat.ExtractRuntimeEditorIds(accessor, result.FileSize, minidumpInfo, esmRecords, verbose);
            log.Debug("Metadata:   EditorIDs complete: {0} IDs", esmRecords.RuntimeEditorIds.Count);
        }, cancellationToken);

        // Phase 5: FormID mapping (90-100%) - now using memory-mapped access
        progress?.Report(new AnalysisProgress
            { Phase = "FormIDs", FilesFound = result.CarvedFiles.Count, PercentComplete = 90 });
        await Task.Run(
            () =>
            {
                result.FormIdMap =
                    EsmRecordFormat.CorrelateFormIdsToNamesMemoryMapped(accessor, result.FileSize,
                        result.EsmRecords!);
            },
            cancellationToken);
    }

    private static void AddModulesFromMinidump(AnalysisResult result, MinidumpInfo minidumpInfo)
    {
        foreach (var module in minidumpInfo.Modules)
        {
            var fileName = Path.GetFileName(module.Name);
            var fileRange = minidumpInfo.GetModuleFileRange(module);

            if (fileRange.HasValue)
            {
                var captured = fileRange.Value.size;
                Console.WriteLine(
                    $"[Minidump]   Module: {fileName} at 0x{fileRange.Value.fileOffset:X8}, captured: {captured:N0} bytes");

                // Determine description based on extension
                var isExe = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                var fileType = isExe ? "Xbox 360 Module (EXE)" : "Xbox 360 Module (DLL)";

                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = fileRange.Value.fileOffset,
                    Length = captured,
                    FileType = fileType,
                    FileName = fileName,
                    SignatureId = "module",
                    Category = FileCategory.Module
                });

                result.TypeCounts.TryGetValue("module", out var modCount);
                result.TypeCounts["module"] = modCount + 1;
            }
        }
    }

    private List<(string SignatureId, long Offset)> FindAllMatches(
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
    ///     Find all signature matches using parallel region-aware scanning.
    ///     Processes contiguous memory regions in parallel for better performance.
    /// </summary>
    private List<(string SignatureId, long Offset)> FindAllMatchesParallel(
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

    private static (long length, string? fileName) EstimateFileSizeAndExtractName(
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

    #region Build Type Detection

    /// <summary>
    ///     Detect the build type (Debug, Release Beta, Release MemDebug) from minidump modules.
    /// </summary>
    public static string? DetectBuildType(MinidumpInfo info)
    {
        foreach (var module in info.Modules)
        {
            var name = Path.GetFileName(module.Name);
            if (name.Contains("Debug", StringComparison.OrdinalIgnoreCase) &&
                !name.Contains("MemDebug", StringComparison.OrdinalIgnoreCase))
            {
                return "Debug";
            }

            if (name.Contains("MemDebug", StringComparison.OrdinalIgnoreCase))
            {
                return "Release MemDebug";
            }

            if (name.Contains("Release_Beta", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ReleaseBeta", StringComparison.OrdinalIgnoreCase))
            {
                return "Release Beta";
            }
        }

        // Default to Release if game exe found but no debug indicators
        if (info.Modules.Any(m => Path.GetFileName(m.Name).StartsWith("Fallout", StringComparison.OrdinalIgnoreCase)))
        {
            return "Release";
        }

        return null;
    }

    /// <summary>
    ///     Find the game executable module (Fallout*.exe).
    /// </summary>
    public static MinidumpModule? FindGameModule(MinidumpInfo info)
    {
        return info.Modules.FirstOrDefault(m =>
            Path.GetFileName(m.Name).StartsWith("Fallout", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(m.Name).EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
