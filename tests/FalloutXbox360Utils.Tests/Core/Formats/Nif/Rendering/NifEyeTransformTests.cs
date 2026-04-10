using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifEyeTransformTests
{
    private const float PositionEpsilon = 0.01f;
    private static readonly string SampleRoot = FindSampleRoot();
    private static readonly string EyeLeftPath = Path.Combine(SampleRoot, "head", "eyelefthuman.nif");
    private static readonly string EyeRightPath = Path.Combine(SampleRoot, "head", "eyerighthuman.nif");
    private static readonly string HeadHumanPath = Path.Combine(SampleRoot, "head", "headhuman.nif");

    [Theory]
    [InlineData(@"head\eyelefthuman.nif")]
    [InlineData(@"head\eyerighthuman.nif")]
    public void HumanEye_RootCompensation_RestoresBindPoseGeometry(string relativeEyePath)
    {
        var eyePath = Path.Combine(SampleRoot, relativeEyePath);
        var eyeNif = LoadNif(eyePath);
        Assert.NotNull(eyeNif);

        var extracted = NifGeometryExtractor.Extract(eyeNif.Value.Data, eyeNif.Value.Info);
        var bindPose = NifGeometryExtractor.Extract(eyeNif.Value.Data, eyeNif.Value.Info, bindPoseOnly: true);

        Assert.NotNull(extracted);
        Assert.NotNull(bindPose);
        Assert.True(extracted.HasGeometry);
        Assert.True(bindPose.HasGeometry);

        var before = GetBounds(extracted);
        var bindPoseBounds = GetBounds(bindPose);

        Assert.True(
            NpcRenderHelpers.TryGetRootRotationCompensation(
                eyeNif.Value.Data,
                eyeNif.Value.Info,
                out var compensation),
            $"Expected non-identity root compensation for '{Path.GetFileName(eyePath)}'");

        Assert.False(MatrixNearlyEqual(compensation, Matrix4x4.Identity, 0.001f));
        Assert.True(MathF.Abs(before.CenterX - bindPoseBounds.CenterX) > 1f ||
                    MathF.Abs(before.CenterZ - bindPoseBounds.CenterZ) > 1f);

        NpcRenderHelpers.TransformModel(extracted, compensation);

        AssertModelsEqual(bindPose, extracted, PositionEpsilon);
    }

    [Fact]
    public void HumanEyes_DirectBonelessAttachment_PlacesEyesInSockets()
    {
        var head = LoadHeadReference();
        var leftEye = AttachEye(EyeLeftPath, head.Bones, head.EyeAttachmentTransform, true);
        var rightEye = AttachEye(EyeRightPath, head.Bones, head.EyeAttachmentTransform, true);

        var leftBounds = GetBounds(leftEye);
        var rightBounds = GetBounds(rightEye);

        Assert.True(IsEyeInsideHeadSocket(leftBounds, -1f, head.Bounds));
        Assert.True(IsEyeInsideHeadSocket(rightBounds, 1f, head.Bounds));

        Assert.True(leftBounds.CenterX < 0f, $"Expected left eye center X < 0, got {leftBounds.CenterX:F3}");
        Assert.True(rightBounds.CenterX > 0f, $"Expected right eye center X > 0, got {rightBounds.CenterX:F3}");
        Assert.True(leftBounds.CenterX < rightBounds.CenterX);
    }

    [Fact]
    public void HumanEyes_WithoutRootCompensation_AreMisplacedByBonelessAttachment()
    {
        var head = LoadHeadReference();

        var leftCompensated = GetBounds(AttachEye(
            EyeLeftPath,
            head.Bones,
            head.EyeAttachmentTransform,
            true));
        var rightCompensated = GetBounds(AttachEye(
            EyeRightPath,
            head.Bones,
            head.EyeAttachmentTransform,
            true));
        var leftUncompensated = GetBounds(AttachEye(
            EyeLeftPath,
            head.Bones,
            head.EyeAttachmentTransform,
            false));
        var rightUncompensated = GetBounds(AttachEye(
            EyeRightPath,
            head.Bones,
            head.EyeAttachmentTransform,
            false));

        Assert.False(IsEyeInsideHeadSocket(leftUncompensated, -1f, head.Bounds));
        Assert.False(IsEyeInsideHeadSocket(rightUncompensated, 1f, head.Bounds));

        Assert.True(MathF.Abs(leftUncompensated.CenterX - leftCompensated.CenterX) > 4f);
        Assert.True(MathF.Abs(rightUncompensated.CenterX - rightCompensated.CenterX) > 4f);

        Assert.True(
            leftUncompensated.CenterX > head.Bounds.MaxX,
            $"Expected uncompensated left eye to sit outside the head bounds, got {leftUncompensated.CenterX:F3}");
        Assert.True(
            rightUncompensated.CenterX > head.Bounds.MaxX,
            $"Expected uncompensated right eye to sit outside the head bounds, got {rightUncompensated.CenterX:F3}");
    }

    private static HeadReference LoadHeadReference()
    {
        var headNif = LoadNif(HeadHumanPath);
        Assert.NotNull(headNif);

        var bones = NifGeometryExtractor.ExtractNamedBoneTransforms(headNif.Value.Data, headNif.Value.Info);
        Assert.True(bones.TryGetValue("Bip01 Head", out var headBone));

        var headModel = NifGeometryExtractor.Extract(
            headNif.Value.Data,
            headNif.Value.Info,
            externalBoneTransforms: bones,
            useDualQuaternionSkinning: true);
        Assert.NotNull(headModel);
        Assert.True(headModel.HasGeometry);

        var bonelessTransform = NpcRenderHelpers.BuildBonelessHeadAttachmentTransform(bones, null);
        var eyeAttachmentTransform = bonelessTransform ?? Matrix4x4.CreateTranslation(headBone.Translation);

        return new HeadReference(bones, GetBounds(headModel), eyeAttachmentTransform);
    }

    private static NifRenderableModel AttachEye(
        string eyePath,
        IReadOnlyDictionary<string, Matrix4x4> headBones,
        Matrix4x4 eyeAttachmentTransform,
        bool applyRootCompensation)
    {
        var eyeNif = LoadNif(eyePath);
        Assert.NotNull(eyeNif);

        var eyeModel = NifGeometryExtractor.Extract(eyeNif.Value.Data, eyeNif.Value.Info);
        Assert.NotNull(eyeModel);
        Assert.True(eyeModel.HasGeometry);

        if (applyRootCompensation)
        {
            Assert.True(
                NpcRenderHelpers.TryGetRootRotationCompensation(
                    eyeNif.Value.Data,
                    eyeNif.Value.Info,
                    out var compensation));
            NpcRenderHelpers.TransformModel(eyeModel, compensation);
        }

        Assert.True(headBones.ContainsKey("Bip01 Head"));
        NpcRenderHelpers.TransformModel(eyeModel, eyeAttachmentTransform);
        return eyeModel;
    }

    private static bool IsEyeInsideHeadSocket(ModelBounds eyeBounds, float expectedSign, ModelBounds headBounds)
    {
        var correctSide = expectedSign < 0f ? eyeBounds.CenterX < -0.5f : eyeBounds.CenterX > 0.5f;
        return correctSide &&
               eyeBounds.CenterX >= headBounds.MinX &&
               eyeBounds.CenterX <= headBounds.MaxX &&
               eyeBounds.CenterY >= headBounds.MinY &&
               eyeBounds.CenterY <= headBounds.MaxY &&
               eyeBounds.CenterZ >= headBounds.MinZ &&
               eyeBounds.CenterZ <= headBounds.MaxZ;
    }

    private static void AssertModelsEqual(
        NifRenderableModel expected,
        NifRenderableModel actual,
        float epsilon)
    {
        Assert.Equal(expected.Submeshes.Count, actual.Submeshes.Count);

        for (var submeshIndex = 0; submeshIndex < expected.Submeshes.Count; submeshIndex++)
        {
            var expectedSubmesh = expected.Submeshes[submeshIndex];
            var actualSubmesh = actual.Submeshes[submeshIndex];

            Assert.Equal(expectedSubmesh.VertexCount, actualSubmesh.VertexCount);
            Assert.Equal(expectedSubmesh.Positions.Length, actualSubmesh.Positions.Length);

            for (var i = 0; i < expectedSubmesh.Positions.Length; i++)
            {
                Assert.InRange(
                    actualSubmesh.Positions[i],
                    expectedSubmesh.Positions[i] - epsilon,
                    expectedSubmesh.Positions[i] + epsilon);
            }
        }
    }

    private static bool MatrixNearlyEqual(Matrix4x4 left, Matrix4x4 right, float epsilon)
    {
        return NearlyEqual(left.M11, right.M11, epsilon) &&
               NearlyEqual(left.M12, right.M12, epsilon) &&
               NearlyEqual(left.M13, right.M13, epsilon) &&
               NearlyEqual(left.M14, right.M14, epsilon) &&
               NearlyEqual(left.M21, right.M21, epsilon) &&
               NearlyEqual(left.M22, right.M22, epsilon) &&
               NearlyEqual(left.M23, right.M23, epsilon) &&
               NearlyEqual(left.M24, right.M24, epsilon) &&
               NearlyEqual(left.M31, right.M31, epsilon) &&
               NearlyEqual(left.M32, right.M32, epsilon) &&
               NearlyEqual(left.M33, right.M33, epsilon) &&
               NearlyEqual(left.M34, right.M34, epsilon) &&
               NearlyEqual(left.M41, right.M41, epsilon) &&
               NearlyEqual(left.M42, right.M42, epsilon) &&
               NearlyEqual(left.M43, right.M43, epsilon) &&
               NearlyEqual(left.M44, right.M44, epsilon);
    }

    private static bool NearlyEqual(float left, float right, float epsilon)
    {
        return MathF.Abs(left - right) <= epsilon;
    }

    private static ModelBounds GetBounds(NifRenderableModel model)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

        foreach (var sub in model.Submeshes)
        {
            for (var i = 0; i < sub.Positions.Length; i += 3)
            {
                var x = sub.Positions[i];
                var y = sub.Positions[i + 1];
                var z = sub.Positions[i + 2];
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (z < minZ) minZ = z;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                if (z > maxZ) maxZ = z;
            }
        }

        return new ModelBounds(minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static (byte[] Data, NifInfo Info)? LoadNif(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return null;

        var data = File.ReadAllBytes(fullPath);
        var nif = NifParser.Parse(data);
        return nif != null ? (data, nif) : null;
    }

    private static string FindSampleRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(
                dir,
                "Sample",
                "Unpacked_Builds",
                "PC_Final_Unpacked",
                "Data",
                "meshes",
                "characters");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir)!;
        }

        return Path.Combine(
            "Sample",
            "Unpacked_Builds",
            "PC_Final_Unpacked",
            "Data",
            "meshes",
            "characters");
    }

    private readonly record struct ModelBounds(
        float MinX,
        float MinY,
        float MinZ,
        float MaxX,
        float MaxY,
        float MaxZ)
    {
        public float CenterX => (MinX + MaxX) * 0.5f;
        public float CenterY => (MinY + MaxY) * 0.5f;
        public float CenterZ => (MinZ + MaxZ) * 0.5f;
    }

    private readonly record struct HeadReference(
        IReadOnlyDictionary<string, Matrix4x4> Bones,
        ModelBounds Bounds,
        Matrix4x4 EyeAttachmentTransform);
}