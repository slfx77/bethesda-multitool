using System.Collections.Generic;
using System.Linq;
using BethesdaMultitool.Core.Formats.Xngine.Flic;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine.Flic;

/// <summary>
///     Vectors for <see cref="FlicFile" />, built to the shapes the retail Arena animations use
///     (surveyed 2026-09-01 across all 20 loose .FLC/.CEL files: every one is 8-bit, every one
///     stores exactly header + 1 frame blocks, and chunk types are limited to COLOR_256, BYTE_RUN,
///     DELTA_FLC and a single thumbnail).
/// </summary>
public class FlicFileTests
{
    private const int Width = 4;
    private const int Height = 2;

    private static void WriteU16(List<byte> to, int value)
    {
        to.Add((byte)(value & 0xFF));
        to.Add((byte)((value >> 8) & 0xFF));
    }

    private static void WriteU32(List<byte> to, int value)
    {
        to.Add((byte)(value & 0xFF));
        to.Add((byte)((value >> 8) & 0xFF));
        to.Add((byte)((value >> 16) & 0xFF));
        to.Add((byte)((value >> 24) & 0xFF));
    }

    private static List<byte> Chunk(ushort type, IEnumerable<byte> data)
    {
        var body = data.ToList();
        var chunk = new List<byte>();
        WriteU32(chunk, body.Count + 6);
        WriteU16(chunk, type);
        chunk.AddRange(body);
        return chunk;
    }

    private static List<byte> FrameBlock(params List<byte>[] chunks)
    {
        var body = chunks.SelectMany(c => c).ToList();
        var block = new List<byte>();
        WriteU32(block, body.Count + 16);
        WriteU16(block, 0xF1FA);
        WriteU16(block, chunks.Length);
        block.AddRange(new byte[8]); // reserved
        block.AddRange(body);
        return block;
    }

    /// <summary>A COLOR_256 chunk whose entry i is (i, i, i) unless overridden.</summary>
    private static List<byte> PaletteChunk(byte? uniform = null)
    {
        var data = new List<byte>();
        WriteU16(data, 1); // one packet
        data.Add(0); // skip count
        data.Add(0); // colour count (0 means 256)
        for (var i = 0; i < Palette.EntryCount; i++)
        {
            var v = uniform ?? (byte)i;
            data.Add(v);
            data.Add(v);
            data.Add(v);
        }

        return Chunk(4, data);
    }

    /// <summary>Assembles a FLIC around the given frame blocks.</summary>
    private static byte[] BuildFlic(
        IEnumerable<List<byte>> frameBlocks,
        int declaredFrames,
        ushort magic = 0xAF12,
        int width = Width,
        int height = Height,
        int depth = 8,
        int speedMs = 100)
    {
        var body = frameBlocks.SelectMany(b => b).ToList();
        var header = new List<byte>();
        WriteU32(header, 128 + body.Count);
        WriteU16(header, magic);
        WriteU16(header, declaredFrames);
        WriteU16(header, width);
        WriteU16(header, height);
        WriteU16(header, depth);
        WriteU16(header, 0); // flags
        WriteU32(header, speedMs);
        header.AddRange(new byte[128 - header.Count]);

        return [.. header, .. body];
    }

    /// <summary>BYTE_RUN data: row 0 is four copies of 7, row 1 is the literals 1..4.</summary>
    private static List<byte> ByteRunChunk()
    {
        return Chunk(15, new byte[]
        {
            1, 4, 7, // row 0: one packet, repeat 7 four times
            1, 0xFC, 1, 2, 3, 4 // row 1: one packet, -4 = four literals
        });
    }

    [Fact]
    public void Parse_ByteRunFrame_DecodesRepeatAndLiteralPackets()
    {
        var flic = FlicFile.Parse(
            BuildFlic([FrameBlock(PaletteChunk(), ByteRunChunk()), FrameBlock(ByteRunChunk())], 1),
            "T.FLC");

        var frame = Assert.Single(flic.Frames);
        Assert.Equal([7, 7, 7, 7, 1, 2, 3, 4], frame.Image.Indices);
        Assert.Equal(Width, flic.Width);
        Assert.Equal(Height, flic.Height);
    }

    [Fact]
    public void Parse_PaletteChunk_IsReadAsFullRangeRgb()
    {
        var flic = FlicFile.Parse(
            BuildFlic([FrameBlock(PaletteChunk(), ByteRunChunk()), FrameBlock(ByteRunChunk())], 1),
            "T.FLC");

        var palette = Assert.Single(flic.Frames).Palette;

        // Entry 200 is (200,200,200): a 6-bit promotion would not round-trip it.
        Assert.Equal(((byte)200, (byte)200, (byte)200, (byte)255), palette.GetEntry(200));
        Assert.Equal(((byte)7, (byte)7, (byte)7, (byte)255), palette.GetEntry(7));
    }

    [Fact]
    public void Parse_DeltaFrame_WritesLiteralPixelPairsAfterAColumnSkip()
    {
        // lineCount 1; control word 1 = one packet; packet skips one column then writes one pair.
        var delta = Chunk(7, new byte[] { 1, 0, 1, 0, 1, 1, 0xAA, 0xBB });

        var flic = FlicFile.Parse(
            BuildFlic(
            [
                FrameBlock(PaletteChunk(), ByteRunChunk()),
                FrameBlock(delta),
                FrameBlock(ByteRunChunk())
            ], 2),
            "T.FLC");

        Assert.Equal(2, flic.Frames.Count);
        Assert.Equal([7, 0xAA, 0xBB, 7, 1, 2, 3, 4], flic.Frames[1].Image.Indices);

        // The delta patches a copy — the earlier frame keeps its own pixels.
        Assert.Equal([7, 7, 7, 7, 1, 2, 3, 4], flic.Frames[0].Image.Indices);
    }

    [Fact]
    public void Parse_DeltaFrame_RepeatsAPairForANegativeCount()
    {
        // count -2 repeats the pair (0x0C, 0x0D) twice, filling the whole 4-pixel row.
        var delta = Chunk(7, new byte[] { 1, 0, 1, 0, 0, 0xFE, 0x0C, 0x0D });

        var flic = FlicFile.Parse(
            BuildFlic(
            [
                FrameBlock(PaletteChunk(), ByteRunChunk()),
                FrameBlock(delta),
                FrameBlock(ByteRunChunk())
            ], 2),
            "T.FLC");

        Assert.Equal([0x0C, 0x0D, 0x0C, 0x0D, 1, 2, 3, 4], flic.Frames[1].Image.Indices);
    }

    [Fact]
    public void Parse_DeltaFrame_SkipsRowsWhenBothHighBitsAreSet()
    {
        // 0xFFFF as int16 is -1, so the row cursor advances by one before the packet applies.
        var delta = Chunk(7, new byte[] { 1, 0, 0xFF, 0xFF, 1, 0, 0, 1, 0x33, 0x44 });

        var flic = FlicFile.Parse(
            BuildFlic(
            [
                FrameBlock(PaletteChunk(), ByteRunChunk()),
                FrameBlock(delta),
                FrameBlock(ByteRunChunk())
            ], 2),
            "T.FLC");

        // Row 0 untouched; row 1's first pair replaced.
        Assert.Equal([7, 7, 7, 7, 0x33, 0x44, 3, 4], flic.Frames[1].Image.Indices);
    }

    [Fact]
    public void Parse_DeltaFrame_BitFifteenAloneSetsTheRowsLastPixel()
    {
        // 0x80AB: bit 15 set, bit 14 clear -> write 0xAB at the end of row 0, then advance.
        var delta = Chunk(7, new byte[] { 1, 0, 0xAB, 0x80, 0, 0 });

        var flic = FlicFile.Parse(
            BuildFlic(
            [
                FrameBlock(PaletteChunk(), ByteRunChunk()),
                FrameBlock(delta),
                FrameBlock(ByteRunChunk())
            ], 2),
            "T.FLC");

        Assert.Equal([7, 7, 7, 0xAB, 1, 2, 3, 4], flic.Frames[1].Image.Indices);
    }

    [Fact]
    public void Parse_FrameBlockWithNoPictureChunk_StillCountsAsAFrame()
    {
        // 8 of KING.FLC's 90 frames carry only a palette. They hold the previous image and must
        // keep their slot, or the animation comes out short.
        var flic = FlicFile.Parse(
            BuildFlic(
            [
                FrameBlock(PaletteChunk(), ByteRunChunk()),
                FrameBlock(PaletteChunk(uniform: 9)),
                FrameBlock(ByteRunChunk())
            ], 2),
            "T.FLC");

        Assert.Equal(2, flic.Frames.Count);
        Assert.Equal(flic.Frames[0].Image.Indices, flic.Frames[1].Image.Indices);
        Assert.Equal(((byte)9, (byte)9, (byte)9, (byte)255), flic.Frames[1].Palette.GetEntry(3));
    }

    [Fact]
    public void Parse_DropsTheTrailingLoopBackFrame_SoTheCountMatchesTheHeader()
    {
        // Every retail file stores header + 1 blocks; the extra one loops back to frame 0.
        var flic = FlicFile.Parse(
            BuildFlic(
            [
                FrameBlock(PaletteChunk(), ByteRunChunk()),
                FrameBlock(ByteRunChunk()),
                FrameBlock(ByteRunChunk())
            ], 2),
            "T.FLC");

        Assert.Equal(2, flic.DeclaredFrameCount);
        Assert.Equal(flic.DeclaredFrameCount, flic.Frames.Count);
    }

    [Fact]
    public void Parse_PrefixBlock_IsNotAFrame()
    {
        var prefix = new List<byte>();
        WriteU32(prefix, 16);
        WriteU16(prefix, 0xF100);
        WriteU16(prefix, 0);
        prefix.AddRange(new byte[8]);

        var flic = FlicFile.Parse(
            BuildFlic([prefix, FrameBlock(PaletteChunk(), ByteRunChunk()), FrameBlock(ByteRunChunk())], 1),
            "T.CEL");

        Assert.Single(flic.Frames);
    }

    [Fact]
    public void SecondsPerFrame_ComesFromTheHeaderDelay()
    {
        var flic = FlicFile.Parse(
            BuildFlic([FrameBlock(PaletteChunk(), ByteRunChunk()), FrameBlock(ByteRunChunk())], 1, speedMs: 142),
            "T.FLC");

        Assert.Equal(0.142, flic.SecondsPerFrame, 6);
        Assert.Equal(0.142, flic.DurationSeconds, 6);
    }

    [Fact]
    public void IsFlic_RecognizesBothMagics()
    {
        Assert.True(FlicFile.IsFlic(BuildFlic([FrameBlock(PaletteChunk(), ByteRunChunk())], 1)));
        Assert.True(FlicFile.IsFlic(BuildFlic([FrameBlock(PaletteChunk(), ByteRunChunk())], 1, magic: 0xAF11)));
        Assert.False(FlicFile.IsFlic("not a flic at all"u8.ToArray()));
    }

    [Fact]
    public void Parse_FliVariant_IsRejectedExplicitly()
    {
        var ex = Assert.Throws<NotSupportedException>(() => FlicFile.Parse(
            BuildFlic([FrameBlock(PaletteChunk(), ByteRunChunk())], 1, magic: 0xAF11), "OLD.FLI"));

        Assert.Contains("FLI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnknownMagic_Throws()
    {
        Assert.Throws<InvalidDataException>(() => FlicFile.Parse(
            BuildFlic([FrameBlock(PaletteChunk(), ByteRunChunk())], 1, magic: 0x1234), "T.FLC"));
    }

    [Fact]
    public void Parse_NonEightBitDepth_Throws()
    {
        Assert.Throws<NotSupportedException>(() => FlicFile.Parse(
            BuildFlic([FrameBlock(PaletteChunk(), ByteRunChunk())], 1, depth: 16), "T.FLC"));
    }

    [Fact]
    public void Parse_PictureBeforeAnyPalette_Throws()
    {
        Assert.Throws<InvalidDataException>(() => FlicFile.Parse(
            BuildFlic([FrameBlock(ByteRunChunk()), FrameBlock(ByteRunChunk())], 1), "T.FLC"));
    }

    [Fact]
    public void Parse_FrameClaimingMoreBytesThanTheFile_Throws()
    {
        var file = BuildFlic([FrameBlock(PaletteChunk(), ByteRunChunk())], 1).ToList();

        // Inflate the first frame block's size field.
        file[128] = 0xFF;
        file[129] = 0xFF;

        Assert.Throws<InvalidDataException>(() => FlicFile.Parse([.. file], "T.FLC"));
    }

    [Fact]
    public void Parse_TooSmallForAHeader_Throws()
    {
        Assert.Throws<InvalidDataException>(() => FlicFile.Parse(new byte[64], "T.FLC"));
    }
}
