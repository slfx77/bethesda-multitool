using System;
using BethesdaMultitool.Core.Compression;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Compression;

/// <summary>
///     Vectors for the 16-bit-word RLE variant (<see cref="RleCodec.DecompressWords" />) that Arena
///     <c>.RMD</c> wilderness chunks use. Expected bytes are written out by hand from the packet
///     rules, not produced by the decoder.
/// </summary>
public class RleCodecWordTests
{
    [Fact]
    public void DecompressWords_PositiveHeader_CopiesLiteralWords()
    {
        // +3 → three literal words follow: 0x0201, 0x0403, 0x0605.
        byte[] input = [0x03, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

        var result = RleCodec.DecompressWords(input, 3);

        Assert.Equal([0x01, 0x02, 0x03, 0x04, 0x05, 0x06], result);
    }

    [Fact]
    public void DecompressWords_NegativeHeader_RepeatsOneWord()
    {
        // -4 (0xFFFC) → the single word 0xBEEF repeats four times.
        byte[] input = [0xFC, 0xFF, 0xEF, 0xBE];

        var result = RleCodec.DecompressWords(input, 4);

        Assert.Equal([0xEF, 0xBE, 0xEF, 0xBE, 0xEF, 0xBE, 0xEF, 0xBE], result);
    }

    [Fact]
    public void DecompressWords_MixedPackets_ConcatenateInOrder()
    {
        byte[] input =
        [
            0x01, 0x00, 0x11, 0x22, // +1 literal: 0x2211
            0xFE, 0xFF, 0x33, 0x44, // -2 repeat:  0x4433 twice
            0x01, 0x00, 0x55, 0x66  // +1 literal: 0x6655
        ];

        var result = RleCodec.DecompressWords(input, 4);

        Assert.Equal([0x11, 0x22, 0x33, 0x44, 0x33, 0x44, 0x55, 0x66], result);
    }

    [Fact]
    public void DecompressWords_StopsAtTheRequestedCount_IgnoringTrailingInput()
    {
        byte[] input = [0x02, 0x00, 0xAA, 0xAA, 0xBB, 0xBB, 0x7F, 0x7F, 0x7F, 0x7F];

        var result = RleCodec.DecompressWords(input, 2);

        Assert.Equal([0xAA, 0xAA, 0xBB, 0xBB], result);
    }

    [Fact]
    public void DecompressWords_ZeroWordCount_ProducesNothing()
    {
        Assert.Empty(RleCodec.DecompressWords([0x01, 0x00, 0xAA, 0xBB], 0));
    }

    [Fact]
    public void DecompressWords_ZeroHeader_Throws()
    {
        // A 0 header would consume two bytes and emit nothing — an infinite loop if allowed.
        Assert.Throws<InvalidDataException>(() => RleCodec.DecompressWords([0x00, 0x00, 0x00, 0x00], 4));
    }

    [Fact]
    public void DecompressWords_PacketOverrunningTheOutput_Throws()
    {
        Assert.Throws<InvalidDataException>(() => RleCodec.DecompressWords([0xF0, 0xFF, 0xAA, 0xBB], 2));
    }

    [Fact]
    public void DecompressWords_TruncatedStream_Throws()
    {
        Assert.Throws<InvalidDataException>(() => RleCodec.DecompressWords([0x04, 0x00, 0xAA, 0xBB], 4));
    }

    [Fact]
    public void DecompressWords_NegativeWordCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RleCodec.DecompressWords([0x01, 0x00], -1));
    }
}
