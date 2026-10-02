using System.Buffers.Binary;
using System.IO.Compression;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     A minimal PNG writer for the Shadowkey images' standard payloads (cut-2 plan decisions D3 and D10): an 8-bit
///     indexed image (color type 3) with its <c>PLTE</c> and an optional <c>tRNS</c>, or an 8-bit truecolor image (color
///     type 2) for a skin with more than 256 colors (none on retail). Rows are written top row first with filter 0 and
///     one zlib stream in one <c>IDAT</c>; no ancillary chunk carries a time or a gamma, so equal pixels give equal
///     bytes.
/// </summary>
/// <remarks>
///     The encoder is deliberately independent of the renderer's PNG path: a standard payload is declared lossless, and
///     an indexed PNG keeps the source's index structure (a skin's first-appearance palette, a <c>.ztx</c> texture's zone
///     palette) exactly, which an RGBA re-encode would not. The writers decode it through their own PNG readers.
/// </remarks>
internal static class ShadowkeyIndexedPng
{
    private const byte IndexedColorType = 3;
    private const byte TruecolorColorType = 2;
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>
    ///     Encodes an 8-bit indexed image: <paramref name="indices" /> is <c>width x height</c> palette indices, top row
    ///     first; <paramref name="palette" /> holds 1 to 256 RGB triplets (3 bytes each); <paramref name="alpha" />, when
    ///     given, is the <c>tRNS</c> alpha of the first entries (the rest stay opaque).
    /// </summary>
    /// <exception cref="ArgumentException">The dimensions, palette or indices are inconsistent.</exception>
    public static byte[] EncodeIndexed(int width, int height, ReadOnlySpan<byte> indices, ReadOnlySpan<byte> palette,
        ReadOnlySpan<byte> alpha = default)
    {
        RequireDimensions(width, height, indices.Length, 1);
        if (palette.Length is 0 or > 768 || palette.Length % 3 != 0)
        {
            throw new ArgumentException("A PNG palette holds 1 to 256 RGB triplets.", nameof(palette));
        }

        var entries = palette.Length / 3;
        if (alpha.Length > entries)
        {
            throw new ArgumentException("A tRNS chunk cannot name more entries than the palette holds.", nameof(alpha));
        }

        foreach (var index in indices)
        {
            if (index >= entries)
            {
                throw new ArgumentException("An index names an entry past the palette.", nameof(indices));
            }
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR"u8, Header(width, height, IndexedColorType));
        WriteChunk(output, "PLTE"u8, palette);
        if (alpha.Length > 0)
        {
            WriteChunk(output, "tRNS"u8, alpha);
        }

        WriteChunk(output, "IDAT"u8, Compress(width, height, indices, 1));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>Encodes an 8-bit RGB truecolor image: <paramref name="rgb" /> is <c>width x height x 3</c> bytes, top row first.</summary>
    /// <exception cref="ArgumentException">The dimensions and the pixel bytes are inconsistent.</exception>
    public static byte[] EncodeRgb(int width, int height, ReadOnlySpan<byte> rgb)
    {
        RequireDimensions(width, height, rgb.Length, 3);
        using var output = new MemoryStream();
        output.Write(Signature);
        WriteChunk(output, "IHDR"u8, Header(width, height, TruecolorColorType));
        WriteChunk(output, "IDAT"u8, Compress(width, height, rgb, 3));
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    /// <summary>The CRC-32 (ISO 3309, as PNG uses it) of a buffer.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static void RequireDimensions(int width, int height, int length, int bytesPerPixel)
    {
        if (width <= 0 || height <= 0 || (long)width * height * bytesPerPixel != length)
        {
            throw new ArgumentException("The pixel bytes must be exactly width x height samples.", nameof(length));
        }
    }

    private static byte[] Header(int width, int height, byte colorType)
    {
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = colorType;
        return header;
    }

    private static byte[] Compress(int width, int height, ReadOnlySpan<byte> pixels, int bytesPerPixel)
    {
        var stride = width * bytesPerPixel;
        var filtered = new byte[(long)(stride + 1) * height];
        for (var row = 0; row < height; row++)
        {
            filtered[(long)row * (stride + 1)] = 0;
            pixels.Slice(row * stride, stride).CopyTo(filtered.AsSpan(row * (stride + 1) + 1, stride));
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(filtered);
        }

        return compressed.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        output.Write(word);
        var crcInput = new byte[4 + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsSpan(4));
        output.Write(crcInput);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(crcInput));
        output.Write(word);
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
