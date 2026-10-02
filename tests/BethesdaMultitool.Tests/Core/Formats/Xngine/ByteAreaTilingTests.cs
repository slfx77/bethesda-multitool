using BethesdaMultitool.Core.Formats.Xngine;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine;

/// <summary>
///     The area-tiling helper the cut-1c plan names (section 8, slice 4): named byte areas tile a
///     length, and what is left over is reported as <c>unclaimed:{start}-{end}</c> gaps, overlaps and
///     out-of-range areas. Each case pins one outcome a looser helper would get wrong: a gap missed,
///     an overlap swallowed, an out-of-range area silently clamped, or an empty area miscounted.
/// </summary>
public sealed class ByteAreaTilingTests
{
    [Fact]
    public void Compute_ReportsAnExactTiling_WithNothingLeftOver()
    {
        var tiling = ByteAreaTiling.Compute(200,
            [new ByteArea("header", 0, 64), new ByteArea("points", 64, 100), new ByteArea("planes", 100, 200)]);

        Assert.True(tiling.TilesExactly);
        Assert.Empty(tiling.Gaps);
        Assert.Empty(tiling.Overlaps);
        Assert.Empty(tiling.OutOfRange);
        Assert.Equal(0, tiling.UnclaimedBytes);
        Assert.Equal(200, tiling.Length);
        Assert.Equal(["header", "points", "planes"], tiling.Areas.Select(a => a.Name));
    }

    [Fact]
    public void Compute_NamesEveryGap_InFileOrder_IncludingTheTrailingOne()
    {
        var tiling = ByteAreaTiling.Compute(200, [new ByteArea("header", 0, 64), new ByteArea("points", 100, 150)]);

        Assert.False(tiling.TilesExactly);
        Assert.Equal(
            [new ByteArea("unclaimed:64-100", 64, 100), new ByteArea("unclaimed:150-200", 150, 200)],
            tiling.Gaps);
        Assert.Equal(86, tiling.UnclaimedBytes);
        Assert.Empty(tiling.Overlaps);
    }

    [Fact]
    public void Compute_ReportsAnOverlap_AndDoesNotCountItAsCoverage()
    {
        // The second area starts 32 bytes before the first ends: that is the wrong-frame-width
        // signature the .3DC acceptance rejects, and it must surface, never be folded into coverage.
        var tiling = ByteAreaTiling.Compute(100, [new ByteArea("a", 0, 64), new ByteArea("b", 32, 100)]);

        Assert.Equal([new ByteAreaOverlap("b", 32, 64)], tiling.Overlaps);
        Assert.Empty(tiling.Gaps);
        Assert.False(tiling.TilesExactly);
    }

    [Fact]
    public void Compute_SetsAsideAreasThatDoNotFit_InsteadOfClampingThem()
    {
        var tiling = ByteAreaTiling.Compute(200,
        [
            new ByteArea("header", 0, 64),
            new ByteArea("before-start", -1, 10),
            new ByteArea("past-end", 90, 300),
            new ByteArea("inverted", 80, 70),
            new ByteArea("points", 64, 200)
        ]);

        Assert.Equal(["before-start", "past-end", "inverted"], tiling.OutOfRange.Select(a => a.Name));
        Assert.Equal(["header", "points"], tiling.Areas.Select(a => a.Name));
        Assert.Empty(tiling.Gaps);
        Assert.False(tiling.TilesExactly);
    }

    [Fact]
    public void Compute_IgnoresEmptyAreas_ForGapsAndOverlaps_ButListsThem()
    {
        // A mesh with zero planes declares a zero-length normal list; it claims nothing and must
        // neither open a gap nor be reported as overlapping the area it sits inside.
        var tiling = ByteAreaTiling.Compute(100,
            [new ByteArea("a", 0, 64), new ByteArea("normals", 20, 20), new ByteArea("b", 64, 100)]);

        Assert.True(tiling.TilesExactly);
        Assert.Equal(["a", "normals", "b"], tiling.Areas.Select(a => a.Name));
        Assert.Equal(0, tiling.Areas[1].Length);
    }

    [Fact]
    public void Compute_SortsAreasByStart_WhateverOrderTheyArrive()
    {
        var tiling = ByteAreaTiling.Compute(64,
            [new ByteArea("late", 40, 64), new ByteArea("early", 0, 20), new ByteArea("middle", 20, 40)]);

        Assert.Equal(["early", "middle", "late"], tiling.Areas.Select(a => a.Name));
        Assert.True(tiling.TilesExactly);
    }

    [Fact]
    public void Compute_RejectsANegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ByteAreaTiling.Compute(-1, []));
    }

    [Fact]
    public void ByteArea_LengthIsEndMinusStart()
    {
        Assert.Equal(36, new ByteArea("points", 64, 100).Length);
        Assert.Equal(0, new ByteArea("empty", 5, 5).Length);
    }
}
