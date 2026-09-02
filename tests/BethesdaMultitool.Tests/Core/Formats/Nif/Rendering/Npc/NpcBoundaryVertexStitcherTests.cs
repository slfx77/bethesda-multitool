using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assembly;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Npc;

public sealed class NpcBoundaryVertexStitcherTests
{
    [Fact]
    public void AddSkinnedPart_PublishesClonedBindPoseAndSourcePathForViewerSeams()
    {
        var sourceSubmesh = new RenderableSubmesh
        {
            Positions = [1f, 2f, 3f],
            Triangles = []
        };
        var part = new NifExportExtractor.ExtractedMeshPart
        {
            Name = "Body",
            Submesh = sourceSubmesh,
            ShapeLocalTransform = Matrix4x4.Identity,
            ShapeWorldTransform = Matrix4x4.CreateTranslation(10f, 20f, 30f),
            Skin = new NifExportExtractor.ExtractedSkinBinding
            {
                BoneBlockIndices = [-1],
                BoneNames = ["Root"],
                InverseBindMatrices = [Matrix4x4.Identity],
                PerVertexInfluences = [[(0, 1f)]]
            }
        };
        var scene = new GlbScene();

        NpcExportSceneBuilder.AddSkinnedPart(
            scene,
            part,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Root"] = 0 },
            @"meshes\actors\character\characterassets\body.nif");

        var emitted = Assert.Single(scene.MeshParts).Submesh;
        Assert.NotSame(sourceSubmesh, emitted);
        Assert.Equal(@"meshes\actors\character\characterassets\body.nif", emitted.SourceNifPath);
        Assert.Equal([11f, 22f, 33f], emitted.BindPosePositions);
        Assert.Equal([1f, 2f, 3f], sourceSubmesh.Positions);
        Assert.Null(sourceSubmesh.BindPosePositions);
        Assert.Null(sourceSubmesh.SourceNifPath);
    }

    [Fact]
    public void DiscoverBoundaryVertexGroups_OnlyReturnsCrossNifThresholdMatchesWithStableIndices()
    {
        var firstBindPose = new[] { 0f, 0f, 0f, 1f, 0f, 0f };
        var sameSourceBindPose = new[] { 0.005f, 0f, 0f };
        var secondSourceBindPose = new[] { 1.005f, 0f, 0f, 1.02f, 0f, 0f };
        var submeshes = new List<RenderableSubmesh>
        {
            CreateSubmesh(@"meshes\actors\body.nif", [10f, 0f, 0f, 20f, 0f, 0f], firstBindPose),
            CreateSubmesh(@"MESHES\ACTORS\BODY.NIF", [30f, 0f, 0f], sameSourceBindPose),
            CreateSubmesh(@"meshes\armor\sleeve.nif", [40f, 0f, 0f, 50f, 0f, 0f], secondSourceBindPose)
        };
        var positionSnapshots = submeshes
            .Select(static submesh => (float[])submesh.Positions.Clone())
            .ToArray();

        var groups = NpcBoundaryVertexStitcher.DiscoverBoundaryVertexGroups(submeshes);

        var group = Assert.Single(groups);
        Assert.Equal(
            [
                new BethesdaViewerMeshVertexIndex(0, 1),
                new BethesdaViewerMeshVertexIndex(2, 0)
            ],
            group.Vertices);
        Assert.Equal(positionSnapshots[0], submeshes[0].Positions);
        Assert.Equal(positionSnapshots[1], submeshes[1].Positions);
        Assert.Equal(positionSnapshots[2], submeshes[2].Positions);
        Assert.Same(firstBindPose, submeshes[0].BindPosePositions);
        Assert.Same(sameSourceBindPose, submeshes[1].BindPosePositions);
        Assert.Same(secondSourceBindPose, submeshes[2].BindPosePositions);
    }

    [Fact]
    public void PopulateViewerSceneBoundaryGroups_UsesFinalMeshPartIndicesWithoutMutation()
    {
        var scene = new BethesdaViewerScene("assembled-npc", BethesdaViewerScenePurpose.NpcAppearance);
        var unrelated = CreateSubmesh(@"meshes\actors\head.nif", [1f, 2f, 3f], [9f, 9f, 9f]);
        var body = CreateSubmesh(@"meshes\actors\body.nif", [4f, 5f, 6f], [2f, 3f, 4f]);
        var sleeve = CreateSubmesh(@"meshes\armor\sleeve.nif", [7f, 8f, 9f], [2.005f, 3f, 4f]);
        AddMeshPart(scene, "Head", unrelated);
        AddMeshPart(scene, "Body", body);
        AddMeshPart(scene, "Sleeve", sleeve);

        NpcBoundaryVertexStitcher.PopulateViewerSceneBoundaryGroups(scene);

        var group = Assert.Single(scene.BoundaryStitchGroups);
        Assert.Equal(
            [
                new BethesdaViewerMeshVertexIndex(1, 0),
                new BethesdaViewerMeshVertexIndex(2, 0)
            ],
            group.Vertices);
        Assert.Equal([4f, 5f, 6f], body.Positions);
        Assert.Equal([7f, 8f, 9f], sleeve.Positions);
        Assert.NotNull(body.BindPosePositions);
        Assert.NotNull(sleeve.BindPosePositions);
    }

    [Fact]
    public void ApplyBoundaryVertexGroups_CanRepeatAfterSkinningWithoutConsumingBindPoseData()
    {
        var bodyBindPose = new[] { 2f, 3f, 4f };
        var sleeveBindPose = new[] { 2.005f, 3f, 4f };
        var submeshes = new List<RenderableSubmesh>
        {
            CreateSubmesh(@"meshes\actors\body.nif", [2f, 4f, 6f], bodyBindPose),
            CreateSubmesh(@"meshes\armor\sleeve.nif", [8f, 10f, 12f], sleeveBindPose)
        };
        var groups = NpcBoundaryVertexStitcher.DiscoverBoundaryVertexGroups(submeshes);
        submeshes[0].Positions[0] = 4f;
        submeshes[0].Positions[1] = 6f;
        submeshes[0].Positions[2] = 8f;
        submeshes[1].Positions[0] = 10f;
        submeshes[1].Positions[1] = 12f;
        submeshes[1].Positions[2] = 14f;

        var stitchedCount = NpcBoundaryVertexStitcher.ApplyBoundaryVertexGroups(submeshes, groups);

        Assert.Equal(2, stitchedCount);
        Assert.Equal([7f, 9f, 11f], submeshes[0].Positions);
        Assert.Equal([7f, 9f, 11f], submeshes[1].Positions);
        Assert.Same(bodyBindPose, submeshes[0].BindPosePositions);
        Assert.Same(sleeveBindPose, submeshes[1].BindPosePositions);
    }

    [Fact]
    public void StitchBoundaryVertices_PreservesLegacyAverageAndClearsAllBindPoseData()
    {
        var submeshes = new List<RenderableSubmesh>
        {
            CreateSubmesh(@"meshes\actors\body.nif", [2f, 4f, 6f], [2f, 3f, 4f]),
            CreateSubmesh(@"meshes\armor\sleeve.nif", [8f, 10f, 12f], [2.005f, 3f, 4f]),
            CreateSubmesh(null, [30f, 31f, 32f], [20f, 21f, 22f])
        };

        NpcBoundaryVertexStitcher.StitchBoundaryVertices(submeshes);

        Assert.Equal([5f, 7f, 9f], submeshes[0].Positions);
        Assert.Equal([5f, 7f, 9f], submeshes[1].Positions);
        Assert.Equal([30f, 31f, 32f], submeshes[2].Positions);
        Assert.All(submeshes, static submesh => Assert.Null(submesh.BindPosePositions));
    }

    private static RenderableSubmesh CreateSubmesh(
        string? sourceNifPath,
        float[] positions,
        float[] bindPosePositions)
    {
        return new RenderableSubmesh
        {
            Positions = positions,
            Triangles = [],
            BindPosePositions = bindPosePositions,
            SourceNifPath = sourceNifPath
        };
    }

    private static void AddMeshPart(
        BethesdaViewerScene scene,
        string name,
        RenderableSubmesh submesh)
    {
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = name,
            Submesh = submesh
        });
    }
}
