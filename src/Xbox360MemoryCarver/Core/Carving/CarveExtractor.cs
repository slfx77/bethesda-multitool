using System.Buffers;
using System.IO.MemoryMappedFiles;
using Xbox360MemoryCarver.Core.Formats;

namespace Xbox360MemoryCarver.Core.Carving;

/// <summary>
///     Handles the extraction and preparation of carved file data.
/// </summary>
internal static class CarveExtractor
{
    /// <summary>
    ///     Prepare extraction data for a single match.
    /// </summary>
    public static ExtractionData? PrepareExtraction(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        long offset,
        string signatureId,
        IFileFormat format,
        string outputPath)
    {
        // Read data before and after the signature for context
        const int preReadSize = 512;
        var actualPreRead = (int)Math.Min(preReadSize, offset);
        var readStart = offset - actualPreRead;

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

            var parseResult = format.Parse(span, sigOffset);
            if (parseResult == null) return null;

            var extractionInfo = BuildExtractionInfo(parseResult, actualPreRead);
            var (leadingBytes, customFilename, originalPath, metadata) = extractionInfo;

            // Adjust for leading bytes (e.g., comments before script signature)
            var adjustedOffset = offset - leadingBytes;
            var adjustedSize = parseResult.EstimatedSize + leadingBytes;

            if (adjustedSize < format.MinSize || adjustedSize > format.MaxSize) return null;

            adjustedSize = (int)Math.Min(adjustedSize, fileSize - adjustedOffset);

            var outputFile = BuildOutputPath(outputPath, signatureId, format, customFilename, offset, parseResult.OutputFolderOverride);

            // Read the actual file data (including any leading bytes)
            var fileData = new byte[adjustedSize];
            accessor.ReadArray(adjustedOffset, fileData, 0, adjustedSize);

            return new ExtractionData(outputFile, fileData, adjustedSize, originalPath, metadata);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (int leadingBytes, string? customFilename, string? originalPath, Dictionary<string, object>? metadata
        )
        BuildExtractionInfo(ParseResult parseResult, int actualPreRead)
    {
        string? customFilename = null;
        string? originalPath = null;
        var leadingBytes = 0;

        // Get the safe filename for extraction
        if (parseResult.Metadata.TryGetValue("safeName", out var safeName)) customFilename = safeName.ToString();

        // Get the original path for the manifest (DDX textures)
        if (parseResult.Metadata.TryGetValue("texturePath", out var pathObj) && pathObj is string path)
            originalPath = path;

        // Get embedded path for XMA files
        if (parseResult.Metadata.TryGetValue("embeddedPath", out var embeddedPathObj) &&
            embeddedPathObj is string embeddedPath)
            originalPath ??= embeddedPath;

        // Check for leading comments (scripts with comments before the scn keyword)
        if (parseResult.Metadata.TryGetValue("leadingCommentSize", out var leadingObj) &&
            leadingObj is int leading)
            leadingBytes = Math.Min(leading, actualPreRead);

        return (leadingBytes, customFilename, originalPath, parseResult.Metadata);
    }

    private static string BuildOutputPath(string outputPath, string signatureId, IFileFormat format,
        string? customFilename, long offset, string? outputFolderOverride = null)
    {
        // Use override if provided, otherwise fall back to format default
        var typeFolder = outputFolderOverride ?? (string.IsNullOrEmpty(format.OutputFolder) ? signatureId : format.OutputFolder);
        var typePath = Path.Combine(outputPath, typeFolder);
        Directory.CreateDirectory(typePath);

        var filename = customFilename ?? $"{offset:X8}";
        var outputFile = Path.Combine(typePath, $"{filename}{format.Extension}");

        var counter = 1;
        while (File.Exists(outputFile))
            outputFile = Path.Combine(typePath, $"{filename}_{counter++}{format.Extension}");

        return outputFile;
    }
}

/// <summary>
///     Data prepared for file extraction.
/// </summary>
internal readonly record struct ExtractionData(
    string OutputFile,
    byte[] Data,
    int FileSize,
    string? OriginalPath,
    Dictionary<string, object>? Metadata);
