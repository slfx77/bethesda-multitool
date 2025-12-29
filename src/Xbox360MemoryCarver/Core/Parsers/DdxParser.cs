using Xbox360MemoryCarver.Core.FileTypes;
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

        try
        {
            return ParseDdxHeader(data, offset, is3Xdo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DdxParser] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
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

        var formatName = TextureFormats.Xbox360GpuTextureFormats.TryGetValue(formatByte, out var fn)
            ? fn
            : $"Unknown(0x{formatByte:X2})";
        var uncompressedSize = CalculateUncompressedSize(width, height, mipCount, formatName);
        var estimatedSize = FindDdxBoundary(data, offset, uncompressedSize);
        var texturePath = TexturePathExtractor.FindPrecedingPath(data, offset, ".ddx");

        var metadata = new Dictionary<string, object>
        {
            ["version"] = version, ["gpuFormat"] = formatByte, ["isTiled"] = isTiled,
            ["dataOffset"] = 0x44, ["uncompressedSize"] = uncompressedSize
        };

        if (texturePath != null)
        {
            metadata["texturePath"] = texturePath;

            // Extract just the filename (e.g., "retainingwall01.ddx" from full path)
            var fileName = Path.GetFileName(texturePath);
            if (!string.IsNullOrEmpty(fileName)) metadata["fileName"] = fileName;

            // Create safe filename for extraction (without extension)
            var safeFileName = Path.GetFileNameWithoutExtension(texturePath);
            if (!string.IsNullOrEmpty(safeFileName))
            {
                metadata["safeName"] = TexturePathExtractor.SanitizeFilename(safeFileName);
            }
        }

        return new ParseResult
        {
            Format = is3Xdo ? "3XDO" : "3XDR", EstimatedSize = estimatedSize, Width = width, Height = height,
            MipCount = mipCount, FourCc = formatName, IsXbox360 = true, Metadata = metadata
        };
    }

    private static bool ValidateDdxHeader(ReadOnlySpan<byte> data, int offset)
    {
        return data[offset + 0x04] != 0xFF && data[offset + 0x24] >= 0x80;
    }

    private static int CalculateUncompressedSize(int width, int height, int mipCount, string formatName)
    {
        var bytesPerBlock = TextureFormats.GetBytesPerBlock(formatName);
        var size = (width + 3) / 4 * ((height + 3) / 4) * bytesPerBlock;

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
        // Start scanning from just after the header - DDX files can be quite small
        const int minScanStart = headerSize + 64;

        // Max size should be based on uncompressed size (compressed is typically smaller)
        // Cap at 10MB to avoid scanning too much
        var maxSize = Math.Min(data.Length - offset,
            Math.Min(headerSize + uncompressedSize * 2 + 512, 10 * 1024 * 1024));

        // DDX files need special validation for the next DDX header
        // Scan for any file signature that would indicate end of this DDX
        for (var i = offset + minScanStart; i < offset + maxSize - 4; i++)
        {
            var slice = data.Slice(i, 4);

            // Check for DDX signatures first (most common case) with validation
            if ((slice.SequenceEqual("3XDO"u8) || slice.SequenceEqual("3XDR"u8)) && IsValidNextDdxHeader(data, i))
                return i - offset;

            // Check for RIFF with validation
            if (slice.SequenceEqual("RIFF"u8) && SignatureBoundaryScanner.IsValidRiffHeader(data, i))
                return i - offset;

            // Check for other file signatures
            if (slice.SequenceEqual("XEX2"u8) ||
                slice.SequenceEqual("XUIS"u8) ||
                slice.SequenceEqual("XUIB"u8) ||
                slice.SequenceEqual("XDBF"u8) ||
                slice.SequenceEqual("TES4"u8) ||
                slice.SequenceEqual("LIPS"u8) ||
                slice.SequenceEqual("scn "u8) ||
                slice.SequenceEqual("DDS "u8) ||
                SignatureBoundaryScanner.IsPngSignature(data, i))
                return i - offset;

            // Check for NIF (Gamebryo) - need more bytes
            if (i + 20 <= data.Length && data.Slice(i, 20).SequenceEqual("Gamebryo File Format"u8))
                return i - offset;
        }

        // No boundary found - use a reasonable estimate based on compressed size
        // DDX compressed data is typically 20-70% of uncompressed size
        return headerSize + Math.Max(100, uncompressedSize * 7 / 10);
    }

    private static bool IsValidNextDdxHeader(ReadOnlySpan<byte> data, int i)
    {
        if (i + 0x30 > data.Length) return false;

        var nextVersion = BinaryUtils.ReadUInt16LE(data, i + 7);
        if (nextVersion is < 3 or > 10 || data[i + 0x24] < 0x80 || data[i + 0x04] == 0xFF) return false;

        var sizeDword = BinaryUtils.ReadUInt32BE(data, i + 0x2C);
        var w = (int)(sizeDword & 0x1FFF) + 1;
        var h = (int)((sizeDword >> 13) & 0x1FFF) + 1;
        return w is > 0 and <= 4096 && h is > 0 and <= 4096 && IsPowerOfTwo(w) && IsPowerOfTwo(h);
    }

    private static bool IsPowerOfTwo(int x)
    {
        return x > 0 && (x & (x - 1)) == 0;
    }
}
