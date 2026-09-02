using System.Collections.Generic;
using System.Linq;
using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Vectors for <see cref="ArenaCfaDecoder" />.
///     <para>
///         The bit-depth cases are the important ones. The reference implements seven separate
///         "demux" routines, one per depth; this port uses a single MSB-first bit reader on the
///         grounds that all seven are the same operation with byte-aligned groups. Each expected
///         array below was computed by hand from the reference's own bit masks for the SAME input
///         bytes, so these tests are the proof of that equivalence rather than a restatement of
///         the port's behaviour.
///     </para>
/// </summary>
public class ArenaCfaDecoderTests
{
    /// <summary>RLE-encodes as literal packets: a control byte of n-1 (bit 7 clear), then n bytes.</summary>
    private static IEnumerable<byte> RleLiterals(IReadOnlyList<byte> data)
    {
        for (var offset = 0; offset < data.Count; offset += 128)
        {
            var run = Math.Min(128, data.Count - offset);
            yield return (byte)(run - 1);
            for (var i = 0; i < run; i++)
            {
                yield return data[offset + i];
            }
        }
    }

    /// <summary>
    ///     Builds a one-frame .CFA. The lookup table is the identity, so a decoded pixel equals the
    ///     unpacked bit value and the tests read as bit-unpacking assertions.
    /// </summary>
    private static byte[] BuildCfa(
        int widthUncompressed,
        int height,
        int bitsPerPixel,
        IReadOnlyList<byte> packedRows,
        int frameCount = 1,
        byte[]? lookup = null)
    {
        var widthCompressed = packedRows.Count / (height * frameCount);
        lookup ??= [.. Enumerable.Range(0, 1 << bitsPerPixel).Select(i => (byte)i)];

        var headerSize = 76 + lookup.Length;
        var file = new List<byte>(new byte[headerSize]);

        void WriteU16(int offset, int value)
        {
            file[offset] = (byte)(value & 0xFF);
            file[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        WriteU16(0, widthUncompressed);
        WriteU16(2, height);
        WriteU16(4, widthCompressed);
        WriteU16(6, 12); // xOffset
        WriteU16(8, 34); // yOffset
        file[10] = (byte)bitsPerPixel;
        file[11] = (byte)frameCount;
        WriteU16(12, headerSize);

        for (var i = 0; i < lookup.Length; i++)
        {
            file[76 + i] = lookup[i];
        }

        file.AddRange(RleLiterals(packedRows));
        return [.. file];
    }

    private static byte[] DecodeSingleRow(int bitsPerPixel, byte[] packed, int pixelCount)
    {
        var frames = ArenaCfaDecoder.Decode(BuildCfa(pixelCount, 1, bitsPerPixel, packed), "T.CFA");
        return Assert.Single(frames).Indices;
    }

    [Fact]
    public void Decode_OneBitPerPixel_MatchesTheReferenceDemux()
    {
        // 0xAB = 1010 1011.
        Assert.Equal([1, 0, 1, 0, 1, 0, 1, 1], DecodeSingleRow(1, [0xAB], 8));
    }

    [Fact]
    public void Decode_TwoBitsPerPixel_MatchesTheReferenceDemux()
    {
        // 0xAB = 10 10 10 11.
        Assert.Equal([2, 2, 2, 3], DecodeSingleRow(2, [0xAB], 4));
    }

    [Fact]
    public void Decode_ThreeBitsPerPixel_MatchesTheReferenceDemux()
    {
        // demux3 over AB CD EF: 5,2,7,4,6,7,5,7 (spans byte boundaries twice).
        Assert.Equal([5, 2, 7, 4, 6, 7, 5, 7], DecodeSingleRow(3, [0xAB, 0xCD, 0xEF], 8));
    }

    [Fact]
    public void Decode_FourBitsPerPixel_MatchesTheReferenceDemux()
    {
        Assert.Equal([10, 11, 12, 13], DecodeSingleRow(4, [0xAB, 0xCD], 4));
    }

    [Fact]
    public void Decode_FiveBitsPerPixel_MatchesTheReferenceDemux()
    {
        // demux5 over AB CD EF 12 34.
        Assert.Equal(
            [21, 15, 6, 30, 30, 4, 17, 20],
            DecodeSingleRow(5, [0xAB, 0xCD, 0xEF, 0x12, 0x34], 8));
    }

    [Fact]
    public void Decode_SixBitsPerPixel_MatchesTheReferenceDemux()
    {
        // demux6 over AB CD EF — the depth ZOMBIE4.CFA actually uses.
        Assert.Equal([42, 60, 55, 47], DecodeSingleRow(6, [0xAB, 0xCD, 0xEF], 4));
    }

    [Fact]
    public void Decode_SevenBitsPerPixel_MatchesTheReferenceDemux()
    {
        Assert.Equal(
            [85, 115, 61, 113, 17, 81, 44, 120],
            DecodeSingleRow(7, [0xAB, 0xCD, 0xEF, 0x12, 0x34, 0x56, 0x78], 8));
    }

    [Fact]
    public void Decode_EightBitsPerPixel_CopiesBytesWithoutTheLookupTable()
    {
        // At full depth the reference applies neither demuxing nor the lookup table, so an
        // inverting table must have no effect.
        var inverting = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            inverting[i] = (byte)(255 - i);
        }

        var frames = ArenaCfaDecoder.Decode(
            BuildCfa(4, 1, 8, [1, 2, 3, 4], lookup: inverting), "T.CFA");

        Assert.Equal([1, 2, 3, 4], Assert.Single(frames).Indices);
    }

    [Fact]
    public void Decode_AppliesTheLookupTableToUnpackedValues()
    {
        // 4bpp values 10, 11, 12, 13 through a table that maps i -> 200 + i.
        var lookup = new byte[16];
        for (var i = 0; i < lookup.Length; i++)
        {
            lookup[i] = (byte)(200 + i);
        }

        var frames = ArenaCfaDecoder.Decode(
            BuildCfa(4, 1, 4, [0xAB, 0xCD], lookup: lookup), "T.CFA");

        Assert.Equal([210, 211, 212, 213], Assert.Single(frames).Indices);
    }

    [Fact]
    public void Decode_DiscardsBitPaddingBeyondTheUncompressedWidth()
    {
        // 3 pixels at 3bpp needs 9 bits, so the byte pair carries 7 bits of padding that must not
        // become a fourth pixel.
        var frames = ArenaCfaDecoder.Decode(BuildCfa(3, 1, 3, [0xAB, 0xCD]), "T.CFA");

        Assert.Equal([5, 2, 7], Assert.Single(frames).Indices);
    }

    [Fact]
    public void Decode_MultipleFramesShareGeometryAndComeFromOneRleStream()
    {
        // Two 4-pixel frames at 4bpp: all frames are packed into a single RLE stream.
        var frames = ArenaCfaDecoder.Decode(
            BuildCfa(4, 1, 4, [0xAB, 0xCD, 0x12, 0x34], frameCount: 2), "T.CFA");

        Assert.Equal(2, frames.Count);
        Assert.Equal([10, 11, 12, 13], frames[0].Indices);
        Assert.Equal([1, 2, 3, 4], frames[1].Indices);
        Assert.All(frames, f => Assert.Equal(4, f.Width));
        Assert.All(frames, f => Assert.Equal(12, f.XOffset));
        Assert.All(frames, f => Assert.Equal(34, f.YOffset));
    }

    [Fact]
    public void Decode_MultipleRows_AdvanceByTheCompressedWidth()
    {
        var frames = ArenaCfaDecoder.Decode(BuildCfa(4, 2, 4, [0xAB, 0xCD, 0x12, 0x34]), "T.CFA");

        var image = Assert.Single(frames);
        Assert.Equal(2, image.Height);
        Assert.Equal([10, 11, 12, 13, 1, 2, 3, 4], image.Indices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void Decode_UnsupportedBitDepth_Throws(int bitsPerPixel)
    {
        var file = BuildCfa(4, 1, 4, [0xAB, 0xCD]);
        file[10] = (byte)bitsPerPixel;

        Assert.Throws<InvalidDataException>(() => ArenaCfaDecoder.Decode(file, "BAD.CFA"));
    }

    [Fact]
    public void Decode_TooSmallForTheLookupTable_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaCfaDecoder.Decode(new byte[40], "TINY.CFA"));
    }

    [Fact]
    public void Decode_HeaderSizeOutsideTheFile_Throws()
    {
        var file = BuildCfa(4, 1, 4, [0xAB, 0xCD]);
        file[12] = 0xFF;
        file[13] = 0xFF;

        Assert.Throws<InvalidDataException>(() => ArenaCfaDecoder.Decode(file, "BAD.CFA"));
    }

    [Fact]
    public void Decode_ZeroFrames_Throws()
    {
        var file = BuildCfa(4, 1, 4, [0xAB, 0xCD]);
        file[11] = 0;

        Assert.Throws<InvalidDataException>(() => ArenaCfaDecoder.Decode(file, "BAD.CFA"));
    }
}
