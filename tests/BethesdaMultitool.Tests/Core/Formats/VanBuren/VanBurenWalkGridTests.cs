using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.VanBuren;
using BethesdaMultitool.Core.Rendering.Level2D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for the Van Buren run-length walk grid, shaped after the 39 shipped grids
///     (measured 2026-09-08): a width, a height, and <c>(u32 count, u8 value)</c> runs.
/// </summary>
public sealed class VanBurenWalkGridTests
{
    private static byte[] Grid(uint width, uint height, params (uint Count, byte Value)[] runs)
    {
        var b = new byte[8 + runs.Length * 5];
        BinaryPrimitives.WriteUInt32LittleEndian(b, width);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), height);
        var at = 8;
        foreach (var (count, value) in runs)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), count);
            b[at + 4] = value;
            at += 5;
        }

        return b;
    }

    /// <summary>A 6x4 grid with a border of 47 around an interior of 0, as a shipped grid is shaped.</summary>
    private static byte[] Bordered()
    {
        return Grid(6, 4, (6, 47), (1, 47), (4, 0), (1, 47), (1, 47), (4, 0), (1, 47), (6, 47));
    }

    [Fact]
    public void RunsAreCountThenValueAndFillTheGridRowMajor()
    {
        var grid = VanBurenWalkGrid.Parse(Bordered(), "small.rle");

        Assert.Equal(6, grid.Width);
        Assert.Equal(4, grid.Height);
        Assert.Equal(8, grid.RunCount);
        Assert.Equal(47, grid.At(0, 0));
        Assert.Equal(0, grid.At(1, 1));
        Assert.Equal(47, grid.At(5, 1));
        Assert.Equal(47, grid.At(3, 3));
        // The interior is four columns by two rows; the other sixteen cells form the border.
        Assert.Equal(8, grid.Cells.Count(c => c == 0));
        Assert.Equal(16, grid.Cells.Count(c => c == VanBurenWalkGrid.Blocked));
    }

    [Fact]
    public void TheValueFirstReadingIsRefused()
    {
        // ⚠ (u8 value, u32 count) was the first reading tried; it lands one row short on every
        // shipped grid. Written that way, the same runs must not tile.
        var b = Bordered();
        for (var at = 8; at < b.Length; at += 5)
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));
            var value = b[at + 4];
            b[at] = value;
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 1), count);
        }

        Assert.False(VanBurenWalkGrid.TryParse(b, "swapped.rle", out _, out var error));
        Assert.Contains("overflows", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RunsThatFallShortOfTheGridAreRefused()
    {
        var b = Grid(6, 4, (6, 47), (17, 0));

        Assert.False(VanBurenWalkGrid.TryParse(b, "short.rle", out _, out var error));
        Assert.Contains("23 of 24", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RunsThatOverflowTheGridAreRefused()
    {
        Assert.Throws<InvalidDataException>(() => VanBurenWalkGrid.Parse(Grid(6, 4, (25, 0)), "long.rle"));
    }

    [Fact]
    public void APayloadThatIsNotAWholeNumberOfRunsIsRefused()
    {
        var b = Bordered().Concat(new byte[] { 0, 0 }).ToArray();

        Assert.False(VanBurenWalkGrid.IsWalkGrid(b));
    }

    [Fact]
    public void TheProbeIsTheTilingItselfBecauseThereIsNoMagic()
    {
        Assert.True(VanBurenWalkGrid.IsWalkGrid(Bordered()));
        // A texture header is dwords too; its "runs" cannot fill a 0x00020000-wide grid.
        var tga = new byte[18 + 30];
        tga[2] = 2;
        Assert.False(VanBurenWalkGrid.IsWalkGrid(tga));
        Assert.False(VanBurenWalkGrid.IsWalkGrid(Grid(0, 4, (1, 0))));
    }

    [Fact]
    public void TheDiagnosticColourKeepsTheBorderDarkAndGivesValueZeroAHue()
    {
        // Value 0 is an OPEN interior on several shipped grids, so it cannot take the black that
        // VoxelLayerRasterizer.ColorFor gives id 0; the border value is the one drawn dark.
        var border = VanBurenMapLevel2DSource.ColourFor(VanBurenWalkGrid.Blocked);
        var open = VanBurenMapLevel2DSource.ColourFor(0);

        Assert.True(border.R < 40 && border.G < 40 && border.B < 40);
        Assert.True(open.R + open.G + open.B > 200);
        Assert.NotEqual(open, VanBurenMapLevel2DSource.ColourFor(9));
    }
}
