using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class FnvWaterNoiseAnimationTests
{
    [Fact]
    public void ScrollUsesCompassDirectionAndWrapsOverTime()
    {
        var north = new WaterNoiseLayer(1f, 0f, 0.25f, 1f);
        var east = new WaterNoiseLayer(1f, 90f, 0.25f, 1f);

        Assert.Equal(0f, FnvWaterNoiseAnimation.Scroll(north, 1f).X, 6);
        Assert.Equal(0.25f, FnvWaterNoiseAnimation.Scroll(north, 1f).Y, 6);
        Assert.Equal(0.25f, FnvWaterNoiseAnimation.Scroll(east, 1f).X, 6);
        Assert.Equal(0f, FnvWaterNoiseAnimation.Scroll(east, 1f).Y, 6);
        Assert.Equal(
            FnvWaterNoiseAnimation.Scroll(north, 1f),
            FnvWaterNoiseAnimation.Scroll(north, 5f));
    }

    [Fact]
    public void InvalidAnimationInputFailsClosedToStationary()
    {
        var layer = new WaterNoiseLayer(1f, float.NaN, 1f, 1f);

        Assert.Equal(default, FnvWaterNoiseAnimation.Scroll(layer, 12f));
    }

    [Fact]
    public void RetailDebugFillPhaseDoesNotCollapseToAnIntegerInClosedForm()
    {
        var debugFill = BitConverter.Int32BitsToSingle(unchecked((int)0xCDCDCDCD));
        var layer = new WaterNoiseLayer(100f, debugFill, debugFill, 0.5f);

        Assert.Equal(-431_602_080f, debugFill);
        Assert.Equal(Vector2.Zero, FnvWaterNoiseAnimation.Scroll(layer, 0f));
        var phase = FnvWaterNoiseAnimation.Scroll(layer, 1.2345f);
        var nextPhase = FnvWaterNoiseAnimation.Scroll(layer, 1.2345f + 1f / 60f);
        Assert.NotEqual(Vector2.Zero, phase);
        Assert.InRange(phase.X, 0f, 1f - float.Epsilon);
        Assert.InRange(phase.Y, 0f, 1f - float.Epsilon);
        Assert.True(Vector2.DistanceSquared(phase, nextPhase) > 1e-6f);
    }
}