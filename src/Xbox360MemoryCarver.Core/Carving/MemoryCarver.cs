using System.Buffers;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Models;
using Xbox360MemoryCarver.Core.Parsers;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Carving;

/// <summary>
///     High-performance memory dump file carver using Aho-Corasick multi-pattern search.
/// </summary>
public sealed class MemoryCarver : IDisposable
{
    private readonly bool _convertDdxToDds;
    private readonly DdxSubprocessConverter? _ddxConverter;
    private readonly ConcurrentBag<CarveEntry> _manifest = [];
    private readonly int _maxFilesPerType;
    private readonly string _outputDir;
    private readonly ConcurrentDictionary<long, byte> _processedOffsets = new();
    private readonly bool _saveAtlas;
    private readonly IReadOnlyDictionary<string, SignatureInfo> _signatureInfoMap;
    private readonly AhoCorasickMatcher _signatureMatcher;
    private readonly ConcurrentDictionary<string, int> _stats = new();
    private readonly bool _verbose;
    private int _ddxConvertedCount;
    private int _ddxConvertFailedCount;
    private bool _disposed;

    public MemoryCarver(string outputDir, int maxFilesPerType = 10000, bool convertDdxToDds = true,
        List<string>? fileTypes = null, bool verbose = false, bool saveAtlas = false)
    {
        _outputDir = outputDir;
        _maxFilesPerType = maxFilesPerType;
        _verbose = verbose;
        _saveAtlas = saveAtlas;

        _signatureMatcher = new AhoCorasickMatcher();
        _signatureInfoMap = GetSignaturesToSearch(fileTypes);

        foreach (var (name, info) in _signatureInfoMap)
        {
            _signatureMatcher.AddPattern(name, info.Magic);
            _stats[name] = 0;
        }
        _signatureMatcher.Build();

        if (convertDdxToDds)
        {
            try
            {
                _ddxConverter = new DdxSubprocessConverter(_verbose, saveAtlas: _saveAtlas);
                _convertDdxToDds = true;
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"Warning: {ex.Message}");
                _convertDdxToDds = false;
            }
        }
    }

    public int DdxConvertedCount => _ddxConvertedCount;
    public int DdxConvertFailedCount => _ddxConvertFailedCount;
    public IReadOnlyDictionary<string, int> Stats => _stats;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyDictionary<string, SignatureInfo> GetSignaturesToSearch(List<string>? fileTypes)
    {
        return fileTypes == null || fileTypes.Count == 0
            ? FileSignatures.Signatures
            : FileSignatures.Signatures.Where(kvp => fileTypes.Any(ft =>
                kvp.Key.Equals(ft, StringComparison.OrdinalIgnoreCase) ||
                kvp.Key.StartsWith(ft + "_", StringComparison.OrdinalIgnoreCase)))
              .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public async Task<List<CarveEntry>> CarveDumpAsync(string dumpPath, IProgress<double>? progress = null)
    {
        var dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        var outputPath = Path.Combine(_outputDir, BinaryUtils.SanitizeFilename(dumpName));
        Directory.CreateDirectory(outputPath);

        _manifest.Clear();
        _processedOffsets.Clear();
        foreach (var key in _stats.Keys) _stats[key] = 0;

        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        // Phase 1: Scanning (0-50%)
        var matches = FindAllMatches(accessor, fileInfo.Length, progress);
        
        // Phase 2: Extraction (50-100%)
        await ExtractMatchesAsync(accessor, fileInfo.Length, matches, outputPath, progress);
        
        await CarveManifest.SaveAsync(outputPath, _manifest);
        progress?.Report(1.0);

        return [.. _manifest];
    }

    private List<(string SigName, long Offset)> FindAllMatches(MemoryMappedViewAccessor accessor, long fileSize, IProgress<double>? progress)
    {
        const int chunkSize = 64 * 1024 * 1024;
        if (_signatureInfoMap.Count == 0) return [];

        var maxPatternLength = _signatureMatcher.MaxPatternLength;
        var allMatches = new List<(string SigName, long Offset)>();
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize + maxPatternLength);

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                var toRead = (int)Math.Min(chunkSize + maxPatternLength, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                foreach (var (name, _, position) in _signatureMatcher.Search(buffer.AsSpan(0, toRead), offset))
                    if (_stats.GetValueOrDefault(name, 0) < _maxFilesPerType)
                        allMatches.Add((name, position));

                offset += chunkSize;
                // Scanning is 0-50% of total progress
                progress?.Report(Math.Min((double)offset / fileSize * 0.5, 0.5));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }

        return allMatches.DistinctBy(m => m.Offset).OrderBy(m => m.Offset).ToList();
    }

    private async Task ExtractMatchesAsync(MemoryMappedViewAccessor accessor, long fileSize,
        List<(string SigName, long Offset)> matches, string outputPath, IProgress<double>? progress)
    {
        if (matches.Count == 0) return;

        var processedCount = 0;
        var totalMatches = matches.Count;

        await Parallel.ForEachAsync(matches, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (match, _) =>
            {
                if (_stats.GetValueOrDefault(match.SigName, 0) >= _maxFilesPerType) return;
                if (!_processedOffsets.TryAdd(match.Offset, 0)) return;

                var sigInfo = _signatureInfoMap[match.SigName];
                var extraction = PrepareExtraction(accessor, fileSize, match.Offset, match.SigName, sigInfo, outputPath);

                if (extraction != null)
                {
                    _stats.AddOrUpdate(match.SigName, 1, (_, v) => v + 1);
                    await WriteFileAsync(extraction.Value.outputFile, extraction.Value.data, match.Offset, match.SigName, extraction.Value.fileSize);
                }

                // Report extraction progress (50-100% of total)
                var currentCount = Interlocked.Increment(ref processedCount);
                // Only report every ~1% to avoid flooding the UI
                if (progress != null && (currentCount % Math.Max(1, totalMatches / 100) == 0 || currentCount == totalMatches))
                {
                    var extractionProgress = (double)currentCount / totalMatches;
                    progress.Report(0.5 + extractionProgress * 0.5);
                }
            });
    }

    private static (string outputFile, byte[] data, int fileSize)? PrepareExtraction(
        MemoryMappedViewAccessor accessor, long fileSize, long offset, string sigName, SignatureInfo sigInfo, string outputPath)
    {
        var headerSize = (int)Math.Min(Math.Min(sigInfo.MaxSize, 64 * 1024), fileSize - offset);
        var headerBuffer = ArrayPool<byte>.Shared.Rent(headerSize);

        try
        {
            accessor.ReadArray(offset, headerBuffer, 0, headerSize);
            var headerSpan = headerBuffer.AsSpan(0, headerSize);

            var parser = ParserFactory.GetParser(sigName);
            int fileDataSize;
            string? customFilename = null;

            if (parser != null)
            {
                var parseResult = parser.ParseHeader(headerSpan);
                if (parseResult == null) return null;
                fileDataSize = parseResult.EstimatedSize;
                if (parseResult.Metadata.TryGetValue("safeName", out var safeName)) customFilename = safeName.ToString();
            }
            else fileDataSize = Math.Min(sigInfo.MaxSize, headerSize);

            if (fileDataSize < sigInfo.MinSize || fileDataSize > sigInfo.MaxSize) return null;
            fileDataSize = (int)Math.Min(fileDataSize, fileSize - offset);

            var typeFolder = string.IsNullOrEmpty(sigInfo.Folder) ? sigName : sigInfo.Folder;
            var typePath = Path.Combine(outputPath, typeFolder);
            Directory.CreateDirectory(typePath);

            var filename = customFilename ?? $"{offset:X8}";
            var outputFile = Path.Combine(typePath, $"{filename}{sigInfo.Extension}");
            var counter = 1;
            while (File.Exists(outputFile)) outputFile = Path.Combine(typePath, $"{filename}_{counter++}{sigInfo.Extension}");

            var fileData = fileDataSize <= headerSize ? headerSpan[..fileDataSize].ToArray() : new byte[fileDataSize];
            if (fileDataSize > headerSize) accessor.ReadArray(offset, fileData, 0, fileDataSize);

            return (outputFile, fileData, fileDataSize);
        }
        finally { ArrayPool<byte>.Shared.Return(headerBuffer); }
    }

    private async Task WriteFileAsync(string outputFile, byte[] data, long offset, string sigName, int fileSize)
    {
        if (_convertDdxToDds && sigName is "ddx_3xdo" or "ddx_3xdr" && IsDdxFile(data))
        {
            var result = await ConvertDdxAsync(data, outputFile, offset, sigName, fileSize);
            if (result) return;
        }

        await File.WriteAllBytesAsync(outputFile, data);
        _manifest.Add(new CarveEntry { FileType = sigName, Offset = offset, SizeInDump = fileSize, SizeOutput = fileSize, Filename = Path.GetFileName(outputFile) });
    }

    private async Task<bool> ConvertDdxAsync(byte[] data, string outputFile, long offset, string sigName, int fileSize)
    {
        if (_ddxConverter == null) return false;

        var is3Xdr = data.Length >= 4 && data[3] == 'R';
        if (is3Xdr) { Interlocked.Increment(ref _ddxConvertFailedCount); return false; }

        var result = await _ddxConverter.ConvertFromMemoryWithResultAsync(data);
        if (!result.Success || result.DdsData == null) { Interlocked.Increment(ref _ddxConvertFailedCount); return false; }

        var ddsOutputFile = Path.ChangeExtension(outputFile.Replace(Path.DirectorySeparatorChar + "ddx" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + "textures" + Path.DirectorySeparatorChar), ".dds");
        Directory.CreateDirectory(Path.GetDirectoryName(ddsOutputFile)!);

        await File.WriteAllBytesAsync(ddsOutputFile, result.DdsData);
        if (result.AtlasData != null && _saveAtlas) await File.WriteAllBytesAsync(ddsOutputFile.Replace(".dds", "_full_atlas.dds"), result.AtlasData);

        _manifest.Add(new CarveEntry
        {
            FileType = sigName, Offset = offset, SizeInDump = fileSize, SizeOutput = result.DdsData.Length,
            Filename = Path.GetFileName(ddsOutputFile), IsCompressed = true,
            ContentType = result.IsPartial ? "dds_partial" : "dds_converted", IsPartial = result.IsPartial, Notes = result.Notes
        });

        Interlocked.Increment(ref _ddxConvertedCount);
        return true;
    }

    private static bool IsDdxFile(byte[] data) => data.Length >= 4 && data[0] == '3' && data[1] == 'X' && data[2] == 'D' && data[3] is (byte)'O' or (byte)'R';
}
