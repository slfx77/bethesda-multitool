using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

public sealed class FnvActiveAdtFogTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(5f, 0f)]
    [InlineData(10f, 0f)]
    [InlineData(15f, 0.25f)]
    [InlineData(20f, 1f)]
    [InlineData(40f, 1f)]
    public void Amount_ClampsThenAppliesAuthoredPower(float distance, float expected)
    {
        // Register parameters, not a claim about their runtime CPU upload source.
        var amount = FnvActiveAdtFog.EvaluateAmount(new Vector4(0, 0, distance, 1), new Vector3(20, 10, 2));
        Assert.Equal(expected, amount, 6);
    }

    [Fact]
    public void Amount_UsesAllThreeClipComponentsWithoutDividingByW()
    {
        var fogParam = new Vector3(20, 10, 1);
        var projected = new Vector4(9, 12, 0, 100);
        Assert.Equal(0.5f, FnvActiveAdtFog.EvaluateAmount(projected, fogParam), 6);
        Assert.Equal(0.5f, FnvActiveAdtFog.EvaluateAmount(projected with { W = 1 }, fogParam), 6);
        Assert.Equal(0f, FnvActiveAdtFog.EvaluateAmount(new Vector4(0, 0, 0, 100), fogParam), 6);
    }

    [Fact]
    public void Amount_RetainsFractionalPowerInsteadOfAnUnrelatedLinearFogLaw()
    {
        var amount = FnvActiveAdtFog.EvaluateAmount(new Vector4(0, 0, 15, 1), new Vector3(20, 10, 0.5f));
        Assert.Equal(MathF.Sqrt(0.5f), amount, 6);
        Assert.NotEqual(0.5f, amount);
    }

    [Fact]
    public void ForwardProjectionRecovery_PreservesXYAndWAndUndoesOnlyReversedDepth()
    {
        var forward = FnvActiveAdtFog.RecoverForwardClipPosition(new Vector4(9, 12, 99, 100));
        Assert.Equal(new Vector4(9, 12, 1, 100), forward);
        var fogParam = new Vector3(20, 10, 1);
        Assert.InRange(FnvActiveAdtFog.EvaluateAmount(forward, fogParam), 0.5f, 0.51f);
        Assert.Equal(1f, FnvActiveAdtFog.EvaluateAmount(new Vector4(9, 12, 99, 100), fogParam));
    }

    [Fact]
    public void InterpolatedVertexFog_IsNotFogRecomputedAtTheInterpolatedPosition()
    {
        var fogParam = new Vector3(20, 20, 2);
        var near = new Vector4(0, 0, 0, 1);
        var far = new Vector4(0, 0, 20, 1);
        // Equal W at the endpoints makes the midpoint weights exactly one half here.
        var interpolatedAmount = (FnvActiveAdtFog.EvaluateAmount(near, fogParam) +
                                  FnvActiveAdtFog.EvaluateAmount(far, fogParam)) * 0.5f;
        Assert.Equal(0.5f, interpolatedAmount);
        Assert.Equal(0.25f, FnvActiveAdtFog.EvaluateAmount(Vector4.Lerp(near, far, 0.5f), fogParam));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(0f)]
    public void Composite_NonpositiveToggleLeavesLitRgbUnchanged(float toggle)
    {
        var lit = new Vector3(2f, 0.4f, 0.1f);
        Assert.Equal(lit, FnvActiveAdtFog.Composite(lit, Vector3.One, 0.75f, toggle));
    }

    [Theory]
    [InlineData(0.01f)]
    [InlineData(1f)]
    [InlineData(2f)]
    public void Composite_PositiveToggleUsesInterpolatedFogAfterLighting(float toggle)
    {
        var actual = FnvActiveAdtFog.Composite(new Vector3(2f, 0.4f, 0.1f),
            new Vector3(0.2f, 0.6f, 1f), 0.25f, toggle);
        VectorAssert.Equal(new Vector3(1.55f, 0.45f, 0.325f), actual);
    }

    [Fact]
    public void ProjectionRecovery_AgreesWithTheForwardCameraMatrixWithoutUsingWorldDistance()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(0.9f, 16f / 9f, 10f, 100000f);
        var point = new Vector4(50, 30, -100, 1);
        var forward = Vector4.Transform(point, projection);
        var reversed = Vector4.Transform(point, projection * CameraState.ReverseZ);
        var recovered = FnvActiveAdtFog.RecoverForwardClipPosition(reversed);
        Assert.Equal(forward.X, recovered.X);
        Assert.Equal(forward.Y, recovered.Y);
        Assert.Equal(forward.W, recovered.W);
        Assert.Equal(forward.Z, recovered.Z, 4);
        var fogParam = new Vector3(500, 500, 1);
        Assert.Equal(FnvActiveAdtFog.EvaluateAmount(forward, fogParam),
            FnvActiveAdtFog.EvaluateAmount(recovered, fogParam), 5);
        Assert.NotEqual(FnvActiveAdtFog.EvaluateAmount(point, fogParam),
            FnvActiveAdtFog.EvaluateAmount(recovered, fogParam));
    }
}