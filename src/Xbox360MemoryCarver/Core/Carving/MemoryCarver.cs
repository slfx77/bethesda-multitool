using System.Buffers;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.FileTypes;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Carving;

/// <summary>
///     High-performance memory dump file carver using multi-pattern signature matching.
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
    private readonly HashSet<string> _signatureIdsToSearch;
    private readonly SignatureMatcher _signatureMatcher;
    private readonly ConcurrentDictionary<string, int> _stats = new();
    private int _ddxConvertedCount;
    private int _ddxConvertFailedCount;
    private bool _disposed;

    public MemoryCarver(string outputDir, int maxFilesPerType = 10000, bool convertDdxToDds = true,
        List<string>? fileTypes = null, bool verbose = false, bool saveAtlas = false)
    {
        _outputDir = outputDir;
        _maxFilesPerType = maxFilesPerType;
        _saveAtlas = saveAtlas;

        _signatureMatcher = new SignatureMatcher();
        _signatureIdsToSearch = GetSignatureIdsToSearch(fileTypes);

        foreach (var sigId in _signatureIdsToSearch)
        {
            var typeDef = FileTypeRegistry.GetBySignatureId(sigId);
            var sig = typeDef?.GetSignature(sigId);
            if (sig != null)
            {
                _signatureMatcher.AddPattern(sig.Id, sig.MagicBytes);
                _stats[sig.Id] = 0;
            }
        }

        _signatureMatcher.Build();

        if (convertDdxToDds)
            try
            {
                _ddxConverter = new DdxSubprocessConverter(verbose, saveAtlas: _saveAtlas);
                _convertDdxToDds = true;
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"Warning: {ex.Message}");
                _convertDdxToDds = false;
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

    private static HashSet<string> GetSignatureIdsToSearch(List<string>? fileTypes)
    {
        if (fileTypes == null || fileTypes.Count == 0)
            // Return all signature IDs
            return FileTypeRegistry.AllTypes
                .SelectMany(t => t.SignatureIds)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filter to requested types
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ft in fileTypes)
        {
            // Try to find by type ID first
            var typeDef = FileTypeRegistry.GetByTypeId(ft);
            if (typeDef != null)
            {
                foreach (var sigId in typeDef.SignatureIds) result.Add(sigId);
                continue;
            }

            // Try by signature ID
            typeDef = FileTypeRegistry.GetBySignatureId(ft);
            if (typeDef != null) result.Add(ft);
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
                // Scanning is 0-50% of total progress
                progress?.Report(Math.Min((double)offset / fileSize * 0.5, 0.5));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return allMatches.DistinctBy(m => m.Offset).OrderBy(m => m.Offset).ToList();
    }

    private async Task ExtractMatchesAsync(MemoryMappedViewAccessor accessor, long fileSize,
        List<(string SignatureId, long Offset)> matches, string outputPath, IProgress<double>? progress)
    {
        if (matches.Count == 0) return;

        var processedCount = 0;
        var totalMatches = matches.Count;

        await Parallel.ForEachAsync(matches,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            async (match, _) =>
            {
                if (_stats.GetValueOrDefault(match.SignatureId, 0) >= _maxFilesPerType) return;
                if (!_processedOffsets.TryAdd(match.Offset, 0)) return;

                var typeDef = FileTypeRegistry.GetBySignatureId(match.SignatureId);
                if (typeDef == null) return;

                var extraction = PrepareExtraction(accessor, fileSize, match.Offset, match.SignatureId, typeDef,
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

    private static (string outputFile, byte[] data, int fileSize, string? originalPath, Dictionary<string, object>? metadata)? PrepareExtraction(
        MemoryMappedViewAccessor accessor, long fileSize, long offset, string signatureId,
        FileTypeDefinition typeDef, string outputPath)
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
            ? Math.Min(typeDef.MaxSize, 512 * 1024) // 512KB for DDX boundary scanning
            : Math.Min(typeDef.MaxSize, 64 * 1024); // 64KB for other types

        var headerSize = (int)Math.Min(headerScanSize, fileSize - offset);
        var totalRead = actualPreRead + headerSize;
        var buffer = ArrayPool<byte>.Shared.Rent(totalRead);

        try
        {
            accessor.ReadArray(readStart, buffer, 0, totalRead);
            var span = buffer.AsSpan(0, totalRead);

            // The signature starts at actualPreRead offset in our buffer
            var sigOffset = actualPreRead;

            var parser = ParserRegistry.GetParserForSignature(signatureId);
            int fileDataSize;
            string? customFilename = null;
            string? originalPath = null;
            Dictionary<string, object>? metadata = null;
            var leadingBytes = 0;

            if (parser != null)
            {
                var parseResult = parser.ParseHeader(span, sigOffset);
                if (parseResult == null) return null;
                fileDataSize = parseResult.EstimatedSize;
                metadata = parseResult.Metadata;

                // Get the safe filename for extraction
                if (parseResult.Metadata.TryGetValue("safeName", out var safeName))
                    customFilename = safeName.ToString();

                // Get the original path for the manifest (DDX textures)
                if (parseResult.Metadata.TryGetValue("texturePath", out var pathObj) && pathObj is string path)
                    originalPath = path;

                // Get embedded path for XMA files
                if (parseResult.Metadata.TryGetValue("embeddedPath", out var embeddedPathObj) && embeddedPathObj is string embeddedPath)
                    originalPath ??= embeddedPath;

                // Check for leading comments (scripts with comments before the scn keyword)
                if (parseResult.Metadata.TryGetValue("leadingCommentSize", out var leadingObj) &&
                    leadingObj is int leading)
                    leadingBytes = Math.Min(leading, actualPreRead); // Can't go beyond what we pre-read
            }
            else
            {
                fileDataSize = Math.Min(typeDef.MaxSize, headerSize);
            }

            // Adjust for leading bytes (e.g., comments before script signature)
            var adjustedOffset = offset - leadingBytes;
            var adjustedSize = fileDataSize + leadingBytes;

            if (adjustedSize < typeDef.MinSize || adjustedSize > typeDef.MaxSize) return null;
            adjustedSize = (int)Math.Min(adjustedSize, fileSize - adjustedOffset);

            var typeFolder = string.IsNullOrEmpty(typeDef.OutputFolder) ? signatureId : typeDef.OutputFolder;
            var typePath = Path.Combine(outputPath, typeFolder);
            Directory.CreateDirectory(typePath);

            var filename = customFilename ?? $"{offset:X8}";
            var outputFile = Path.Combine(typePath, $"{filename}{typeDef.Extension}");
            var counter = 1;
            while (File.Exists(outputFile))
                outputFile = Path.Combine(typePath, $"{filename}_{counter++}{typeDef.Extension}");

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
        if (_convertDdxToDds && signatureId is "ddx_3xdo" or "ddx_3xdr" && IsDdxFile(data))
        {
            var result = await ConvertDdxAsync(data, outputFile, offset, signatureId, fileSize, originalPath);
            if (result) return;
        }

        // Repair XMA files if needed
        var outputData = data;
        var isRepaired = false;
        if (signatureId == "xma" && metadata != null)
        {
            outputData = Converters.XmaRepairUtil.TryRepair(data, metadata);
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

    private async Task<bool> ConvertDdxAsync(byte[] data, string outputFile, long offset, string signatureId,
        int fileSize, string? originalPath)
    {
        if (_ddxConverter == null) return false;

        var is3Xdr = data.Length >= 4 && data[3] == 'R';
        if (is3Xdr)
        {
            Interlocked.Increment(ref _ddxConvertFailedCount);
            return false;
        }

        var result = await _ddxConverter.ConvertFromMemoryWithResultAsync(data);
        if (!result.Success || result.DdsData == null)
        {
            Interlocked.Increment(ref _ddxConvertFailedCount);
            return false;
        }

        var ddsOutputFile = Path.ChangeExtension(outputFile.Replace(
            Path.DirectorySeparatorChar + "ddx" + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + "textures" + Path.DirectorySeparatorChar), ".dds");
        Directory.CreateDirectory(Path.GetDirectoryName(ddsOutputFile)!);

        // Handle file conflicts with retry logic (same texture may appear multiple times in memory)
        await WriteFileWithRetryAsync(ddsOutputFile, result.DdsData);
        if (result.AtlasData != null && _saveAtlas)
            await WriteFileWithRetryAsync(ddsOutputFile.Replace(".dds", "_full_atlas.dds"), result.AtlasData);

        _manifest.Add(new CarveEntry
        {
            FileType = signatureId,
            Offset = offset,
            SizeInDump = fileSize,
            SizeOutput = result.DdsData.Length,
            Filename = Path.GetFileName(ddsOutputFile),
            OriginalPath = originalPath,
            IsCompressed = true,
            ContentType = result.IsPartial ? "dds_partial" : "dds_converted",
            IsPartial = result.IsPartial,
            Notes = result.Notes
        });

        Interlocked.Increment(ref _ddxConvertedCount);
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
        {
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

    private static bool IsDdxFile(byte[] data)
    {
        return data.Length >= 4 && data[0] == '3' && data[1] == 'X' && data[2] == 'D' &&
               data[3] is (byte)'O' or (byte)'R';
    }
}
