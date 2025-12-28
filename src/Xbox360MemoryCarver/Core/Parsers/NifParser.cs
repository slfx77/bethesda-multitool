using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for NetImmerse/Gamebryo (NIF) model files.
/// </summary>
public class NifParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 64)
        {
            return null;
        }

        var headerMagic = data.Slice(offset, 20);
        if (!headerMagic.SequenceEqual("Gamebryo File Format"u8))
        {
            return null;
        }

        try
        {
            var versionOffset = offset + 22;
            var nullPos = FindNullTerminator(data, versionOffset, 40);

            if (nullPos == -1)
            {
                return null;
            }

            var versionString = Encoding.ASCII.GetString(data[versionOffset..nullPos]);
            var estimatedSize = EstimateNifSize(data, offset, nullPos, versionString);

            return new ParseResult
            {
                Format = "NIF",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object> { ["version"] = versionString }
            };
        }
        catch
        {
            return null;
        }
    }

    private static int FindNullTerminator(ReadOnlySpan<byte> data, int start, int maxLength)
    {
        var end = Math.Min(start + maxLength, data.Length);
        for (var i = start; i < end; i++)
        {
            if (data[i] == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int EstimateNifSize(ReadOnlySpan<byte> data, int offset, int nullPos, string versionString)
    {
        var estimatedSize = 50000;

        if (!versionString.Contains("20.", StringComparison.Ordinal))
        {
            return estimatedSize;
        }

        var parseOffset = nullPos + 1;
        if (data.Length < offset + 100)
        {
            return estimatedSize;
        }

        for (var testOffset = parseOffset; testOffset < Math.Min(parseOffset + 60, data.Length - 4); testOffset += 4)
        {
            var potentialBlocks = BinaryUtils.ReadUInt32LE(data, testOffset);
            if (potentialBlocks is >= 1 and <= 10000)
            {
                estimatedSize = Math.Min((int)(potentialBlocks * 500 + 1000), 20 * 1024 * 1024);
                break;
            }
        }

        return estimatedSize;
    }
}
