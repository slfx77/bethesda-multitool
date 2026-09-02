using BethesdaMultitool.Core.Compression;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Compression;

/// <summary>
///     Vectors for the Arena type-04 LZSS decoder. Streams are hand-built (flag byte, then
///     literals / 12-bit-offset reference pairs) and the decoded bytes are pinned independently.
///     Reminder of the layout: flag bits are consumed LSB-first with no shift before the first
///     bit; a reference pair is byte1 = offset low 8, byte2 = offset high nibble | (length - 3);
///     the ring read position is the 12-bit offset + 18, the write cursor starts at 0, and the
///     4096-byte ring is prefilled with 0x20.
/// </summary>
public class LzssCodecTests
{
    [Fact]
    public void Decompress_LiteralsOnly_CopiesVerbatim()
    {
        // Flag 0x07 = bits 1,1,1 (LSB-first): three literals. The remaining five flag bits are
        // never processed because the input ends first (decoding is input-driven).
        byte[] input = [0x07, 0x41, 0x42, 0x43];

        var result = LzssCodec.Decompress(input, 3);

        Assert.Equal("ABC"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_ReferenceIntoUntouchedWindow_YieldsPrefillBytes()
    {
        // Flag 0x00 -> first item is a reference. byte1 = 0x00, byte2 = 0x00: raw offset 0,
        // length 0 + 3 = 3. Read position = 0 + 18 = 18, and nothing has been written yet,
        // so the copy reads the ring prefill: three 0x20 bytes.
        byte[] input = [0x00, 0x00, 0x00];

        var result = LzssCodec.Decompress(input, 3);

        Assert.Equal([0x20, 0x20, 0x20], result);
    }

    [Fact]
    public void Decompress_SelfOverlappingReference_ExpandsRun()
    {
        // Flag 0x01: bit 0 set -> literal 'A' (ring[0] = 'A', write cursor -> 1).
        // Bit 1 clear -> reference: byte1 = 0xEE, byte2 = 0xF2 -> raw offset
        // ((0xF2 & 0xF0) << 4) | 0xEE = 0xFEE = 4078; read position = 4078 + 18 = 4096, which
        // wraps to ring index 0; length = (0xF2 & 0x0F) + 3 = 5. The copy reads ring[0] = 'A',
        // then reads the bytes the same reference just wrote (indices 1..4) - the classic
        // ring-buffer self-overlap - producing five more 'A's.
        byte[] input = [0x01, 0x41, 0xEE, 0xF2];

        var result = LzssCodec.Decompress(input, 6);

        Assert.Equal("AAAAAA"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_InputEndsEarly_ZeroFillsRemainingOutput()
    {
        // Same three-literal stream as above, but 5 bytes of output are declared. The reference
        // decoder zero-fills whatever the stream did not produce.
        byte[] input = [0x07, 0x41, 0x42, 0x43];

        var result = LzssCodec.Decompress(input, 5);

        Assert.Equal([0x41, 0x42, 0x43, 0x00, 0x00], result);
    }

    [Theory]
    // Flag says literal but the literal byte is missing.
    [InlineData(new byte[] { 0x01 }, 4)]
    // Flag says reference but only one of the two pair bytes is present.
    [InlineData(new byte[] { 0x00, 0x11 }, 4)]
    // The self-overlap vector needs 6 output bytes; declaring 4 makes the copy overrun.
    [InlineData(new byte[] { 0x01, 0x41, 0xEE, 0xF2 }, 4)]
    public void Decompress_MalformedStream_Throws(byte[] input, int decompressedLength)
    {
        Assert.Throws<InvalidDataException>(() => LzssCodec.Decompress(input, decompressedLength));
    }
}
