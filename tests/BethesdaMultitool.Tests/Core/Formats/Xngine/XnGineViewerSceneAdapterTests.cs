using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Xngine;

/// <summary>
///     Pins the classic-mesh → viewer-scene adaptation, above all its BASIS CHANGE.
///     <para>
///         ⚠ This is the assertion that matters. Classic geometry is Y-down; the GLB file exporter
///         reaches glTF's Y-up by NEGATING Y, but the viewer uses the Z-up NIF basis
///         (its camera passes <c>Vector3.UnitZ</c> as up). Applying the exporter's flip here, or
///         this rotation before an export, lays every mesh on its side — and a mesh on its side
///         still renders, so nothing but an explicit test catches it.
///     </para>
/// </summary>
public sealed class XnGineViewerSceneAdapterTests
{
    private static XnGineTriangleMesh Mesh(params XnGineVertex[] vertices)
    {
        var indices = Enumerable.Range(0, Math.Max(3, vertices.Length)).Select(i => i % vertices.Length).ToList();
        return new XnGineTriangleMesh(42, 1f, Vector3.One, [new XnGineSubMesh(7, 3, vertices, indices)]);
    }

    private static XnGineVertex At(float x, float y, float z)
    {
        return new XnGineVertex(new Vector3(x, y, z), Vector3.UnitY, Vector2.Zero);
    }

    [Fact]
    public void ToViewerSpace_MapsClassicYDownOntoViewerZUp()
    {
        // 10 units UP in classic space is -Y (the axis points down); it must land as +Z in the
        // viewer, which is what the camera treats as up.
        Assert.Equal(new Vector3(0, 0, 10), XnGineViewerSceneAdapter.ToViewerSpace(new Vector3(0, -10, 0)));

        // Classic +Z (forward) becomes viewer +Y (forward); X is untouched.
        Assert.Equal(new Vector3(0, 5, 0), XnGineViewerSceneAdapter.ToViewerSpace(new Vector3(0, 0, 5)));
        Assert.Equal(new Vector3(3, 0, 0), XnGineViewerSceneAdapter.ToViewerSpace(new Vector3(3, 0, 0)));
    }

    [Fact]
    public void ToViewerSpace_IsAProperRotationSoWindingIsPreserved()
    {
        // A rotation preserves handedness; a mirror does not. The exporter uses a mirror and must
        // therefore reverse its triangles — this path must NOT, and that difference is only safe
        // while the determinant stays +1.
        var x = XnGineViewerSceneAdapter.ToViewerSpace(Vector3.UnitX);
        var y = XnGineViewerSceneAdapter.ToViewerSpace(Vector3.UnitY);
        var z = XnGineViewerSceneAdapter.ToViewerSpace(Vector3.UnitZ);

        Assert.Equal(1f, Vector3.Dot(Vector3.Cross(x, y), z), 5);
    }

    [Fact]
    public void ToViewerScene_EmitsOneRootAndOneMeshPartPerSubMesh()
    {
        var scene = XnGineViewerSceneAdapter.ToViewerScene(
            Mesh(At(1, 0, 0), At(0, 1, 0), At(0, 0, 1)), "ARMOR");

        // GlbScene always ships a "SceneRoot" at index 0; the mesh node hangs under it, so there
        // are two nodes and exactly one parentless root.
        Assert.Equal(2, scene.Nodes.Count);
        Assert.Equal("SceneRoot", scene.Nodes[GlbScene.RootNodeIndex].Name);
        Assert.Single(scene.Nodes, n => n.ParentIndex is null);

        var meshNode = scene.Nodes[1];
        Assert.Equal("ARMOR", meshNode.Name);
        Assert.Equal(GlbScene.RootNodeIndex, meshNode.ParentIndex);

        var part = Assert.Single(scene.MeshParts);
        Assert.Equal(1, part.NodeIndex);
        Assert.Equal(9, part.Submesh.Positions.Length);
        Assert.Equal(3, part.Submesh.Triangles.Length);
    }

    [Fact]
    public void ToViewerScene_CarriesVerticesThroughTheBasisChange()
    {
        // One vertex 4 units above the origin in classic terms (-Y).
        var scene = XnGineViewerSceneAdapter.ToViewerScene(Mesh(At(0, -4, 0), At(1, 0, 0), At(0, 0, 1)), "M");

        var positions = Assert.Single(scene.MeshParts).Submesh.Positions;
        Assert.Equal(0f, positions[0]);
        Assert.Equal(0f, positions[1]);
        Assert.Equal(4f, positions[2]);
    }

    [Fact]
    public void ToViewerScene_DoesNotReverseTriangleOrder()
    {
        var scene = XnGineViewerSceneAdapter.ToViewerScene(Mesh(At(1, 0, 0), At(0, 1, 0), At(0, 0, 1)), "M");

        Assert.Equal<ushort[]>([0, 1, 2], Assert.Single(scene.MeshParts).Submesh.Triangles);
    }

    [Fact]
    public void ToViewerScene_ResolvesTexturePathsThroughTheCallback()
    {
        var scene = XnGineViewerSceneAdapter.ToViewerScene(
            Mesh(At(1, 0, 0), At(0, 1, 0), At(0, 0, 1)), "M",
            (archive, record) => $"TEXTURE.{archive:D3}:{record}");

        Assert.Equal("TEXTURE.007:3", Assert.Single(scene.MeshParts).Submesh.DiffuseTexturePath);
    }

    [Fact]
    public void ToViewerScene_WithNoResolver_LeavesTheMeshUntextured()
    {
        var scene = XnGineViewerSceneAdapter.ToViewerScene(Mesh(At(1, 0, 0), At(0, 1, 0), At(0, 0, 1)), "M");

        Assert.Null(Assert.Single(scene.MeshParts).Submesh.DiffuseTexturePath);
    }

    [Fact]
    public void ToViewerScene_SkipsEmptySubMeshesRatherThanFailing()
    {
        // Placeholder records are normal in these archives — 1,203 of Redguard's 5,870 ROB segments.
        var mesh = new XnGineTriangleMesh(1, 1f, Vector3.One, [new XnGineSubMesh(0, 0, [], [])]);

        Assert.Empty(XnGineViewerSceneAdapter.ToViewerScene(mesh, "EMPTY").MeshParts);
    }

    [Fact]
    public void ToViewerScene_LevelAssembly_ConjugatesThePlacementIntoViewerSpace()
    {
        // A placement that translates 10 units UP in the game's Y-down space (-Y) must move the
        // instance 10 units up in the viewer's Z-up space (+Z). Applying the raw matrix to
        // already-rotated vertices would instead move it along -Y, i.e. backwards in the viewer.
        var mesh = Mesh(At(0, 0, 0), At(1, 0, 0), At(0, 0, 1));
        var placement = Matrix4x4.CreateTranslation(0, -10, 0);

        var scene = XnGineViewerSceneAdapter.ToViewerScene("LEVEL", [new XnGineMeshInstance(mesh, placement, "obj")]);

        var node = scene.Nodes[1];
        Assert.Equal(0f, node.LocalTransform.Translation.X, 4);
        Assert.Equal(0f, node.LocalTransform.Translation.Y, 4);
        Assert.Equal(10f, node.LocalTransform.Translation.Z, 4);
    }

    [Fact]
    public void ToViewerScene_LevelAssembly_PlacesOneNodePerInstanceUnderTheRoot()
    {
        var mesh = Mesh(At(1, 0, 0), At(0, 1, 0), At(0, 0, 1));
        var scene = XnGineViewerSceneAdapter.ToViewerScene("LEVEL", [
            new XnGineMeshInstance(mesh, Matrix4x4.Identity, "a"),
            new XnGineMeshInstance(mesh, Matrix4x4.CreateTranslation(5, 0, 0), "b")
        ]);

        // SceneRoot plus one node per instance; a shared mesh still gets its own placement.
        Assert.Equal(3, scene.Nodes.Count);
        Assert.Equal(2, scene.MeshParts.Count);
        Assert.All(scene.Nodes.Skip(1), n => Assert.Equal(GlbScene.RootNodeIndex, n.ParentIndex));
    }

    [Fact]
    public void ToViewerScene_LevelAssembly_WithNothingPlaced_Throws()
    {
        Assert.Throws<InvalidDataException>(() => XnGineViewerSceneAdapter.ToViewerScene("EMPTY", []));
    }

    [Fact]
    public void ToViewerScene_SubMeshBeyondSixteenBitIndices_Throws()
    {
        var vertices = Enumerable.Range(0, XnGineViewerSceneAdapter.MaximumVerticesPerSubMesh + 1)
            .Select(_ => At(0, 0, 0)).ToArray();
        var mesh = new XnGineTriangleMesh(1, 1f, Vector3.One, [new XnGineSubMesh(0, 0, vertices, [0, 1, 2])]);

        Assert.Throws<InvalidDataException>(() => XnGineViewerSceneAdapter.ToViewerScene(mesh, "BIG"));
    }
}