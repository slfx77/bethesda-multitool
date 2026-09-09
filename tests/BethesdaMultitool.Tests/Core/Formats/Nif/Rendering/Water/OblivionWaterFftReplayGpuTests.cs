using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

[Collection(OblivionWaterReplayGpuGroup.Name)]
[Trait("Category", TestCategories.Gpu)]
public sealed class OblivionWaterFftReplayGpuTests
{
    [Theory]
    [InlineData(false, 128)]
    [InlineData(true, 128)]
    [InlineData(false, 256)]
    [InlineData(true, 256)]
    public void Zero_rain_blend_converts_signed_raw_height_without_advancing_displacement(bool hardware, int size)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var fixture = new OblivionWaterReplayGpuFixture(size == 128 ? "fft-normal-low" : "fft-normal-high",
            hardware, size, size);
        var initial = fixture.Prepass.CommittedState;
        var plan = fixture.Record(OblivionWaterSimulationTestData.Rain(amount: 0));
        Assert.Equal(OblivionWaterSimulationStage.FftNormal, Assert.Single(plan.Passes).Stage);
        Assert.False(plan.InvokedDisplacement);
        Assert.Null(fixture.Prepass.OrdinaryNormal);
        fixture.SubmitAndCheck();
        Assert.Equal(initial with { LastInvocation = 1 }, fixture.Prepass.CommittedState);
        Assert.Equal(fixture.Resources[new OblivionWaterSimulationResource(6)].Srv.BindlessIndex,
            fixture.Prepass.OrdinaryNormal);
        Assert.Null(fixture.Prepass.WadingNormal);
        Assert.Equal(ResourceStates.PixelShaderResource,
            fixture.Resources[new OblivionWaterSimulationResource(8)].State);
        Assert.Equal(0, fixture.RecordedClears);
        Assert.Equal(1, fixture.RecordedUploads);
        Assert.True(fixture.RejectedNaiveChannels >= 64,
            "Signed nonuniform input must discriminate the unadapted viewport.");
    }

    [Theory]
    [InlineData(false, 128)]
    [InlineData(true, 128)]
    [InlineData(false, 256)]
    [InlineData(true, 256)]
    public void Interior_blend_uses_absolute_fft_and_actual_rain_scratch_then_releases_at_one(bool hardware, int size)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var fixture = new OblivionWaterReplayGpuFixture(size == 128 ? "fft-mixed-low" : "fft-mixed-high",
            hardware, size);
        var plan = fixture.Record(OblivionWaterSimulationTestData.Rain(amount: 0.375f) with
        {
            RainRate = 10,
            RainSamples = ImmutableArray.Create(new OblivionWaterRecordedRainSample(8224, 24543))
        });
        Assert.Collection(plan.Passes,
            pass => Assert.Equal(OblivionWaterSimulationStage.FftAbsoluteHeight, pass.Stage),
            pass => Assert.Equal(OblivionWaterSimulationStage.RainStamp, pass.Stage),
            pass => Assert.Equal(OblivionWaterSimulationStage.RainEvolution, pass.Stage),
            pass =>
            {
                Assert.Equal(OblivionWaterSimulationStage.MixedHeight, pass.Stage);
                Assert.Equal(new OblivionWaterSimulationResource(9), pass.Input0);
                Assert.Equal(plan.Next.ScratchB, pass.Input1);
                Assert.NotEqual(plan.Next.RainHeight, pass.Input1);
            },
            pass => Assert.Equal(OblivionWaterSimulationStage.Normal, pass.Stage));
        Assert.True(plan.AcquireFftIntermediate);
        Assert.True(plan.AcquireMixedHeight);
        fixture.SubmitAndCheck();
        var mixedNormal = fixture.Bytes(6).ToArray();
        Assert.Equal(new OblivionWaterSimulationResource(5), fixture.Prepass.CommittedState.FinalInput);
        Assert.Null(fixture.Prepass.WadingNormal);
        var endpoint = fixture.Record(OblivionWaterSimulationTestData.Rain(2));
        Assert.True(endpoint.ReleaseFftIntermediate);
        Assert.True(endpoint.ReleaseMixedHeight);
        Assert.Equal(OblivionWaterSimulationStage.Normal, Assert.Single(endpoint.Passes).Stage);
        fixture.SubmitAndCheck();
        Assert.NotEqual(mixedNormal, fixture.Bytes(6));
        Assert.Null(fixture.Prepass.CommittedState.FftIntermediate);
        Assert.False(fixture.Prepass.CommittedState.MixedHeightResident);
        Assert.Equal(ResourceStates.PixelShaderResource,
            fixture.Resources[new OblivionWaterSimulationResource(8)].State);
        Assert.Equal(6, fixture.RecordedUploads);
        Assert.Equal(1, fixture.RecordedClears);
    }
}