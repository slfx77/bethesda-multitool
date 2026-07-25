using BethesdaMultitool.Core.Coverage;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Coverage;

/// <summary>
///     Characterization tests for <see cref="CoverageAnalyzer.FindGaps" /> after converting the nested
///     per-region full re-scan (O(regions * recognized)) into a forward-cursor two-pointer sweep
///     (O(regions + recognized)). Inputs mirror the caller's contract: both lists sorted by Start,
///     recognized intervals merged (non-overlapping).
/// </summary>
public class CoverageAnalyzerFindGapsTests
{
    private static CoverageInterval Region(long start, long end)
    {
        return new CoverageInterval(start, end, CoverageCategory.Region);
    }

    private static CoverageInterval Recognized(long start, long end)
    {
        return new CoverageInterval(start, end, CoverageCategory.Region);
    }

    private static (long Offset, long Size)[] Gaps(
        List<CoverageInterval> regions, List<CoverageInterval> recognized)
    {
        return CoverageAnalyzer.FindGaps(regions, recognized)
            .Select(g => (g.FileOffset, g.Size))
            .ToArray();
    }

    [Fact]
    public void FindGaps_NoRecognized_WholeRegionIsAGap()
    {
        Assert.Equal([(0L, 100L)], Gaps([Region(0, 100)], []));
    }

    [Fact]
    public void FindGaps_FullyCovered_NoGaps()
    {
        Assert.Empty(Gaps([Region(0, 100)], [Recognized(0, 100)]));
    }

    [Fact]
    public void FindGaps_RecognizedInMiddle_GapsOnBothSides()
    {
        Assert.Equal([(0L, 20L), (40L, 60L)], Gaps([Region(0, 100)], [Recognized(20, 40)]));
    }

    [Fact]
    public void FindGaps_MultipleRecognized_GapsBetween()
    {
        Assert.Equal(
            [(30L, 30L), (80L, 20L)],
            Gaps([Region(0, 100)], [Recognized(0, 30), Recognized(60, 80)]));
    }

    [Fact]
    public void FindGaps_MultipleRegions_CursorResumesForward()
    {
        Assert.Equal(
            [(0L, 20L), (30L, 20L), (100L, 50L)],
            Gaps([Region(0, 50), Region(100, 150)], [Recognized(20, 30)]));
    }

    [Fact]
    public void FindGaps_RecognizedSpansRegionBoundary_NotConsumedEarly()
    {
        // [40,60) overlaps both regions; the forward cursor must not advance past it after region 1.
        Assert.Equal(
            [(0L, 40L), (60L, 40L)],
            Gaps([Region(0, 50), Region(50, 100)], [Recognized(40, 60)]));
    }
}