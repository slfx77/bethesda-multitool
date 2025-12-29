using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Bethesda ESP/ESM (Elder Scrolls Plugin/Master) files.
///     These files use the TES4 record format.
/// </summary>
public class EspParser : IFileParser
{
    private static readonly byte[] Tes4Signature = "TES4"u8.ToArray();

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        const int minHeaderSize = 24;
        if (data.Length < offset + minHeaderSize) return null;

        // Check magic "TES4"
        var magic = data.Slice(offset, 4);
        if (!magic.SequenceEqual("TES4"u8)) return null;

        try
        {
            // TES4 record header structure (little-endian):
            // 0x00: Type "TES4" (4 bytes)
            // 0x04: Data size (4 bytes) - size of record data (not including header)
            // 0x08: Flags (4 bytes)
            // 0x0C: Form ID (4 bytes)
            // 0x10: Version control info (4 bytes)
            // 0x14: Form version (2 bytes)
            // 0x16: Unknown (2 bytes)
            // Total header: 24 bytes, then data follows

            var dataSize = BinaryUtils.ReadUInt32LE(data, offset + 4);
            var flags = BinaryUtils.ReadUInt32LE(data, offset + 8);
            var formId = BinaryUtils.ReadUInt32LE(data, offset + 12);

            // Validate - data size should be reasonable
            if (dataSize > 500 * 1024 * 1024) // Max 500MB
                return null;

            // Check if this is a master file (ESM) or plugin (ESP)
            var isMaster = (flags & 0x01) != 0;

            // For memory dumps, we likely have partial files
            // Scan for actual file boundary using the shared scanner
            var headerDataSize = (int)dataSize + 24;
            var minSize = Math.Min(headerDataSize, 1024);
            const int maxScan = 100 * 1024 * 1024; // ESP files can be very large

            // Use the shared boundary scanner, but we need special handling for TES4
            // because TES4 files contain many internal record types
            var estimatedSize = FindEspBoundary(data, offset, headerDataSize);

            return new ParseResult
            {
                Format = isMaster ? "ESM" : "ESP",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object>
                {
                    ["dataSize"] = dataSize,
                    ["flags"] = flags,
                    ["formId"] = formId,
                    ["isMaster"] = isMaster
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EspParser] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Find the boundary of an ESP/ESM file.
    ///     ESP files need special handling because they contain many internal record types.
    /// </summary>
    private static int FindEspBoundary(ReadOnlySpan<byte> data, int offset, int headerDataSize)
    {
        const int maxScan = 100 * 1024 * 1024; // ESP files can be very large

        var scanLimit = Math.Min(data.Length - offset, maxScan);
        var minSize = Math.Min(headerDataSize, 1024);

        // In a memory dump, we might not have the full file
        // Look for other file signatures that would indicate end
        // Be careful - TES4 files contain many record types with 4-byte signatures
        // Only look for definitely external file signatures
        for (var i = offset + minSize; i < offset + scanLimit - 4; i++)
        {
            var slice = data.Slice(i, 4);

            // Check for external file signatures (not internal ESP record types)
            if (slice.SequenceEqual("XEX2"u8) ||
                slice.SequenceEqual("XDBF"u8) ||
                slice.SequenceEqual("XUIS"u8) ||
                slice.SequenceEqual("XUIB"u8) ||
                slice.SequenceEqual("RIFF"u8) ||
                slice.SequenceEqual("3XDO"u8) ||
                slice.SequenceEqual("3XDR"u8) ||
                slice.SequenceEqual("DDS "u8) ||
                SignatureBoundaryScanner.IsPngSignature(data, i))
                return i - offset;

            // Check for NIF (Gamebryo)
            if (i + 20 <= data.Length && data.Slice(i, 20).SequenceEqual("Gamebryo File Format"u8))
                return i - offset;

            // Check for another TES4 header which would indicate a new ESP file
            if (slice.SequenceEqual("TES4"u8) && i > offset + 24 && data.Length > i + 8)
            {
                var nextDataSize = BinaryUtils.ReadUInt32LE(data, i + 4);
                if (nextDataSize > 0 && nextDataSize < 100 * 1024 * 1024) return i - offset;
            }
        }

        // No boundary found - use the header-reported size if reasonable
        if (headerDataSize > 24 && headerDataSize < scanLimit) return headerDataSize;

        // Default to a reasonable size for partial ESP data
        return Math.Min(1 * 1024 * 1024, scanLimit); // Default to 1MB
    }
}
