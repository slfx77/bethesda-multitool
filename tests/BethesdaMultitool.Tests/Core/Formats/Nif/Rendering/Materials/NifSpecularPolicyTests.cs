using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

public sealed class NifSpecularPolicyTests
{
    [Fact]
    public void ClassicPpLighting_UsesAuthoredFlagAndNormalMaskPath_NotBlackMaterialTint()
    {
        var submesh = ClassicSubmesh(1u, (0f, 0f, 0f));

        Assert.True(NifSpecularPolicy.IsEnabled(submesh));
    }

    [Fact]
    public void ClassicPpLighting_KeepsSpecularBitClearShapeDisabled()
    {
        var submesh = ClassicSubmesh(0u, (1f, 1f, 1f));

        Assert.False(NifSpecularPolicy.IsEnabled(submesh));
    }

    [Fact]
    public void ClassicPpLighting_RequiresConsumableNormalTbnAndPositiveGloss()
    {
        Assert.False(NifSpecularPolicy.IsEnabled(ClassicSubmesh(1u, withNormalPath: false)));
        Assert.False(NifSpecularPolicy.IsEnabled(ClassicSubmesh(1u, withTangents: false)));
        Assert.False(NifSpecularPolicy.IsEnabled(ClassicSubmesh(1u, glossiness: 0f)));
    }

    [Fact]
    public void RetailCliffVertiC2_AuthoredSpecularShapesSurviveBlackMaterialColor()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        // FalloutNV.esm STAT 0x0015626B CliffVertiC2.
        var model = LoadRetailModel(@"meshes\landscape\rocks\cliffs\cliffverti_c2.nif");
        Assert.SkipWhen(model is null, "Retail Fallout - Meshes.bsa is not available.");

        var lit = model!.Submeshes
            .Where(static sub => sub.ShaderMetadata?.PropertyType == "BSShaderPPLightingProperty")
            .ToArray();

        Assert.Equal(3, lit.Length);
        Assert.All(lit, sub =>
        {
            Assert.Equal(1u, sub.ShaderMetadata!.ShaderFlags!.Value & 1u);
            Assert.Equal((0f, 0f, 0f), sub.SpecularColor);
            Assert.NotNull(sub.NormalMapTexturePath);
            Assert.NotNull(sub.Tangents);
            Assert.NotNull(sub.Bitangents);
            Assert.True(NifSpecularPolicy.IsEnabled(sub));
        });
    }

    [Fact]
    public void RetailProspectorSaloon_RespectsPerShapeSpecularFlag()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        // FalloutNV.esm STAT 0x0010243E NVProspectorSaloon.
        var model = LoadRetailModel(@"meshes\architecture\goodsprings\nv_prospectorsaloon.nif");
        Assert.SkipWhen(model is null, "Retail Fallout - Meshes.bsa is not available.");

        var main = Assert.Single(model!.Submeshes,
            static sub => sub.ShapeName == "NV_ProspectorSaloon:0");
        Assert.Equal(0u, main.ShaderMetadata!.ShaderFlags!.Value & 1u);
        Assert.False(NifSpecularPolicy.IsEnabled(main));

        var flaggedBlack = model.Submeshes
            .Where(static sub =>
                sub.ShaderMetadata?.ShaderFlags is uint flags && (flags & 1u) != 0 &&
                sub.SpecularColor == (0f, 0f, 0f))
            .ToArray();
        Assert.NotEmpty(flaggedBlack);
        Assert.All(flaggedBlack, sub => Assert.True(NifSpecularPolicy.IsEnabled(sub)));
    }

    private static RenderableSubmesh ClassicSubmesh(
        uint shaderFlags,
        (float R, float G, float B)? specularColor = null,
        float glossiness = 10f,
        bool withNormalPath = true,
        bool withTangents = true)
    {
        return new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            Normals = [0f, 0f, 1f],
            Tangents = withTangents ? [1f, 0f, 0f] : null,
            Bitangents = withTangents ? [0f, 1f, 0f] : null,
            NormalMapTexturePath = withNormalPath ? @"textures\fixture_n.dds" : null,
            MaterialGlossiness = glossiness,
            SpecularColor = specularColor ?? (0f, 0f, 0f),
            ShaderMetadata = new NifShaderTextureMetadata
            {
                PropertyType = "BSShaderPPLightingProperty",
                ShaderFlags = shaderFlags
            }
        };
    }

    private static NifRenderableModel? LoadRetailModel(string path)
    {
        var bsaPath = FindRetailMeshesBsa();
        if (bsaPath is null)
        {
            return null;
        }

        using var extractor = new BsaExtractor(bsaPath);
        var file = extractor.Archive.FindFile(path);
        if (file is null)
        {
            return null;
        }

        var data = extractor.ExtractFile(file);
        var nif = NifParser.Parse(data);
        return nif is null
            ? null
            : NifGeometryExtractor.Extract(data, nif, skipSkinning: true,
                collectBillboards: true, dropBoneAttachedShapes: true);
    }

    private static string? FindRetailMeshesBsa()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT") ?? string.Empty,
                "Fallout - Meshes.bsa"),
            Path.GetFullPath(Path.Combine("Sample", "Full_Builds", "Fallout New Vegas (PC Final)",
                "Data", "Fallout - Meshes.bsa")),
            RealAssetPaths.SteamGameFile("Fallout New Vegas", @"Data\Fallout - Meshes.bsa")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}