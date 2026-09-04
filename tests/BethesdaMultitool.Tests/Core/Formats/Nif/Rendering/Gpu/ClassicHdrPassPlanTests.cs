using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class ClassicHdrPassPlanTests
{
    [Fact]
    public void OddSizedTarget_ReducesByFourUntilOneAndRunsOneSeparableBloomPair()
    {
        var plan = ClassicHdrPassPlan.Create(
            770,
            481,
            true,
            999f);

        var expectedLevels = new[]
        {
            new ClassicHdrReductionLevel(770, 481, 192, 120),
            new ClassicHdrReductionLevel(192, 120, 48, 30),
            new ClassicHdrReductionLevel(48, 30, 12, 7),
            new ClassicHdrReductionLevel(12, 7, 3, 1),
            new ClassicHdrReductionLevel(3, 1, 1, 1)
        };
        var actualLevels = Enumerable.Range(0, plan.DownsampleDrawCount)
            .Select(plan.GetReductionLevel)
            .ToArray();

        Assert.Equal(expectedLevels, actualLevels);
        Assert.Equal(1, ClassicHdrPassPlan.AdaptDrawCount);
        Assert.Equal(1, plan.BrightPassBlurDrawCount);
        Assert.Equal(1, plan.BlurDrawCount);
        Assert.Equal(1, ClassicHdrPassPlan.CompositeDrawCount);
        Assert.Equal(
            [
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Adapt,
                ClassicHdrPassKind.BrightPassBlurVertical,
                ClassicHdrPassKind.BlurHorizontal,
                ClassicHdrPassKind.Composite
            ],
            Enumerable.Range(0, plan.TotalDrawCount).Select(plan.GetPassKind));
    }

    [Theory]
    [InlineData(-100f)]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(999f)]
    public void LaterFusedGraph_DoesNotRepeatBrightPassBlur(float authoredBlurPasses)
    {
        var plan = ClassicHdrPassPlan.Create(768, 480, true, authoredBlurPasses);

        Assert.Equal(1, plan.BrightPassBlurDrawCount);
        Assert.Equal(1, plan.BlurDrawCount);
        Assert.Single(
            Enumerable.Range(0, plan.TotalDrawCount),
            i => plan.GetPassKind(i) == ClassicHdrPassKind.BrightPassBlurVertical);
        Assert.Single(
            Enumerable.Range(0, plan.TotalDrawCount),
            i => plan.GetPassKind(i) == ClassicHdrPassKind.BlurHorizontal);
    }

    [Fact]
    public void Tes4TwoPassSetting_ProducesSeparateBrightPassAndTwoCumulativeAxisPairs()
    {
        var plan = ClassicHdrPassPlan.CreateTes4(768, 480, true, 2f);

        Assert.Equal(ClassicHdrBloomTopology.Tes4SeparateBrightPassCumulative, plan.BloomTopology);
        Assert.Equal(2, plan.BlurPairCount);
        Assert.Equal(1, plan.BrightPassDrawCount);
        Assert.Equal(0, plan.BrightPassBlurDrawCount);
        Assert.Equal(4, plan.BlurDrawCount);
        Assert.Equal(
            [
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Downsample16,
                ClassicHdrPassKind.Adapt,
                ClassicHdrPassKind.BrightPass,
                ClassicHdrPassKind.BlurVertical,
                ClassicHdrPassKind.BlurHorizontal,
                ClassicHdrPassKind.BlurVertical,
                ClassicHdrPassKind.BlurHorizontal,
                ClassicHdrPassKind.Composite
            ],
            Enumerable.Range(0, plan.TotalDrawCount).Select(plan.GetPassKind));
    }

    [Theory]
    [InlineData(-2.9f, 2)]
    [InlineData(-1f, 1)]
    [InlineData(0f, 0)]
    [InlineData(1.9f, 1)]
    [InlineData(2f, 2)]
    [InlineData(999f, ClassicHdrPassPlan.MaxBloomPairCount)]
    public void Tes4BlurPassCount_TruncatesTakesAbsoluteValueAndBoundsCorruptInput(
        float activeBlurPasses,
        int expected)
    {
        Assert.Equal(expected, ClassicHdrPassPlan.ResolveTes4BlurPairCount(activeBlurPasses));
    }

    [Fact]
    public void Tes4BlurPassCount_NonFiniteInputRecordsNoUnboundedLoop()
    {
        Assert.Equal(0, ClassicHdrPassPlan.ResolveTes4BlurPairCount(float.NaN));
        Assert.Equal(0, ClassicHdrPassPlan.ResolveTes4BlurPairCount(float.PositiveInfinity));
        Assert.Equal(0, ClassicHdrPassPlan.ResolveTes4BlurPairCount(float.NegativeInfinity));
    }

    [Fact]
    public void BloomOff_RetainsReductionAndAdaptButOmitsOnlyBloomDraw()
    {
        var enabled = ClassicHdrPassPlan.Create(768, 480, true, 2f);
        var disabled = ClassicHdrPassPlan.Create(768, 480, false, 2f);

        Assert.Equal(enabled.DownsampleDrawCount, disabled.DownsampleDrawCount);
        Assert.Equal(1, ClassicHdrPassPlan.AdaptDrawCount);
        Assert.Equal(0, disabled.BrightPassBlurDrawCount);
        Assert.Equal(0, disabled.BlurDrawCount);
        Assert.Equal(ClassicHdrPassKind.Composite, disabled.GetPassKind(disabled.TotalDrawCount - 1));
    }

    [Fact]
    public void Tes4BloomOff_OmitsSeparateBrightPassAndEveryBlurPair()
    {
        var enabled = ClassicHdrPassPlan.CreateTes4(768, 480, true, 2f);
        var disabled = ClassicHdrPassPlan.CreateTes4(768, 480, false, 2f);

        Assert.Equal(enabled.DownsampleDrawCount, disabled.DownsampleDrawCount);
        Assert.Equal(5, enabled.TotalDrawCount - disabled.TotalDrawCount);
        Assert.Equal(0, disabled.BrightPassDrawCount);
        Assert.Equal(0, disabled.BlurDrawCount);
        Assert.Equal(ClassicHdrPassKind.Composite, disabled.GetPassKind(disabled.TotalDrawCount - 1));
    }

    [Fact]
    public void SkyrimFirstFrame_UsesExactCeilingQuarterChainThroughOneByOne()
    {
        var plan = ClassicHdrPassPlan.CreateSkyrim(
            1920, 1080, historyAvailable: false, bloomEnabled: true);

        var expected = new[]
        {
            new ClassicHdrReductionLevel(1920, 1080, 480, 270),
            new ClassicHdrReductionLevel(480, 270, 120, 68),
            new ClassicHdrReductionLevel(120, 68, 30, 17),
            new ClassicHdrReductionLevel(30, 17, 8, 5),
            new ClassicHdrReductionLevel(8, 5, 2, 2),
            new ClassicHdrReductionLevel(2, 2, 1, 1)
        };
        Assert.Equal(expected,
            Enumerable.Range(0, plan.DownsampleDrawCount).Select(plan.GetReductionLevel));
        Assert.Equal(HdrReductionDimensionRule.CeilingQuarter, plan.DimensionRule);
        Assert.False(plan.FinalReductionInAdapt);
        Assert.True(plan.BloomEnabled);
        Assert.Equal(1, plan.BrightPassBlurDrawCount);
        Assert.Equal(1, plan.BlurDrawCount);
        Assert.Equal(ClassicHdrPassKind.BrightPassBlurVertical, plan.GetPassKind(7));
        Assert.Equal(ClassicHdrPassKind.BlurHorizontal, plan.GetPassKind(8));
        Assert.Equal(ClassicHdrPassKind.Composite, plan.GetPassKind(9));
    }

    [Fact]
    public void SkyrimPrimedFrame_FusesFinalReductionIntoAdapt()
    {
        var plan = ClassicHdrPassPlan.CreateSkyrim(
            1920, 1080, historyAvailable: true, bloomEnabled: true);

        Assert.Equal(5, plan.DownsampleDrawCount);
        Assert.Equal(new ClassicHdrReductionLevel(8, 5, 2, 2), plan.GetReductionLevel(4));
        Assert.True(plan.FinalReductionInAdapt);
        Assert.Equal(ClassicHdrPassKind.Adapt, plan.GetPassKind(5));
    }

    [Fact]
    public void SkyrimTinyTarget_StillRunsMandatorySlot4Reduction()
    {
        var plan = ClassicHdrPassPlan.CreateSkyrim(
            3, 2, historyAvailable: true, bloomEnabled: false);

        Assert.Equal(1, plan.DownsampleDrawCount);
        Assert.Equal(new ClassicHdrReductionLevel(3, 2, 1, 1), plan.GetReductionLevel(0));
    }

    [Fact]
    public void SkyrimBloomKillSwitch_OmitsOnlyTheRecoveredBlurPair()
    {
        var enabled = ClassicHdrPassPlan.CreateSkyrim(
            1920, 1080, historyAvailable: true, bloomEnabled: true);
        var disabled = ClassicHdrPassPlan.CreateSkyrim(
            1920, 1080, historyAvailable: true, bloomEnabled: false);

        Assert.Equal(enabled.DownsampleDrawCount, disabled.DownsampleDrawCount);
        Assert.Equal(2, enabled.TotalDrawCount - disabled.TotalDrawCount);
        Assert.Equal(0, disabled.BrightPassBlurDrawCount);
        Assert.Equal(0, disabled.BlurDrawCount);
        Assert.Equal(ClassicHdrPassKind.Composite, disabled.GetPassKind(disabled.TotalDrawCount - 1));
    }
}
