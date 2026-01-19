// Copyright (c) 2026 Xbox360MemoryCarver Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.IO.Compression;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Formats.Xma;

namespace Xbox360MemoryCarver.Core.Formats.Bsa;

/// <summary>
///     Extracts files from BSA archives.
///     BSA format is always little-endian, even for Xbox 360 archives.
/// </summary>
public sealed class BsaExtractor : IDisposable
{
    // Converter cache - keyed by extension (e.g., ".ddx", ".nif")
    // Uses FormatRegistry to resolve converters
    private readonly Dictionary<string, IFileConverter?> _converterCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _defaultCompressed;
    private readonly bool _embedFileNames;
    private readonly HashSet<string> _enabledExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileStream _stream;
    private bool _disposed;
    private bool _verbose;

    // XMA needs special handling since it uses XmaOggConverter, not XmaFormat's WAV converter
    private XmaOggConverter? _xmaOggConverter;

    /// <summary>
    ///     Create an extractor for a BSA archive.
    /// </summary>
    public BsaExtractor(string filePath)
    {
        Archive = BsaParser.Parse(filePath);
        _stream = File.OpenRead(filePath);
        _defaultCompressed = Archive.Header.DefaultCompressed;
        _embedFileNames = Archive.Header.EmbedFileNames;
    }

    /// <summary>
    ///     Create an extractor from an already-parsed archive.
    /// </summary>
    public BsaExtractor(BsaArchive archive, Stream stream)
    {
        Archive = archive;
        _stream = stream as FileStream ?? throw new ArgumentException("Stream must be a FileStream", nameof(stream));
        _defaultCompressed = Archive.Header.DefaultCompressed;
        _embedFileNames = Archive.Header.EmbedFileNames;
    }

    /// <summary>The parsed archive.</summary>
    public BsaArchive Archive { get; }

    /// <summary>Whether DDX conversion is enabled and available.</summary>
    public bool DdxConversionEnabled => _enabledExtensions.Contains(".ddx") && GetOrCreateConverter(".ddx") != null;

    /// <summary>Whether XMA conversion is enabled and available.</summary>
    public bool XmaConversionEnabled => _enabledExtensions.Contains(".xma") && _xmaOggConverter?.IsAvailable == true;

    /// <summary>Whether NIF conversion is enabled and available.</summary>
    public bool NifConversionEnabled => _enabledExtensions.Contains(".nif") && GetOrCreateConverter(".nif") != null;

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream.Dispose();
            _disposed = true;
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
        _verbose = verbose;

        if (!enable)
        {
            _enabledExtensions.Remove(".ddx");
            return true;
        }

        var converter = GetOrCreateConverter(".ddx");
        if (converter == null)
        {
            return false;
        }

        _enabledExtensions.Add(".ddx");
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
    ///     Enable XMA to OGG conversion during extraction.
    ///     Requires FFmpeg to be installed and in PATH.
    /// </summary>
    /// <param name="enable">Whether to enable conversion.</param>
    /// <returns>True if conversion was successfully enabled, false if FFmpeg is not available.</returns>
    public bool EnableXmaConversion(bool enable)
    {
        if (!enable)
        {
            _enabledExtensions.Remove(".xma");
            return true;
        }

        // XMA uses special XmaOggConverter for OGG output (not the default WAV from XmaFormat)
        _xmaOggConverter ??= new XmaOggConverter();
        if (!_xmaOggConverter.IsAvailable)
        {
            _xmaOggConverter = null;
            return false;
        }

        _enabledExtensions.Add(".xma");
        return true;
    }

    /// <summary>
    ///     Convert XMA data to OGG Vorbis (PC format).
    /// </summary>
    /// <param name="xmaData">The XMA file data.</param>
    /// <returns>Conversion result with OGG data if successful.</returns>
    public async Task<ConversionResult> ConvertXmaAsync(byte[] xmaData)
    {
        if (_xmaOggConverter == null)
        {
            return new ConversionResult { Success = false, Notes = "XMA converter not initialized" };
        }

        return await _xmaOggConverter.ConvertAsync(xmaData);
    }

    /// <summary>
    ///     Enable NIF conversion (Xbox 360 big-endian to PC little-endian) during extraction.
    /// </summary>
    /// <param name="enable">Whether to enable conversion.</param>
    /// <param name="verbose">Whether to enable verbose logging for the converter.</param>
    /// <returns>True if conversion was successfully enabled.</returns>
    public bool EnableNifConversion(bool enable, bool verbose = false)
    {
        _verbose = verbose;

        if (!enable)
        {
            _enabledExtensions.Remove(".nif");
            return true;
        }

        var converter = GetOrCreateConverter(".nif");
        if (converter == null)
        {
            return false;
        }

        _enabledExtensions.Add(".nif");
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

    /// <summary>
    ///     Extract a single file to a byte array.
    /// </summary>
    public byte[] ExtractFile(BsaFileRecord file)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stream.Position = file.Offset;

        // Determine if this file is compressed
        var isCompressed = _defaultCompressed != file.CompressionToggle;

        // Skip embedded file name if present
        var dataOffset = 0;
        if (_embedFileNames)
        {
            var nameLen = _stream.ReadByte();
            dataOffset = 1 + nameLen;
            _stream.Position = file.Offset + dataOffset;
        }

        var dataSize = (int)file.Size - dataOffset;

        if (isCompressed)
        {
            // First 4 bytes are uncompressed size (little-endian)
            var uncompressedSizeBytes = new byte[4];
            _stream.ReadExactly(uncompressedSizeBytes, 0, 4);
            var uncompressedSize = BitConverter.ToUInt32(uncompressedSizeBytes, 0);

            var compressedSize = dataSize - 4;
            var compressedData = ArrayPool<byte>.Shared.Rent(compressedSize);
            try
            {
                _stream.ReadExactly(compressedData, 0, compressedSize);

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

        {
            // Uncompressed - just read the data
            var result = new byte[dataSize];
            _stream.ReadExactly(result, 0, dataSize);
            return result;
        }
    }

    /// <summary>
    ///     Extract a single file to disk, optionally converting Xbox 360 formats to PC.
    ///     Supports: DDX->DDS, XMA->OGG, NIF (big-endian to little-endian).
    /// </summary>
    public async Task<BsaExtractResult> ExtractFileToDiskAsync(BsaFileRecord file, string outputDir,
        bool overwrite = false)
    {
        var outputPath = Path.Combine(outputDir, file.FullPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var extension = Path.GetExtension(file.Name ?? "").ToLowerInvariant();

        // Determine conversion type based on enabled extensions
        var shouldConvertDdx = _enabledExtensions.Contains(".ddx") && GetOrCreateConverter(".ddx") != null &&
                               extension == ".ddx";
        var shouldConvertXma = _enabledExtensions.Contains(".xma") && _xmaOggConverter?.IsAvailable == true &&
                               extension == ".xma";
        var shouldConvertNif = _enabledExtensions.Contains(".nif") && GetOrCreateConverter(".nif") != null &&
                               extension == ".nif";

        // Adjust output path for conversions that change extension
        if (shouldConvertDdx)
        {
            outputPath = Path.ChangeExtension(outputPath, ".dds");
        }
        else if (shouldConvertXma)
        {
            outputPath = Path.ChangeExtension(outputPath, ".ogg");
        }
        // NIF keeps same extension

        if (!overwrite && File.Exists(outputPath))
        {
            return new BsaExtractResult
            {
                SourcePath = file.FullPath,
                OutputPath = outputPath,
                Success = true,
                OriginalSize = file.Size,
                ExtractedSize = new FileInfo(outputPath).Length,
                WasCompressed = _defaultCompressed != file.CompressionToggle,
                WasConverted = false,
                Error = "File already exists (skipped)"
            };
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            var data = ExtractFile(file);

            // Convert DDX to DDS
            if (shouldConvertDdx)
            {
                var conversionResult = await ConvertDdxAsync(data);
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
                        WasCompressed = _defaultCompressed != file.CompressionToggle,
                        WasConverted = true,
                        ConversionType = "DDX->DDS"
                    };
                }

                // Conversion failed - save as original DDX
                outputPath = Path.Combine(outputDir, file.FullPath);
                await File.WriteAllBytesAsync(outputPath, data);
                return new BsaExtractResult
                {
                    SourcePath = file.FullPath,
                    OutputPath = outputPath,
                    Success = true,
                    OriginalSize = file.Size,
                    ExtractedSize = data.Length,
                    WasCompressed = _defaultCompressed != file.CompressionToggle,
                    WasConverted = false,
                    Error = $"DDX conversion failed: {conversionResult.Notes}"
                };
            }

            // Convert XMA to OGG
            if (shouldConvertXma)
            {
                var conversionResult = await ConvertXmaAsync(data);
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
                        WasCompressed = _defaultCompressed != file.CompressionToggle,
                        WasConverted = true,
                        ConversionType = "XMA->OGG"
                    };
                }

                // Conversion failed - save as original XMA
                outputPath = Path.Combine(outputDir, file.FullPath);
                await File.WriteAllBytesAsync(outputPath, data);
                return new BsaExtractResult
                {
                    SourcePath = file.FullPath,
                    OutputPath = outputPath,
                    Success = true,
                    OriginalSize = file.Size,
                    ExtractedSize = data.Length,
                    WasCompressed = _defaultCompressed != file.CompressionToggle,
                    WasConverted = false,
                    Error = $"XMA conversion failed: {conversionResult.Notes}"
                };
            }

            // Convert NIF (Xbox 360 big-endian to PC little-endian)
            if (shouldConvertNif)
            {
                var conversionResult = await ConvertNifAsync(data);
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
                        WasCompressed = _defaultCompressed != file.CompressionToggle,
                        WasConverted = !conversionResult.Notes?.Contains("already") == true,
                        ConversionType = conversionResult.Notes?.Contains("already") == true ? null : "NIF BE->LE"
                    };
                }

                // Conversion failed - save as original NIF
                await File.WriteAllBytesAsync(outputPath, data);
                return new BsaExtractResult
                {
                    SourcePath = file.FullPath,
                    OutputPath = outputPath,
                    Success = true,
                    OriginalSize = file.Size,
                    ExtractedSize = data.Length,
                    WasCompressed = _defaultCompressed != file.CompressionToggle,
                    WasConverted = false,
                    Error = $"NIF conversion failed: {conversionResult.Notes}"
                };
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
                WasCompressed = _defaultCompressed != file.CompressionToggle,
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
                WasCompressed = _defaultCompressed != file.CompressionToggle,
                WasConverted = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    ///     Extract a single file to disk (synchronous, no conversion).
    /// </summary>
    [Obsolete("Use ExtractFileToDiskAsync for Xbox 360 to PC conversion support (DDX, XMA, NIF)")]
    public BsaExtractResult ExtractFileToDisk(BsaFileRecord file, string outputDir, bool overwrite = false)
    {
        var outputPath = Path.Combine(outputDir, file.FullPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;

        if (!overwrite && File.Exists(outputPath))
        {
            return new BsaExtractResult
            {
                SourcePath = file.FullPath,
                OutputPath = outputPath,
                Success = true,
                OriginalSize = file.Size,
                ExtractedSize = new FileInfo(outputPath).Length,
                WasCompressed = _defaultCompressed != file.CompressionToggle,
                Error = "File already exists (skipped)"
            };
        }

        try
        {
            Directory.CreateDirectory(outputDirectory);
            var data = ExtractFile(file);
            File.WriteAllBytes(outputPath, data);

            return new BsaExtractResult
            {
                SourcePath = file.FullPath,
                OutputPath = outputPath,
                Success = true,
                OriginalSize = file.Size,
                ExtractedSize = data.Length,
                WasCompressed = _defaultCompressed != file.CompressionToggle
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
                WasCompressed = _defaultCompressed != file.CompressionToggle,
                Error = ex.Message
            };
        }
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
