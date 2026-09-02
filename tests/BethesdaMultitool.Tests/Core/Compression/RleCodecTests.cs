using BethesdaMultitool.Core.Compression;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Compression;

/// <summary>
///     Vectors for the Arena type-02 RLE decoder. Every stream is hand-built packet by packet
///     and the decoded bytes are pinned independently of the implementation.
/// </summary>
public class RleCodecTests
{
    [Fact]
    public void Decompress_LiteralPacket_CopiesVerbatim()
    {
        // Control 0x02: bit 7 clear -> literal packet of (0x02 + 1) = 3 verbatim bytes.
        byte[] input = [0x02, 0x58, 0x59, 0x5A];

        var result = RleCodec.Decompress(input, 3);

        Assert.Equal("XYZ"u8.ToArray(), result);
    }

    [Fact]
    public void Decompress_RunPacket_RepeatsValue()
    {
        // Control 0x84: bit 7 set -> run packet; count = 0x84 - 0x7F = 5 repeats of 0xAB.
        byte[] input = [0x84, 0xAB];

        var result = RleCodec.Decompress(input, 5);

        Assert.Equal([0xAB, 0xAB, 0xAB, 0xAB, 0xAB], result);
    }

    [Fact]
    public void Decompress_MixedPackets_DecodesInSequence()
    {
        byte[] input =
        [
            0x01, 0x11, 0x22, // literal packet: 0x01 + 1 = 2 bytes -> 11 22
            0x80, 0x33, //       run packet: 0x80 - 0x7F = 1 repeat  -> 33
            0x82, 0x44, //       run packet: 0x82 - 0x7F = 3 repeats -> 44 44 44
        ];

        var result = RleCodec.Decompress(input, 6);

        Assert.Equal([0x11, 0x22, 0x33, 0x44, 0x44, 0x44], result);
    }

    [Fact]
    public void Decompress_ZeroLength_ReadsNothing()
    {
        // With no output owed, the stream is never touched.
        var result = RleCodec.Decompress([], 0);

        Assert.Empty(result);
    }

    [Theory]
    // Literal packet declares 6 bytes but only 1 follows.
    [InlineData(new byte[] { 0x05, 0x01 }, 6)]
    // Run of 5 does not fit the declared output of 3.
    [InlineData(new byte[] { 0x84, 0xAB }, 3)]
    // Run packet is missing its value byte.
    [InlineData(new byte[] { 0x84 }, 5)]
    // Output owed but the stream is empty.
    [InlineData(new byte[0], 1)]
    public void Decompress_MalformedStream_Throws(byte[] input, int decompressedLength)
    {
        Assert.Throws<InvalidDataException>(() => RleCodec.Decompress(input, decompressedLength));
    }
}
