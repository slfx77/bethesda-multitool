using BethesdaMultitool.Core.Compression;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Compression;

/// <summary>
///     Vectors for <see cref="LzssCodec.DecompressBattlespire" />, the per-entry BSA codec. They
///     exercise the three ways this variant differs from Arena's: the code pair's packing
///     (<c>offset = first | ((second &amp; 0xF0) &lt;&lt; 4)</c>, length in the second byte's low
///     nibble), the split window prefill (0x20, then 0x00 for the last 18 bytes), and the
///     input-driven stop.
///     <para>
///         These vectors were derived from <c>bsa_format.txt</c>'s bit table on 2026-08-31 and had
///         the two code bytes the wrong way round; the retail check of the day only looked at each
///         mesh's <c>v2.7</c> signature, which a literal-only first flag byte decodes correctly
///         either way, so the error survived until a whole record was parsed (2026-09-03). They
///         now follow battlespire-tools' working decoder, and a real-asset test decodes entire
///         archives rather than signatures.
///     </para>
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
        // Code bytes: first = offset low (0xEE), second = offset high nibble (0xF) + length 0.
        byte[] input = [0x01, (byte)'A', 0xEE, 0xF0];

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
        byte[] input = [0x00, 0xFA, 0xF0];

        Assert.Equal([0x00, 0x00, 0x00], LzssCodec.DecompressBattlespire(input));
    }

    [Fact]
    public void Decompress_CodePastTheWindowEnd_WrapsToTheStart()
    {
        // Offset 4095 is the last (zeroed) cell; the next two reads wrap to 0 and 1, which are
        // still space-prefilled.
        byte[] input = [0x00, 0xFF, 0xF0];

        Assert.Equal([0x00, 0x20, 0x20], LzssCodec.DecompressBattlespire(input));
    }

    [Fact]
    public void Decompress_LengthNibble_AddsThree()
    {
        // Length nibble 0xC -> 15 bytes, all from the space prefill.
        byte[] input = [0x00, 0x00, 0x0C];

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
        // A code of all zeroes reads offset 0 under both variants, so both yield spaces.
        byte[] input = [0x00, 0x00, 0x00];

        Assert.Equal([0x20, 0x20, 0x20], LzssCodec.DecompressBattlespire(input));
        Assert.Equal([0x20, 0x20, 0x20], LzssCodec.Decompress(input, 3));

        // Where they diverge: the window's tail. Offset 4090 lands in the final 18 cells, which
        // Battlespire zeroes and Arena leaves as spaces — identical input, different output, which
        // is why the two codecs cannot be merged.
        byte[] tail = [0x00, 0xFA, 0xF0];

        Assert.Equal([0x00, 0x00, 0x00], LzssCodec.DecompressBattlespire(tail));
        Assert.All(LzssCodec.Decompress(tail, 3), b => Assert.Equal(0x20, b));
    }
}