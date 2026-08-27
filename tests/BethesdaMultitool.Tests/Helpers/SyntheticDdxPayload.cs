namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Builds well-formed synthetic <c>.ddx</c> bytes — a 0x44 header plus XMemCompress/LZX
///     chunk framing — so <c>DdxFormat</c>'s framing walk can be exercised without real game
///     assets.
/// </summary>
/// <remarks>
///     The chunk-writing here is a deliberate duplicate of DDXConv's proven
///     <c>DDXConv.Tests/Support/SyntheticDdx.cs</c> writer (<c>Build3XdoStreams</c>,
///     <c>WriteLzxStream</c>, <c>WriteLzxUncompressedChunk</c>), which is <c>internal</c> to
///     <c>DDXConv.Tests</c> — an assembly this test project does not (and should not) reference,
///     and a git submodule that must not be edited. The header differs: DDXConv's builder only
///     populates what its own parser reads, whereas <c>DdxFormat</c> additionally validates the
///     flags byte at 0x24 and reads the size/first-stream-length dwords at 0x3C/0x40.
///     <para>
///         Framing recap (source of truth: <c>DDXConv/Compression/LzxDecompressor.cs:33-35</c>):
///         a continuation chunk is a BE16 compressed size spanning <c>size + 2</c>; the stream's
///         final chunk leads with 0xFF then BE16 uncompressed and BE16 compressed sizes, spanning
///         <c>compressed + 10</c>.
///     </para>
/// </remarks>
internal static class SyntheticDdxPayload
{
    internal const int HeaderSize = 0x44;

    /// <summary>Uncompressed bytes carried by one chunk (DefaultUncompressedChunkSize).</summary>
    internal const int ChunkPayloadMax = 0x8000;

    /// <summary>
    ///     A 0x44-byte DDX header carrying everything <c>DdxFormat</c> validates or reads:
    ///     magic, version @0x07 (LE), the >= 0x80 flags byte @0x24, the GPU format byte @0x2B,
    ///     the packed size dword @0x2C (BE), and the two dwords the format doc used to call
    ///     padding — total uncompressed size @0x3C and first-stream length @0x40 (both BE).
    /// </summary>
    internal static byte[] BuildHeader(
        string magic,
        int width,
        int height,
        byte gpuFormat = 0x52,
        ushort version = 4,
        uint uncompressedSize = 0,
        uint firstStreamLength = 0)
    {
        var header = new byte[HeaderSize];
        header[0] = (byte)magic[0];
        header[1] = (byte)magic[1];
        header[2] = (byte)magic[2];
        header[3] = (byte)magic[3];

        header[0x07] = (byte)(version & 0xFF);
        header[0x08] = (byte)((version >> 8) & 0xFF);

        // Fetch-constant dword 0 @0x24: DdxFormat requires the top byte >= 0x80.
        header[0x24] = 0x80;

        // Fetch-constant dword 1 @0x28 (BE); the GPU format is its low byte.
        header[0x2B] = gpuFormat;

        // Fetch-constant dword 2 @0x2C (BE): bits 0-12 width-1, bits 13-25 height-1.
        var sizeDword = (uint)((width - 1) & 0x1FFF) | ((uint)((height - 1) & 0x1FFF) << 13);
        WriteBigEndian(header, 0x2C, sizeDword);

        WriteBigEndian(header, 0x3C, uncompressedSize);
        WriteBigEndian(header, 0x40, firstStreamLength);
        return header;
    }

    /// <summary>Header followed by the supplied already-framed stream bytes.</summary>
    internal static byte[] Concat(byte[] header, params byte[][] parts)
    {
        var total = header.Length + parts.Sum(p => p.Length);
        var result = new byte[total];
        header.CopyTo(result, 0);
        var offset = header.Length;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    /// <summary>
    ///     One complete XMemCompress stream: continuation chunks of exactly 32 KB followed by a
    ///     0xFF-terminated final chunk.
    /// </summary>
    internal static byte[] FrameStream(ReadOnlySpan<byte> payload)
    {
        using var ms = new MemoryStream();
        var offset = 0;
        while (true)
        {
            var remaining = payload.Length - offset;
            var n = Math.Min(ChunkPayloadMax, remaining);
            var last = remaining <= ChunkPayloadMax;
            WriteChunk(ms, payload.Slice(offset, n), last);
            offset += n;
            if (last)
            {
                break;
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    ///     A chunk whose framing header declares <paramref name="compressedSize" /> directly, for
    ///     probing the framing guards (zero size, over-cap size) without hand-rolling bytes in the
    ///     test.
    /// </summary>
    internal static byte[] FrameRawContinuationChunk(int compressedSize, int actualContentBytes)
    {
        var chunk = new byte[2 + actualContentBytes];
        chunk[0] = (byte)(compressedSize >> 8);
        chunk[1] = (byte)compressedSize;
        return chunk;
    }

    /// <summary>
    ///     One chunk carrying <paramref name="payload" />. Content is the 4-byte bitstream seed,
    ///     the three repeat offsets, the payload, and the 4-byte terminator pad — 20 bytes of
    ///     overhead, exactly as DDXConv's writer produces.
    /// </summary>
    private static void WriteChunk(Stream stream, ReadOnlySpan<byte> payload, bool lastChunkOfStream)
    {
        if (payload.Length > ChunkPayloadMax)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"LZX chunk payload {payload.Length} exceeds the 32 KB chunk granularity");
        }

        if (!lastChunkOfStream && payload.Length != ChunkPayloadMax)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                "only the last chunk of a stream may be shorter than 32 KB");
        }

        var compressed = payload.Length + 20;
        if (lastChunkOfStream)
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

        // Bitstream seed: [blockType=3 (uncompressed), 24-bit block size], packed as two
        // little-endian 16-bit words consumed MSB-first.
        var seed = (3u << 28) | ((uint)payload.Length << 4);
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

        if (lastChunkOfStream)
        {
            // The 0xFF framing spans compressedSize + 10: 5 header + content + 5 trailing.
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
