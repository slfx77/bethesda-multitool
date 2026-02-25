using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats;

namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Unified analyzer for memory dumps. Provides both file carving analysis
///     (for GUI visualization) and metadata extraction (for CLI reporting).
///     Delegates scanning to <see cref="MinidumpFileScanner"/>, reporting to
///     <see cref="MinidumpReportWriter"/>, and metadata extraction to
///     <see cref="MinidumpMetadataExtractor"/>.
/// </summary>
public sealed class MinidumpAnalyzer
{
    private readonly MinidumpFileScanner _fileScanner;

    public MinidumpAnalyzer()
    {
        _fileScanner = new MinidumpFileScanner();
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

        if (includeMetadata)
        {
            // Full concurrent analysis: all 5 scanners run simultaneously,
            // followed by sequential post-processing (match parsing, LAND/REFR, FormID mapping, etc.)
            var moduleRanges = BuildModuleRanges(minidumpInfo);
            await ConcurrentScanCoordinator.RunConcurrentAnalysisAsync(
                accessor, result, minidumpInfo, _fileScanner, moduleOffsets, moduleRanges,
                progress, verbose, cancellationToken);
        }
        else
        {
            // Lightweight path: signature scan + parse only (no metadata extraction)
            var scanProgress = CreateScanProgress(progress);
            var matches = await Task.Run(
                () => _fileScanner.FindAllMatchesParallel(accessor, result.FileSize, minidumpInfo, scanProgress),
                cancellationToken);

            progress?.Report(new AnalysisProgress
                { Phase = "Parsing", FilesFound = matches.Count, PercentComplete = 50 });
            await MinidumpFileScanner.ParseMatchesAsync(
                accessor, result, matches, moduleOffsets, minidumpInfo, progress, cancellationToken);

            MinidumpFileScanner.SortCarvedFilesByOffset(result);
        }

        progress?.Report(new AnalysisProgress
            { Phase = "Complete", FilesFound = result.CarvedFiles.Count, PercentComplete = 100 });

        stopwatch.Stop();
        result.AnalysisTime = stopwatch.Elapsed;

        return result;
    }

    /// <summary>
    ///     Generate a markdown report from analysis results.
    /// </summary>
    public static string GenerateReport(AnalysisResult result)
    {
        return MinidumpReportWriter.GenerateReport(result);
    }

    /// <summary>
    ///     Generate a brief text summary suitable for console output.
    /// </summary>
    public static string GenerateSummary(AnalysisResult result)
    {
        return MinidumpReportWriter.GenerateSummary(result);
    }

    /// <summary>
    ///     Generate a detailed semantic dump of all ESM records found.
    /// </summary>
    public static string GenerateSemanticDump(AnalysisResult result)
    {
        return MinidumpSemanticDumper.GenerateSemanticDump(result);
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

    #region Private Helpers

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

    #endregion
}
