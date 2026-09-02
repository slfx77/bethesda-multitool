using BethesdaMultitool.Core.Compression;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Compression;

/// <summary>
///     Hand-derived vectors for <see cref="LzssCodec.DecompressBattlespire" />, the per-entry BSA
///     codec clean-roomed from battlespire-tools' <c>bsa_format.txt</c>. Every expected output is
///     worked out on paper from the spec's rules, and the vectors deliberately exercise the three
///     ways this variant differs from Arena's: the byte-swapped code pair, the split window
///     prefill (0x20 then 0x00 for the last 18), and the input-driven stop.
/// </summary>
public class LzssCodecBattlespireTests
{
    [Fact]
    public void Decompress_AllLiteralFlag_CopiesBytesThrough()
    {
        // Flag 0xFF = eight literal bits, LSB first.
        byte[] input = [0xFF, (byte)'A', (byte)'B', (byte)'C', (byte)'D', (byte)'E', (byte)'F', (byte)'G', (byte)'H'];

        Assert.Equal("ABCDEFGH"u8.ToArray(), LzssCodec.DecompressBattlespire(input));
    }

    [Fact]
    public void Decompress_OverlappingCode_ExpandsARun()
    {
        // One literal 'A' lands at window position 4078 (the write origin). The code then reads
        // three bytes from absolute offset 4078 — overlapping its own output, so 'A' repeats.
        // Code bytes: first = lengthNibble<<4 | offsetHigh = 0x0F, second = offset low = 0xEE.
        byte[] input = [0x01, (byte)'A', 0x0F, 0xEE];

        Assert.Equal("AAAA"u8.ToArray(), LzssCodec.DecompressBattlespire(input));
    }

    [Fact]
    public void Decompress_CodeIntoTheSpacePrefill_YieldsSpaces()
    {
        // Offset 0 sits in the 0x20-filled region of the untouched window.
        byte[] input = [0x00, 0x00, 0x00];

        Assert.Equal([0x20, 0x20, 0x20], LzssCodec.DecompressBattlespire(input));
    }

    [Fact]
    public void Decompress_CodeIntoTheZeroedTail_YieldsZeroes()
    {
        // Offset 4090 sits in the final 18 bytes, which this variant zeroes instead of spacing —
        // the Arena codec would produce 0x20 here, which is exactly why the two must not merge.
        byte[] input = [0x00, 0x0F, 0xFA];

        Assert.Equal([0x00, 0x00, 0x00], LzssCodec.DecompressBattlespire(input));
    }

    [Fact]
    public void Decompress_CodePastTheWindowEnd_WrapsToTheStart()
    {
        // Offset 4095 is the last (zeroed) cell; the next two reads wrap to 0 and 1, which are
        // still space-prefilled.
        byte[] input = [0x00, 0x0F, 0xFF];

        Assert.Equal([0x00, 0x20, 0x20], LzssCodec.DecompressBattlespire(input));
    }

    [Fact]
    public void Decompress_LengthNibble_AddsThree()
    {
        // Length nibble 0xC -> 15 bytes, all from the space prefill.
        byte[] input = [0x00, 0xC0, 0x00];

        var result = LzssCodec.DecompressBattlespire(input);

        Assert.Equal(15, result.Length);
        Assert.All(result, b => Assert.Equal(0x20, b));
    }

    [Fact]
    public void Decompress_ExhaustedInput_StopsWithoutError()
    {
        // The archive stores no decompressed size, so a clear flag bit with no code behind it is
        // the ordinary end of stream, not corruption.
        Assert.Equal("A"u8.ToArray(), LzssCodec.DecompressBattlespire([0x01, (byte)'A']));
        Assert.Empty(LzssCodec.DecompressBattlespire([0x00]));
        Assert.Empty(LzssCodec.DecompressBattlespire([]));
    }

    [Fact]
    public void Decompress_IsNotBitCompatibleWithTheArenaVariant()
    {
        // The same three bytes read as a code by both variants: Battlespire takes the FIRST byte
        // as length+offset-high and reads offset 0 (spaces); Arena takes the SECOND byte that way
        // and biases the offset by +18. Identical input, different output — the proof the two
        // codecs cannot be merged.
        byte[] input = [0x00, 0x00, 0x00];

        var battlespire = LzssCodec.DecompressBattlespire(input);
        var arena = LzssCodec.Decompress(input, 3);

        Assert.Equal([0x20, 0x20, 0x20], battlespire);
        Assert.Equal([0x20, 0x20, 0x20], arena);

        // Where they diverge: length nibble placement. 0xC0 0x00 is 15 bytes under Battlespire
        // (length in the first byte) but 3 bytes under Arena (length in the second byte's low
        // nibble, which is 0 here).
        Assert.Equal(15, LzssCodec.DecompressBattlespire([0x00, 0xC0, 0x00]).Length);
        Assert.Equal(3, LzssCodec.Decompress([0x00, 0xC0, 0x00], 3).Length);
    }
}
