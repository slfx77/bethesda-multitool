using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerScenePoseMaterializer12Tests
{
    [Fact]
    public void AggregateBoundsUseRigidAndCurrentSkinnedWorldTransforms()
    {
        var scene = new BethesdaViewerScene(
            "posed-bounds",
            BethesdaViewerScenePurpose.NpcAppearance,
            new BethesdaViewerBounds(new Vector3(-100f), new Vector3(100f)));
        var rigidNode = scene.AddNode(
            "Rigid",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(10f, 20f, 30f),
            BethesdaViewerNodeRole.Attachment);
        var jointNode = scene.AddNode(
            "Joint",
            BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(-5f, -6f, -7f),
            BethesdaViewerNodeRole.Skeleton);

        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "RigidTriangle",
            NodeIndex = rigidNode,
            Submesh = Triangle(
                0f, 0f, 0f,
                1f, 0f, 0f,
                0f, 1f, 0f)
        });
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "SkinnedTriangle",
            NodeIndex = jointNode,
            Submesh = Triangle(
                0f, 0f, 0f,
                2f, 0f, 0f,
                0f, 3f, 0f),
            Skin = new BethesdaViewerSkinBinding
            {
                JointNodeIndices = [jointNode],
                InverseBindMatrices = [Matrix4x4.Identity],
                PerVertexInfluences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]]
            }
        });

        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(
            BethesdaViewerSceneDecoder12.Decode(scene));

        Assert.NotNull(posed.Bounds);
        AssertClose(new Vector3(-5f, -6f, -7f), posed.Bounds.Value.Minimum);
        AssertClose(new Vector3(11f, 21f, 30f), posed.Bounds.Value.Maximum);
        AssertClose(new Vector3(10f, 20f, 30f), posed.Mesh.Submeshes[0].Vertices[0].Position);
        AssertClose(new Vector3(-5f, -6f, -7f), posed.Mesh.Submeshes[1].Vertices[0].Position);
    }

    [Fact]
    public void RawNifAggregateBoundsExcludeExactCameraCenteredSkyLayers()
    {
        var scene = new BethesdaViewerScene(
            "raw-sky-bounds",
            BethesdaViewerScenePurpose.RawNif,
            bounds: null);
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "Object",
            NodeIndex = BethesdaViewerScene.RootNodeIndex,
            Submesh = Triangle(
                -1f, -2f, -3f,
                4f, -2f, -3f,
                -1f, 5f, 6f)
        });
        var sky = Triangle(
            -50_000f, -50_000f, -50_000f,
            50_000f, -50_000f, -50_000f,
            0f, 50_000f, 50_000f);
        sky.SkyType = SkyObjectType.Sky;
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "Atmosphere",
            NodeIndex = BethesdaViewerScene.RootNodeIndex,
            Submesh = sky
        });

        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(
            BethesdaViewerSceneDecoder12.Decode(scene));

        Assert.NotNull(posed.Bounds);
        AssertClose(new Vector3(-1f, -2f, -3f), posed.Bounds.Value.Minimum);
        AssertClose(new Vector3(4f, 5f, 6f), posed.Bounds.Value.Maximum);
    }

    [Fact]
    public void NpcAggregateBoundsKeepSkyTaggedPartsInAssembledSceneSpace()
    {
        var scene = new BethesdaViewerScene(
            "npc-sky-tag",
            BethesdaViewerScenePurpose.NpcAppearance,
            bounds: null);
        var tagged = Triangle(
            10f, 20f, 30f,
            11f, 20f, 30f,
            10f, 21f, 30f);
        tagged.SkyType = SkyObjectType.Stars;
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "TaggedAttachment",
            NodeIndex = BethesdaViewerScene.RootNodeIndex,
            Submesh = tagged
        });

        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(
            BethesdaViewerSceneDecoder12.Decode(scene));

        Assert.NotNull(posed.Bounds);
        AssertClose(new Vector3(10f, 20f, 30f), posed.Bounds.Value.Minimum);
        AssertClose(new Vector3(11f, 21f, 30f), posed.Bounds.Value.Maximum);
        Assert.Contains(posed.Warnings, warning =>
            warning.Contains("camera centering is disabled", StringComparison.Ordinal));
    }

    private static RenderableSubmesh Triangle(params float[] positions) => new()
    {
        Positions = positions,
        Triangles = [0, 1, 2],
        DiffuseTexturePath = @"textures\test\white.dds"
    };

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-4f);
}
