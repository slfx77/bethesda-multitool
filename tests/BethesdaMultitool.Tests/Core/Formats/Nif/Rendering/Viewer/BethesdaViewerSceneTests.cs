using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerSceneTests
{
    [Fact]
    public void GlbBridge_RoundTripPreservesIndicesHierarchySkinAndNativeMaterialObject()
    {
        var source = new GlbScene();
        var skeletonIndex = source.AddNode(
            "Spine_3",
            GlbScene.RootNodeIndex,
            Matrix4x4.CreateTranslation(1f, 2f, 3f),
            Matrix4x4.CreateTranslation(4f, 5f, 6f),
            GlbNodeKind.Skeleton,
            "Spine");
        var attachmentIndex = source.AddNode(
            "Weapon_4",
            skeletonIndex,
            Matrix4x4.CreateRotationZ(0.25f),
            Matrix4x4.CreateTranslation(7f, 8f, 9f),
            GlbNodeKind.Attachment,
            "Weapon");
        var submesh = new RenderableSubmesh
        {
            ShapeName = "Body",
            Positions = [0f, 0f, 0f, 2f, 4f, 6f, -1f, 1f, 3f],
            Triangles = [0, 1, 2],
            DiffuseTexturePath = @"textures\actors\body_d.dds",
            NormalMapTexturePath = @"textures\actors\body_n.dds",
            UseVertexColors = true,
            TintColor = (0.25f, 0.5f, 0.75f)
        };
        var jointIndices = new[] { skeletonIndex, attachmentIndex };
        var inverseBinds = new[]
        {
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(-4f, -5f, -6f)
        };
        (int BoneIdx, float Weight)[][] influences =
        [
            [(0, 0.75f), (1, 0.25f)],
            [(0, 1f)],
            [(1, 1f)]
        ];
        source.MeshParts.Add(new GlbMeshPart
        {
            Name = "Body",
            NodeIndex = attachmentIndex,
            Submesh = submesh,
            Skin = new GlbSkinBinding
            {
                JointNodeIndices = jointIndices,
                InverseBindMatrices = inverseBinds,
                PerVertexInfluences = influences
            }
        });

        var viewer = BethesdaViewerSceneGlbAdapter.FromGlbScene(
            source,
            "assembled-npc",
            BethesdaViewerScenePurpose.NpcAppearance,
            game: BethesdaGame.Fallout76);

        Assert.Equal(BethesdaViewerScene.CurrentContractVersion, viewer.ContractVersion);
        Assert.Equal("assembled-npc", viewer.SourceLabel);
        Assert.Equal(BethesdaViewerScenePurpose.NpcAppearance, viewer.Purpose);
        Assert.Equal(BethesdaGame.Fallout76, viewer.Game);
        Assert.Equal(source.Nodes.Count, viewer.Nodes.Count);
        Assert.Equal(skeletonIndex, viewer.Nodes[attachmentIndex].ParentIndex);
        Assert.Equal(BethesdaViewerNodeRole.Skeleton, viewer.Nodes[skeletonIndex].Role);
        Assert.Equal(BethesdaViewerNodeRole.Attachment, viewer.Nodes[attachmentIndex].Role);
        Assert.Equal(source.Nodes[skeletonIndex].LocalTransform, viewer.Nodes[skeletonIndex].LocalTransform);
        Assert.Equal(source.Nodes[attachmentIndex].WorldTransform, viewer.Nodes[attachmentIndex].WorldTransform);
        Assert.True(viewer.TryGetNodeIndex("Spine", out var resolvedSpine));
        Assert.Equal(skeletonIndex, resolvedSpine);
        Assert.Same(submesh, viewer.MeshParts[0].Submesh);
        Assert.Same(jointIndices, viewer.MeshParts[0].Skin!.JointNodeIndices);
        Assert.Same(inverseBinds, viewer.MeshParts[0].Skin!.InverseBindMatrices);
        Assert.Same(influences, viewer.MeshParts[0].Skin!.PerVertexInfluences);
        Assert.Equal(new Vector3(6f, 8f, 9f), viewer.Bounds!.Value.Minimum);
        Assert.Equal(new Vector3(9f, 12f, 15f), viewer.Bounds.Value.Maximum);

        var roundTrip = BethesdaViewerSceneGlbAdapter.ToGlbScene(viewer);

        Assert.Equal(source.Nodes.Count, roundTrip.Nodes.Count);
        for (var index = 0; index < source.Nodes.Count; index++)
        {
            Assert.Equal(source.Nodes[index].Name, roundTrip.Nodes[index].Name);
            Assert.Equal(source.Nodes[index].LookupName, roundTrip.Nodes[index].LookupName);
            Assert.Equal(source.Nodes[index].ParentIndex, roundTrip.Nodes[index].ParentIndex);
            Assert.Equal(source.Nodes[index].LocalTransform, roundTrip.Nodes[index].LocalTransform);
            Assert.Equal(source.Nodes[index].WorldTransform, roundTrip.Nodes[index].WorldTransform);
            Assert.Equal(source.Nodes[index].Kind, roundTrip.Nodes[index].Kind);
        }

        Assert.Equal(attachmentIndex, roundTrip.MeshParts[0].NodeIndex);
        Assert.Same(submesh, roundTrip.MeshParts[0].Submesh);
        Assert.Same(jointIndices, roundTrip.MeshParts[0].Skin!.JointNodeIndices);
        Assert.Same(inverseBinds, roundTrip.MeshParts[0].Skin!.InverseBindMatrices);
        Assert.Same(influences, roundTrip.MeshParts[0].Skin!.PerVertexInfluences);
        Assert.True(roundTrip.TryGetNodeIndex("Weapon", out var resolvedWeapon));
        Assert.Equal(attachmentIndex, resolvedWeapon);
    }

    [Fact]
    public void ContractCarriesMultipleNamedNodeMorphAndTextKeyClips()
    {
        var scene = new BethesdaViewerScene(
            "raw-face.nif",
            BethesdaViewerScenePurpose.RawNif);
        var faceNodeIndex = scene.AddNode(
            "Face",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            BethesdaViewerNodeRole.Skeleton,
            "Face");
        scene.AnimationClips.Add(new BethesdaViewerAnimationClip(
            "Idle",
            0f,
            2f,
            true,
            [
                new BethesdaViewerNodeAnimationTrack(
                    faceNodeIndex,
                    1f,
                    0f,
                    BethesdaViewerKeyInterpolation.Linear,
                    [
                        new BethesdaViewerQuaternionKey(0f, Quaternion.Identity),
                        new BethesdaViewerQuaternionKey(2f, Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, 0f))
                    ],
                    BethesdaViewerKeyInterpolation.Linear,
                    [new BethesdaViewerVector3Key(0f, Vector3.Zero)],
                    BethesdaViewerKeyInterpolation.Constant,
                    [new BethesdaViewerFloatKey(0f, 1f)])
            ],
            [
                new BethesdaViewerMorphWeightTrack(
                    0,
                    ["BlinkLeft", "BlinkRight"],
                    1f,
                    0f,
                    BethesdaViewerKeyInterpolation.Linear,
                    [
                        new BethesdaViewerMorphWeightKey(0f, [0f, 0f]),
                        new BethesdaViewerMorphWeightKey(0.25f, [1f, 1f]),
                        new BethesdaViewerMorphWeightKey(0.5f, [0f, 0f])
                    ])
            ],
            [new BethesdaViewerTextKey(0.25f, "blink")]));
        scene.AnimationClips.Add(new BethesdaViewerAnimationClip(
            "LookLeft",
            0f,
            1f,
            false,
            [],
            [],
            [new BethesdaViewerTextKey(1f, "done")]));

        Assert.Equal(2, scene.AnimationClips.Count);
        Assert.Equal("Idle", scene.AnimationClips[0].Name);
        Assert.Equal("LookLeft", scene.AnimationClips[1].Name);
        Assert.Equal(faceNodeIndex, scene.AnimationClips[0].NodeTracks[0].NodeIndex);
        Assert.Equal(2f, scene.AnimationClips[0].Duration);
        Assert.Equal(
            ["BlinkLeft", "BlinkRight"],
            scene.AnimationClips[0].MorphWeightTracks[0].TargetNames);
        Assert.Equal(
            [1f, 1f],
            scene.AnimationClips[0].MorphWeightTracks[0].Keys[1].Weights);
        Assert.Equal("blink", scene.AnimationClips[0].TextKeys[0].Label);
        Assert.False(scene.AnimationClips[1].Loops);
    }

    [Theory]
    // int-typed parameter: BethesdaViewerScenePurpose is internal, and a public test signature
    // exposing it is CS0051 — cast inside instead.
    [InlineData((int)BethesdaViewerScenePurpose.RawNif)]
    [InlineData((int)BethesdaViewerScenePurpose.NpcAppearance)]
    public void PurposeDistinguishesRawAndAssembledViewerInputs(int purposeValue)
    {
        var purpose = (BethesdaViewerScenePurpose)purposeValue;
        var scene = new BethesdaViewerScene("source", purpose);

        Assert.Equal(purpose, scene.Purpose);
        Assert.Equal(BethesdaViewerNodeRole.Root, scene.Nodes[BethesdaViewerScene.RootNodeIndex].Role);
    }

    [Fact]
    public void GeneratedTextures_NormalizeKeysAndOwnDeepSnapshots()
    {
        var basePixels = new byte[]
        {
            10, 20, 30, 255,
            40, 50, 60, 255,
            70, 80, 90, 255,
            100, 110, 120, 255
        };
        var mipPixels = new byte[] { 55, 65, 75, 255 };
        var sourceTexture = new DecodedTexture
        {
            MipLevels =
            [
                new DecodedTextureMipLevel
                {
                    Width = 2,
                    Height = 2,
                    Pixels = basePixels
                },
                new DecodedTextureMipLevel
                {
                    Width = 1,
                    Height = 1,
                    Pixels = mipPixels
                }
            ]
        };
        var scene = new BethesdaViewerScene("assembled-npc", BethesdaViewerScenePurpose.NpcAppearance);

        scene.AddGeneratedTexture(@"FaceGen_EGT/00000014_Player.DDS", sourceTexture);
        basePixels[0] = 200;
        mipPixels[0] = 210;

        Assert.Single(scene.GeneratedTextures);
        Assert.True(scene.TryGetGeneratedTexture(
            @"TEXTURES\facegen_egt\00000014_player.dds",
            out var captured));
        var capturedTexture = Assert.IsType<DecodedTexture>(captured);
        Assert.NotSame(sourceTexture, capturedTexture);
        Assert.NotSame(sourceTexture.MipLevels, capturedTexture.MipLevels);
        Assert.NotSame(basePixels, capturedTexture.Pixels);
        Assert.NotSame(mipPixels, capturedTexture.GetMipLevel(1).Pixels);
        Assert.Equal(10, capturedTexture.Pixels[0]);
        Assert.Equal(55, capturedTexture.GetMipLevel(1).Pixels[0]);
        Assert.Equal(2, capturedTexture.MipCount);
        Assert.True(scene.GeneratedTextures.ContainsKey(
            @"textures\FACEGEN_EGT\00000014_PLAYER.dds"));
    }

    [Fact]
    public void TextureSourcePaths_AreDeduplicatedImmutableDescriptors()
    {
        var sourcePaths = new[] { @"D:\Data\Textures.ba2", @"d:\data\textures.ba2", "  ", @"D:\Data\Materials.ba2" };
        var scene = new BethesdaViewerScene(
            "raw.nif",
            BethesdaViewerScenePurpose.RawNif,
            textureSourcePaths: sourcePaths);

        sourcePaths[0] = @"D:\Mutated.ba2";

        Assert.Equal(2, scene.TextureSourcePaths.Count);
        Assert.Equal(@"D:\Data\Textures.ba2", scene.TextureSourcePaths[0]);
        Assert.Equal(@"D:\Data\Materials.ba2", scene.TextureSourcePaths[1]);
    }
}
