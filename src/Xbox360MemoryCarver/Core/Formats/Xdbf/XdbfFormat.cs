using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xdbf;

/// <summary>
///     Xbox XDBF (Xbox Dashboard File) format module.
/// </summary>
public sealed class XdbfFormat : FileFormatBase
{
    public override string FormatId => "xdbf";
    public override string DisplayName => "XDBF";
    public override string Extension => ".xdbf";
    public override FileCategory Category => FileCategory.Xbox;
    public override string OutputFolder => "xbox";
    public override int MinSize => 24;
    public override int MaxSize => 10 * 1024 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new()
        {
            Id = "xdbf",
            MagicBytes = "XDBF"u8.ToArray(),
            Description = "Xbox Dashboard File"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 24;
        if (data.Length < offset + minHeaderSize)
        {
            return null;
        }

        var magic = data.Slice(offset, 4);
        if (!magic.SequenceEqual("XDBF"u8))
        {
            return null;
        }

        try
        {
            var version = BinaryUtils.ReadUInt32BE(data, offset + 4);
            var entryCount = BinaryUtils.ReadUInt32BE(data, offset + 8);
            var entryTableOffset = BinaryUtils.ReadUInt32BE(data, offset + 12);
            var freeCount = BinaryUtils.ReadUInt32BE(data, offset + 16);

            if (entryCount > 10000 || freeCount > 10000)
            {
                return null;
            }

            const int headerSize = 24;
            var minSize = Math.Max(headerSize + (int)entryTableOffset, 1024);

            const int maxScan = 10 * 1024 * 1024;
            const int defaultSize = 512 * 1024;

            var estimatedSize = SignatureBoundaryScanner.FindBoundary(
                data, offset, minSize, maxScan, defaultSize,
                "XDBF"u8, false);

            return new ParseResult
            {
                Format = "XDBF",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = version,
                    ["entryCount"] = entryCount
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XdbfFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
