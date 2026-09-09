using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class OblivionHairLayerDecodeTests
{
    [Fact]
    public void Decode_PreservesIndependentLayerWithoutSpecularAndRechecksDiffuseOverride()
    {
        var source = CreateSource();
        var decoded = ReferenceSubmeshDecoder12.Decode(source,
            new ReferenceSubmeshDecodeOptions12(source.DiffuseTexturePath, source.NormalMapTexturePath));
        Assert.Equal(OblivionHairLayerTestData.Layer, decoded.OblivionHairLayerTexturePath);
        Assert.Null(decoded.SpecularMapTexturePath);
        Assert.False(decoded.SpecularEnabled);
        Assert.False(decoded.UsesOblivionOrdinarySpecularPolicy);
        Assert.Equal(0f, decoded.AlphaTestThreshold);
        Assert.Equal(source.MaterialAlpha, decoded.MaterialAlpha);
        Assert.All(decoded.Vertices, vertex => Assert.Equal(1f, vertex.VertexColor.W));
        var replaced = ReferenceSubmeshDecoder12.Decode(source,
            new ReferenceSubmeshDecodeOptions12("textures/replacement.dds", source.NormalMapTexturePath));
        Assert.Null(replaced.OblivionHairLayerTexturePath);
        Assert.Equal(decoded.AlphaRenderMode, replaced.AlphaRenderMode);
        Assert.Equal(decoded.AlphaTestThreshold, replaced.AlphaTestThreshold);
    }

    [Fact]
    public void NativeSceneCopyAndRepeatedPose_KeepLayerAndExactGreenOneTint()
    {
        var source = CreateSource();
        var scene = new BethesdaViewerScene("hair", BethesdaViewerScenePurpose.NpcAppearance);
        var node = scene.AddNode("hair", BethesdaViewerScene.RootNodeIndex, Matrix4x4.Identity,
            Matrix4x4.CreateTranslation(2f, 3f, 4f), BethesdaViewerNodeRole.Attachment);
        scene.MeshParts.Add(new BethesdaViewerMeshPart { Name = "hair", NodeIndex = node, Submesh = source });
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        source.OblivionHairLayerTexturePath = null;
        source.HasAuthoredOblivionHairLayerInputs = false;
        source.TintColor = (0f, 0f, 0f);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
            var part = Assert.Single(posed.Mesh.Submeshes);
            Assert.Equal(OblivionHairLayerTestData.Layer, part.OblivionHairLayerTexturePath);
            Assert.Null(part.SpecularMapTexturePath);
            Assert.All(part.Vertices, vertex => Assert.Equal(Vector4.One, vertex.VertexColor));
            Assert.Equal(new Vector3(2f, 3f, 4f), part.Vertices[0].Position);
            VectorAssert.Equal(new Vector3(384f / 255f), part.EffectTint);
            Assert.Equal(0f, part.AlphaTestThreshold);
            Assert.Empty(posed.UnsupportedMeshParts);
        }
    }

    private static RenderableSubmesh CreateSource()
    {
        using var textures = new NifTextureResolver();
        textures.InjectTexture(OblivionHairLayerTestData.Layer, TestTextures.Single(247, 235, 222, 255));
        var source = OblivionHairLayerTestData.Create();
        OblivionHairLayerPolicy.Apply(source, textures, source.DiffuseTexturePath);
        return source;
    }
}