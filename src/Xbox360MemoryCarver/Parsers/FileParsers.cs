using Xbox360MemoryCarver.Models;
using Xbox360MemoryCarver.Utils;

namespace Xbox360MemoryCarver.Parsers;

/// <summary>
/// Base interface for file parsers.
/// </summary>
public interface IFileParser
{
    /// <summary>
    /// Parse header from data and return file info.
    /// </summary>
    ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0);
}

/// <summary>
/// Result from parsing a file header.
/// </summary>
public class ParseResult
{
    public required string Format { get; init; }
    public int EstimatedSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; }
    public string? FourCc { get; init; }
    public bool IsXbox360 { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = [];
}

/// <summary>
/// Parser for DDS (DirectDraw Surface) texture files.
/// </summary>
public class DdsParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 128)
            return null;

        var headerData = data.Slice(offset, 128);

        // Check magic
        if (!headerData[..4].SequenceEqual("DDS "u8))
            return null;

        try
        {
            uint headerSize = BinaryUtils.ReadUInt32LE(headerData, 4);
            uint height = BinaryUtils.ReadUInt32LE(headerData, 12);
            uint width = BinaryUtils.ReadUInt32LE(headerData, 16);
            uint pitchOrLinearSize = BinaryUtils.ReadUInt32LE(headerData, 20);
            uint mipmapCount = BinaryUtils.ReadUInt32LE(headerData, 28);
            var fourcc = headerData.Slice(84, 4);
            string endianness = "little";

            // Check if values are reasonable for little-endian, try big-endian if not
            if (height > 16384 || width > 16384 || headerSize != 124)
            {
                height = BinaryUtils.ReadUInt32BE(headerData, 12);
                width = BinaryUtils.ReadUInt32BE(headerData, 16);
                pitchOrLinearSize = BinaryUtils.ReadUInt32BE(headerData, 20);
                mipmapCount = BinaryUtils.ReadUInt32BE(headerData, 28);
                endianness = "big";
            }

            if (height == 0 || width == 0 || height > 16384 || width > 16384)
                return null;

            string fourccStr = System.Text.Encoding.ASCII.GetString(fourcc).TrimEnd('\0');
            int bytesPerBlock = GetBytesPerBlock(fourccStr);

            int estimatedSize = CalculateMipmapSize((int)width, (int)height, (int)mipmapCount, bytesPerBlock);

            return new ParseResult
            {
                Format = "DDS",
                EstimatedSize = estimatedSize + 128,
                Width = (int)width,
                Height = (int)height,
                MipCount = (int)mipmapCount,
                FourCc = fourccStr,
                IsXbox360 = endianness == "big",
                Metadata = new Dictionary<string, object>
                {
                    ["pitch"] = pitchOrLinearSize,
                    ["endianness"] = endianness
                }
            };
        }
        catch
        {
            return null;
        }
    }

    private static int GetBytesPerBlock(string fourcc)
    {
        return fourcc switch
        {
            "DXT1" => 8,
            "DXT2" or "DXT3" or "DXT4" or "DXT5" => 16,
            "ATI1" or "BC4U" or "BC4S" => 8,
            "ATI2" or "BC5U" or "BC5S" => 16,
            _ => 16
        };
    }

    private static int CalculateMipmapSize(int width, int height, int mipmapCount, int bytesPerBlock)
    {
        int blocksWide = (width + 3) / 4;
        int blocksHigh = (height + 3) / 4;
        int estimatedSize = blocksWide * blocksHigh * bytesPerBlock;

        if (mipmapCount > 1)
        {
            int mipWidth = width, mipHeight = height;
            for (int i = 1; i < Math.Min(mipmapCount, 16); i++)
            {
                mipWidth = Math.Max(1, mipWidth / 2);
                mipHeight = Math.Max(1, mipHeight / 2);
                int mipBlocksWide = Math.Max(1, (mipWidth + 3) / 4);
                int mipBlocksHigh = Math.Max(1, (mipHeight + 3) / 4);
                estimatedSize += mipBlocksWide * mipBlocksHigh * bytesPerBlock;
            }
        }

        return estimatedSize;
    }
}

/// <summary>
/// Parser for Xbox 360 DDX texture files.
/// </summary>
public class DdxParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 68;
        if (data.Length < offset + minHeaderSize)
            return null;

        var magic = data.Slice(offset, 4);
        bool is3Xdo = magic.SequenceEqual("3XDO"u8);
        bool is3Xdr = magic.SequenceEqual("3XDR"u8);

        if (!is3Xdo && !is3Xdr)
            return null;

        try
        {
            string formatType = is3Xdo ? "3XDO" : "3XDR";

            // Version at offset 0x07 (little-endian uint16)
            ushort version = BinaryUtils.ReadUInt16LE(data, offset + 7);
            if (version < 3)
                return null;

            // Validate DDX header structure
            byte headerIndicator = data[offset + 0x04];
            if (headerIndicator == 0xFF)
                return null;

            byte flagsByte = data[offset + 0x24];
            if (flagsByte < 0x80)
                return null;

            // Read format code from offset 0x28 (low byte)
            uint formatDword = BinaryUtils.ReadUInt32BE(data, offset + 0x28);
            int formatByte = (int)(formatDword & 0xFF);

            // Read dimensions from file offset 0x2C (size_2d structure)
            uint sizeDword = BinaryUtils.ReadUInt32BE(data, offset + 0x2C);
            int width = (int)(sizeDword & 0x1FFF) + 1;
            int height = (int)((sizeDword >> 13) & 0x1FFF) + 1;

            // Read mip count
            int mipCount = (int)(((formatDword >> 16) & 0xF) + 1);
            if (mipCount > 13) mipCount = 1;

            // Tiled flag from offset 0x24
            uint flagsDword = BinaryUtils.ReadUInt32BE(data, offset + 0x24);
            bool isTiled = ((flagsDword >> 22) & 0x1) != 0;

            // Validate dimensions
            if (width == 0 || height == 0 || width > 4096 || height > 4096)
                return null;

            // Get format name
            string formatName = FileSignatures.Xbox360GpuTextureFormats.TryGetValue(formatByte, out var fn)
                ? fn
                : $"Unknown(0x{formatByte:X2})";

            int bytesPerBlock = FileSignatures.GetBytesPerBlock(formatName);
            int blocksW = (width + 3) / 4;
            int blocksH = (height + 3) / 4;
            int baseSize = blocksW * blocksH * bytesPerBlock;

            // Total uncompressed size with mipmaps
            int uncompressedSize = baseSize;
            int mipW = width, mipH = height;
            for (int i = 1; i < mipCount; i++)
            {
                mipW = Math.Max(1, mipW / 2);
                mipH = Math.Max(1, mipH / 2);
                int mipBlocksW = Math.Max(1, (mipW + 3) / 4);
                int mipBlocksH = Math.Max(1, (mipH + 3) / 4);
                uncompressedSize += mipBlocksW * mipBlocksH * bytesPerBlock;
            }

            // DDX files are compressed - estimate actual size by scanning for next signature
            // This is more accurate for memory dump carving where files are packed together
            int estimatedSize = FindDdxBoundary(data, offset, uncompressedSize);

            return new ParseResult
            {
                Format = formatType,
                EstimatedSize = estimatedSize,
                Width = width,
                Height = height,
                MipCount = mipCount,
                FourCc = formatName,
                IsXbox360 = true,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = version,
                    ["gpuFormat"] = formatByte,
                    ["isTiled"] = isTiled,
                    ["dataOffset"] = 0x44,
                    ["uncompressedSize"] = uncompressedSize
                }
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Find the boundary of this DDX file by scanning for the next DDX signature.
    /// This helps with memory dump carving where compressed DDX files are packed together.
    /// </summary>
    private static int FindDdxBoundary(ReadOnlySpan<byte> data, int offset, int uncompressedSize)
    {
        const int headerSize = 0x44;

        // DDX files contain XMemCompress data which typically compresses DXT to ~50-90% of original
        // For memory carving, we need to estimate the compressed size accurately

        // Check if data starts with XMemCompress frame marker (0xFF)
        // If so, try to parse XMemCompress frames to find actual end
        if (offset + headerSize < data.Length && data[offset + headerSize] == 0xFF)
        {
            int xmemEnd = FindXMemCompressEnd(data, offset + headerSize, uncompressedSize);
            if (xmemEnd > 0)
            {
                return headerSize + xmemEnd;
            }
        }

        // XMemCompress frame parsing failed - use size-based estimation
        // DXT data typically compresses to 50-90% of original size
        // Use 100% as maximum to be safe (poorly compressible data), plus small overhead
        int estimatedCompressedMax = uncompressedSize + 512;

        // Minimum is typically 40% compression for highly compressible textures
        int estimatedCompressedMin = Math.Max(100, uncompressedSize * 2 / 5);

        int minSize = headerSize + estimatedCompressedMin;
        int maxSize = Math.Min(data.Length - offset, headerSize + estimatedCompressedMax);

        // Scan for next DDX signature within the expected range
        // Only accept signatures that are reasonably close to expected size
        ReadOnlySpan<byte> ddx3xdo = "3XDO"u8;
        ReadOnlySpan<byte> ddx3xdr = "3XDR"u8;

        for (int i = offset + minSize; i < offset + maxSize && i < data.Length - 0x44; i++)
        {
            var slice = data.Slice(i, 4);
            if (slice.SequenceEqual(ddx3xdo) || slice.SequenceEqual(ddx3xdr))
            {
                // Validate this looks like a real DDX header with stricter checks
                // Check version field
                ushort nextVersion = BinaryUtils.ReadUInt16LE(data, i + 7);
                if (nextVersion < 3 || nextVersion > 10) continue;

                // Check flags byte
                byte nextFlags = data[i + 0x24];
                if (nextFlags < 0x80) continue;

                // Check header indicator byte
                byte nextHeaderIndicator = data[i + 0x04];
                if (nextHeaderIndicator == 0xFF) continue;

                // Parse dimensions from the next header to validate it makes sense
                uint nextSizeDword = BinaryUtils.ReadUInt32BE(data, i + 0x2C);
                int nextWidth = (int)(nextSizeDword & 0x1FFF) + 1;
                int nextHeight = (int)((nextSizeDword >> 13) & 0x1FFF) + 1;

                // Reject if dimensions are invalid (must be powers of 2 and reasonable)
                if (nextWidth <= 0 || nextWidth > 4096 || nextHeight <= 0 || nextHeight > 4096)
                    continue;
                if (!IsPowerOfTwo(nextWidth) || !IsPowerOfTwo(nextHeight))
                    continue;

                // Found valid next DDX - this is our boundary
                // Add a small overlap margin to reduce false positives where a compressed
                // payload happens to contain a DDX-looking sequence. This keeps the
                // carved data large enough for multi-chunk textures while still stopping
                // before the next real header.
                const int overlapMargin = 0x8000; // 32KB tail to keep potential trailing chunks
                return Math.Min((i - offset) + overlapMargin, maxSize);
            }
        }

        // No next signature found within expected range
        // Use a compression-ratio based estimate (typical DXT compression is ~70%)
        int typicalCompressedSize = uncompressedSize * 7 / 10;
        return headerSize + Math.Max(estimatedCompressedMin, typicalCompressedSize);
    }

    private static bool IsPowerOfTwo(int x)
    {
        return x > 0 && (x & (x - 1)) == 0;
    }

    /// <summary>
    /// Try to find the end of XMemCompress data by parsing frame headers.
    /// Returns the offset from start of compressed data, or -1 if unable to parse.
    /// </summary>
    private static int FindXMemCompressEnd(ReadOnlySpan<byte> data, int start, int expectedUncompressed)
    {
        // XMemCompress/LZX decompression is complex and frame parsing is unreliable.
        // Instead of trying to parse frames, we'll return -1 to force use of the 
        // size-based estimation which is more reliable for memory-carved textures.
        //
        // The actual compressed size will be determined when DDXConv decompresses
        // the data and reports how many bytes were consumed.

        return -1; // Let the caller use size-based estimation
    }
}

/// <summary>
/// Parser for Xbox Media Audio (XMA) files.
/// </summary>
public class XmaParser : IFileParser
{
    private static readonly ushort[] XmaFormatCodes = [0x0165, 0x0166];

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 12)
            return null;

        // Check RIFF magic
        if (!data.Slice(offset, 4).SequenceEqual("RIFF"u8))
            return null;

        try
        {
            uint fileSize = BinaryUtils.ReadUInt32LE(data, offset + 4) + 8;
            var formatType = data.Slice(offset + 8, 4);

            if (!formatType.SequenceEqual("WAVE"u8))
                return null;

            // Search through chunks to find XMA format information
            int searchOffset = offset + 12;
            while (searchOffset < Math.Min(offset + 200, data.Length - 8))
            {
                var chunkId = data.Slice(searchOffset, 4);

                // Check for XMA2 chunk
                if (chunkId.SequenceEqual("XMA2"u8))
                {
                    return new ParseResult
                    {
                        Format = "XMA",
                        EstimatedSize = (int)fileSize,
                        IsXbox360 = true,
                        Metadata = new Dictionary<string, object> { ["isXma"] = true }
                    };
                }

                // Check for fmt chunk with XMA format code
                if (chunkId.SequenceEqual("fmt "u8) && data.Length >= searchOffset + 10)
                {
                    ushort formatTag = (ushort)(BinaryUtils.ReadUInt32LE(data, searchOffset + 8) & 0xFFFF);
                    if (XmaFormatCodes.Contains(formatTag))
                    {
                        return new ParseResult
                        {
                            Format = "XMA",
                            EstimatedSize = (int)fileSize,
                            IsXbox360 = true,
                            Metadata = new Dictionary<string, object> { ["isXma"] = true, ["formatTag"] = formatTag }
                        };
                    }
                }

                uint chunkSize = BinaryUtils.ReadUInt32LE(data, searchOffset + 4);
                searchOffset += 8 + (int)((chunkSize + 1) & ~1u);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Parser for NetImmerse/Gamebryo (NIF) model files.
/// </summary>
public class NifParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 64)
            return null;

        var headerMagic = data.Slice(offset, 20);
        if (!headerMagic.SequenceEqual("Gamebryo File Format"u8))
            return null;

        try
        {
            int versionOffset = offset + 22;

            // Look for null terminator after header
            int nullPos = -1;
            for (int i = versionOffset; i < Math.Min(versionOffset + 40, data.Length); i++)
            {
                if (data[i] == 0)
                {
                    nullPos = i;
                    break;
                }
            }

            if (nullPos == -1)
                return null;

            string versionString = System.Text.Encoding.ASCII.GetString(data[versionOffset..nullPos]);
            int estimatedSize = 50000; // Default fallback

            // For NIF 20.x, try to estimate size based on block count
            if (versionString.Contains("20."))
            {
                int parseOffset = nullPos + 1;
                if (data.Length >= offset + 100)
                {
                    for (int testOffset = parseOffset; testOffset < Math.Min(parseOffset + 60, data.Length - 4); testOffset += 4)
                    {
                        uint potentialBlocks = BinaryUtils.ReadUInt32LE(data, testOffset);
                        if (potentialBlocks >= 1 && potentialBlocks <= 10000)
                        {
                            estimatedSize = Math.Min((int)(potentialBlocks * 500 + 1000), 20 * 1024 * 1024);
                            break;
                        }
                    }
                }
            }

            return new ParseResult
            {
                Format = "NIF",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object> { ["version"] = versionString }
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Parser for Bethesda script files.
/// </summary>
public class ScriptParser : IFileParser
{
    private static readonly byte[][] ScriptHeaders =
    [
        "scn "u8.ToArray(),
        "Scn "u8.ToArray(),
        "SCN "u8.ToArray(),
        "ScriptName "u8.ToArray(),
        "scriptname "u8.ToArray(),
        "SCRIPTNAME "u8.ToArray()
    ];

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 10)
            return null;

        try
        {
            int maxEnd = Math.Min(offset + 100000, data.Length);
            var scriptData = data[offset..maxEnd];

            // Find first line
            int firstLineEnd = -1;
            for (int i = 0; i < scriptData.Length; i++)
            {
                if (scriptData[i] == '\n')
                {
                    firstLineEnd = i;
                    break;
                }
            }

            if (firstLineEnd == -1)
                return null;

            string firstLine = System.Text.Encoding.ASCII.GetString(scriptData[..firstLineEnd]).Trim();

            // Extract script name
            string? scriptName = null;
            string lowerLine = firstLine.ToLowerInvariant();

            if (lowerLine.StartsWith("scn "))
                scriptName = firstLine[4..].Trim();
            else if (lowerLine.StartsWith("scriptname "))
                scriptName = firstLine[11..].Trim();
            else
                return null;

            // Clean script name
            int invalidChar = scriptName.IndexOfAny([';', '\r', '\t', ' ']);
            if (invalidChar >= 0)
                scriptName = scriptName[..invalidChar];

            if (string.IsNullOrEmpty(scriptName) || !scriptName.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return null;

            // Find script end
            int endPos = FindScriptEnd(scriptData, firstLineEnd);

            return new ParseResult
            {
                Format = "Script",
                EstimatedSize = endPos,
                Metadata = new Dictionary<string, object>
                {
                    ["scriptName"] = scriptName,
                    ["safeName"] = new string([.. scriptName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')])
                }
            };
        }
        catch
        {
            return null;
        }
    }

    private static int FindScriptEnd(ReadOnlySpan<byte> scriptData, int firstLineEnd)
    {
        int endPos = scriptData.Length;
        int searchStart = firstLineEnd + 1;

        // Find next script header
        foreach (var header in ScriptHeaders)
        {
            int nextScript = BinaryUtils.FindPattern(scriptData[searchStart..], header);
            if (nextScript >= 0)
            {
                int absolutePos = searchStart + nextScript;
                // Find previous newline
                int boundary = -1;
                for (int i = absolutePos - 1; i >= 0; i--)
                {
                    if (scriptData[i] == '\n')
                    {
                        boundary = i;
                        break;
                    }
                }
                endPos = Math.Min(endPos, boundary >= 0 ? boundary : absolutePos);
            }
        }

        // Stop at garbage
        for (int i = 0; i < endPos; i++)
        {
            byte b = scriptData[i];
            if (b == 0 || (b < 32 && b != 9 && b != 10 && b != 13) || b > 126)
            {
                endPos = Math.Min(endPos, i);
                break;
            }
        }

        // Trim trailing whitespace
        while (endPos > 0 && (scriptData[endPos - 1] == 9 || scriptData[endPos - 1] == 10 ||
                             scriptData[endPos - 1] == 13 || scriptData[endPos - 1] == 32))
        {
            endPos--;
        }

        return endPos;
    }
}

/// <summary>
/// Parser for Bink video files.
/// </summary>
public class BikParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 20)
            return null;

        var magic = data.Slice(offset, 4);
        if (!magic.SequenceEqual("BIKi"u8) && !magic.SequenceEqual("BIK"u8.ToArray().Concat(new byte[] { 0 }).ToArray()))
            return null;

        try
        {
            uint fileSize = BinaryUtils.ReadUInt32LE(data, offset + 4);
            // Validate size
            if (fileSize < 20 || fileSize > 500 * 1024 * 1024)
                return null;

            return new ParseResult
            {
                Format = "BIK",
                EstimatedSize = (int)fileSize
            };
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Parser for PNG image files.
/// </summary>
public class PngParser : IFileParser
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IendMagic = [0x49, 0x45, 0x4E, 0x44];

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 8)
            return null;

        if (!data.Slice(offset, 8).SequenceEqual(PngMagic))
            return null;

        // Find IEND chunk
        int searchPos = offset + 8;
        int maxSearch = Math.Min(offset + 50 * 1024 * 1024, data.Length - 4);

        while (searchPos < maxSearch)
        {
            if (data.Slice(searchPos, 4).SequenceEqual(IendMagic))
            {
                // IEND found, add 4 bytes for CRC
                return new ParseResult
                {
                    Format = "PNG",
                    EstimatedSize = searchPos + 8 - offset
                };
            }
            searchPos++;
        }

        return null;
    }
}

/// <summary>
/// Factory for getting appropriate parser for a file type.
/// </summary>
public static class ParserFactory
{
    private static readonly Dictionary<string, IFileParser> Parsers = new()
    {
        ["dds"] = new DdsParser(),
        ["ddx_3xdo"] = new DdxParser(),
        ["ddx_3xdr"] = new DdxParser(),
        ["xma"] = new XmaParser(),
        ["nif"] = new NifParser(),
        ["kf"] = new NifParser(),
        ["egm"] = new NifParser(),
        ["egt"] = new NifParser(),
        ["script_scn"] = new ScriptParser(),
        ["script_sn"] = new ScriptParser(),
        ["bik"] = new BikParser(),
        ["png"] = new PngParser()
    };

    public static IFileParser? GetParser(string fileType)
    {
        return Parsers.TryGetValue(fileType, out var parser) ? parser : null;
    }
}
