using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using ImageMagick;
using SharpGLTF.Schema2;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Media.Models;
using Xunit;
using Xunit.Sdk;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Adversarial checks that the corpus oracle detects changes while tolerating equivalent encodings.</summary>
public sealed class NifCorpusExportAssertionsTests
{
    /// <summary>Different vertex welding and triangle ordering retain exactly the same oriented surfaces.</summary>
    [Fact]
    public void Equivalent_ReorderedAndWeldedTrianglesPass()
    {
        var original = Model();
        var reordered = Model(welded: true);
        Assert.NotEqual(original.LogicalMeshes[0].Primitives[0].GetVertexAccessor("POSITION").Count,
            reordered.LogicalMeshes[0].Primitives[0].GetVertexAccessor("POSITION").Count);
        NifCorpusExportAssertions.Equivalent(original, reordered);
    }

    /// <summary>Distinct table rows with identical surface content may bind separate triangles equivalently.</summary>
    [Fact]
    public void Equivalent_DuplicateMaterialRowsPass()
    {
        var original = Model();
        var duplicated = Model(duplicateMaterial: true);
        Assert.Single(original.LogicalMaterials);
        Assert.Equal(2, duplicated.LogicalMaterials.Count);
        NifCorpusExportAssertions.Equivalent(original, duplicated);
    }

    /// <summary>Identical material tables cannot hide a material assigned to the wrong oriented triangle.</summary>
    [Fact]
    public void Equivalent_ChangedTriangleMaterialBindingFails()
    {
        var original = Model(duplicateMaterial: true);
        var changed = Model(duplicateMaterial: true);
        foreach (var model in new[] { original, changed })
        {
            var channel = Assert.IsType<MaterialChannel>(model.LogicalMaterials[1].FindChannel("BaseColor"));
            channel.Color = new Vector4(0.25f, 0.5f, 1, 1);
        }
        changed.LogicalMeshes[0].Primitives[0].Material = changed.LogicalMaterials[1];
        changed.LogicalMeshes[0].Primitives[1].Material = changed.LogicalMaterials[0];
        Assert.ThrowsAny<XunitException>(() => NifCorpusExportAssertions.Equivalent(original, changed));
    }

    /// <summary>Vertex changes and winding reversal cannot be hidden by sorting or unchanged triangle counts.</summary>
    /// <param name="reverse">Whether to reverse winding instead of changing a vertex coordinate.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equivalent_ChangedGeometryOrWindingFails(bool reverse)
    {
        var changed = reverse ? Model(reverse: true) : Model(firstX: 0.25f);
        Assert.ThrowsAny<XunitException>(() => NifCorpusExportAssertions.Equivalent(Model(), changed));
    }

    /// <summary>Different texture pixels must fail even when material labels and geometry remain identical.</summary>
    [Fact]
    public void Equivalent_ChangedMaterialPixelsFail()
    {
        var changed = Model(blue: true);
        Assert.ThrowsAny<XunitException>(() => NifCorpusExportAssertions.Equivalent(Model(), changed));
    }

    /// <summary>A changed placement or a second occurrence is observable even when logical mesh bytes match.</summary>
    /// <param name="extraOccurrence">Whether to add another occurrence instead of moving the existing one.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equivalent_ChangedNodeOccurrenceFails(bool extraOccurrence)
    {
        var changed = extraOccurrence ? Model(extraOccurrence: true) : Model(translateX: 3);
        Assert.ThrowsAny<XunitException>(() => NifCorpusExportAssertions.Equivalent(Model(), changed));
    }

    /// <summary>The second joint/weight accessor set contributes to independently evaluated world positions.</summary>
    [Fact]
    public void Equivalent_FifthJointWorldChangeFails()
    {
        var original = Model(skinned: true);
        var changed = Model(skinned: true, fifthJointX: 10);
        Assert.NotNull(original.LogicalMeshes[0].Primitives[0].GetVertexAccessor("JOINTS_1"));
        NifCorpusExportAssertions.Equivalent(original, Model(skinned: true));
        Assert.ThrowsAny<XunitException>(() => NifCorpusExportAssertions.Equivalent(original, changed));
    }

    /// <summary>Only source-proven unauthored tangents may differ when neither output uses a normal map.</summary>
    /// <param name="authored">Whether the source actually owns tangent data that must remain strict.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equivalent_UnmappedFallbackNeedsUnauthoredSource(bool authored)
    {
        var source = TangentSource(authored, normalMapped: false);
        using var resolver = new NifTextureResolver(static _ => null);
        var native = Model(tangents: true);
        var neutral = Model();
        if (authored)
            Assert.ThrowsAny<XunitException>(() => NifCorpusExportAssertions.Equivalent(native, neutral, source, resolver));
        else
            NifCorpusExportAssertions.Equivalent(native, neutral, source, resolver);
    }

    /// <summary>Normal-mapped output requires tangent presence and handedness even when source tangents were generated.</summary>
    /// <param name="changeHandedness">Whether to change tangent direction instead of omitting its accessor.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equivalent_NormalMappedMissingOrChangedTangentsFail(bool changeHandedness)
    {
        var source = TangentSource(authored: false, normalMapped: true);
        using var resolver = new NifTextureResolver(static _ =>
            DecodedTexture.FromBaseLevel([128, 128, 255, 255], 1, 1, false));
        var original = Model(tangents: true, normalMapped: true);
        var changed = Model(tangents: changeHandedness, normalMapped: true, negativeHandedness: changeHandedness);
        Assert.ThrowsAny<XunitException>(() => NifCorpusExportAssertions.Equivalent(original, changed, source, resolver));
    }

    /// <summary>Proves the legacy triangle loss is exact repeated position, not general collinearity or epsilon proximity.</summary>
    /// <param name="fixture">Repeated index, equal positions with different UVs, collinear, or almost coincident.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacyWriter_DropsOnlyExactRepeatedPositions(int fixture)
    {
        var source = new GlbScene();
        float[] positions = [0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0, 2, 1, 0, 2, 0, 1, 2];
        ushort[] indices = [0, 1, 2, 3, 4, 5];
        if (fixture == 0) indices[5] = 4;
        if (fixture == 1) { positions[15] = 1; positions[16] = 0; }
        if (fixture == 2) { positions[15] = 2; positions[16] = 0; }
        if (fixture == 3) positions[12] = 1e-8f;
        source.MeshParts.Add(new GlbMeshPart
        {
            Name = "triangles", NodeIndex = 0, Submesh = new RenderableSubmesh
            {
                Positions = positions, Triangles = indices,
                Normals = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1],
                UVs = [0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1]
            }
        });
        using var resolver = new NifTextureResolver(static _ => null);
        Assert.True(NifNeutralSceneAdapter.TryAdapt(source, resolver, "triangles", out var neutral,
            out var reason, TestContext.Current.CancellationToken), reason);
        Assert.Equal(2, neutral.Meshes.Sum(static mesh => mesh.Primitives.Sum(static part => part.Indices.Count / 3)));
        var expectedDrops = fixture < 2 ? 1 : 0;
        Assert.Equal(expectedDrops, NifCorpusLegacyTriangles.RepeatedPositions(source, resolver));
        Assert.Equal(expectedDrops, NifCorpusLegacyTriangles.RepeatedPositions(neutral));
        var native = ModelRoot.ParseGLB(GlbWriter.WriteToBytes(source, resolver));
        Assert.Equal(2 - expectedDrops, native.LogicalMeshes.Sum(static mesh =>
            mesh.Primitives.Sum(static part => part.GetIndices().Count / 3)));
    }

    /// <summary>Supplies explicit source material/tangent ownership for the narrowly permitted generated fallback.</summary>
    private static GlbScene TangentSource(bool authored, bool normalMapped)
    {
        var source = new GlbScene();
        source.MeshParts.Add(new GlbMeshPart
        {
            Name = "source", NodeIndex = 0, Submesh = new RenderableSubmesh
            {
                Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0], Triangles = [0, 1, 2],
                Tangents = authored ? [1, 0, 0, 1, 0, 0, 1, 0, 0] : null,
                NormalMapTexturePath = normalMapped ? "normal.dds" : null
            }
        });
        return source;
    }

    /// <summary>The source snapshot detects changed values and replacement of equivalent owned objects or buffers.</summary>
    /// <param name="mutation">Geometry contents, equivalent tangent buffer, or equivalent source-node replacement.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SourceSnapshot_DetectsContentAndOwnershipChanges(int mutation)
    {
        var source = new GlbScene();
        var geometry = new RenderableSubmesh
        {
            Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0],
            Triangles = [0, 1, 2],
            Tangents = [1, 0, 0, 1, 0, 0, 1, 0, 0]
        };
        source.MeshParts.Add(new GlbMeshPart { Name = "triangle", NodeIndex = 0, Submesh = geometry });
        var snapshot = new NifCorpusSourceSnapshot(source);
        snapshot.AssertUnchanged(source);
        switch (mutation)
        {
            case 0:
                geometry.Positions[0] = 0.5f;
                break;
            case 1:
                geometry.Tangents = (float[])geometry.Tangents.Clone();
                break;
            case 2:
                source.Nodes[0] = new GlbNode
                {
                    Name = "SceneRoot", LocalTransform = Matrix4x4.Identity,
                    WorldTransform = Matrix4x4.Identity, Kind = GlbNodeKind.Root
                };
                break;
        }
        Assert.ThrowsAny<XunitException>(() => snapshot.AssertUnchanged(source));
    }

    /// <summary>Encodes two known triangles with optional deliberate corruption, placement, welding or five-joint skinning.</summary>
    private static ModelRoot Model(bool welded = false, bool reverse = false, float firstX = 0,
        bool blue = false, float translateX = 0, bool extraOccurrence = false,
        bool skinned = false, float fifthJointX = 0, bool tangents = false,
        bool normalMapped = false, bool negativeHandedness = false, bool duplicateMaterial = false)
    {
        Vector3[] positions = welded
            ? [Vector3.Zero, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitX]
            : [new(firstX, 0, 0), Vector3.UnitX, Vector3.UnitY, Vector3.Zero, -Vector3.UnitX];
        int[] indices = welded ? [0, 2, 3, 0, 1, 2] : [0, 1, 2, 3, 2, 4];
        if (reverse) (indices[1], indices[2]) = (indices[2], indices[1]);
        var nodes = new List<SceneNode>
        {
            new("placement", Matrix4x4.CreateTranslation(translateX, 0, 0), meshIndex: 0,
                skinIndex: skinned ? 0 : null)
        };
        var roots = new List<int> { 0 };
        var skins = new List<SceneSkin>();
        SceneSkinInfluences? influences = null;
        if (extraOccurrence)
        {
            nodes.Add(new SceneNode("same label", Matrix4x4.CreateTranslation(5, 0, 0), meshIndex: 0));
            roots.Add(1);
        }
        if (skinned)
        {
            roots.Add(nodes.Count);
            nodes.Add(new SceneNode("armature", Matrix4x4.Identity, [2, 3, 4, 5, 6]));
            for (var joint = 0; joint < 5; joint++)
            {
                nodes.Add(new SceneNode($"joint{joint}",
                    Matrix4x4.CreateTranslation(joint == 4 ? fifthJointX : 0, 0, 0)));
            }
            skins.Add(new SceneSkin("skin", [2, 3, 4, 5, 6], Enumerable.Repeat(Matrix4x4.Identity, 5), 1));
            influences = new SceneSkinInfluences(5,
                Enumerable.Range(0, positions.Length).SelectMany(static _ => Enumerable.Range(0, 5)),
                Enumerable.Repeat(0.2f, positions.Length * 5));
        }
        var vertices = positions.Select(static position => new SceneVertex(position, Vector3.UnitZ,
            Vector4.One, Vector2.Zero));
        using var pixel = new MagickImage(blue ? MagickColors.Blue : MagickColors.Red, 1, 1);
        var tangentHandedness = negativeHandedness ? -1f : 1f;
        var tangentData = tangents ? new SceneTangents(Enumerable.Repeat(
            new Vector4(1, 0, 0, tangentHandedness), positions.Length)) : null;
        var material = new SceneMaterial("same material", Vector4.One, new SceneTextureBinding(0, 0), unlit: !normalMapped)
            { NormalTexture = normalMapped ? new SceneTextureBinding(0, 0) : null };
        var materials = new List<SceneMaterial> { material };
        var primitives = new List<ScenePrimitive>();
        if (duplicateMaterial)
        {
            materials.Add(material);
            primitives.Add(new ScenePrimitive("first", vertices, indices.Take(3), 0, skinInfluences: influences,
                tangents: tangentData));
            primitives.Add(new ScenePrimitive("second", vertices, indices.Skip(3), 1, skinInfluences: influences,
                tangents: tangentData));
        }
        else
        {
            primitives.Add(new ScenePrimitive("surface", vertices, indices, 0, skinInfluences: influences,
                tangents: tangentData));
        }
        var document = new ModelDocument("synthetic", "corpus oracle",
            [new SceneDefinition("scene", roots)], nodes,
            [new SceneMesh("mesh", primitives)], materials,
            [new SceneImage("same image", pixel.ToByteArray(MagickFormat.Png))],
            [new SceneSampler(SceneTextureWrap.Repeat, SceneTextureWrap.Repeat)], skins: skins);
        var token = TestContext.Current.CancellationToken;
        return ModelRoot.ParseGLB(GltfExporter.Encode(SceneGltfBuilder.Build(document, GltfExportIntent.Interchange, token), token));
    }
}
