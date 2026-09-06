using System.Numerics;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Retail gate for FNV's compact transform B-splines. AttackLeft has an authored Hit marker at
///     0.5 s and drives the pelvis/tail/spine/head/arms through 33 compact spline interpolators in
///     addition to 19 ordinary transform interpolators.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class FnvNightstalkerBsplineAnimationRetailTests(ITestOutputHelper output)
{
    private const string ModelPath = @"meshes\creatures\nightstalker\nvnightstalker.nif";
    private const string AnimationPath = @"meshes\creatures\nightstalker\h2hattackleft.kf";
    private const string ModelSha256 =
        "3EEA84CA6594AEE1A0308C6AA9B7D5DDE18410F0337B1967A31AAA92D215F1EF";
    private const string AnimationSha256 =
        "5CEB69C927CF1B6BD3532D4FF535178A2B0F668C1D05B038B37828607429E6B8";

    [Fact]
    public void AttackLeft_BindsAllFiftyTwoTracksAndMovesWholeBodyAtHitMarker()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile(
            "Fallout New Vegas",
            @"Data\Fallout - Meshes.bsa",
            @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("FNV Fallout - Meshes.bsa"));

        using var service = NifBrowserService.CreateFromBsa(archivePath!);
        var modelData = Assert.IsType<byte[]>(service.ReadNifData(ModelPath));
        Assert.Equal(ModelSha256, Convert.ToHexString(SHA256.HashData(modelData)));
        var build = service.BuildViewerSceneWithDiagnostics(
            modelData,
            "nvnightstalker.nif",
            ModelPath);
        var scene = Assert.IsType<BethesdaViewerScene>(build.Scene);
        Assert.Equal(2, scene.MeshParts.Count);
        Assert.Equal(55, scene.Nodes.Count);

        var catalog = Assert.IsType<NifModelFamilyAnimationCatalog>(scene.ModelFamilyAnimations);
        Assert.Equal(NifModelFamilyAnimationResolutionStatus.Resolved, catalog.Status);
        Assert.Equal(14, catalog.Animations.Count);
        var attack = Assert.Single(catalog.Animations, static asset =>
            string.Equals(asset.VirtualPath, AnimationPath, StringComparison.OrdinalIgnoreCase));
        var animationData = service.ReadModelFamilyAnimationData(
            attack,
            TestContext.Current.CancellationToken);
        Assert.Equal(28_291, animationData.Length);
        Assert.Equal(AnimationSha256, Convert.ToHexString(SHA256.HashData(animationData)));

        var nif = Assert.IsType<NifInfo>(NifParser.Parse(animationData));
        var source = Assert.Single(NifControllerSequenceNameTrackReader.ReadAll(animationData, nif));
        Assert.Equal("AttackLeft", source.Name);
        Assert.Equal(NifCycleType.Clamp, source.Cycle);
        Assert.Equal(0f, source.StartTime);
        Assert.Equal(1.5666666f, source.StopTime, 6);
        Assert.Equal(19, source.Tracks.Length);
        Assert.Equal(33, source.BsplineTracks!.Length);
        Assert.Equal(0, source.UnsupportedTransformTrackCount);
        var hit = Assert.Single(source.TextKeys, static key =>
            string.Equals(key.Label, "Hit", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0.5f, hit.Time, 6);

        var binding = BethesdaViewerKfAnimationBinder.ParseAndBind(
            animationData,
            scene,
            attack.RelativePath);
        Assert.True(binding.HasAcceptedClips, binding.Summary);
        var report = Assert.Single(binding.Reports);
        Assert.Equal(52, report.SourceTrackCount);
        Assert.Equal(52, report.BoundTrackCount);
        Assert.Equal(0, report.UnsupportedTransformTrackCount);
        Assert.Equal(0, report.MissingTargetTrackCount);
        Assert.Equal(0, report.AmbiguousTargetTrackCount);
        Assert.Equal(0, report.DuplicateSourceTrackCount);
        Assert.Equal(0, report.DestinationCollisionTrackCount);
        Assert.Null(report.FailureReason);

        var clip = Assert.Single(binding.AcceptedClips);
        var evaluator = new BethesdaViewerAnimationPoseEvaluator(
            scene.Nodes.Select(static node => node.LocalTransform).ToArray(),
            scene.Nodes.Select(static node => node.ParentIndex).ToArray(),
            clip);
        var authoredStart = new Matrix4x4[scene.Nodes.Count];
        var hitPose = new Matrix4x4[scene.Nodes.Count];
        var repeatedStart = new Matrix4x4[scene.Nodes.Count];
        evaluator.EvaluateNodeWorlds(0f, authoredStart);
        evaluator.EvaluateNodeWorlds(hit.Time, hitPose);
        evaluator.EvaluateNodeWorlds(0f, repeatedStart);
        for (var nodeIndex = 0; nodeIndex < authoredStart.Length; nodeIndex++)
        {
            Assert.Equal(authoredStart[nodeIndex], repeatedStart[nodeIndex]);
        }

        var discriminators = new[]
        {
            "Bip01 Pelvis",
            "Bip01 Spine0 Tail2",
            "Bip01 Head",
            "Bip01RUpperArm",
            "Bip01LUpperArm"
        };
        var deltas = new List<float>(discriminators.Length);
        foreach (var target in discriminators)
        {
            Assert.True(scene.TryGetNodeIndex(target, out var nodeIndex), target);
            var degrees = RotationDeltaDegrees(authoredStart[nodeIndex], hitPose[nodeIndex]);
            deltas.Add(degrees);
            output.WriteLine("{0}: t=0 -> Hit=0.5 world rotation delta = {1:F6} degrees", target, degrees);
            Assert.True(degrees > 2f, $"{target} moved only {degrees:F6} degrees at Hit=0.5");
        }

        Assert.True(deltas.Max() > 20f, $"largest whole-body discriminator was {deltas.Max():F6} degrees");
    }

    private static float RotationDeltaDegrees(Matrix4x4 first, Matrix4x4 second)
    {
        Assert.True(Matrix4x4.Decompose(first, out _, out var firstRotation, out _));
        Assert.True(Matrix4x4.Decompose(second, out _, out var secondRotation, out _));
        firstRotation = Quaternion.Normalize(firstRotation);
        secondRotation = Quaternion.Normalize(secondRotation);
        var dot = Math.Clamp(MathF.Abs(Quaternion.Dot(firstRotation, secondRotation)), 0f, 1f);
        return 2f * MathF.Acos(dot) * (180f / MathF.PI);
    }
}
