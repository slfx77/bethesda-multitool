using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.FaceGen;

/// <summary>
///     FaceGen EGM geometry-morph file format module (FREGM002).
///     EGM files are always little-endian, even on Xbox 360.
///     Size is exact and header-computed: 64-byte header, then (sym + asym) morphs of
///     (4-byte float scale + vertexCount * 6 bytes of int16 XYZ deltas) each.
///     Layout mirrors <c>Core/Formats/Nif/Rendering/FaceGen/EgmParser.cs</c>.
/// </summary>
public sealed class EgmFormat : FileFormatBase
{
    private const int HeaderSize = 64;
    private const int MaxVertexCount = 500_000;
    private const int MaxMorphCount = 200;

    public override string FormatId => "egm";
    public override string DisplayName => "EGM";
    public override string Extension => ".egm";
    public override FileCategory Category => FileCategory.Model;
    public override string GroupLabel => "FaceGen";
    public override string OutputFolder => "facegen";
    public override int MinSize => HeaderSize;
    public override int MaxSize => 16 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "egm",
            MagicBytes = "FREGM002"u8.ToArray(),
            Description = "FaceGen EGM geometry morph"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + HeaderSize)
        {
            return null;
        }

        if (!data.Slice(offset, 8).SequenceEqual("FREGM002"u8))
        {
            return null;
        }

        // Header: vertexCount u32LE @8, symmetric morph count @12, asymmetric morph count @16.
        var vertexCount = BinaryUtils.ReadUInt32LE(data, offset + 8);
        var symCount = BinaryUtils.ReadUInt32LE(data, offset + 12);
        var asymCount = BinaryUtils.ReadUInt32LE(data, offset + 16);

        if (vertexCount is 0 or > MaxVertexCount || symCount > MaxMorphCount || asymCount > MaxMorphCount)
        {
            return null;
        }

        var totalSize = HeaderSize + (long)(symCount + asymCount) * (4 + (long)vertexCount * 6);
        if (totalSize > MaxSize)
        {
            return null;
        }

        return new ParseResult
        {
            Format = "EGM",
            EstimatedSize = (int)totalSize,
            Metadata = new Dictionary<string, object>
            {
                ["vertexCount"] = (int)vertexCount,
                ["symMorphCount"] = (int)symCount,
                ["asymMorphCount"] = (int)asymCount
            }
        };
    }
}
