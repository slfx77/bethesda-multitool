namespace Xbox360MemoryCarver.Compression;

/// <summary>
/// Pure managed LZX decompressor using MonoGame-derived LzxDecoder.
/// Replaces the 32-bit XnaNative.dll dependency for Xbox 360 XMemDecompress.
/// </summary>
public static class ManagedLzxDecompressor
{
    // Xbox 360 XMemCompress typically uses 64KB window (window_bits = 16)
    // LzxDecoder supports window_bits 15-21 for regular LZX
    private const int DefaultWindowBits = 16;
    private static readonly int[] OffsetCandidates = [0, 2, 4, 8, 16, 32];

    /// <summary>
    /// Decompress Xbox 360 XMemCompress LZX data.
    /// </summary>
    /// <param name="compressedData">The compressed LZX data.</param>
    /// <param name="uncompressedSize">Expected size of decompressed data.</param>
    /// <returns>Decompressed data, or null if decompression failed.</returns>
    public static byte[]? Decompress(byte[] compressedData, int uncompressedSize)
    {
        return Decompress(compressedData, uncompressedSize, DefaultWindowBits);
    }

    /// <summary>
    /// Decompress Xbox 360 XMemCompress LZX data with custom window size.
    /// </summary>
    /// <param name="compressedData">The compressed LZX data.</param>
    /// <param name="uncompressedSize">Expected size of decompressed data.</param>
    /// <param name="windowBits">Window size in bits (15-21 for regular LZX).</param>
    /// <returns>Decompressed data, or null if decompression failed.</returns>
    public static byte[]? Decompress(byte[] compressedData, int uncompressedSize, int windowBits)
    {
        if (compressedData == null || compressedData.Length == 0)
            return null;

        if (uncompressedSize <= 0)
            return null;

        // Clamp window bits to valid range
        windowBits = Math.Clamp(windowBits, 15, 21);

        try
        {
            var result = TryDecompressWithWindowBitsAndOffsets(compressedData, uncompressedSize, windowBits, out _);
            if (result != null)
                return result;

            // Try alternative window sizes if default fails
            return TryAlternativeWindowSizes(compressedData, uncompressedSize, windowBits);
        }
        catch (Exception)
        {
            // Try alternative approaches
            return TryAlternativeWindowSizes(compressedData, uncompressedSize, windowBits);
        }
    }

    /// <summary>
    /// Decompress LZX data and report how many compressed bytes were consumed.
    /// This allows callers to walk framed/concatenated streams produced by XMemCompress.
    /// </summary>
    public static byte[]? Decompress(byte[] compressedData, int uncompressedSize, out int bytesConsumed)
    {
        return Decompress(compressedData, uncompressedSize, DefaultWindowBits, out bytesConsumed);
    }

    /// <summary>
    /// Decompress LZX data with explicit window size and bytes-consumed reporting.
    /// </summary>
    public static byte[]? Decompress(byte[] compressedData, int uncompressedSize, int windowBits, out int bytesConsumed)
    {
        bytesConsumed = 0;
        if (compressedData == null || compressedData.Length == 0)
            return null;

        if (uncompressedSize <= 0)
            return null;

        windowBits = Math.Clamp(windowBits, 15, 21);

        try
        {
            return TryDecompressWithWindowBitsAndOffsets(compressedData, uncompressedSize, windowBits, out bytesConsumed)
                   ?? TryAlternativeWindowSizesWithConsumed(compressedData, uncompressedSize, windowBits, out bytesConsumed);
        }
        catch (Exception)
        {
            return TryAlternativeWindowSizesWithConsumed(compressedData, uncompressedSize, windowBits, out bytesConsumed);
        }
    }

    /// <summary>
    /// Enable diagnostic output.
    /// </summary>
    public static bool Verbose { get; set; } = false;

    /// <summary>
    /// Try decompression with a specific window size.
    /// </summary>
    private static byte[]? TryDecompressWithWindowBitsAndOffsets(byte[] compressedData, int uncompressedSize, int windowBits, out int bytesConsumed)
    {
        foreach (var offset in OffsetCandidates)
        {
            if (offset >= compressedData.Length)
                break;

            var sliceLength = compressedData.Length - offset;
            var slice = new byte[sliceLength];
            Array.Copy(compressedData, offset, slice, 0, sliceLength);

            var result = TryDecompressWithWindowBits(slice, uncompressedSize, windowBits, out var consumedInner);
            if (result != null)
            {
                bytesConsumed = consumedInner + offset;
                return result;
            }
        }

        bytesConsumed = 0;
        return null;
    }

    private static byte[]? TryDecompressWithWindowBits(byte[] compressedData, int uncompressedSize, int windowBits, out int bytesConsumed)
    {
        bytesConsumed = 0;
        try
        {
            if (Verbose)
                Console.WriteLine($"[LZX] Trying window_bits={windowBits}, input={compressedData.Length}, expected_output={uncompressedSize}");

            // Create the LZX decoder with specified window size
            var decoder = new LzxDecoder(windowBits);

            // Create memory streams for input/output
            using var inputStream = new MemoryStream(compressedData);
            using var outputStream = new MemoryStream(uncompressedSize);

            // Decompress
            var result = decoder.Decompress(inputStream, outputStream, uncompressedSize);
            bytesConsumed = (int)inputStream.Position;

            if (Verbose)
                Console.WriteLine($"[LZX] Decompress result: {result}, output size: {outputStream.Length}");

            if (result != 0)
                return null;

            // Get the decompressed data
            return outputStream.ToArray();
        }
        catch (Exception ex)
        {
            if (Verbose)
                Console.WriteLine($"[LZX] Exception: {ex.Message}");
            bytesConsumed = 0;
            return null;
        }
    }

    /// <summary>
    /// Try decompression with different window sizes.
    /// Xbox 360 games may use different window configurations.
    /// </summary>
    private static byte[]? TryAlternativeWindowSizes(byte[] compressedData, int uncompressedSize, int excludeBits)
    {
        // Try common Xbox 360 window sizes: 64KB (16), 128KB (17), 32KB (15), 256KB (18)
        int[] windowSizes = [16, 17, 15, 18, 19, 20, 21];

        foreach (var bits in windowSizes)
        {
            if (bits == excludeBits)
                continue;

            var result = TryDecompressWithWindowBitsAndOffsets(compressedData, uncompressedSize, bits, out _);
            if (result != null)
                return result;
        }

        return null;
    }

    private static byte[]? TryAlternativeWindowSizesWithConsumed(byte[] compressedData, int uncompressedSize, int excludeBits, out int bytesConsumed)
    {
        bytesConsumed = 0;
        int[] windowSizes = [16, 17, 15, 18, 19, 20, 21];

        foreach (var bits in windowSizes)
        {
            if (bits == excludeBits)
                continue;

            var result = TryDecompressWithWindowBitsAndOffsets(compressedData, uncompressedSize, bits, out bytesConsumed);
            if (result != null)
                return result;
        }

        bytesConsumed = 0;
        return null;
    }

    /// <summary>
    /// Check if decompression is available (always true for managed implementation).
    /// </summary>
    public static bool IsAvailable => true;
}
