using System.Buffers;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Text.Json;
using Xbox360MemoryCarver.Core.Converters;
using Xbox360MemoryCarver.Core.Models;
using Xbox360MemoryCarver.Core.Parsers;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Carving;

/// <summary>
/// Entry in the carving manifest.
/// </summary>
public class CarveEntry
{
    public string FileType { get; set; } = "";
    public long Offset { get; set; }
    public int SizeInDump { get; set; }
    public int SizeOutput { get; set; }
    public string Filename { get; set; } = "";
    public bool IsCompressed { get; set; }
    public string? ContentType { get; set; }
    public bool IsPartial { get; set; }
    public string? Notes { get; set; }
}

/// <summary>
/// High-performance memory dump file carver using:
/// - Aho-Corasick multi-pattern search (single pass for all signatures)
/// - Memory-mapped file I/O for zero-copy reads
/// - Parallel extraction with concurrent collections
/// - ArrayPool buffer reuse to minimize GC pressure
/// </summary>
public sealed class MemoryCarver : IDisposable
{
    private readonly string _outputDir;
    private readonly int _maxFilesPerType;
    private readonly ConcurrentDictionary<string, int> _stats;
    private readonly ConcurrentBag<CarveEntry> _manifest;
    private readonly ConcurrentDictionary<long, byte> _processedOffsets;
    private readonly bool _convertDdxToDds;
    private readonly bool _verbose;
    private readonly bool _saveAtlas;
    private readonly DdxSubprocessConverter? _ddxConverter;
    private int _ddxConvertedCount;
    private int _ddxConvertFailedCount;
    private readonly AhoCorasickMatcher _signatureMatcher;
    private readonly IReadOnlyDictionary<string, SignatureInfo> _signatureInfoMap;
    private bool _disposed;

    private static readonly JsonSerializerOptions CachedJsonOptions = new()
    {
        WriteIndented = true
    };

    public int DdxConvertedCount => _ddxConvertedCount;
    public int DdxConvertFailedCount => _ddxConvertFailedCount;
    public IReadOnlyDictionary<string, int> Stats => _stats;

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
        _stats = new ConcurrentDictionary<string, int>();
        _manifest = [];
        _processedOffsets = new ConcurrentDictionary<long, byte>();
        _verbose = verbose;
        _saveAtlas = saveAtlas;

        // Build signature matcher
        _signatureMatcher = new AhoCorasickMatcher();
        _signatureInfoMap = GetSignaturesToSearch(fileTypes);

        foreach ((string? name, SignatureInfo? info) in _signatureInfoMap)
        {
            _signatureMatcher.AddPattern(name, info.Magic);
            _stats[name] = 0;
        }

        _signatureMatcher.Build();

        // Initialize DDX converter if conversion is enabled
        if (convertDdxToDds)
        {
            try
            {
                _ddxConverter = new DdxSubprocessConverter(verbose: _verbose, saveAtlas: _saveAtlas);
                _convertDdxToDds = true;
                if (_verbose)
                {
                    Console.WriteLine($"DDXConv found at: {_ddxConverter.DdxConvPath}");
                }
            }
            catch (FileNotFoundException ex)
            {
                Console.WriteLine($"Warning: {ex.Message}");
                Console.WriteLine("DDX files will be saved without conversion.");
                _convertDdxToDds = false;
            }
        }
        else
        {
            _convertDdxToDds = false;
        }
    }

    private static IReadOnlyDictionary<string, SignatureInfo> GetSignaturesToSearch(List<string>? fileTypes) =>
        fileTypes == null || fileTypes.Count == 0
            ? FileSignatures.Signatures
            : FileSignatures.Signatures
                .Where(kvp => fileTypes.Any(ft =>
                    kvp.Key.Equals(ft, StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.StartsWith(ft + "_", StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    /// <summary>
    /// Carve files from a memory dump.
    /// </summary>
    public async Task<List<CarveEntry>> CarveDumpAsync(
        string dumpPath,
        IProgress<double>? progress = null)
    {
        string dumpName = Path.GetFileNameWithoutExtension(dumpPath);
        string outputPath = Path.Combine(_outputDir, BinaryUtils.SanitizeFilename(dumpName));
        Directory.CreateDirectory(outputPath);

        _manifest.Clear();
        _processedOffsets.Clear();
        foreach (string key in _stats.Keys)
        {
            _stats[key] = 0;
        }

        var fileInfo = new FileInfo(dumpPath);
        long fileSize = fileInfo.Length;

        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using MemoryMappedViewAccessor accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

        // Find all signature matches in a single pass
        List<(string SigName, long Offset)> matches = FindAllMatches(accessor, fileSize, progress);

        // Extract files in parallel
        await ExtractMatchesParallelAsync(accessor, fileSize, matches, outputPath);

        // Save manifest
        await SaveManifestAsync(outputPath);

        return [.. _manifest];
    }

    private List<(string SigName, long Offset)> FindAllMatches(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        IProgress<double>? progress)
    {
        const int chunkSize = 64 * 1024 * 1024; // 64MB chunks

        if (_signatureInfoMap.Count == 0)
        {
            return [];
        }

        int maxPatternLength = _signatureMatcher.MaxPatternLength;

        var allMatches = new List<(string SigName, long Offset)>();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(chunkSize + maxPatternLength);

        try
        {
            long offset = 0;
            while (offset < fileSize)
            {
                int toRead = (int)Math.Min(chunkSize + maxPatternLength, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                Span<byte> span = buffer.AsSpan(0, toRead);
                List<(string Name, byte[] Pattern, long Position)> matches = _signatureMatcher.Search(span, offset);

                foreach ((string? name, byte[] _, long position) in matches)
                {
                    if (_stats.GetValueOrDefault(name, 0) < _maxFilesPerType)
                    {
                        allMatches.Add((name, position));
                    }
                }

                offset += chunkSize;
                progress?.Report(Math.Min((double)offset / fileSize, 0.5));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return allMatches
            .DistinctBy(m => m.Offset)
            .OrderBy(m => m.Offset)
            .ToList();
    }

    private async Task ExtractMatchesParallelAsync(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        List<(string SigName, long Offset)> matches,
        string outputPath)
    {
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        await Parallel.ForEachAsync(matches, options, async (match, _) =>
        {
            if (_stats.GetValueOrDefault(match.SigName, 0) >= _maxFilesPerType)
            {
                return;
            }

            if (!_processedOffsets.TryAdd(match.Offset, 0))
            {
                return;
            }

            SignatureInfo sigInfo = _signatureInfoMap[match.SigName];

            try
            {
                (string outputFile, byte[] data, int fileSize)? extractionData = PrepareExtraction(accessor, fileSize, match.Offset, match.SigName, sigInfo, outputPath);

                if (extractionData != null)
                {
                    _stats.AddOrUpdate(match.SigName, 1, (_, v) => v + 1);

                    await WriteFileAsync(
                        extractionData.Value.outputFile,
                        extractionData.Value.data,
                        match.Offset,
                        match.SigName,
                        extractionData.Value.fileSize);
                }
            }
            catch (Exception ex)
            {
                // Log extraction errors in verbose mode for debugging
                if (_verbose)
                {
                    Console.WriteLine($"[Extract] Error at offset 0x{match.Offset:X8} ({match.SigName}): {ex.Message}");
                }
            }
        });
    }

    private static (string outputFile, byte[] data, int fileSize)? PrepareExtraction(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        long offset,
        string sigName,
        SignatureInfo sigInfo,
        string outputPath)
    {
        int headerSize = Math.Min(sigInfo.MaxSize, 64 * 1024);
        headerSize = (int)Math.Min(headerSize, fileSize - offset);

        byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(headerSize);
        try
        {
            accessor.ReadArray(offset, headerBuffer, 0, headerSize);
            Span<byte> headerSpan = headerBuffer.AsSpan(0, headerSize);

            IFileParser? parser = ParserFactory.GetParser(sigName);
            int fileDataSize;
            string? customFilename = null;

            if (parser != null)
            {
                ParseResult? parseResult = parser.ParseHeader(headerSpan);
                if (parseResult == null)
                {
                    return null;
                }

                fileDataSize = parseResult.EstimatedSize;

                if (parseResult.Metadata.TryGetValue("safeName", out object? safeName))
                {
                    customFilename = safeName.ToString();
                }
            }
            else
            {
                fileDataSize = Math.Min(sigInfo.MaxSize, headerSize);
            }

            if (fileDataSize < sigInfo.MinSize || fileDataSize > sigInfo.MaxSize)
            {
                return null;
            }

            fileDataSize = (int)Math.Min(fileDataSize, fileSize - offset);

            string typeFolder = string.IsNullOrEmpty(sigInfo.Folder) ? sigName : sigInfo.Folder;
            string typePath = Path.Combine(outputPath, typeFolder);
            Directory.CreateDirectory(typePath);

            string filename = customFilename ?? $"{offset:X8}";
            string outputFile = Path.Combine(typePath, $"{filename}{sigInfo.Extension}");

            int counter = 1;
            while (File.Exists(outputFile))
            {
                outputFile = Path.Combine(typePath, $"{filename}_{counter++}{sigInfo.Extension}");
            }

            byte[] fileData;
            if (fileDataSize <= headerSize)
            {
                fileData = headerSpan[..fileDataSize].ToArray();
            }
            else
            {
                fileData = new byte[fileDataSize];
                accessor.ReadArray(offset, fileData, 0, fileDataSize);
            }

            return (outputFile, fileData, fileDataSize);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBuffer);
        }
    }

    private async Task WriteFileAsync(string outputFile, byte[] data, long offset, string sigName, int fileSize)
    {
        try
        {
            // Check if this is a DDX file and conversion is enabled
            if (_convertDdxToDds &&
                sigName is "ddx_3xdo" or "ddx_3xdr" &&
                IsDdxFile(data))
            {
                DdxConversionResult? convertedResult = await TryConvertDdxAsync(data, outputFile, offset, sigName, fileSize);
                if (convertedResult != null)
                {
                    return;
                }
            }

            await File.WriteAllBytesAsync(outputFile, data);

            _manifest.Add(new CarveEntry
            {
                FileType = sigName,
                Offset = offset,
                SizeInDump = fileSize,
                SizeOutput = fileSize,
                Filename = Path.GetFileName(outputFile)
            });
        }
        catch (Exception ex)
        {
            _stats.AddOrUpdate(sigName, 0, (_, v) => Math.Max(0, v - 1));

            if (_verbose)
            {
                Console.WriteLine($"[Write] Error writing {sigName} at 0x{offset:X8}: {ex.Message}");
            }
        }
    }

    private async Task<DdxConversionResult?> TryConvertDdxAsync(byte[] data, string outputFile, long offset, string sigName, int fileSize)
    {
        DdxConversionResult result = await ConvertDdxToDdsAsync(data);

        if (!result.Success || result.DdsData == null)
        {
            Interlocked.Increment(ref _ddxConvertFailedCount);
            return null;
        }

        string ddsOutputFile = outputFile
            .Replace(Path.DirectorySeparatorChar + "ddx" + Path.DirectorySeparatorChar,
                     Path.DirectorySeparatorChar + "textures" + Path.DirectorySeparatorChar);
        ddsOutputFile = Path.ChangeExtension(ddsOutputFile, ".dds");

        string? texturesDir = Path.GetDirectoryName(ddsOutputFile);
        if (texturesDir != null)
        {
            Directory.CreateDirectory(texturesDir);
        }

        await File.WriteAllBytesAsync(ddsOutputFile, result.DdsData);

        if (result.AtlasData != null && _saveAtlas)
        {
            string atlasOutputFile = ddsOutputFile.Replace(".dds", "_full_atlas.dds");
            await File.WriteAllBytesAsync(atlasOutputFile, result.AtlasData);
        }

        _manifest.Add(new CarveEntry
        {
            FileType = sigName,
            Offset = offset,
            SizeInDump = fileSize,
            SizeOutput = result.DdsData.Length,
            Filename = Path.GetFileName(ddsOutputFile),
            IsCompressed = true,
            ContentType = result.IsPartial ? "dds_partial" : "dds_converted",
            IsPartial = result.IsPartial,
            Notes = result.Notes
        });

        Interlocked.Increment(ref _ddxConvertedCount);
        return result;
    }

    private static bool IsDdxFile(byte[] data) =>
        data.Length >= 4 && data[0] == '3' && data[1] == 'X' && data[2] == 'D' && data[3] is (byte)'O' or (byte)'R';

    private async Task<DdxConversionResult> ConvertDdxToDdsAsync(byte[] ddxData)
    {
        // Check magic first
        if (ddxData.Length < 4)
        {
            return DdxConversionResult.Failure("File too small");
        }

        bool is3Xdo = ddxData[0] == '3' && ddxData[1] == 'X' && ddxData[2] == 'D' && ddxData[3] == 'O';
        bool is3Xdr = ddxData[0] == '3' && ddxData[1] == 'X' && ddxData[2] == 'D' && ddxData[3] == 'R';

        // Validate DDX signature and check for unsupported 3XDR format
        return (!is3Xdo && !is3Xdr, is3Xdr, _ddxConverter) switch
        {
            (true, _, _) => DdxConversionResult.Failure("Not a DDX file"),
            (_, true, _) => DdxConversionResult.Failure("3XDR format not yet supported for conversion"),
            (_, _, null) => DdxConversionResult.Failure("DDX converter not available"),
            _ => await _ddxConverter.ConvertFromMemoryWithResultAsync(ddxData)
        };
    }

    private async Task SaveManifestAsync(string outputPath)
    {
        string manifestPath = Path.Combine(outputPath, "manifest.json");
        var manifestList = _manifest.ToList();
        string json = JsonSerializer.Serialize(manifestList, CachedJsonOptions);
        await File.WriteAllTextAsync(manifestPath, json);
    }

    /// <summary>
    /// Dispose resources held by the carver.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // DdxSubprocessConverter doesn't hold unmanaged resources,
        // but we clear the reference for GC
        GC.SuppressFinalize(this);
    }
}
