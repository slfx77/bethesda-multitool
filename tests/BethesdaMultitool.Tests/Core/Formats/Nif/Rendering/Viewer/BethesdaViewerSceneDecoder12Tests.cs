using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerSceneDecoder12Tests
{
    [Fact]
    public void DecodePreservesSceneGraphResourcesSkinsNpcSemanticsSkyWaterAndSeams()
    {
        var scene = new BethesdaViewerScene(
            "assembled-npc",
            BethesdaViewerScenePurpose.NpcAppearance,
            new BethesdaViewerBounds(new Vector3(-1f), new Vector3(2f)),
            BethesdaGame.Fallout76,
            [@"C:\Game\Data\Textures.ba2", @"C:\Game\Data\Fallout76.ba2"]);
        var skeletonNode = scene.AddNode(
            "Spine_1",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.CreateTranslation(1f, 2f, 3f),
            Matrix4x4.CreateTranslation(4f, 5f, 6f),
            BethesdaViewerNodeRole.Skeleton,
            "Spine");
        var shaderMetadata = new NifShaderTextureMetadata
        {
            PropertyType = "BSLightingShaderProperty",
            MaterialPath = @"materials\actors\body.bgsm"
        };
        var bindPose = new[] { 10f, 11f, 12f, 13f, 14f, 15f, 16f, 17f, 18f };
        var npc = new RenderableSubmesh
        {
            ShapeName = "Face",
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            DiffuseTexturePath = @"textures\facegen_egt\npc.dds",
            ShaderMetadata = shaderMetadata,
            UseVertexColors = true,
            UseVertexAlphaForOpacity = false,
            MaterialDiffuse = (0.2f, 0.3f, 0.4f),
            IsEyeEnvmap = true,
            EnvMapScale = 0.75f,
            RenderOrder = 7,
            TintColor = (0.25f, 0.5f, 0.75f),
            IsFaceGen = true,
            SubsurfaceColor = (0.8f, 0.35f, 0.2f),
            AnimatedEmissiveColor = (0.1f, 0.2f, 0.3f),
            EmissiveColor = (0.4f, 0.5f, 0.6f),
            BindPosePositions = bindPose,
            SourceNifPath = @"meshes\actors\head.nif",
            IsFarLodFallback = true
        };
        var jointIndices = new[] { skeletonNode };
        var inverseBinds = new[] { Matrix4x4.CreateTranslation(-4f, -5f, -6f) };
        (int BoneIdx, float Weight)[][] influences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]];
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "Face",
            NodeIndex = skeletonNode,
            Submesh = npc,
            Skin = new BethesdaViewerSkinBinding
            {
                JointNodeIndices = jointIndices,
                InverseBindMatrices = inverseBinds,
                PerVertexInfluences = influences
            }
        });
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "Sky",
            NodeIndex = BethesdaViewerScene.RootNodeIndex,
            Submesh = Triangle(@"textures\sky\stars.dds", SkyObjectType.Stars)
        });
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "Water",
            NodeIndex = BethesdaViewerScene.RootNodeIndex,
            Submesh = Triangle(RenderableSubmesh.WaterSurfaceTexturePath)
        });
        var rotationKeys = new[] { new BethesdaViewerQuaternionKey(0f, Quaternion.Identity) };
        var morphNames = new[] { "BlinkLeft" };
        var morphWeights = new[] { 0.25f };
        var animation = new BethesdaViewerAnimationClip(
            "Idle",
            0f,
            1f,
            true,
            [
                new BethesdaViewerNodeAnimationTrack(
                    skeletonNode,
                    1f,
                    0f,
                    BethesdaViewerKeyInterpolation.Linear,
                    rotationKeys,
                    BethesdaViewerKeyInterpolation.Linear,
                    [],
                    BethesdaViewerKeyInterpolation.Linear,
                    [])
            ],
            [
                new BethesdaViewerMorphWeightTrack(
                    0,
                    morphNames,
                    1f,
                    0f,
                    BethesdaViewerKeyInterpolation.Linear,
                    [new BethesdaViewerMorphWeightKey(0f, morphWeights)])
            ],
            [new BethesdaViewerTextKey(0.5f, "middle")]);
        scene.AnimationClips.Add(animation);
        var stitchVertices = new[]
        {
            new BethesdaViewerMeshVertexIndex(0, 0),
            new BethesdaViewerMeshVertexIndex(1, 0)
        };
        scene.BoundaryStitchGroups.Add(new BethesdaViewerBoundaryStitchGroup
        {
            Vertices = stitchVertices
        });
        scene.AddGeneratedTexture(
            @"textures\facegen_egt\npc.dds",
            new DecodedTexture
            {
                MipLevels =
                [
                    new DecodedTextureMipLevel
                    {
                        Width = 1,
                        Height = 1,
                        Pixels = [10, 20, 30, 255]
                    }
                ]
            });
        Assert.True(scene.TryGetGeneratedTexture(@"textures\facegen_egt\npc.dds", out var sceneTexture));

        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);

        Assert.Equal(BethesdaViewerScene.CurrentContractVersion, decoded.ContractVersion);
        Assert.Equal(scene.SourceLabel, decoded.SourceLabel);
        Assert.Equal(BethesdaViewerScenePurpose.NpcAppearance, decoded.Purpose);
        Assert.Equal(BethesdaGame.Fallout76, decoded.Game);
        Assert.Equal(scene.Bounds, decoded.Bounds);
        Assert.Equal(scene.TextureSourcePaths.ToArray(), decoded.TextureSourcePaths.ToArray());
        Assert.NotSame(scene.TextureSourcePaths, decoded.TextureSourcePaths);
        Assert.Equal(scene.Nodes.Count, decoded.Nodes.Count);
        Assert.Equal(scene.Nodes[skeletonNode].LocalTransform, decoded.Nodes[skeletonNode].LocalTransform);
        Assert.Equal(scene.Nodes[skeletonNode].WorldTransform, decoded.Nodes[skeletonNode].WorldTransform);
        Assert.Equal("Spine", decoded.Nodes[skeletonNode].LookupName);
        Assert.Equal(scene.MeshParts.Count, decoded.MeshParts.Count);
        Assert.Equal(scene.MeshParts.Count, decoded.Mesh.Submeshes.Count);
        Assert.Same(decoded.MeshParts[0].Submesh, decoded.Mesh.Submeshes[0]);

        var decodedNpc = decoded.MeshParts[0];
        Assert.Equal(skeletonNode, decodedNpc.NodeIndex);
        Assert.Equal(npc.ShapeName, decodedNpc.NativeSemantics.ShapeName);
        Assert.Same(shaderMetadata, decodedNpc.NativeSemantics.ShaderMetadata);
        Assert.True(decodedNpc.NativeSemantics.UseVertexColors);
        Assert.False(decodedNpc.NativeSemantics.UseVertexAlphaForOpacity);
        Assert.True(decodedNpc.NativeSemantics.IsEyeEnvmap);
        Assert.Equal(npc.EnvMapScale, decodedNpc.NativeSemantics.EyeEnvironmentMapScale);
        Assert.Equal(npc.RenderOrder, decodedNpc.NativeSemantics.RenderOrder);
        Assert.Equal(npc.TintColor, decodedNpc.NativeSemantics.TintColor);
        Assert.True(decodedNpc.NativeSemantics.IsFaceGen);
        Assert.Equal(npc.SubsurfaceColor, decodedNpc.NativeSemantics.SubsurfaceColor);
        Assert.Equal(npc.SourceNifPath, decodedNpc.NativeSemantics.SourceNifPath);
        Assert.True(decodedNpc.NativeSemantics.IsFarLodFallback);
        Assert.NotNull(decodedNpc.NativeSemantics.BindPosePositions);
        Assert.Equal(bindPose, decodedNpc.NativeSemantics.BindPosePositions);
        Assert.NotSame(bindPose, decodedNpc.NativeSemantics.BindPosePositions);

        Assert.NotSame(jointIndices, decodedNpc.Skin!.JointNodeIndices);
        Assert.NotSame(inverseBinds, decodedNpc.Skin.InverseBindMatrices);
        Assert.NotSame(influences, decodedNpc.Skin.PerVertexInfluences);
        Assert.Equal(jointIndices, decodedNpc.Skin.JointNodeIndices);
        Assert.Equal(inverseBinds, decodedNpc.Skin.InverseBindMatrices);
        Assert.Equal(influences[0], decodedNpc.Skin.PerVertexInfluences[0]);

        Assert.Equal(SkyObjectType.Stars, decoded.MeshParts[1].NativeSemantics.SkyType);
        Assert.Null(decoded.MeshParts[1].NativeSemantics.AuthoredSkyVertexColors);
        Assert.Equal(
            RenderableSubmesh.WaterSurfaceTexturePath,
            decoded.MeshParts[2].Submesh.DiffuseTexturePath);
        Assert.Single(decoded.AnimationClips);
        Assert.NotSame(animation, decoded.AnimationClips[0]);
        Assert.Equal(skeletonNode, decoded.AnimationClips[0].NodeTracks[0].NodeIndex);
        Assert.NotSame(rotationKeys, decoded.AnimationClips[0].NodeTracks[0].RotationKeys);
        Assert.Equal(rotationKeys, decoded.AnimationClips[0].NodeTracks[0].RotationKeys);
        Assert.Equal(0, decoded.AnimationClips[0].MorphWeightTracks[0].MeshPartIndex);
        Assert.NotSame(morphNames, decoded.AnimationClips[0].MorphWeightTracks[0].TargetNames);
        Assert.NotSame(
            morphWeights,
            decoded.AnimationClips[0].MorphWeightTracks[0].Keys[0].Weights);
        Assert.Equal(
            morphWeights,
            decoded.AnimationClips[0].MorphWeightTracks[0].Keys[0].Weights);
        Assert.Single(decoded.BoundaryStitchGroups);
        Assert.NotSame(stitchVertices, decoded.BoundaryStitchGroups[0].Vertices);
        Assert.Equal(stitchVertices, decoded.BoundaryStitchGroups[0].Vertices);
        Assert.Single(decoded.GeneratedTextures);
        Assert.Same(sceneTexture, decoded.GeneratedTextures[@"textures\facegen_egt\npc.dds"]);
    }

    [Fact]
    public void DecodeSnapshotsRawSkyBlendWeightsButNeverPromotesNpcSkyTags()
    {
        var colors = new byte[]
        {
            255, 0, 0, 64,
            0, 255, 0, 128,
            0, 0, 255, 255,
        };
        var scene = new BethesdaViewerScene(
            "raw-atmosphere",
            BethesdaViewerScenePurpose.RawNif);
        var sky = Triangle(string.Empty, SkyObjectType.Sky, colors);
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "Atmosphere",
            NodeIndex = BethesdaViewerScene.RootNodeIndex,
            Submesh = sky
        });

        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);

        var snapshot = decoded.MeshParts[0].NativeSemantics.AuthoredSkyVertexColors;
        Assert.NotNull(snapshot);
        Assert.Equal(colors, snapshot);
        Assert.NotSame(colors, snapshot);
        colors[0] = 0;
        Assert.Equal(255, snapshot[0]);
    }

    [Fact]
    public void DecodeDropsMalformedOptionalAnimationWithoutDroppingGeometry()
    {
        var scene = new BethesdaViewerScene("malformed-animation", BethesdaViewerScenePurpose.RawNif);
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "Triangle",
            NodeIndex = BethesdaViewerScene.RootNodeIndex,
            Submesh = Triangle(string.Empty)
        });
        scene.AnimationClips.Add(new BethesdaViewerAnimationClip(
            "Broken",
            -float.MaxValue,
            float.MaxValue,
            true,
            [],
            [],
            []));

        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);

        Assert.Single(decoded.MeshParts);
        Assert.Empty(decoded.AnimationClips);
    }

    // vertexColors flows through the initializer because UseVertexColors / VertexColors are
    // init-only on RenderableSubmesh — a submesh's colour state is fixed at construction.
    private static RenderableSubmesh Triangle(
        string diffuse, SkyObjectType? skyType = null, byte[]? vertexColors = null)
    {
        return new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            DiffuseTexturePath = diffuse,
            SkyType = skyType,
            UseVertexColors = vertexColors is not null,
            VertexColors = vertexColors
        };
    }
}
