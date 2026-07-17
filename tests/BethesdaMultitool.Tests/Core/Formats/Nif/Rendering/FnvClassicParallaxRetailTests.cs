using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class FnvClassicParallaxRetailTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";
    private const string SilverRushPath = @"meshes\architecture\strip\nv_silverrush01.nif";
    private const string SulfurCavePath = @"meshes\dungeons\caves\rooms\nvsulfurcaveroomdoor01.nif";
    private const string PomPath = @"meshes\architecture\retaining wall\rwstairsstone01_nv.nif";

    [Fact]
    [Trait("Category", BucketBTestGuard.Category)]
    public void RetailMeshes_KnownFixturesSelectSimpleParallaxAndExcludePom()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        using var archive = ArchiveReader.Open(FindRetailMeshesPath());

        var (silverData, silverNif) = ReadNif(archive, SilverRushPath);
        var silverMetadata = Assert.IsType<NifShaderTextureMetadata>(
            NifTextureResolver.ReadShaderMetadata(silverData, silverNif, [39]));
        Assert.Equal("BSShaderPPLightingProperty", silverMetadata.PropertyType);
        Assert.Equal(0x82000800u, silverMetadata.ShaderFlags);
        Assert.Equal(@"textures\landscape\RubblePile05_p.dds", silverMetadata.HeightMapPath);
        Assert.Equal(4f, silverMetadata.ParallaxMaxPasses);
        Assert.Equal(1f, silverMetadata.ParallaxScale);
        Assert.NotNull(NifClassicParallaxPolicy.Resolve(silverMetadata));

        var silverModel = ExtractModel(silverData, silverNif);
        var silverShape = Assert.Single(silverModel.Submeshes.Where(static submesh =>
            submesh.ShapeName == "Silverrush09:0"));
        Assert.Equal(silverMetadata.HeightMapPath, silverShape.ClassicParallaxHeightMapTexturePath);

        var (sulfurData, sulfurNif) = ReadNif(archive, SulfurCavePath);
        var sulfurMetadata = Assert.IsType<NifShaderTextureMetadata>(
            NifTextureResolver.ReadShaderMetadata(sulfurData, sulfurNif, [14]));
        Assert.Equal(0x82000901u, sulfurMetadata.ShaderFlags);
        Assert.False(string.IsNullOrWhiteSpace(sulfurMetadata.HeightMapPath));
        Assert.NotNull(NifClassicParallaxPolicy.Resolve(sulfurMetadata));

        var sulfurModel = ExtractModel(sulfurData, sulfurNif);
        var sulfurShape = Assert.Single(sulfurModel.Submeshes.Where(static submesh =>
            submesh.ShapeName == "NVSulfurCaveRoomDoor01:3"));
        Assert.Equal(sulfurMetadata.HeightMapPath, sulfurShape.ClassicParallaxHeightMapTexturePath);

        var (pomData, pomNif) = ReadNif(archive, PomPath);
        var pomMetadata = Assert.IsType<NifShaderTextureMetadata>(
            NifTextureResolver.ReadShaderMetadata(pomData, pomNif, [27]));
        Assert.Equal(0x9C000800u, pomMetadata.ShaderFlags);
        Assert.Equal(@"textures\decals\ImpactDecalConcrete01_P.dds", pomMetadata.HeightMapPath);
        Assert.Equal(4f, pomMetadata.ParallaxMaxPasses);
        Assert.Equal(1f, pomMetadata.ParallaxScale);
        Assert.Null(NifClassicParallaxPolicy.Resolve(pomMetadata));

        var pomModel = ExtractModel(pomData, pomNif);
        Assert.DoesNotContain(pomModel.Submeshes, static submesh =>
            submesh.ClassicParallaxHeightMapTexturePath is not null);
    }

    [Fact]
    [Trait("Category", BucketBTestGuard.Category)]
    public void RetailMeshes_ClassicParallaxCensusMatchesPcFinal()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        using var archive = ArchiveReader.Open(FindRetailMeshesPath());
        var nifEntries = archive.ListFiles()
            .Where(static entry => entry.FullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var parseErrors = 0;
        var ppLightingProperties = 0;
        var parallaxFlagProperties = 0;
        var parallaxSlot3Properties = 0;
        var simpleParallaxProperties = 0;
        var pomProperties = 0;
        var classicRouteOverlapProperties = 0;
        var maxPassesFourProperties = 0;
        var scaleOneProperties = 0;
        var scaleTwentyProperties = 0;
        var meshesWithParallax = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in nifEntries)
        {
            NifInfo? nif;
            byte[] data;
            try
            {
                data = archive.Extract(entry);
                nif = NifParser.Parse(data) as NifInfo;
            }
            catch
            {
                parseErrors++;
                continue;
            }

            if (nif is null)
            {
                parseErrors++;
                continue;
            }

            for (var blockIndex = 0; blockIndex < nif.Blocks.Count; blockIndex++)
            {
                if (nif.Blocks[blockIndex].TypeName != "BSShaderPPLightingProperty")
                {
                    continue;
                }

                ppLightingProperties++;
                var metadata = NifTextureResolver.ReadShaderMetadata(data, nif, [blockIndex]);
                if (metadata?.ShaderFlags is not { } flags ||
                    (flags & NifClassicParallaxPolicy.ParallaxFlag) == 0)
                {
                    continue;
                }

                parallaxFlagProperties++;
                if (string.IsNullOrWhiteSpace(metadata.HeightMapPath))
                {
                    continue;
                }

                parallaxSlot3Properties++;
                meshesWithParallax.Add(entry.FullPath);
                var isPom = (flags & NifClassicParallaxPolicy.ParallaxOcclusionFlag) != 0;
                if (isPom)
                {
                    pomProperties++;
                    Assert.Null(NifClassicParallaxPolicy.Resolve(metadata));
                }
                else
                {
                    simpleParallaxProperties++;
                    Assert.NotNull(NifClassicParallaxPolicy.Resolve(metadata));
                }

                if ((flags & (1u << 7)) != 0 ||
                    !string.IsNullOrWhiteSpace(metadata.EnvironmentMapPath) ||
                    !string.IsNullOrWhiteSpace(metadata.EnvironmentMaskPath))
                {
                    classicRouteOverlapProperties++;
                }

                if (metadata.ParallaxMaxPasses == 4f) maxPassesFourProperties++;
                if (metadata.ParallaxScale == 1f) scaleOneProperties++;
                if (metadata.ParallaxScale == 20f) scaleTwentyProperties++;
            }
        }

        Assert.Equal(14_881, nifEntries.Length);
        Assert.Equal(0, parseErrors);
        Assert.Equal(42_317, ppLightingProperties);
        Assert.Equal(481, parallaxFlagProperties);
        Assert.Equal(481, parallaxSlot3Properties);
        Assert.Equal(255, meshesWithParallax.Count);
        Assert.Equal(464, simpleParallaxProperties);
        Assert.Equal(17, pomProperties);
        // TexIndices.z can therefore be a strict state-discriminated union: none of the retail
        // height candidates also requests the classic environment route/custom-mask occupant.
        // FO4's specular-map occupant is sourced from external BGSM materials, not these PP blocks.
        Assert.Equal(0, classicRouteOverlapProperties);
        Assert.Equal(481, maxPassesFourProperties);
        Assert.Equal(467, scaleOneProperties);
        Assert.Equal(14, scaleTwentyProperties);
    }

    private static (byte[] Data, NifInfo Nif) ReadNif(ArchiveReader archive, string path)
    {
        var data = archive.ReadFile(path);
        Assert.NotNull(data);
        return (data, Assert.IsType<NifInfo>(NifParser.Parse(data)));
    }

    private static NifRenderableModel ExtractModel(byte[] data, NifInfo nif) =>
        Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data, nif,
            skipSkinning: true,
            collectBillboards: true,
            dropBoneAttachedShapes: true,
            treatRootsAsIdentity: true));

    private static string FindRetailMeshesPath()
    {
        var bsaPath = SampleFileFixture.FindSamplePath(MeshesBsaRelative);
        Assert.SkipWhen(bsaPath is null, "FNV PC-final meshes BSA not available");
        return bsaPath!;
    }
}
