using Xbox360MemoryCarver.Core.Models;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
/// Base interface for file parsers.
/// </summary>
public interface IFileParser
{
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

            // Find DDX boundary by scanning for next signature
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
    /// </summary>
    private static int FindDdxBoundary(ReadOnlySpan<byte> data, int offset, int uncompressedSize)
    {
        const int headerSize = 0x44;

        // DXT data typically compresses to 50-90% of original size
        int estimatedCompressedMax = uncompressedSize + 512;
        int estimatedCompressedMin = Math.Max(100, uncompressedSize * 2 / 5);

        int minSize = headerSize + estimatedCompressedMin;
        int maxSize = Math.Min(data.Length - offset, headerSize + estimatedCompressedMax);

        // Scan for next DDX signature within the expected range
        ReadOnlySpan<byte> ddx3xdo = "3XDO"u8;
        ReadOnlySpan<byte> ddx3xdr = "3XDR"u8;

        for (int i = offset + minSize; i < offset + maxSize && i < data.Length - 0x44; i++)
        {
            var slice = data.Slice(i, 4);
            if (slice.SequenceEqual(ddx3xdo) || slice.SequenceEqual(ddx3xdr))
            {
                // Validate this looks like a real DDX header
                ushort nextVersion = BinaryUtils.ReadUInt16LE(data, i + 7);
                if (nextVersion < 3 || nextVersion > 10) continue;

                byte nextFlags = data[i + 0x24];
                if (nextFlags < 0x80) continue;

                byte nextHeaderIndicator = data[i + 0x04];
                if (nextHeaderIndicator == 0xFF) continue;

                // Parse dimensions from the next header
                uint nextSizeDword = BinaryUtils.ReadUInt32BE(data, i + 0x2C);
                int nextWidth = (int)(nextSizeDword & 0x1FFF) + 1;
                int nextHeight = (int)((nextSizeDword >> 13) & 0x1FFF) + 1;

                if (nextWidth <= 0 || nextWidth > 4096 || nextHeight <= 0 || nextHeight > 4096)
                    continue;
                if (!IsPowerOfTwo(nextWidth) || !IsPowerOfTwo(nextHeight))
                    continue;

                // Found valid next DDX - add overlap margin
                const int overlapMargin = 0x8000;
                return Math.Min((i - offset) + overlapMargin, maxSize);
            }
        }

        // No next signature found - use compression-ratio estimate
        int typicalCompressedSize = uncompressedSize * 7 / 10;
        return headerSize + Math.Max(estimatedCompressedMin, typicalCompressedSize);
    }

    private static bool IsPowerOfTwo(int x)
    {
        return x > 0 && (x & (x - 1)) == 0;
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

        if (!data.Slice(offset, 4).SequenceEqual("RIFF"u8))
            return null;

        try
        {
            uint fileSize = BinaryUtils.ReadUInt32LE(data, offset + 4) + 8;
            var formatType = data.Slice(offset + 8, 4);

            if (!formatType.SequenceEqual("WAVE"u8))
                return null;

            int searchOffset = offset + 12;
            while (searchOffset < Math.Min(offset + 200, data.Length - 8))
            {
                var chunkId = data.Slice(searchOffset, 4);

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
            int estimatedSize = 50000;

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

        int searchPos = offset + 8;
        int maxSearch = Math.Min(offset + 50 * 1024 * 1024, data.Length - 4);

        while (searchPos < maxSearch)
        {
            if (data.Slice(searchPos, 4).SequenceEqual(IendMagic))
            {
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
        ["png"] = new PngParser()
    };

    public static IFileParser? GetParser(string fileType)
    {
        return Parsers.TryGetValue(fileType, out var parser) ? parser : null;
    }
}
