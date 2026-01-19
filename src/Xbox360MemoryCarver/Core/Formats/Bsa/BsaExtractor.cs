// Copyright (c) 2026 Xbox360MemoryCarver Contributors
// Licensed under the MIT License.

using System.Buffers;
using System.IO.Compression;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Formats.Ddx;
using Xbox360MemoryCarver.Core.Formats.Nif;
using Xbox360MemoryCarver.Core.Formats.Xma;

namespace Xbox360MemoryCarver.Core.Formats.Bsa;

/// <summary>
/// Extracts files from BSA archives.
/// BSA format is always little-endian, even for Xbox 360 archives.
/// </summary>
public sealed class BsaExtractor : IDisposable
{
    private readonly BsaArchive _archive;
    private readonly FileStream _stream;
    private readonly bool _defaultCompressed;
    private readonly bool _embedFileNames;
    private bool _disposed;

    // DDX to DDS conversion support
    private DdxFormat? _ddxConverter;
    private bool _convertDdxEnabled;

    // XMA to OGG conversion support
    private XmaOggConverter? _xmaConverter;
    private bool _convertXmaEnabled;

    // NIF (Xbox 360 BE → PC LE) conversion support
    private NifFormat? _nifConverter;
    private bool _convertNifEnabled;

    /// <summary>
    /// Create an extractor for a BSA archive.
    /// </summary>
    public BsaExtractor(string filePath)
    {
        _archive = BsaParser.Parse(filePath);
        _stream = File.OpenRead(filePath);
        _defaultCompressed = _archive.Header.DefaultCompressed;
        _embedFileNames = _archive.Header.EmbedFileNames;
    }

    /// <summary>
    /// Create an extractor from an already-parsed archive.
    /// </summary>
    public BsaExtractor(BsaArchive archive, Stream stream)
    {
        _archive = archive;
        _stream = stream as FileStream ?? throw new ArgumentException("Stream must be a FileStream", nameof(stream));
        _defaultCompressed = _archive.Header.DefaultCompressed;
        _embedFileNames = _archive.Header.EmbedFileNames;
    }

    /// <summary>The parsed archive.</summary>
    public BsaArchive Archive => _archive;

    /// <summary>
    /// Enable DDX to DDS conversion during extraction.
    /// </summary>
    /// <param name="enable">Whether to enable conversion.</param>
    /// <param name="verbose">Whether to enable verbose logging for the converter.</param>
    /// <returns>True if conversion was successfully enabled, false if DDXConv is not available.</returns>
    public bool EnableDdxConversion(bool enable, bool verbose = false)
    {
        _convertDdxEnabled = enable;

        if (enable && _ddxConverter == null)
        {
            _ddxConverter = new DdxFormat();
            if (!_ddxConverter.Initialize(verbose))
            {
                _ddxConverter = null;
                _convertDdxEnabled = false;
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether DDX conversion is enabled and available.</summary>
    public bool DdxConversionEnabled => _convertDdxEnabled && _ddxConverter != null;

    /// <summary>
    /// Convert DDX data to DDS.
    /// </summary>
    /// <param name="ddxData">The DDX file data.</param>
    /// <returns>Conversion result with DDS data if successful.</returns>
    public async Task<DdxConversionResult> ConvertDdxAsync(byte[] ddxData)
    {
        if (_ddxConverter == null)
        {
            return new DdxConversionResult { Success = false, Notes = "Converter not initialized" };
        }

        return await _ddxConverter.ConvertAsync(ddxData);
    }

    /// <summary>
    /// Enable XMA to OGG conversion during extraction.
    /// Requires FFmpeg to be installed and in PATH.
    /// </summary>
    /// <param name="enable">Whether to enable conversion.</param>
    /// <returns>True if conversion was successfully enabled, false if FFmpeg is not available.</returns>
    public bool EnableXmaConversion(bool enable)
    {
        _convertXmaEnabled = enable;

        if (enable && _xmaConverter == null)
        {
            _xmaConverter = new XmaOggConverter();
            if (!_xmaConverter.IsAvailable)
            {
                _xmaConverter = null;
                _convertXmaEnabled = false;
                return false;
            }
        }

        return _xmaConverter?.IsAvailable ?? false;
    }

    /// <summary>Whether XMA conversion is enabled and available.</summary>
    public bool XmaConversionEnabled => _convertXmaEnabled && _xmaConverter != null;

    /// <summary>
    /// Convert XMA data to OGG Vorbis (PC format).
    /// </summary>
    /// <param name="xmaData">The XMA file data.</param>
    /// <returns>Conversion result with OGG data if successful.</returns>
    public async Task<DdxConversionResult> ConvertXmaAsync(byte[] xmaData)
    {
        if (_xmaConverter == null)
        {
            return new DdxConversionResult { Success = false, Notes = "XMA converter not initialized" };
        }

        return await _xmaConverter.ConvertAsync(xmaData);
    }

    /// <summary>
    /// Enable NIF conversion (Xbox 360 big-endian to PC little-endian) during extraction.
    /// </summary>
    /// <param name="enable">Whether to enable conversion.</param>
    /// <param name="verbose">Whether to enable verbose logging for the converter.</param>
    /// <returns>True if conversion was successfully enabled.</returns>
    public bool EnableNifConversion(bool enable, bool verbose = false)
    {
        _convertNifEnabled = enable;

        if (enable && _nifConverter == null)
        {
            _nifConverter = new NifFormat();
            _nifConverter.Initialize(verbose);
        }

        return true;
    }

    /// <summary>Whether NIF conversion is enabled and available.</summary>
    public bool NifConversionEnabled => _convertNifEnabled && _nifConverter != null;

    /// <summary>
    /// Convert Xbox 360 NIF (big-endian) to PC NIF (little-endian).
    /// </summary>
    /// <param name="nifData">The NIF file data.</param>
    /// <returns>Conversion result with PC NIF data if successful.</returns>
    public async Task<DdxConversionResult> ConvertNifAsync(byte[] nifData)
    {
        if (_nifConverter == null)
        {
            return new DdxConversionResult { Success = false, Notes = "NIF converter not initialized" };
        }

        // Check if the NIF is big-endian (Xbox 360) before converting
        var parseResult = _nifConverter.Parse(nifData);
        if (parseResult == null)
        {
            return new DdxConversionResult { Success = false, Notes = "Invalid NIF file" };
        }

        var metadata = parseResult.Metadata;
        if (!_nifConverter.CanConvert("nif", metadata))
        {
            // Already little-endian (PC format), no conversion needed
            return new DdxConversionResult
            {
                Success = true,
                DdsData = nifData,
                Notes = "NIF is already in PC format (little-endian), no conversion needed"
            };
        }

        return await _nifConverter.ConvertAsync(nifData, metadata);
    }

    /// <summary>
    /// Extract a single file to a byte array.
    /// </summary>
    public byte[] ExtractFile(BsaFileRecord file)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stream.Position = file.Offset;

        // Determine if this file is compressed
        var isCompressed = _defaultCompressed != file.CompressionToggle;

        // Skip embedded file name if present
        int dataOffset = 0;
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
            _stream.Read(uncompressedSizeBytes, 0, 4);
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
        else
        {
            // Uncompressed - just read the data
            var result = new byte[dataSize];
            _stream.ReadExactly(result, 0, dataSize);
            return result;
        }
    }

    /// <summary>
    /// Extract a single file to disk, optionally converting Xbox 360 formats to PC.
    /// Supports: DDX->DDS, XMA->OGG, NIF (big-endian to little-endian).
    /// </summary>
    public async Task<BsaExtractResult> ExtractFileToDiskAsync(BsaFileRecord file, string outputDir, bool overwrite = false)
    {
        var outputPath = Path.Combine(outputDir, file.FullPath);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        var extension = Path.GetExtension(file.Name ?? "").ToLowerInvariant();

        // Determine conversion type
        var shouldConvertDdx = _convertDdxEnabled && _ddxConverter != null && extension == ".ddx";
        var shouldConvertXma = _convertXmaEnabled && _xmaConverter != null && extension == ".xma";
        var shouldConvertNif = _convertNifEnabled && _nifConverter != null && extension == ".nif";

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
                var conversionResult = await _ddxConverter!.ConvertAsync(data);
                if (conversionResult.Success && conversionResult.DdsData != null)
                {
                    await File.WriteAllBytesAsync(outputPath, conversionResult.DdsData);
                    return new BsaExtractResult
                    {
                        SourcePath = file.FullPath,
                        OutputPath = outputPath,
                        Success = true,
                        OriginalSize = file.Size,
                        ExtractedSize = conversionResult.DdsData.Length,
                        WasCompressed = _defaultCompressed != file.CompressionToggle,
                        WasConverted = true,
                        ConversionType = "DDX->DDS"
                    };
                }
                else
                {
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
            }

            // Convert XMA to OGG
            if (shouldConvertXma)
            {
                var conversionResult = await ConvertXmaAsync(data);
                if (conversionResult.Success && conversionResult.DdsData != null)
                {
                    await File.WriteAllBytesAsync(outputPath, conversionResult.DdsData);
                    return new BsaExtractResult
                    {
                        SourcePath = file.FullPath,
                        OutputPath = outputPath,
                        Success = true,
                        OriginalSize = file.Size,
                        ExtractedSize = conversionResult.DdsData.Length,
                        WasCompressed = _defaultCompressed != file.CompressionToggle,
                        WasConverted = true,
                        ConversionType = "XMA->OGG"
                    };
                }
                else
                {
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
            }

            // Convert NIF (Xbox 360 big-endian to PC little-endian)
            if (shouldConvertNif)
            {
                var conversionResult = await ConvertNifAsync(data);
                if (conversionResult.Success && conversionResult.DdsData != null)
                {
                    await File.WriteAllBytesAsync(outputPath, conversionResult.DdsData);
                    return new BsaExtractResult
                    {
                        SourcePath = file.FullPath,
                        OutputPath = outputPath,
                        Success = true,
                        OriginalSize = file.Size,
                        ExtractedSize = conversionResult.DdsData.Length,
                        WasCompressed = _defaultCompressed != file.CompressionToggle,
                        WasConverted = !conversionResult.Notes?.Contains("already") == true,
                        ConversionType = conversionResult.Notes?.Contains("already") == true ? null : "NIF BE->LE"
                    };
                }
                else
                {
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
    /// Extract a single file to disk (synchronous, no conversion).
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
    /// Extract all files to a directory.
    /// </summary>
    public async Task<List<BsaExtractResult>> ExtractAllAsync(
        string outputDir,
        bool overwrite = false,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BsaExtractResult>();
        var allFiles = _archive.AllFiles.ToList();
        var total = allFiles.Count;

        for (int i = 0; i < total; i++)
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
    /// Extract files matching a filter.
    /// </summary>
    public async Task<List<BsaExtractResult>> ExtractFilteredAsync(
        string outputDir,
        Func<BsaFileRecord, bool> filter,
        bool overwrite = false,
        IProgress<(int current, int total, string fileName)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BsaExtractResult>();
        var filteredFiles = _archive.AllFiles.Where(filter).ToList();
        var total = filteredFiles.Count;

        for (int i = 0; i < total; i++)
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
    /// Get file extension statistics.
    /// </summary>
    public Dictionary<string, int> GetExtensionStats()
    {
        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _archive.AllFiles)
        {
            var ext = Path.GetExtension(file.Name ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
                ext = "(no extension)";

            stats.TryGetValue(ext, out var count);
            stats[ext] = count + 1;
        }
        return stats.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Get folder statistics.
    /// </summary>
    public Dictionary<string, int> GetFolderStats()
    {
        return _archive.Folders
            .Where(f => f.Name is not null)
            .ToDictionary(f => f.Name!, f => f.Files.Count)
            .OrderByDescending(kv => kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _stream.Dispose();
            _disposed = true;
        }
    }
}
