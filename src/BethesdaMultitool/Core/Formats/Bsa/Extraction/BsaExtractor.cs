using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
using BethesdaMultitool.Core.Formats.Xma;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Bsa.Extraction;

/// <summary>
///     Extracts files from BSA archives.
///     BSA format is always little-endian, even for Xbox 360 archives.
///     Thread-safe: uses memory-mapped file for lock-free concurrent reads.
/// </summary>
public sealed class BsaExtractor : IDisposable
{
    // Converter cache - keyed by extension (e.g., ".ddx", ".nif")
    // Uses FormatRegistry to resolve converters. Guarded by _converterLock: converters are
    // resolved lazily on first touch, which can race when conversion-enabled extraction fans out.
    private readonly Dictionary<string, IFileConverter?> _converterCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _converterLock = new();
    private readonly bool _defaultCompressed;
    private readonly bool _embedFileNames;
    // Concurrent set (value unused): Enable* toggles can race conversion-enabled extraction workers.
    private readonly ConcurrentDictionary<string, byte> _enabledExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly MemoryMappedFile _mappedFile;

    // One long-lived whole-file read-only view; every read passes an absolute offset per call, so
    // concurrent ExtractFile calls share it without locks (same pattern as Ba2Extractor, which
    // adopted it because per-read CreateViewAccessor is a real MapViewOfFile each time).
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _archiveFileLength;
    private bool _disposed;
    private bool _verbose;

    // Set once by ArchiveHandleRegistry when this instance becomes a shared handle; the
    // conversion toggles below are per-instance state and must stay off shared instances.
    private bool _shared;

    // XMA needs special handling since it uses XmaWavConverter
    private bool _xmaConversionEnabled;

    /// <summary>
    ///     Create an extractor for a BSA archive.
    /// </summary>
    public BsaExtractor(string filePath)
    {
        Archive = BsaParser.Parse(filePath);
        _archiveFileLength = new FileInfo(filePath).Length;
        _mappedFile = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _defaultCompressed = Archive.Header.DefaultCompressed;
        _embedFileNames = Archive.Header.EmbedFileNames;
    }

    /// <summary>
    ///     Create an extractor from an already-parsed archive.
    /// </summary>
    public BsaExtractor(BsaArchive archive, Stream stream)
    {
        Archive = archive;
        if (stream is not FileStream fs)
        {
            throw new ArgumentException("Stream must be a FileStream for memory-mapped access", nameof(stream));
        }

        _archiveFileLength = fs.Length;
        _mappedFile =
            MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        _view = _mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _defaultCompressed = Archive.Header.DefaultCompressed;
        _embedFileNames = Archive.Header.EmbedFileNames;
    }

    /// <summary>The parsed archive.</summary>
    public BsaArchive Archive { get; }

    /// <summary>Whether DDX conversion is enabled and available.</summary>
    public bool DdxConversionEnabled => _enabledExtensions.ContainsKey(".ddx") && GetOrCreateConverter(".ddx") != null;

    /// <summary>Whether XMA conversion is enabled and available.</summary>
    public bool XmaConversionEnabled => _xmaConversionEnabled;

    /// <summary>Whether NIF conversion is enabled and available.</summary>
    public bool NifConversionEnabled => _enabledExtensions.ContainsKey(".nif") && GetOrCreateConverter(".nif") != null;

    public void Dispose()
    {
        if (!_disposed)
        {
            _view.Dispose();
            _mappedFile.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    ///     Marks this extractor as a shared, registry-owned handle. The raw
    ///     <see cref="ExtractFile(BsaFileRecord)" /> path never consults conversion state, so
    ///     read-only sharing is safe — but the Enable* conversion toggles mutate per-instance
    ///     state visible to every holder, so they throw on a shared instance. Sites that need
    ///     conversion (Xbox→PC extraction flows) must open a private extractor.
    /// </summary>
    internal void MarkShared() => _shared = true;

    private void ThrowIfShared()
    {
        if (_shared)
        {
            throw new InvalidOperationException(
                "Conversion toggles are per-instance state and this BsaExtractor is a shared " +
                "registry handle; open a private BsaExtractor for conversion-enabled extraction.");
        }
    }

    /// <summary>
    ///     Enable DDX to DDS conversion during extraction.
    /// </summary>
    /// <param name="enable">Whether to enable conversion.</param>
    /// <param name="verbose">Whether to enable verbose logging for the converter.</param>
    /// <returns>True if conversion was successfully enabled, false if DDXConv is not available.</returns>
    public bool EnableDdxConversion(bool enable, bool verbose = false)
    {
        ThrowIfShared();
        _verbose = verbose;

        if (!enable)
        {
            _enabledExtensions.TryRemove(".ddx", out _);
            return true;
        }

        var converter = GetOrCreateConverter(".ddx");
        if (converter == null)
        {
            return false;
        }

        _enabledExtensions.TryAdd(".ddx", 0);
        return true;
    }

    /// <summary>
    ///     Convert DDX data to DDS.
    /// </summary>
    /// <param name="ddxData">The DDX file data.</param>
    /// <returns>Conversion result with DDS data if successful.</returns>
    public async Task<ConversionResult> ConvertDdxAsync(byte[] ddxData)
    {
        var converter = GetOrCreateConverter(".ddx");
        if (converter == null)
        {
            return new ConversionResult { Success = false, Notes = "Converter not initialized" };
        }

        return await converter.ConvertAsync(ddxData);
    }

    /// <summary>
    ///     Enable XMA to WAV conversion during extraction.
    ///     Requires FFmpeg to be installed and in PATH.
    /// </summary>
    /// <param name="enable">Whether to enable conversion.</param>
    /// <returns>True if conversion was successfully enabled, false if FFmpeg is not available.</returns>
    public bool EnableXmaConversion(bool enable)
    {
        ThrowIfShared();
        if (!enable)
        {
            _enabledExtensions.TryRemove(".xma", out _);
            _xmaConversionEnabled = false;
            return true;
        }

        if (!XmaWavConverter.IsAvailable)
        {
            _xmaConversionEnabled = false;
            return false;
        }

        _xmaConversionEnabled = true;

        _enabledExtensions.TryAdd(".xma", 0);
        return true;
    }

    /// <summary>
    ///     Convert XMA data to WAV (PC format).
    /// </summary>
    /// <param name="xmaData">The XMA file data.</param>
    /// <returns>Conversion result with WAV data if successful.</returns>
    public async Task<ConversionResult> ConvertXmaAsync(byte[] xmaData)
    {
        if (!_xmaConversionEnabled)
        {
            return new ConversionResult { Success = false, Notes = "XMA converter not initialized" };
        }

        return await XmaWavConverter.ConvertAsync(xmaData);
    }

    /// <summary>
    ///     Enable NIF conversion (Xbox 360 big-endian to PC little-endian) during extraction.
    /// </summary>
    /// <param name="enable">Whether to enable conversion.</param>
    /// <param name="verbose">Whether to enable verbose logging for the converter.</param>
    /// <returns>True if conversion was successfully enabled.</returns>
    public bool EnableNifConversion(bool enable, bool verbose = false)
    {
        ThrowIfShared();
        _verbose = verbose;

        if (!enable)
        {
            _enabledExtensions.TryRemove(".nif", out _);
            return true;
        }

        var converter = GetOrCreateConverter(".nif");
        if (converter == null)
        {
            return false;
        }

        _enabledExtensions.TryAdd(".nif", 0);
        return true;
    }

    /// <summary>
    ///     Convert Xbox 360 NIF (big-endian) to PC NIF (little-endian).
    /// </summary>
    /// <param name="nifData">The NIF file data.</param>
    /// <returns>Conversion result with PC NIF data if successful.</returns>
    public async Task<ConversionResult> ConvertNifAsync(byte[] nifData)
    {
        var converter = GetOrCreateConverter(".nif");
        if (converter == null)
        {
            return new ConversionResult { Success = false, Notes = "NIF converter not initialized" };
        }

        // Get the format to check if conversion is needed
        var format = FormatRegistry.GetByExtension(".nif");
        if (format == null)
        {
            return new ConversionResult { Success = false, Notes = "NIF format not found" };
        }

        // Check if the NIF is big-endian (Xbox 360) before converting
        var parseResult = format.Parse(nifData);
        if (parseResult == null)
        {
            return new ConversionResult { Success = false, Notes = "Invalid NIF file" };
        }

        var metadata = parseResult.Metadata;
        if (!converter.CanConvert("nif", metadata))
            // Already little-endian (PC format), no conversion needed
        {
            return new ConversionResult
            {
                Success = true,
                OutputData = nifData,
                Notes = "NIF is already in PC format (little-endian), no conversion needed"
            };
        }

        return await converter.ConvertAsync(nifData, metadata);
    }

    /// <summary>
    ///     Get or create a converter for the given extension using FormatRegistry.
    /// </summary>
    private IFileConverter? GetOrCreateConverter(string extension)
    {
        lock (_converterLock)
        {
            if (_converterCache.TryGetValue(extension, out var cached))
            {
                return cached;
            }

            var converter = FormatRegistry.GetConverterByExtension(extension);
            if (converter != null)
            {
                converter.Initialize(_verbose);
            }

            _converterCache[extension] = converter;
            return converter;
        }
    }

    /// <summary>
    ///     Find the offset just past the last non-zero byte in the underlying archive file.
    ///     Returns the archive length when the file is fully intact.
    ///     <para>
    ///         Partially-recovered ("partial data") BSAs keep an intact header + file table
    ///         but have a zero-filled payload tail: the file is allocated to its full size,
    ///         yet only the leading region was actually written. Any file record whose data
    ///         offset is at or beyond this boundary extracts to all zeros. Callers use the
    ///         boundary to drop those ghost entries so resolution can fall through to a
    ///         complete source instead of silently packing zeros.
    ///     </para>
    ///     <para>
    ///         Detection is a binary search for the last non-zero byte over 4 KB chunks
    ///         (~log2(size) reads), so it costs only a handful of reads even on multi-GB
    ///         archives. It assumes the dead region is a contiguous tail — the observed
    ///         failure mode for truncated recoveries; mid-file holes are not detected here.
    ///     </para>
    /// </summary>
    public long FindDataTruncationBoundary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        const int ChunkSize = 4096;
        var fileLength = _archiveFileLength;
        if (fileLength <= 0)
        {
            return fileLength;
        }

        // If the final chunk holds any data, the file isn't tail-truncated — common case.
        var lastChunkStart = Math.Max(0, fileLength - ChunkSize);
        if (!IsRegionZero(lastChunkStart, (int)(fileLength - lastChunkStart)))
        {
            return fileLength;
        }

        // Binary search the last chunk that contains a non-zero byte. lo = highest chunk
        // start known to contain data (none yet); hi = lowest chunk start known to be zero.
        long lo = 0;
        var hi = lastChunkStart;
        long lastDataChunkStart = -1;
        while (lo <= hi)
        {
            var midChunk = lo + (hi - lo) / 2 / ChunkSize * ChunkSize;
            var len = (int)Math.Min(ChunkSize, fileLength - midChunk);
            if (IsRegionZero(midChunk, len))
            {
                hi = midChunk - ChunkSize;
            }
            else
            {
                lastDataChunkStart = midChunk;
                lo = midChunk + ChunkSize;
            }
        }

        if (lastDataChunkStart < 0)
        {
            // Whole file is zero (header/table would have failed to parse, but be safe).
            return 0;
        }

        // Find the exact last non-zero byte within the last data-bearing chunk.
        var chunkLen = (int)Math.Min(ChunkSize, fileLength - lastDataChunkStart);
        var buffer = ArrayPool<byte>.Shared.Rent(chunkLen);
        try
        {
            using var accessor =
                _mappedFile.CreateViewAccessor(lastDataChunkStart, chunkLen, MemoryMappedFileAccess.Read);
            accessor.ReadArray(0, buffer, 0, chunkLen);
            for (var i = chunkLen - 1; i >= 0; i--)
            {
                if (buffer[i] != 0)
                {
                    return lastDataChunkStart + i + 1;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return lastDataChunkStart;
    }

    private bool IsRegionZero(long offset, int length)
    {
        if (length <= 0)
        {
            return true;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            using var accessor = _mappedFile.CreateViewAccessor(offset, length, MemoryMappedFileAccess.Read);
            accessor.ReadArray(0, buffer, 0, length);
            return BinaryUtils.IsAllZero(buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Extract a single file to a byte array.
    ///     Thread-safe without locks: all reads pass absolute offsets to the shared read-only
    ///     view accessor (no cursor state), so concurrent calls never contend.
    /// </summary>
    public byte[] ExtractFile(BsaFileRecord file)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Determine if this file is compressed
        var isCompressed = _defaultCompressed != file.CompressionToggle;

        // Calculate data offset and size (use long for offset arithmetic)
        var dataOffset = (long)file.Offset;
        var dataSize = (int)file.Size;

        // Skip embedded file name if present
        if (_embedFileNames)
        {
            var nameLen = _view.ReadByte(dataOffset);
            dataOffset += 1 + nameLen;
            dataSize -= 1 + nameLen;
        }

        if (isCompressed)
        {
            // First 4 bytes are uncompressed size (little-endian)
            var uncompressedSize = _view.ReadUInt32(dataOffset);

            var compressedSize = dataSize - 4;

            // Edge case: if stored size is exactly 4 bytes, there's no compressed data
            // This means the original file was empty (0 bytes)
            if (compressedSize <= 0)
            {
                return uncompressedSize == 0
                    ? []
                    : throw new InvalidDataException(
                        $"Compressed file has no data but uncompressed size is {uncompressedSize}");
            }

            var compressedData = ArrayPool<byte>.Shared.Rent(compressedSize);
            try
            {
                _view.ReadArray(dataOffset + 4, compressedData, 0, compressedSize);

                // Skyrim Special Edition (BSA v105) compresses with LZ4 block format; v103/v104
                // (Oblivion / FO3 / FNV / Skyrim LE) use zlib (occasionally raw deflate).
                if (Archive.Header.Version >= 105)
                {
                    return Lz4BlockDecoder.DecodeFrame(
                        compressedData.AsSpan(0, compressedSize), (int)uncompressedSize);
                }

                // Decompress using zlib (deflate with 2-byte header)
                var result = new byte[uncompressedSize];
                using var compressedStream = new MemoryStream(compressedData, 0, compressedSize);

                // Skip zlib header (2 bytes) - BSA uses raw deflate sometimes, zlib other times
                // Try zlib first, fall back to raw deflate
                try
                {
                    using var zlibStream = new ZLibStream(compressedStream, CompressionMode.Decompress);
                    zlibStream.ReadExactly(result, 0, (int)uncompressedSize);
                }
                catch
                {
                    // Try raw deflate
                    compressedStream.Position = 0;
                    using var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress);
                    deflateStream.ReadExactly(result, 0, (int)uncompressedSize);
                }

                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(compressedData);
            }
        }

        // Uncompressed - just read the data
        var uncompressedResult = new byte[dataSize];
        _view.ReadArray(dataOffset, uncompressedResult, 0, dataSize);
        return uncompressedResult;
    }

    /// <summary>
    ///     Extract a single file to disk, optionally converting Xbox 360 formats to PC.
    ///     Supports: DDX->DDS, XMA->WAV, NIF (big-endian to little-endian).
    /// </summary>
    public async Task<BsaExtractResult> ExtractFileToDiskAsync(BsaFileRecord file, string outputDir,
        bool overwrite = false)
    {
        var outputPath = Path.Combine(outputDir, file.FullPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var extension = Path.GetExtension(file.Name ?? "").ToLowerInvariant();
        var wasCompressed = _defaultCompressed != file.CompressionToggle;

        // Determine conversion - short-circuit on extension first (cheapest check)
        var (conversionType, targetExtension) = GetConversionInfo(extension);
        if (targetExtension != null)
        {
            outputPath = Path.ChangeExtension(outputPath, targetExtension);
        }

        if (!overwrite && File.Exists(outputPath))
        {
            return new BsaExtractResult
            {
                SourcePath = file.FullPath,
                OutputPath = outputPath,
                Success = true,
                OriginalSize = file.Size,
                ExtractedSize = new FileInfo(outputPath).Length,
                WasCompressed = wasCompressed,
                WasConverted = false,
                Error = "File already exists (skipped)"
            };
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            var data = ExtractFile(file);

            // Apply conversion if configured
            if (conversionType != null)
            {
                return await TryConvertAndWriteAsync(
                    file, data, outputDir, outputPath, conversionType, wasCompressed);
            }

            // No conversion - write as-is
            await File.WriteAllBytesAsync(outputPath, data);
            return new BsaExtractResult
            {
                SourcePath = file.FullPath,
                OutputPath = outputPath,
                Success = true,
                OriginalSize = file.Size,
                ExtractedSize = data.Length,
                WasCompressed = wasCompressed,
                WasConverted = false
            };
        }
        catch (Exception ex)
        {
            return new BsaExtractResult
            {
                SourcePath = file.FullPath,
                OutputPath = outputPath,
                Success = false,
                OriginalSize = file.Size,
                ExtractedSize = 0,
                WasCompressed = wasCompressed,
                WasConverted = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    ///     Determine conversion type and target extension for a file.
    ///     Short-circuits on extension first (cheapest check).
    /// </summary>
    private (string? ConversionType, string? TargetExtension) GetConversionInfo(string extension)
    {
        return extension switch
        {
            ".ddx" when _enabledExtensions.ContainsKey(".ddx") && GetOrCreateConverter(".ddx") != null
                => ("DDX->DDS", ".dds"),
            ".xma" when _enabledExtensions.ContainsKey(".xma") && XmaWavConverter.IsAvailable
                => ("XMA->WAV", ".wav"),
            ".nif" when _enabledExtensions.ContainsKey(".nif") && GetOrCreateConverter(".nif") != null
                => ("NIF BE->LE", null), // NIF keeps same extension
            _ => (null, null)
        };
    }

    /// <summary>
    ///     Try to convert file data and write to disk, falling back to original on failure.
    /// </summary>
    private async Task<BsaExtractResult> TryConvertAndWriteAsync(
        BsaFileRecord file,
        byte[] data,
        string outputDir,
        string outputPath,
        string conversionType,
        bool wasCompressed)
    {
        var conversionResult = conversionType switch
        {
            "DDX->DDS" => await ConvertDdxAsync(data),
            "XMA->WAV" => await ConvertXmaAsync(data),
            "NIF BE->LE" => await ConvertNifAsync(data),
            _ => new ConversionResult { Success = false, Notes = "Unknown conversion type" }
        };

        // Handle NIF "already PC format" case
        var wasConverted = true;
        var actualConversionType = conversionType;
        if (conversionType == "NIF BE->LE" && conversionResult.Notes?.Contains("already") == true)
        {
            wasConverted = false;
            actualConversionType = null;
        }

        if (conversionResult is { Success: true, OutputData: not null })
        {
            await File.WriteAllBytesAsync(outputPath, conversionResult.OutputData);
            return new BsaExtractResult
            {
                SourcePath = file.FullPath,
                OutputPath = outputPath,
                Success = true,
                OriginalSize = file.Size,
                ExtractedSize = conversionResult.OutputData.Length,
                WasCompressed = wasCompressed,
                WasConverted = wasConverted,
                ConversionType = actualConversionType
            };
        }

        // Conversion failed - save as original
        var originalPath = Path.Combine(outputDir, file.FullPath);
        await File.WriteAllBytesAsync(originalPath, data);
        return new BsaExtractResult
        {
            SourcePath = file.FullPath,
            OutputPath = originalPath,
            Success = true,
            OriginalSize = file.Size,
            ExtractedSize = data.Length,
            WasCompressed = wasCompressed,
            WasConverted = false,
            Error = $"{conversionType} conversion failed: {conversionResult.Notes}"
        };
    }

    /// <summary>
    ///     Extract all files to a directory.
    /// </summary>
    public async Task<List<BsaExtractResult>> ExtractAllAsync(
        string outputDir,
        bool overwrite = false,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BsaExtractResult>();
        var allFiles = Archive.AllFiles.ToList();
        var total = allFiles.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = allFiles[i];
            progress?.Report((i + 1, total, file.FullPath));

            var result = await ExtractFileToDiskAsync(file, outputDir, overwrite);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    ///     Extract files matching a filter.
    /// </summary>
    public async Task<List<BsaExtractResult>> ExtractFilteredAsync(
        string outputDir,
        Func<BsaFileRecord, bool> filter,
        bool overwrite = false,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BsaExtractResult>();
        var filteredFiles = Archive.AllFiles.Where(filter).ToList();
        var total = filteredFiles.Count;

        for (var i = 0; i < total; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = filteredFiles[i];
            progress?.Report((i + 1, total, file.FullPath));

            var result = await ExtractFileToDiskAsync(file, outputDir, overwrite);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    ///     Get file extension statistics.
    /// </summary>
    public Dictionary<string, int> GetExtensionStats()
    {
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Archive.AllFiles)
        {
            var ext = Path.GetExtension(file.Name ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
            {
                ext = "(no extension)";
            }

            stats.TryGetValue(ext, out var count);
            stats[ext] = count + 1;
        }

        return stats.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    ///     Get folder statistics.
    /// </summary>
    public Dictionary<string, int> GetFolderStats()
    {
        return Archive.Folders
            .Where(f => f.Name is not null)
            .ToDictionary(f => f.Name!, f => f.Files.Count)
            .OrderByDescending(kv => kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }
}
