using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for the Van Buren <c>8TRE</c> scene container, shaped after the 39 shipped scenes
///     measured 2026-09-06 — every one tiling its declared content exactly.
/// </summary>
public sealed class VanBurenSceneFileTests
{
    private static byte[] Scene((string Tag, int Length)[] chunks, int trailer = 16, int lengthBias = 0)
    {
        var content = chunks.Sum(c => VanBurenSceneFile.ChunkHeaderLength + c.Length);
        var b = new byte[VanBurenSceneFile.HeaderLength + content + trailer];
        Encoding.ASCII.GetBytes(VanBurenSceneFile.Tag).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(content + lengthBias));

        var at = VanBurenSceneFile.HeaderLength;
        foreach (var (tag, length) in chunks)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(b, at);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), (uint)length);
            at += VanBurenSceneFile.ChunkHeaderLength + length;
        }

        return b;
    }

    [Fact]
    public void Parse_TilesTheDeclaredContentAndReportsTheTrailerSeparately()
    {
        // ⚑ The payload is LONGER than the content. Walking to the end of the payload instead of to
        // 8 + declared length fails on 36 of the 39 shipped scenes — that is how the trailer was
        // found, and why it is surfaced rather than folded into the last chunk.
        var scene = VanBurenSceneFile.Parse(
            Scene([("HEAD", 16), ("LVLD", 64), ("MATD", 32), ("TXTD", 8), ("VTXD", 128), ("TREE", 48)]),
            "level.8tre");

        Assert.Equal(["HEAD", "LVLD", "MATD", "TXTD", "VTXD", "TREE"], scene.Chunks.Select(c => c.Tag));
        Assert.Equal([16, 64, 32, 8, 128, 48], scene.Chunks.Select(c => c.Length));
        Assert.Equal(16, scene.TrailerLength);
        // Offset is the chunk BODY, so it clears the file header AND the chunk's own tag+length.
        Assert.Equal(VanBurenSceneFile.HeaderLength + VanBurenSceneFile.ChunkHeaderLength,
            scene.Chunks[0].Offset);
    }

    [Fact]
    public void Parse_RejectsAChunkStreamThatDoesNotConsumeTheContent()
    {
        // A declared content length longer than the chunks fill must be refused. ⚠ It surfaces as
        // the TAG error rather than the length one, because the walk reaches into the trailer and
        // finds bytes that are not a four-character tag — which is the check that stops a
        // zero-filled region parsing as endless zero-length chunks. Either refusal is correct, so
        // this pins the refusal and not the wording.
        Assert.Throws<InvalidDataException>(() =>
            VanBurenSceneFile.Parse(Scene([("HEAD", 16)], lengthBias: 8), "bad.8tre"));
    }

    [Fact]
    public void Parse_RejectsAZeroFilledRegion()
    {
        // ⚑ The reason the tag check exists: without it, zeros parse as an unbounded run of
        // zero-length chunks tagged "\0\0\0\0" that lands exactly on the end, so padding validates.
        var b = new byte[64];
        Encoding.ASCII.GetBytes(VanBurenSceneFile.Tag).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 32);

        Assert.Throws<InvalidDataException>(() => VanBurenSceneFile.Parse(b, "zeros.8tre"));
    }

    [Fact]
    public void Parse_RejectsAChunkRunningPastTheContentEnd()
    {
        var b = Scene([("HEAD", 16), ("VTXD", 16)]);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(VanBurenSceneFile.HeaderLength + 4), 9999);

        Assert.Throws<InvalidDataException>(() => VanBurenSceneFile.Parse(b, "bad.8tre"));
    }

    [Fact]
    public void Read_ReturnsTheChunkBody()
    {
        var b = Scene([("HEAD", 16), ("VTXD", 32)]);
        var scene = VanBurenSceneFile.Parse(b, "t.8tre");
        b[scene.Chunks[1].Offset] = 0xAB;

        Assert.Equal(0xAB, VanBurenSceneFile.Read(b, scene.Chunks[1])[0]);
        Assert.Equal(32, VanBurenSceneFile.Read(b, scene.Chunks[1]).Length);
    }

    [Fact]
    public void IsScene_RequiresTheTagAndAContentLengthThatFits()
    {
        Assert.True(VanBurenSceneFile.IsScene(Scene([("HEAD", 16)])));

        var overrun = Scene([("HEAD", 16)]);
        BinaryPrimitives.WriteUInt32LittleEndian(overrun.AsSpan(4), 999_999);
        Assert.False(VanBurenSceneFile.IsScene(overrun));

        Assert.False(VanBurenSceneFile.IsScene(Encoding.ASCII.GetBytes("EMAP\0\0\0\0")));
    }
}