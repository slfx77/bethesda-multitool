using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Xbox XUI (Xbox User Interface) files.
///     Handles both XUIS (scene) and XUIB (binary) formats.
/// </summary>
public class XuiParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 16;
        if (data.Length < offset + minHeaderSize) return null;

        var magic = data.Slice(offset, 4);
        var isScene = magic.SequenceEqual("XUIS"u8);
        var isBinary = magic.SequenceEqual("XUIB"u8);

        if (!isScene && !isBinary) return null;

        try
        {
            // XUI header structure (big-endian on Xbox 360):
            // 0x00: Magic (4 bytes) - "XUIS" or "XUIB"
            // 0x04: Version (4 bytes)
            // 0x08: File size (4 bytes) - total file size including header
            // 0x0C: Various flags/counts

            var version = BinaryUtils.ReadUInt32BE(data, offset + 4);
            var fileSize = BinaryUtils.ReadUInt32BE(data, offset + 8);

            // Validate the reported size
            if (fileSize < minHeaderSize || fileSize > 10 * 1024 * 1024) // Max 10MB sanity check
            {
                // Try little-endian interpretation
                fileSize = BinaryUtils.ReadUInt32LE(data, offset + 8);
                if (fileSize < minHeaderSize || fileSize > 10 * 1024 * 1024)
                {
                    // Fall back to scanning for next signature using shared scanner
                    const int minSize = 1024; // Minimum reasonable XUI size
                    const int maxScan = 5 * 1024 * 1024;
                    const int defaultSize = 256 * 1024; // Default to 256KB

                    // Exclude the current XUI signature type from detection
                    var excludeSig = isScene ? "XUIS"u8 : "XUIB"u8;
                    fileSize = (uint)SignatureBoundaryScanner.FindBoundary(
                        data, offset, minSize, maxScan, defaultSize,
                        excludeSignature: excludeSig, validateRiff: false);
                }
            }

            return new ParseResult
            {
                Format = isScene ? "XUI Scene" : "XUI Binary",
                EstimatedSize = (int)fileSize,
                Metadata = new Dictionary<string, object>
                {
                    ["version"] = version,
                    ["isScene"] = isScene
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[XuiParser] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
