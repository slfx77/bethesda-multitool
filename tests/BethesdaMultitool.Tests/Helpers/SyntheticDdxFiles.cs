namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Complete synthetic <c>.ddx</c> files for the NIF reader's DDX gate tests: the BMT-side equivalent of DDXConv's
///     proven <c>DDXConv.Tests/Support/SyntheticDdx.cs</c> (internal to <c>DDXConv.Tests</c>, which this project does
///     not reference; the submodule is not edited). The bytes match that builder exactly for the same inputs (header at
///     0x00-0x43, one XMemCompress stream of uncompressed-block LZX chunks, the intel-E8 bit in the first chunk only, the
///     last chunk 0xFF-framed), so DDXConv behaviors its own tests pin hold here too. Two additions the NIF reader needs
///     and DDXConv's builder leaves zero: the fetch constant's mip-max field (d4 bits 6-9, big-endian dword at 0x34), and
///     the choice of magic and version.
/// </summary>
internal static class SyntheticDdxFiles
{
    /// <summary>DXT1.</summary>
    public const byte Dxt1 = 0x52;

    /// <summary>DXT5.</summary>
    public const byte Dxt5 = 0x54;

    /// <summary>ATI1 (BC4).</summary>
    public const byte Ati1 = 0x7B;

    /// <summary>ATI2 (BC5).</summary>
    public const byte Ati2 = 0x71;

    private const int HeaderSize = 0x44;
    private const int ChunkPayloadMax = 0x8000;

    /// <summary>Builds a complete DDX: header plus <paramref name="payload" /> as one XMemCompress stream.</summary>
    /// <param name="magic"><c>3XDO</c> or <c>3XDR</c>.</param>
    /// <param name="width">Level-zero width.</param>
    /// <param name="height">Level-zero height.</param>
    /// <param name="format">The GPU format byte (written at 0x24 and 0x2B, as DDXConv's builder does).</param>
    /// <param name="payload">The decompressed surface bytes.</param>
    /// <param name="mipMax">The fetch constant's mip-max field; the declared mip count is this plus one.</param>
    /// <param name="version">The DDX version (DDXConv requires at least 3).</param>
    public static byte[] Build(string magic, int width, int height, byte format, byte[] payload, int mipMax = 0,
        ushort version = 3)
    {
        using var stream = new MemoryStream();
        var header = new byte[HeaderSize];
        header[0] = (byte)magic[0];
        header[1] = (byte)magic[1];
        header[2] = (byte)magic[2];
        header[3] = (byte)magic[3];
        header[0x07] = (byte)version;
        header[0x08] = (byte)(version >> 8);
        header[0x24] = format;
        header[0x2B] = format;
        var size = (uint)((width - 1) & 0x1FFF) | ((uint)((height - 1) & 0x1FFF) << 13);
        WriteBigEndian(header, 0x2C, size);
        WriteBigEndian(header, 0x34, (uint)(mipMax & 0xF) << 6);
        stream.Write(header);
        WriteStream(stream, payload);
        return stream.ToArray();
    }

    /// <summary>
    ///     A deterministic non-zero payload whose every block is stamped with its index, as DDXConv's
    ///     <c>IndexStampedBlocks</c> writes it.
    /// </summary>
    public static byte[] IndexStampedBlocks(int blockCount, int blockSize)
    {
        var data = new byte[blockCount * blockSize];
        for (var i = 0; i < blockCount; i++)
        {
            for (var b = 0; b < blockSize; b += 2)
            {
                data[i * blockSize + b] = (byte)i;
                data[i * blockSize + b + 1] = (byte)(i >> 8);
            }
        }

        return data;
    }

    private static void WriteStream(Stream stream, ReadOnlySpan<byte> payload)
    {
        var offset = 0;
        var first = true;
        while (true)
        {
            var remaining = payload.Length - offset;
            var count = Math.Min(ChunkPayloadMax, remaining);
            var last = remaining <= ChunkPayloadMax;
            WriteChunk(stream, payload.Slice(offset, count), first, last);
            offset += count;
            first = false;
            if (last)
            {
                break;
            }
        }
    }

    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> payload, bool first, bool last)
    {
        var compressed = payload.Length + 20;
        if (last)
        {
            stream.WriteByte(0xFF);
            stream.WriteByte((byte)(payload.Length >> 8));
            stream.WriteByte((byte)payload.Length);
            stream.WriteByte((byte)(compressed >> 8));
            stream.WriteByte((byte)compressed);
        }
        else
        {
            stream.WriteByte((byte)(compressed >> 8));
            stream.WriteByte((byte)compressed);
        }

        var seed = first
            ? (3u << 28) | ((uint)payload.Length << 4)
            : (3u << 29) | ((uint)payload.Length << 5);
        var word0 = (ushort)(seed >> 16);
        var word1 = (ushort)seed;
        stream.WriteByte((byte)word0);
        stream.WriteByte((byte)(word0 >> 8));
        stream.WriteByte((byte)word1);
        stream.WriteByte((byte)(word1 >> 8));

        Span<byte> repeatOffsets = [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0];
        stream.Write(repeatOffsets);
        stream.Write(payload);
        Span<byte> terminatorPad = [0, 0, 0, 0];
        stream.Write(terminatorPad);
        if (last)
        {
            Span<byte> streamTail = [0, 0, 0, 0, 0];
            stream.Write(streamTail);
        }
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
