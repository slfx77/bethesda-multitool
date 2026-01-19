using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.FaceGen;

/// <summary>
///     Bethesda FaceGen format module for face morphs and tints.
///     Handles EGM (morph data), EGT (tint data), and TRI (triangle morphs).
/// </summary>
/// <remarks>
///     FaceGen files have clear magic signatures:
///     - FREGM002 - Face morph data (.egm)
///     - FREGT003 - Face tint data (.egt)
///     - FRTRI003 - Face triangle morphs (.tri)
///     These files are used for character face customization and may be loaded
///     when NPCs or the player character are rendered.
/// </remarks>
public sealed class FaceGenFormat : FileFormatBase
{
    public override string FormatId => "facegen";
    public override string DisplayName => "FaceGen";
    public override string Extension => ".egm"; // Primary extension
    public override FileCategory Category => FileCategory.Model;
    public override string OutputFolder => "facegen";
    public override int MinSize => 32;
    public override int MaxSize => 5 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "facegen_egm",
            MagicBytes = "FREGM"u8.ToArray(),
            Description = "FaceGen morph data (.egm)"
        },
        new()
        {
            Id = "facegen_egt",
            MagicBytes = "FREGT"u8.ToArray(),
            Description = "FaceGen tint data (.egt)"
        },
        new()
        {
            Id = "facegen_tri",
            MagicBytes = "FRTRI"u8.ToArray(),
            Description = "FaceGen triangle morphs (.tri)"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 32;
        if (data.Length < offset + minHeaderSize)
        {
            return null;
        }

        // Detect which FaceGen type this is
        var (formatType, extension) = DetectFaceGenType(data, offset);
        if (formatType == null)
        {
            return null;
        }

        try
        {
            // FaceGen header structure (common pattern):
            // 0x00: Magic (5-8 bytes, e.g., "FREGM002", "FREGT003", "FRTRI003")
            // After magic: Various counts and offsets

            // Read version suffix (e.g., "002", "003")
            var versionStr = Encoding.ASCII.GetString(data.Slice(offset + 5, 3));
            if (!int.TryParse(versionStr, out var version))
            {
                return null;
            }

            if (version < 1 || version > 10)
            {
                return null;
            }

            // Read first size field after magic (at offset 8)
            var size1 = BinaryUtils.ReadUInt32LE(data, offset + 8);
            var size2 = BinaryUtils.ReadUInt32LE(data, offset + 12);

            // Sanity check sizes
            if (size1 > 100000 || size2 > 100000)
            {
                return null;
            }

            // Estimate size using boundary scanning
            // Use current format's magic as exclude signature to avoid matching self
            var excludeSignature = formatType switch
            {
                "EGM" => "FREGM"u8,
                "EGT" => "FREGT"u8,
                "TRI" => "FRTRI"u8,
                _ => ReadOnlySpan<byte>.Empty
            };

            var estimatedSize = SignatureBoundaryScanner.FindBoundary(
                data, offset, minHeaderSize, 2 * 1024 * 1024, 64 * 1024,
                excludeSignature);

            return new ParseResult
            {
                Format = formatType,
                EstimatedSize = estimatedSize,
                ExtensionOverride = extension,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = version,
                    ["type"] = formatType
                }
            };
        }
        catch
        {
            return null;
        }
    }

    private static (string? formatType, string? extension) DetectFaceGenType(ReadOnlySpan<byte> data, int offset)
    {
        if (data.Length < offset + 8)
        {
            return (null, null);
        }

        var magic5 = data.Slice(offset, 5);

        if (magic5.SequenceEqual("FREGM"u8))
        {
            return ("EGM", ".egm");
        }

        if (magic5.SequenceEqual("FREGT"u8))
        {
            return ("EGT", ".egt");
        }

        if (magic5.SequenceEqual("FRTRI"u8))
        {
            return ("TRI", ".tri");
        }

        return (null, null);
    }
}
