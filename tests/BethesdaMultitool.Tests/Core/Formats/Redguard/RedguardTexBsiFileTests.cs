using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic vectors for <see cref="RedguardTexBsiFile" />, shaped after the 415 retail sets
///     measured 2026-09-05. The two things most easily got wrong are pinned directly: the frame
///     count lives at BHDR+14 (not +16), and an animated image's DATA opens with a table of
///     LITTLE-endian u32 ROW offsets, four bytes each.
/// </summary>
public sealed class RedguardTexBsiFileTests
{
    private static byte[] Subrecord(string tag, byte[] payload)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        return [.. Encoding.ASCII.GetBytes(tag), .. length, .. payload];
    }

    private static byte[] Header(int width, int height, int frameCount, int x = 0, int y = 0)
    {
        var header = new byte[RedguardTexBsiFile.ImageHeaderLength];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)x);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)y);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), (ushort)frameCount);

        // A decoy at +16: the reference's other candidate for the frame count, and what a
        // mis-indexed reader would pick up.
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 71);
        return header;
    }

    private static byte[] SixBitPalette()
    {
        var rgb = new byte[RedguardTexBsiFile.PaletteLength];
        for (var i = 0; i < 256; i++)
        {
            rgb[i * 3] = (byte)(i / 4);
            rgb[i * 3 + 1] = (byte)(i / 4);
            rgb[i * 3 + 2] = (byte)(i / 4);
        }

        return rgb;
    }

    /// <summary>A still image: BSIF, BHDR, DATA (width x height indices), "END ".</summary>
    private static byte[] Still(string name, int width, int height, byte fill)
    {
        var pixels = new byte[width * height];
        Array.Fill(pixels, fill);
        var body = new List<byte>();
        body.AddRange(Subrecord("BSIF", []));
        body.AddRange(Subrecord("BHDR", Header(width, height, 1)));
        body.AddRange(Subrecord("DATA", pixels));
        body.AddRange(Subrecord("END ", []));
        return Image(name, [.. body]);
    }

    /// <summary>
    ///     An animated image: IFHD, BHDR, CMAP, DATA, "END ". DATA is the row-offset table
    ///     followed by <paramref name="frameCount" /> x height rows, but frame 1 reuses frame 0's
    ///     first row — the sharing the format exists for.
    /// </summary>
    private static byte[] Animated(string name, int width, int height, int frameCount)
    {
        var tableLength = 4 * height * frameCount;
        var rows = new List<byte>();
        var offsets = new List<uint>();
        for (var f = 0; f < frameCount; f++)
        {
            for (var y = 0; y < height; y++)
            {
                if (f == 1 && y == 0)
                {
                    offsets.Add(offsets[0]); // share frame 0's first row
                    continue;
                }

                offsets.Add((uint)(tableLength + rows.Count));
                var row = new byte[width];
                Array.Fill(row, (byte)(f * 16 + y + 1));
                rows.AddRange(row);
            }
        }

        var data = new List<byte>();
        foreach (var offset in offsets)
        {
            data.AddRange(BitConverter.GetBytes(offset));
        }

        data.AddRange(rows);

        var body = new List<byte>();
        body.AddRange(Subrecord("IFHD", new byte[44]));
        body.AddRange(Subrecord("BHDR", Header(width, height, frameCount)));
        body.AddRange(Subrecord("CMAP", SixBitPalette()));
        body.AddRange(Subrecord("DATA", [.. data]));
        body.AddRange(Subrecord("END ", []));
        return Image(name, [.. body]);
    }

    private static byte[] Image(string name, byte[] body)
    {
        var head = new byte[RedguardTexBsiFile.NameLength];
        Encoding.ASCII.GetBytes(name).CopyTo(head, 0);
        return [.. head, .. BitConverter.GetBytes((uint)body.Length), .. body];
    }

    private static byte[] Build(params byte[][] images) =>
        [.. images.SelectMany(static i => i), .. new byte[RedguardTexBsiFile.NameLength]];

    [Fact]
    public void Parse_StillImage_ReadsDimensionsAndPixels()
    {
        var file = RedguardTexBsiFile.Parse(Build(Still("A00000", 4, 3, 7)), "TEXBSI.000");

        var image = Assert.Single(file.Images);
        Assert.Equal(("A00000", 4, 3, 1, false), (image.Name, image.Width, image.Height, image.FrameCount, image.IsAnimated));
        Assert.Null(image.Palette);
        var frame = Assert.Single(image.Frames);
        Assert.Equal(12, frame.Indices.Length);
        Assert.All(frame.Indices, index => Assert.Equal(7, index));
    }

    [Fact]
    public void Parse_FrameCountComesFromOffsetFourteen()
    {
        // The builder writes 71 at +16 — what a reader that mis-indexed the BHDR struct would use.
        var file = RedguardTexBsiFile.Parse(Build(Still("A00000", 2, 2, 1)), "TEXBSI.000");

        Assert.Equal(1, Assert.Single(file.Images).FrameCount);
    }

    [Fact]
    public void Parse_AnimatedImage_ResolvesRowsThroughTheDwordOffsetTable()
    {
        var file = RedguardTexBsiFile.Parse(Build(Animated("B00000", 3, 2, 3)), "TEXBSI.357");

        var image = Assert.Single(file.Images);
        Assert.Equal((3, 2, 3, true), (image.Width, image.Height, image.FrameCount, image.IsAnimated));
        Assert.Equal(3, image.Frames.Count);
        Assert.All(image.Frames, frame => Assert.Equal(6, frame.Indices.Length));

        // Frame 0's rows are 1 and 2; frame 1 shares row 0 with frame 0 and has its own row 1.
        Assert.Equal([1, 1, 1, 2, 2, 2], image.Frames[0].Indices);
        Assert.Equal([1, 1, 1, 18, 18, 18], image.Frames[1].Indices);
        Assert.Equal([33, 33, 33, 34, 34, 34], image.Frames[2].Indices);
    }

    [Fact]
    public void Parse_AnimatedImage_CarriesItsOwnPromotedPalette()
    {
        var file = RedguardTexBsiFile.Parse(Build(Animated("B00000", 2, 1, 2)), "TEXBSI.357");

        var palette = Assert.Single(file.Images).Palette;
        Assert.NotNull(palette);

        // 6-bit VGA: the ramp's last entry is 63, which promotes to 255 rather than staying 63.
        Assert.Equal(Palette.EntryCount * 4, palette!.Rgba.Length);
        Assert.Equal(255, palette.Rgba[255 * 4]);
    }

    [Fact]
    public void Parse_IfhdWithASingleFrame_IsReadAsAStill()
    {
        // TEXBSI.357's fourth image: the IFHD tag but one frame and a plain width x height DATA.
        // The reference would read it as a row table and walk pixels as offsets.
        var body = new List<byte>();
        body.AddRange(Subrecord("IFHD", new byte[44]));
        body.AddRange(Subrecord("BHDR", Header(4, 2, 1)));
        body.AddRange(Subrecord("CMAP", SixBitPalette()));
        body.AddRange(Subrecord("DATA", Enumerable.Range(1, 8).Select(i => (byte)i).ToArray()));
        body.AddRange(Subrecord("END ", []));

        var file = RedguardTexBsiFile.Parse(Build(Image("C00000", [.. body])), "TEXBSI.357");

        var image = Assert.Single(file.Images);
        Assert.False(image.IsAnimated);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], Assert.Single(image.Frames).Indices);
        Assert.NotNull(image.Palette);
    }

    [Fact]
    public void Parse_ManyImages_WalkToTheNineNulTerminator()
    {
        var file = RedguardTexBsiFile.Parse(
            Build(Still("A00000", 2, 2, 1), Still("A00001", 3, 1, 2), Animated("A00002", 2, 2, 2)), "TEXBSI.000");

        Assert.Equal(["A00000", "A00001", "A00002"], file.Images.Select(i => i.Name));
        Assert.Equal([false, false, true], file.Images.Select(i => i.IsAnimated));
    }

    [Fact]
    public void Parse_StillWhoseDataIsNotWidthTimesHeight_Throws()
    {
        var body = new List<byte>();
        body.AddRange(Subrecord("BSIF", []));
        body.AddRange(Subrecord("BHDR", Header(4, 4, 1)));
        body.AddRange(Subrecord("DATA", new byte[15])); // one short of 16
        body.AddRange(Subrecord("END ", []));

        Assert.Throws<InvalidDataException>(() => RedguardTexBsiFile.Parse(Build(Image("A00000", [.. body])), "BAD.000"));
    }

    [Fact]
    public void Parse_SubrecordPastItsBody_Throws()
    {
        var bytes = Build(Still("A00000", 2, 2, 1));

        // Inflate the DATA subrecord's big-endian length past the image body.
        var dataLengthAt = RedguardTexBsiFile.NameLength + 4 + 8 + (8 + RedguardTexBsiFile.ImageHeaderLength) + 4;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(dataLengthAt), 4096);

        Assert.Throws<InvalidDataException>(() => RedguardTexBsiFile.Parse(bytes, "BAD.000"));
    }

    [Fact]
    public void IsTexBsiFileName_MatchesTheNumberedSetsOnly()
    {
        Assert.True(RedguardTexBsiFile.IsTexBsiFileName("TEXBSI.000"));
        Assert.True(RedguardTexBsiFile.IsTexBsiFileName("texbsi.415"));
        Assert.False(RedguardTexBsiFile.IsTexBsiFileName("TEXBSI.DAT"));
        Assert.False(RedguardTexBsiFile.IsTexBsiFileName("TEXTURE.000"));
        Assert.False(RedguardTexBsiFile.IsTexBsiFileName("TEXBSI"));
    }
}
