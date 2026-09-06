using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Fallout;

/// <summary>
///     Synthetic vectors for <see cref="FalloutFrmFile" /> and <see cref="FalloutPalette" />,
///     shaped after the retail Fallout 1 archives measured 2026-09-05. The two facts most easily
///     got wrong are pinned outright: the FRM header is 0x3E bytes (not the 0x3A its last field
///     begins at), and COLOR.PAL's 255s are sentinels rather than 8-bit colour.
/// </summary>
public sealed class FalloutFrmFileTests
{
    private static byte[] Be16(int v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        return b;
    }

    private static byte[] Be32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        return b;
    }

    private static byte[] Frame(int width, int height, byte fill, int xOffset = 0, int yOffset = 0)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, fill);
        var header = new byte[FalloutFrmFile.FrameHeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)height);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)pixels.Length);
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(8), (short)xOffset);
        BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(10), (short)yOffset);
        return [.. header, .. pixels];
    }

    /// <summary>
    ///     Builds a sprite whose six directions take the offsets given — repeat an offset to share
    ///     artwork, which is what retail does for symmetric directions.
    /// </summary>
    private static byte[] Build(int framesPerDirection, uint[] directionOffsets, byte[] frameArea, int fps = 10, int actionFrame = 0)
    {
        var header = new byte[FalloutFrmFile.HeaderLength];
        Be32(FalloutFrmFile.RetailVersion).CopyTo(header, 0);
        Be16(fps).CopyTo(header, 4);
        Be16(actionFrame).CopyTo(header, 6);
        Be16(framesPerDirection).CopyTo(header, 8);
        for (var i = 0; i < FalloutFrmFile.DirectionCount; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(0x0A + 2 * i), (short)(i + 1));
            BinaryPrimitives.WriteInt16BigEndian(header.AsSpan(0x16 + 2 * i), (short)(-(i + 1)));
            Be32(directionOffsets[i]).CopyTo(header, FalloutFrmFile.DirectionOffsetsPosition + 4 * i);
        }

        Be32((uint)frameArea.Length).CopyTo(header, FalloutFrmFile.FrameAreaSizePosition);
        return [.. header, .. frameArea];
    }

    [Fact]
    public void Parse_SingleDirectionSprite_ReadsHeaderAndFrames()
    {
        var area = Frame(4, 3, 9, xOffset: 5, yOffset: -7);
        var file = FalloutFrmFile.Parse(Build(1, [0, 0, 0, 0, 0, 0], area), "BACK1.FRM");

        Assert.Equal((FalloutFrmFile.RetailVersion, 10, 0, 1), (file.Version, file.FramesPerSecond, file.ActionFrame, file.FramesPerDirection));
        Assert.Equal(6, file.Directions.Count);

        // Every direction shares offset 0, so there is one run of artwork behind all six.
        var frame = Assert.Single(file.DistinctFrames);
        Assert.Equal((4, 3), (frame.Width, frame.Height));
        Assert.Equal((5, -7), (frame.Bitmap.XOffset, frame.Bitmap.YOffset));
        Assert.All(frame.Bitmap.Indices, index => Assert.Equal(9, index));
        Assert.Equal([1, 2, 3, 4, 5, 6], file.XShifts);
        Assert.Equal([-1, -2, -3, -4, -5, -6], file.YShifts);
    }

    [Fact]
    public void Parse_FrameAreaStartsAtThirtyEightHexNotThirtyA()
    {
        // The four bytes at 0x3A are the frame-area size. A reader that treated the header as
        // 0x3A bytes would read those as the first frame's width and height and fail the
        // width * height == size check — which is exactly how the real off-by-four was caught.
        var area = Frame(2, 2, 1);
        var file = FalloutFrmFile.Parse(Build(1, [0, 0, 0, 0, 0, 0], area), "A.FRM");

        Assert.Equal(0x3E, FalloutFrmFile.HeaderLength);
        Assert.Equal((2, 2), (Assert.Single(file.DistinctFrames).Width, 2));
    }

    [Fact]
    public void Parse_DirectionsWithDistinctOffsets_ReadTheirOwnRuns()
    {
        var first = Frame(2, 2, 1);
        var second = Frame(3, 1, 2);
        var area = first.Concat(second).ToArray();

        // Directions 0-2 share the first run; 3-5 share the second.
        var file = FalloutFrmFile.Parse(Build(1, [0, 0, 0, (uint)first.Length, (uint)first.Length, (uint)first.Length], area), "CRITTER.FRM");

        Assert.Equal(2, file.DistinctFrames.Count());
        Assert.Equal(1, file.Directions[0].Frames[0].Bitmap.Indices[0]);
        Assert.Equal(2, file.Directions[3].Frames[0].Bitmap.Indices[0]);
        Assert.Equal((2, 2), (file.Directions[0].Frames[0].Width, file.Directions[0].Frames[0].Height));
        Assert.Equal((3, 1), (file.Directions[3].Frames[0].Width, file.Directions[3].Frames[0].Height));
    }

    [Fact]
    public void Parse_MultipleFramesPerDirection_WalkSequentially()
    {
        var area = Frame(2, 2, 1).Concat(Frame(2, 2, 2)).Concat(Frame(1, 1, 3)).ToArray();
        var file = FalloutFrmFile.Parse(Build(3, [0, 0, 0, 0, 0, 0], area), "ANIM.FRM");

        var frames = file.Directions[0].Frames;
        Assert.Equal(3, frames.Count);
        Assert.Equal([1, 2, 3], frames.Select(f => f.Bitmap.Indices[0]));
    }

    [Fact]
    public void Parse_FrameWhoseSizeContradictsItsDimensions_Throws()
    {
        var area = Frame(4, 4, 1);
        BinaryPrimitives.WriteUInt32BigEndian(area.AsSpan(4), 15); // 4*4 is 16

        var error = Assert.Throws<InvalidDataException>(() => FalloutFrmFile.Parse(Build(1, [0, 0, 0, 0, 0, 0], area), "BAD.FRM"));

        Assert.Contains("4x4", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FramePastTheEnd_Throws()
    {
        var area = Frame(2, 2, 1);

        // Two frames declared, one supplied.
        Assert.Throws<InvalidDataException>(() => FalloutFrmFile.Parse(Build(2, [0, 0, 0, 0, 0, 0], area), "SHORT.FRM"));
    }

    [Fact]
    public void IsFrmFileName_CoversTheNumberedDirectionSiblings()
    {
        Assert.True(FalloutFrmFile.IsFrmFileName("HANPWR.FRM"));
        Assert.True(FalloutFrmFile.IsFrmFileName("hmjmpsat.fr0"));
        Assert.True(FalloutFrmFile.IsFrmFileName("HMJMPSAT.FR5"));
        Assert.False(FalloutFrmFile.IsFrmFileName("COLOR.PAL"));
        Assert.False(FalloutFrmFile.IsFrmFileName("MASTER.DAT"));
    }

    [Fact]
    public void Palette_PromotesSixBitEntriesAndTreatsTheWhitesAsSentinels()
    {
        var rgb = new byte[Palette.RgbByteCount];

        // Index 0: the transparency sentinel. Index 1: a genuine 6-bit mid grey. Index 229+: the
        // colour-cycling placeholders.
        rgb[0] = rgb[1] = rgb[2] = 255;
        rgb[3] = rgb[4] = rgb[5] = 63;
        rgb[6] = rgb[7] = rgb[8] = 32;
        for (var i = FalloutPalette.CycleStart; i < Palette.EntryCount; i++)
        {
            rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = 255;
        }

        var palette = FalloutPalette.Parse(rgb, "COLOR.PAL");

        // 63 promotes to exactly 255 — not left at 63, which is what an 8-bit reading would do.
        Assert.Equal((255, 255, 255, (byte)255), palette.GetEntry(1));

        // 32 lands on 130, not the 129 a value * 255 / 63 scale would give: the promotion
        // replicates the high bits ((v << 2) | (v >> 4)), which is what Palette.FromVga6Bit does
        // and what keeps both endpoints exact.
        Assert.Equal((byte)130, palette.GetEntry(2).R);
        Assert.Equal((byte)0, palette.GetEntry(0).A);
        Assert.Equal((byte)255, palette.GetEntry(FalloutPalette.CycleStart).R);
    }

    [Fact]
    public void Palette_ComponentAboveSixBitThatIsNotASentinel_Throws()
    {
        var rgb = new byte[Palette.RgbByteCount];
        rgb[3] = 200; // entry 1, not a sentinel and not 6-bit

        Assert.Throws<InvalidDataException>(() => FalloutPalette.Parse(rgb, "BAD.PAL"));
    }

    [Fact]
    public void Palette_TooShort_Throws()
    {
        Assert.Throws<InvalidDataException>(() => FalloutPalette.Parse(new byte[100], "SHORT.PAL"));
    }
}
