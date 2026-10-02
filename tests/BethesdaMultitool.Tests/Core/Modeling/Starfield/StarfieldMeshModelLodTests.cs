using BethesdaMultitool.Core.Modeling.Starfield;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Starfield.StarfieldMeshModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     The LOD alternatives (cut-2 plan decision D1, section 3.3, slice 5): one node per LOD list in one exclusive layer-set
///     group with LOD 0 on, each LOD primitive sharing the main primitive's buffers, an empty list as a node without a mesh.
/// </summary>
public class StarfieldMeshModelLodTests
{
    [Fact]
    public void LodLists_FormOneExclusiveGroup_WithLodZeroOn()
    {
        var builder = StarfieldMeshTestBuilder.Quad();
        builder.Lods = [[0, 1, 2], [0, 2, 3]];

        var document = Read(builder.Build()).Document;

        SceneValidation.ValidateStructure(document);
        Assert.Equal(3, document.Nodes.Count);
        Assert.Equal(3, document.Meshes.Count);
        Assert.Equal(new[] { 0, 1, 2 }, Assert.Single(document.Scenes).RootNodeIndices);
        Assert.Equal(new[] { "lod0", "lod1", "lod2" }, document.LayerSets.Select(static l => l.Id));
        Assert.All(document.LayerSets, static l =>
        {
            Assert.Equal(StarfieldMeshModelLayers.ExclusiveGroup, l.ExclusiveGroup);
            Assert.Equal(StarfieldMeshModelLayers.SourceKind, l.SourceKind);
        });
        Assert.Equal(new[] { true, false, false }, document.LayerSets.Select(static l => l.DefaultOn));
        Assert.Equal(new[] { 0, 1, 2 }, document.LayerSets.Select(static l => Assert.Single(l.Members)));
        Assert.Equal(new int?[] { 0, 1, 2 }, document.Nodes.Select(static n => n.MeshIndex));
        Assert.Equal($"{DefaultName}.lod2", document.Nodes[2].Name);

        // Control: the same document with a second default-on alternative in the group fails structural validation.
        var twoOn = document.LayerSets.Select(static l => l.Id == "lod1"
            ? new SceneLayerSet(l.Id, l.Label, l.Members, true, l.SourceKind, l.ExclusiveGroup)
            : l).ToList();
        var broken = new ModelDocument(document.SourceFormat, document.Name, document.Scenes, document.Nodes,
            document.Meshes, document.Materials, sourceIdentity: document.SourceIdentity,
            diagnostics: document.Diagnostics, units: document.Units, sourceBasis: document.SourceBasis,
            nativeStates: document.NativeStates, layerSets: twoOn);
        Assert.Throws<InvalidDataException>(() => SceneValidation.ValidateStructure(broken));
    }

    [Fact]
    public void LodPrimitives_ShareEveryBufferOfTheMainPrimitive_AndCarryTheirOwnTriangles()
    {
        var builder = StarfieldMeshFileLayoutTests.FullBuilder();
        builder.Lods = [[0, 1, 2], [2, 3, 0]];

        var document = Read(builder.Build()).Document;
        var main = Primary(document);

        Assert.Equal(builder.Indices.Select(static i => (int)i), main.Indices);
        for (var lod = 1; lod <= 2; lod++)
        {
            var primitive = Assert.Single(document.Meshes[lod].Primitives);
            Assert.Same(main.Vertices, primitive.Vertices);
            Assert.Same(main.Tangents, primitive.Tangents);
            Assert.Same(main.Attributes, primitive.Attributes);
            Assert.Same(main.AdditionalTextureCoordinates, primitive.AdditionalTextureCoordinates);
            Assert.Equal(main.MaterialIndex, primitive.MaterialIndex);
            Assert.Equal(builder.Lods[lod - 1].Select(static i => (int)i), primitive.Indices);
        }

        // Control: the LOD triangles are their own, not the main list's.
        Assert.NotEqual(main.Indices, document.Meshes[1].Primitives[0].Indices);
    }

    [Fact]
    public void AnEmptyLodList_GetsANodeWithoutAMesh()
    {
        var document = Read(StarfieldMeshFileLayoutTests.FullBuilder().Build()).Document;

        SceneValidation.ValidateStructure(document);
        Assert.Equal(3, document.Nodes.Count);
        Assert.Equal(2, document.Meshes.Count);
        Assert.Null(document.Nodes[2].MeshIndex);
        Assert.Equal(new[] { 2 }, document.LayerSets.Single(static l => l.Id == "lod2").Members);
        Assert.Contains(document.Diagnostics, d => d.Code == StarfieldMeshModelDiagnostics.EmptyLod);
    }

    [Fact]
    public void WithoutLodLists_ThereIsOneNodeAndNoLayerSet()
    {
        var document = Read(StarfieldMeshTestBuilder.Quad().Build()).Document;

        Assert.Single(document.Nodes);
        Assert.Empty(document.LayerSets);
        Assert.DoesNotContain(document.Diagnostics, d => d.Code == StarfieldMeshModelDiagnostics.EmptyLod);
    }
}
