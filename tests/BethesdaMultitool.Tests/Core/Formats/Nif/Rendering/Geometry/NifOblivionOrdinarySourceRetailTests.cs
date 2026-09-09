using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class NifOblivionOrdinarySourceRetailTests
{
    [Fact]
    public void InstalledMiddleClassShoes_KeepOrdinaryProvenanceAcrossBothExtractionRoutes()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));
        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(@"meshes\clothes\middleclass\01\m\shoes.nif"));
        Assert.Equal("C8891E94E4A324DD20E91342B0B85E21B83F247A49B28132DFA0547C2AA2EDBF",
            Convert.ToHexString(SHA256.HashData(data)));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        Assert.Equal(@"textures\clothes\middleclass\Shoe01.dds",
            NifOblivionOrdinarySourceReader.ReadDiffusePath(data, nif, 1));

        var cpu = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(data, nif, bindPoseOnly: true));
        var rendered = Assert.Single(cpu.Submeshes);
        var exported = Assert.Single(NifExportExtractor.Extract(data, nif).MeshParts);
        Assert.NotNull(exported.Skin);
        AssertSource(rendered);
        AssertSource(exported.Submesh);
        AssertSource(RenderableSubmeshCloner.DeepClone(exported.Submesh));
    }

    private static void AssertSource(RenderableSubmesh part)
    {
        Assert.Equal(1, part.SourceBlockIndex);
        Assert.Equal("Foot", part.ShapeName);
        Assert.Equal("foot", part.LegacyMaterialName);
        Assert.True(part.HasAuthoredOblivionOrdinaryInputs);
        Assert.Equal(@"textures\clothes\middleclass\Shoe01.dds", part.AuthoredOblivionOrdinaryDiffusePath);
        Assert.False(part.HasAuthoredOblivionBodySkinInputs);
        Assert.False(part.IsFaceGen);
        Assert.False(part.UsesClassicHairMaterial);
        Assert.Equal(550, part.VertexCount);
        Assert.Equal(712 * 3, part.Triangles.Length);
        Assert.Equal(10f, part.MaterialGlossiness);
        Assert.Equal((1f, 1f, 1f), part.SpecularColor);
        Assert.NotNull(part.Normals);
        Assert.NotNull(part.Tangents);
        Assert.NotNull(part.Bitangents);
        Assert.Equal(550 * 3, part.Normals.Length);
        Assert.Equal(550 * 3, part.Tangents.Length);
        Assert.Equal(550 * 3, part.Bitangents.Length);
    }
}