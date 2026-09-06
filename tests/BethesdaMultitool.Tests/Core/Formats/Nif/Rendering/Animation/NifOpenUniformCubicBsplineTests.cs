using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class NifOpenUniformCubicBsplineTests
{
    [Fact]
    public void Sample_FourControlPointsUsesCubicBasisInsteadOfLinearizedKeys()
    {
        var controlPoints = new[] { 0f, 0f, 0f, 8f };

        var midpoint = NifOpenUniformCubicBspline.Sample(controlPoints, 2f, 6f, 4f);
        var basis = NifOpenUniformCubicBspline.ComputeBasis(4, 2f, 6f, 4f);

        Assert.Equal(1f, midpoint, 5);
        Assert.Equal(0, basis.FirstControlPoint);
        Assert.Equal(4, basis.Count);
        Assert.Equal(1f, Enumerable.Range(0, basis.Count).Sum(index => basis[index]), 5);
        Assert.Equal(0.125f, basis[0], 5);
        Assert.Equal(0.375f, basis[1], 5);
        Assert.Equal(0.375f, basis[2], 5);
        Assert.Equal(0.125f, basis[3], 5);
    }

    [Fact]
    public void Sample_ClampsBothEndpointsToExactControlPoints()
    {
        var controlPoints = new[]
        {
            new Vector3(1f, 2f, 3f),
            new Vector3(4f, 5f, 6f),
            new Vector3(7f, 8f, 9f),
            new Vector3(10f, 11f, 12f),
            new Vector3(13f, 14f, 15f)
        };

        Assert.Equal(controlPoints[0], NifOpenUniformCubicBspline.Sample(
            controlPoints,
            1f,
            3f,
            -100f));
        Assert.Equal(controlPoints[^1], NifOpenUniformCubicBspline.Sample(
            controlPoints,
            1f,
            3f,
            100f));
    }

    [Fact]
    public void Sample_QuaternionResultIsNormalizedAfterComponentBasisBlend()
    {
        var controlPoints = Enumerable.Repeat(new Quaternion(1f, 0f, 0f, 1f), 4).ToArray();

        var value = NifOpenUniformCubicBspline.Sample(controlPoints, 0f, 1f, 0.37f);

        Assert.Equal(1f, value.Length(), 5);
        Assert.Equal(MathF.Sqrt(0.5f), value.X, 5);
        Assert.Equal(MathF.Sqrt(0.5f), value.W, 5);
    }

    [Theory]
    [InlineData(3, 0f, 1f, 0.5f)]
    [InlineData(4, 1f, 1f, 1f)]
    [InlineData(4, 0f, 1f, float.NaN)]
    public void ComputeBasis_MalformedInputsFailClosed(
        int controlPointCount,
        float startTime,
        float stopTime,
        float time)
    {
        Assert.Throws<InvalidDataException>(() =>
            NifOpenUniformCubicBspline.ComputeBasis(
                controlPointCount,
                startTime,
                stopTime,
                time));
    }
}
