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

    public MemoryCarver(string outputDir, int maxFilesPerType = 10000, bool convertDdxToDds = true,
        List<string>? fileTypes = null, bool verbose = false, bool saveAtlas = false)
    {
        _outputDir = outputDir;
        _maxFilesPerType = maxFilesPerType;
        _saveAtlas = saveAtlas;
        _enableConversion = convertDdxToDds;

        _signatureMatcher = new SignatureMatcher();
        _signatureIdsToSearch = GetSignatureIdsToSearch(fileTypes);

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

        _signatureMatcher.Build();

        // Initialize converters from format modules
        if (_enableConversion) InitializeConverters(verbose);
    }

    /// <summary>
    ///     Total count of converted files across all converters.
    /// </summary>
    public int TotalConvertedCount => _converters.Values.Sum(c => c.ConvertedCount);

    /// <summary>
    ///     Total count of failed conversions across all converters.
    /// </summary>
    public int TotalConvertFailedCount => _converters.Values.Sum(c => c.FailedCount);

    // Legacy properties for backward compatibility
    public int DdxConvertedCount => _converters.TryGetValue("ddx", out var c) ? c.ConvertedCount : 0;
    public int DdxConvertFailedCount => _converters.TryGetValue("ddx", out var c) ? c.FailedCount : 0;

    public IReadOnlyDictionary<string, int> Stats => _stats;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
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
        {
            // Return signature IDs from formats that have signature scanning enabled
            return FormatRegistry.All
                .Where(f => f.EnableSignatureScanning)
                .SelectMany(f => f.Signatures.Select(s => s.Id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Filter to requested types (user explicitly requested, so include even if scanning disabled)
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ft in fileTypes)
        {
            // Try to find by format ID first
            var format = FormatRegistry.GetByFormatId(ft);
            if (format != null)
            {
                foreach (var sig in format.Signatures) result.Add(sig.Id);
                continue;
            }

            // Try by signature ID
            format = FormatRegistry.GetBySignatureId(ft);
            if (format != null) result.Add(ft);
        }

        return result;
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

    private List<(string SignatureId, long Offset)> FindAllMatches(MemoryMappedViewAccessor accessor, long fileSize,
        IProgress<double>? progress)
    {
        const int chunkSize = 64 * 1024 * 1024;
        if (_signatureIdsToSearch.Count == 0)
        {
            return [];
        }

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
                {
                    if (_stats.GetValueOrDefault(name, 0) < _maxFilesPerType)
                    {
                        allMatches.Add((name, position));
                    }
                }

                offset += chunkSize;
                // Scanning is 0-50% of total progress
                progress?.Report(Math.Min((double)offset / fileSize * 0.5, 0.5));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var result = allMatches.DistinctBy(m => m.Offset).OrderBy(m => m.Offset).ToList();

        return result;
    }

    private async Task ExtractMatchesAsync(MemoryMappedViewAccessor accessor, long fileSize,
        List<(string SignatureId, long Offset)> matches, string outputPath, IProgress<double>? progress)
    {
        if (matches.Count == 0)
        {
            return;
        }

        var processedCount = 0;
        var totalMatches = matches.Count;

        await Parallel.ForEachAsync(matches,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (match, _) =>
            {
                if (_stats.GetValueOrDefault(match.SignatureId, 0) >= _maxFilesPerType)
                {
                    return;
                }

                if (!_processedOffsets.TryAdd(match.Offset, 0))
                {
                    return;
                }

                var format = FormatRegistry.GetBySignatureId(match.SignatureId);
                if (format == null)
                {
                    return;
                }

                var extraction = PrepareExtraction(accessor, fileSize, match.Offset, match.SignatureId, format,
                    outputPath);

                if (extraction != null)
                {
                    _stats.AddOrUpdate(match.SignatureId, 1, (_, v) => v + 1);
                    await WriteFileAsync(extraction.Value.outputFile, extraction.Value.data, match.Offset,
                        match.SignatureId, extraction.Value.fileSize, extraction.Value.originalPath,
                        extraction.Value.metadata);
                }

                // Report extraction progress (50-100% of total)
                var currentCount = Interlocked.Increment(ref processedCount);
                // Only report every ~1% to avoid flooding the UI
                if (progress != null &&
                    (currentCount % Math.Max(1, totalMatches / 100) == 0 || currentCount == totalMatches))
                {
                    var extractionProgress = (double)currentCount / totalMatches;
                    progress.Report(0.5 + extractionProgress * 0.5);
                }
            });
    }

    private static (string outputFile, byte[] data, int fileSize, string? originalPath, Dictionary<string, object>?
        metadata)? PrepareExtraction(
            MemoryMappedViewAccessor accessor, long fileSize, long offset, string signatureId,
            IFileFormat format, string outputPath)
    {
        // For DDX files, we need to read some data before the signature to find the path
        // For scripts, we need to look for leading comments before the signature
        // Read up to 512 bytes before and the header after
        const int preReadSize = 512;
        var actualPreRead = (int)Math.Min(preReadSize, offset);
        var readStart = offset - actualPreRead;

        // For DDX files, read more data to find boundaries (compressed textures can be large)
        // For other types, 64KB is usually enough for header parsing
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

            var sigOffset = actualPreRead;

            int fileDataSize;
            string? customFilename = null;
            string? originalPath = null;
            Dictionary<string, object>? metadata = null;
            var leadingBytes = 0;

            var parseResult = format.Parse(span, sigOffset);
            if (parseResult == null)
            {
                return null;
            }

            fileDataSize = parseResult.EstimatedSize;
            metadata = parseResult.Metadata;

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
                leadingBytes = Math.Min(leading, actualPreRead); // Can't go beyond what we pre-read
            }

            // Adjust for leading bytes (e.g., comments before script signature)
            var adjustedOffset = offset - leadingBytes;
            var adjustedSize = fileDataSize + leadingBytes;

            if (adjustedSize < format.MinSize || adjustedSize > format.MaxSize)
            {
                return null;
            }

            adjustedSize = (int)Math.Min(adjustedSize, fileSize - adjustedOffset);

            var typeFolder = string.IsNullOrEmpty(format.OutputFolder) ? signatureId : format.OutputFolder;
            var typePath = Path.Combine(outputPath, typeFolder);
            Directory.CreateDirectory(typePath);

            var filename = customFilename ?? $"{offset:X8}";
            var outputFile = Path.Combine(typePath, $"{filename}{format.Extension}");
            var counter = 1;
            while (File.Exists(outputFile))
            {
                outputFile = Path.Combine(typePath, $"{filename}_{counter++}{format.Extension}");
            }

            // Read the actual file data (including any leading bytes)
            var fileData = new byte[adjustedSize];
            accessor.ReadArray(adjustedOffset, fileData, 0, adjustedSize);

            return (outputFile, fileData, adjustedSize, originalPath, metadata);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteFileAsync(string outputFile, byte[] data, long offset, string signatureId, int fileSize,
        string? originalPath, Dictionary<string, object>? metadata)
    {
        var format = FormatRegistry.GetBySignatureId(signatureId);

        // Try conversion if available for this format
        if (_enableConversion && format != null && _converters.TryGetValue(format.FormatId, out var converter) &&
            converter.CanConvert(signatureId, metadata))
        {
            var convertResult = await TryConvertAsync(converter, data, outputFile, offset, signatureId, fileSize,
                originalPath, metadata);
            if (convertResult)
            {
                return;
            }
        }

        // Repair files if needed using IFileRepairer interface
        var outputData = data;
        var isRepaired = false;
        if (format is IFileRepairer repairer && repairer.NeedsRepair(metadata))
        {
            outputData = repairer.Repair(data, metadata);
            isRepaired = outputData != data;
        }

        await WriteFileWithRetryAsync(outputFile, outputData);
        _manifest.Add(new CarveEntry
        {
            FileType = signatureId,
            Offset = offset,
            SizeInDump = fileSize,
            SizeOutput = outputData.Length,
            Filename = Path.GetFileName(outputFile),
            OriginalPath = originalPath,
            Notes = isRepaired ? "Repaired" : null
        });
    }

    private async Task<bool> TryConvertAsync(IFileConverter converter, byte[] data, string outputFile, long offset,
        string signatureId, int fileSize, string? originalPath, Dictionary<string, object>? metadata)
    {
        var result = await converter.ConvertAsync(data, metadata);
        if (!result.Success || result.DdsData == null) return false;

        var format = FormatRegistry.GetBySignatureId(signatureId);
        var originalFolder = format?.OutputFolder ?? signatureId;
        var targetFolder = converter.TargetFolder;

        var convertedOutputFile = Path.ChangeExtension(outputFile.Replace(
            Path.DirectorySeparatorChar + originalFolder + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + targetFolder + Path.DirectorySeparatorChar), converter.TargetExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(convertedOutputFile)!);

        await WriteFileWithRetryAsync(convertedOutputFile, result.DdsData);

        // Save atlas if available
        if (result.AtlasData != null && _saveAtlas)
            await WriteFileWithRetryAsync(convertedOutputFile.Replace(".dds", "_full_atlas.dds"), result.AtlasData);

        _manifest.Add(new CarveEntry
        {
            FileType = signatureId,
            Offset = offset,
            SizeInDump = fileSize,
            SizeOutput = result.DdsData.Length,
            Filename = Path.GetFileName(convertedOutputFile),
            OriginalPath = originalPath,
            IsCompressed = true,
            ContentType = result.IsPartial ? "converted_partial" : "converted",
            IsPartial = result.IsPartial,
            Notes = result.Notes
        });

        return true;
    }

    /// <summary>
    ///     Write file with retry logic for handling concurrent access to same filename.
    ///     If file is locked, generates a unique filename with suffix.
    /// </summary>
    private static async Task WriteFileWithRetryAsync(string outputFile, byte[] data, int maxRetries = 3)
    {
        var currentPath = outputFile;
        for (var attempt = 0; attempt < maxRetries; attempt++)
            try
            {
                await File.WriteAllBytesAsync(currentPath, data);
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                // File might be locked or already exists from another thread
                // Generate a unique filename with offset-based suffix
                var dir = Path.GetDirectoryName(outputFile)!;
                var nameWithoutExt = Path.GetFileNameWithoutExtension(outputFile);
                var ext = Path.GetExtension(outputFile);
                var suffix = Guid.NewGuid().ToString("N")[..8];
                currentPath = Path.Combine(dir, $"{nameWithoutExt}_{suffix}{ext}");
            }
    }
}
