using System.Buffers;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core.Minidump;
using Xbox360MemoryCarver.Core.Models;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Core;

/// <summary>
///     Analyzes memory dumps to identify extractable file types without extracting them.
/// </summary>
public class MemoryDumpAnalyzer
{
    private readonly AhoCorasickMatcher _signatureMatcher;
    private readonly IReadOnlyDictionary<string, SignatureInfo> _signatures;

    public MemoryDumpAnalyzer()
    {
        _signatures = FileSignatures.Signatures;
        _signatureMatcher = new AhoCorasickMatcher();

        foreach (var (name, sig) in _signatures) _signatureMatcher.AddPattern(name, sig.Magic);

        _signatureMatcher.Build();
    }

    /// <summary>
    ///     Analyze a memory dump file to identify all extractable files.
    /// </summary>
    public AnalysisResult Analyze(string filePath, IProgress<AnalysisProgress>? progress)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new AnalysisResult { FilePath = filePath };

        var fileInfo = new FileInfo(filePath);
        result.FileSize = fileInfo.Length;

        // Parse minidump to get module information and memory mappings
        var minidumpInfo = MinidumpParser.Parse(filePath);

        if (minidumpInfo.IsValid)
        {
            Console.WriteLine(
                $"[Minidump] {minidumpInfo.Modules.Count} modules, {minidumpInfo.MemoryRegions.Count} memory regions, Xbox 360: {minidumpInfo.IsXbox360}");

            // Add modules directly to results
            foreach (var module in minidumpInfo.Modules)
            {
                var fileName = Path.GetFileName(module.Name);
                var fileRange = minidumpInfo.GetModuleFileRange(module);

                if (fileRange.HasValue)
                {
                    var captured = fileRange.Value.size;
                    Console.WriteLine(
                        $"[Minidump]   Module: {fileName} at 0x{fileRange.Value.fileOffset:X8}, captured: {captured:N0} bytes");

                    // Determine type based on extension
                    var fileType = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? "Xbox 360 Module (EXE)"
                        : "Xbox 360 Module (DLL)";

                    // Add module to results
                    result.CarvedFiles.Add(new CarvedFileInfo
                    {
                        Offset = fileRange.Value.fileOffset,
                        Length = captured,
                        FileType = fileType,
                        FileName = fileName
                    });

                    result.TypeCounts.TryGetValue("module", out var modCount);
                    result.TypeCounts["module"] = modCount + 1;
                }
            }
        }

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);

        // Build set of module file offsets to exclude from signature scanning
        var moduleOffsets = new HashSet<long>(
            minidumpInfo.Modules
                .Select(m => minidumpInfo.GetModuleFileRange(m))
                .Where(r => r.HasValue)
                .Select(r => r!.Value.fileOffset));

        var matches = FindAllMatches(accessor, result.FileSize, progress);

        // Convert matches to CarvedFileInfo using proper parsers
        // Skip XEX signatures at module offsets (already added above)
        foreach (var (sigName, offset) in matches)
        {
            // Skip XEX signatures at module offsets (already added above)
            if (sigName == "xex" && moduleOffsets.Contains(offset))
                continue;

            var sig = _signatures[sigName];
            var (length, fileName) = EstimateFileSizeAndExtractName(accessor, result.FileSize, offset, sigName, sig);

            if (length > 0)
            {
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = offset,
                    Length = length,
                    FileType = sig.Description ?? sigName,
                    FileName = fileName
                });

                result.TypeCounts.TryGetValue(sigName, out var count);
                result.TypeCounts[sigName] = count + 1;
            }
        }

        // Sort all results by offset
        var sortedFiles = result.CarvedFiles.OrderBy(f => f.Offset).ToList();
        result.CarvedFiles.Clear();
        foreach (var file in sortedFiles) result.CarvedFiles.Add(file);

        stopwatch.Stop();
        result.AnalysisTime = stopwatch.Elapsed;

        return result;
    }

    private List<(string SigName, long Offset)> FindAllMatches(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        IProgress<AnalysisProgress>? progress)
    {
        const int chunkSize = 64 * 1024 * 1024; // 64MB chunks
        var maxPatternLength = _signatureMatcher.MaxPatternLength;

        var allMatches = new List<(string SigName, long Offset)>();
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
        string sigName,
        SignatureInfo sig)
    {
        // For DDX files, we need to read some data before the signature to find the path
        // Read up to 512 bytes before and the header after
        const int preReadSize = 512;
        var actualPreRead = (int)Math.Min(preReadSize, offset);
        var readStart = offset - actualPreRead;

        var headerSize = Math.Min(sig.MaxSize, 64 * 1024);
        headerSize = (int)Math.Min(headerSize, fileSize - offset);
        var totalRead = actualPreRead + headerSize;

        var buffer = ArrayPool<byte>.Shared.Rent(totalRead);
        try
        {
            accessor.ReadArray(readStart, buffer, 0, totalRead);
            var span = buffer.AsSpan(0, totalRead);

            // The signature starts at actualPreRead offset in our buffer
            var sigOffset = actualPreRead;

            // Use parser for accurate size estimation
            var parser = ParserFactory.GetParser(sigName);
            if (parser != null)
            {
                var parseResult = parser.ParseHeader(span, sigOffset);
                if (parseResult != null)
                {
                    var estimatedSize = parseResult.EstimatedSize;
                    if (estimatedSize >= sig.MinSize && estimatedSize <= sig.MaxSize)
                    {
                        var length = Math.Min(estimatedSize, (int)(fileSize - offset));

                        // Extract filename from texture path if available
                        string? fileName = null;
                        if (parseResult.Metadata.TryGetValue("texturePath", out var pathObj) &&
                            pathObj is string texturePath) fileName = texturePath;

                        return (length, fileName);
                    }
                }

                // Parser returned null - invalid file, skip it
                return (0, null);
            }

            // Fallback for types without parsers
            return (Math.Min(sig.MaxSize, (int)(fileSize - offset)), null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
