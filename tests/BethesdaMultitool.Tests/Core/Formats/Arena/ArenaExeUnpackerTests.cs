using System.Collections.Generic;
using System.Text;
using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Vectors for <see cref="ArenaExeUnpacker" />, the PKLITE decompressor for Arena's A.EXE.
///     <para>
///         There is no compressor to generate fixtures with, so each stream below is assembled by
///         hand from the format rules: a 16-bit control array whose bits are consumed from the
///         least significant end, with literal and back-reference bytes drawn from the byte stream
///         that follows it. The expected output is worked out on paper, so these tests genuinely
///         constrain the decoder rather than restate it.
///     </para>
/// </summary>
public class ArenaExeUnpackerTests
{
    /// <summary>Assembles a packed executable around a hand-built compressed payload.</summary>
    private static byte[] BuildExe(IReadOnlyList<byte> payload, int declaredSize)
    {
        var file = new List<byte>(new byte[ArenaExeUnpacker.CompressedStart]);
        file.AddRange(payload);

        // Trailer: the decompressed size as a real-mode far address, then padding.
        var segment = declaredSize / 16;
        var offset = declaredSize % 16;
        file.Add((byte)(segment & 0xFF));
        file.Add((byte)((segment >> 8) & 0xFF));
        file.Add((byte)(offset & 0xFF));
        file.Add((byte)((offset >> 8) & 0xFF));
        file.AddRange(new byte[4]);

        return [.. file];
    }

    private static string Decode(IReadOnlyList<byte> payload, int declaredSize)
    {
        return Encoding.ASCII.GetString(ArenaExeUnpacker.Unpack(BuildExe(payload, declaredSize), "T.EXE"));
    }

    [Fact]
    public void Unpack_SingleLiteral_AppliesThePositionDerivedXorKey()
    {
        // Bits (LSB first): 0 = literal, 1 = duplication, then the escape code 011100 and 0xFF to
        // finish. Array = bits 1,3,4,5 set = 0x003A. The literal is read with BitsRead = 1, so its
        // key is 16 - 1 = 15 and 'A' is stored as 0x41 ^ 0x0F = 0x4E.
        byte[] payload = [0x3A, 0x00, 0x4E, 0xFF, 0xFF, 0xFF];

        Assert.Equal("A", Decode(payload, 1));
    }

    [Fact]
    public void Unpack_BackReference_CopiesEarlierOutput()
    {
        // Two literals then a length-2 back-reference at distance 2, which repeats them.
        // A length of 2 skips the high-offset code entirely, so only a low byte follows.
        byte[] payload = [0xAC, 0x03, 0x4E, 0x4C, 0x02, 0xFF, 0xFF, 0xFF];

        Assert.Equal("ABAB", Decode(payload, 4));
    }

    [Fact]
    public void Unpack_OverlappingBackReference_ExpandsAsARun()
    {
        // One literal then length 3 (code 11) at distance 1: the copy reads bytes it is still
        // writing, which is how the format encodes runs. 'A' becomes "AAAA". Length 3 is not 2, so
        // the high offset code ("1" = 0) is read before the low byte.
        byte[] payload = [0xBE, 0x03, 0x4E, 0x01, 0xFF, 0xFF, 0xFF];

        Assert.Equal("AAAA", Decode(payload, 4));
    }

    [Fact]
    public void Unpack_LengthAboveTwo_ReadsTheHighOffsetCode()
    {
        // Three literals, then length 4 (code 000) at distance 3. Because the length is not 2 the
        // high offset byte is coded first — here as "1", meaning 0.
        byte[] payload = [0x88, 0x1D, 0x4E, 0x4C, 0x4E, 0x03, 0xFF, 0xFF, 0xFF];

        Assert.Equal("ABCABCA", Decode(payload, 7));
    }

    [Fact]
    public void LooksPacked_RequiresTheTerminator()
    {
        Assert.True(ArenaExeUnpacker.LooksPacked(BuildExe([0x3A, 0x00, 0x4E, 0xFF, 0xFF, 0xFF], 1)));

        // Same file with the terminator broken.
        var unterminated = BuildExe([0x3A, 0x00, 0x4E, 0xFF, 0x00, 0x00], 1);
        Assert.False(ArenaExeUnpacker.LooksPacked(unterminated));
    }

    [Fact]
    public void LooksPacked_RejectsAFileTooShortToHoldAPayload()
    {
        Assert.False(ArenaExeUnpacker.LooksPacked(new byte[ArenaExeUnpacker.CompressedStart]));
        Assert.False(ArenaExeUnpacker.LooksPacked([]));
    }

    [Theory]
    // The trailer is a far address: size = (segment * 16) + offset.
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 16)]
    [InlineData(2, 3, 35)]
    [InlineData(19039, 0, 304624)] // the retail A.EXE's declared size
    public void ReadDeclaredSize_ScalesTheSegmentByParagraphs(int segment, int offset, int expected)
    {
        var file = new List<byte>(new byte[ArenaExeUnpacker.CompressedStart + 4]);
        file.Add((byte)(segment & 0xFF));
        file.Add((byte)((segment >> 8) & 0xFF));
        file.Add((byte)(offset & 0xFF));
        file.Add((byte)((offset >> 8) & 0xFF));
        file.AddRange(new byte[4]);

        Assert.Equal(expected, ArenaExeUnpacker.ReadDeclaredSize([.. file]));
    }

    [Fact]
    public void Unpack_MissingTerminator_Throws()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => ArenaExeUnpacker.Unpack(BuildExe([0x3A, 0x00, 0x4E, 0xFF, 0x00, 0x00], 1), "BAD.EXE"));

        Assert.Contains("0xFFFF", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unpack_FileTooSmall_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaExeUnpacker.Unpack(new byte[100], "TINY.EXE"));
    }

    [Fact]
    public void Unpack_BackReferenceBeforeTheStart_Throws()
    {
        // The same shape as the two-literal case, but the distance points past the beginning.
        byte[] payload = [0xAC, 0x03, 0x4E, 0x4C, 0x40, 0xFF, 0xFF, 0xFF];

        var ex = Assert.Throws<InvalidDataException>(() => ArenaExeUnpacker.Unpack(BuildExe(payload, 4), "BAD.EXE"));
        Assert.Contains("before the start", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unpack_MoreOutputThanDeclared_Throws()
    {
        // Declares one byte but the stream produces four.
        var ex = Assert.Throws<InvalidDataException>(
            () => ArenaExeUnpacker.Unpack(BuildExe([0xDE, 0x01, 0x4E, 0x01, 0xFF, 0xFF, 0xFF], 1), "BAD.EXE"));

        Assert.Contains("more than", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unpack_EscapeSkipByte_ResumesWithoutEmitting()
    {
        // An escape length followed by 0xFE means "ignore this bit and carry on". Bits: 1 (dup),
        // 011100 (escape) -> 0xFE, then 0 (literal 'A'), then 1 (dup), 011100 (escape) -> 0xFF.
        // Array bits set: 0,2,3,4,8,10,11,12 = 0x1D1D. The literal is read with BitsRead = 8, so
        // its key is 8 and 'A' is stored as 0x41 ^ 0x08 = 0x49.
        byte[] payload = [0x1D, 0x1D, 0xFE, 0x49, 0xFF, 0xFF, 0xFF];

        Assert.Equal("A", Decode(payload, 1));
    }
}
