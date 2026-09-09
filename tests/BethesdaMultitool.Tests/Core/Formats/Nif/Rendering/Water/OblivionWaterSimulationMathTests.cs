using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class OblivionWaterSimulationMathTests
{
    [Fact]
    public void RecurrenceUsesUpdatedGreenAndDampsEveryChannel()
    {
        var result = OblivionWaterSimulationMath.Evolve(new Vector4(.75f, .625f, .25f, 1),
            new Vector4(1, .5f, .75f, .25f), new Vector3(.5f, .25f, .5f));
        Assert.Equal(new Vector4(.609375f, .4375f, .375f, .75f), result);
    }

    [Fact]
    public void AlgebraDoesNotInventRenderTargetSaturation()
    {
        var result = OblivionWaterSimulationMath.Evolve(new Vector4(1, .5f, .5f, .5f), Vector4.Zero, Vector3.One);
        Assert.Equal(new Vector4(-3, -3.5f, .5f, .5f), result);
    }

    [Fact]
    public void SignedEightTapNormalUsesAbsoluteHeightsAndSourceAxisSigns()
    {
        var result =
            OblivionWaterSimulationMath.EncodeNormal(new Vector4(-1, 2, -3, 4), new Vector4(-5, 6, -7, 8), .125f);
        var root = MathF.Sqrt(29);
        Near(new Vector4(.5f - 1 / root, .5f + 1.5f / root, .5f + 2 / root, 1), result);
    }

    [Theory]
    [InlineData(.25f, .4875f)]
    [InlineData(1.25f, .8375f)]
    public void HeightBlendScalesOnlyFftEndpointAndDoesNotClampRawShaderAmount(float amount, float expected)
    {
        Near(new Vector4(expected, expected, expected, 1),
            OblivionWaterSimulationMath.BlendHeight(-.25f, -.75f, .5f, amount));
    }

    [Fact]
    public void RecenterSamplesOffsetUvButFadesAtOriginalUv()
    {
        var uv = new Vector2(.95f, .5f);
        Assert.Equal(new Vector2(.5f, .5f), OblivionWaterSimulationMath.RecenterSampleUv(uv, new Vector2(-.45f, 0)));
        Near(new Vector4(.35f, .65f, .45f, .8f),
            OblivionWaterSimulationMath.Recenter(uv, new Vector4(.2f, .8f, .4f, .6f)));
        Assert.Equal(new Vector2(.99f, .5f),
            OblivionWaterSimulationMath.RecenterSampleUv(new Vector2(.5f, .5f), new Vector2(.49f, 0)));
        Assert.Equal(new Vector4(.2f, .8f, .4f, .6f),
            OblivionWaterSimulationMath.Recenter(new Vector2(.5f, .5f), new Vector4(.2f, .8f, .4f, .6f)));
        Near(new Vector4(.5f, .5f, .5f, 1),
            OblivionWaterSimulationMath.Recenter(new Vector2(1, .5f), Vector4.Zero));
    }

    [Fact]
    public void CallerClearHasByte127BiasAndStampIsReplacementState()
    {
        Assert.Equal(0xFF7F7F7Fu, OblivionWaterSimulationMath.InitialPackedClear);
        Assert.Equal((ushort)32639, OblivionWaterSimulationMath.InitialUnorm16Rgb);
        Assert.InRange(OblivionWaterSimulationMath.InitialState.X, .49803f, .49804f);
        Assert.Equal(1f, OblivionWaterSimulationMath.InitialState.W);
        Assert.Equal(new Vector4(.9f, .5f, .5f, .5f), OblivionWaterSimulationMath.StampState);
    }

    [Theory]
    [InlineData(0, -1f)]
    [InlineData(32767, 1f)]
    public void RecordedRandomEndpointsMapToExactClipEdges(int random, float expected)
    {
        Assert.Equal(expected, OblivionWaterSimulationMath.RainClipOffset(random));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32768)]
    public void RandomValuesOutsideInstalledRandRangeAreRejected(int random)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OblivionWaterSimulationMath.RainClipOffset(random));
    }

    [Theory]
    [InlineData(9, .1f, 0)]
    [InlineData(10, .1f, 1)]
    [InlineData(19, .1f, 1)]
    [InlineData(10, 0f, 0)]
    [InlineData(1, 1.9999999f, 1)]
    [InlineData(1024, .25f, 256)]
    public void RainCountTruncatesThePositiveProduct(int rate, float seconds, int expected)
    {
        Assert.Equal(expected, OblivionWaterSimulationMath.RainEventCount(rate, seconds));
    }

    [Fact]
    public void RainCountSupportsSubnormalTimeAndRejectsOverflow()
    {
        Assert.Equal(0, OblivionWaterSimulationMath.RainEventCount(int.MaxValue, float.Epsilon));
        Assert.Throws<ArgumentOutOfRangeException>(() => OblivionWaterSimulationMath.RainEventCount(int.MaxValue, 2f));
    }

    [Fact]
    public void ProductWhoseCountDependsOnFpuOrCrtModeIsRejected()
    {
        // Exact product =1082130559 +8388607/8388608. A double multiply rounds to1082130560;
        // the alternate helper can retain the fraction. Neither active mode is guessed here.
        var seconds = BitConverter.UInt32BitsToSingle(0x3F800001);
        Assert.Throws<ArgumentException>(() => OblivionWaterSimulationMath.RainEventCount(1082130431, seconds));
    }

    [Fact]
    public void ExactBelowIntegerProductAndAboveIntegerStoredFloatHaveUnambiguousCounts()
    {
        // Lower float neighbor of1/3 times3 is exactly the float immediately below1; do not
        // enlarge an already representable product with nextafter and falsely reject it.
        Assert.Equal(0, OblivionWaterSimulationMath.RainEventCount(3, BitConverter.UInt32BitsToSingle(0x3EAAAAAA)));
        Assert.Equal(1, OblivionWaterSimulationMath.RainEventCount(3, BitConverter.UInt32BitsToSingle(0x3EAAAAAB)));
        Assert.Equal(1, OblivionWaterSimulationMath.RainEventCount(10, .1f));
    }

    [Fact]
    public void SingleVersusDoubleCountDisagreementAtCommonFractionIsNotGuessed()
    {
        // float(.04)*25 is below1 in wider precision but can round to1 at24 bits.
        Assert.Throws<ArgumentException>(() => OblivionWaterSimulationMath.RainEventCount(25, .04f));
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion, false, 12u)]
    [InlineData(BethesdaGame.Morrowind, false, 12u)]
    [InlineData(BethesdaGame.FalloutNewVegas, false, 99u)]
    [InlineData(BethesdaGame.FalloutNewVegas, true, 12u)]
    public void Tes4WadingToggleKeepsOrdinaryFftOrRainNormalWhileFnvRetainsFlatControl(
        BethesdaGame game, bool enabled, uint expected)
    {
        Assert.Equal(expected, OblivionWaterSimulationRoutePolicy.ApplyOrdinaryRippleToggle(game, enabled, 12, 99));
    }

    private static void Near(Vector4 expected, Vector4 actual)
    {
        Assert.InRange(Vector4.Distance(expected, actual), 0f, 0.000002f);
    }
}