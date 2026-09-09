using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class OblivionWaterContinuousHeightTests
{
    private static WaterSurfaceParams Settings => WaterSurfaceParams.Default with
    {
        WindVelocity = 5f, WindDirection = 100f, WaveAmplitude = 0.15f, WaveFrequency = 0.7f
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArbitraryTimeIsRepeatableAndDoesNotWrapAtTheLegacyLoop(bool highResolution)
    {
        var producer = OblivionWaterSurfaceSynthesizer.CreateContinuousSurface(Settings, highResolution);
        Assert.Equal(highResolution ? 256 : 128, producer.Size);
        var first = Sample(producer, 0.31f);
        var loopLater = Sample(producer, 0.31f + 32f / 12f);
        var repeated = Sample(producer, 0.31f);
        Assert.Equal(first, repeated);
        Assert.Contains(first.Zip(loopLater), pair => MathF.Abs(pair.First - pair.Second) > 1e-4f);
        Assert.Contains(first, value => value < 0f);
        Assert.Contains(first, value => value > 0f);
        Assert.All(first, value => Assert.True(float.IsFinite(value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoublingFrequencyAndHalvingTimePreservesTheEntireField(bool highResolution)
    {
        var ordinary = OblivionWaterSurfaceSynthesizer.CreateContinuousSurface(Settings, highResolution);
        var doubled = OblivionWaterSurfaceSynthesizer.CreateContinuousSurface(
            Settings with { WaveFrequency = Settings.WaveFrequency * 2f }, highResolution);
        Assert.Equal(Sample(ordinary, 1.37f), Sample(doubled, 1.37f / 2f));
    }

    [Fact]
    public void ZeroAmplitudeProducesZeroHeightAndZeroFrequencyFreezesANonzeroField()
    {
        var calm = OblivionWaterSurfaceSynthesizer.CreateContinuousSurface(Settings with { WaveAmplitude = 0f }, false);
        Assert.All(Sample(calm, 123f), value => Assert.Equal(0f, value));
        var staticSurface =
            OblivionWaterSurfaceSynthesizer.CreateContinuousSurface(Settings with { WaveFrequency = 0f }, false);
        var initial = Sample(staticSurface, 0f);
        Assert.Contains(initial, value => MathF.Abs(value) > 1e-4f);
        Assert.Equal(initial, Sample(staticSurface, 123f));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.MaxValue)]
    [InlineData(-0.1f)]
    public void InvalidTimeDoesNotOverwriteTheCallerBuffer(float elapsed)
    {
        var producer = OblivionWaterSurfaceSynthesizer.CreateContinuousSurface(Settings, false);
        var buffer = Enumerable.Repeat(42f, producer.Size * producer.Size).ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => producer.Evaluate(elapsed, buffer));
        Assert.All(buffer, value => Assert.Equal(42f, value));
    }

    [Fact]
    public void DestinationHasExactSizeAndRetainsOwnershipAfterSubsequentEvaluations()
    {
        var producer = OblivionWaterSurfaceSynthesizer.CreateContinuousSurface(Settings, false);
        Assert.Throws<ArgumentException>(() => producer.Evaluate(0f, new float[producer.Size]));
        var first = Sample(producer, 0f);
        var retained = (float[])first.Clone();
        _ = Sample(producer, 5f);
        Assert.Equal(retained, first);
    }

    private static float[] Sample(OblivionWaterSurfaceSynthesizer.ContinuousSurface producer, float time)
    {
        var result = new float[producer.Size * producer.Size];
        producer.Evaluate(time, result);
        return result;
    }
}