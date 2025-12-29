using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Utility for finding file boundaries by scanning for known signatures.
/// </summary>
internal static class SignatureBoundaryScanner
{
    /// <summary>
    ///     Known file signatures to scan for when determining file boundaries.
    /// </summary>
    private static readonly byte[][] KnownSignatures =
    [
        "3XDO"u8.ToArray(), // DDX texture
        "3XDR"u8.ToArray(), // DDX texture
        "RIFF"u8.ToArray(), // Audio (XMA, WAV)
        "XEX2"u8.ToArray(), // Xbox Executable
        "XUIS"u8.ToArray(), // XUI Scene
        "XUIB"u8.ToArray(), // XUI Binary
        "XDBF"u8.ToArray(), // Dashboard file
        "TES4"u8.ToArray(), // ESP/ESM plugin
        "LIPS"u8.ToArray(), // Lip sync
        "scn "u8.ToArray(), // Script
        "DDS "u8.ToArray(), // DDS texture
        [0x89, 0x50, 0x4E, 0x47] // PNG
    ];

    /// <summary>
    ///     Gamebryo/NIF signature (20 bytes).
    /// </summary>
    private static readonly byte[] GamebryoSignature = "Gamebryo File Format"u8.ToArray();

    /// <summary>
    ///     Scan for the next file signature starting from a given position.
    /// </summary>
    /// <param name="data">The data buffer to scan.</param>
    /// <param name="offset">The offset where the current file starts.</param>
    /// <param name="minSize">Minimum size before starting to scan (to avoid false positives in headers).</param>
    /// <param name="maxSize">Maximum size to scan.</param>
    /// <param name="excludeSignature">Optional signature to exclude (e.g., skip detecting same type).</param>
    /// <returns>The offset of the next signature relative to the file start, or -1 if not found.</returns>
    public static int FindNextSignature(
        ReadOnlySpan<byte> data,
        int offset,
        int minSize,
        int maxSize,
        ReadOnlySpan<byte> excludeSignature = default)
    {
        return FindNextSignatureCore(data, offset, minSize, maxSize, excludeSignature, validateRiff: false);
    }

    /// <summary>
    ///     Scan for the next file signature with RIFF validation.
    ///     RIFF headers will be validated to ensure they have reasonable size and WAVE format.
    /// </summary>
    /// <param name="data">The data buffer to scan.</param>
    /// <param name="offset">The offset where the current file starts.</param>
    /// <param name="minSize">Minimum size before starting to scan (to avoid false positives in headers).</param>
    /// <param name="maxSize">Maximum size to scan.</param>
    /// <param name="excludeSignature">Optional signature to exclude (e.g., skip detecting same type).</param>
    /// <returns>The offset of the next signature relative to the file start, or -1 if not found.</returns>
    public static int FindNextSignatureWithRiffValidation(
        ReadOnlySpan<byte> data,
        int offset,
        int minSize,
        int maxSize,
        ReadOnlySpan<byte> excludeSignature = default)
    {
        return FindNextSignatureCore(data, offset, minSize, maxSize, excludeSignature, validateRiff: true);
    }

    /// <summary>
    ///     Find the boundary of a file by scanning for the next known signature.
    ///     Returns a size relative to the file start.
    /// </summary>
    /// <param name="data">The data buffer to scan.</param>
    /// <param name="offset">The offset where the current file starts.</param>
    /// <param name="minSize">Minimum size before starting to scan.</param>
    /// <param name="maxSize">Maximum size to scan.</param>
    /// <param name="defaultSize">Default size to return if no boundary is found.</param>
    /// <param name="excludeSignature">Optional signature to exclude from detection.</param>
    /// <param name="validateRiff">If true, validate RIFF headers before treating them as boundaries.</param>
    /// <returns>The estimated file size.</returns>
    public static int FindBoundary(
        ReadOnlySpan<byte> data,
        int offset,
        int minSize,
        int maxSize,
        int defaultSize,
        ReadOnlySpan<byte> excludeSignature = default,
        bool validateRiff = true)
    {
        var boundaryOffset = validateRiff
            ? FindNextSignatureWithRiffValidation(data, offset, minSize, maxSize, excludeSignature)
            : FindNextSignature(data, offset, minSize, maxSize, excludeSignature);

        if (boundaryOffset > 0)
        {
            return boundaryOffset;
        }

        // No boundary found, use default but cap at available data
        var availableData = Math.Min(data.Length - offset, maxSize);
        return Math.Min(defaultSize, availableData);
    }

    /// <summary>
    ///     Check if a position contains any known file signature.
    /// </summary>
    public static bool IsKnownSignature(ReadOnlySpan<byte> data, int position)
    {
        if (position + 4 > data.Length) return false;

        var slice = data.Slice(position, 4);

#pragma warning disable S3267 // Loops should be simplified using LINQ - cannot use Span in lambda
        foreach (var sig in KnownSignatures)
            if (slice.SequenceEqual(sig))
                return true;
#pragma warning restore S3267

        // Check Gamebryo
        if (position + 20 <= data.Length && data.Slice(position, 20).SequenceEqual(GamebryoSignature)) return true;

        return false;
    }

    /// <summary>
    ///     Check if a position contains a PNG signature.
    /// </summary>
    public static bool IsPngSignature(ReadOnlySpan<byte> data, int position)
    {
        if (position + 4 > data.Length) return false;
        return data[position] == 0x89 && data[position + 1] == 0x50 &&
               data[position + 2] == 0x4E && data[position + 3] == 0x47;
    }

    /// <summary>
    ///     Validate that a RIFF header looks legitimate (has valid size and WAVE format).
    /// </summary>
    public static bool IsValidRiffHeader(ReadOnlySpan<byte> data, int position)
    {
        if (position + 12 > data.Length) return false;

        // Check RIFF size is reasonable (not 0, not too large)
        var riffSize = BinaryUtils.ReadUInt32LE(data, position + 4);
        if (riffSize < 36 || riffSize > 100 * 1024 * 1024) return false;

        // Check format is WAVE
        return data.Slice(position + 8, 4).SequenceEqual("WAVE"u8);
    }

    private static int FindNextSignatureCore(
        ReadOnlySpan<byte> data,
        int offset,
        int minSize,
        int maxSize,
        ReadOnlySpan<byte> excludeSignature,
        bool validateRiff)
    {
        var scanStart = offset + minSize;
        var scanEnd = Math.Min(offset + maxSize, data.Length - 4);

        for (var i = scanStart; i < scanEnd; i++)
        {
            // Check 4-byte signatures
            var slice = data.Slice(i, 4);

#pragma warning disable S3267 // Loops should be simplified using LINQ - cannot use Span in lambda
            foreach (var sig in KnownSignatures)
            {
                if (!slice.SequenceEqual(sig)) continue;

                // Skip if this is the excluded signature
                if (!excludeSignature.IsEmpty && slice.SequenceEqual(excludeSignature)) continue;

                // Validate RIFF headers if requested
                if (validateRiff && slice.SequenceEqual("RIFF"u8))
                {
                    if (!IsValidRiffHeader(data, i)) continue;
                }

                return i - offset;
            }
#pragma warning restore S3267

            // Check for Gamebryo/NIF (20-byte signature)
            if (i + 20 <= data.Length && data.Slice(i, 20).SequenceEqual(GamebryoSignature)) return i - offset;
        }

        return -1;
    }
}
