using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcHeadBoneCorrectionTests
{
    [Fact]
    public void BuildBonelessHeadAttachmentTransform_HeadOnlyUsesTranslationOnly()
    {
        var attachmentBones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 Head"] = CreateTransform(
                translation: new Vector3(1f, 2f, 3f),
                rotation: Matrix4x4.CreateRotationX(0.35f))
        };

        var result = NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(
            attachmentBones,
            poseDeltaCache: null);

        Assert.True(result.HasValue);
        AssertMatrixEqual(Matrix4x4.CreateTranslation(1f, 2f, 3f), result.Value);
    }

    [Fact]
    public void BuildBonelessHeadAttachmentTransform_FullBodyUsesPoseDeltaRotationAndTranslation()
    {
        var attachmentBones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 Head"] = CreateTransform(
                translation: new Vector3(4f, 5f, 6f),
                rotation: Matrix4x4.CreateRotationX(0.35f))
        };
        var poseDeltas = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 Head"] = Matrix4x4.CreateRotationZ(-0.2f) * Matrix4x4.CreateTranslation(99f, 88f, 77f)
        };

        var result = NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(
            attachmentBones,
            poseDeltas);

        Assert.True(result.HasValue);
        var expected =
            Matrix4x4.CreateRotationZ(-0.2f) *
            Matrix4x4.CreateTranslation(4f, 5f, 6f);
        AssertMatrixEqual(expected, result.Value);
    }

    [Fact]
    public void GetHeadAttachmentCorrection_BonelessIdentityRoot_UsesBonelessAttachmentTransform()
    {
        var targetHeadTransform = CreateTransform(
            translation: new Vector3(1f, 2f, 3f),
            rotation: Matrix4x4.CreateRotationX(0.35f));
        var bonelessAttachmentTransform = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var nifBones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["__root__"] = Matrix4x4.CreateTranslation(25f, -8f, 13f)
        };

        var result = NpcRenderHelpers.GetHeadAttachmentCorrection(
            nifBones,
            targetHeadTransform,
            bonelessAttachmentTransform);

        Assert.Equal(
            NpcRenderHelpers.HeadAttachmentCorrectionMode.BonelessUseAttachmentTransform,
            result.Mode);
        AssertMatrixEqual(bonelessAttachmentTransform, result.Correction);
    }

    [Fact]
    public void GetHeadAttachmentCorrection_BonelessRotatedRoot_UsesBonelessAttachmentTransform()
    {
        var targetHeadTransform = CreateTransform(
            translation: new Vector3(4f, 5f, 6f),
            rotation: Matrix4x4.CreateRotationY(-0.25f));
        var bonelessAttachmentTransform =
            Matrix4x4.CreateRotationZ(-0.15f) *
            Matrix4x4.CreateTranslation(4f, 5f, 6f);
        var rootRotation = Matrix4x4.CreateRotationZ(MathF.PI / 2f);
        var nifBones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["__root__"] = rootRotation * Matrix4x4.CreateTranslation(20f, 30f, 40f)
        };

        var result = NpcRenderHelpers.GetHeadAttachmentCorrection(
            nifBones,
            targetHeadTransform,
            bonelessAttachmentTransform);

        Assert.Equal(
            NpcRenderHelpers.HeadAttachmentCorrectionMode.BonelessUseAttachmentTransform,
            result.Mode);
        AssertMatrixEqual(bonelessAttachmentTransform, result.Correction);
    }

    [Fact]
    public void GetHeadAttachmentCorrection_BonelessWithoutOverride_UsesTargetHeadTransform()
    {
        var targetHeadTransform = CreateTransform(
            translation: new Vector3(4f, 5f, 6f),
            rotation: Matrix4x4.CreateRotationY(-0.25f));
        var rootRotation = Matrix4x4.CreateRotationY(-MathF.PI / 2f);
        var nifBones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["__root__"] = rootRotation * Matrix4x4.CreateTranslation(20f, 30f, 40f)
        };

        var result = NpcRenderHelpers.GetHeadAttachmentCorrection(nifBones, targetHeadTransform);

        Assert.Equal(
            NpcRenderHelpers.HeadAttachmentCorrectionMode.BonelessUseAttachmentTransform,
            result.Mode);
        AssertMatrixEqual(targetHeadTransform, result.Correction);
    }

    [Fact]
    public void GetHeadAttachmentCorrection_BonelessReflectedRoot_UsesBonelessAttachmentTransform()
    {
        var targetHeadTransform = CreateTransform(
            translation: new Vector3(7f, 8f, 9f),
            rotation: Matrix4x4.CreateRotationZ(0.4f));
        var bonelessAttachmentTransform = Matrix4x4.CreateTranslation(7f, 8f, 9f);
        var rootScale = Matrix4x4.CreateScale(-1f, 1f, 1f);
        var nifBones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["__root__"] = rootScale * Matrix4x4.CreateTranslation(12f, 13f, 14f)
        };

        var result = NpcRenderHelpers.GetHeadAttachmentCorrection(
            nifBones,
            targetHeadTransform,
            bonelessAttachmentTransform);

        Assert.Equal(
            NpcRenderHelpers.HeadAttachmentCorrectionMode.BonelessUseAttachmentTransform,
            result.Mode);
        AssertMatrixEqual(bonelessAttachmentTransform, result.Correction);
    }

    [Fact]
    public void GetHeadAttachmentCorrection_BonedAttachment_UsesInverseHeadTimesTarget()
    {
        var targetHeadTransform = CreateTransform(
            translation: new Vector3(3f, 2f, 1f),
            rotation: Matrix4x4.CreateRotationX(0.15f) * Matrix4x4.CreateRotationY(0.2f));
        var nifHeadTransform = CreateTransform(
            translation: new Vector3(11f, 12f, 13f),
            rotation: Matrix4x4.CreateRotationZ(-0.3f));
        Matrix4x4.Invert(nifHeadTransform, out var invNifHeadTransform);
        var nifBones = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bip01 Head"] = nifHeadTransform
        };
        var expected = invNifHeadTransform * targetHeadTransform;

        var result = NpcRenderHelpers.GetHeadAttachmentCorrection(nifBones, targetHeadTransform);

        Assert.Equal(NpcRenderHelpers.HeadAttachmentCorrectionMode.Boned, result.Mode);
        AssertMatrixEqual(expected, result.Correction);
    }

    private static Matrix4x4 CreateTransform(Vector3 translation, Matrix4x4 rotation)
    {
        rotation.M41 = translation.X;
        rotation.M42 = translation.Y;
        rotation.M43 = translation.Z;
        rotation.M44 = 1f;
        return rotation;
    }

    private static void AssertMatrixEqual(Matrix4x4 expected, Matrix4x4 actual, float epsilon = 0.001f)
    {
        AssertClose(expected.M11, actual.M11, epsilon);
        AssertClose(expected.M12, actual.M12, epsilon);
        AssertClose(expected.M13, actual.M13, epsilon);
        AssertClose(expected.M14, actual.M14, epsilon);
        AssertClose(expected.M21, actual.M21, epsilon);
        AssertClose(expected.M22, actual.M22, epsilon);
        AssertClose(expected.M23, actual.M23, epsilon);
        AssertClose(expected.M24, actual.M24, epsilon);
        AssertClose(expected.M31, actual.M31, epsilon);
        AssertClose(expected.M32, actual.M32, epsilon);
        AssertClose(expected.M33, actual.M33, epsilon);
        AssertClose(expected.M34, actual.M34, epsilon);
        AssertClose(expected.M41, actual.M41, epsilon);
        AssertClose(expected.M42, actual.M42, epsilon);
        AssertClose(expected.M43, actual.M43, epsilon);
        AssertClose(expected.M44, actual.M44, epsilon);
    }

    private static void AssertClose(float expected, float actual, float epsilon)
    {
        Assert.InRange(actual, expected - epsilon, expected + epsilon);
    }
}
