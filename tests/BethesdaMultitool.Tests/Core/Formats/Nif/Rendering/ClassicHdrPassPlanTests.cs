using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

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
    public void AuthoredBlurPasses_DoesNotRepeatBrightPassBlur(float authoredBlurPasses)
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
}