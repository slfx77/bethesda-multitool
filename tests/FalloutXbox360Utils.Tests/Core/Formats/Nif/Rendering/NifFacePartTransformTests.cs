using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifFacePartTransformTests
{
    private static readonly string SampleRoot = FindSampleRoot();

    [Theory]
    [InlineData(@"head\mouthhuman.nif")]
    [InlineData(@"head\teethlowerhuman.nif")]
    [InlineData(@"head\teethupperhuman.nif")]
    [InlineData(@"head\tonguehuman.nif")]
    public void HumanFacePart_RotatedRoot_RequiresCompensatedHeadAttachment(string relativePath)
    {
        var nif = LoadNif(Path.Combine(SampleRoot, relativePath));
        Assert.NotNull(nif);

        Assert.True(
            NpcRenderHelpers.TryGetRootRotationCompensation(
                nif.Value.Data,
                nif.Value.Info,
                out var rootCompensation),
            $"Expected rotated root for '{relativePath}'");
        Assert.False(MatrixNearlyEqual(rootCompensation, Matrix4x4.Identity, 0.001f));

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

        AssertMatrixEqual(bonelessAttachmentTransform, preserved.Correction);
        AssertMatrixEqual(rootCompensation * bonelessAttachmentTransform, compensated.Correction);
    }

    private static (byte[] Data, NifInfo Info)? LoadNif(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            return null;
        }

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
            {
                return candidate;
            }

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

    private static void AssertMatrixEqual(Matrix4x4 expected, Matrix4x4 actual, float epsilon = 0.001f)
    {
        Assert.InRange(actual.M11, expected.M11 - epsilon, expected.M11 + epsilon);
        Assert.InRange(actual.M12, expected.M12 - epsilon, expected.M12 + epsilon);
        Assert.InRange(actual.M13, expected.M13 - epsilon, expected.M13 + epsilon);
        Assert.InRange(actual.M14, expected.M14 - epsilon, expected.M14 + epsilon);
        Assert.InRange(actual.M21, expected.M21 - epsilon, expected.M21 + epsilon);
        Assert.InRange(actual.M22, expected.M22 - epsilon, expected.M22 + epsilon);
        Assert.InRange(actual.M23, expected.M23 - epsilon, expected.M23 + epsilon);
        Assert.InRange(actual.M24, expected.M24 - epsilon, expected.M24 + epsilon);
        Assert.InRange(actual.M31, expected.M31 - epsilon, expected.M31 + epsilon);
        Assert.InRange(actual.M32, expected.M32 - epsilon, expected.M32 + epsilon);
        Assert.InRange(actual.M33, expected.M33 - epsilon, expected.M33 + epsilon);
        Assert.InRange(actual.M34, expected.M34 - epsilon, expected.M34 + epsilon);
        Assert.InRange(actual.M41, expected.M41 - epsilon, expected.M41 + epsilon);
        Assert.InRange(actual.M42, expected.M42 - epsilon, expected.M42 + epsilon);
        Assert.InRange(actual.M43, expected.M43 - epsilon, expected.M43 + epsilon);
        Assert.InRange(actual.M44, expected.M44 - epsilon, expected.M44 + epsilon);
    }

    private static bool MatrixNearlyEqual(Matrix4x4 left, Matrix4x4 right, float epsilon)
    {
        return MathF.Abs(left.M11 - right.M11) <= epsilon &&
               MathF.Abs(left.M12 - right.M12) <= epsilon &&
               MathF.Abs(left.M13 - right.M13) <= epsilon &&
               MathF.Abs(left.M14 - right.M14) <= epsilon &&
               MathF.Abs(left.M21 - right.M21) <= epsilon &&
               MathF.Abs(left.M22 - right.M22) <= epsilon &&
               MathF.Abs(left.M23 - right.M23) <= epsilon &&
               MathF.Abs(left.M24 - right.M24) <= epsilon &&
               MathF.Abs(left.M31 - right.M31) <= epsilon &&
               MathF.Abs(left.M32 - right.M32) <= epsilon &&
               MathF.Abs(left.M33 - right.M33) <= epsilon &&
               MathF.Abs(left.M34 - right.M34) <= epsilon &&
               MathF.Abs(left.M41 - right.M41) <= epsilon &&
               MathF.Abs(left.M42 - right.M42) <= epsilon &&
               MathF.Abs(left.M43 - right.M43) <= epsilon &&
               MathF.Abs(left.M44 - right.M44) <= epsilon;
    }
}