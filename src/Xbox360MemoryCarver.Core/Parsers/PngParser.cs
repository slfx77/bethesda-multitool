namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for PNG image files.
/// </summary>
public class PngParser : IFileParser
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] IendMagic = [0x49, 0x45, 0x4E, 0x44];

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 8)
        {
            return null;
        }

        if (!data.Slice(offset, 8).SequenceEqual(PngMagic))
        {
            return null;
        }

        var searchPos = offset + 8;
        var maxSearch = Math.Min(offset + 50 * 1024 * 1024, data.Length - 4);

        while (searchPos < maxSearch)
        {
            if (data.Slice(searchPos, 4).SequenceEqual(IendMagic))
            {
                return new ParseResult
                {
                    Format = "PNG",
                    EstimatedSize = searchPos + 8 - offset
                };
            }

            searchPos++;
        }

        return null;
    }
}
