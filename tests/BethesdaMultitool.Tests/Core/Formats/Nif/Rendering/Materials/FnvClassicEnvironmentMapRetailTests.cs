using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

[Collection(SequentialIntegrationGroup.Name)]
public sealed class FnvClassicEnvironmentMapRetailTests
{
    private const string MeshesBsaRelative =
        @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa";

    private const string HeliosReflectorPath =
        @"meshes\architecture\helios_one\heliosone_solarreflector_row.nif";

    private const string GoodspringsGeneralStorePath =
        @"meshes\architecture\goodsprings\nv_generalstore.nif";

    private const string EyeFixturePath = @"meshes\characters\hair\shades.nif";

    [Fact]
    public void Policy_UsesClassicSlot4CubeAndOptionalSlot5RedMaskOnlyWhenBit7IsSet()
    {
        var metadata = new NifShaderTextureMetadata
        {
            PropertyType = "BSShaderPPLightingProperty",
            ShaderFlags = 0x82000181u,
            EnvMapScale = 1.25f,
            TextureSlots =
            [
                @"textures\architecture\helios_one\solar_reflector.dds",
                @"textures\architecture\helios_one\solar_reflector_n.dds",
                null,
                null,
                @"textures\effects\chrome_e.dds",
                @"textures\architecture\helios_one\Solar_Reflector_M.dds",
                null,
                null
            ]
        };

        var resolved = NifClassicEnvironmentMapPolicy.Resolve(metadata);
        Assert.NotNull(resolved);
        var material = resolved.Value;
        Assert.Equal(@"textures\effects\chrome_e.dds", material.CubeMapTexturePath);
        Assert.Equal(@"textures\architecture\helios_one\Solar_Reflector_M.dds", material.MaskTexturePath);
        Assert.Equal(1.25f, material.Scale);
        Assert.False(material.UsesWindowReflection);

        Assert.Null(NifClassicEnvironmentMapPolicy.Resolve(WithFlags(0x82000101u)));
        var window = NifClassicEnvironmentMapPolicy.Resolve(WithFlags(0x82200181u));
        Assert.NotNull(window);
        Assert.True(window.Value.UsesWindowReflection);
        // Retail eye properties carry both bits 7 and 17. SLS2059 ENVMAP_EYE is a separate
        // material family and must not fall into the world-material SLS2057/2058 route.
        Assert.Null(NifClassicEnvironmentMapPolicy.Resolve(WithFlags(0x82020181u)));

        var slotsWithoutMask = metadata.TextureSlots.ToArray();
        slotsWithoutMask[5] = null;
        var withoutMask = NifClassicEnvironmentMapPolicy.Resolve(new NifShaderTextureMetadata
        {
            PropertyType = metadata.PropertyType,
            ShaderFlags = metadata.ShaderFlags,
            EnvMapScale = metadata.EnvMapScale,
            TextureSlots = slotsWithoutMask
        });
        Assert.NotNull(withoutMask);
        Assert.Null(withoutMask.Value.MaskTexturePath);

        NifShaderTextureMetadata WithFlags(uint flags)
        {
            return new NifShaderTextureMetadata
            {
                PropertyType = metadata.PropertyType,
                ShaderFlags = flags,
                EnvMapScale = metadata.EnvMapScale,
                TextureSlots = metadata.TextureSlots
            };
        }
    }

    [Fact]
    public void GoodspringsGeneralStore_Bit21SelectsSls2058WindowReflection()
    {
        using var archives = OpenRetailMeshes();
        Assert.True(archives.TryExtractFile(GoodspringsGeneralStorePath, out var data, out _),
            $"Retail NIF missing: {GoodspringsGeneralStorePath}");
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));

        var metadata = NifTextureResolver.ReadShaderMetadata(data, nif, [22]);
        Assert.NotNull(metadata);
        Assert.Equal(0x82200181u, metadata.ShaderFlags);
        var material = NifClassicEnvironmentMapPolicy.Resolve(metadata);
        Assert.NotNull(material);
        Assert.True(material.Value.UsesWindowReflection);
        Assert.Equal(
            @"textures\effects\ShinyBright_e.dds",
            material.Value.CubeMapTexturePath,
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            @"textures\architecture\goodsprings\NV_StoreGlass_m.dds",
            material.Value.MaskTexturePath,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Shades_Bit17EyePropertiesAreExcludedEvenThoughTheyAlsoCarryBit7()
    {
        using var archives = OpenRetailMeshes();
        Assert.True(archives.TryExtractFile(EyeFixturePath, out var data, out _),
            $"Retail NIF missing: {EyeFixturePath}");
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var eyeProperties = Enumerable.Range(0, nif.Blocks.Count)
            .Where(index => nif.Blocks[index].TypeName == "BSShaderPPLightingProperty")
            .Select(index => NifTextureResolver.ReadShaderMetadata(data, nif, [index]))
            .Where(static metadata =>
                metadata?.ShaderFlags is { } flags && (flags & (1u << 17)) != 0)
            .ToArray();

        Assert.NotEmpty(eyeProperties);
        Assert.All(eyeProperties, static metadata =>
        {
            Assert.True((metadata!.ShaderFlags!.Value & (1u << 7)) != 0);
            Assert.Null(NifClassicEnvironmentMapPolicy.Resolve(metadata));
        });
    }

    [Fact]
    public void HeliosReflector_ExtractsClassicEnvironmentPayloadWithoutFo4MaterialSemantics()
    {
        using var archives = OpenRetailMeshes();
        Assert.True(archives.TryExtractFile(HeliosReflectorPath, out var data, out _),
            $"Retail NIF missing: {HeliosReflectorPath}");
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        var model = Assert.IsType<NifRenderableModel>(NifGeometryExtractor.Extract(
            data, nif,
            skipSkinning: true,
            collectBillboards: true,
            dropBoneAttachedShapes: true,
            treatRootsAsIdentity: true));

        var reflective = model.Submeshes
            .Where(static submesh => submesh.ClassicEnvironmentMapTexturePath is not null)
            .ToArray();
        Assert.NotEmpty(reflective);
        Assert.All(reflective, static submesh =>
        {
            Assert.Equal("BSShaderPPLightingProperty", submesh.ShaderMetadata?.PropertyType);
            Assert.Equal(@"textures\effects\chrome_e.dds", submesh.ClassicEnvironmentMapTexturePath);
            Assert.Equal(
                @"textures\architecture\helios_one\Solar_Reflector_M.dds",
                submesh.ClassicEnvironmentMaskTexturePath);
            Assert.Equal(1f, submesh.ClassicEnvironmentMapScale);
            Assert.Null(submesh.EnvironmentMapTexturePath);
            Assert.Equal(0f, submesh.EnvironmentMapScale);
            Assert.Null(submesh.SpecularMapTexturePath);
        });
    }

    [Fact]
    [Trait("Category", BucketBTestGuard.Category)]
    public void RetailMeshes_ClassicEnvironmentCensusMatchesPcFinal()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var bsaPath = FindRetailMeshesPath();
        using var archive = ArchiveReader.Open(bsaPath);
        var nifEntries = archive.ListFiles()
            .Where(static entry => entry.FullPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var parseErrors = 0;
        var ppLightingProperties = 0;
        var environmentProperties = 0;
        var meshesWithEnvironment = 0;
        var slot4Cubes = 0;
        var slot5Masks = 0;
        var bothCubeAndMask = 0;
        var windowProperties = 0;
        var meshesWithWindowEnvironment = 0;
        var eyeProperties = 0;
        var bit7EyeProperties = 0;
        foreach (var entry in nifEntries)
        {
            NifInfo? nif;
            byte[] data;
            try
            {
                data = archive.Extract(entry);
                nif = NifParser.Parse(data);
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

            var meshHasEnvironment = false;
            var meshHasWindowEnvironment = false;
            for (var blockIndex = 0; blockIndex < nif.Blocks.Count; blockIndex++)
            {
                if (nif.Blocks[blockIndex].TypeName != "BSShaderPPLightingProperty")
                {
                    continue;
                }

                ppLightingProperties++;
                var metadata = NifTextureResolver.ReadShaderMetadata(data, nif, [blockIndex]);
                if (metadata?.ShaderFlags is not { } flags || (flags & (1u << 7)) == 0)
                {
                    continue;
                }

                environmentProperties++;
                meshHasEnvironment = true;
                if ((flags & (1u << 21)) != 0)
                {
                    windowProperties++;
                    meshHasWindowEnvironment = true;
                }

                if ((flags & (1u << 17)) != 0)
                {
                    eyeProperties++;
                    bit7EyeProperties++;
                }

                var hasCube = !string.IsNullOrWhiteSpace(metadata.EnvironmentMapPath);
                var hasMask = !string.IsNullOrWhiteSpace(metadata.EnvironmentMaskPath);
                if (hasCube) slot4Cubes++;
                if (hasMask) slot5Masks++;
                if (hasCube && hasMask) bothCubeAndMask++;
            }

            if (meshHasEnvironment) meshesWithEnvironment++;
            if (meshHasWindowEnvironment) meshesWithWindowEnvironment++;
        }

        Assert.Equal(14_881, nifEntries.Length);
        Assert.Equal(0, parseErrors);
        Assert.Equal(42_317, ppLightingProperties);
        Assert.Equal(4_379, environmentProperties);
        Assert.Equal(1_655, meshesWithEnvironment);
        Assert.Equal(4_148, slot4Cubes);
        Assert.Equal(3_607, slot5Masks);
        Assert.Equal(3_486, bothCubeAndMask);
        Assert.Equal(745, windowProperties);
        Assert.Equal(483, meshesWithWindowEnvironment);
        Assert.Equal(23, eyeProperties);
        Assert.Equal(23, bit7EyeProperties);
    }

    private static MeshArchiveSet OpenRetailMeshes()
    {
        return MeshArchiveSet.Open(FindRetailMeshesPath(), null, false);
    }

    private static string FindRetailMeshesPath()
    {
        var bsaPath = SampleFileFixture.FindSamplePath(MeshesBsaRelative);
        Assert.SkipWhen(bsaPath is null, "FNV PC-final meshes BSA not available");
        return bsaPath!;
    }
}