using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

public sealed class OblivionHairLayerPolicyTests
{
    [Fact]
    public void ActorHair_SelectsActorIconLayerWithoutChangingCoverageOrNormalAlpha()
    {
        using var textures = new NifTextureResolver();
        textures.InjectTexture(OblivionHairLayerTestData.Layer, TestTextures.Single(247, 235, 222, 255));
        textures.InjectTexture("textures/characters/hair/authored_hl.dds", TestTextures.Single(1, 2, 3, 0));
        var source = OblivionHairLayerTestData.Create();
        source.DiffuseTexturePath = "textures/characters/hair/authored.dds";
        var colors = (byte[])source.VertexColors!.Clone();
        var model = new NifRenderableModel { Submeshes = { source } };

        NpcHairSubmeshPolicy.Apply(model, BethesdaGame.Oblivion, source.TintColor, textures,
            OblivionHairLayerTestData.Diffuse);

        Assert.Equal(OblivionHairLayerTestData.Layer,
            OblivionHairLayerPolicy.ResolveTexturePath(source, OblivionHairLayerTestData.Diffuse));
        Assert.Null(source.SpecularMapTexturePath);
        Assert.Null(source.NormalMapTexturePath); // Layer operation remains independent of missing bump data.
        Assert.Equal(colors, source.VertexColors);
        Assert.True(source.HasAlphaBlend);
        Assert.True(source.HasAlphaTest);
        Assert.Equal(0, source.AlphaTestThreshold);
        Assert.Equal(1f, source.MaterialAlpha);
    }

    [Fact]
    public void MissingLayerAndOtherGames_ClearStaleActorBindings()
    {
        using var textures = new NifTextureResolver();
        var source = OblivionHairLayerTestData.Create();
        source.OblivionHairLayerDiffusePath = OblivionHairLayerTestData.Diffuse;
        source.OblivionHairLayerTexturePath = OblivionHairLayerTestData.Layer;
        OblivionHairLayerPolicy.Apply(source, textures, OblivionHairLayerTestData.Diffuse);
        Assert.Null(source.OblivionHairLayerTexturePath);
        textures.InjectTexture(OblivionHairLayerTestData.Layer, TestTextures.Single(247, 235, 222, 255));
        foreach (var game in Enum.GetValues<BethesdaGame>().Where(value => value != BethesdaGame.Oblivion))
        {
            source.UsesClassicHairMaterial = true;
            OblivionHairLayerPolicy.Apply(source, textures, OblivionHairLayerTestData.Diffuse);
            Assert.NotNull(source.OblivionHairLayerTexturePath);
            NpcHairSubmeshPolicy.Apply(new NifRenderableModel { Submeshes = { source } }, game,
                source.TintColor, textures, OblivionHairLayerTestData.Diffuse);
            Assert.False(source.UsesClassicHairMaterial);
            Assert.Null(source.OblivionHairLayerDiffusePath);
            Assert.Null(source.OblivionHairLayerTexturePath);
        }
    }

    [Fact]
    public void DecodeAdmission_RechecksNormalizedCurrentFamilyAndPositiveSourceProof()
    {
        using var textures = new NifTextureResolver();
        textures.InjectTexture(OblivionHairLayerTestData.Layer, TestTextures.Single(247, 235, 222, 255));
        var source = OblivionHairLayerTestData.Create();
        OblivionHairLayerPolicy.Apply(source, textures, OblivionHairLayerTestData.Diffuse);
        Assert.NotNull(OblivionHairLayerPolicy.ResolveTexturePath(source, @"Textures\Characters\Hair\GREY.dds"));
        Assert.Null(OblivionHairLayerPolicy.ResolveTexturePath(source, "textures/characters/hair/red.dds"));
        source.DiffuseTexturePath = "textures/characters/hair/red.dds";
        Assert.Null(OblivionHairLayerPolicy.ResolveTexturePath(source, source.DiffuseTexturePath));
        source.DiffuseTexturePath = OblivionHairLayerTestData.Diffuse;
        source.OblivionHairLayerTexturePath = "textures/characters/hair/grey_hh.dds";
        Assert.Null(OblivionHairLayerPolicy.ResolveTexturePath(source, source.DiffuseTexturePath));
    }

    [Theory]
    [InlineData("unknown-source")]
    [InlineData("generic-hair")]
    [InlineData("skin")]
    [InlineData("hair-variant")]
    [InlineData("facegen")]
    [InlineData("eye")]
    [InlineData("emissive")]
    [InlineData("lighting30")]
    [InlineData("two-sided")]
    [InlineData("green")]
    [InlineData("alpha")]
    [InlineData("material-alpha")]
    [InlineData("threshold")]
    [InlineData("test-function")]
    [InlineData("material-diffuse")]
    [InlineData("uv")]
    [InlineData("no-tint")]
    [InlineData("invalid-tint")]
    [InlineData("specular-lane")]
    [InlineData("parallax-lane")]
    [InlineData("environment-lane")]
    [InlineData("gradient-lane")]
    public void MaterialReplacement_CannotInheritLayerAdmission(string variant)
    {
        using var textures = new NifTextureResolver();
        textures.InjectTexture(OblivionHairLayerTestData.Layer, TestTextures.Single(247, 235, 222, 255));
        var source = OblivionHairLayerTestData.Create();
        OblivionHairLayerPolicy.Apply(source, textures, OblivionHairLayerTestData.Diffuse);
        Assert.NotNull(source.OblivionHairLayerTexturePath);
        ChangeMaterial(source, variant);
        Assert.Null(OblivionHairLayerPolicy.ResolveTexturePath(source, source.DiffuseTexturePath));
        OblivionHairLayerPolicy.Apply(source, textures, OblivionHairLayerTestData.Diffuse);
        Assert.Null(source.OblivionHairLayerTexturePath);
    }

    [Theory]
    [InlineData((int)GpuTexturePayloadFormat.Rgba8, true)]
    [InlineData((int)GpuTexturePayloadFormat.BC1, true)]
    [InlineData((int)GpuTexturePayloadFormat.BC2, true)]
    [InlineData((int)GpuTexturePayloadFormat.BC3, true)]
    [InlineData((int)GpuTexturePayloadFormat.BC7, true)]
    [InlineData((int)GpuTexturePayloadFormat.BC4, false)]
    [InlineData((int)GpuTexturePayloadFormat.BC5, false)]
    [InlineData((int)GpuTexturePayloadFormat.BC4S, false)]
    [InlineData((int)GpuTexturePayloadFormat.BC5S, false)]
    [InlineData(999, false)]
    public void Residency_RequiresKnownResidentColorPayload(int format, bool supported)
    {
        var payloadFormat = (GpuTexturePayloadFormat)format;
        Assert.False(
            OblivionHairLayerPolicy.IsResidentLayer(OblivionHairLayerTestData.Layer, false, false, payloadFormat));
        Assert.Equal(supported,
            OblivionHairLayerPolicy.IsResidentLayer(OblivionHairLayerTestData.Layer, true, false, payloadFormat));
        Assert.False(
            OblivionHairLayerPolicy.IsResidentLayer(OblivionHairLayerTestData.Layer, true, true, payloadFormat));
        Assert.False(OblivionHairLayerPolicy.IsResidentLayer(null, true, false, payloadFormat));
        Assert.False(OblivionHairLayerPolicy.IsResidentLayer(" ", true, false, payloadFormat));
    }

    private static void ChangeMaterial(RenderableSubmesh source, string variant)
    {
        switch (variant)
        {
            case "unknown-source": source.HasAuthoredOblivionHairLayerInputs = false; break;
            case "generic-hair": source.UsesClassicHairMaterial = false; break;
            case "skin": source.LegacyMaterialName = "skin"; break;
            case "hair-variant": source.LegacyMaterialName = "HairVariant"; break;
            case "facegen": source.IsFaceGen = true; break;
            case "eye": source.IsEyeEnvmap = true; break;
            case "emissive": source.IsEmissive = true; break;
            case "lighting30": source.IsLighting30 = true; break;
            case "two-sided": source.IsDoubleSided = true; break;
            case "green": source.VertexColors![1] = 254; break;
            case "alpha": source.VertexColors![3] = 254; break;
            case "material-alpha": source.MaterialAlpha = 0.5f; break;
            case "threshold": source.AlphaTestThreshold = 128; break;
            case "test-function": source.AlphaTestFunction = 1; break;
            case "material-diffuse": source.MaterialDiffuse = (0.5f, 1f, 1f); break;
            case "uv": source.UVs![0] = float.NaN; break;
            case "no-tint": source.TintColor = null; break;
            case "invalid-tint": source.TintColor = (float.NaN, 1f, 1f); break;
            case "specular-lane": source.SpecularMapTexturePath = "other.dds"; break;
            case "parallax-lane": source.ClassicParallaxHeightMapTexturePath = "other.dds"; break;
            case "environment-lane": source.ClassicEnvironmentMaskTexturePath = "other.dds"; break;
            case "gradient-lane": source.GradientMapTexturePath = "other.dds"; break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }
    }
}