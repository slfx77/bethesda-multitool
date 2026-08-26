using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.FaceGen;

/// <summary>
///     FaceGen EGT texture-morph file format module (FREGT003).
///     EGT files are always little-endian, even on Xbox 360.
///     Size is exact and header-computed: 64-byte header, then (sym + asym) morphs of
///     (4-byte float scale + 3 RGB channels of rows * align8(cols) int8 deltas) each —
///     the engine aligns each row's stride to 8 bytes.
///     Layout mirrors <c>Core/Formats/Nif/Rendering/FaceGen/EgtParser.cs</c>.
/// </summary>
public sealed class EgtFormat : FileFormatBase
{
    private const int HeaderSize = 64;
    private const int ChannelCount = 3; // RGB
    private const int MaxDimension = 500_000;
    private const int MaxMorphCount = 200;

    public override string FormatId => "egt";
    public override string DisplayName => "EGT";
    public override string Extension => ".egt";
    public override FileCategory Category => FileCategory.Model;
    public override string GroupLabel => "FaceGen";
    public override string OutputFolder => "facegen";
    public override int MinSize => HeaderSize;
    public override int MaxSize => 16 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "egt",
            MagicBytes = "FREGT003"u8.ToArray(),
            Description = "FaceGen EGT texture morph"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + HeaderSize)
        {
            return null;
        }

        if (!data.Slice(offset, 8).SequenceEqual("FREGT003"u8))
        {
            return null;
        }

        // Header: rows u32LE @8, cols @12, symmetric morph count @16, asymmetric morph count @20.
        var rows = BinaryUtils.ReadUInt32LE(data, offset + 8);
        var cols = BinaryUtils.ReadUInt32LE(data, offset + 12);
        var symCount = BinaryUtils.ReadUInt32LE(data, offset + 16);
        var asymCount = BinaryUtils.ReadUInt32LE(data, offset + 20);

        if (rows is 0 or > MaxDimension || cols is 0 or > MaxDimension ||
            symCount > MaxMorphCount || asymCount > MaxMorphCount)
        {
            return null;
        }

        var alignedCols = (long)((cols + 7) & ~7u);
        var perMorphSize = 4 + ChannelCount * alignedCols * rows;
        var totalSize = HeaderSize + (symCount + asymCount) * perMorphSize;
        if (totalSize > MaxSize)
        {
            return null;
        }

        return new ParseResult
        {
            Format = "EGT",
            EstimatedSize = (int)totalSize,
            Metadata = new Dictionary<string, object>
            {
                ["rows"] = (int)rows,
                ["cols"] = (int)cols,
                ["symMorphCount"] = (int)symCount,
                ["asymMorphCount"] = (int)asymCount
            }
        };
    }
}
