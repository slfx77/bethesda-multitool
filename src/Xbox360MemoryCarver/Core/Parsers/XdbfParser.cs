using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Xbox XDBF (Xbox Dashboard File) files.
///     XDBF files contain achievement data, images, and other dashboard content.
/// </summary>
public class XdbfParser : IFileParser
{
    private static readonly byte[] XdbfSignature = "XDBF"u8.ToArray();

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 24;
        if (data.Length < offset + minHeaderSize) return null;

        // Check magic "XDBF"
        var magic = data.Slice(offset, 4);
        if (!magic.SequenceEqual("XDBF"u8)) return null;

        try
        {
            // XDBF header structure (big-endian on Xbox 360):
            // 0x00: Magic "XDBF" (4 bytes)
            // 0x04: Version (4 bytes) - typically 0x10000
            // 0x08: Entry table entry count (4 bytes)
            // 0x0C: Entry table offset relative to end of header (4 bytes)  
            // 0x10: Free space table entry count (4 bytes)
            // 0x14: Free space table offset (4 bytes)

            var version = BinaryUtils.ReadUInt32BE(data, offset + 4);
            var entryCount = BinaryUtils.ReadUInt32BE(data, offset + 8);
            var entryTableOffset = BinaryUtils.ReadUInt32BE(data, offset + 12);
            var freeCount = BinaryUtils.ReadUInt32BE(data, offset + 16);

            // Validate header values
            if (entryCount > 10000 || freeCount > 10000) return null;

            // Header is 24 bytes, then entry table, then data
            const int headerSize = 24;
            var minSize = headerSize + (int)entryTableOffset;
            minSize = Math.Max(minSize, 1024);

            // Use the shared boundary scanner
            const int maxScan = 10 * 1024 * 1024; // XDBF files can be up to 10MB
            const int defaultSize = 512 * 1024; // Default to 512KB

            var estimatedSize = SignatureBoundaryScanner.FindBoundary(
                data, offset, minSize, maxScan, defaultSize,
                excludeSignature: XdbfSignature, validateRiff: false);

            return new ParseResult
            {
                Format = "XDBF",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = version,
                    ["entryCount"] = entryCount,
                    ["freeCount"] = freeCount
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XdbfParser] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
