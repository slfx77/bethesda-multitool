using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Geometry;

[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", TestCategories.BucketB)]
public sealed class NifOblivionHairSourceRetailTests
{
    [Fact]
    public void InstalledStyle02_KeepsExactColorProofAndActorSelectedGreyLayerAcrossExtractionAndClone()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var archivePath = RealAssetPaths.SteamGameFile("Oblivion", @"Data\Oblivion - Meshes.bsa");
        Assert.SkipWhen(archivePath is null, RealAssetPaths.SkipMessage("Oblivion - Meshes.bsa"));
        using var archive = ArchiveReader.Open(archivePath);
        var data = Assert.IsType<byte[]>(archive.ReadFile(@"meshes\characters\hair\style02.nif"));
        Assert.Equal("CA31B1CE277BC70DB1558CFD83C2A37A206B1EC8EA304CA827870D79389AB01F",
            Convert.ToHexString(SHA256.HashData(data)));
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        Assert.Equal(0x0A020000u, nif.BinaryVersion);
        Assert.Equal(10u, nif.UserVersion);
        Assert.Equal(9u, nif.BsVersion);
        Assert.Equal(2, nif.Blocks.Count(block => block.TypeName == "NiDirectionalLight"));
        Assert.True(NifOblivionHairSourceReader.IsEligible(data, nif, 2));
        var cpu = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(data, nif, bindPoseOnly: true));
        var rendered = Assert.Single(cpu.Submeshes);
        var exported = Assert.Single(NifExportExtractor.Extract(data, nif).MeshParts);
        Assert.Null(exported.Skin);
        AssertSource(rendered);
        AssertSource(exported.Submesh);
        var composed = RenderableSubmeshCloner.CloneGeometryWithRenderState(exported.Submesh, rendered);
        AssertSource(RenderableSubmeshCloner.DeepClone(composed));

        // Exact installed source plus an independent positive layer fixture. Native capture verifies
        // the installed DDS residency; this test does not substitute its pixel for an installed render.
        using var textures = new NifTextureResolver();
        const string diffuse = "textures/characters/hair/grey.dds";
        const string layer = "textures/characters/hair/grey_hl.dds";
        textures.InjectTexture(layer, TestTextures.Single(247, 235, 222, 255));
        NpcHairSubmeshPolicy.Apply(new NifRenderableModel { Submeshes = { composed } }, BethesdaGame.Oblivion,
            (192f / 255f, 192f / 255f, 192f / 255f), textures, diffuse);
        Assert.Equal(layer, OblivionHairLayerPolicy.ResolveTexturePath(composed, diffuse));
        Assert.Equal(0, composed.AlphaTestThreshold);
        Assert.Null(composed.SpecularMapTexturePath);
    }

    private static void AssertSource(RenderableSubmesh part)
    {
        Assert.True(part.HasAuthoredOblivionHairLayerInputs);
        Assert.Equal(2, part.SourceBlockIndex);
        Assert.Equal("Hair", part.LegacyMaterialName);
        Assert.Equal(1356, part.VertexCount);
        Assert.Equal(1922 * 3, part.Triangles.Length);
        Assert.False(part.HasAuthoredOblivionOrdinaryInputs);
        Assert.False(part.HasAuthoredOblivionBodySkinInputs);
        Assert.False(part.UsesClassicHairMaterial);
        Assert.False(part.IsFaceGen);
        Assert.False(part.IsDoubleSided);
        Assert.True(part.HasAlphaBlend);
        Assert.True(part.HasAlphaTest);
        Assert.NotNull(part.VertexColors);
        Assert.Equal(1356 * 4, part.VertexColors.Length);
        Assert.All(Enumerable.Range(0, 1356), vertex =>
        {
            Assert.Equal(255, part.VertexColors[vertex * 4 + 1]);
            Assert.Equal(255, part.VertexColors[vertex * 4 + 3]);
        });
    }
}