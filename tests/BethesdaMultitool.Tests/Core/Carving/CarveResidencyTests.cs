using BethesdaMultitool.Core.Carving;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Core.Formats.Png;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Carving;

/// <summary>
///     The carver has always known how many bytes of a file were resident, but only that scalar
///     survived into the manifest — so a zero-filled hole was indistinguishable from a run of
///     legitimate zeros, and a file cut short at its end read the same as one damaged in the middle.
///     These pin the interval arithmetic and the per-format "did the gap hit something structural"
///     verdicts built on top of it.
/// </summary>
public sealed class CarveResidencyTests
{
    [Fact]
    public void FromPresentRuns_FullCoverage_IsComplete()
    {
        var residency = CarveResidency.FromPresentRuns([new CarveHole(0, 64)], 64);

        Assert.False(residency.IsPartial);
        Assert.Empty(residency.Holes);
        Assert.Equal(1.0, residency.Coverage);
    }

    [Fact]
    public void FromPresentRuns_ShortRun_IsTailTruncatedWithNoInteriorHole()
    {
        var residency = CarveResidency.FromPresentRuns([new CarveHole(0, 48)], 64);

        Assert.True(residency.TailTruncated);
        Assert.Equal(0, residency.InteriorHoleCount);
        Assert.Equal([new CarveHole(48, 16)], residency.Holes);
        Assert.Equal(0.75, residency.Coverage);
    }

    [Fact]
    public void FromPresentRuns_GapBetweenRuns_IsAnInteriorHole()
    {
        var residency = CarveResidency.FromPresentRuns(
            [new CarveHole(0, 16), new CarveHole(32, 32)], 64);

        Assert.False(residency.TailTruncated);
        Assert.Equal(1, residency.InteriorHoleCount);
        Assert.Equal([new CarveHole(16, 16)], residency.Holes);
        Assert.Equal(0.75, residency.Coverage);
    }

    [Fact]
    public void FromPresentRuns_LeadingGap_IsCountedFromZero()
    {
        var residency = CarveResidency.FromPresentRuns([new CarveHole(8, 56)], 64);

        Assert.Equal([new CarveHole(0, 8)], residency.Holes);
        Assert.False(residency.TailTruncated);
    }

    [Fact]
    public void FromPresentRuns_NoRunsAtAll_IsOneWholeFileHole()
    {
        var residency = CarveResidency.FromPresentRuns([], 64);

        Assert.Equal([new CarveHole(0, 64)], residency.Holes);
        Assert.True(residency.TailTruncated);
        Assert.Equal(0.0, residency.Coverage);
    }

    [Theory]
    [InlineData(0, 4, true)] // overlaps the front
    [InlineData(16, 4, false)] // entirely after
    [InlineData(6, 2, true)] // entirely inside
    [InlineData(10, 8, false)] // starts at the exclusive end
    public void Overlaps_IsHalfOpen(int start, int length, bool expected)
    {
        Assert.Equal(expected, GapAssessment.Overlaps([new CarveHole(2, 8)], start, length));
    }

    [Fact]
    public void MissingWithin_SumsOnlyTheOverlappingPart()
    {
        Assert.Equal(6, GapAssessment.MissingWithin([new CarveHole(4, 8), new CarveHole(20, 4)], 6, 8));
    }

    [Fact]
    public void DdxAssessor_NamesTheHeaderThenTheFirstStream()
    {
        var format = new DdxFormat();
        var data = new byte[0x44 + 256];
        // BE32 @0x40 = first stream's compressed length.
        data[0x43] = 0x80; // 128 bytes

        Assert.Contains("header", format.AssessGaps(data, [new CarveHole(0x10, 4)], null));
        Assert.Contains("first LZX stream", format.AssessGaps(data, [new CarveHole(0x50, 4)], null));
        // Past the first stream: only tail mips are at risk, which is not a usability verdict.
        Assert.Null(format.AssessGaps(data, [new CarveHole(0x44 + 200, 4)], null));
    }

    [Fact]
    public void DdsAssessor_FlagsOnlyTheHeader()
    {
        var format = new DdsFormat();
        var data = new byte[1024];

        Assert.Contains("DDS header", format.AssessGaps(data, [new CarveHole(64, 4)], null));
        Assert.Null(format.AssessGaps(data, [new CarveHole(512, 4)], null));
    }

    [Fact]
    public void PngAssessor_TreatsTheWholeImageStreamAsUnresynchronisable()
    {
        var format = new PngFormat();
        var data = new byte[512];

        Assert.Contains("IHDR", format.AssessGaps(data, [new CarveHole(8, 4)], null));
        Assert.Contains("image stream", format.AssessGaps(data, [new CarveHole(200, 4)], null));
        // The trailing IEND carries no image data.
        Assert.Null(format.AssessGaps(data, [new CarveHole(504, 8)], null));
    }
}
