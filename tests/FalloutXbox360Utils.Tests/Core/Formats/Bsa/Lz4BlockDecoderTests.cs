using System.Text;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Bsa;

/// <summary>
///     Regression coverage for the inline LZ4 frame decoder used by Skyrim SE (v105) BSAs.
///     The decisive case is a <b>block-linked</b> match: a sequence in block N that copies from
///     output produced by block N-1. Vanilla SSE splits any texture larger than the frame's block
///     size into multiple linked blocks, so before the fix ~5% of textures (every 2048²/4096² mip
///     chain) threw "LZ4 match offset out of range" and rendered as missing.
/// </summary>
public class Lz4BlockDecoderTests
{
    // FLG=0x40 (version 01, block-linked, no checksums/content-size/dict), BD=0x70, HC=0x00 (skipped).
    private static readonly byte[] FrameHeader = [0x04, 0x22, 0x4D, 0x18, 0x40, 0x70, 0x00];

    [Fact]
    public void DecodeFrame_MatchReferencesPreviousBlock_DecodesAcrossBlockBoundary()
    {
        // Block 1: 8 literals "ABCDEFGH".
        byte[] block1 = [0x80, .. "ABCDEFGH"u8];
        // Block 2: 0 literals, a match of length 16 (nibble 12 + 4) at offset 8 — i.e. reaching back
        // to the very start of block 1's output. This is impossible to satisfy without a shared,
        // cross-block output window.
        byte[] block2 = [0x0C, 0x08, 0x00];

        var frame = BuildFrame(block1, block2);
        var result = Lz4BlockDecoder.DecodeFrame(frame, decompressedSize: 24);

        Assert.Equal("ABCDEFGHABCDEFGHABCDEFGH", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void DecodeFrame_SingleBlockWithinBlockMatch_Roundtrips()
    {
        // Common case, must still work: 4 literals "ABCD" then a match (offset 4, len 4) → "ABCDABCD".
        byte[] block = [0x40, .. "ABCD"u8, 0x04, 0x00];

        var frame = BuildFrame(block);
        var result = Lz4BlockDecoder.DecodeFrame(frame, decompressedSize: 8);

        Assert.Equal("ABCDABCD", Encoding.ASCII.GetString(result));
    }

    private static byte[] BuildFrame(params byte[][] compressedBlocks)
    {
        var ms = new List<byte>(FrameHeader);
        foreach (var block in compressedBlocks)
        {
            ms.AddRange(BitConverter.GetBytes((uint)block.Length)); // little-endian block size, high bit clear = compressed
            ms.AddRange(block);
        }

        ms.AddRange(BitConverter.GetBytes(0u)); // EndMark
        return ms.ToArray();
    }
}
