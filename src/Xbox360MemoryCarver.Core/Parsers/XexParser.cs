using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Xbox 360 Executable (XEX) files.
///     Note: In memory dumps, XEX2 signatures may appear in various contexts:
///     - Loaded module headers
///     - Embedded executables in game data
///     - False positives in compressed/encrypted data
/// </summary>
public class XexParser : IFileParser
{
    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 24)
        {
            return null;
        }

        // Check for XEX2 magic (big-endian)
        if (!data.Slice(offset, 4).SequenceEqual("XEX2"u8))
        {
            return null;
        }

        try
        {
            // XEX2 header structure (big-endian):
            // 0x00: Magic "XEX2"
            // 0x04: Module flags (uint32 BE)
            // 0x08: Data offset (uint32 BE) - offset to PE data
            // 0x0C: Reserved
            // 0x10: Security info offset (uint32 BE)
            // 0x14: Optional header count (uint32 BE)

            var moduleFlags = BinaryUtils.ReadUInt32BE(data, offset + 0x04);
            var dataOffset = BinaryUtils.ReadUInt32BE(data, offset + 0x08);
            var securityInfoOffset = BinaryUtils.ReadUInt32BE(data, offset + 0x10);
            var optionalHeaderCount = BinaryUtils.ReadUInt32BE(data, offset + 0x14);

            // Basic validation
            if (dataOffset == 0 || dataOffset > 50 * 1024 * 1024)
            {
                return null;
            }

            if (optionalHeaderCount > 100)
            {
                return null;
            }

            // Try to find image size from optional headers
            var imageSize = FindImageSize(data, offset, optionalHeaderCount);

            // If we couldn't find image size, estimate based on data offset
            // XEX files typically have headers + compressed PE data
            var estimatedSize = imageSize > 0
                ? imageSize
                : (int)Math.Min(dataOffset * 4, 10 * 1024 * 1024);

            return new ParseResult
            {
                Format = "XEX2",
                EstimatedSize = estimatedSize,
                IsXbox360 = true,
                Metadata = new Dictionary<string, object>
                {
                    ["moduleFlags"] = moduleFlags,
                    ["dataOffset"] = dataOffset,
                    ["securityInfoOffset"] = securityInfoOffset,
                    ["optionalHeaderCount"] = optionalHeaderCount
                }
            };
        }
        catch
        {
            return null;
        }
    }

    private static int FindImageSize(ReadOnlySpan<byte> data, int offset, uint optionalHeaderCount)
    {
        // Optional headers start at offset 0x18
        var headerOffset = offset + 0x18;

        for (var i = 0; i < optionalHeaderCount && headerOffset + 8 <= data.Length; i++)
        {
            var headerId = BinaryUtils.ReadUInt32BE(data, headerOffset);
            var headerData = BinaryUtils.ReadUInt32BE(data, headerOffset + 4);

            // Header ID 0x00010001 contains image size info
            // Header ID 0x00018002 is file size
            if (headerId == 0x00010001 || headerId == 0x00018002)
            {
                // For small headers, data is inline
                if ((headerId & 0xFF) <= 1)
                {
                    return (int)headerData;
                }
            }

            headerOffset += 8;
        }

        return 0;
    }
}
