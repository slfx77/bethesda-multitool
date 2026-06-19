using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Bsa;

/// <summary>
///     Decoder for the LZ4 compression used by Skyrim Special Edition BSAs (version 105). Each
///     compressed entry is prefixed by the BSA's own 4-byte uncompressed-size field, followed by a
///     complete <b>LZ4 frame</b> (magic <c>04 22 4D 18</c>, a frame descriptor, one or more data
///     blocks, and an end mark). Earlier BSAs (v103/v104) use zlib/deflate instead (see
///     <see cref="BsaExtractor" />).
///     <para>
///         Implemented inline to avoid a third-party dependency. <see cref="DecodeFrame" /> parses
///         the frame envelope and dispatches each block to <see cref="DecodeBlock" />, which decodes
///         the raw LZ4 block format (token = literal/match lengths, 2-byte little-endian match
///         offset, overlapping match copy). Blocks decode into a single shared output buffer at a
///         running absolute position so matches may reference data from <b>earlier</b> blocks —
///         LZ4's default block-linked mode, which Bethesda uses for any texture large enough to
///         span multiple blocks. (Block-independent frames simply emit no cross-block matches, so
///         the same code path decodes them correctly too.)
///     </para>
/// </summary>
internal static class Lz4BlockDecoder
{
    private static readonly byte[] FrameMagic = [0x04, 0x22, 0x4D, 0x18];

    /// <summary>
    ///     Decode a complete LZ4 frame into a buffer of the known decompressed size.
    /// </summary>
    /// <param name="src">The LZ4 frame bytes (starting at the 4-byte frame magic).</param>
    /// <param name="decompressedSize">Exact total size of the decompressed output.</param>
    /// <returns>The decompressed bytes.</returns>
    /// <exception cref="InvalidDataException">Thrown when the frame is malformed.</exception>
    public static byte[] DecodeFrame(ReadOnlySpan<byte> src, int decompressedSize)
    {
        if (decompressedSize < 0)
        {
            throw new InvalidDataException($"Invalid LZ4 decompressed size: {decompressedSize}");
        }

        if (src.Length < 7 || !src[..4].SequenceEqual(FrameMagic))
        {
            throw new InvalidDataException("Not an LZ4 frame (bad magic).");
        }

        var pos = ReadFrameDescriptor(src, out var hasBlockChecksum);

        var dst = new byte[decompressedSize];
        var written = 0;

        while (pos + 4 <= src.Length)
        {
            var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(pos, 4));
            pos += 4;

            if (blockSize == 0)
            {
                break; // EndMark
            }

            var isUncompressed = (blockSize & 0x80000000) != 0;
            var size = (int)(blockSize & 0x7FFFFFFF);
            if (pos + size > src.Length)
            {
                throw new InvalidDataException("LZ4 block extends past frame end.");
            }

            // Decode into the shared buffer at the running absolute position so block-linked
            // matches can reach back into earlier blocks' output.
            written = DecodeOneBlock(src.Slice(pos, size), dst, written, isUncompressed);

            pos += size;
            if (hasBlockChecksum)
            {
                pos += 4;
            }
        }

        return dst;
    }

    /// <summary>Parse the frame descriptor and return the offset of the first data block.</summary>
    private static int ReadFrameDescriptor(ReadOnlySpan<byte> src, out bool hasBlockChecksum)
    {
        var flg = src[4];
        hasBlockChecksum = (flg & 0x10) != 0;

        var pos = 6; // magic(4) + FLG + BD
        if ((flg & 0x08) != 0)
        {
            pos += 8; // Content Size
        }

        if ((flg & 0x01) != 0)
        {
            pos += 4; // Dictionary ID
        }

        return pos + 1; // Header Checksum (HC) byte
    }

    /// <summary>
    ///     Decode one block into the shared output buffer <paramref name="dst" /> starting at the
    ///     absolute position <paramref name="dPos" />. Returns the new absolute position.
    /// </summary>
    private static int DecodeOneBlock(ReadOnlySpan<byte> block, Span<byte> dst, int dPos, bool isUncompressed)
    {
        if (!isUncompressed)
        {
            return DecodeBlock(block, dst, dPos);
        }

        if (dPos + block.Length > dst.Length)
        {
            throw new InvalidDataException("LZ4 uncompressed block exceeds output buffer.");
        }

        block.CopyTo(dst.Slice(dPos));
        return dPos + block.Length;
    }

    /// <summary>
    ///     Decode a single raw LZ4 block into <paramref name="dst" /> starting at absolute position
    ///     <paramref name="dPos" />, consuming all of <paramref name="src" />. Matches are resolved
    ///     against the whole output written so far (including earlier blocks), so this supports
    ///     block-linked frames. Returns the new absolute write position.
    /// </summary>
    public static int DecodeBlock(ReadOnlySpan<byte> src, Span<byte> dst, int dPos = 0)
    {
        var sPos = 0;

        while (sPos < src.Length)
        {
            var token = src[sPos++];

            dPos = CopyLiterals(src, ref sPos, dst, dPos, token >> 4);

            // The last sequence in a block is literals-only with no trailing match.
            if (sPos >= src.Length)
            {
                break;
            }

            dPos = CopyMatch(src, ref sPos, dst, dPos, token & 0x0F);
        }

        return dPos;
    }

    private static int CopyLiterals(ReadOnlySpan<byte> src, ref int sPos, Span<byte> dst, int dPos, int length)
    {
        if (length == 15)
        {
            length += ReadLength(src, ref sPos);
        }

        if (length == 0)
        {
            return dPos;
        }

        if (sPos + length > src.Length || dPos + length > dst.Length)
        {
            throw new InvalidDataException("LZ4 literal run exceeds buffer bounds.");
        }

        src.Slice(sPos, length).CopyTo(dst.Slice(dPos));
        sPos += length;
        return dPos + length;
    }

    private static int CopyMatch(ReadOnlySpan<byte> src, ref int sPos, Span<byte> dst, int dPos, int length)
    {
        if (sPos + 2 > src.Length)
        {
            throw new InvalidDataException("LZ4 match offset truncated.");
        }

        var matchOffset = src[sPos] | (src[sPos + 1] << 8);
        sPos += 2;

        if (length == 15)
        {
            length += ReadLength(src, ref sPos);
        }

        length += 4; // LZ4 minimum match length

        if (matchOffset <= 0 || matchOffset > dPos)
        {
            throw new InvalidDataException($"LZ4 match offset out of range: {matchOffset}.");
        }

        if (dPos + length > dst.Length)
        {
            throw new InvalidDataException("LZ4 match copy exceeds output buffer.");
        }

        // Byte-by-byte copy: matches may overlap (matchOffset < length) to repeat a run.
        var matchPos = dPos - matchOffset;
        for (var i = 0; i < length; i++)
        {
            dst[dPos] = dst[matchPos];
            dPos++;
            matchPos++;
        }

        return dPos;
    }

    private static int ReadLength(ReadOnlySpan<byte> src, ref int pos)
    {
        var length = 0;
        while (pos < src.Length)
        {
            var b = src[pos++];
            length += b;
            if (b != 0xFF)
            {
                break;
            }
        }

        return length;
    }
}
