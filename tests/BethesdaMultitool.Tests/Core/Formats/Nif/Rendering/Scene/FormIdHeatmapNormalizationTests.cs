using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     Pins <see cref="FormIdHeatmapRanking" /> — the ORDINAL normalization the 3D viewer's FormID
///     heatmap recomputes as the camera moves. The defining property is that each distinct FormID in
///     range is one evenly-spaced step along the ramp regardless of the numeric gaps between them,
///     which is what value-linear normalization could not do: a worldspace with late additions (FO3's
///     DCWorld15) pushed the bulk of its refs into a sliver of the ramp and rendered them one colour.
/// </summary>
public sealed class FormIdHeatmapNormalizationTests
{
    private static FormIdHeatmapRanking Ranked(params uint[] formIds)
    {
        var ranking = new FormIdHeatmapRanking();
        foreach (var id in formIds)
        {
            ranking.Add(id);
        }

        ranking.Seal();
        return ranking;
    }

    [Fact]
    public void Endpoints_MapToZeroAndOne()
    {
        var ranking = Ranked(0x100, 0x180, 0x200);
        Assert.Equal(0f, ranking.Normalize(0x100));
        Assert.Equal(1f, ranking.Normalize(0x200));
    }

    [Fact]
    public void ConsecutiveRanks_AreEvenlySpacedRegardlessOfNumericGaps()
    {
        // Three refs whose VALUES are wildly uneven (gap 1 then gap 0x00F00000) still land on
        // 0 / 0.5 / 1 — the granularity the value-linear window destroyed.
        var ranking = Ranked(0x1000, 0x1001, 0x00F01001);
        Assert.Equal(0f, ranking.Normalize(0x1000));
        Assert.Equal(0.5f, ranking.Normalize(0x1001), 6);
        Assert.Equal(1f, ranking.Normalize(0x00F01001));
    }

    [Fact]
    public void LateOutlier_DoesNotCollapseTheRestOfTheRamp()
    {
        // The DCWorld15 shape: a dense early block plus one very late addition. Under value-linear
        // normalization every early ref mapped to ~0; ordinally they still span the whole ramp.
        var ids = new uint[16];
        for (var i = 0; i < 15; i++)
        {
            ids[i] = (uint)(0x1000 + i);
        }

        ids[15] = 0x00FF0000;

        var ranking = Ranked(ids);
        Assert.Equal(16, ranking.DistinctCount);
        Assert.Equal(0f, ranking.Normalize(0x1000));
        Assert.Equal(14f / 15f, ranking.Normalize(0x100E), 6); // last of the dense block
        Assert.Equal(1f, ranking.Normalize(0x00FF0000));
        // Distinct colours, not one flat band: the dense block spans most of the ramp.
        var first = FormIdHeatmapPalette.ToRgb(ranking.Normalize(0x1000));
        var last = FormIdHeatmapPalette.ToRgb(ranking.Normalize(0x100E));
        Assert.NotEqual(first, last);
    }

    [Fact]
    public void Duplicates_CollapseToOneStep()
    {
        var ranking = Ranked(0x10, 0x10, 0x20, 0x20, 0x20, 0x30);
        Assert.Equal(3, ranking.DistinctCount);
        Assert.Equal(0f, ranking.Normalize(0x10));
        Assert.Equal(0.5f, ranking.Normalize(0x20), 6);
        Assert.Equal(1f, ranking.Normalize(0x30));
    }

    [Fact]
    public void UnsortedInput_IsRankedByValue()
    {
        var ranking = Ranked(0x300, 0x100, 0x200);
        Assert.Equal(0f, ranking.Normalize(0x100));
        Assert.Equal(0.5f, ranking.Normalize(0x200), 6);
        Assert.Equal(1f, ranking.Normalize(0x300));
        Assert.Equal(0x100u, ranking.Min);
        Assert.Equal(0x300u, ranking.Max);
    }

    [Fact]
    public void SingleEntry_MapsToNeutralMiddle()
    {
        var ranking = Ranked(0x1234);
        Assert.Equal(1, ranking.DistinctCount);
        Assert.Equal(0.5f, ranking.Normalize(0x1234));
    }

    [Fact]
    public void Empty_IsNeutralAndReportsNoSteps()
    {
        var ranking = Ranked();
        Assert.True(ranking.IsEmpty);
        Assert.Equal(0, ranking.DistinctCount);
        Assert.Equal(0.5f, ranking.Normalize(0x1234));
        Assert.Equal(0u, ranking.Min);
        Assert.Equal(0u, ranking.Max);
    }

    [Fact]
    public void UnknownFormId_TakesItsInsertionPositionInsteadOfAnEndpoint()
    {
        // Cannot arise from the renderer (one predicate gates scan and tint) but must stay total.
        var ranking = Ranked(0x100, 0x200, 0x300, 0x400, 0x500);
        Assert.Equal(0.5f, ranking.Normalize(0x280), 6); // between ranks 2 and 3 → index 2 of 4
        Assert.Equal(0f, ranking.Normalize(0x1)); // below everything
        Assert.Equal(1f, ranking.Normalize(0xFFFF)); // above everything
    }

    [Fact]
    public void ResetAndReuse_DropsThePriorScan()
    {
        var ranking = new FormIdHeatmapRanking();
        ranking.Add(0x10);
        ranking.Add(0x20);
        ranking.Seal();
        Assert.Equal(2, ranking.DistinctCount);

        ranking.Reset();
        Assert.True(ranking.IsEmpty);
        for (var i = 0; i < 400; i++)
        {
            ranking.Add((uint)(0x5000 + i)); // past the initial 256 buffer — growth must not corrupt
        }

        ranking.Seal();

        Assert.Equal(400, ranking.DistinctCount);
        Assert.Equal(0x5000u, ranking.Min);
        Assert.Equal(0x5000u + 399u, ranking.Max);
        Assert.Equal(0f, ranking.Normalize(0x5000));
        Assert.Equal(1f, ranking.Normalize(0x5000 + 399));
    }

    [Fact]
    public void FullUintRange_DoesNotOverflow()
    {
        var ranking = Ranked(0u, 0x7FFFFFFFu, uint.MaxValue);
        Assert.Equal(0f, ranking.Normalize(0u));
        Assert.Equal(0.5f, ranking.Normalize(0x7FFFFFFFu), 6);
        Assert.Equal(1f, ranking.Normalize(uint.MaxValue));
    }
}