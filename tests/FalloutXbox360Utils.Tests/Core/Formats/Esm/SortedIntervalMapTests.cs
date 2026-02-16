using FalloutXbox360Utils.Core.Formats.Esm;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

public class SortedIntervalMapTests
{
    private static GrupHeaderInfo MakeGrup(long offset, uint size, uint labelFormId, int groupType = 1)
    {
        var label = BitConverter.GetBytes(labelFormId); // LE bytes
        return new GrupHeaderInfo
        {
            Offset = offset,
            GroupSize = size,
            Label = label,
            GroupType = groupType
        };
    }

    [Fact]
    public void FindContainingInterval_BasicContainment_ReturnsCorrectIndex()
    {
        // Three non-overlapping intervals:
        //   [100..200), [300..500), [600..700)
        var groups = new List<GrupHeaderInfo>
        {
            MakeGrup(100, 100, 0xAAAA),
            MakeGrup(300, 200, 0xBBBB),
            MakeGrup(600, 100, 0xCCCC)
        };
        var map = new SortedIntervalMap(groups);

        Assert.Equal(3, map.Count);

        // Inside first interval
        Assert.Equal(0, map.FindContainingInterval(150));
        // Inside second interval
        Assert.Equal(1, map.FindContainingInterval(400));
        // Inside third interval
        Assert.Equal(2, map.FindContainingInterval(650));
    }

    [Fact]
    public void FindContainingInterval_BoundaryExclusion_ReturnsMinusOne()
    {
        // Interval [100..200) — exclusive on both ends to match Build*Map logic
        var groups = new List<GrupHeaderInfo> { MakeGrup(100, 100, 0x1111) };
        var map = new SortedIntervalMap(groups);

        // Offset exactly at start (the GRUP header itself) — excluded
        Assert.Equal(-1, map.FindContainingInterval(100));
        // Offset exactly at end — excluded
        Assert.Equal(-1, map.FindContainingInterval(200));
        // Just inside start
        Assert.Equal(0, map.FindContainingInterval(101));
        // Just inside end
        Assert.Equal(0, map.FindContainingInterval(199));
    }

    [Fact]
    public void FindContainingInterval_EmptyInput_ReturnsMinusOne()
    {
        var map = new SortedIntervalMap([]);
        Assert.Equal(0, map.Count);
        Assert.Equal(-1, map.FindContainingInterval(50));
    }

    [Fact]
    public void FindContainingInterval_SingleInterval_Works()
    {
        var groups = new List<GrupHeaderInfo> { MakeGrup(1000, 500, 0xDEAD) };
        var map = new SortedIntervalMap(groups);

        Assert.Equal(1, map.Count);
        Assert.Equal(0, map.FindContainingInterval(1250));
        Assert.Equal(-1, map.FindContainingInterval(999));
        Assert.Equal(-1, map.FindContainingInterval(1500));
    }

    [Fact]
    public void FindContainingInterval_GapBetweenIntervals_ReturnsMinusOne()
    {
        // [100..200) gap [300..400)
        var groups = new List<GrupHeaderInfo>
        {
            MakeGrup(100, 100, 0xAA),
            MakeGrup(300, 100, 0xBB)
        };
        var map = new SortedIntervalMap(groups);

        // In the gap
        Assert.Equal(-1, map.FindContainingInterval(250));
    }

    [Fact]
    public void FindContainingInterval_LargeOffsetPastAll_ReturnsMinusOne()
    {
        var groups = new List<GrupHeaderInfo> { MakeGrup(100, 100, 0x42) };
        var map = new SortedIntervalMap(groups);

        Assert.Equal(-1, map.FindContainingInterval(999999));
    }

    [Fact]
    public void FindContainingInterval_OffsetBeforeAll_ReturnsMinusOne()
    {
        var groups = new List<GrupHeaderInfo> { MakeGrup(500, 100, 0x42) };
        var map = new SortedIntervalMap(groups);

        Assert.Equal(-1, map.FindContainingInterval(10));
    }

    [Fact]
    public void GetLabelAsFormId_ReturnsCorrectFormId()
    {
        var groups = new List<GrupHeaderInfo>
        {
            MakeGrup(100, 100, 0x0017B37C),
            MakeGrup(300, 100, 0xDEADBEEF)
        };
        var map = new SortedIntervalMap(groups);

        Assert.Equal(0x0017B37Cu, map.GetLabelAsFormId(0));
        Assert.Equal(0xDEADBEEFu, map.GetLabelAsFormId(1));
    }

    [Fact]
    public void FindContainingInterval_UnsortedInput_StillWorks()
    {
        // Input deliberately out of order — constructor should sort
        var groups = new List<GrupHeaderInfo>
        {
            MakeGrup(600, 100, 0xCC),
            MakeGrup(100, 100, 0xAA),
            MakeGrup(300, 200, 0xBB)
        };
        var map = new SortedIntervalMap(groups);

        Assert.Equal(0, map.FindContainingInterval(150));
        Assert.Equal(1, map.FindContainingInterval(400));
        Assert.Equal(2, map.FindContainingInterval(650));

        // Verify labels are correctly associated after sorting
        Assert.Equal(0xAAu, map.GetLabelAsFormId(0));
        Assert.Equal(0xBBu, map.GetLabelAsFormId(1));
        Assert.Equal(0xCCu, map.GetLabelAsFormId(2));
    }
}
