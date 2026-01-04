using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Xbox Media Audio (XMA) files.
/// </summary>
public class XmaParser : IFileParser
{
    private static readonly ushort[] XmaFormatCodes = [0x0165, 0x0166];

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 12) return null;

        if (!data.Slice(offset, 4).SequenceEqual("RIFF"u8)) return null;

        try
        {
            var riffSize = BinaryUtils.ReadUInt32LE(data, offset + 4);
            var reportedFileSize = (int)(riffSize + 8);
            var formatType = data.Slice(offset + 8, 4);

            if (!formatType.SequenceEqual("WAVE"u8)) return null;

            // Validate the reported size is reasonable
            if (reportedFileSize < 44 || reportedFileSize > 100 * 1024 * 1024) return null;

            // Check if the reported size extends past another file signature
            var boundarySize = ValidateAndAdjustSize(data, offset, reportedFileSize);

            // Parse chunks and look for XMA indicators
            return ParseXmaChunks(data, offset, reportedFileSize, boundarySize);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XmaParser] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Validate the RIFF-reported size and adjust if it extends past another file.
    /// </summary>
    private static int ValidateAndAdjustSize(ReadOnlySpan<byte> data, int offset, int reportedSize)
    {
        // Ensure we have enough data to scan
        if (offset >= data.Length) return reportedSize;

        // Use the shared boundary scanner to find if another file starts within the reported size
        // Start from minimum RIFF header size (44 bytes = RIFF header + fmt chunk minimum)
        const int minSize = 44;
        var availableData = data.Length - offset;
        if (availableData < minSize) return Math.Min(reportedSize, availableData);

        var maxScan = Math.Min(availableData, reportedSize);

        // Find next signature within the reported size
        var boundaryOffset = SignatureBoundaryScanner.FindNextSignatureWithRiffValidation(
            data, offset, minSize, maxScan, excludeSignature: "RIFF"u8);

        // If we found another file signature within the reported size, truncate to that point
        if (boundaryOffset > 0 && boundaryOffset < reportedSize)
        {
            return boundaryOffset;
        }

        return reportedSize;
    }

    /// <summary>
    ///     Parse RIFF chunks looking for XMA indicators and embedded paths.
    /// </summary>
    private static ParseResult? ParseXmaChunks(ReadOnlySpan<byte> data, int offset, int reportedSize, int boundarySize)
    {
        var searchOffset = offset + 12;
        var maxSearchOffset = Math.Min(offset + boundarySize, data.Length);

        string? embeddedPath = null;
        int? dataChunkOffset = null;
        int? dataChunkSize = null;
        ushort? formatTag = null;
        bool needsRepair = false;
        bool hasSeekChunk = false;

        while (searchOffset < maxSearchOffset - 8)
        {
            var chunkId = data.Slice(searchOffset, 4);
            var chunkSize = BinaryUtils.ReadUInt32LE(data, searchOffset + 4);

            // Prevent overflow
            if (chunkSize > int.MaxValue - 16) break;

            if (chunkId.SequenceEqual("fmt "u8))
            {
                // Check format tag
                if (searchOffset + 10 <= data.Length)
                {
                    formatTag = (ushort)(BinaryUtils.ReadUInt32LE(data, searchOffset + 8) & 0xFFFF);

                    // Check if fmt chunk contains an embedded path (corruption indicator)
                    // Don't trust the chunk size for corrupted files - scan a reasonable range
                    var fmtDataStart = searchOffset + 12; // After fmt header + format tag
                    var fmtDataEnd = Math.Min(searchOffset + 256, data.Length); // Scan up to 256 bytes

                    if (fmtDataEnd > fmtDataStart)
                    {
                        var path = TryExtractPath(data.Slice(fmtDataStart, fmtDataEnd - fmtDataStart));
                        if (path != null)
                        {
                            embeddedPath = path;
                            needsRepair = true;
                        }
                    }
                }
            }
            else if (chunkId.SequenceEqual("XMA2"u8))
            {
                // XMA2 chunk is a strong XMA indicator
                formatTag ??= 0x0166;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataChunkOffset = searchOffset;
                dataChunkSize = (int)chunkSize;
            }
            else if (chunkId.SequenceEqual("seek"u8))
            {
                hasSeekChunk = true;
            }

            // Move to next chunk (word-aligned)
            var nextOffset = searchOffset + 8 + (int)((chunkSize + 1) & ~1u);
            if (nextOffset <= searchOffset) break;
            searchOffset = nextOffset;
        }

        // Must have XMA format tag to be considered XMA
        if (formatTag == null || !XmaFormatCodes.Contains(formatTag.Value))
            return null;

        // Calculate actual file size
        int actualSize;
        if (dataChunkOffset.HasValue && dataChunkSize.HasValue)
        {
            // Size is data chunk end position
            var dataEnd = dataChunkOffset.Value + 8 + dataChunkSize.Value;
            actualSize = dataEnd - offset;

            // If reported size is much larger than what we have, it's truncated
            if (reportedSize > actualSize + 100)
            {
                needsRepair = true;
            }
        }
        else
        {
            actualSize = boundarySize;
        }

        // Build result
        var metadata = new Dictionary<string, object>
        {
            ["isXma"] = true,
            ["formatTag"] = formatTag.Value,
            ["hasSeekChunk"] = hasSeekChunk
        };

        if (embeddedPath != null)
        {
            metadata["embeddedPath"] = embeddedPath;
            metadata["safeName"] = SanitizeFilename(embeddedPath);
        }

        if (needsRepair)
        {
            metadata["needsRepair"] = true;
            metadata["reportedSize"] = reportedSize;
        }

        return new ParseResult
        {
            Format = "XMA",
            EstimatedSize = actualSize,
            IsXbox360 = true,
            Metadata = metadata
        };
    }

    /// <summary>
    ///     Try to extract a file path from data (looks for patterns like "sound\" or ".xma").
    /// </summary>
    private static string? TryExtractPath(ReadOnlySpan<byte> data)
    {
        // Look for common path patterns
        var pathIndicators = new[] { "sound\\", "music\\", "fx\\", ".xma", ".wav" };

        // Convert to string for searching
        var str = Encoding.ASCII.GetString(data.ToArray());

        foreach (var indicator in pathIndicators)
        {
            var idx = str.IndexOf(indicator, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                // Find start and end of path
                var start = idx;
                while (start > 0 && IsPrintablePathChar(str[start - 1])) start--;

                var end = idx + indicator.Length;
                while (end < str.Length && IsPrintablePathChar(str[end])) end++;

                var path = str.Substring(start, end - start).Trim('\0', ' ');
                if (path.Length > 5) return path;
            }
        }

        return null;
    }

    private static bool IsPrintablePathChar(char c) =>
        c >= 0x20 && c < 0x7F && c != '"' && c != '<' && c != '>' && c != '|';

    /// <summary>
    ///     Sanitize a path to create a valid filename.
    /// </summary>
    private static string SanitizeFilename(string path)
    {
        // Get just the filename from the path
        var filename = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(filename))
        {
            filename = path.Replace('\\', '_').Replace('/', '_');
        }

        // Remove invalid characters
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            filename = filename.Replace(c, '_');
        }

        return filename;
    }
}
