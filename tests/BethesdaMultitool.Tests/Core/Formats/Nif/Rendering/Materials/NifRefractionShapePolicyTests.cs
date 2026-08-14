using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

public sealed class NifRefractionShapePolicyTests
{
    [Fact]
    public void RefractionOnlyNormalMapHelper_IsSkipped()
    {
        var metadata = CreateMetadata(
            materialPath: null,
            diffusePath: @"textures\effects\VaporTileNormal_n.dds");

        Assert.True(NifRefractionShapePolicy.ShouldSkipUnsupportedDistortion(130, metadata));
    }

    [Fact]
    public void Fallout4LightingBgsmWithParsedConventionalDiffuse_IsRetained()
    {
        var metadata = CreateMetadata(
            @"materials\Props\NukaColaFull.BGSM",
            @"textures\Props\NukaColaFull_d.dds");

        Assert.False(NifRefractionShapePolicy.ShouldSkipUnsupportedDistortion(
            130,
            metadata,
            @"props/nukacolafull_d.dds"));
    }

    [Theory]
    [InlineData(100u, @"materials\Props\NukaColaFull.BGSM", @"props/nukacolafull_d.dds")]
    [InlineData(130u, null, @"props/nukacolafull_d.dds")]
    [InlineData(130u, @"materials\Props\NukaColaGlass.BGEM", @"props/nukacolafull_d.dds")]
    [InlineData(130u, @"materials\Props\Distortion.BGSM", @"effects/distortion.dds")]
    [InlineData(130u, @"materials\Props\Distortion.BGSM", @"effects/distortion_n.dds")]
    [InlineData(130u, @"materials\Props\NukaColaFull.BGSM", null)]
    public void RetentionException_FailsClosedOutsideExactFo4LightingSurface(
        uint bsVersion,
        string? materialPath,
        string? parsedMaterialDiffuse)
    {
        Assert.True(NifRefractionShapePolicy.ShouldSkipUnsupportedDistortion(
            bsVersion,
            CreateMetadata(materialPath, @"textures\Props\NukaColaFull_d.dds"),
            parsedMaterialDiffuse));
    }

    [Fact]
    public void ShapeWithoutRefractionFlags_IsNotSkipped()
    {
        var metadata = CreateMetadata(null, null, shaderFlags: 0u);

        Assert.False(NifRefractionShapePolicy.ShouldSkipUnsupportedDistortion(130, metadata));
    }

    [Fact]
    public void Fallout4ClassicLightingPropertyDoesNotUseTheException()
    {
        var metadata = CreateMetadata(
            @"materials\Props\NukaColaFull.BGSM",
            @"textures\Props\NukaColaFull_d.dds",
            propertyType: "BSShaderPPLightingProperty");

        Assert.True(NifRefractionShapePolicy.ShouldSkipUnsupportedDistortion(
            130,
            metadata,
            @"props/nukacolafull_d.dds"));
    }

    private static NifShaderTextureMetadata CreateMetadata(
        string? materialPath,
        string? diffusePath,
        uint shaderFlags = 1u << 16,
        string propertyType = "BSLightingShaderProperty")
    {
        return new NifShaderTextureMetadata
        {
            PropertyType = propertyType,
            ShaderFlags = shaderFlags,
            MaterialPath = materialPath,
            TextureSlots = [diffusePath]
        };
    }
}
