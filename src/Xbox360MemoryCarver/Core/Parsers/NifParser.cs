using System.Text;
using System.Text.RegularExpressions;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for NetImmerse/Gamebryo (NIF) model files.
/// </summary>
public partial class NifParser : IFileParser
{
    private static readonly byte[] GamebryoSignature = "Gamebryo File Format"u8.ToArray();

    // Valid NIF version strings follow pattern: "X.Y.Z.W" or "X, Y, Z, W"
    // Known versions: 4.0.0.2, 10.0.1.0, 10.1.0.0, 10.2.0.0, 20.0.0.4, 20.0.0.5, 20.2.0.7, etc.
    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}\.\d{1,2}\.\d{1,2}$")]
    private static partial Regex VersionPattern();

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        // Need at least 64 bytes for basic header validation
        if (data.Length < offset + 64) return null;

        var headerMagic = data.Slice(offset, 20);
        if (!headerMagic.SequenceEqual("Gamebryo File Format"u8)) return null;

        try
        {
            // After "Gamebryo File Format" should come ", Version " (12 bytes)
            // Total prefix: "Gamebryo File Format, Version " = 30 bytes
            if (data.Length < offset + 50) return null;

            // Check for ", Version " after the magic
            var versionPrefixSpan = data.Slice(offset + 20, 10);
            if (!versionPrefixSpan.SequenceEqual(", Version "u8))
            {
                // Not a valid NIF header - just happens to contain "Gamebryo File Format" text
                return null;
            }

            // Version string starts at offset + 30
            var versionOffset = offset + 30;

            // Find the newline (0x0A) that terminates the version string
            // Version string should be at most 15 characters (e.g., "20.2.0.7")
            var newlinePos = -1;
            for (int i = versionOffset; i < Math.Min(versionOffset + 20, data.Length); i++)
            {
                if (data[i] == 0x0A) // newline
                {
                    newlinePos = i;
                    break;
                }
            }

            if (newlinePos == -1 || newlinePos <= versionOffset)
            {
                // No valid version terminator found
                return null;
            }

            var versionString = Encoding.ASCII.GetString(data[versionOffset..newlinePos]);

            // Validate version string format (X.Y.Z.W pattern)
            if (!VersionPattern().IsMatch(versionString))
            {
                // Invalid version format - likely false positive
                return null;
            }

            // Parse the major version to further validate
            var majorVersion = 0;
            var dotIdx = versionString.IndexOf('.');
            if (dotIdx > 0 && int.TryParse(versionString[..dotIdx], out var major))
            {
                majorVersion = major;
            }

            // Known NIF versions are 2.x through 20.x
            // Fallout New Vegas uses 20.0.0.5 and 20.2.0.7
            if (majorVersion < 2 || majorVersion > 30)
            {
                // Version out of expected range
                return null;
            }

            // After the newline, check for valid binary data
            // Byte at newlinePos+1 should be the little-endian version uint32
            if (data.Length >= newlinePos + 5)
            {
                // Read the 4-byte version number
                var binaryVersion = BinaryUtils.ReadUInt32LE(data, newlinePos + 1);

                // Valid version numbers are encoded as hex digits:
                // 20.0.0.5 = 0x14000005, 20.2.0.7 = 0x14020007
                // First nibble should be the major version in hex (20 decimal = 0x14)
                var binaryMajor = (binaryVersion >> 24) & 0xFF;

                // Allow some flexibility but reject obviously wrong values
                if (binaryMajor == 0 || binaryMajor > 0x20)
                {
                    // Binary version doesn't match expected pattern
                    return null;
                }
            }

            var estimatedSize = EstimateNifSize(data, offset, newlinePos, versionString);

            return new ParseResult
            {
                Format = "NIF",
                EstimatedSize = estimatedSize,
                Metadata = new Dictionary<string, object> { ["version"] = versionString }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NifParser] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static int EstimateNifSize(ReadOnlySpan<byte> data, int offset, int newlinePos, string versionString)
    {
        // Start with a reasonable default
        var estimatedSize = 50000;

        // Try to parse block count for better estimation
        if (versionString.Contains("20.", StringComparison.Ordinal))
        {
            var parseOffset = newlinePos + 1;
            if (data.Length >= offset + 100)
                for (var testOffset = parseOffset;
                     testOffset < Math.Min(parseOffset + 60, data.Length - 4);
                     testOffset += 4)
                {
                    var potentialBlocks = BinaryUtils.ReadUInt32LE(data, testOffset);
                    if (potentialBlocks is >= 1 and <= 10000)
                    {
                        estimatedSize = Math.Min((int)(potentialBlocks * 500 + 1000), 20 * 1024 * 1024);
                        break;
                    }
                }
        }

        // Use the shared boundary scanner to find actual boundary
        const int minScanStart = 100; // NIF header is at least ~100 bytes
        const int maxScan = 20 * 1024 * 1024; // Max 20MB

        var boundarySize = SignatureBoundaryScanner.FindBoundary(
            data, offset, minScanStart, maxScan, estimatedSize,
            excludeSignature: GamebryoSignature, validateRiff: true);

        return boundarySize;
    }
}
