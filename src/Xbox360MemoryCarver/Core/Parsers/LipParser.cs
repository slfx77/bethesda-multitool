using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Bethesda LIP (lip-sync) files.
///     LIP files contain facial animation data for dialogue.
/// </summary>
public class LipParser : IFileParser
{
    private static readonly byte[] LipsSignature = "LIPS"u8.ToArray();

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 12;
        if (data.Length < offset + minHeaderSize) return null;

        // Check magic "LIPS"
        var magic = data.Slice(offset, 4);
        if (!magic.SequenceEqual("LIPS"u8)) return null;

        try
        {
            // LIP file structure (little-endian):
            // 0x00: Magic "LIPS" (4 bytes)
            // 0x04: Version (4 bytes) - typically 1
            // 0x08: File size or data length (4 bytes)
            // Rest: Timing/phoneme data

            var version = BinaryUtils.ReadUInt32LE(data, offset + 4);

            // Version should be reasonable (1-10)
            if (version == 0 || version > 10) return null;

            // Try to read file size from header
            var reportedSize = BinaryUtils.ReadUInt32LE(data, offset + 8);

            int estimatedSize;
            if (reportedSize > minHeaderSize && reportedSize < 5 * 1024 * 1024)
            {
                // Reported size seems valid
                estimatedSize = (int)reportedSize;
            }
            else
            {
                // Fall back to scanning for boundary using shared scanner
                const int minSize = 20;
                const int maxScan = 1 * 1024 * 1024; // LIP files are typically small
                const int defaultSize = 64 * 1024; // Default to 64KB

                estimatedSize = SignatureBoundaryScanner.FindBoundary(
                    data, offset, minSize, maxScan, defaultSize,
                    excludeSignature: LipsSignature, validateRiff: false);
            }

            return new ParseResult
            {
                Format = "LIP",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = version
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LipParser] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
