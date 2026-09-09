using System.Text;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Formats.Png;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Synthetic vectors for <see cref="ArenaSaveGame" />: a 166,631-byte image authored to the
///     measured layout (six blocks, odd-aligned planes, a 61-byte MHDR payload written by hand to
///     the documented field positions, a 7x5 live region inside sentinel fill, and a level name
///     with residue after its terminator). Every expectation is a literal; none is computed by
///     calling the reader's own arithmetic.
/// </summary>
public sealed class ArenaSaveGameTests
{
    private const int FileLength = 166_631;
    private const int LiveWidth = 7;
    private const int LiveDepth = 5;

    /// <summary>Builds the fixture. Regions are filled so that each block is recognisable on its own.</summary>
    private static byte[] BuildSave(byte[]? paletteOverride = null, string mifName = "abc.mif")
    {
        var file = new byte[FileLength];

        // Screenshot: a diagonal gradient of indices, row-major 320 wide.
        for (var y = 0; y < 200; y++)
        {
            for (var x = 0; x < 320; x++)
            {
                file[y * 320 + x] = (byte)((x + y) & 0xFF);
            }
        }

        // Palette: 6-bit components, entry i = (i & 0x3F, (i * 3) & 0x3F, (i * 7) & 0x3F).
        var palette = paletteOverride ?? BuildPalette();
        palette.CopyTo(file, 64_000);

        // State block: fill with a marker byte, then plant the known fields.
        Array.Fill(file, (byte)0xAA, 64_768, 3_559);

        // Level name: "abc" NUL-terminated, then residue from a longer previous occupant.
        var levelField = Encoding.ASCII.GetBytes("abc\0DEFGHIJ\0");
        levelField.CopyTo(file, 0x1048C);

        var infField = Encoding.ASCII.GetBytes("abc.inf\0");
        infField.CopyTo(file, 0x104AD);

        Array.Fill(file, (byte)0xFF, 0x104BA, 64);

        // MHDR payload, authored to the documented layout: start points at 2..17 (x at 2+2i,
        // y at 10+2i), starting level at 18, declared level count at 19, width at 21, depth at 23.
        var mhdr = new byte[61];
        mhdr[2] = 0x34;
        mhdr[3] = 0x12; // start point 0 x = 0x1234
        mhdr[10] = 0x78;
        mhdr[11] = 0x56; // start point 0 y = 0x5678
        mhdr[18] = 1; // starting level index
        mhdr[19] = 3; // declared level count
        mhdr[21] = LiveWidth; // width
        mhdr[23] = LiveDepth; // depth
        mhdr.CopyTo(file, 0x1053A);

        var mifField = Encoding.ASCII.GetBytes(mifName + "\0");
        mifField.CopyTo(file, 0x10577);

        // MAP1 plane at 0x10AE7: 0xE000 fill, live cells = 0x1000 + column + row * 16.
        WritePlane(file, 0x10AE7, 0xE000, (column, row) => (ushort)(0x1000 + column + row * 16));

        // FLOR plane at 0x18AE7: 0x0000 fill, live cells = 0x2000 + column * 2 + row * 32.
        WritePlane(file, 0x18AE7, 0x0000, (column, row) => (ushort)(0x2000 + column * 2 + row * 32));

        // Memory image: a byte ramp.
        for (var i = 0; i < 32_768; i++)
        {
            file[0x20AE7 + i] = (byte)(i * 3);
        }

        return file;
    }

    private static byte[] BuildPalette()
    {
        var palette = new byte[768];
        for (var i = 0; i < 256; i++)
        {
            palette[i * 3] = (byte)(i & 0x3F);
            palette[i * 3 + 1] = (byte)((i * 3) & 0x3F);
            palette[i * 3 + 2] = (byte)((i * 7) & 0x3F);
        }

        return palette;
    }

    private static void WritePlane(byte[] file, int offset, ushort fill, Func<int, int, ushort> live)
    {
        for (var row = 0; row < 128; row++)
        {
            for (var column = 0; column < 128; column++)
            {
                var value = row < LiveDepth && column < LiveWidth ? live(column, row) : fill;
                var at = offset + row * 256 + column * 2;
                file[at] = (byte)(value & 0xFF);
                file[at + 1] = (byte)(value >> 8);
            }
        }
    }

    [Fact]
    public void Blocks_TileTheFileExactly()
    {
        // Literal offsets and lengths; each block starts where the previous one ends.
        Assert.Equal(0, ArenaSaveGame.ScreenshotOffset);
        Assert.Equal(64_000, ArenaSaveGame.ScreenshotLength);
        Assert.Equal(64_000, ArenaSaveGame.PaletteOffset);
        Assert.Equal(768, ArenaSaveGame.PaletteLength);
        Assert.Equal(64_768, ArenaSaveGame.StateOffset);
        Assert.Equal(3_559, ArenaSaveGame.StateLength);
        Assert.Equal(68_327, ArenaSaveGame.Map1Offset);
        Assert.Equal(32_768, ArenaSaveGame.PlaneLength);
        Assert.Equal(101_095, ArenaSaveGame.FloorOffset);
        Assert.Equal(133_863, ArenaSaveGame.MemoryImageOffset);
        Assert.Equal(32_768, ArenaSaveGame.MemoryImageLength);
        Assert.Equal(166_631, ArenaSaveGame.FileLength);

        Assert.Equal(ArenaSaveGame.PaletteOffset, ArenaSaveGame.ScreenshotOffset + ArenaSaveGame.ScreenshotLength);
        Assert.Equal(ArenaSaveGame.StateOffset, ArenaSaveGame.PaletteOffset + ArenaSaveGame.PaletteLength);
        Assert.Equal(ArenaSaveGame.Map1Offset, ArenaSaveGame.StateOffset + ArenaSaveGame.StateLength);
        Assert.Equal(ArenaSaveGame.FloorOffset, ArenaSaveGame.Map1Offset + ArenaSaveGame.PlaneLength);
        Assert.Equal(ArenaSaveGame.MemoryImageOffset, ArenaSaveGame.FloorOffset + ArenaSaveGame.PlaneLength);
        Assert.Equal(ArenaSaveGame.FileLength, ArenaSaveGame.MemoryImageOffset + ArenaSaveGame.MemoryImageLength);

        // The planes are odd-aligned and the state-block fields sit inside the state block.
        Assert.Equal(1, ArenaSaveGame.Map1Offset % 2);
        Assert.Equal(66_700, ArenaSaveGame.LevelNameOffset);
        Assert.Equal(66_733, ArenaSaveGame.InfNameOffset);
        Assert.Equal(66_746, ArenaSaveGame.FfRunOffset);
        Assert.Equal(66_874, ArenaSaveGame.MifHeaderCopyOffset);
        Assert.Equal(66_935, ArenaSaveGame.MifNameOffset);
        Assert.Equal(ArenaSaveGame.InfNameOffset, ArenaSaveGame.LevelNameOffset + ArenaSaveGame.LevelNameFieldLength);
        Assert.Equal(ArenaSaveGame.FfRunOffset, ArenaSaveGame.InfNameOffset + ArenaSaveGame.InfNameFieldLength);
        Assert.Equal(ArenaSaveGame.MifNameOffset, ArenaSaveGame.MifHeaderCopyOffset + 61);
    }

    [Fact]
    public void IsSaveGame_AcceptsTheFixture()
    {
        Assert.True(ArenaSaveGame.IsSaveGame(BuildSave()));
    }

    [Fact]
    public void IsSaveGame_RejectsWrongLength()
    {
        Assert.False(ArenaSaveGame.IsSaveGame(new byte[FileLength - 1]));
        Assert.False(ArenaSaveGame.IsSaveGame(new byte[FileLength + 1]));
        Assert.False(ArenaSaveGame.IsSaveGame(BuildSave().Take(FileLength - 1).ToArray()));
    }

    [Fact]
    public void IsSaveGame_RejectsAnEightBitPaletteComponent()
    {
        var palette = BuildPalette();
        palette[700] = 0x40;
        Assert.False(ArenaSaveGame.IsSaveGame(BuildSave(palette)));
    }

    [Fact]
    public void IsSaveGame_RejectsAZeroMapDimension()
    {
        var file = BuildSave();
        file[0x1053A + 21] = 0;
        Assert.False(ArenaSaveGame.IsSaveGame(file));
    }

    [Fact]
    public void IsSaveGame_RejectsAMifNameThatIsNotAMif()
    {
        Assert.False(ArenaSaveGame.IsSaveGame(BuildSave(mifName: "abc.inf")));
        // A control character (U+0001) inside the name fails the printable-ASCII gate. Written as an
        // escape so the byte is visible: an earlier revision embedded the raw 0x01 in the literal.
        Assert.False(ArenaSaveGame.IsSaveGame(BuildSave(mifName: "\u0001bc.mif")));

        // An unterminated name past the probe bound is rejected too.
        var file = BuildSave(mifName: "abcdefghijklmnop.mif");
        Assert.False(ArenaSaveGame.IsSaveGame(file));
    }

    [Fact]
    public void Parse_RejectsWrongLength()
    {
        Assert.Throws<InvalidDataException>(() => ArenaSaveGame.Parse(new byte[100], "short"));
    }

    [Fact]
    public void Names_ReadToTheFirstNul_AndKeepNothingAfterIt()
    {
        var save = ArenaSaveGame.Parse(BuildSave(), "SAVEGAME.00");

        Assert.Equal("abc", save.LevelName);
        Assert.Equal("abc.inf", save.InfName);
        Assert.Equal("abc.mif", save.MifName);

        // The residue is still in the raw state block: file 0x1048C + 4 = state index 1936.
        Assert.Equal((byte)'D', save.State[0x1048C + 4 - 64_768]);
    }

    [Fact]
    public void MapHeader_IsParsedFromTheMhdrCopy()
    {
        var save = ArenaSaveGame.Parse(BuildSave(), "SAVEGAME.00");

        Assert.Equal(7, save.MapHeader.Width);
        Assert.Equal(5, save.MapHeader.Depth);
        Assert.Equal(1, save.MapHeader.StartingLevelIndex);
        Assert.Equal(3, save.MapHeader.DeclaredLevelCount);
        Assert.Empty(save.MapHeader.Levels);
        Assert.Equal(0x1234, save.MapHeader.StartPoints[0].X);
        Assert.Equal(0x5678, save.MapHeader.StartPoints[0].Y);
        Assert.True(save.MapHeader.StartPoints[1].IsUnset);
        Assert.Equal(7, save.LiveWidth);
        Assert.Equal(5, save.LiveDepth);
    }

    [Fact]
    public void Planes_ReadAsLittleEndianCellsAt256BytePitch()
    {
        var save = ArenaSaveGame.Parse(BuildSave(), "SAVEGAME.00");

        Assert.Equal(128 * 128, save.Map1.Length);
        Assert.Equal(128 * 128, save.Floor.Length);

        // Live cell (6, 4) of MAP1 = 0x1000 + 6 + 4 * 16 = 0x1046; of FLOR = 0x2000 + 12 + 128 = 0x208C.
        Assert.Equal(0x1046, save.Map1At(6, 4));
        Assert.Equal(0x208C, save.FloorAt(6, 4));
        Assert.Equal(0x1000, save.Map1At(0, 0));
        Assert.Equal(0x2000, save.FloorAt(0, 0));

        // First dead column and first dead row are fill.
        Assert.Equal(0xE000, save.Map1At(7, 0));
        Assert.Equal(0xE000, save.Map1At(0, 5));
        Assert.Equal(0xE000, save.Map1At(127, 127));
        Assert.Equal(0x0000, save.FloorAt(7, 0));
        Assert.Equal(0x0000, save.FloorAt(0, 5));

        // Row-major indexing agrees with the accessor.
        Assert.Equal(save.Map1At(3, 2), save.Map1[3 + 2 * 128]);

        var deadMap1 = 0;
        for (var i = 0; i < save.Map1.Length; i++)
        {
            if (save.Map1[i] == 0xE000)
            {
                deadMap1++;
            }
        }

        Assert.Equal(128 * 128 - 35, deadMap1);
    }

    [Fact]
    public void LiveSubGrids_AreRowMajorAtTheLiveWidth()
    {
        var save = ArenaSaveGame.Parse(BuildSave(), "SAVEGAME.00");

        var map1 = save.GetLiveMap1();
        var floor = save.GetLiveFloor();

        Assert.Equal(35, map1.Length);
        Assert.Equal(35, floor.Length);

        // index = x + z * 7: (x=6, z=4) -> 34.
        Assert.Equal(0x1046, map1[34]);
        Assert.Equal(0x208C, floor[34]);
        // (x=0, z=1) -> 7 = 0x1000 + 16.
        Assert.Equal(0x1010, map1[7]);
        Assert.Equal(0x2020, floor[7]);
        Assert.DoesNotContain((ushort)0xE000, map1);
    }

    [Fact]
    public void Palette_IsExposedRawAndPromotedByShiftingLeftTwo()
    {
        var save = ArenaSaveGame.Parse(BuildSave(), "SAVEGAME.00");

        Assert.Equal(768, save.Palette6Bit.Length);
        // Entry 63: (63, 189 & 63 = 61, 441 & 63 = 57).
        Assert.Equal(63, save.Palette6Bit[63 * 3]);
        Assert.Equal(61, save.Palette6Bit[63 * 3 + 1]);
        Assert.Equal(57, save.Palette6Bit[63 * 3 + 2]);

        // << 2 exactly: 63 -> 252, 61 -> 244, 57 -> 228; alpha opaque.
        var (r, g, b, a) = save.PromotedPalette.GetEntry(63);
        Assert.Equal(252, r);
        Assert.Equal(244, g);
        Assert.Equal(228, b);
        Assert.Equal(255, a);
    }

    [Fact]
    public void Screenshot_IsTheFirst64000BytesAt320x200()
    {
        var save = ArenaSaveGame.Parse(BuildSave(), "SAVEGAME.00");

        Assert.Equal(320, save.Screenshot.Width);
        Assert.Equal(200, save.Screenshot.Height);
        // Pixel (x=5, y=199) = (5 + 199) & 0xFF = 204.
        Assert.Equal(204, save.Screenshot.Indices[199 * 320 + 5]);
    }

    [Fact]
    public void RenderScreenshotPng_WritesA320x200Png()
    {
        var save = ArenaSaveGame.Parse(BuildSave(), "SAVEGAME.00");

        var png = save.RenderScreenshotPng();

        Assert.True(PngImageDecoder.HasPngSignature(png));
        var decoded = PngImageDecoder.Decode(png);
        Assert.Equal(320, decoded.Width);
        Assert.Equal(200, decoded.Height);

        // Pixel (0, 0) is index 0 -> palette entry 0 = (0, 0, 0) promoted; pixel (63, 0) is
        // index 63 -> (252, 244, 228).
        var at = (0 * 320 + 63) * 4;
        Assert.Equal(252, decoded.Pixels[at]);
        Assert.Equal(244, decoded.Pixels[at + 1]);
        Assert.Equal(228, decoded.Pixels[at + 2]);
        Assert.Equal(255, decoded.Pixels[at + 3]);
    }

    [Fact]
    public void StateAndMemoryImage_AreExposedRaw()
    {
        var save = ArenaSaveGame.Parse(BuildSave(), "SAVEGAME.00");

        Assert.Equal(3_559, save.State.Length);
        Assert.Equal(0xAA, save.State[0]);
        Assert.Equal(0xFF, save.State[0x104BA - 64_768]);
        Assert.Equal(0xFF, save.State[0x104BA + 63 - 64_768]);
        Assert.Equal(0xAA, save.State[3_558]);

        Assert.Equal(32_768, save.RawMemoryImage.Length);
        Assert.Equal(0, save.RawMemoryImage[0]);
        Assert.Equal(3, save.RawMemoryImage[1]);
        // (32767 * 3) & 0xFF = 98301 & 0xFF = 253.
        Assert.Equal(253, save.RawMemoryImage[32_767]);
    }
}