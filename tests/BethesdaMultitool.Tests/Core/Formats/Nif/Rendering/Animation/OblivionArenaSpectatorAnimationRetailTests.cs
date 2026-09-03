using System.Numerics;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Retail gate for Oblivion's self-contained arena spectator. This asset is not a humanoid body
///     part waiting for <c>meshes\characters\_male\skeleton.nif</c>: it owns its skin hierarchy and
///     an embedded palette-backed Idle sequence, including the non-humanoid <c>Lowerbody</c> joint.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class OblivionArenaSpectatorAnimationRetailTests
{
    private const string SpectatorPath =
        @"meshes\architecture\arena\arenaspectatorm01.nif";

    [Fact]
    public void MaleSpectator_CollectsEmbeddedIdleAgainstAuthoredInternalRig()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile(
            "Oblivion",
            @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));

        using var extractor = new BsaExtractor(archivePath!);
        var file = extractor.Archive.AllFiles.Single(record =>
            string.Equals(record.FullPath, SpectatorPath, StringComparison.OrdinalIgnoreCase));
        var data = extractor.ExtractFile(file);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));

        Assert.Equal(NifVersions.Gamebryo20004, nif.BinaryVersion);
        Assert.Equal(11u, nif.BsVersion);
        Assert.True(nif.HasInlineStrings);
        var signature = NifAnimationDetector.Detect(data, nif);
        Assert.True(signature.HasInternalSkin);
        Assert.True(signature.HasControllerSequenceTracks);

        var animation = NifControllerSequenceTrackCollector.Collect(
            data,
            nif,
            preserveFileRootTransformAndTrack: true);

        Assert.NotNull(animation);
        Assert.Equal(0f, animation.ClipStart);
        Assert.Equal(1.066667f, animation.ClipStop, 5);
        Assert.True(animation.ClipLoops);
        Assert.Equal(animation.Bones.Length, animation.Tracks.Length);
        var tracks = animation.Tracks.OfType<NifNodeTrack>().ToArray();
        Assert.Contains(tracks, static track =>
            string.Equals(track.NodeName, "Bip01 Head", StringComparison.OrdinalIgnoreCase) &&
            track.HasMotion);
        Assert.DoesNotContain(tracks, static track =>
            string.Equals(track.NodeName, "Bip01", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(track.NodeName, "Bip01 NonAccum", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(animation.Bones, static bone =>
            string.Equals(bone.Name, "Lowerbody", StringComparison.OrdinalIgnoreCase));
        Assert.All(animation.Bones, static bone => Assert.True(bone.SourceBlockIndex >= 0));

        var scene = Assert.IsType<GlbScene>(
            NifExportSceneBuilder.Build(data, nif, SpectatorPath));
        Assert.Equal(
            ["Hand", "Lowerbody:0", "UpperBody"],
            scene.MeshParts.Select(static part => part.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.All(scene.MeshParts, part =>
            Assert.False(NifBlockParsers.IsHiddenShape(
                data,
                nif.Blocks[part.Submesh.SourceBlockIndex],
                nif)));

        var atStart = new Matrix4x4[animation.Bones.Length];
        var atHalfSecond = new Matrix4x4[animation.Bones.Length];
        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, 0f, atStart);
        NifAnimationPoseEvaluator.EvaluateBoneWorlds(animation, 0.5f, atHalfSecond);
        Assert.All(atStart, AssertFinite);
        Assert.All(atHalfSecond, AssertFinite);
        Assert.Contains(
            Enumerable.Range(0, atStart.Length),
            index => MaximumElementDelta(atStart[index], atHalfSecond[index]) > 1e-4f);
    }

    private static void AssertFinite(Matrix4x4 value)
    {
        Assert.True(
            float.IsFinite(value.M11) && float.IsFinite(value.M12) &&
            float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
            float.IsFinite(value.M21) && float.IsFinite(value.M22) &&
            float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
            float.IsFinite(value.M31) && float.IsFinite(value.M32) &&
            float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
            float.IsFinite(value.M41) && float.IsFinite(value.M42) &&
            float.IsFinite(value.M43) && float.IsFinite(value.M44));
    }

    private static float MaximumElementDelta(Matrix4x4 left, Matrix4x4 right)
    {
        return new[]
        {
            MathF.Abs(left.M11 - right.M11), MathF.Abs(left.M12 - right.M12),
            MathF.Abs(left.M13 - right.M13), MathF.Abs(left.M14 - right.M14),
            MathF.Abs(left.M21 - right.M21), MathF.Abs(left.M22 - right.M22),
            MathF.Abs(left.M23 - right.M23), MathF.Abs(left.M24 - right.M24),
            MathF.Abs(left.M31 - right.M31), MathF.Abs(left.M32 - right.M32),
            MathF.Abs(left.M33 - right.M33), MathF.Abs(left.M34 - right.M34),
            MathF.Abs(left.M41 - right.M41), MathF.Abs(left.M42 - right.M42),
            MathF.Abs(left.M43 - right.M43), MathF.Abs(left.M44 - right.M44)
        }.Max();
    }
}
