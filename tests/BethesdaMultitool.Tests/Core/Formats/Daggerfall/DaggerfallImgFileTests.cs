using System;
using System.Collections.Generic;
using System.IO;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Hand-built vectors for <see cref="DaggerfallImgFile" />. Shapes follow the retail corpus
///     (surveyed 2026-09-02): 191 headered files, every one with pixel-data length ==
///     width*height and file size == 12 + pixel-data length (189 at compression 0x0000, two —
///     FRAM00I0/TALK00I0 — at raw 0x0800); 72 headerless files matched by the 19-entry size
///     table; exactly six 64,768-byte files carrying an appended 6-bit VGA palette; and three
///     12-byte FMAP files with no image data at all.
/// </summary>
public class DaggerfallImgFileTests
{
    private static void WriteI16(List<byte> to, int value)
    {
        to.Add((byte)(value & 0xFF));
        to.Add((byte)((value >> 8) & 0xFF));
    }

    /// <summary>Builds a headered IMG: the 12 header bytes then the data verbatim.</summary>
    private static byte[] BuildHeadered(
        int offsetX,
        int offsetY,
        int width,
        int height,
        int compression,
        byte[] data,
        int? declaredDataLength = null)
    {
        var file = new List<byte>();
        WriteI16(file, offsetX);
        WriteI16(file, offsetY);
        WriteI16(file, width);
        WriteI16(file, height);
        WriteI16(file, compression);
        WriteI16(file, declaredDataLength ?? data.Length);
        file.AddRange(data);
        return [.. file];
    }

    [Fact]
    public void Parse_HeaderedImg_ReadsOffsetsGeometryAndRawPixels()
    {
        // Retail BANK00I0.IMG in miniature: offsets (48, 5), raw pixels, data length == w*h.
        var file = BuildHeadered(48, 5, 3, 2, 0, [1, 2, 3, 4, 5, 6]);

        var parsed = DaggerfallImgFile.Parse(file, "BANK00I0.IMG");

        Assert.True(parsed.HasHeader);
        Assert.Equal(0, parsed.Compression);
        Assert.Null(parsed.EmbeddedPalette);
        Assert.Equal(3, parsed.Bitmap.Width);
        Assert.Equal(2, parsed.Bitmap.Height);
        Assert.Equal(48, parsed.Bitmap.XOffset);
        Assert.Equal(5, parsed.Bitmap.YOffset);
        Assert.Equal([1, 2, 3, 4, 5, 6], parsed.Bitmap.Indices);
    }

    [Fact]
    public void Parse_HeaderedImg_OffsetsAreSigned()
    {
        // -3 encodes as FD FF, -2 as FE FF: the header fields are i16, not u16.
        var file = BuildHeadered(-3, -2, 1, 1, 0, [9]);

        var parsed = DaggerfallImgFile.Parse(file, "SOME00I0.IMG");

        Assert.Equal(-3, parsed.Bitmap.XOffset);
        Assert.Equal(-2, parsed.Bitmap.YOffset);
        Assert.Equal([9], parsed.Bitmap.Indices);
    }

    [Fact]
    public void Parse_Compression0800_DecodesAsRaw()
    {
        // Retail FRAM00I0/TALK00I0 carry 0x0800 — a word absent from the reference's enum —
        // over plainly raw data; the reference never consults the word for IMG and decodes
        // them raw, so this port must too.
        var file = BuildHeadered(0, 0, 2, 2, 0x0800, [1, 2, 3, 4]);

        var parsed = DaggerfallImgFile.Parse(file, "FRAM00I0.IMG");

        Assert.Equal(0x0800, parsed.Compression);
        Assert.Equal([1, 2, 3, 4], parsed.Bitmap.Indices);
    }

    [Fact]
    public void Parse_RleCompressedImg_DecodesRuns()
    {
        // 4x2 RLE image. Stream hand-decoded: 130 > 127 repeats 5 three times (130 - 127);
        // 2 copies three literals 6,7,8 (2 + 1); 129 repeats 9 twice (129 - 127). 3+3+2 = 8.
        var file = BuildHeadered(0, 0, 4, 2, 2, [130, 5, 2, 6, 7, 8, 129, 9]);

        var parsed = DaggerfallImgFile.Parse(file, "FAKE00I0.IMG");

        Assert.Equal(2, parsed.Compression);
        Assert.Equal([5, 5, 5, 6, 7, 8, 9, 9], parsed.Bitmap.Indices);
    }

    [Theory]
    [InlineData(720, 9, 80)]
    [InlineData(4508, 322, 14)]
    [InlineData(26496, 184, 144)]
    [InlineData(64000, 320, 200)]
    [InlineData(112128, 512, 219)]
    public void Parse_HeaderlessSizes_UseTheDimensionTable(int fileSize, int width, int height)
    {
        // No header anywhere: byte 0 is already pixel 0. These sizes all equal width*height.
        var file = new byte[fileSize];
        file[0] = 0xAB;
        file[fileSize - 1] = 0xCD;

        var parsed = DaggerfallImgFile.Parse(file, "AMAP00I0.IMG");

        Assert.False(parsed.HasHeader);
        Assert.Equal(0, parsed.Compression);
        Assert.Equal(width, parsed.Bitmap.Width);
        Assert.Equal(height, parsed.Bitmap.Height);
        Assert.Equal(0, parsed.Bitmap.XOffset);
        Assert.Equal(0, parsed.Bitmap.YOffset);
        Assert.Equal(width * height, parsed.Bitmap.Indices.Length);
        Assert.Equal(0xAB, parsed.Bitmap.Indices[0]);
        Assert.Equal(0xCD, parsed.Bitmap.Indices[(width * height) - 1]);
    }

    [Fact]
    public void HeaderlessTable_IsTheExactNineteenEntryReferencePort()
    {
        var expected = new Dictionary<int, (int Width, int Height)>
        {
            [44] = (22, 22),
            [289] = (17, 17),
            [441] = (49, 9),
            [512] = (32, 16),
            [720] = (9, 80),
            [990] = (45, 22),
            [1720] = (43, 40),
            [2140] = (107, 20),
            [2916] = (81, 36),
            [3200] = (40, 80),
            [3938] = (179, 22),
            [4280] = (107, 40),
            [4508] = (322, 14),
            [20480] = (320, 64),
            [26496] = (184, 144),
            [64000] = (320, 200),
            [64768] = (320, 200),
            [68800] = (320, 215),
            [112128] = (512, 219),
        };

        Assert.Equal(expected.Count, DaggerfallImgFile.HeaderlessDimensionsBySize.Count);
        foreach (var (size, dimensions) in expected)
        {
            Assert.Equal(dimensions, DaggerfallImgFile.HeaderlessDimensionsBySize[size]);
        }
    }

    [Fact]
    public void Parse_HeaderlessEntry44_IsInternallyInconsistent_Throws()
    {
        // The reference maps 44 bytes to 22x22 = 484 pixels — impossible from 44 bytes. No
        // retail IMG has this size; the reference would silently zero-pad, this port throws.
        Assert.Throws<InvalidDataException>(
            () => DaggerfallImgFile.Parse(new byte[44], "TINY00I0.IMG"));
    }

    [Fact]
    public void Parse_EmbeddedPalette_IsReadAfterPixelsAndPromotedFrom6Bit()
    {
        // A 64,768-byte palettized fullscreen: 64,000 raw pixels then 768 bytes of 6-bit VGA.
        // Promotion is (v << 2) | (v >> 4), hand-computed: 63 -> 252|3 = 255; 32 -> 128|2 = 130;
        // 1 -> 4|0 = 4; 10 -> 40|0 = 40; 47 -> 188|2 = 190.
        var file = new byte[64768];
        file[0] = 5;
        file[63999] = 6;
        file[64000] = 0;   // entry 0: (0, 0, 0)
        file[64001] = 0;
        file[64002] = 0;
        file[64003] = 63;  // entry 1: (63, 32, 1)
        file[64004] = 32;
        file[64005] = 1;
        file[64006] = 10;  // entry 2: (10, 47, 63)
        file[64007] = 47;
        file[64008] = 63;

        var parsed = DaggerfallImgFile.Parse(file, "TITL00I0.IMG");

        Assert.False(parsed.HasHeader);
        Assert.Equal(320, parsed.Bitmap.Width);
        Assert.Equal(200, parsed.Bitmap.Height);
        Assert.Equal(5, parsed.Bitmap.Indices[0]);
        Assert.Equal(6, parsed.Bitmap.Indices[63999]);
        Assert.NotNull(parsed.EmbeddedPalette);
        Assert.Equal(((byte)0, (byte)0, (byte)0, (byte)255), parsed.EmbeddedPalette.GetEntry(0));
        Assert.Equal(((byte)255, (byte)130, (byte)4, (byte)255), parsed.EmbeddedPalette.GetEntry(1));
        Assert.Equal(((byte)40, (byte)190, (byte)255, (byte)255), parsed.EmbeddedPalette.GetEntry(2));
    }

    [Fact]
    public void Parse_64768FileWithNonPalettizedName_IgnoresTrailingBytes()
    {
        // The embedded palette is keyed by NAME, not by the 64,768-byte size: any other name
        // decodes the 320x200 pixels and ignores the 768-byte tail, as in the reference.
        var file = new byte[64768];
        file[0] = 0x11;

        var parsed = DaggerfallImgFile.Parse(file, "FAKE00I0.IMG");

        Assert.Null(parsed.EmbeddedPalette);
        Assert.Equal(64000, parsed.Bitmap.Indices.Length);
        Assert.Equal(0x11, parsed.Bitmap.Indices[0]);
    }

    [Theory]
    [InlineData("CHGN00I0.IMG")]
    [InlineData("DIE_00I0.IMG")]
    [InlineData("PICK02I0.IMG")]
    [InlineData("PICK03I0.IMG")]
    [InlineData("PRIS00I0.IMG")]
    [InlineData("TITL00I0.IMG")]
    public void HasEmbeddedPalette_MatchesTheSixReferenceNames(string name)
    {
        Assert.True(DaggerfallImgFile.HasEmbeddedPalette(name));
        Assert.True(DaggerfallImgFile.HasEmbeddedPalette(name.ToLowerInvariant()));
        Assert.Equal(string.Empty, DaggerfallImgFile.GetPaletteFileName(name));
    }

    [Fact]
    public void HasEmbeddedPalette_IsFalseForOtherNames()
    {
        Assert.False(DaggerfallImgFile.HasEmbeddedPalette("AMAP00I0.IMG"));
        Assert.False(DaggerfallImgFile.HasEmbeddedPalette("PICK01I0.IMG"));
    }

    [Fact]
    public void Parse_PalettizedNameWithoutPaletteBytes_Throws()
    {
        // 64,000 bytes is a valid headerless size, but a palettized name must carry 768 more.
        Assert.Throws<InvalidDataException>(
            () => DaggerfallImgFile.Parse(new byte[64000], "TITL00I0.IMG"));
    }

    [Theory]
    [InlineData("FMAP0I00.IMG")]
    [InlineData("FMAP0I01.IMG")]
    [InlineData("FMAP0I16.IMG")]
    public void Parse_UnsupportedFmapNames_Throw(string name)
    {
        // Retail: each is exactly 12 bytes of zeroed header, no image.
        Assert.Throws<NotSupportedException>(
            () => DaggerfallImgFile.Parse(new byte[12], name));
        Assert.True(DaggerfallImgFile.IsUnsupported(name));
    }

    [Fact]
    public void Parse_TruncatedHeader_Throws()
    {
        // 8 bytes: not a headerless size, too small for the 12-byte header.
        Assert.Throws<InvalidDataException>(
            () => DaggerfallImgFile.Parse(new byte[8], "SOME00I0.IMG"));
    }

    [Fact]
    public void Parse_TruncatedPixelData_Throws()
    {
        // Header promises 4x2 = 8 pixels but only 5 data bytes exist (17-byte file, not a
        // headerless size).
        var file = BuildHeadered(0, 0, 4, 2, 0, [1, 2, 3, 4, 5], declaredDataLength: 8);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallImgFile.Parse(file, "SOME00I0.IMG"));
    }

    [Fact]
    public void Parse_EmptyGeometry_Throws()
    {
        var file = BuildHeadered(0, 0, 0, 5, 0, []);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallImgFile.Parse(file, "SOME00I0.IMG"));
    }

    [Fact]
    public void Parse_TruncatedRleStream_ThrowsInsteadOfSpinning()
    {
        // 2x2 RLE image whose stream ends after one literal pixel (0 copies 0+1 byte): the
        // decoder must throw at end of input, never loop waiting for pixels.
        var file = BuildHeadered(0, 0, 2, 2, 2, [0, 9]);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallImgFile.Parse(file, "FAKE00I0.IMG"));
    }

    [Fact]
    public void Parse_RleWithNoStreamBytes_Throws()
    {
        // Header claims RLE but the file ends at the header: first code-byte read must throw.
        var file = BuildHeadered(0, 0, 1, 1, 2, []);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallImgFile.Parse(file, "FAKE00I0.IMG"));
    }

    [Fact]
    public void Parse_RleRunOverflowingTheImage_Throws()
    {
        // 2x1 image, but 130 repeats 3 pixels (130 - 127): one more than the image holds.
        var file = BuildHeadered(0, 0, 2, 1, 2, [130, 5]);

        Assert.Throws<InvalidDataException>(
            () => DaggerfallImgFile.Parse(file, "FAKE00I0.IMG"));
    }

    [Theory]
    [InlineData("FMAPAI00.IMG", "FMAP_PAL.COL")]
    [InlineData("NITE01I0.IMG", "NIGHTSKY.COL")]
    [InlineData("nite01i0.img", "NIGHTSKY.COL")]
    [InlineData("DANK02I0.IMG", "DANKBMAP.COL")]
    [InlineData("TMAP00I0.IMG", "MAP.PAL")]
    [InlineData("BANK00I0.IMG", "ART_PAL.COL")]
    [InlineData("AMAP00I0.IMG", "ART_PAL.COL")]
    public void GetPaletteFileName_FollowsTheReferenceRouting(string name, string palette)
    {
        Assert.Equal(palette, DaggerfallImgFile.GetPaletteFileName(name));
    }

    [Fact]
    public void IsImgFileName_MatchesExtensionCaseInsensitively()
    {
        Assert.True(DaggerfallImgFile.IsImgFileName("AMAP00I0.IMG"));
        Assert.True(DaggerfallImgFile.IsImgFileName("amap00i0.img"));
        Assert.False(DaggerfallImgFile.IsImgFileName("TEXTURE.010"));
        Assert.False(DaggerfallImgFile.IsImgFileName("FACES.CIF"));
    }
}
