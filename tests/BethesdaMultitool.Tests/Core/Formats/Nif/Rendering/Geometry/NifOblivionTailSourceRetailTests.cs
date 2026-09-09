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
public sealed class NifOblivionTailSourceRetailTests
{
    [Fact]
    public void InstalledKhajiitTail_PreservesNonwhiteAmbientAndBodyProvenanceAcrossExtractionAndCloning()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));
        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(@"meshes\characters\khajiit\khajiittail.nif"));
        const string expectedHash = "E49AA6FABB2C52BB8A5185DD83630581D3D5A7F48A331EB77A5B0207C0E0C05F";
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(data)));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var context = Assert.IsType<NifOblivionBodySkinSourceReader>(
            NifOblivionBodySkinSourceReader.Create(data, nif));
        Assert.Equal((0.588f, 0.588f, 0.588f), context.ReadAmbientColor(1));

        var cpu = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(data, nif, bindPoseOnly: true));
        var rendered = Assert.Single(cpu.Submeshes);
        var exported = Assert.Single(NifExportExtractor.Extract(data, nif).MeshParts);
        Assert.NotNull(exported.Skin);
        AssertSource(rendered);
        AssertSource(exported.Submesh);
        AssertSource(RenderableSubmeshCloner.DeepClone(exported.Submesh));
        AssertSource(RenderableSubmeshCloner.CloneGeometryWithRenderState(rendered, exported.Submesh));
        Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(data)));
    }

    private static void AssertSource(RenderableSubmesh part)
    {
        Assert.Equal(1, part.SourceBlockIndex);
        Assert.Equal("Tail", part.ShapeName);
        Assert.Equal("skin", part.LegacyMaterialName);
        Assert.True(part.HasAuthoredOblivionBodySkinInputs);
        Assert.Equal((0.588f, 0.588f, 0.588f), part.AuthoredOblivionBodySkinAmbientColor);
        Assert.Equal(@"textures\characters\khajiit\female\tail.dds", part.AuthoredOblivionBodySkinDiffusePath);
        Assert.False(part.HasAuthoredOblivionOrdinaryInputs);
        Assert.False(part.IsFaceGen);
        Assert.Equal((1f, 1f, 1f), part.MaterialDiffuse);
        Assert.Equal(145, part.VertexCount);
        Assert.Equal(248 * 3, part.Triangles.Length);
        Assert.NotNull(part.Normals);
        Assert.NotNull(part.Tangents);
        Assert.NotNull(part.Bitangents);
        Assert.Equal(145 * 3, part.Normals.Length);
        Assert.Equal(145 * 3, part.Tangents.Length);
        Assert.Equal(145 * 3, part.Bitangents.Length);
    }
}