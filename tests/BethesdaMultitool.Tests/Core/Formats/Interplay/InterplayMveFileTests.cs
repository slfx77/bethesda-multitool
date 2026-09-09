using System.Text;
using BethesdaMultitool.Core.Formats.Interplay;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Interplay;

/// <summary>
///     Vectors for Interplay <c>.MVE</c> identification, shaped after the 13 retail movies measured
///     2026-09-06 (Fallout plus the Van Buren prototype), all 640x480 and all tiling exactly.
/// </summary>
public sealed class InterplayMveFileTests
{
    private static byte[] Opcode(byte opcode, byte version, params byte[] data)
    {
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes((ushort)data.Length));
        b.Add(opcode);
        b.Add(version);
        b.AddRange(data);
        return [.. b];
    }

    private static byte[] Chunk(ushort type, params byte[][] opcodes)
    {
        var payload = opcodes.SelectMany(o => o).ToArray();
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes((ushort)payload.Length));
        b.AddRange(BitConverter.GetBytes(type));
        b.AddRange(payload);
        return [.. b];
    }

    private static byte[] Movie(int trailing = 0, params byte[][] chunks)
    {
        var b = new List<byte>(Encoding.ASCII.GetBytes(InterplayMveFile.Signature));
        b.AddRange([0x1A, 0x00, 0x1A, 0x00, 0x00, 0x01, 0x33, 0x11]); // the 8 bytes after the text
        foreach (var chunk in chunks)
        {
            b.AddRange(chunk);
        }

        b.AddRange(new byte[trailing]);
        return [.. b];
    }

    [Fact]
    public void Parse_ReadsTheScreenSizeFromOpcodeZeroA()
    {
        var movie = InterplayMveFile.Parse(
            Movie(0, Chunk(2,
                Opcode(InterplayMveFile.ScreenSizeOpcode, 0, 0x80, 0x02, 0xE0, 0x01, 1, 1),
                Opcode(InterplayMveFile.EndOfChunkOpcode, 0))),
            "BOIL1.MVE");

        Assert.Equal((640, 480), (movie.Width, movie.Height));
        Assert.Single(movie.Chunks);
    }

    [Fact]
    public void Parse_WalksEveryChunkAndItsOpcodes()
    {
        var movie = InterplayMveFile.Parse(
            Movie(0,
                Chunk(2, Opcode(InterplayMveFile.PaletteOpcode, 0, 1, 0, 2, 0, 9, 9, 9, 8, 8, 8),
                    Opcode(InterplayMveFile.EndOfChunkOpcode, 0)),
                Chunk(1, Opcode(0x08, 0, 1, 2, 3, 4), Opcode(InterplayMveFile.EndOfChunkOpcode, 0))),
            "T.MVE");

        Assert.Equal(2, movie.Chunks.Count);
        Assert.Equal([2, 1], movie.Chunks.Select(c => c.Type));
        Assert.Equal(1, movie.PaletteChunks);
        Assert.Contains((byte)0x08, movie.Chunks[1].Opcodes);
    }

    [Fact]
    public void Parse_RejectsTrailingBytes()
    {
        // The chunks must reach EOF exactly — that is what makes this a parse and not a sniff.
        var error = Assert.Throws<InvalidDataException>(() =>
            InterplayMveFile.Parse(Movie(3, Chunk(2, Opcode(InterplayMveFile.EndOfChunkOpcode, 0))), "BAD.MVE"));
        Assert.Contains("chunks end at", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsAChunkRunningPastTheFile()
    {
        var b = Movie(0, Chunk(2, Opcode(InterplayMveFile.EndOfChunkOpcode, 0)));
        b[InterplayMveFile.HeaderLength] = 0xFF; // inflate the chunk length

        Assert.Throws<InvalidDataException>(() => InterplayMveFile.Parse(b, "BAD.MVE"));
    }

    [Fact]
    public void Parse_RejectsSomethingThatIsNotAMovie()
    {
        Assert.Throws<InvalidDataException>(() => InterplayMveFile.Parse("RIFF....WAVE"u8.ToArray(), "x.wav"));
    }

    [Fact]
    public void IsMveFile_ChecksTheSignature()
    {
        Assert.True(InterplayMveFile.IsMveFile(Movie(0, Chunk(0, Opcode(InterplayMveFile.EndOfChunkOpcode, 0)))));
        Assert.False(InterplayMveFile.IsMveFile("Interplay AVI File\u001a\0......"u8.ToArray()));
    }
}
