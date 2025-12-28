using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Xbox Media Audio (XMA) files.
/// </summary>
public class XmaParser : IFileParser
{
    private static readonly ushort[] XmaFormatCodes = [0x0165, 0x0166];

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 12)
        {
            return null;
        }

        if (!data.Slice(offset, 4).SequenceEqual("RIFF"u8))
        {
            return null;
        }

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

            if (chunkId.SequenceEqual("XMA2"u8))
            {
                return CreateXmaResult(fileSize, null);
            }

            if (chunkId.SequenceEqual("fmt "u8) && data.Length >= searchOffset + 10)
            {
                var formatTag = (ushort)(BinaryUtils.ReadUInt32LE(data, searchOffset + 8) & 0xFFFF);
                if (XmaFormatCodes.Contains(formatTag))
                {
                    return CreateXmaResult(fileSize, formatTag);
                }
            }

            var chunkSize = BinaryUtils.ReadUInt32LE(data, searchOffset + 4);
            searchOffset += 8 + (int)((chunkSize + 1) & ~1u);
        }

        return null;
    }

    private static ParseResult CreateXmaResult(int fileSize, ushort? formatTag)
    {
        var metadata = new Dictionary<string, object> { ["isXma"] = true };
        if (formatTag.HasValue)
        {
            metadata["formatTag"] = formatTag.Value;
        }

        return new ParseResult
        {
            Format = "XMA",
            EstimatedSize = fileSize,
            IsXbox360 = true,
            Metadata = metadata
        };
    }
}
