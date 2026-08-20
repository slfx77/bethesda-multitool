using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

public sealed class RecoveredSkyShaderReferenceTests
{
    private static readonly Vector3 Horizon = new(0.9f, 0.3f, 0.1f);
    private static readonly Vector3 Lower = new(0.2f, 0.5f, 0.7f);
    private static readonly Vector3 Upper = new(0.05f, 0.1f, 0.4f);

    [Theory]
    [InlineData(1f, 0f, 0f, 0.9f, 0.3f, 0.1f)]
    [InlineData(0f, 1f, 0f, 0.2f, 0.5f, 0.7f)]
    [InlineData(0f, 0f, 1f, 0.05f, 0.1f, 0.4f)]
    public void SkyVertexRgbSelectsRecoveredBlendColorRows(
        float r, float g, float b, float expectedR, float expectedG, float expectedB)
    {
        // Expected values are literal recovered-shader vectors, independent of the production helper.
        var expected = new Vector3(expectedR, expectedG, expectedB);
        var actual = SkyBlendWeights.Evaluate(new Vector3(r, g, b), Horizon, Lower, Upper);

        VectorAssert.Equal(expected, actual);
    }

    [Fact]
    public void MixedWeightsUseRecoveredThreeTermMultiplyAddOrder()
    {
        var weights = new Vector3(0.25f, 0.5f, 0.125f);
        var expected = Horizon * 0.25f + Lower * 0.5f + Upper * 0.125f;

        VectorAssert.Equal(expected, SkyBlendWeights.Evaluate(weights, Horizon, Lower, Upper));
    }

    [Fact]
    public void CloudRedChannelIsBlendWeightNotLiteralRedTint()
    {
        var tint = new Vector3(0.7f, 0.8f, 0.9f);

        VectorAssert.Equal(tint, SkyBlendWeights.Evaluate(Vector3.UnitX, tint, Vector3.Zero, Vector3.Zero));
        VectorAssert.Equal(Vector3.Zero,
            SkyBlendWeights.Evaluate(Vector3.UnitY, tint, Vector3.Zero, Vector3.Zero));
    }

    [Fact]
    public void AuthoredAlphaCompositesOverHorizonWithoutChangingBackgroundOpacity()
    {
        var weighted = new Vector3(0.1f, 0.2f, 0.3f);
        var expected = new Vector3(0.5f, 0.25f, 0.2f);

        VectorAssert.Equal(expected, SkyBlendWeights.CompositeAtmosphere(weighted, Horizon, 0.5f));
    }
}