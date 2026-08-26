using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.FaceGen;

/// <summary>
///     FaceGen TRI generation-input file format module (FRTRI003).
///     TRI files are always little-endian, even on Xbox 360.
///     Only the leading structure is stable: a 64-byte header followed by two Vector3
///     arrays (vertexCount @8 and vertexBlock1Count @28, 12 bytes per entry — mirroring
///     <c>Core/Formats/Nif/Rendering/Inspection/TriParser.cs</c> header words 0 and 5).
///     The tail (morph records, name strings) is variable, so the estimate is a floor
///     and every parse is flagged with boundaryFallback metadata.
/// </summary>
public sealed class TriFormat : FileFormatBase
{
    private const int HeaderSize = 64;
    private const int MaxVertexCount = 500_000;

    public override string FormatId => "tri";
    public override string DisplayName => "TRI";
    public override string Extension => ".tri";
    public override FileCategory Category => FileCategory.Model;
    public override string GroupLabel => "FaceGen";
    public override string OutputFolder => "facegen";
    public override int MinSize => HeaderSize;
    public override int MaxSize => 16 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "tri",
            MagicBytes = "FRTRI003"u8.ToArray(),
            Description = "FaceGen TRI generation input"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + HeaderSize)
        {
            return null;
        }

        if (!data.Slice(offset, 8).SequenceEqual("FRTRI003"u8))
        {
            return null;
        }

        // Header word 0 (@8) = vertex count; header word 5 (@28) = second Vector3 block count.
        var vertexCount = BinaryUtils.ReadUInt32LE(data, offset + 8);
        var vertexBlock1Count = BinaryUtils.ReadUInt32LE(data, offset + 28);

        if (vertexCount is 0 or > MaxVertexCount || vertexBlock1Count > MaxVertexCount)
        {
            return null;
        }

        // Header + the two fixed-width Vector3 blocks. The variable tail is not included.
        var floorSize = HeaderSize + ((long)vertexCount + vertexBlock1Count) * 12;
        if (floorSize > MaxSize)
        {
            return null;
        }

        return new ParseResult
        {
            Format = "TRI",
            EstimatedSize = (int)floorSize,
            Metadata = new Dictionary<string, object>
            {
                ["vertexCount"] = (int)vertexCount,
                ["vertexBlock1Count"] = (int)vertexBlock1Count,
                ["boundaryFallback"] = true,
                ["boundaryFallbackReason"] = "TRI tail is variable; size is the header-derived floor"
            }
        };
    }
}
