using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerRigidSiblingAssemblerTests
{
    [Theory]
    [InlineData(@"meshes\creatures\bear\bearbody.nif", @"meshes\creatures\bear\bearhead.nif")]
    [InlineData(@"meshes/creatures/bear/blackbearbody.NIF", @"meshes\creatures\bear\blackbearhead.nif")]
    [InlineData(@"meshes\creatures\minotaur\minotaur.nif", @"meshes\creatures\minotaur\head.nif")]
    [InlineData(@"meshes/creatures/Minotaur/MINOTAUR.NIF", @"meshes\creatures\Minotaur\head.nif")]
    public void ResolveHeadSibling_UsesOnlyExactClassicConventions(
        string modelPath,
        string expectedSiblingPath)
    {
        var resolved = BethesdaViewerRigidSiblingAssembler.TryResolveHeadSibling(
            modelPath,
            out var siblingPath,
            out var targetNodeName);

        Assert.True(resolved);
        Assert.Equal(expectedSiblingPath, siblingPath, ignoreCase: true);
        Assert.Equal("Bip01 Head", targetNodeName);
    }

    [Theory]
    [InlineData(@"meshes\creatures\bear\bearhead.nif")]
    [InlineData(@"meshes\creatures\bear\body.kf")]
    [InlineData(@"meshes\creatures\minotaur\minotaurvariant.nif")]
    [InlineData(@"minotaur.nif")]
    [InlineData(@"meshes\creatures\bear\..\wolf\wolfbody.nif")]
    [InlineData(@"C:\Games\Data\meshes\creatures\bear\bearbody.nif")]
    [InlineData(@"\\server\Data\meshes\creatures\bear\bearbody.nif")]
    public void ResolveHeadSibling_RejectsFuzzyOrUnsafePaths(string modelPath)
    {
        Assert.False(BethesdaViewerRigidSiblingAssembler.TryResolveHeadSibling(
            modelPath,
            out var siblingPath,
            out var targetNodeName));
        Assert.Empty(siblingPath);
        Assert.Empty(targetNodeName);
    }

    [Fact]
    public void Attach_RigidDescendantsFollowCanonicalHeadAndPreserveExternalSkin()
    {
        var host = BuildRiggedHost();
        var sibling = BuildRigidHeadSibling("Bip01 Head");
        var originalHostPart = Assert.Single(host.MeshParts);
        var originalSiblingPart = Assert.Single(sibling.MeshParts);
        var originalHostNodeCount = host.Nodes.Count;
        var originalSiblingNodeCount = sibling.Nodes.Count;
        Assert.True(host.TryGetNodeIndex("Bip01 Head", out var hostHeadNodeIndex));

        var attached = BethesdaViewerRigidSiblingAssembler.TryAttach(
            host,
            sibling,
            @"meshes\creatures\bear\bearhead.nif",
            "Bip01 Head",
            out var result,
            out var diagnostic);

        Assert.True(attached, diagnostic);
        Assert.NotSame(host, result);
        Assert.Equal(originalHostNodeCount, host.Nodes.Count);
        Assert.Single(host.MeshParts);
        Assert.Equal(originalSiblingNodeCount, sibling.Nodes.Count);
        Assert.Single(sibling.MeshParts);

        var retainedBody = result.MeshParts[0];
        Assert.Equal(originalHostPart.NodeIndex, retainedBody.NodeIndex);
        Assert.Same(originalHostPart.Submesh, retainedBody.Submesh);
        Assert.Same(originalHostPart.Skin, retainedBody.Skin);
        Assert.Equal([hostHeadNodeIndex], retainedBody.Skin!.JointNodeIndices);

        var attachedHead = Assert.Single(result.MeshParts.Skip(1));
        Assert.Equal("bearhead.nif::BearHead", attachedHead.Name);
        Assert.Same(originalSiblingPart.Submesh, attachedHead.Submesh);
        Assert.Null(attachedHead.Skin);
        Assert.True(attachedHead.NodeIndex.HasValue);
        var attachedHeadNode = result.Nodes[attachedHead.NodeIndex.Value];
        Assert.Equal(hostHeadNodeIndex, attachedHeadNode.ParentIndex);
        Assert.Null(attachedHeadNode.LookupName);
        Assert.Null(attachedHeadNode.SourceBlockIndex);

        var siblingPartNode = sibling.Nodes[originalSiblingPart.NodeIndex!.Value];
        var expectedWorld = siblingPartNode.LocalTransform * host.Nodes[hostHeadNodeIndex].WorldTransform;
        Assert.Equal(expectedWorld, attachedHeadNode.WorldTransform);
        Assert.NotEqual(siblingPartNode.WorldTransform, attachedHeadNode.WorldTransform);
    }

    [Fact]
    public void Attach_AnchorlessAuthoredRootPreservesItsBranchBelowCanonicalHead()
    {
        var host = BuildRiggedHost();
        var sibling = BuildAnchorlessRigidHeadSibling();
        var hostNodes = host.Nodes.ToArray();
        var hostParts = host.MeshParts.ToArray();
        var siblingNodes = sibling.Nodes.ToArray();
        var siblingParts = sibling.MeshParts.ToArray();
        Assert.True(host.TryGetNodeIndex("Bip01 Head", out var hostHeadNodeIndex));

        var attached = BethesdaViewerRigidSiblingAssembler.TryAttach(
            host,
            sibling,
            "bearhead.nif",
            "Bip01 Head",
            out var result,
            out var diagnostic);

        Assert.True(attached, diagnostic);
        Assert.NotSame(host, result);
        Assert.Contains("authored root 'BearHead'", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hostNodes, host.Nodes);
        Assert.Equal(hostParts, host.MeshParts);
        Assert.Equal(siblingNodes, sibling.Nodes);
        Assert.Equal(siblingParts, sibling.MeshParts);

        var attachedPart = Assert.Single(result.MeshParts.Skip(1));
        var attachedShape = result.Nodes[attachedPart.NodeIndex!.Value];
        var attachedRootIndex = Assert.IsType<int>(attachedShape.ParentIndex);
        var attachedRoot = result.Nodes[attachedRootIndex];
        Assert.Equal(hostHeadNodeIndex, attachedRoot.ParentIndex);
        Assert.Equal(sibling.Nodes[1].LocalTransform, attachedRoot.LocalTransform);
        var expectedRootWorld =
            sibling.Nodes[1].LocalTransform * host.Nodes[hostHeadNodeIndex].WorldTransform;
        Assert.Equal(expectedRootWorld, attachedRoot.WorldTransform);
        Assert.Equal(sibling.Nodes[2].LocalTransform, attachedShape.LocalTransform);
        Assert.Equal(sibling.Nodes[2].LocalTransform * expectedRootWorld, attachedShape.WorldTransform);
        Assert.Null(attachedRoot.LookupName);
        Assert.Null(attachedShape.LookupName);
    }

    [Fact]
    public void Attach_AmbiguousHostTargetFailsWithoutPublishingFallback()
    {
        var host = BuildRiggedHost();
        host.AddNode(
            "Duplicate Head",
            GlbScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            GlbNodeKind.Skeleton,
            "Bip01 Head",
            sourceBlockIndex: null);
        var sibling = BuildRigidHeadSibling("Bip01 Head");
        var originalNodeCount = host.Nodes.Count;
        var originalPart = Assert.Single(host.MeshParts);

        var attached = BethesdaViewerRigidSiblingAssembler.TryAttach(
            host,
            sibling,
            "bearhead.nif",
            "Bip01 Head",
            out var result,
            out var diagnostic);

        Assert.False(attached);
        Assert.Same(host, result);
        Assert.Contains("unique skeleton joint", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalNodeCount, host.Nodes.Count);
        Assert.Same(originalPart, Assert.Single(host.MeshParts));
    }

    [Fact]
    public void Attach_SkinnedSiblingFailsWithoutChangingExternalSkinBinding()
    {
        var host = BuildRiggedHost();
        var sibling = BuildRigidHeadSibling("Bip01 Head");
        var siblingPart = Assert.Single(sibling.MeshParts);
        sibling.MeshParts[0] = new GlbMeshPart
        {
            Name = siblingPart.Name,
            NodeIndex = siblingPart.NodeIndex,
            Submesh = siblingPart.Submesh,
            Skin = new GlbSkinBinding
            {
                JointNodeIndices = [siblingPart.NodeIndex!.Value],
                InverseBindMatrices = [Matrix4x4.Identity],
                PerVertexInfluences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]]
            }
        };
        var originalHostSkin = Assert.Single(host.MeshParts).Skin;

        var attached = BethesdaViewerRigidSiblingAssembler.TryAttach(
            host,
            sibling,
            "bearhead.nif",
            "Bip01 Head",
            out var result,
            out var diagnostic);

        Assert.False(attached);
        Assert.Same(host, result);
        Assert.Contains("not a rigid attachment", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Same(originalHostSkin, Assert.Single(host.MeshParts).Skin);
    }

    [Fact]
    public void Attach_LateNonDescendantPartDiscardsTheWholeCandidate()
    {
        var host = BuildRiggedHost();
        var sibling = BuildRigidHeadSibling("Bip01 Head");
        var unrelatedNode = sibling.AddNode(
            "Unrelated Root Part",
            GlbScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            GlbNodeKind.Attachment,
            "Unrelated",
            sourceBlockIndex: 99);
        sibling.MeshParts.Add(new GlbMeshPart
        {
            Name = "Unrelated",
            NodeIndex = unrelatedNode,
            Submesh = Submesh("Unrelated")
        });
        var originalHostNodes = host.Nodes.ToArray();
        var originalHostParts = host.MeshParts.ToArray();
        var originalSiblingNodes = sibling.Nodes.ToArray();
        var originalSiblingParts = sibling.MeshParts.ToArray();

        var attached = BethesdaViewerRigidSiblingAssembler.TryAttach(
            host,
            sibling,
            "bearhead.nif",
            "Bip01 Head",
            out var result,
            out var diagnostic);

        Assert.False(attached);
        Assert.Same(host, result);
        Assert.Contains("not a descendant", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalHostNodes, host.Nodes);
        Assert.Equal(originalHostParts, host.MeshParts);
        Assert.Equal(originalSiblingNodes, sibling.Nodes);
        Assert.Equal(originalSiblingParts, sibling.MeshParts);
    }

    [Fact]
    public void Attach_AnchorlessMultipleRenderableRootsFailWithoutPublishingFallback()
    {
        var host = BuildRiggedHost();
        var sibling = BuildAnchorlessRigidHeadSibling();
        var secondRoot = sibling.AddNode(
            "Second Head Root",
            GlbScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            GlbNodeKind.Skeleton,
            "SecondHeadRoot",
            sourceBlockIndex: 3);
        var secondShape = sibling.AddNode(
            "Second Head Shape",
            secondRoot,
            Matrix4x4.Identity,
            Matrix4x4.Identity,
            GlbNodeKind.Attachment,
            "SecondHeadShape",
            sourceBlockIndex: 4);
        sibling.MeshParts.Add(new GlbMeshPart
        {
            Name = "SecondHeadShape",
            NodeIndex = secondShape,
            Submesh = Submesh("SecondHeadShape")
        });
        var originalHostNodes = host.Nodes.ToArray();
        var originalHostParts = host.MeshParts.ToArray();

        var attached = BethesdaViewerRigidSiblingAssembler.TryAttach(
            host,
            sibling,
            "bearhead.nif",
            "Bip01 Head",
            out var result,
            out var diagnostic);

        Assert.False(attached);
        Assert.Same(host, result);
        Assert.Contains("multiple renderable root branches", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalHostNodes, host.Nodes);
        Assert.Equal(originalHostParts, host.MeshParts);
    }

    private static GlbScene BuildRiggedHost()
    {
        var scene = new GlbScene();
        var neckLocal = Matrix4x4.CreateTranslation(10f, 0f, 0f);
        var neck = scene.AddNode(
            "Bip01 Neck",
            GlbScene.RootNodeIndex,
            neckLocal,
            neckLocal,
            GlbNodeKind.Skeleton,
            "Bip01 Neck",
            sourceBlockIndex: null);
        var headLocal = Matrix4x4.CreateTranslation(0f, 5f, 0f);
        var headWorld = headLocal * neckLocal;
        var head = scene.AddNode(
            "Bip01 Head",
            neck,
            headLocal,
            headWorld,
            GlbNodeKind.Skeleton,
            "Bip01 Head",
            sourceBlockIndex: null);
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "BearBody",
            NodeIndex = null,
            Submesh = Submesh("BearBody"),
            Skin = new GlbSkinBinding
            {
                JointNodeIndices = [head],
                InverseBindMatrices = [Matrix4x4.Identity],
                PerVertexInfluences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]]
            }
        });
        return scene;
    }

    private static GlbScene BuildRigidHeadSibling(string anchorLookupName)
    {
        var scene = new GlbScene();
        // The sibling repeats the authored rest-world head transform. It is an identity anchor,
        // not another transform to multiply on top of the canonical external skeleton joint.
        var siblingAnchorLocal = Matrix4x4.CreateTranslation(100f, 200f, 300f);
        var anchor = scene.AddNode(
            "Sibling Head Anchor",
            GlbScene.RootNodeIndex,
            siblingAnchorLocal,
            siblingAnchorLocal,
            GlbNodeKind.Skeleton,
            anchorLookupName,
            sourceBlockIndex: 1);
        var shapeLocal = Matrix4x4.CreateTranslation(0f, 0f, 2f);
        var shape = scene.AddNode(
            "BearHeadShape",
            anchor,
            shapeLocal,
            shapeLocal * siblingAnchorLocal,
            GlbNodeKind.Attachment,
            "BearHead",
            sourceBlockIndex: 2);
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "BearHead",
            NodeIndex = shape,
            Submesh = Submesh("BearHead")
        });
        return scene;
    }

    private static GlbScene BuildAnchorlessRigidHeadSibling()
    {
        var scene = new GlbScene();
        var rootLocal = Matrix4x4.CreateRotationZ(0.25f) * Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var root = scene.AddNode(
            "BearHead",
            GlbScene.RootNodeIndex,
            rootLocal,
            rootLocal,
            GlbNodeKind.Skeleton,
            "BearHead",
            sourceBlockIndex: 1);
        var shapeLocal = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0f, 0f, 4f);
        var shape = scene.AddNode(
            "BearHead:0",
            root,
            shapeLocal,
            shapeLocal * rootLocal,
            GlbNodeKind.Attachment,
            "BearHead:0",
            sourceBlockIndex: 2);
        scene.MeshParts.Add(new GlbMeshPart
        {
            Name = "BearHead:0",
            NodeIndex = shape,
            Submesh = Submesh("BearHead:0")
        });
        return scene;
    }

    private static RenderableSubmesh Submesh(string name)
    {
        return new RenderableSubmesh
        {
            ShapeName = name,
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            DiffuseTexturePath = @"textures\creatures\bear\bear.dds"
        };
    }
}
