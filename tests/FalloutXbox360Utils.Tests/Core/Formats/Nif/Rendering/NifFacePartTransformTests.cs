using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifFacePartTransformTests
{
    private static readonly string SampleRoot = NifSampleLoader.FindCharactersSampleRoot();

    [Theory]
    [InlineData(@"head\mouthhuman.nif")]
    [InlineData(@"head\teethlowerhuman.nif")]
    [InlineData(@"head\teethupperhuman.nif")]
    [InlineData(@"head\tonguehuman.nif")]
    public void HumanFacePart_RotatedRoot_RequiresCompensatedHeadAttachment(string relativePath)
    {
        var nif = NifSampleLoader.LoadNif(Path.Combine(SampleRoot, relativePath));
        Assert.NotNull(nif);

        Assert.True(
            NpcRenderHelpers.TryGetRootRotationCompensation(
                nif.Value.Data,
                nif.Value.Info,
                out var rootCompensation),
            $"Expected rotated root for '{relativePath}'");
        Assert.False(MatrixAssert.NearlyEqual(rootCompensation, Matrix4x4.Identity, 0.001f));

        var nifBones = NifGeometryExtractor.ExtractNamedBoneTransforms(nif.Value.Data, nif.Value.Info);
        Assert.True(nifBones.ContainsKey(NifGeometryExtractor.RootTransformKey));
        Assert.Contains(
            nifBones.Keys,
            name => !string.Equals(name, NifGeometryExtractor.RootTransformKey, StringComparison.OrdinalIgnoreCase));

        var targetHeadTransform =
            Matrix4x4.CreateRotationX(0.1f) *
            Matrix4x4.CreateRotationY(-0.2f) *
            Matrix4x4.CreateTranslation(3f, 5f, 7f);
        var bonelessAttachmentTransform = Matrix4x4.CreateTranslation(20f, 30f, 40f);

        var preserved = NpcRenderHelpers.GetHeadAttachmentCorrection(
            nifBones,
            targetHeadTransform,
            bonelessAttachmentTransform);
        var compensated = NpcRenderHelpers.GetHeadAttachmentCorrection(
            nifBones,
            targetHeadTransform,
            bonelessAttachmentTransform,
            NpcRenderHelpers.HeadAttachmentRootPolicy.CompensateRotatedRoot);

        MatrixAssert.Equal(bonelessAttachmentTransform, preserved.Correction);
        MatrixAssert.Equal(rootCompensation * bonelessAttachmentTransform, compensated.Correction);
    }
}