using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

public sealed class NifClassicParallaxPolicyTests
{
    private const string HeightPath = @"textures\landscape\RubblePile05_p.dds";

    [Fact]
    public void Resolve_RequiresClassicPropertyBit11AndSlot3AndExplicitlyExcludesPom()
    {
        var material = NifClassicParallaxPolicy.Resolve(Metadata(
            NifClassicParallaxPolicy.ParallaxFlag,
            HeightPath));

        Assert.NotNull(material);
        Assert.Equal(HeightPath, material.Value.HeightMapTexturePath);
        Assert.Null(NifClassicParallaxPolicy.Resolve(Metadata(0, HeightPath)));
        Assert.Null(NifClassicParallaxPolicy.Resolve(Metadata(
            NifClassicParallaxPolicy.ParallaxFlag |
            NifClassicParallaxPolicy.ParallaxOcclusionFlag,
            HeightPath)));
        Assert.Null(NifClassicParallaxPolicy.Resolve(Metadata(
            NifClassicParallaxPolicy.ParallaxFlag,
            null)));
        Assert.Null(NifClassicParallaxPolicy.Resolve(Metadata(
            NifClassicParallaxPolicy.ParallaxFlag,
            HeightPath,
            "BSLightingShaderProperty")));
        Assert.Null(NifClassicParallaxPolicy.Resolve(Metadata(
            NifClassicParallaxPolicy.ParallaxFlag,
            HeightPath,
            "Lighting30ShaderProperty")));
    }

    [Fact]
    public void HasUsableGeometry_RequiresUvAndAtLeastOneFiniteNondegenerateTbn()
    {
        Assert.True(NifClassicParallaxPolicy.HasUsableGeometry(Submesh()));
        Assert.False(NifClassicParallaxPolicy.HasUsableGeometry(Submesh(false)));
        Assert.False(NifClassicParallaxPolicy.HasUsableGeometry(Submesh(includeTangents: false)));
        Assert.False(NifClassicParallaxPolicy.HasUsableGeometry(Submesh(
            tangents: [0f, 0f, 0f])));
        Assert.False(NifClassicParallaxPolicy.HasUsableGeometry(Submesh(
            bitangents: [float.NaN, 1f, 0f])));
    }

    [Fact]
    public void ComputeMaterialUv_MatchesSm3004HardcodedOneSampleEquation()
    {
        var shifted = NifClassicParallaxPolicy.ComputeMaterialUv(
            new Vector2(0.2f, 0.3f),
            0.75f,
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
            new Vector3(3f, 4f, 12f));

        Assert.Equal(0.20230769f, shifted.X, 6);
        Assert.Equal(0.30307692f, shifted.Y, 6);

        var neutralHeight = NifClassicParallaxPolicy.ComputeMaterialUv(
            new Vector2(0.2f, 0.3f),
            0.5f,
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
            new Vector3(3f, 4f, 12f));
        Assert.Equal(new Vector2(0.2f, 0.3f), neutralHeight);

        var headOn = NifClassicParallaxPolicy.ComputeMaterialUv(
            new Vector2(0.2f, 0.3f),
            0.75f,
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
            Vector3.UnitZ);
        Assert.Equal(new Vector2(0.2f, 0.3f), headOn);
    }

    private static NifShaderTextureMetadata Metadata(
        uint flags,
        string? heightPath,
        string propertyType = "BSShaderPPLightingProperty")
    {
        return new NifShaderTextureMetadata
        {
            PropertyType = propertyType,
            ShaderFlags = flags,
            TextureSlots = [null, null, null, heightPath, null, null, null, null]
        };
    }

    private static RenderableSubmesh Submesh(
        bool includeUvs = true,
        bool includeTangents = true,
        float[]? tangents = null,
        float[]? bitangents = null)
    {
        return new RenderableSubmesh
        {
            Positions = [0f, 0f, 0f],
            Triangles = [],
            Normals = [0f, 0f, 1f],
            UVs = includeUvs ? [0.25f, 0.75f] : null,
            Tangents = includeTangents ? tangents ?? [1f, 0f, 0f] : null,
            Bitangents = bitangents ?? [0f, 1f, 0f]
        };
    }
}