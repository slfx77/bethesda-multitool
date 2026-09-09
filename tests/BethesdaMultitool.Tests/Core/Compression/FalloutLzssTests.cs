using BethesdaMultitool.Core.Compression;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Compression;

/// <summary>
///     Vectors for the Fallout DAT1 block-framed LZSS decoder. Streams are hand-built block by
///     block (16-bit big-endian block length; 0 = terminator, HIGH BIT SET = raw bytes whose count
///     is the low 15 bits, any other positive value = LZSS-coded)
///     <para>
///         ⚠⚠ These vectors USED to encode a raw block as the NEGATED count (0xFFFD for 3 bytes),
///         matching what the decoder then did and what the ported reference's prose says. Retail
///         disagrees: a raw block's length is the LOW 15 BITS (0x8003 for 3). The tests were written
///         from the same wrong premise as the code, so they could not catch it — corrected
///         2026-09-06 after a full-archive A/B: the negated reading fails 206 of MASTER.DAT's
///         entries, the low-15-bit reading fails none of 19,784.
///     </para>
///     and the decoded bytes are pinned independently. Inside a coded
///     block: fresh 4096-byte dictionary filled with 0x20, write cursor at 4078, LSB-first flag
///     bits, references are byte1 = offset low 8, byte2 = offset high nibble | (length - 3).
/// </summary>
public class FalloutLzssTests
{
    [Fact]
    public void Decompress_RawBlockWithTerminator_CopiesVerbatim()
    {
        // Block length 0x8003 -> 3 raw bytes "XYZ", then the 00 00 terminator.
        byte[] input = [0x80, 0x03, 0x58, 0x59, 0x5A, 0x00, 0x00];

        var result = FalloutLzss.Decompress(input, 3);

        Assert.Equal("XYZ"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_CodedBlockSelfOverlappingReference_ExpandsRun()
    {
        // Block length +4 -> 4 coded bytes. Flag 0x01: bit 0 set -> literal 'A'
        // (dictionary[4078] = 'A', write cursor -> 4079). Bit 1 clear -> reference:
        // offset ((0xF2 & 0xF0) << 4) | 0xEE = 0xFEE = 4078, length (0xF2 & 0x0F) + 3 = 5.
        // The copy starts at dictionary[4078] = 'A' and then reads the bytes it just wrote
        // (4079..4082) - the ring-buffer self-overlap - so it emits five more 'A's.
        byte[] input = [0x00, 0x04, 0x01, 0x41, 0xEE, 0xF2, 0x00, 0x00];

        var result = FalloutLzss.Decompress(input, 6);

        Assert.Equal("AAAAAA"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_ReferenceIntoUntouchedDictionary_YieldsPrefillBytes()
    {
        // Block length +3: flag 0x00 -> reference with offset 0, length 3, reading the 0x20
        // dictionary prefill. Then the terminator.
        byte[] input = [0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00];

        var result = FalloutLzss.Decompress(input, 3);

        Assert.Equal([0x20, 0x20, 0x20], result);
    }

    [Fact]
    public void Decompress_RawThenCodedBlock_ConcatenatesAndResetsDictionary()
    {
        // Raw block (-2 -> "XY") followed by the same coded block as the self-overlap vector.
        // The coded block decodes to the identical "AAAAAA" because each coded block gets a
        // fresh dictionary - the raw block contributes nothing to it. Trailing garbage after
        // the 00 00 terminator must be ignored.
        byte[] input =
        [
            0x80, 0x02, 0x58, 0x59, //             raw block: "XY"
            0x00, 0x04, 0x01, 0x41, 0xEE, 0xF2, // coded block: "AAAAAA"
            0x00, 0x00, //                         terminator
            0xDE, 0xAD //                         trailing bytes, ignored
        ];

        var result = FalloutLzss.Decompress(input, 8);

        Assert.Equal("XYAAAAAA"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_NoTerminator_EndsAtInputEnd()
    {
        // A stream may end by exhausting the input instead of via the 00 00 terminator.
        byte[] input = [0x80, 0x02, 0x58, 0x59];

        var result = FalloutLzss.Decompress(input, 2);

        Assert.Equal("XY"u8.ToArray(), result);
    }

    [Theory]
    // A lone byte where a 2-byte block length is expected.
    [InlineData(new byte[] { 0xFF }, 1)]
    // Coded block of 2: flag 0x00 demands a 2-byte reference but only 1 byte remains in the block.
    [InlineData(new byte[] { 0x00, 0x02, 0x00, 0x11 }, 3)]
    // Coded block length 10 extends past the end of the input.
    [InlineData(new byte[] { 0x00, 0x0A, 0x01, 0x41 }, 6)]
    // Stream produces 3 bytes but the directory declared 5.
    [InlineData(new byte[] { 0x80, 0x03, 0x58, 0x59, 0x5A, 0x00, 0x00 }, 5)]
    // Stream produces 3 bytes but the directory declared 2 (raw block overruns).
    [InlineData(new byte[] { 0x80, 0x03, 0x58, 0x59, 0x5A, 0x00, 0x00 }, 2)]
    public void Decompress_MalformedStream_Throws(byte[] input, int unpackedLength)
    {
        Assert.Throws<InvalidDataException>(() => FalloutLzss.Decompress(input, unpackedLength));
    }

    [Fact]
    public void Decompress_ReadsARawBlockLengthFromTheLowFifteenBitsNotTheNegatedWord()
    {
        // THE regression this file exists to prevent. TEXT/SPANISH/DIALOG/SET.MSG ends with a
        // header of 0x800B and exactly 11 bytes: 16,384 already produced + 11 == the declared
        // 16,395. Reading the word as -n gives 32,757 and throws.
        // Proven by a full-archive A/B with the SHIPPED extractor: the negated reading fails 206 of
        // MASTER.DAT's entries (19,578 of 19,784 extracted); the low-15-bit reading fails NONE.
        // Bytes spelled out rather than written as a string literal - the tail is "ecesita.}" plus
        // CR LF, and escaping it through a shell heredoc is how this file got a stray newline once.
        byte[] tail = [0x65, 0x63, 0x65, 0x73, 0x69, 0x74, 0x61, 0x2E, 0x7D, 0x0D, 0x0A];
        byte[] input = [0x80, 0x0B, .. tail, 0x00, 0x00];

        Assert.Equal(tail, FalloutLzss.Decompress(input, tail.Length));
    }
}