using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerNifAnimationAdapterTests
{
    [Fact]
    public void SourceBlockIdentityBindsDuplicateNamesToTheExactNode()
    {
        var scene = new BethesdaViewerScene("duplicate.nif", BethesdaViewerScenePurpose.RawNif);
        scene.AddNode(
            "Bone_4",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            "Bone",
            sourceBlockIndex: 4);
        var expectedNode = scene.AddNode(
            "Bone_9",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            "Bone",
            sourceBlockIndex: 9);
        var animation = Animation(sourceBlockIndex: 9);

        var clip = BethesdaViewerNifAnimationAdapter.TryCreateClip(scene, animation, "Idle");

        Assert.NotNull(clip);
        Assert.Equal("Idle", clip.Name);
        Assert.Equal(expectedNode, Assert.Single(clip.NodeTracks).NodeIndex);
        Assert.Equal(2f, clip.Duration);
        Assert.Equal("loop", Assert.Single(clip.TextKeys).Label);
    }

    [Fact]
    public void AmbiguousNameWithoutBlockIdentityIsNotSilentlyRetargeted()
    {
        var scene = new BethesdaViewerScene("duplicate.nif", BethesdaViewerScenePurpose.RawNif);
        for (var index = 0; index < 2; index++)
        {
            scene.AddNode(
                $"Node{index}",
                BethesdaViewerScene.RootNodeIndex,
                Matrix4x4.Identity,
                Matrix4x4.Identity,
                BethesdaViewerNodeRole.Skeleton,
                "Bone");
        }

        Assert.Null(BethesdaViewerNifAnimationAdapter.TryCreateClip(scene, Animation(-1)));
    }

    [Fact]
    public void MissingSourceBlockIdentityDoesNotFallBackToMatchingName()
    {
        var scene = new BethesdaViewerScene("mismatch.nif", BethesdaViewerScenePurpose.RawNif);
        scene.AddNode(
            "Bone_4",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            "Bone",
            sourceBlockIndex: 4);

        Assert.Null(BethesdaViewerNifAnimationAdapter.TryCreateClip(scene, Animation(9)));
    }

    private static NifMeshAnimation Animation(int sourceBlockIndex)
    {
        return new NifMeshAnimation(
            [new NifAnimBone("Bone", -1, Vector3.Zero, Quaternion.Identity, 1f, sourceBlockIndex)],
            [new NifNodeTrack(
                "Bone",
                1f,
                0f,
                NifKeyInterpolation.Linear,
                [
                    new NifQuatKey(0f, Quaternion.Identity),
                    new NifQuatKey(2f, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f))
                ],
                NifKeyInterpolation.Linear,
                [],
                NifKeyInterpolation.Linear,
                [])],
            [new NifAnimTextKey(1f, "loop")],
            0f,
            2f,
            true);
    }
}
