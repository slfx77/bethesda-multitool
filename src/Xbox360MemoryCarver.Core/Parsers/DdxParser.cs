using Xbox360MemoryCarver.Core.Models;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Xbox 360 DDX texture files (3XDO and 3XDR variants).
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

        try { return ParseDdxHeader(data, offset, is3Xdo); }
        catch { return null; }
    }

    private static ParseResult? ParseDdxHeader(ReadOnlySpan<byte> data, int offset, bool is3Xdo)
    {
        var version = BinaryUtils.ReadUInt16LE(data, offset + 7);
        if (version < 3 || !ValidateDdxHeader(data, offset)) return null;

        var formatDword = BinaryUtils.ReadUInt32BE(data, offset + 0x28);
        var formatByte = (int)(formatDword & 0xFF);
        var sizeDword = BinaryUtils.ReadUInt32BE(data, offset + 0x2C);
        var width = (int)(sizeDword & 0x1FFF) + 1;
        var height = (int)((sizeDword >> 13) & 0x1FFF) + 1;
        var mipCount = Math.Min((int)(((formatDword >> 16) & 0xF) + 1), 13);
        var flagsDword = BinaryUtils.ReadUInt32BE(data, offset + 0x24);
        var isTiled = ((flagsDword >> 22) & 0x1) != 0;

        if (width == 0 || height == 0 || width > 4096 || height > 4096) return null;

        var formatName = FileSignatures.Xbox360GpuTextureFormats.TryGetValue(formatByte, out var fn) ? fn : $"Unknown(0x{formatByte:X2})";
        var uncompressedSize = CalculateUncompressedSize(width, height, mipCount, formatName);
        var estimatedSize = FindDdxBoundary(data, offset, uncompressedSize);
        var texturePath = TexturePathExtractor.FindPrecedingPath(data, offset);

        var metadata = new Dictionary<string, object>
        {
            ["version"] = version, ["gpuFormat"] = formatByte, ["isTiled"] = isTiled,
            ["dataOffset"] = 0x44, ["uncompressedSize"] = uncompressedSize
        };
        
        if (texturePath != null)
        {
            metadata["texturePath"] = texturePath;
            // Convert texture path to safe filename for extraction
            // e.g., "textures\architecture\anvil\anvildoor01.ddx" -> "textures_architecture_anvil_anvildoor01"
            var safeName = ConvertPathToSafeName(texturePath);
            if (!string.IsNullOrEmpty(safeName))
            {
                metadata["safeName"] = safeName;
            }
        }

        return new ParseResult
        {
            Format = is3Xdo ? "3XDO" : "3XDR", EstimatedSize = estimatedSize, Width = width, Height = height,
            MipCount = mipCount, FourCc = formatName, IsXbox360 = true, Metadata = metadata
        };
    }

    private static string? ConvertPathToSafeName(string texturePath)
    {
        if (string.IsNullOrEmpty(texturePath)) return null;

        // Remove extension
        var nameWithoutExt = Path.GetFileNameWithoutExtension(texturePath);
        if (string.IsNullOrEmpty(nameWithoutExt)) return null;

        // Get parent directory for context (e.g., "anvil" from "textures\architecture\anvil\anvildoor01.ddx")
        var directory = Path.GetDirectoryName(texturePath);
        var parentDir = !string.IsNullOrEmpty(directory) ? Path.GetFileName(directory) : null;

        // Combine parent dir + filename for uniqueness
        var safeName = !string.IsNullOrEmpty(parentDir)
            ? $"{parentDir}_{nameWithoutExt}"
            : nameWithoutExt;

        // Clean to valid filename characters
        return new string([.. safeName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')]);
    }

    private static bool ValidateDdxHeader(ReadOnlySpan<byte> data, int offset) =>
        data[offset + 0x04] != 0xFF && data[offset + 0x24] >= 0x80;

    private static int CalculateUncompressedSize(int width, int height, int mipCount, string formatName)
    {
        var bytesPerBlock = FileSignatures.GetBytesPerBlock(formatName);
        var size = ((width + 3) / 4) * ((height + 3) / 4) * bytesPerBlock;

        int mipW = width, mipH = height;
        for (var i = 1; i < mipCount; i++)
        {
            mipW = Math.Max(1, mipW / 2);
            mipH = Math.Max(1, mipH / 2);
            size += Math.Max(1, (mipW + 3) / 4) * Math.Max(1, (mipH + 3) / 4) * bytesPerBlock;
        }
        return size;
    }

    private static int FindDdxBoundary(ReadOnlySpan<byte> data, int offset, int uncompressedSize)
    {
        const int headerSize = 0x44;
        var minSize = headerSize + Math.Max(100, uncompressedSize * 2 / 5);
        var maxSize = Math.Min(data.Length - offset, headerSize + uncompressedSize + 512);

        for (var i = offset + minSize; i < offset + maxSize && i < data.Length - 0x44; i++)
        {
            var slice = data.Slice(i, 4);
            if ((slice.SequenceEqual("3XDO"u8) || slice.SequenceEqual("3XDR"u8)) && IsValidNextHeader(data, i))
                return Math.Min(i - offset + 0x8000, maxSize);
        }
        return headerSize + Math.Max(Math.Max(100, uncompressedSize * 2 / 5), uncompressedSize * 7 / 10);
    }

    private static bool IsValidNextHeader(ReadOnlySpan<byte> data, int i)
    {
        var nextVersion = BinaryUtils.ReadUInt16LE(data, i + 7);
        if (nextVersion is < 3 or > 10 || data[i + 0x24] < 0x80 || data[i + 0x04] == 0xFF) return false;

        var sizeDword = BinaryUtils.ReadUInt32BE(data, i + 0x2C);
        var w = (int)(sizeDword & 0x1FFF) + 1;
        var h = (int)((sizeDword >> 13) & 0x1FFF) + 1;
        return w is > 0 and <= 4096 && h is > 0 and <= 4096 && IsPowerOfTwo(w) && IsPowerOfTwo(h);
    }

    private static bool IsPowerOfTwo(int x) => x > 0 && (x & (x - 1)) == 0;
}
