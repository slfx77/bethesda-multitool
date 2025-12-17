namespace Xbox360MemoryCarver.Compression;

/// <summary>
/// Static helper class for Xbox 360 XMemCompress/XMemDecompress operations.
/// Provides a managed 64-bit compatible implementation using LzxDecoder.
/// 
/// Xbox 360 XMemCompress uses LZX compression with:
/// - Default window size: 128KB (17 bits)  
/// - Default chunk size: 512KB (output frame size)
/// - Framing format similar to XNB but with different sizes
/// </summary>
public static class XCompression
{
    // Xbox 360 XMemCompress defaults
    private const int DefaultWindowBits = 17;  // 128KB window
    private const int DefaultChunkSize = 512 * 1024;  // 512KB output frames

    /// <summary>
    /// Decompress Xbox 360 XMemCompress data.
    /// </summary>
    public static byte[]? Decompress(byte[] compressedData, int uncompressedSize)
    {
        return DecompressWithBytesConsumed(compressedData, uncompressedSize, out _);
    }

    /// <summary>
    /// Decompress Xbox 360 XMemCompress data and report bytes consumed.
    /// </summary>
    public static byte[]? DecompressWithBytesConsumed(byte[] compressedData, int uncompressedSize, out int bytesConsumed)
    {
        bytesConsumed = 0;

        if (compressedData == null || compressedData.Length == 0)
            return null;

        if (uncompressedSize <= 0)
            return null;

        // Try raw LZX with 17-bit window (Xbox default)
        var result = TryDecompressRawLzx(compressedData, uncompressedSize, 17, out bytesConsumed);
        if (result != null && result.Length == uncompressedSize)
            return result;

        // Try with framed format (like XNB but with Xbox parameters)
        result = TryDecompressFramedLzx(compressedData, uncompressedSize, out bytesConsumed);
        if (result != null)
            return result;

        // Try other window sizes
        int[] windowBitsToTry = [16, 15, 18, 19, 20];
        foreach (var bits in windowBitsToTry)
        {
            result = TryDecompressRawLzx(compressedData, uncompressedSize, bits, out bytesConsumed);
            if (result != null && result.Length == uncompressedSize)
                return result;
        }

        return null;
    }

    /// <summary>
    /// Try to decompress using framed LZX format (similar to XNB).
    /// Each frame has a 2-byte big-endian size header.
    /// If first byte is 0xFF, there's an extended header with frame size.
    /// </summary>
    private static byte[]? TryDecompressFramedLzx(byte[] compressedData, int uncompressedSize, out int bytesConsumed)
    {
        bytesConsumed = 0;

        try
        {
            using var input = new MemoryStream(compressedData);
            using var output = new MemoryStream(uncompressedSize);

            var decoder = new LzxDecoder(DefaultWindowBits);
            long startPos = input.Position;

            while (input.Position < input.Length && output.Position < uncompressedSize)
            {
                // Read frame header (big-endian)
                int hi = input.ReadByte();
                int lo = input.ReadByte();
                if (hi < 0 || lo < 0) break;

                int blockSize = (hi << 8) | lo;
                int frameSize = DefaultChunkSize; // Default output frame size

                // Check for extended header
                if (hi == 0xFF)
                {
                    hi = lo;
                    lo = input.ReadByte();
                    if (lo < 0) break;
                    frameSize = (hi << 8) | lo;

                    hi = input.ReadByte();
                    lo = input.ReadByte();
                    if (hi < 0 || lo < 0) break;
                    blockSize = (hi << 8) | lo;
                }

                if (blockSize == 0 || frameSize == 0)
                    break;

                // Limit frame size to remaining output needed
                int outputNeeded = uncompressedSize - (int)output.Position;
                if (frameSize > outputNeeded)
                    frameSize = outputNeeded;

                // Decompress this frame
                long frameStart = input.Position;
                using var frameOutput = new MemoryStream(frameSize);

                int result = decoder.Decompress(input, frameOutput, frameSize);
                if (result != 0)
                    break;

                // Copy to main output
                frameOutput.Position = 0;
                frameOutput.CopyTo(output);

                // Ensure we consumed exactly blockSize bytes
                input.Position = frameStart + blockSize;
            }

            bytesConsumed = (int)input.Position;

            if (output.Position > 0)
                return output.ToArray();
        }
        catch
        {
            // Fall through
        }

        bytesConsumed = 0;
        return null;
    }

    /// <summary>
    /// Try raw LZX decompression (no framing) with specified window size.
    /// </summary>
    private static byte[]? TryDecompressRawLzx(byte[] compressedData, int uncompressedSize, int windowBits, out int bytesConsumed)
    {
        bytesConsumed = 0;

        try
        {
            var decoder = new LzxDecoder(windowBits);

            using var inputStream = new MemoryStream(compressedData);
            using var outputStream = new MemoryStream(uncompressedSize);

            int result = decoder.Decompress(inputStream, outputStream, uncompressedSize);
            bytesConsumed = (int)inputStream.Position;

            if (result != 0)
                return null;

            var output = outputStream.ToArray();
            if (output.Length == 0)
                return null;

            return output;
        }
        catch
        {
            bytesConsumed = 0;
            return null;
        }
    }

    /// <summary>
    /// Check if XCompression is available.
    /// </summary>
    public static bool IsAvailable => true;
}
