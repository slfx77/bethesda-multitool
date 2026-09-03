using System;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Vectors for <see cref="DaggerfallSkyFile" />. The layout is fixed with no header, so the
///     tests pin the offsets: 32 COL-shaped palettes from 0, frames from 549,120, and the
///     frame-to-palette pairing that drives the time-of-day shading.
/// </summary>
public class DaggerfallSkyFileTests
{
    private static byte[] BuildSky()
    {
        var file = new byte[DaggerfallSkyFile.FileLength];

        // Each palette block is a COL: u32 length 776, u32 magic 0xB123, 768 RGB bytes.
        for (var p = 0; p < DaggerfallSkyFile.PaletteCount; p++)
        {
            var at = p * Palette.ColFileLength;
            file[at] = 0x08;
            file[at + 1] = 0x03;
            file[at + 4] = 0x23;
            file[at + 5] = 0xB1;

            // Entry 1's red channel carries the palette index, so pairing is observable.
            file[at + 8 + 3] = (byte)(100 + p);
        }

        // Frame i's first pixel carries its frame index.
        for (var f = 0; f < DaggerfallSkyFile.FrameCount; f++)
        {
            file[DaggerfallSkyFile.ImageDataOffset + (f * DaggerfallSkyFile.FrameWidth * DaggerfallSkyFile.FrameHeight)] = (byte)f;
        }

        return file;
    }

    [Fact]
    public void FileLength_MatchesTheRetailSize()
    {
        Assert.Equal(7_758_080, DaggerfallSkyFile.FileLength);
        Assert.Equal(549_120, DaggerfallSkyFile.ImageDataOffset);
    }

    [Fact]
    public void Parse_ReadsThirtyTwoPalettesFromTheStart()
    {
        var sky = DaggerfallSkyFile.Parse(BuildSky(), "SKY00.DAT");

        Assert.Equal(32, sky.Palettes.Count);
        Assert.Equal(100, sky.Palettes[0].GetEntry(1).R);
        Assert.Equal(131, sky.Palettes[31].GetEntry(1).R);
    }

    [Fact]
    public void Parse_ReadsSixtyFourFramesAtTheImageOffset()
    {
        var sky = DaggerfallSkyFile.Parse(BuildSky(), "SKY00.DAT");

        Assert.Equal(64, sky.Frames.Count);
        Assert.All(sky.Frames, f =>
        {
            Assert.Equal(512, f.Width);
            Assert.Equal(220, f.Height);
        });
        Assert.Equal(0, sky.Frames[0].Indices[0]);
        Assert.Equal(63, sky.Frames[63].Indices[0]);
    }

    [Fact]
    public void GetFrame_AddressesWestThenEast()
    {
        var sky = DaggerfallSkyFile.Parse(BuildSky(), "SKY00.DAT");

        Assert.Equal(5, sky.GetFrame(0, 5).Indices[0]);
        Assert.Equal(32 + 5, sky.GetFrame(1, 5).Indices[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => sky.GetFrame(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sky.GetFrame(0, 32));
    }

    [Fact]
    public void PaletteFor_PairsBothHalvesWithTheSameTimeOfDayPalette()
    {
        var sky = DaggerfallSkyFile.Parse(BuildSky(), "SKY00.DAT");

        // Frame 7 (west) and frame 39 (east) are the same step and share palette 7.
        Assert.Equal(107, sky.PaletteFor(7).GetEntry(1).R);
        Assert.Equal(107, sky.PaletteFor(39).GetEntry(1).R);
        Assert.Throws<ArgumentOutOfRangeException>(() => sky.PaletteFor(64));
    }

    [Fact]
    public void Parse_WrongLength_Throws()
    {
        Assert.Throws<InvalidDataException>(() => DaggerfallSkyFile.Parse(new byte[1000], "SKY00.DAT"));
    }

    [Theory]
    [InlineData("SKY00.DAT", true)]
    [InlineData("sky31.dat", true)]
    [InlineData("SKYPAL.DAT", false)] // a palette pair beside the sets, not a set
    [InlineData("SKY.DAT", false)]
    [InlineData("SKY000.DAT", false)]
    public void IsSkyFileName_RequiresExactlyTwoDigits(string name, bool expected)
    {
        Assert.Equal(expected, DaggerfallSkyFile.IsSkyFileName(name));
    }
}
