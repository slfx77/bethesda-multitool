using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>VID movie parsing: header, block walk, persistent canvas and the two run-length rules.</summary>
public class DaggerfallVidFileTests
{
    /// <summary>A movie with the retail header, a palette block, then the given blocks verbatim.</summary>
    internal static byte[] Movie(int frameCount, int width, int height, params byte[][] blocks)
    {
        var header = new byte[DaggerfallVidFile.HeaderLength];
        header[0] = (byte)'V';
        header[1] = (byte)'I';
        header[2] = (byte)'D';
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(3), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(5), (ushort)frameCount);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(7), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(9), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(11), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(13), 14);

        var palette = new byte[1 + DaggerfallVidFile.PaletteLength];
        palette[0] = 2;
        for (var i = 0; i < 256; i++)
        {
            // 6-bit VGA: index i maps to a grey of i/4, so index 4 is grey 0x04 -> 0x10 after promotion.
            palette[1 + i * 3] = (byte)(i / 4);
            palette[1 + i * 3 + 1] = (byte)(i / 4);
            palette[1 + i * 3 + 2] = (byte)(i / 4);
        }

        return [.. header, .. palette, .. blocks.SelectMany(b => b)];
    }

    /// <summary>A full-frame block: runs carry a value byte.</summary>
    internal static byte[] FullFrame(ushort delay, params byte[] payload)
    {
        return [3, (byte)(delay & 0xFF), (byte)(delay >> 8), .. payload];
    }

    /// <summary>An incremental block: runs SKIP pixels and carry no value byte.</summary>
    internal static byte[] IncrementalFrame(ushort delay, params byte[] payload)
    {
        return [1, (byte)(delay & 0xFF), (byte)(delay >> 8), .. payload];
    }

    /// <summary>An incremental block that starts at a row.</summary>
    internal static byte[] RowFrame(ushort delay, ushort row, params byte[] payload)
    {
        return [4, (byte)(delay & 0xFF), (byte)(delay >> 8), (byte)(row & 0xFF), (byte)(row >> 8), .. payload];
    }

    internal static byte[] AudioStart(params byte[] samples)
    {
        return [124, 0, 0, 166, (byte)(samples.Length & 0xFF), (byte)(samples.Length >> 8), .. samples];
    }

    internal static byte[] AudioMore(params byte[] samples)
    {
        return [125, (byte)(samples.Length & 0xFF), (byte)(samples.Length >> 8), .. samples];
    }

    internal static readonly byte[] EndOfFile = [20];

    [Fact]
    public void Parse_ReadsTheHeaderPaletteAudioAndBlockCensus()
    {
        // A frame's payload must end exactly when the canvas is full or on a zero control; anything
        // after that would be read as the next block, as it would be by the reference.
        var bytes = Movie(2, 4, 2,
            AudioStart(10, 20, 30),
            FullFrame(5, 0x88, 0x07),
            AudioMore(40, 50),
            IncrementalFrame(6, 0x82, 0x02, 0x09, 0x0A, 0x00),
            EndOfFile);

        var vid = DaggerfallVidFile.Parse(bytes, "ANIM0000.VID");

        Assert.Equal("ANIM0000.VID", vid.Name);
        Assert.Equal("VID", vid.HeaderTag);
        Assert.Equal(512, vid.Unknown1);
        Assert.Equal(14, vid.Unknown2);
        Assert.Equal(4, vid.GlobalDelay);
        Assert.Equal((4, 2), (vid.Width, vid.Height));
        Assert.Equal(2, vid.DeclaredFrameCount);
        Assert.Equal(2, vid.FrameCount);
        Assert.True(vid.EndOfFileSeen);

        Assert.Equal([10, 20, 30, 40, 50], vid.Audio.ToArray());
        Assert.Equal(5 / 11025d, vid.AudioSeconds);

        Assert.Equal(1, vid.BlockCounts[DaggerfallVidBlockType.VideoStartFrame]);
        Assert.Equal(1, vid.BlockCounts[DaggerfallVidBlockType.VideoIncrementalFrame]);
        Assert.Equal(1, vid.BlockCounts[DaggerfallVidBlockType.AudioStartFrame]);
        Assert.Equal(1, vid.BlockCounts[DaggerfallVidBlockType.AudioIncrementalFrame]);
        Assert.Equal(1, vid.BlockCounts[DaggerfallVidBlockType.EndOfFile]);

        // The census counts the header's own palette block, so a movie with no later palette
        // change still reports one.
        Assert.Equal(1, vid.BlockCounts[DaggerfallVidBlockType.Palette]);
    }

    [Fact]
    public void EnumerateFrames_PaintsAPersistentCanvas_WithRunsAndLiteralsPerBlockKind()
    {
        // Full frame: a run of 8 sevens, then a literal of 2, filling the 4x2 canvas is not needed —
        // the payload stops early with a 0 control.
        var bytes = Movie(3, 4, 2,
            FullFrame(5, 0x86, 0x07, 0x02, 0x01, 0x02, 0x00),
            IncrementalFrame(6, 0x82, 0x02, 0x0A, 0x0B, 0x00),
            RowFrame(7, 1, 0x02, 0x0C, 0x0D, 0x00),
            EndOfFile);

        var vid = DaggerfallVidFile.Parse(bytes, "X.VID");
        var frames = vid.EnumerateFrames().ToList();

        Assert.Equal(3, frames.Count);
        Assert.Equal([0, 1, 2], frames.Select(f => f.Index));
        Assert.Equal([5, 6, 7], frames.Select(f => f.Delay));
        Assert.Equal(DaggerfallVidBlockType.VideoStartFrame, frames[0].BlockType);
        Assert.Equal(DaggerfallVidBlockType.VideoIncrementalRowOffsetFrame, frames[2].BlockType);

        // Full frame: six 7s then the literals 1 and 2.
        Assert.Equal([7, 7, 7, 7, 7, 7, 1, 2], frames[0].Bitmap.Indices);

        // Incremental: the leading run SKIPS two pixels, so they keep their old values.
        Assert.Equal([7, 7, 10, 11, 7, 7, 1, 2], frames[1].Bitmap.Indices);

        // Row frame: writing starts at row 1, i.e. pixel index 4.
        Assert.Equal([7, 7, 10, 11, 12, 13, 1, 2], frames[2].Bitmap.Indices);

        // Palette entries are promoted from 6-bit VGA: index 4 is grey 1 -> 0x04.
        var (r, g, b, a) = frames[0].Palette.GetEntry(4);
        Assert.Equal((4, 4, 4, 255), (r, g, b, a));
    }

    [Fact]
    public void EnumerateFrames_AppliesAPaletteBlockMidStream()
    {
        var replacement = new byte[1 + DaggerfallVidFile.PaletteLength];
        replacement[0] = 2;
        replacement[1 + 7 * 3] = 0x3F;

        var bytes = Movie(2, 2, 1,
            FullFrame(5, 0x82, 0x07),
            replacement,
            IncrementalFrame(5, 0x00),
            EndOfFile);

        var vid = DaggerfallVidFile.Parse(bytes, "X.VID");
        var frames = vid.EnumerateFrames().ToList();

        Assert.Equal(2, frames.Count);

        // The header's palette block plus the replacement.
        Assert.Equal(2, vid.BlockCounts[DaggerfallVidBlockType.Palette]);
        // The fixture's palette gives index 7 the 6-bit grey 1, which promotes to 4.
        Assert.Equal((byte)4, frames[0].Palette.GetEntry(7).R);
        Assert.Equal((byte)255, frames[1].Palette.GetEntry(7).R);
    }

    [Fact]
    public void Parse_RejectsMalformedMovies()
    {
        Assert.Throws<InvalidDataException>(() => DaggerfallVidFile.Parse("NOTVID"u8.ToArray(), "X.VID"));
        Assert.Throws<InvalidDataException>(() => DaggerfallVidFile.Parse([.. "VID\0"u8, 0, 0, 0, 0], "X.VID"));

        // No palette block after the header.
        var noPalette = Movie(1, 2, 1, EndOfFile);
        noPalette[DaggerfallVidFile.HeaderLength] = 20;
        Assert.Throws<InvalidDataException>(() => DaggerfallVidFile.Parse(noPalette, "X.VID"));

        // An unknown block type.
        Assert.Throws<InvalidDataException>(() => DaggerfallVidFile.Parse(Movie(1, 2, 1, [99]), "X.VID"));

        // An audio block whose rate is not the 11,025 Hz value.
        var badRate = Movie(1, 2, 1, AudioStart(1, 2), EndOfFile);
        badRate[DaggerfallVidFile.HeaderLength + 1 + DaggerfallVidFile.PaletteLength + 3] = 100;
        Assert.Throws<InvalidDataException>(() => DaggerfallVidFile.Parse(badRate, "X.VID"));

        // A truncated audio payload.
        var truncated = Movie(1, 2, 1, AudioMore(1, 2, 3), EndOfFile);
        Assert.Throws<InvalidDataException>(() => DaggerfallVidFile.Parse(truncated[..^4], "X.VID"));
    }

    [Theory]
    [InlineData("ANIM0000.VID", true)]
    [InlineData("dag2.vid", true)]
    [InlineData("INTRO.FLC", false)]
    public void IsVidFileName_UsesTheExtension(string name, bool expected)
    {
        Assert.Equal(expected, DaggerfallVidFile.IsVidFileName(name));
    }
}
