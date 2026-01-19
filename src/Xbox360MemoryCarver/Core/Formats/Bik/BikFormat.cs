using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Bik;

/// <summary>
///     RAD Game Tools Bink video format module.
///     Used for pre-rendered cinematics and logo videos.
/// </summary>
/// <remarks>
///     Bink video files have a clear "BIK" signature followed by version letter:
///     - BIKi - Bink 2.x (common on Xbox 360)
///     - BIKh - Bink 2.x variant
///     - BIKb - Older Bink variant
///     Header structure:
///     - 0x00: Signature (4 bytes, e.g., "BIKi")
///     - 0x04: File size (uint32, little-endian)
///     - 0x08: Frame count (uint32)
///     - 0x0C: Largest frame size (uint32)
///     - 0x10: Frame count again (uint32)
///     - 0x14: Width (uint32)
///     - 0x18: Height (uint32)
///     - 0x1C: FPS dividend (uint32)
///     - 0x20: FPS divisor (uint32)
///     Note: Bink videos are typically large (10-200+ MB) and may only be partially
///     loaded during playback. The file size field in the header is reliable.
/// </remarks>
public sealed class BikFormat : FileFormatBase
{
    public override string FormatId => "bik";
    public override string DisplayName => "BIK";
    public override string Extension => ".bik";
    public override FileCategory Category => FileCategory.Video;
    public override string OutputFolder => "video";
    public override int MinSize => 44;
    public override int MaxSize => 500 * 1024 * 1024; // 500 MB max

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "bik_i",
            MagicBytes = "BIKi"u8.ToArray(),
            Description = "Bink 2.x video"
        },
        new()
        {
            Id = "bik_h",
            MagicBytes = "BIKh"u8.ToArray(),
            Description = "Bink 2.x video (variant)"
        },
        new()
        {
            Id = "bik_b",
            MagicBytes = "BIKb"u8.ToArray(),
            Description = "Bink video (legacy)"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 44;
        if (data.Length < offset + minHeaderSize)
        {
            return null;
        }

        // Verify BIK signature
        var magic = data.Slice(offset, 3);
        if (!magic.SequenceEqual("BIK"u8))
        {
            return null;
        }

        // Check version byte (i, h, b, etc.)
        var versionByte = (char)data[offset + 3];
        if (versionByte < 'a' || versionByte > 'z')
        {
            return null;
        }

        try
        {
            // Read header fields
            var fileSize = BinaryUtils.ReadUInt32LE(data, offset + 4);
            var frameCount = BinaryUtils.ReadUInt32LE(data, offset + 8);
            var largestFrameSize = BinaryUtils.ReadUInt32LE(data, offset + 12);
            var width = BinaryUtils.ReadUInt32LE(data, offset + 20);
            var height = BinaryUtils.ReadUInt32LE(data, offset + 24);

            // Validate dimensions
            if (width == 0 || height == 0 || width > 4096 || height > 4096)
            {
                return null;
            }

            // Validate frame count
            if (frameCount == 0 || frameCount > 1000000)
            {
                return null;
            }

            // File size in header includes header, so use it directly
            // Add 8 bytes for the magic and size fields themselves
            var estimatedSize = (int)Math.Min(fileSize + 8, MaxSize);

            // Sanity check - largest frame shouldn't be bigger than total file
            if (largestFrameSize > fileSize)
            {
                return null;
            }

            return new ParseResult
            {
                Format = $"BIK{versionByte}",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = versionByte.ToString(),
                    ["width"] = (int)width,
                    ["height"] = (int)height,
                    ["frameCount"] = (int)frameCount,
                    ["fileSize"] = (long)fileSize,
                    ["dimensions"] = $"{width}x{height}"
                }
            };
        }
        catch
        {
            return null;
        }
    }
}
