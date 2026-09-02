using System;
using BethesdaMultitool.Core.Formats.Arena;
using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Vectors for <see cref="ArenaLightTable" />, the 13x256 palette-remap table Arena uses for
///     distance shading. Shapes match the retail files (both are exactly 3,328 bytes).
/// </summary>
public class ArenaLightTableTests
{
    /// <summary>Level 0 is the identity, and each later level shifts every index up by its level.</summary>
    private static byte[] BuildTable()
    {
        var bytes = new byte[ArenaLightTable.FileLength];
        for (var level = 0; level < ArenaLightTable.LevelCount; level++)
        {
            for (var i = 0; i < ArenaLightTable.EntriesPerLevel; i++)
            {
                bytes[(level * ArenaLightTable.EntriesPerLevel) + i] = (byte)((i + level) & 0xFF);
            }
        }

        return bytes;
    }

    [Fact]
    public void FileLength_MatchesTheRetailFileSize()
    {
        // NORMAL.LGT and FOG.LGT are both exactly this size on disk.
        Assert.Equal(3328, ArenaLightTable.FileLength);
        Assert.Equal(13, ArenaLightTable.LevelCount);
        Assert.Equal(256, ArenaLightTable.EntriesPerLevel);
    }

    [Fact]
    public void Parse_WrongLength_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaLightTable.Parse(new byte[ArenaLightTable.FileLength - 1]));
        Assert.Throws<InvalidDataException>(() => ArenaLightTable.Parse(new byte[ArenaLightTable.FileLength + 1]));
    }

    [Fact]
    public void Level_ReturnsThatRowsRemapEntries()
    {
        var table = ArenaLightTable.Parse(BuildTable());

        Assert.Equal(0, table.Level(0)[0]);
        Assert.Equal(200, table.Level(0)[200]);
        Assert.Equal(203, table.Level(3)[200]);
    }

    [Fact]
    public void Level_OutOfRange_Throws()
    {
        var table = ArenaLightTable.Parse(BuildTable());

        Assert.Throws<ArgumentOutOfRangeException>(() => table.Level(-1).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => table.Level(ArenaLightTable.LevelCount).ToArray());
    }

    [Fact]
    public void IsIdentity_TrueOnlyForTheUnshiftedRow()
    {
        var table = ArenaLightTable.Parse(BuildTable());

        Assert.True(table.IsIdentity(0));
        Assert.False(table.IsIdentity(1));
    }

    [Fact]
    public void Apply_RemapsEveryPixelAndKeepsOffsets()
    {
        var table = ArenaLightTable.Parse(BuildTable());
        var source = new IndexedBitmap(2, 1, [10, 20], xOffset: 7, yOffset: 9);

        var shaded = table.Apply(source, 5);

        Assert.Equal([15, 25], shaded.Indices);
        Assert.Equal(7, shaded.XOffset);
        Assert.Equal(9, shaded.YOffset);

        // The source is untouched.
        Assert.Equal([10, 20], source.Indices);
    }

    [Fact]
    public void Shade_MovesColoursButKeepsTheSourceIndexAlpha()
    {
        var rgb = new byte[Palette.RgbByteCount];
        for (var i = 0; i < Palette.EntryCount; i++)
        {
            rgb[i * 3] = (byte)i; // red channel carries the index, so a remap is visible
        }

        var palette = Palette.FromRgb8(rgb).WithTransparentIndex(4);
        var table = ArenaLightTable.Parse(BuildTable());

        var shaded = table.Shade(palette, 2);

        // Entry 4 now shows entry 6's colour...
        Assert.Equal(6, shaded.GetEntry(4).R);

        // ...but transparency belongs to the source pixel, so index 4 stays transparent.
        Assert.Equal(0, shaded.GetEntry(4).A);
        Assert.Equal(255, shaded.GetEntry(5).A);
    }

    [Fact]
    public void Shade_AtAnIdentityLevel_LeavesThePaletteUnchanged()
    {
        var rgb = new byte[Palette.RgbByteCount];
        for (var i = 0; i < rgb.Length; i++)
        {
            rgb[i] = (byte)(i * 5);
        }

        var palette = Palette.FromRgb8(rgb);
        var table = ArenaLightTable.Parse(BuildTable());

        Assert.Equal(palette.Rgba.ToArray(), table.Shade(palette, 0).Rgba.ToArray());
    }

    [Fact]
    public void Remapped_WrongMapLength_Throws()
    {
        var palette = Palette.FromRgb8(new byte[Palette.RgbByteCount]);

        Assert.Throws<ArgumentException>(() => palette.Remapped(new byte[10]));
    }
}
