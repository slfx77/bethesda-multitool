using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Dds;

/// <summary>
///     DirectDraw Surface (DDS) texture format module.
/// </summary>
public sealed class DdsFormat : FileFormatBase
{
    public override string FormatId => "dds";
    public override string DisplayName => "DDS";
    public override string Extension => ".dds";
    public override FileCategory Category => FileCategory.Texture;
    public override string OutputFolder => "textures";
    public override int MinSize => 128;
    public override int MaxSize => 50 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "dds",
            MagicBytes = "DDS "u8.ToArray(),
            Description = "DirectDraw Surface texture"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 128)
        {
            return null;
        }

        var headerData = data.Slice(offset, 128);
        if (!headerData[..4].SequenceEqual("DDS "u8))
        {
            return null;
        }

        try
        {
            var headerSize = BinaryUtils.ReadUInt32LE(headerData, 4);
            var height = BinaryUtils.ReadUInt32LE(headerData, 12);
            var width = BinaryUtils.ReadUInt32LE(headerData, 16);
            var pitchOrLinearSize = BinaryUtils.ReadUInt32LE(headerData, 20);
            var mipmapCount = BinaryUtils.ReadUInt32LE(headerData, 28);
            var fourcc = headerData.Slice(84, 4);
            var endianness = "little";

            // Check if big-endian (Xbox 360)
            if (height > 16384 || width > 16384 || headerSize != 124)
            {
                height = BinaryUtils.ReadUInt32BE(headerData, 12);
                width = BinaryUtils.ReadUInt32BE(headerData, 16);
                pitchOrLinearSize = BinaryUtils.ReadUInt32BE(headerData, 20);
                mipmapCount = BinaryUtils.ReadUInt32BE(headerData, 28);
                endianness = "big";
            }

            if (height == 0 || width == 0 || height > 16384 || width > 16384)
            {
                return null;
            }

            var fourccStr = Encoding.ASCII.GetString(fourcc).TrimEnd('\0');
            var bytesPerBlock = GetBytesPerBlock(fourccStr);
            var estimatedSize = CalculateMipmapSize((int)width, (int)height, (int)mipmapCount, bytesPerBlock);

            // Try to find texture path before the DDS header
            var texturePath = TexturePathExtractor.FindPrecedingDdsPath(data, offset);

            var metadata = new Dictionary<string, object>
            {
                ["pitch"] = pitchOrLinearSize,
                ["endianness"] = endianness,
                ["width"] = (int)width,
                ["height"] = (int)height,
                ["mipCount"] = (int)mipmapCount,
                ["fourCc"] = fourccStr,
                ["isXbox360"] = endianness == "big"
            };

            string? fileName = null;
            if (!string.IsNullOrEmpty(texturePath))
            {
                metadata["texturePath"] = texturePath;
                var fn = Path.GetFileName(texturePath);
                if (!string.IsNullOrEmpty(fn))
                {
                    metadata["fileName"] = fn;
                    metadata["safeName"] = TexturePathExtractor.SanitizeFilename(Path.GetFileNameWithoutExtension(fn));
                    fileName = fn;
                }
            }

            return new ParseResult
            {
                Format = "DDS",
                EstimatedSize = estimatedSize + 128,
                FileName = fileName,
                Metadata = metadata
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DdsFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public override string GetDisplayDescription(string signatureId,
        IReadOnlyDictionary<string, object>? metadata = null)
    {
        if (metadata != null && metadata.TryGetValue("width", out var w) && metadata.TryGetValue("height", out var h))
        {
            return $"DDS ({w}x{h})";
        }

        return "DirectDraw Surface texture";
    }

    private static int GetBytesPerBlock(string fourcc)
    {
        return fourcc switch
        {
            "DXT1" => 8,
            "ATI1" or "BC4U" or "BC4S" => 8,
            _ => 16 // DXT2-5, ATI2, BC5U, BC5S, and others default to 16
        };
    }

    private static int CalculateMipmapSize(int width, int height, int mipmapCount, int bytesPerBlock)
    {
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var estimatedSize = blocksWide * blocksHigh * bytesPerBlock;

        var mipWidth = width;
        var mipHeight = height;
        for (var i = 1; i < mipmapCount && i < 13; i++)
        {
            mipWidth = Math.Max(1, mipWidth / 2);
            mipHeight = Math.Max(1, mipHeight / 2);
            blocksWide = Math.Max(1, (mipWidth + 3) / 4);
            blocksHigh = Math.Max(1, (mipHeight + 3) / 4);
            estimatedSize += blocksWide * blocksHigh * bytesPerBlock;
        }

        return estimatedSize;
    }
}
