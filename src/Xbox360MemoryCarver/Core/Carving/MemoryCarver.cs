using System.Buffers;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core.Formats;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Carving;

/// <summary>
///     High-performance memory dump file carver using multi-pattern signature matching.
/// </summary>
public sealed class MemoryCarver : IDisposable
{
    private readonly Dictionary<string, IFileConverter> _converters = new();
    private readonly bool _enableConversion;
    private readonly ConcurrentBag<CarveEntry> _manifest = [];
    private readonly int _maxFilesPerType;
    private readonly string _outputDir;
    private readonly ConcurrentDictionary<long, byte> _processedOffsets = new();
    private readonly bool _saveAtlas;
    private readonly HashSet<string> _signatureIdsToSearch;
    private readonly SignatureMatcher _signatureMatcher;
    private readonly ConcurrentDictionary<string, int> _stats = new();
    private bool _disposed;
    private CarveWriter? _writer;

    public MemoryCarver(
        string outputDir,
        int maxFilesPerType = 10000,
        bool convertDdxToDds = true,
        List<string>? fileTypes = null,
        bool verbose = false,
        bool saveAtlas = false)
    {
        _outputDir = outputDir;
        _maxFilesPerType = maxFilesPerType;
        _saveAtlas = saveAtlas;
        _enableConversion = convertDdxToDds;

        _signatureMatcher = new SignatureMatcher();
        _signatureIdsToSearch = GetSignatureIdsToSearch(fileTypes);

        InitializeSignatures();
        _signatureMatcher.Build();

        if (_enableConversion) InitializeConverters(verbose);
    }

    public int DdxConvertedCount => _converters.TryGetValue("ddx", out var c) ? c.ConvertedCount : 0;
    public int DdxConvertFailedCount => _converters.TryGetValue("ddx", out var c) ? c.FailedCount : 0;
    public int XurConvertedCount => _converters.TryGetValue("xui", out var c) ? c.ConvertedCount : 0;
    public int XurConvertFailedCount => _converters.TryGetValue("xui", out var c) ? c.FailedCount : 0;
    public IReadOnlyDictionary<string, int> Stats => _stats;

    /// <summary>
    ///     Offsets of files that failed conversion (DDX→DDS, etc.).
    /// </summary>
    public IReadOnlyCollection<long> FailedConversionOffsets => _writer?.FailedConversionOffsets ?? [];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }

    public async Task<List<CarveEntry>> CarveDumpAsync(string dumpPath, IProgress<double>? progress = null)
    {
        var dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        var outputPath = Path.Combine(_outputDir, BinaryUtils.SanitizeFilename(dumpName));
        Directory.CreateDirectory(outputPath);

        Reset();

        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var matches = FindAllMatches(accessor, fileInfo.Length, progress);
        await ExtractMatchesAsync(accessor, fileInfo.Length, matches, outputPath, progress);
        await CarveManifest.SaveAsync(outputPath, _manifest);

        progress?.Report(1.0);
        return [.. _manifest];
    }

    private void Reset()
    {
        _manifest.Clear();
        _processedOffsets.Clear();
        foreach (var key in _stats.Keys) _stats[key] = 0;
    }

    private void InitializeSignatures()
    {
        foreach (var sigId in _signatureIdsToSearch)
        {
            var format = FormatRegistry.GetBySignatureId(sigId);
            var sig = format?.Signatures.FirstOrDefault(s => s.Id.Equals(sigId, StringComparison.OrdinalIgnoreCase));
            if (sig != null)
            {
                _signatureMatcher.AddPattern(sig.Id, sig.MagicBytes);
                _stats[sig.Id] = 0;
            }
        }
    }

    private void InitializeConverters(bool verbose)
    {
        var options = new Dictionary<string, object> { ["saveAtlas"] = _saveAtlas };

        foreach (var format in FormatRegistry.All)
            if (format is IFileConverter converter && converter.Initialize(verbose, options))
                _converters[format.FormatId] = converter;
    }

    private static HashSet<string> GetSignatureIdsToSearch(List<string>? fileTypes)
    {
        if (fileTypes == null || fileTypes.Count == 0)
            return FormatRegistry.All
                .Where(f => f.EnableSignatureScanning)
                .SelectMany(f => f.Signatures.Select(s => s.Id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ft in fileTypes)
        {
            var format = FormatRegistry.GetByFormatId(ft);
            if (format != null)
            {
                foreach (var sig in format.Signatures) result.Add(sig.Id);

                continue;
            }

            format = FormatRegistry.GetBySignatureId(ft);
            if (format != null) result.Add(ft);
        }

        return result;
    }

    private List<(string SignatureId, long Offset)> FindAllMatches(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        IProgress<double>? progress)
    {
        const int chunkSize = 64 * 1024 * 1024;
        if (_signatureIdsToSearch.Count == 0) return [];

        var maxPatternLength = _signatureMatcher.MaxPatternLength;
        var allMatches = new List<(string SignatureId, long Offset)>();
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
                progress?.Report(Math.Min((double)offset / fileSize * 0.5, 0.5));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return allMatches.DistinctBy(m => m.Offset).OrderBy(m => m.Offset).ToList();
    }

    private async Task ExtractMatchesAsync(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        List<(string SignatureId, long Offset)> matches,
        string outputPath,
        IProgress<double>? progress)
    {
        if (matches.Count == 0) return;

        _writer = new CarveWriter(_converters, _enableConversion, _saveAtlas, _manifest.Add);
        var processedCount = 0;
        var totalMatches = matches.Count;

        await Parallel.ForEachAsync(matches,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (match, _) =>
            {
                if (_stats.GetValueOrDefault(match.SignatureId, 0) >= _maxFilesPerType) return;
                if (!_processedOffsets.TryAdd(match.Offset, 0)) return;

                var format = FormatRegistry.GetBySignatureId(match.SignatureId);
                if (format == null) return;

                var extraction = CarveExtractor.PrepareExtraction(accessor, fileSize, match.Offset,
                    match.SignatureId, format, outputPath);

                if (extraction != null)
                {
                    _stats.AddOrUpdate(match.SignatureId, 1, (_, v) => v + 1);
                    await _writer.WriteFileAsync(new WriteFileParams(
                        extraction.Value.OutputFile,
                        extraction.Value.Data,
                        match.Offset,
                        match.SignatureId,
                        extraction.Value.FileSize,
                        extraction.Value.OriginalPath,
                        extraction.Value.Metadata));
                }

                var currentCount = Interlocked.Increment(ref processedCount);
                if (progress != null &&
                    (currentCount % Math.Max(1, totalMatches / 100) == 0 || currentCount == totalMatches))
                    progress.Report(0.5 + (double)currentCount / totalMatches * 0.5);
            });
    }
}
