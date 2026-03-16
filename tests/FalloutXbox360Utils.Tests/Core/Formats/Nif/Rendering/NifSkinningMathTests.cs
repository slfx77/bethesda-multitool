using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifSkinningMathTests
{
    [Fact]
    public void AnalyzeDualQuaternionCompatibility_RigidMatrices_AreAccepted()
    {
        var compatibility = NifSkinningExtractor.AnalyzeDualQuaternionCompatibility(
        [
            Matrix4x4.Identity,
            Matrix4x4.CreateFromYawPitchRoll(0.2f, -0.1f, 0.3f) * Matrix4x4.CreateTranslation(1f, 2f, 3f)
        ]);

        Assert.True(compatibility.CanUse);
        Assert.Equal(-1, compatibility.MatrixIndex);
    }

    [Fact]
    public void AnalyzeDualQuaternionCompatibility_ScaledMatrix_IsRejected()
    {
        var compatibility = NifSkinningExtractor.AnalyzeDualQuaternionCompatibility(
        [
            Matrix4x4.Identity,
            Matrix4x4.CreateScale(1.12f, 0.91f, 1.04f) * Matrix4x4.CreateTranslation(4f, 5f, 6f)
        ]);

        Assert.False(compatibility.CanUse);
        Assert.Equal(1, compatibility.MatrixIndex);
        Assert.True(
            MathF.Abs(compatibility.ScaleX - 1f) > 0.025f ||
            MathF.Abs(compatibility.ScaleY - 1f) > 0.025f ||
            MathF.Abs(compatibility.ScaleZ - 1f) > 0.025f);
    }

    [Fact]
    public void AnalyzeDualQuaternionCompatibility_ReflectedMatrix_IsRejected()
    {
        var compatibility = NifSkinningExtractor.AnalyzeDualQuaternionCompatibility(
        [
            Matrix4x4.CreateScale(-1f, 1f, 1f)
        ]);

        Assert.False(compatibility.CanUse);
        Assert.Equal(0, compatibility.MatrixIndex);
        Assert.True(compatibility.Determinant < 0f);
    }
}
