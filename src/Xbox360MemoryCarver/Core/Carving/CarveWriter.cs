using System.Collections.Concurrent;
using Xbox360MemoryCarver.Core.Formats;

namespace Xbox360MemoryCarver.Core.Carving;

/// <summary>
///     Parameters for file write operations.
/// </summary>
internal sealed record WriteFileParams(
    string OutputFile,
    byte[] Data,
    long Offset,
    string SignatureId,
    int FileSize,
    string? OriginalPath,
    Dictionary<string, object>? Metadata);

/// <summary>
///     Handles file writing, conversion, and repair operations for carved files.
/// </summary>
internal sealed class CarveWriter
{
    private readonly Action<CarveEntry> _addToManifest;
    private readonly Dictionary<string, IFileConverter> _converters;
    private readonly bool _enableConversion;
    private readonly ConcurrentBag<long> _failedConversionOffsets = [];
    private readonly bool _saveAtlas;

    public CarveWriter(
        Dictionary<string, IFileConverter> converters,
        bool enableConversion,
        bool saveAtlas,
        Action<CarveEntry> addToManifest)
    {
        _converters = converters;
        _enableConversion = enableConversion;
        _saveAtlas = saveAtlas;
        _addToManifest = addToManifest;
    }

    /// <summary>
    ///     Offsets of files that failed conversion (DDX→DDS, XMA→WAV, etc.).
    /// </summary>
    public IReadOnlyCollection<long> FailedConversionOffsets => _failedConversionOffsets;

    public async Task WriteFileAsync(WriteFileParams p)
    {
        var format = FormatRegistry.GetBySignatureId(p.SignatureId);

        // Try conversion if available for this format
        if (_enableConversion && format != null && _converters.TryGetValue(format.FormatId, out var converter) &&
            converter.CanConvert(p.SignatureId, p.Metadata))
        {
            var convertResult = await TryConvertAsync(converter, p);
            if (convertResult) return;
        }

        // Repair files if needed using IFileRepairer interface
        var outputData = p.Data;
        var isRepaired = false;
        if (format is IFileRepairer repairer && repairer.NeedsRepair(p.Metadata))
        {
            outputData = repairer.Repair(p.Data, p.Metadata);
            isRepaired = outputData != p.Data;
        }

        await WriteFileWithRetryAsync(p.OutputFile, outputData);
        _addToManifest(new CarveEntry
        {
            FileType = p.SignatureId,
            Offset = p.Offset,
            SizeInDump = p.FileSize,
            SizeOutput = outputData.Length,
            Filename = Path.GetFileName(p.OutputFile),
            OriginalPath = p.OriginalPath,
            Notes = isRepaired ? "Repaired" : null,
            Metadata = p.Metadata
        });
    }

    private async Task<bool> TryConvertAsync(IFileConverter converter, WriteFileParams p)
    {
        var result = await converter.ConvertAsync(p.Data, p.Metadata);
        if (!result.Success || result.DdsData == null)
        {
            // Track that this file failed conversion
            _failedConversionOffsets.Add(p.Offset);
            return false;
        }

        var format = FormatRegistry.GetBySignatureId(p.SignatureId);
        var originalFolder = format?.OutputFolder ?? p.SignatureId;
        var targetFolder = converter.TargetFolder;

        var convertedOutputFile = Path.ChangeExtension(p.OutputFile.Replace(
            Path.DirectorySeparatorChar + originalFolder + Path.DirectorySeparatorChar,
            Path.DirectorySeparatorChar + targetFolder + Path.DirectorySeparatorChar), converter.TargetExtension);
        Directory.CreateDirectory(Path.GetDirectoryName(convertedOutputFile)!);

        await WriteFileWithRetryAsync(convertedOutputFile, result.DdsData);

        // Save atlas if available
        if (result.AtlasData != null && _saveAtlas)
            await WriteFileWithRetryAsync(convertedOutputFile.Replace(".dds", "_full_atlas.dds"), result.AtlasData);

        _addToManifest(new CarveEntry
        {
            FileType = p.SignatureId,
            Offset = p.Offset,
            SizeInDump = p.FileSize,
            SizeOutput = result.DdsData.Length,
            Filename = Path.GetFileName(convertedOutputFile),
            OriginalPath = p.OriginalPath,
            IsCompressed = true,
            ContentType = result.IsPartial ? "converted_partial" : "converted",
            IsPartial = result.IsPartial,
            Notes = result.Notes,
            Metadata = p.Metadata
        });

        return true;
    }

    /// <summary>
    ///     Write file with retry logic for handling concurrent access to same filename.
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
                var dir = Path.GetDirectoryName(outputFile)!;
                var nameWithoutExt = Path.GetFileNameWithoutExtension(outputFile);
                var ext = Path.GetExtension(outputFile);
                var suffix = Guid.NewGuid().ToString("N")[..8];
                currentPath = Path.Combine(dir, $"{nameWithoutExt}_{suffix}{ext}");
            }
    }
}
