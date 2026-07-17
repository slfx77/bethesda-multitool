using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifLighting30EmissionPolicyTests
{
    private const string GlowPath = @"textures\architecture\goodsprings\NV_ProspectorSaloon-Neon_g.dds";

    [Theory]
    [InlineData(1u)]
    [InlineData(29u)]
    public void ClassicPpStandardAndLighting30TypesResolveSlot2(uint shaderType)
    {
        var nif = ClassicNif();
        var metadata = Metadata(shaderType: shaderType, glowPath: GlowPath);

        Assert.True(NifLighting30EmissionPolicy.IsStandardLighting30(nif, metadata));
        Assert.Equal(GlowPath, NifLighting30EmissionPolicy.ResolveGlowMapPath(nif, metadata));
    }

    [Fact]
    public void ExplicitLighting30PropertyResolvesSlot2()
    {
        var nif = ClassicNif();
        var metadata = Metadata(
            propertyType: "Lighting30ShaderProperty",
            shaderType: null,
            glowPath: GlowPath);

        Assert.True(NifLighting30EmissionPolicy.IsStandardLighting30(nif, metadata));
        Assert.Equal(GlowPath, NifLighting30EmissionPolicy.ResolveGlowMapPath(nif, metadata));
    }

    [Theory]
    [InlineData(1u << 2)]  // LowDetail
    [InlineData(1u << 10)] // FaceGen
    [InlineData(1u << 18)] // Hair
    public void SpecializedFlags1RejectLighting30Glow(uint shaderFlags)
    {
        var nif = ClassicNif();
        var metadata = Metadata(shaderFlags: shaderFlags, glowPath: GlowPath);

        Assert.False(NifLighting30EmissionPolicy.IsStandardLighting30(nif, metadata));
        Assert.Null(NifLighting30EmissionPolicy.ResolveGlowMapPath(nif, metadata));
    }

    [Theory]
    [InlineData(1u << 1)] // LOD landscape
    [InlineData(1u << 2)] // LOD building
    public void SpecializedFlags2RejectLighting30Glow(uint shaderFlags2)
    {
        var nif = ClassicNif();
        var metadata = Metadata(shaderFlags2: shaderFlags2, glowPath: GlowPath);

        Assert.False(NifLighting30EmissionPolicy.IsStandardLighting30(nif, metadata));
        Assert.Null(NifLighting30EmissionPolicy.ResolveGlowMapPath(nif, metadata));
    }

    [Fact]
    public void FaceGenShaderType14RejectsSlot2()
    {
        var nif = ClassicNif();
        var metadata = Metadata(shaderType: 14u, glowPath: GlowPath);

        Assert.False(NifLighting30EmissionPolicy.IsStandardLighting30(nif, metadata));
        Assert.Null(NifLighting30EmissionPolicy.ResolveGlowMapPath(nif, metadata));
    }

    [Fact]
    public void MissingSlot2DoesNotInventGlowPath()
    {
        var nif = ClassicNif();
        var metadata = Metadata(glowPath: null);

        // The material remains in the Lighting30 family so no-map material emission can still be
        // folded into ambient, but the overloaded texture slot itself fails closed.
        Assert.True(NifLighting30EmissionPolicy.IsStandardLighting30(nif, metadata));
        Assert.Null(NifLighting30EmissionPolicy.ResolveGlowMapPath(nif, metadata));
    }

    [Theory]
    [InlineData(26u)]
    [InlineData(35u)]
    public void NonClassicBsVersionsRejectOverloadedSlot2(uint bsVersion)
    {
        var nif = ClassicNif();
        nif.BsVersion = bsVersion;
        var metadata = Metadata(glowPath: GlowPath);

        Assert.False(NifLighting30EmissionPolicy.IsStandardLighting30(nif, metadata));
        Assert.Null(NifLighting30EmissionPolicy.ResolveGlowMapPath(nif, metadata));
    }

    [Fact]
    public void MissingRawFlagsFailClosed()
    {
        var nif = ClassicNif();
        var metadata = new NifShaderTextureMetadata
        {
            PropertyType = "BSShaderPPLightingProperty",
            ShaderType = 1u,
            TextureSlots = [null, null, GlowPath],
        };

        Assert.False(NifLighting30EmissionPolicy.IsStandardLighting30(nif, metadata));
        Assert.Null(NifLighting30EmissionPolicy.ResolveGlowMapPath(nif, metadata));
    }

    [Fact]
    public void ResolvedExternalEmissionReplacesOnlyRgbAndKeepsMaterialMultiplier()
    {
        var selected = NifLighting30EmissionPolicy.SelectEmission(
            new System.Numerics.Vector3(0.1f, 0.2f, 0.3f),
            21f,
            externalEmittance: true,
            resolvedExternalColor: new System.Numerics.Vector3(0.8f, 0.6f, 0.4f));

        Assert.Equal(new System.Numerics.Vector3(0.8f, 0.6f, 0.4f), selected.Color);
        Assert.Equal(21f, selected.MaterialMultiplier);
    }

    [Fact]
    public void UnresolvedExternalEmissionFallsBackToMaterialRgb()
    {
        var material = new System.Numerics.Vector3(0.1f, 0.2f, 0.3f);
        var selected = NifLighting30EmissionPolicy.SelectEmission(
            material, 2f, externalEmittance: true, resolvedExternalColor: null);

        Assert.Equal(material, selected.Color);
        Assert.Equal(2f, selected.MaterialMultiplier);
    }

    private static NifInfo ClassicNif() => new() { BsVersion = 34u };

    private static NifShaderTextureMetadata Metadata(
        string propertyType = "BSShaderPPLightingProperty",
        uint? shaderType = 1u,
        uint shaderFlags = 0u,
        uint shaderFlags2 = 0u,
        string? glowPath = null) =>
        new()
        {
            PropertyType = propertyType,
            ShaderType = shaderType,
            ShaderFlags = shaderFlags,
            ShaderFlags2 = shaderFlags2,
            TextureSlots = [@"textures\fixture_d.dds", @"textures\fixture_n.dds", glowPath],
        };
}
