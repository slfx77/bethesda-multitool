using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerExternalSkeletonRigAdapterTests
{
    [Fact]
    public void Apply_InstallsCanonicalHierarchyRebindsSkinAndPreservesRawPartState()
    {
        var source = BuildStubbedModel();
        var sourcePart = source.MeshParts[0];
        var sourceSkin = Assert.IsType<GlbSkinBinding>(sourcePart.Skin);
        var sourceNodeCount = source.Nodes.Count;

        var applied = BethesdaViewerExternalSkeletonRigAdapter.TryApply(
            source,
            CanonicalSkeleton(),
            out var rebound,
            out var diagnostic);

        Assert.True(applied, diagnostic);
        Assert.NotSame(source, rebound);
        Assert.Equal(sourceNodeCount, source.Nodes.Count);
        Assert.Same(sourcePart, source.MeshParts[0]);

        Assert.True(rebound.TryGetNodeIndex("Bip01 Pelvis", out var pelvisIndex));
        Assert.True(rebound.TryGetNodeIndex("Bip01 Head", out var headIndex));
        Assert.True(rebound.TryGetNodeIndex("Bip01 R Hand", out var handIndex));
        Assert.Equal(pelvisIndex, rebound.Nodes[headIndex].ParentIndex);
        Assert.Equal(headIndex, rebound.Nodes[handIndex].ParentIndex);
        Assert.Equal(new Vector3(2f, 0f, 0f), rebound.Nodes[headIndex].LocalTransform.Translation);
        Assert.Equal(new Vector3(2f, 3f, 0f), rebound.Nodes[handIndex].WorldTransform.Translation);
        Assert.Null(rebound.Nodes[headIndex].SourceBlockIndex);

        var reboundPart = rebound.MeshParts[0];
        var reboundSkin = Assert.IsType<GlbSkinBinding>(reboundPart.Skin);
        Assert.Same(sourcePart.Submesh, reboundPart.Submesh);
        Assert.Same(sourceSkin.InverseBindMatrices, reboundSkin.InverseBindMatrices);
        Assert.Same(sourceSkin.PerVertexInfluences, reboundSkin.PerVertexInfluences);
        Assert.Equal([headIndex, handIndex], reboundSkin.JointNodeIndices);
        Assert.Equal(@"textures\creatures\bear\body.dds", reboundPart.Submesh.DiffuseTexturePath);
        Assert.Equal((0.2f, 0.4f, 0.6f), reboundPart.Submesh.TintColor);
        Assert.True(rebound.TryGetNodeIndex("Marker", out var markerIndex));
        Assert.Equal(markerIndex, reboundPart.NodeIndex);
        Assert.Equal(headIndex, rebound.Nodes[markerIndex].ParentIndex);
        Assert.Equal(new Vector3(2f, 3f, 5f), rebound.Nodes[markerIndex].WorldTransform.Translation);

        var viewerScene = BethesdaViewerSceneGlbAdapter.FromGlbScene(
            rebound,
            "bearbody.nif",
            BethesdaViewerScenePurpose.RawNif);
        var binding = BethesdaViewerKfAnimationBinder.Bind(
            viewerScene,
            [new NifNameTargetedAnimationClip(
                "Idle",
                1f,
                0f,
                2f,
                NifCycleType.Loop,
                null,
                [Track("Bip01 Head"), Track("Bip01 R Hand")],
                [],
                0)],
            "idle.kf");

        var clip = Assert.Single(binding.AcceptedClips);
        Assert.Equal([headIndex, handIndex], clip.NodeTracks.Select(static track => track.NodeIndex));
        Assert.Contains("canonical skeleton node", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_MissingJointFailsClosedWithoutMutatingOrPartiallyReplacingScene()
    {
        var source = BuildStubbedModel();
        var originalNodes = source.Nodes.ToArray();
        var originalParts = source.MeshParts.ToArray();

        var applied = BethesdaViewerExternalSkeletonRigAdapter.TryApply(
            source,
            CanonicalSkeleton().Where(static node => node.LookupName != "Bip01 R Hand").ToArray(),
            out var result,
            out var diagnostic);

        Assert.False(applied);
        Assert.Same(source, result);
        Assert.Contains("missing joint 'Bip01 R Hand'", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalNodes, source.Nodes);
        Assert.Equal(originalParts, source.MeshParts);
        Assert.Same(originalParts[0].Skin, source.MeshParts[0].Skin);
    }

    [Fact]
    public void Apply_AmbiguousJointFailsClosedWithoutFallingBackToOneMatchingNode()
    {
        var source = BuildStubbedModel();
        var skeleton = CanonicalSkeleton().ToList();
        skeleton.Add(Node(
            50,
            "Duplicate Head",
            "Bip01 Head",
            20,
            Matrix4x4.CreateTranslation(-2f, 0f, 0f)));

        var applied = BethesdaViewerExternalSkeletonRigAdapter.TryApply(
            source,
            skeleton,
            out var result,
            out var diagnostic);

        Assert.False(applied);
        Assert.Same(source, result);
        Assert.Contains("ambiguous (2 matches)", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, source.Nodes.Count);
        Assert.Single(source.MeshParts);
    }

    [Fact]
    public void Apply_InvalidExistingSkinFailsBeforePublishingCandidateRig()
    {
        var source = BuildStubbedModel();
        var part = source.MeshParts[0];
        source.MeshParts[0] = new GlbMeshPart
        {
            Name = part.Name,
            NodeIndex = part.NodeIndex,
            Submesh = part.Submesh,
            Skin = new GlbSkinBinding
            {
                JointNodeIndices = part.Skin!.JointNodeIndices,
                InverseBindMatrices = [Matrix4x4.Identity],
                PerVertexInfluences = part.Skin.PerVertexInfluences
            }
        };

        var applied = BethesdaViewerExternalSkeletonRigAdapter.TryApply(
            source,
            CanonicalSkeleton(),
            out var result,
            out var diagnostic);

        Assert.False(applied);
        Assert.Same(source, result);
        Assert.Contains("inconsistent binding dimensions", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, source.Nodes.Count);
        Assert.Single(source.MeshParts);
    }

    private static GlbScene BuildStubbedModel()
    {
        var scene = new GlbScene();
        var bodyRoot = scene.AddNode(
            "Scene Root_0",
            GlbScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            GlbNodeKind.Skeleton,
            "Scene Root",
            0);
        var head = scene.AddNode(
            "Bip01 Head_11",
            bodyRoot,
            Matrix4x4.CreateTranslation(100f, 0f, 0f),
            Matrix4x4.CreateTranslation(100f, 0f, 0f),
            GlbNodeKind.Skeleton,
            "Bip01 Head",
            11);
        var hand = scene.AddNode(
            "Bip01 R Hand_12",
            bodyRoot,
            Matrix4x4.CreateTranslation(0f, 200f, 0f),
            Matrix4x4.CreateTranslation(0f, 200f, 0f),
            GlbNodeKind.Skeleton,
            "Bip01 R Hand",
            12);
        var marker = scene.AddNode(
            "Marker_13",
            head,
            Matrix4x4.CreateTranslation(0f, 0f, 5f),
            Matrix4x4.CreateTranslation(100f, 0f, 5f),
            GlbNodeKind.Attachment,
            "Marker",
            13);
        var submesh = new RenderableSubmesh
        {
            ShapeName = "Bear_Body",
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            DiffuseTexturePath = @"textures\creatures\bear\body.dds",
            TintColor = (0.2f, 0.4f, 0.6f)
        };
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "Bear_Body",
            NodeIndex = marker,
            Submesh = submesh,
            Skin = new GlbSkinBinding
            {
                JointNodeIndices = [head, hand],
                InverseBindMatrices = [Matrix4x4.Identity, Matrix4x4.Identity],
                PerVertexInfluences =
                [
                    [(0, 1f)],
                    [(0, 0.5f), (1, 0.5f)],
                    [(1, 1f)]
                ]
            }
        });
        return scene;
    }

    private static NifExportExtractor.ExtractedNode[] CanonicalSkeleton()
    {
        var root = Matrix4x4.Identity;
        var pelvisLocal = Matrix4x4.CreateTranslation(0f, 3f, 0f);
        var pelvisWorld = pelvisLocal * root;
        var headLocal = Matrix4x4.CreateTranslation(2f, 0f, 0f);
        var headWorld = headLocal * pelvisWorld;
        var handLocal = Matrix4x4.CreateTranslation(0f, 0f, 0f);
        var handWorld = handLocal * headWorld;
        return
        [
            new NifExportExtractor.ExtractedNode(10, "Scene Root", "Scene Root", null, root, root),
            Node(20, "Pelvis", "Bip01 Pelvis", 10, pelvisLocal, pelvisWorld),
            Node(30, "Head", "Bip01 Head", 20, headLocal, headWorld),
            Node(40, "Hand", "Bip01 R Hand", 30, handLocal, handWorld)
        ];
    }

    private static NifExportExtractor.ExtractedNode Node(
        int blockIndex,
        string name,
        string lookupName,
        int parentBlockIndex,
        Matrix4x4 localTransform,
        Matrix4x4? worldTransform = null)
    {
        return new NifExportExtractor.ExtractedNode(
            blockIndex,
            name,
            lookupName,
            parentBlockIndex,
            localTransform,
            worldTransform ?? localTransform);
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
            [new NifVec3Key(0f, Vector3.Zero)],
            NifKeyInterpolation.Linear,
            []);
    }
}
