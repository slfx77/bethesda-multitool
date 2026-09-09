using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerKfAnimationBinderTests
{
    [Fact]
    public void Bind_AggregatesPerSequenceReportsWithoutMutatingScene()
    {
        var scene = new BethesdaViewerScene("body.nif", BethesdaViewerScenePurpose.RawNif);
        scene.AddNode(
            "Head",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            "Bip01 Head");
        var supported = Clip("Idle", Track("Bip01 Head"), 2);
        var missing = Clip("Aim", Track("Missing Bone"));

        var result = BethesdaViewerKfAnimationBinder.Bind(
            scene,
            [supported, missing],
            "idle.kf");

        Assert.True(result.HasAcceptedClips);
        Assert.Equal("idle.kf", result.SourceLabel);
        Assert.Equal(2, result.SourceSequenceCount);
        Assert.Single(result.AcceptedClips);
        Assert.Equal(2, result.Reports.Count);
        Assert.Contains("Loaded 1/2", result.Summary, StringComparison.Ordinal);
        Assert.Contains("2 BSpline/unsupported", result.Summary, StringComparison.Ordinal);
        Assert.Contains("1 non-unique or missing", result.Summary, StringComparison.Ordinal);
        Assert.Empty(scene.AnimationClips);
    }

    [Fact]
    public void Bind_EmptyReaderResultReportsSupportedLayoutBoundary()
    {
        var scene = new BethesdaViewerScene("body.nif", BethesdaViewerScenePurpose.RawNif);

        var result = BethesdaViewerKfAnimationBinder.Bind(scene, [], "unsupported.kf");

        Assert.False(result.HasAcceptedClips);
        Assert.Empty(result.AcceptedClips);
        Assert.Empty(result.Reports);
        Assert.Contains("No supported controller sequence", result.Summary, StringComparison.Ordinal);
    }

    private static NifNameTargetedAnimationClip Clip(
        string name,
        NifNodeTrack track,
        int unsupportedCount = 0)
    {
        return new NifNameTargetedAnimationClip(
            name,
            1f,
            0f,
            2f,
            NifCycleType.Loop,
            null,
            [track],
            [],
            unsupportedCount);
    }

    private static NifNodeTrack Track(string nodeName)
    {
        return new NifNodeTrack(
            nodeName,
            1f,
            0f,
            NifKeyInterpolation.Linear,
            [],
            NifKeyInterpolation.Linear,
            [
                new NifVec3Key(0f, Vector3.Zero),
                new NifVec3Key(2f, Vector3.UnitX)
            ],
            NifKeyInterpolation.Linear,
            []);
    }
}