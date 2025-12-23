using System.Buffers;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core.Models;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Core;

/// <summary>
/// Analyzes memory dumps to identify extractable file types without extracting them.
/// </summary>
public class MemoryDumpAnalyzer
{
    private readonly AhoCorasickMatcher _signatureMatcher;
    private readonly Dictionary<string, SignatureInfo> _signatures;

    public MemoryDumpAnalyzer()
    {
        _signatures = FileSignatures.Signatures;
        _signatureMatcher = new AhoCorasickMatcher();

        foreach (var (name, sig) in _signatures)
        {
            _signatureMatcher.AddPattern(name, sig.Magic);
        }
        _signatureMatcher.Build();
    }

    /// <summary>
    /// Analyze a memory dump file to identify all extractable files.
    /// </summary>
    public AnalysisResult Analyze(string filePath, IProgress<AnalysisProgress>? progress)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new AnalysisResult
        {
            FilePath = filePath
        };

        var fileInfo = new FileInfo(filePath);
        result.FileSize = fileInfo.Length;

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);

        var matches = FindAllMatches(accessor, result.FileSize, progress);

        // Convert matches to CarvedFileInfo using proper parsers
        foreach (var (sigName, offset) in matches)
        {
            var sig = _signatures[sigName];
            var length = EstimateFileSize(accessor, result.FileSize, offset, sigName, sig);

            if (length > 0)
            {
                result.CarvedFiles.Add(new CarvedFileInfo
                {
                    Offset = offset,
                    Length = length,
                    FileType = sig.Description ?? sigName
                });

                result.TypeCounts.TryGetValue(sigName, out var count);
                result.TypeCounts[sigName] = count + 1;
            }
        }

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
        int maxPatternLength = _signatureMatcher.MaxPatternLength;

        var allMatches = new List<(string SigName, long Offset)>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + maxPatternLength);
        var progressData = new AnalysisProgress { TotalBytes = fileSize };

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                int toRead = (int)Math.Min(chunkSize + maxPatternLength, fileSize - offset);
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

    private long EstimateFileSize(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        long offset,
        string sigName,
        SignatureInfo sig)
    {
        // Read enough data to parse the header
        int headerSize = Math.Min(sig.MaxSize, 64 * 1024);
        headerSize = (int)Math.Min(headerSize, fileSize - offset);

        var buffer = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            accessor.ReadArray(offset, buffer, 0, headerSize);
            var span = buffer.AsSpan(0, headerSize);

            // Use parser if available for accurate size estimation
            var parser = ParserFactory.GetParser(sigName);
            if (parser != null)
            {
                var parseResult = parser.ParseHeader(span);
                if (parseResult != null)
                {
                    var estimatedSize = parseResult.EstimatedSize;
                    if (estimatedSize >= sig.MinSize && estimatedSize <= sig.MaxSize)
                    {
                        return Math.Min(estimatedSize, (int)(fileSize - offset));
                    }
                }
                // Parser returned null - invalid file, skip it
                return 0;
            }

            // Fallback for types without parsers
            return Math.Min(sig.MaxSize, (int)(fileSize - offset));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
