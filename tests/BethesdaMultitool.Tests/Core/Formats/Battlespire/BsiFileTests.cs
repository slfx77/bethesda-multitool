using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     BSI chunk parsing on synthetic files: the big-endian chunk lengths, the BSIF skip, palettes,
///     several images per file, and both data layouts (raw, and the line table with its run-length
///     lines).
/// </summary>
public class BsiFileTests
{
    /// <summary>A chunk: 4-byte tag, BIG-endian u32 length, payload.</summary>
    private static byte[] Chunk(string tag, params byte[] payload)
    {
        var header = new byte[8];
        Encoding.ASCII.GetBytes(tag.PadRight(4)).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)payload.Length);
        return [.. header, .. payload];
    }

    private static byte[] Header(int width, int height, int frames, int compression, int xOffset = 0, int yOffset = 0)
    {
        var header = new byte[26];
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(0), (short)xOffset);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(2), (short)yOffset);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(4), (short)width);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(6), (short)height);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(14), (short)frames);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(24), (short)compression);
        return header;
    }

    /// <summary>A CMAP whose entry i is the 6-bit grey i/4, so index i renders as grey (i/4)*4ish.</summary>
    private static byte[] ColorMap()
    {
        var map = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            map[i * 3] = (byte)(i / 4);
            map[i * 3 + 1] = (byte)(i / 4);
            map[i * 3 + 2] = (byte)(i / 4);
        }

        return map;
    }

    private static byte[] HighColor()
    {
        var chunk = new byte[BsiFile.HighColorLength];

        // Entry 3 is pure red in 15-bit rrrrrggg gggbbbbb form (shifted left one bit, as the
        // format stores it).
        BinaryPrimitives.WriteUInt16LittleEndian(chunk.AsSpan(3 * 2), 0x1F << 11);
        return chunk;
    }

    [Fact]
    public void Parse_ReadsHeaderPalettesAndRawFrames()
    {
        byte[] pixels = [1, 2, 3, 4, 200, 201, 202, 203];
        byte[] file =
        [
            // BSIF carries a length that counts from its own header, and the engine skips the
            // eight header bytes and reads on — so the fixture emits the header alone.
            .. Chunk("BSIF"),
            .. Chunk("IFHD", new byte[44]),
            .. Chunk("NAME", "TERRAIN\0"u8.ToArray()),
            .. Chunk("BHDR", Header(2, 2, 2, 0, xOffset: 260, yOffset: 262)),
            .. Chunk("HICL", HighColor()),
            .. Chunk("CMAP", ColorMap()),
            .. Chunk("HTBL", new byte[8192]),
            .. Chunk("DATA", pixels),
            .. Chunk("END ")
        ];

        var bsi = BsiFile.Parse(file, "TERR02.BSI");

        var image = Assert.Single(bsi.Images);
        Assert.Equal("TERRAIN", image.Name);
        Assert.Equal((2, 2, 2), (image.Width, image.Height, image.FrameCount));
        Assert.Equal((260, 262), (image.XOffset, image.YOffset));
        Assert.Equal(0, image.Compression);
        Assert.Equal(8192, image.HighColorTable.Length);

        Assert.Equal(2, image.Frames.Count);
        Assert.Equal([1, 2, 3, 4], image.Frames[0].Indices);
        Assert.Equal([200, 201, 202, 203], image.Frames[1].Indices);
        Assert.Equal(260, image.Frames[0].XOffset);

        // CMAP is 6-bit VGA: index 200 is grey 50, which promotes to (50 << 2) | (50 >> 4) = 203.
        Assert.NotNull(image.ColorMap);
        Assert.Equal((byte)203, image.ColorMap.GetEntry(200).R);

        // HICL fills only the even slots; entry 3 of the chunk lands at palette index 6.
        Assert.NotNull(image.HighColor);
        Assert.Equal((byte)248, image.HighColor.GetEntry(6).R);
        Assert.Equal((byte)0, image.HighColor.GetEntry(7).R);
    }

    [Fact]
    public void Parse_ExpandsTheLineTable_ForRawAndRunLengthLines()
    {
        // Four pixels wide, two lines: the first raw, the second run-length coded as a run of
        // three 9s and one literal 7.
        var lineTable = new byte[8];
        var body = new List<byte>();
        var rawOffset = 8;
        body.AddRange([5, 6, 7, 8]);
        var rleOffset = rawOffset + 4;
        body.AddRange([0x83, 9, 0x01, 7]);

        BinaryPrimitives.WriteUInt32LittleEndian(lineTable.AsSpan(0), (uint)rawOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(lineTable.AsSpan(4), 0x8000_0000u | (uint)rleOffset);

        byte[] file =
        [
            .. Chunk("BHDR", Header(4, 2, 1, 6)),
            .. Chunk("CMAP", ColorMap()),
            .. Chunk("DATA", [.. lineTable, .. body]),
            .. Chunk("END ")
        ];

        var image = Assert.Single(BsiFile.Parse(file, "FIRE10.BSI").Images);

        Assert.Equal(6, image.Compression);
        var frame = Assert.Single(image.Frames);
        Assert.Equal([5, 6, 7, 8, 9, 9, 9, 7], frame.Indices);
    }

    [Fact]
    public void Parse_ReadsSeveralImagesFromOneFile_EachWithItsOwnPalette()
    {
        var second = ColorMap();
        second[10 * 3] = 0x3F;

        byte[] file =
        [
            .. Chunk("BHDR", Header(2, 1, 1, 0)),
            .. Chunk("CMAP", ColorMap()),
            .. Chunk("DATA", [10, 11]),
            .. Chunk("BHDR", Header(1, 2, 1, 0)),
            .. Chunk("CMAP", second),
            .. Chunk("DATA", [12, 13]),
            .. Chunk("END ")
        ];

        var bsi = BsiFile.Parse(file, "SPMENU.BSI");

        Assert.Equal(2, bsi.Images.Count);
        Assert.Equal((2, 1), (bsi.Images[0].Width, bsi.Images[0].Height));
        Assert.Equal((1, 2), (bsi.Images[1].Width, bsi.Images[1].Height));
        Assert.Equal([10, 11], bsi.Images[0].Frames[0].Indices);
        Assert.Equal([12, 13], bsi.Images[1].Frames[0].Indices);

        // The second image's own CMAP is the one it carries.
        Assert.Equal((byte)8, bsi.Images[0].ColorMap!.GetEntry(10).R);
        Assert.Equal((byte)255, bsi.Images[1].ColorMap!.GetEntry(10).R);
    }

    [Theory]
    [InlineData("TERR02.BSI", true)]
    [InlineData("terr02.bsi", true)]
    [InlineData("ARMOR.3D", false)]
    public void IsBsiFileName_UsesTheExtension(string name, bool expected)
    {
        Assert.Equal(expected, BsiFile.IsBsiFileName(name));
    }

    [Fact]
    public void Parse_RejectsWhatIsNotAnImage()
    {
        // A DOS batch file, like the six stray entries inside the retail BSI.BSA.
        Assert.Throws<InvalidDataException>(() => BsiFile.Parse("@echo off\r\ncopy a b\r\n"u8.ToArray(), "SMP_BAK.BAT"));

        // A chunk whose declared length runs past the file.
        byte[] truncated = [.. Chunk("BHDR", Header(2, 2, 1, 0)), .. Chunk("DATA", 1, 2, 3)];
        truncated[^5] = 0xFF;
        Assert.Throws<InvalidDataException>(() => BsiFile.Parse(truncated, "X.BSI"));

        // Data that does not fill the declared geometry.
        byte[] short_ = [.. Chunk("BHDR", Header(4, 4, 1, 0)), .. Chunk("CMAP", ColorMap()), .. Chunk("DATA", 1, 2), .. Chunk("END ")];
        Assert.Throws<InvalidDataException>(() => BsiFile.Parse(short_, "X.BSI"));

        // A well-formed file that carries no image is legal, not an error: retail's BIP.BSI is
        // eight bytes holding only an END chunk.
        Assert.Empty(BsiFile.Parse(Chunk("END "), "BIP.BSI").Images);
        byte[] noImage = [.. Chunk("IFHD", new byte[44]), .. Chunk("CMAP", ColorMap()), .. Chunk("END ")];
        Assert.Empty(BsiFile.Parse(noImage, "X.BSI").Images);

        // A run-length line that overruns its width.
        var lineTable = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lineTable, 0x8000_0000u | 4u);
        byte[] overrun = [.. Chunk("BHDR", Header(2, 1, 1, 6)), .. Chunk("DATA", [.. lineTable, 0x85, 3]), .. Chunk("END ")];
        Assert.Throws<InvalidDataException>(() => BsiFile.Parse(overrun, "X.BSI"));
    }
}
