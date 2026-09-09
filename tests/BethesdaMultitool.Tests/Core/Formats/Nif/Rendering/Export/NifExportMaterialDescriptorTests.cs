using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class NifExportMaterialDescriptorTests
{
    private static readonly float[] ExpectedPositions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f];
    private static readonly ushort[] ExpectedTriangles = [0, 1, 2];
    private static readonly float[] ExpectedNormals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f];
    private static readonly float[] ExpectedTangents = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f];
    private static readonly float[] ExpectedBitangents = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f];
    private static readonly float[] ExpectedUvs = [0f, 0f, 1f, 0f, 0f, 1f];

    [Theory]
    [InlineData(false, 0x14000004u)]
    [InlineData(true, 0x14000004u)]
    [InlineData(false, 0x14000005u)]
    [InlineData(true, 0x14000005u)]
    public void ClassicMaterial_PreservesLiteralColorsAlphaGlossAndGeometry(bool strips, uint version)
    {
        var fixture = new NifExportMaterialDescriptorTestData(strips, binaryVersion: version);

        foreach (var submesh in ExtractBoth(fixture))
        {
            AssertMaterial(submesh, (0.25f, 0.5f, 0.75f), (0.125f, 0.375f, 0.625f), 24f, 0.625f);
            Assert.Equal("AuthoredMaterial", submesh.LegacyMaterialName);
            AssertGeometry(submesh);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ModernMaterial_PreservesSpecularButNotAbsentDiffuse_InBothByteOrders(bool strips, bool bigEndian)
    {
        var fixture = new NifExportMaterialDescriptorTestData(strips, bigEndian, 34);

        foreach (var submesh in ExtractBoth(fixture))
        {
            AssertMaterial(submesh, (0.25f, 0.5f, 0.75f), null, 24f, 0.625f);
            Assert.Equal("AuthoredMaterial", submesh.LegacyMaterialName);
            AssertGeometry(submesh);
        }
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("out-of-range")]
    [InlineData("wrong-type")]
    [InlineData("truncated")]
    public void MissingOrMalformedOwnerMaterial_KeepsDefaultsAndIgnoresDetachedMaterial(string material)
    {
        var fixture = new NifExportMaterialDescriptorTestData(material: material);

        foreach (var submesh in ExtractBoth(fixture))
        {
            AssertMaterial(submesh, (0f, 0f, 0f), null, 10f, 1f);
            Assert.Null(submesh.LegacyMaterialName);
            AssertGeometry(submesh);
        }
    }

    [Fact]
    public void AuthoredBlackDiffuse_RemainsPresentRatherThanMissing()
    {
        var fixture = new NifExportMaterialDescriptorTestData(material: "black");

        foreach (var submesh in ExtractBoth(fixture))
        {
            AssertMaterial(submesh, (0.25f, 0.5f, 0.75f), (0f, 0f, 0f), 24f, 0.625f);
            Assert.NotNull(submesh.MaterialDiffuse);
        }
    }

    [Fact]
    public void SharedGeometry_UsesOnlyEachOwnersMaterial_NotAnotherShapeOrDetachedProperty()
    {
        var fixture = new NifExportMaterialDescriptorTestData(multipleOwners: true);
        var cpu = NifGeometryExtractor.Extract(fixture.Data, fixture.Info, bindPoseOnly: true);
        Assert.NotNull(cpu);
        var exported = NifExportExtractor.Extract(fixture.Data, fixture.Info);

        foreach (var submeshes in new[]
                     { cpu.Submeshes.ToArray(), exported.MeshParts.Select(static part => part.Submesh).ToArray() })
        {
            Assert.Equal(3, submeshes.Length);
            var primary = Assert.Single(submeshes, static part => part.SourceBlockIndex == 1);
            var second = Assert.Single(submeshes, static part => part.SourceBlockIndex == 4);
            var noMaterial = Assert.Single(submeshes, static part => part.SourceBlockIndex == 6);
            AssertMaterial(primary, (0.25f, 0.5f, 0.75f), (0.125f, 0.375f, 0.625f), 24f, 0.625f);
            AssertMaterial(second, (0.875f, 0.625f, 0.375f), (0.75f, 0.25f, 0.5f), 32f, 0.75f);
            AssertMaterial(noMaterial, (0f, 0f, 0f), null, 10f, 1f);
            Assert.Equal("AuthoredMaterial", primary.LegacyMaterialName);
            Assert.Equal("skin", second.LegacyMaterialName);
            Assert.Null(noMaterial.LegacyMaterialName);
            Assert.Equal(primary.Positions, second.Positions);
            Assert.Equal(primary.Positions, noMaterial.Positions);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeCompositionAndPose_PreserveDescriptors_UsingCurrentGenericSpecularFallback(bool strips)
    {
        var fixture = new NifExportMaterialDescriptorTestData(strips);
        var exported = NifExportSceneBuilder.Build(fixture.Data, fixture.Info, "synthetic material");
        Assert.NotNull(exported);
        var exportPart = Assert.Single(exported.MeshParts);
        AssertMaterial(exportPart.Submesh, (0.25f, 0.5f, 0.75f), (0.125f, 0.375f, 0.625f), 24f, 0.625f);
        AssertGeometry(exportPart.Submesh);
        Assert.Equal("AuthoredMaterial", exportPart.Submesh.LegacyMaterialName);

        var scene = BethesdaViewerSceneGlbAdapter.FromGlbScene(exported, "synthetic material",
            BethesdaViewerScenePurpose.RawNif, game: BethesdaGame.Oblivion);
        var nativePart = Assert.Single(scene.MeshParts);
        AssertMaterial(nativePart.Submesh, (0.25f, 0.5f, 0.75f), (0.125f, 0.375f, 0.625f), 24f, 0.625f);
        Assert.Equal("AuthoredMaterial", nativePart.Submesh.LegacyMaterialName);
        var decoded = BethesdaViewerSceneDecoder12.Decode(scene);
        var posed = BethesdaViewerScenePoseMaterializer12.Materialize(decoded);
        Assert.Empty(posed.UnsupportedMeshParts);
        foreach (var part in new[] { Assert.Single(decoded.MeshParts).Submesh, Assert.Single(posed.Mesh.Submeshes) })
        {
            Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), part.SpecularColor);
            Assert.Equal(new Vector3(0.125f, 0.375f, 0.625f), part.MaterialDiffuse);
            Assert.Equal(24f, part.Glossiness);
            Assert.Equal(0.625f, part.MaterialAlpha);
            // Pin the CURRENT metadata-less fallback only, not recovered retail TES4 selection.
            Assert.True(part.SpecularEnabled);
            Assert.False(part.IsEmissive);
            Assert.Equal(3, part.Vertices.Length);
            Assert.Equal(ExpectedTriangles, part.Indices);
            Assert.Equal(Vector3.Zero, part.Vertices[0].Position);
            Assert.Equal(Vector3.UnitX, part.Vertices[1].Position);
            Assert.Equal(Vector3.UnitY, part.Vertices[2].Position);
            Assert.All(part.Vertices, static vertex =>
            {
                Assert.Equal(Vector3.UnitZ, vertex.Normal);
                Assert.Equal(Vector3.UnitX, vertex.Tangent);
                Assert.Equal(Vector3.UnitY, vertex.Bitangent);
            });
        }
    }

    private static RenderableSubmesh[] ExtractBoth(NifExportMaterialDescriptorTestData fixture)
    {
        var cpu = NifGeometryExtractor.Extract(fixture.Data, fixture.Info, bindPoseOnly: true);
        Assert.NotNull(cpu);
        var exported = NifExportExtractor.Extract(fixture.Data, fixture.Info);
        return [Assert.Single(cpu.Submeshes), Assert.Single(exported.MeshParts).Submesh];
    }

    private static void AssertMaterial(RenderableSubmesh submesh,
        (float R, float G, float B) specular,
        (float R, float G, float B)? diffuse,
        float gloss,
        float alpha)
    {
        Assert.Equal(specular, submesh.SpecularColor);
        Assert.Equal(diffuse, submesh.MaterialDiffuse);
        Assert.Equal(gloss, submesh.MaterialGlossiness);
        Assert.Equal(alpha, submesh.MaterialAlpha);
        Assert.False(submesh.IsEmissive);
        Assert.False(submesh.UsesClassicHairMaterial);
        Assert.False(submesh.IsFaceGen);
        Assert.Null(submesh.TintColor);
    }

    private static void AssertGeometry(RenderableSubmesh submesh)
    {
        Assert.Equal(1, submesh.SourceBlockIndex);
        Assert.Equal("Primary", submesh.ShapeName);
        Assert.Equal(ExpectedPositions, submesh.Positions);
        Assert.Equal(ExpectedTriangles, submesh.Triangles);
        Assert.Equal(ExpectedNormals, submesh.Normals);
        Assert.Equal(ExpectedTangents, submesh.Tangents);
        Assert.Equal(ExpectedBitangents, submesh.Bitangents);
        Assert.Equal(ExpectedUvs, submesh.UVs);
        Assert.Null(submesh.VertexColors);
        Assert.False(submesh.HasAlphaBlend);
        Assert.False(submesh.HasAlphaTest);
    }
}