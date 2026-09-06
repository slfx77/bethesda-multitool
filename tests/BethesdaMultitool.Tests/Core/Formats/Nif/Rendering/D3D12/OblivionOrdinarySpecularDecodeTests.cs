using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class OblivionOrdinarySpecularDecodeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeCarriesEligibilityWithoutChangingAuthoredCandidate(bool eligible)
    {
        var source = CreateSource(eligible);
        var decoded = ReferenceSubmeshDecoder12.Decode(source,
            new ReferenceSubmeshDecodeOptions12(source.DiffuseTexturePath, source.NormalMapTexturePath));

        Assert.Equal(eligible, decoded.UsesOblivionOrdinarySpecularPolicy);
        Assert.True(decoded.SpecularEnabled);
        Assert.Equal(new Vector3(0.2f, 0.4f, 0.8f), decoded.SpecularColor);
        Assert.Equal(10f, decoded.Glossiness);
        Assert.True(decoded.HasBump);
        Assert.Equal(source.NormalMapTexturePath, decoded.NormalMapTexturePath);
    }

    [Fact]
    public void DecodeRechecksComposedFaceGenAndNormalOverrides()
    {
        var source = CreateSource(true);
        source.IsFaceGen = true;
        var skin = ReferenceSubmeshDecoder12.Decode(source,
            new ReferenceSubmeshDecodeOptions12(source.DiffuseTexturePath, source.NormalMapTexturePath));
        Assert.False(skin.UsesOblivionOrdinarySpecularPolicy);

        source.IsFaceGen = false;
        var replaced = ReferenceSubmeshDecoder12.Decode(source,
            new ReferenceSubmeshDecodeOptions12(source.DiffuseTexturePath, "textures/other_n.dds"));
        Assert.False(replaced.UsesOblivionOrdinarySpecularPolicy);
        Assert.Equal("textures/other_n.dds", replaced.NormalMapTexturePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeSceneCopyAndPoseKeepEligibility(bool skinned)
    {
        var source = CreateSource(true);
        var scene = new BethesdaViewerScene("ordinary", BethesdaViewerScenePurpose.NpcAppearance, null);
        var node = scene.AddNode("part", BethesdaViewerScene.RootNodeIndex,
            Matrix4x4.Identity, Matrix4x4.CreateTranslation(2f, 3f, 4f), BethesdaViewerNodeRole.Attachment);
        scene.MeshParts.Add(new BethesdaViewerMeshPart
        {
            Name = "part",
            NodeIndex = node,
            Submesh = source,
            Skin = skinned ? new BethesdaViewerSkinBinding
            {
                JointNodeIndices = [node],
                InverseBindMatrices = [Matrix4x4.Identity],
                PerVertexInfluences = [[(0, 1f)], [(0, 1f)], [(0, 1f)]]
            } : null
        });

        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        source.HasAuthoredOblivionOrdinaryInputs = false;
        source.NormalMapTexturePath = "textures/replaced_n.dds";
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        var part = Assert.Single(posed.Mesh.Submeshes);
        Assert.True(part.UsesOblivionOrdinarySpecularPolicy);
        Assert.Equal("textures/test_n.dds", part.NormalMapTexturePath);
        Assert.Equal(new Vector3(2f, 3f, 4f), part.Vertices[0].Position);
        Assert.Equal(new Vector3(0.2f, 0.4f, 0.8f), part.SpecularColor);
    }

    private static RenderableSubmesh CreateSource(bool eligible) => new()
    {
        ShapeName = "part",
        LegacyMaterialName = "foot",
        Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
        Triangles = [0, 1, 2],
        Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
        Tangents = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
        Bitangents = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f],
        UVs = [0f, 0f, 1f, 0f, 0f, 1f],
        DiffuseTexturePath = "textures/test.dds",
        NormalMapTexturePath = "textures/test_n.dds",
        HasAuthoredOblivionOrdinaryInputs = eligible,
        AuthoredOblivionOrdinaryDiffusePath = "textures/test.dds",
        MaterialAlpha = 1f,
        MaterialGlossiness = 10f,
        SpecularColor = (0.2f, 0.4f, 0.8f)
    };
}
