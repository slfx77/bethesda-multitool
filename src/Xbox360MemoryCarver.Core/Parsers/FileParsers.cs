using System.Text;
using Xbox360MemoryCarver.Core.Models;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Base interface for file parsers.
/// </summary>
public interface IFileParser
{
    ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0);
}

/// <summary>
///     Result from parsing a file header.
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
///     Parser for DDS (DirectDraw Surface) texture files.
/// </summary>
public class DdsParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 128) return null;

        var headerData = data.Slice(offset, 128);

        if (!headerData[..4].SequenceEqual("DDS "u8)) return null;

        try
        {
            var headerSize = BinaryUtils.ReadUInt32LE(headerData, 4);
            var height = BinaryUtils.ReadUInt32LE(headerData, 12);
            var width = BinaryUtils.ReadUInt32LE(headerData, 16);
            var pitchOrLinearSize = BinaryUtils.ReadUInt32LE(headerData, 20);
            var mipmapCount = BinaryUtils.ReadUInt32LE(headerData, 28);
            var fourcc = headerData.Slice(84, 4);
            var endianness = "little";

            if (height > 16384 || width > 16384 || headerSize != 124)
            {
                height = BinaryUtils.ReadUInt32BE(headerData, 12);
                width = BinaryUtils.ReadUInt32BE(headerData, 16);
                pitchOrLinearSize = BinaryUtils.ReadUInt32BE(headerData, 20);
                mipmapCount = BinaryUtils.ReadUInt32BE(headerData, 28);
                endianness = "big";
            }

            if (height == 0 || width == 0 || height > 16384 || width > 16384) return null;

            var fourccStr = Encoding.ASCII.GetString(fourcc).TrimEnd('\0');
            var bytesPerBlock = GetBytesPerBlock(fourccStr);
            var estimatedSize = CalculateMipmapSize((int)width, (int)height, (int)mipmapCount, bytesPerBlock);

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
                    ["pitch"] = pitchOrLinearSize, ["endianness"] = endianness
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
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var estimatedSize = blocksWide * blocksHigh * bytesPerBlock;

        if (mipmapCount > 1)
        {
            int mipWidth = width, mipHeight = height;
            for (var i = 1; i < Math.Min(mipmapCount, 16); i++)
            {
                mipWidth = Math.Max(1, mipWidth / 2);
                mipHeight = Math.Max(1, mipHeight / 2);
                var mipBlocksWide = Math.Max(1, (mipWidth + 3) / 4);
                var mipBlocksHigh = Math.Max(1, (mipHeight + 3) / 4);
                estimatedSize += mipBlocksWide * mipBlocksHigh * bytesPerBlock;
            }
        }

        return estimatedSize;
    }
}

/// <summary>
///     Parser for Xbox 360 DDX texture files.
/// </summary>
public class DdxParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 68;
        if (data.Length < offset + minHeaderSize) return null;

        var magic = data.Slice(offset, 4);
        var is3Xdo = magic.SequenceEqual("3XDO"u8);
        var is3Xdr = magic.SequenceEqual("3XDR"u8);

        if (!is3Xdo && !is3Xdr) return null;

        try
        {
            return ParseDdxHeader(data, offset, is3Xdo);
        }
        catch
        {
            return null;
        }
    }

    private static ParseResult? ParseDdxHeader(ReadOnlySpan<byte> data, int offset, bool is3Xdo)
    {
        var formatType = is3Xdo ? "3XDO" : "3XDR";

        // Version at offset 0x07 (little-endian uint16)
        var version = BinaryUtils.ReadUInt16LE(data, offset + 7);
        if (version < 3) return null;

        // Validate DDX header structure
        if (!ValidateDdxHeader(data, offset)) return null;

        // Read format code from offset 0x28 (low byte)
        var formatDword = BinaryUtils.ReadUInt32BE(data, offset + 0x28);
        var formatByte = (int)(formatDword & 0xFF);

        // Read dimensions from file offset 0x2C (size_2d structure)
        var sizeDword = BinaryUtils.ReadUInt32BE(data, offset + 0x2C);
        var width = (int)(sizeDword & 0x1FFF) + 1;
        var height = (int)((sizeDword >> 13) & 0x1FFF) + 1;

        // Read mip count
        var mipCount = (int)(((formatDword >> 16) & 0xF) + 1);
        if (mipCount > 13) mipCount = 1;

        // Tiled flag from offset 0x24
        var flagsDword = BinaryUtils.ReadUInt32BE(data, offset + 0x24);
        var isTiled = ((flagsDword >> 22) & 0x1) != 0;

        // Validate dimensions
        if (width == 0 || height == 0 || width > 4096 || height > 4096) return null;

        // Get format name
        var formatName = FileSignatures.Xbox360GpuTextureFormats.TryGetValue(formatByte, out var fn)
            ? fn
            : $"Unknown(0x{formatByte:X2})";

        var uncompressedSize = CalculateUncompressedSize(width, height, mipCount, formatName);
        var estimatedSize = FindDdxBoundary(data, offset, uncompressedSize);

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

    private static bool ValidateDdxHeader(ReadOnlySpan<byte> data, int offset)
    {
        var headerIndicator = data[offset + 0x04];
        if (headerIndicator == 0xFF) return false;

        var flagsByte = data[offset + 0x24];
        return flagsByte >= 0x80;
    }

    private static int CalculateUncompressedSize(int width, int height, int mipCount, string formatName)
    {
        var bytesPerBlock = FileSignatures.GetBytesPerBlock(formatName);
        var blocksW = (width + 3) / 4;
        var blocksH = (height + 3) / 4;
        var uncompressedSize = blocksW * blocksH * bytesPerBlock;

        int mipW = width, mipH = height;
        for (var i = 1; i < mipCount; i++)
        {
            mipW = Math.Max(1, mipW / 2);
            mipH = Math.Max(1, mipH / 2);
            var mipBlocksW = Math.Max(1, (mipW + 3) / 4);
            var mipBlocksH = Math.Max(1, (mipH + 3) / 4);
            uncompressedSize += mipBlocksW * mipBlocksH * bytesPerBlock;
        }

        return uncompressedSize;
    }

    /// <summary>
    ///     Find the boundary of this DDX file by scanning for the next DDX signature.
    /// </summary>
    private static int FindDdxBoundary(ReadOnlySpan<byte> data, int offset, int uncompressedSize)
    {
        const int headerSize = 0x44;

        // DXT data typically compresses to 50-90% of original size
        var estimatedCompressedMax = uncompressedSize + 512;
        var estimatedCompressedMin = Math.Max(100, uncompressedSize * 2 / 5);

        var minSize = headerSize + estimatedCompressedMin;
        var maxSize = Math.Min(data.Length - offset, headerSize + estimatedCompressedMax);

        var foundBoundary = ScanForNextDdxSignature(data, offset, minSize, maxSize);
        if (foundBoundary.HasValue) return foundBoundary.Value;

        // No next signature found - use compression-ratio estimate
        var typicalCompressedSize = uncompressedSize * 7 / 10;
        return headerSize + Math.Max(estimatedCompressedMin, typicalCompressedSize);
    }

    private static int? ScanForNextDdxSignature(ReadOnlySpan<byte> data, int offset, int minSize, int maxSize)
    {
        var ddx3xdo = "3XDO"u8;
        var ddx3xdr = "3XDR"u8;

        for (var i = offset + minSize; i < offset + maxSize && i < data.Length - 0x44; i++)
        {
            var slice = data.Slice(i, 4);
            if (!slice.SequenceEqual(ddx3xdo) && !slice.SequenceEqual(ddx3xdr)) continue;

            if (IsValidNextDdxHeader(data, i))
            {
                const int overlapMargin = 0x8000;
                return Math.Min(i - offset + overlapMargin, maxSize);
            }
        }

        return null;
    }

    private static bool IsValidNextDdxHeader(ReadOnlySpan<byte> data, int i)
    {
        var nextVersion = BinaryUtils.ReadUInt16LE(data, i + 7);
        if (nextVersion is < 3 or > 10) return false;

        var nextFlags = data[i + 0x24];
        if (nextFlags < 0x80) return false;

        var nextHeaderIndicator = data[i + 0x04];
        if (nextHeaderIndicator == 0xFF) return false;

        // Parse dimensions from the next header
        var nextSizeDword = BinaryUtils.ReadUInt32BE(data, i + 0x2C);
        var nextWidth = (int)(nextSizeDword & 0x1FFF) + 1;
        var nextHeight = (int)((nextSizeDword >> 13) & 0x1FFF) + 1;

        return nextWidth is > 0 and <= 4096 &&
               nextHeight is > 0 and <= 4096 &&
               IsPowerOfTwo(nextWidth) &&
               IsPowerOfTwo(nextHeight);
    }

    private static bool IsPowerOfTwo(int x)
    {
        return x > 0 && (x & (x - 1)) == 0;
    }
}

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
            var fileSize = BinaryUtils.ReadUInt32LE(data, offset + 4) + 8;
            var formatType = data.Slice(offset + 8, 4);

            return !formatType.SequenceEqual("WAVE"u8)
                ? null
                : SearchForXmaChunks(data, offset, (int)fileSize);
        }
        catch
        {
            return null;
        }
    }

    private static ParseResult? SearchForXmaChunks(ReadOnlySpan<byte> data, int offset, int fileSize)
    {
        var searchOffset = offset + 12;
        while (searchOffset < Math.Min(offset + 200, data.Length - 8))
        {
            var chunkId = data.Slice(searchOffset, 4);

            if (chunkId.SequenceEqual("XMA2"u8)) return CreateXmaResult(fileSize, null);

            if (chunkId.SequenceEqual("fmt "u8) && data.Length >= searchOffset + 10)
            {
                var formatTag = (ushort)(BinaryUtils.ReadUInt32LE(data, searchOffset + 8) & 0xFFFF);
                if (XmaFormatCodes.Contains(formatTag)) return CreateXmaResult(fileSize, formatTag);
            }

            var chunkSize = BinaryUtils.ReadUInt32LE(data, searchOffset + 4);
            searchOffset += 8 + (int)((chunkSize + 1) & ~1u);
        }

        return null;
    }

    private static ParseResult CreateXmaResult(int fileSize, ushort? formatTag)
    {
        var metadata = new Dictionary<string, object> { ["isXma"] = true };
        if (formatTag.HasValue) metadata["formatTag"] = formatTag.Value;

        return new ParseResult { Format = "XMA", EstimatedSize = fileSize, IsXbox360 = true, Metadata = metadata };
    }
}

/// <summary>
///     Parser for NetImmerse/Gamebryo (NIF) model files.
/// </summary>
public class NifParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 64) return null;

        var headerMagic = data.Slice(offset, 20);
        if (!headerMagic.SequenceEqual("Gamebryo File Format"u8)) return null;

        try
        {
            var versionOffset = offset + 22;
            var nullPos = FindNullTerminator(data, versionOffset, 40);

            if (nullPos == -1) return null;

            var versionString = Encoding.ASCII.GetString(data[versionOffset..nullPos]);
            var estimatedSize = EstimateNifSize(data, offset, nullPos, versionString);

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

    private static int FindNullTerminator(ReadOnlySpan<byte> data, int start, int maxLength)
    {
        var end = Math.Min(start + maxLength, data.Length);
        for (var i = start; i < end; i++)
            if (data[i] == 0)
                return i;

        return -1;
    }

    private static int EstimateNifSize(ReadOnlySpan<byte> data, int offset, int nullPos, string versionString)
    {
        var estimatedSize = 50000;

        if (!versionString.Contains("20.", StringComparison.Ordinal)) return estimatedSize;

        var parseOffset = nullPos + 1;
        if (data.Length < offset + 100) return estimatedSize;

        for (var testOffset = parseOffset; testOffset < Math.Min(parseOffset + 60, data.Length - 4); testOffset += 4)
        {
            var potentialBlocks = BinaryUtils.ReadUInt32LE(data, testOffset);
            if (potentialBlocks is >= 1 and <= 10000)
            {
                estimatedSize = Math.Min((int)(potentialBlocks * 500 + 1000), 20 * 1024 * 1024);
                break;
            }
        }

        return estimatedSize;
    }
}

/// <summary>
///     Parser for PNG image files.
/// </summary>
public class PngParser : IFileParser
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IendMagic = [0x49, 0x45, 0x4E, 0x44];

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 8) return null;

        if (!data.Slice(offset, 8).SequenceEqual(PngMagic)) return null;

        var searchPos = offset + 8;
        var maxSearch = Math.Min(offset + 50 * 1024 * 1024, data.Length - 4);

        while (searchPos < maxSearch)
        {
            if (data.Slice(searchPos, 4).SequenceEqual(IendMagic))
                return new ParseResult { Format = "PNG", EstimatedSize = searchPos + 8 - offset };

            searchPos++;
        }

        return null;
    }
}

/// <summary>
///     Factory for getting appropriate parser for a file type.
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
