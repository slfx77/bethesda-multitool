using Xbox360MemoryCarver.Core.Formats;

namespace Xbox360MemoryCarver.Core.Carving;

/// <summary>
///     Handles file writing, conversion, and repair operations for carved files.
/// </summary>
internal sealed class CarveWriter
{
    private readonly Action<CarveEntry> _addToManifest;
    private readonly Dictionary<string, IFileConverter> _converters;
    private readonly bool _enableConversion;
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

    public async Task WriteFileAsync(
        string outputFile,
        byte[] data,
        long offset,
        string signatureId,
        int fileSize,
        string? originalPath,
        Dictionary<string, object>? metadata)
    {
        var format = FormatRegistry.GetBySignatureId(signatureId);

        // Try conversion if available for this format
        if (_enableConversion && format != null && _converters.TryGetValue(format.FormatId, out var converter) &&
            converter.CanConvert(signatureId, metadata))
        {
            var convertResult = await TryConvertAsync(converter, data, outputFile, offset, signatureId, fileSize,
                originalPath, metadata);
            if (convertResult) return;
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
        _addToManifest(new CarveEntry
        {
            FileType = signatureId,
            Offset = offset,
            SizeInDump = fileSize,
            SizeOutput = outputData.Length,
            Filename = Path.GetFileName(outputFile),
            OriginalPath = originalPath,
            Notes = isRepaired ? "Repaired" : null,
            Metadata = metadata
        });
    }

    private async Task<bool> TryConvertAsync(
        IFileConverter converter,
        byte[] data,
        string outputFile,
        long offset,
        string signatureId,
        int fileSize,
        string? originalPath,
        Dictionary<string, object>? metadata)
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

        _addToManifest(new CarveEntry
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
            Notes = result.Notes,
            Metadata = metadata
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
