using System.Numerics;
using BethesdaMultitool.Core.Modeling.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The NiTextureTransform composition (Assumed: nif.xml's TransformMethod products, recalled rather than measured).
///     Each case checks Shared's resulting row-vector matrix on a sample point against the nif.xml product evaluated by
///     hand here; the control is a transform with no Shared equivalent, which must be refused rather than approximated.
/// </summary>
public class NifModelTextureTransformTests
{
    /// <summary>Max (C S R T B) about the origin: the offset is the scaled translation.</summary>
    [Fact]
    public void Max_ScaleThenTranslate_OffsetsByTheScaledTranslation()
    {
        Assert.True(NifModelTextureTransform.TryCompose(1, new Vector2(0.5f, 0f), new Vector2(2f, 2f), 0f,
            Vector2.Zero, out var transform, out _));

        Assert.Equal(new Vector2(1f, 0f), transform.Offset);
        Assert.Equal(new Vector2(2f, 2f), transform.Scale);
        AssertClose(new Vector2(1.5f, 0.5f), Vector2.Transform(new Vector2(0.25f, 0.25f), transform.ToMatrix()));
    }

    /// <summary>Maya (C R B F T S) flips V: (u, v) becomes (u, 1 - v).</summary>
    [Fact]
    public void Maya_FlipsV()
    {
        Assert.True(NifModelTextureTransform.TryCompose(2, Vector2.Zero, Vector2.One, 0f, Vector2.Zero,
            out var transform, out _));

        AssertClose(new Vector2(0.25f, 0.75f), Vector2.Transform(new Vector2(0.25f, 0.25f), transform.ToMatrix()));
        Assert.Equal(-1f, transform.Scale.Y);
    }

    /// <summary>A rotation about the center keeps the center fixed and turns (center + (d, 0)) to (center + (0, d)).</summary>
    [Fact]
    public void Max_RotationAboutTheCenter_KeepsTheCenterFixed()
    {
        var center = new Vector2(0.5f, 0.5f);
        Assert.True(NifModelTextureTransform.TryCompose(1, Vector2.Zero, Vector2.One, MathF.PI / 2, center,
            out var transform, out _));
        var matrix = transform.ToMatrix();

        AssertClose(center, Vector2.Transform(center, matrix));
        AssertClose(new Vector2(0.5f, 0.75f), Vector2.Transform(new Vector2(0.75f, 0.5f), matrix));
    }

    /// <summary>
    ///     Control: in the Max order a non-uniform scale after a 45-degree rotation shears, which Shared's
    ///     scale-rotate-offset form cannot express; the composition is refused. The same values in the Maya
    ///     (deprecated) order, which scales first, are representable.
    /// </summary>
    [Fact]
    public void NonUniformScaleAfterRotation_IsRefused()
    {
        var scale = new Vector2(2f, 1f);

        Assert.False(NifModelTextureTransform.TryCompose(1, Vector2.Zero, scale, MathF.PI / 4, Vector2.Zero,
            out _, out var residual));
        Assert.True(residual > NifModelTextureTransform.Tolerance);
        Assert.True(NifModelTextureTransform.TryCompose(0, Vector2.Zero, scale, MathF.PI / 4, Vector2.Zero,
            out _, out _));
    }

    private static void AssertClose(Vector2 expected, Vector2 actual)
    {
        Assert.True(Vector2.Distance(expected, actual) < 1e-6f, $"expected {expected}, got {actual}");
    }
}
