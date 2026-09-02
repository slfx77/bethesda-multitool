using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class NifExportSceneBuilderHierarchyTests
{
    [Fact]
    public void AddNodes_EmitsParentBeforeChild_WhenNifBlockOrderIsReversed()
    {
        var nodes = new[]
        {
            Node(blockIndex: 4, parentBlockIndex: 8, "Child"),
            Node(blockIndex: 8, parentBlockIndex: null, "Parent")
        };
        var scene = new GlbScene();

        var (_, byBlock) = NifExportSceneBuilder.AddNodes(scene, nodes);

        var parentIndex = byBlock[8];
        var childIndex = byBlock[4];
        Assert.True(parentIndex < childIndex);
        Assert.Equal(parentIndex, scene.Nodes[childIndex].ParentIndex);
    }

    [Fact]
    public void AddNodes_RootsOnlyFirstCycleNode_AndRetainsRemainingRelationship()
    {
        var nodes = new[]
        {
            Node(blockIndex: 4, parentBlockIndex: 8, "First"),
            Node(blockIndex: 8, parentBlockIndex: 4, "Second")
        };
        var scene = new GlbScene();

        var (_, byBlock) = NifExportSceneBuilder.AddNodes(scene, nodes);

        Assert.Equal(GlbScene.RootNodeIndex, scene.Nodes[byBlock[4]].ParentIndex);
        Assert.Equal(byBlock[4], scene.Nodes[byBlock[8]].ParentIndex);
    }

    [Fact]
    public void TryCreateSkinBinding_DoesNotRetargetMissingExactBoneByName()
    {
        var skin = new NifExportExtractor.ExtractedSkinBinding
        {
            BoneBlockIndices = [9],
            BoneNames = ["Bone"],
            InverseBindMatrices = [Matrix4x4.Identity],
            PerVertexInfluences = [[]]
        };

        var resolved = NifExportSceneBuilder.TryCreateSkinBinding(
            skin,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Bone"] = 3 },
            new Dictionary<int, int> { [4] = 3 },
            out var binding);

        Assert.False(resolved);
        Assert.Null(binding);
    }

    [Fact]
    public void TryCreateSkinBinding_UsesExactBoneIdentityAheadOfDuplicateName()
    {
        var skin = new NifExportExtractor.ExtractedSkinBinding
        {
            BoneBlockIndices = [9],
            BoneNames = ["Bone"],
            InverseBindMatrices = [Matrix4x4.Identity],
            PerVertexInfluences = [[]]
        };

        var resolved = NifExportSceneBuilder.TryCreateSkinBinding(
            skin,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Bone"] = 3 },
            new Dictionary<int, int> { [9] = 7 },
            out var binding);

        Assert.True(resolved);
        Assert.Equal([7], Assert.IsType<GlbSkinBinding>(binding).JointNodeIndices);
    }

    private static NifExportExtractor.ExtractedNode Node(
        int blockIndex,
        int? parentBlockIndex,
        string name)
    {
        return new NifExportExtractor.ExtractedNode(
            blockIndex,
            name,
            name,
            parentBlockIndex,
            Matrix4x4.Identity,
            Matrix4x4.Identity);
    }
}
