using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerNameTargetedAnimationAdapterTests
{
    [Fact]
    public void TryCreateClip_UniqueNameBindingNormalizesTheOuterSequenceClock()
    {
        var scene = SceneWithNode("Head display", "Bip01 Head");
        var sourceTrack = Track(
            "bip01 head",
            new NifVec3Key(2f, Vector3.Zero),
            new NifVec3Key(6f, new Vector3(4f, 0f, 0f)));
        var source = Clip(
            frequency: 2f,
            startTime: 2f,
            stopTime: 6f,
            tracks: [sourceTrack],
            textKeys: [new NifAnimTextKey(4f, "Footstep")],
            unsupportedCount: 3);

        var clip = BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(
            scene,
            source,
            suppressAccumulatedRootMotion: true,
            out var report);

        Assert.NotNull(clip);
        Assert.Equal(0f, clip.StartTime);
        Assert.Equal(2f, clip.EndTime);
        Assert.True(clip.Loops);
        var bound = Assert.Single(clip.NodeTracks);
        Assert.Equal(1, bound.NodeIndex);
        Assert.Equal(1f, bound.Frequency);
        Assert.Equal(0f, bound.Phase);
        Assert.Equal(2f, bound.TranslationKeys[1].Time);
        Assert.Equal(1f, Assert.Single(clip.TextKeys).Time);
        Assert.Equal(1, report.BoundTrackCount);
        Assert.Equal(3, report.UnsupportedTransformTrackCount);
        Assert.Null(report.FailureReason);
        Assert.Empty(scene.AnimationClips); // creation is transactional; caller owns attachment

        sourceTrack.TranslationKeys[1] = new NifVec3Key(6f, new Vector3(99f));
        source.TextKeys[0] = new NifAnimTextKey(4f, "Changed");
        Assert.Equal(4f, bound.TranslationKeys[1].Value.X);
        Assert.Equal("Footstep", clip.TextKeys[0].Label);
    }

    [Fact]
    public void TryCreateClip_AmbiguousDestinationNameFailsWithoutMutatingScene()
    {
        var scene = new BethesdaViewerScene("test", BethesdaViewerScenePurpose.NpcAppearance);
        scene.AddNode(
            "Head A",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            "Bip01 Head");
        scene.AddNode(
            "Head B",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            "Bip01 Head");

        var clip = BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(
            scene,
            Clip(tracks: [Track("Bip01 Head")]),
            true,
            out var report);

        Assert.Null(clip);
        Assert.Equal(1, report.AmbiguousTargetTrackCount);
        Assert.Empty(scene.AnimationClips);
    }

    [Fact]
    public void TryCreateClip_DuplicateSourceTargetsRejectEveryDuplicate()
    {
        var scene = SceneWithNode("Head", "Bip01 Head");
        var source = Clip(tracks: [Track("Bip01 Head"), Track("bip01 head")]);

        var clip = BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(
            scene,
            source,
            false,
            out var report);

        Assert.Null(clip);
        Assert.Equal(2, report.DuplicateSourceTrackCount);
        Assert.Equal(0, report.BoundTrackCount);
    }

    [Fact]
    public void TryCreateClip_TwoAliasesCannotClaimTheSameDestinationNode()
    {
        var scene = SceneWithNode("Head Display", "Bip01 Head");
        var source = Clip(tracks: [Track("Head Display"), Track("Bip01 Head")]);

        var clip = BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(
            scene,
            source,
            false,
            out var report);

        Assert.Null(clip);
        Assert.Equal(2, report.DestinationCollisionTrackCount);
    }

    [Fact]
    public void TryCreateClip_AccumulationRootSuppressionLeavesOtherTracksPlayable()
    {
        var scene = new BethesdaViewerScene("test", BethesdaViewerScenePurpose.NpcAppearance);
        scene.AddNode(
            "Pelvis",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            "Bip01 Pelvis");
        scene.AddNode(
            "Head",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            "Bip01 Head");
        var source = Clip(
            tracks: [Track("Bip01 Pelvis"), Track("Bip01 Head")],
            accumRootName: "Bip01 Pelvis");

        var clip = BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(
            scene,
            source,
            true,
            out var report);

        Assert.NotNull(clip);
        Assert.Equal(2, Assert.Single(clip.NodeTracks).NodeIndex);
        Assert.Equal(1, report.SuppressedAccumRootTrackCount);
    }

    // The cycle travels as an int because NifCycleType is internal, and a public xUnit theory
    // parameter of an internal type is a CS0051 accessibility error.
    [Theory]
    [InlineData((int)NifCycleType.Reverse)]
    [InlineData(99)]
    public void TryCreateClip_ReverseAndUnknownCyclesFailClosed(int cycleValue)
    {
        var cycle = (NifCycleType)cycleValue;
        var scene = SceneWithNode("Head", "Bip01 Head");

        var clip = BethesdaViewerNameTargetedAnimationAdapter.TryCreateClip(
            scene,
            Clip(cycle: cycle, tracks: [Track("Bip01 Head")]),
            false,
            out var report);

        Assert.Null(clip);
        Assert.NotNull(report.FailureReason);
        Assert.Empty(scene.AnimationClips);
    }

    private static BethesdaViewerScene SceneWithNode(string name, string lookupName)
    {
        var scene = new BethesdaViewerScene("test", BethesdaViewerScenePurpose.NpcAppearance);
        scene.AddNode(
            name,
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            lookupName);
        return scene;
    }

    private static NifNameTargetedAnimationClip Clip(
        float frequency = 1f,
        float startTime = 0f,
        float stopTime = 4f,
        NifCycleType cycle = NifCycleType.Loop,
        NifNodeTrack[]? tracks = null,
        NifAnimTextKey[]? textKeys = null,
        string? accumRootName = null,
        int unsupportedCount = 0)
    {
        return new NifNameTargetedAnimationClip(
            "Idle",
            frequency,
            startTime,
            stopTime,
            cycle,
            accumRootName,
            tracks ?? [],
            textKeys ?? [],
            unsupportedCount);
    }

    private static NifNodeTrack Track(string nodeName, params NifVec3Key[] translationKeys)
    {
        return new NifNodeTrack(
            nodeName,
            1f,
            0f,
            NifKeyInterpolation.Linear,
            [],
            NifKeyInterpolation.Linear,
            translationKeys.Length > 0
                ? translationKeys
                : [new NifVec3Key(0f, Vector3.Zero)],
            NifKeyInterpolation.Linear,
            []);
    }
}
