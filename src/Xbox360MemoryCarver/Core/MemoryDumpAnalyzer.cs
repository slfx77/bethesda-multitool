using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using Xbox360MemoryCarver.Core.Formats;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Formats.Scda;
using Xbox360MemoryCarver.Core.Minidump;

namespace Xbox360MemoryCarver.Core;

/// <summary>
///     Unified analyzer for memory dumps. Provides both file carving analysis
///     (for GUI visualization) and metadata extraction (for CLI reporting).
/// </summary>
public sealed class MemoryDumpAnalyzer
{
    private readonly SignatureMatcher _signatureMatcher;

    public MemoryDumpAnalyzer()
    {
        _signatureMatcher = new SignatureMatcher();

        // Register all signatures from the format registry for analysis
        // (includes all formats for visualization, even those with scanning disabled)
        foreach (var format in FormatRegistry.All)
            foreach (var sig in format.Signatures)
                _signatureMatcher.AddPattern(sig.Id, sig.MagicBytes);

        _signatureMatcher.Build();
    }

    /// <summary>
    ///     Analyze a memory dump file to identify all extractable files.
    ///     This is the unified analysis method used by both GUI and CLI.
    /// </summary>
    /// <param name="filePath">Path to the memory dump file.</param>
    /// <param name="progress">Optional progress callback for scan progress.</param>
    /// <param name="includeMetadata">Whether to include SCDA/ESM metadata extraction (default: true).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S3776:Cognitive Complexity", Justification = "Multi-phase analysis pipeline requires coordinating several analysis stages")]
    public async Task<AnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<AnalysisProgress>? progress = null,
        bool includeMetadata = true,
        CancellationToken cancellationToken = default)
    {
        // Progress phases and their weight (totals 100%):
        // - Scanning: 0-50%
        // - Parsing: 50-70%
        // - Loading: 70-75% (if metadata enabled)
        // - SCDA scan: 75-85%
        // - ESM scan: 85-95%
        // - FormID map: 95-100%

        var stopwatch = Stopwatch.StartNew();
        var result = new AnalysisResult { FilePath = filePath };

        var fileInfo = new FileInfo(filePath);
        result.FileSize = fileInfo.Length;

        // Parse minidump to get module information and memory mappings (quick operation)
        var minidumpInfo = MinidumpParser.Parse(filePath);
        result.MinidumpInfo = minidumpInfo;

        if (minidumpInfo.IsValid)
        {
            result.BuildType = DetectBuildType(minidumpInfo);
            Console.WriteLine(
                $"[Minidump] {minidumpInfo.Modules.Count} modules, {minidumpInfo.MemoryRegions.Count} memory regions, Xbox 360: {minidumpInfo.IsXbox360}");

            // Add modules directly to results
            AddModulesFromMinidump(result, minidumpInfo);
        }

        // Build set of module file offsets to exclude from signature scanning
        var moduleOffsets = new HashSet<long>(
            minidumpInfo.Modules
                .Select(m => minidumpInfo.GetModuleFileRange(m))
                .Where(r => r.HasValue)
                .Select(r => r!.Value.fileOffset));

        // Phase 1: Signature scanning (0-50%)
        var scanProgress = progress != null
            ? new Progress<AnalysisProgress>(p =>
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
            })
            : null;

        var matches = await Task.Run(() =>
        {
            using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);
            return FindAllMatches(accessor, result.FileSize, scanProgress);
        }, cancellationToken);

        // Phase 2: Parsing matches (50-70%)
        progress?.Report(new AnalysisProgress { Phase = "Parsing", FilesFound = matches.Count, PercentComplete = 50 });

        await Task.Run(() =>
        {
            using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);

            var processed = 0;
            foreach (var (signatureId, offset) in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Skip XEX signatures at module offsets (already added above)
                if (signatureId == "xex" && moduleOffsets.Contains(offset)) continue;

                var format = FormatRegistry.GetBySignatureId(signatureId);
                if (format == null) continue;

                var signature =
                    format.Signatures.FirstOrDefault(s => s.Id.Equals(signatureId, StringComparison.OrdinalIgnoreCase));
                if (signature == null) continue;

                var (length, fileName) =
                    EstimateFileSizeAndExtractName(accessor, result.FileSize, offset, signatureId, format);

                if (length > 0)
                {
                    result.CarvedFiles.Add(new CarvedFileInfo
                    {
                        Offset = offset,
                        Length = length,
                        FileType = signature.Description,
                        FileName = fileName
                    });

                    result.TypeCounts.TryGetValue(signatureId, out var count);
                    result.TypeCounts[signatureId] = count + 1;
                }

                processed++;
                if (progress != null && processed % 100 == 0)
                {
                    var parsePercent = 50 + (processed * 20.0 / matches.Count);
                    progress.Report(new AnalysisProgress
                    {
                        Phase = "Parsing",
                        FilesFound = result.CarvedFiles.Count,
                        PercentComplete = parsePercent
                    });
                }
            }
        }, cancellationToken);

        // Sort all results by offset
        var sortedFiles = result.CarvedFiles.OrderBy(f => f.Offset).ToList();
        result.CarvedFiles.Clear();
        foreach (var file in sortedFiles) result.CarvedFiles.Add(file);

        // Extract metadata (SCDA records, ESM records, FormID mapping)
        if (includeMetadata)
        {
            // Phase 3: Loading dump data (70-75%)
            progress?.Report(new AnalysisProgress { Phase = "Loading", FilesFound = result.CarvedFiles.Count, PercentComplete = 70 });
            var data = await File.ReadAllBytesAsync(filePath, cancellationToken);

            // Phase 4: SCDA scan (75-85%)
            progress?.Report(new AnalysisProgress { Phase = "Scripts", FilesFound = result.CarvedFiles.Count, PercentComplete = 75 });
            await Task.Run(() =>
            {
                var scdaScanResult = ScdaFormat.ScanForRecords(data);
                foreach (var record in scdaScanResult.Records)
                {
                    record.ScriptName = ScdaExtractor.ExtractScriptNameFromSourcePublic(record.SourceText);
                }
                result.ScdaRecords = scdaScanResult.Records;
            }, cancellationToken);

            // Phase 5: ESM scan (85-95%)
            progress?.Report(new AnalysisProgress { Phase = "ESM Records", FilesFound = result.CarvedFiles.Count, PercentComplete = 85 });
            await Task.Run(() =>
            {
                var esmRecords = EsmRecordFormat.ScanForRecords(data);
                result.EsmRecords = esmRecords;
            }, cancellationToken);

            // Phase 6: FormID mapping (95-100%)
            progress?.Report(new AnalysisProgress { Phase = "FormIDs", FilesFound = result.CarvedFiles.Count, PercentComplete = 95 });
            await Task.Run(() =>
            {
                result.FormIdMap = EsmRecordFormat.CorrelateFormIdsToNames(data, result.EsmRecords!);
            }, cancellationToken);
        }

        progress?.Report(new AnalysisProgress { Phase = "Complete", FilesFound = result.CarvedFiles.Count, PercentComplete = 100 });

        stopwatch.Stop();
        result.AnalysisTime = stopwatch.Elapsed;

        return result;
    }

    /// <summary>
    ///     Synchronous wrapper for AnalyzeAsync. Prefer AnalyzeAsync for new code.
    /// </summary>
    [Obsolete("Use AnalyzeAsync instead for better async support.")]
    public AnalysisResult Analyze(string filePath, IProgress<AnalysisProgress>? progress)
    {
        return AnalyzeAsync(filePath, progress, includeMetadata: true).GetAwaiter().GetResult();
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

    #region Report Generation

    /// <summary>
    ///     Generate a markdown report from analysis results.
    /// </summary>
    public static string GenerateReport(AnalysisResult result)
    {
        var sb = new StringBuilder();

        AppendHeader(sb, result);
        AppendCarvedFilesSection(sb, result);
        AppendModuleSection(sb, result);
        AppendScriptSection(sb, result);
        AppendEsmSection(sb, result);
        AppendFormIdSection(sb, result);

        return sb.ToString();
    }

    /// <summary>
    ///     Generate a brief text summary suitable for console output.
    /// </summary>
    public static string GenerateSummary(AnalysisResult result)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"Dump: {Path.GetFileName(result.FilePath)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Size: {result.FileSize / (1024.0 * 1024.0):F2} MB");

        if (result.MinidumpInfo?.IsValid == true)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Build: {result.BuildType ?? "Unknown"}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Modules: {result.MinidumpInfo.Modules.Count}");

            var gameModule = FindGameModule(result.MinidumpInfo);
            if (gameModule != null)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"Game: {Path.GetFileName(gameModule.Name)} ({gameModule.Size / 1024.0:F0} KB)");
            }
        }

        if (result.CarvedFiles.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Carved Files: {result.CarvedFiles.Count}");
        }

        if (result.ScdaRecords.Count > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"SCDA Records: {result.ScdaRecords.Count}");
            var withSource = result.ScdaRecords.Count(s => s.HasAssociatedSctx);
            if (withSource > 0)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"With Source: {withSource}");
            }
        }

        if (result.EsmRecords != null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"Editor IDs: {result.EsmRecords.EditorIds.Count}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"FormID Map: {result.FormIdMap.Count}");
        }

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, AnalysisResult result)
    {
        sb.AppendLine("# Memory Dump Analysis Report");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**File**: {Path.GetFileName(result.FilePath)}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Size**: {result.FileSize:N0} bytes ({result.FileSize / (1024.0 * 1024.0):F2} MB)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Analysis Time**: {result.AnalysisTime.TotalSeconds:F2}s");
    }

    private static void AppendCarvedFilesSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.CarvedFiles.Count == 0)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Carved Files Summary");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Total Files**: {result.CarvedFiles.Count}");
        sb.AppendLine();

        // Group by type
        var byType = result.TypeCounts.OrderByDescending(kv => kv.Value).ToList();
        sb.AppendLine("| File Type | Count |");
        sb.AppendLine("|-----------|-------|");
        foreach (var (type, count) in byType)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {type} | {count} |");
        }

        sb.AppendLine();
    }

    private static void AppendModuleSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.MinidumpInfo?.IsValid != true)
        {
            return;
        }

        var info = result.MinidumpInfo;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Architecture**: {(info.IsXbox360 ? "Xbox 360 (PowerPC)" : "Other")}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Build Type**: {result.BuildType ?? "Unknown"}");
        sb.AppendLine();

        sb.AppendLine("## Loaded Modules");
        sb.AppendLine();
        sb.AppendLine("| Module | Base Address | Size |");
        sb.AppendLine("|--------|-------------|------|");

        foreach (var module in info.Modules.OrderBy(m => m.BaseAddress32))
        {
            var fileName = Path.GetFileName(module.Name);
            var sizeKB = module.Size / 1024.0;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {fileName} | 0x{module.BaseAddress32:X8} | {sizeKB:F0} KB |");
        }

        sb.AppendLine();

        var totalMemory = info.MemoryRegions.Sum(r => r.Size);
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Memory Regions**: {info.MemoryRegions.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Total Captured**: {totalMemory:N0} bytes ({totalMemory / (1024.0 * 1024.0):F2} MB)");
        sb.AppendLine();
    }

    private static void AppendScriptSection(StringBuilder sb, AnalysisResult result)
    {
        sb.AppendLine("## Compiled Scripts (SCDA)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Total SCDA Records**: {result.ScdaRecords.Count}");

        var withSource = result.ScdaRecords.Count(s => s.HasAssociatedSctx);
        var withNames = result.ScdaRecords.Count(s => !string.IsNullOrEmpty(s.ScriptName));
        sb.AppendLine(CultureInfo.InvariantCulture, $"**With Source (SCTX)**: {withSource}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"**With Script Names**: {withNames}");
        sb.AppendLine();

        if (result.ScdaRecords.Count == 0)
        {
            return;
        }

        sb.AppendLine("| Offset | Script Name | Bytecode Size | Has Source |");
        sb.AppendLine("|--------|-------------|--------------|------------|");

        foreach (var scda in result.ScdaRecords.Take(30))
        {
            var name = !string.IsNullOrEmpty(scda.ScriptName) ? scda.ScriptName : "-";
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| 0x{scda.Offset:X8} | {name} | {scda.BytecodeLength} bytes | {(scda.HasAssociatedSctx ? "Yes" : "No")} |");
        }

        if (result.ScdaRecords.Count > 30)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| ... | ... | ({result.ScdaRecords.Count - 30} more) | ... |");
        }

        sb.AppendLine();
    }

    private static void AppendEsmSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.EsmRecords == null)
        {
            return;
        }

        sb.AppendLine("## ESM Records");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Editor IDs (EDID)**: {result.EsmRecords.EditorIds.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Game Settings (GMST)**: {result.EsmRecords.GameSettings.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Script Sources (SCTX)**: {result.EsmRecords.ScriptSources.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**FormID Refs (SCRO)**: {result.EsmRecords.FormIdReferences.Count}");
        sb.AppendLine();
    }

    private static void AppendFormIdSection(StringBuilder sb, AnalysisResult result)
    {
        if (result.FormIdMap.Count == 0)
        {
            return;
        }

        sb.AppendLine("## FormID Correlations");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"**Mapped FormIDs**: {result.FormIdMap.Count}");
        sb.AppendLine();

        sb.AppendLine("| FormID | Editor ID |");
        sb.AppendLine("|--------|-----------|");

        foreach (var (formId, name) in result.FormIdMap.Take(30).OrderBy(kv => kv.Key))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| 0x{formId:X8} | {name} |");
        }

        if (result.FormIdMap.Count > 30)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| ... | ({result.FormIdMap.Count - 30} more) |");
        }
    }

    #endregion

    private static void AddModulesFromMinidump(AnalysisResult result, MinidumpInfo minidumpInfo)
    {
        var xexFormat = FormatRegistry.GetByFormatId("xex");
        if (xexFormat == null) return;

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
                    FileName = fileName
                });

                result.TypeCounts.TryGetValue("xex", out var modCount);
                result.TypeCounts["xex"] = modCount + 1;
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

                foreach (var (name, _, position) in matches) allMatches.Add((name, position));

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
                        fileName = fn;
                    else if (parseResult.Metadata.TryGetValue("scriptName", out var scriptNameObj) &&
                             scriptNameObj is string sn && !string.IsNullOrEmpty(sn))
                        fileName = sn;
                    else if (parseResult.Metadata.TryGetValue("texturePath", out var pathObj) &&
                             pathObj is string texturePath)
                        // Fall back to extracting filename from path
                        fileName = Path.GetFileName(texturePath);

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
