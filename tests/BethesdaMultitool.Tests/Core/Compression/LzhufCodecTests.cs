using BethesdaMultitool.Core.Compression;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Compression;

/// <summary>
///     Vectors for the Arena type-08 LZHUF decoder. The single-symbol vectors are fully
///     hand-derived from the initial tree state (the derivations are spelled out below); the
///     multi-symbol vectors were produced by an independent inverse implementation (an encoder
///     mirroring the documented tree-update rules) and cross-verified, with the exact
///     compressed bytes AND decoded bytes pinned here so either side drifting fails the test.
///     <para>
///         Initial-tree refresher (Okumura layout): 314 leaves for symbols 0..313 sit at tree
///         slots 0..313 with frequency 1; internal slot i (314..626) pairs slots 2*(i-314) and
///         2*(i-314)+1; slot 626 is the root. A leaf at slot s therefore has parent slot
///         (s >> 1) + 314, and the branch bit taken from that parent is s &amp; 1. Walking a
///         leaf's parent chain to the root and reversing the collected bits yields its code.
///     </para>
/// </summary>
public class LzhufCodecTests
{
    [Fact]
    public void Decompress_SingleLiteral_HandDerivedInitialCode()
    {
        // Symbol 0x00 sits at leaf slot 0. Parent chain (slot -> parent, branch bit = slot & 1):
        //   0 -> 314 (bit 0), 314 -> 471 (bit 0), 471 -> 549 (bit 1), 549 -> 588 (bit 1),
        //   588 -> 608 (bit 0), 608 -> 618 (bit 0), 618 -> 623 (bit 0), 623 -> 625 (bit 1),
        //   625 -> 626 root (bit 1).
        // Reversed (root -> leaf) the code is 1 1 0 0 0 1 1 0 0 (9 bits). Packed MSB-first with
        // 7 zero padding bits: 11000110 0_0000000 = 0xC6 0x00.
        byte[] input = [0xC6, 0x00];

        var result = LzhufCodec.Decompress(input, 1);

        Assert.Equal([0x00], result);
    }

    [Fact]
    public void Decompress_RepeatedLiteral_AdaptiveTreeShortensCode()
    {
        // "AAB". The first 'A' (0x41, leaf slot 65) codes as 111001101 (9 bits, derived the same
        // way as the 0x00 vector: chain 65 -> 346 (bit 1), 346 -> 487 (bit 0), 487 -> 557 (bit 1),
        // 557 -> 592 (bit 1), 592 -> 610 (bit 0), 610 -> 619 (bit 0), 619 -> 623 (bit 1),
        // 623 -> 625 (bit 1), 625 -> 626 root (bit 1); reversed = 111001101). The update step
        // then bumps 'A' up the tree, so the SECOND 'A' codes as only 11000101 (8 bits) - this
        // pins the adaptive re-sort itself. 'B' (0x42) then codes as 111001110 (9 bits).
        // 9 + 8 + 9 = 26 bits: 11100110 11100010 11110011 10_000000 = E6 E2 F3 80.
        byte[] input = [0xE6, 0xE2, 0xF3, 0x80];

        var result = LzhufCodec.Decompress(input, 3);

        Assert.Equal("AAB"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_SelfOverlappingMatch_ExpandsRun()
    {
        // "ABC" as literals (codes 111001101, 111001110, 111001111 - the tree updates after
        // each), then match symbol 259 = length 6 (code 10001111 after three updates) with
        // offset 2: position prefix for high bits 0 is 000 (3 bits), then the low 6 bits 000010.
        // The decoder computes copyPos = historyPos(3) - offset(2) - 1 = 0 and copies 6 bytes:
        // it reads ring[0..2] = "ABC", then ring[3..5] - the bytes this same match just wrote -
        // the classic ring self-overlap. Output: "ABCABCABC".
        // 9+9+9+8+3+6 = 44 bits -> E6 F3 B9 F1 E0 20 (4 zero padding bits).
        byte[] input = [0xE6, 0xF3, 0xB9, 0xF1, 0xE0, 0x20];

        var result = LzhufCodec.Decompress(input, 9);

        Assert.Equal("ABCABCABC"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_MatchIntoUntouchedWindow_HandDerivedYieldsPrefill()
    {
        // A single match, so the whole vector is derivable from the initial state by hand:
        // length 4 -> symbol 256 + (4 - 3) = 257, leaf slot 257. Parent chain:
        //   257 -> 442 (bit 1), 442 -> 535 (bit 0), 535 -> 581 (bit 1), 581 -> 604 (bit 1),
        //   604 -> 616 (bit 0), 616 -> 622 (bit 0), 622 -> 625 (bit 0), 625 -> 626 (bit 1);
        // reversed = 10001101 (8 bits). Offset 100: high 6 bits = 100 >> 6 = 1, whose table
        // prefix is 0010 (4 bits, d_len 4); low 6 bits = 100 & 0x3F = 36 = 100100.
        // 8 + 4 + 6 = 18 bits: 10001101 00101001 00_000000 = 8D 29 00.
        // Nothing was written to the ring yet, so the match reads the 0x20 prefill.
        byte[] input = [0x8D, 0x29, 0x00];

        var result = LzhufCodec.Decompress(input, 4);

        Assert.Equal([0x20, 0x20, 0x20, 0x20], result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Decompress_TruncatedStream_Throws(int keptBytes)
    {
        // The "ABCABCABC" vector cut short at every possible byte boundary: the decoder must
        // report truncation instead of silently decoding phantom zero bits.
        byte[] full = [0xE6, 0xF3, 0xB9, 0xF1, 0xE0, 0x20];
        var truncated = full.AsSpan(0, keptBytes).ToArray();

        Assert.Throws<InvalidDataException>(() => LzhufCodec.Decompress(truncated, 9));
    }

    [Fact]
    public void Decompress_MatchOverrunsDeclaredOutput_Throws()
    {
        // The "ABCABCABC" vector with only 8 output bytes declared: the final 6-byte match
        // starts at position 3 and would write past the end.
        byte[] input = [0xE6, 0xF3, 0xB9, 0xF1, 0xE0, 0x20];

        Assert.Throws<InvalidDataException>(() => LzhufCodec.Decompress(input, 8));
    }
}
