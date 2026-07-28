using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

public sealed class FnvClassicBasicShaderPolicyTests
{
    [Fact]
    public void Resolve_SelectsSls1009WithoutVertexDataAndSls1013WithCompleteVertexData()
    {
        Assert.Equal(
            FnvClassicBasicShaderMode.Sls1009,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), Submesh()));
        Assert.Equal(
            FnvClassicBasicShaderMode.Sls1013VertexColor,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), Submesh(true)));
    }

    [Theory]
    [InlineData(1u << 0)] // specular
    [InlineData(1u << 1)] // skinned pass flag
    [InlineData(1u << 2)] // low detail
    [InlineData(1u << 5)] // forced single pass
    [InlineData(1u << 7)] // environment map
    [InlineData(1u << 10)] // FaceGen
    [InlineData(1u << 11)] // parallax
    [InlineData(1u << 15)] // refraction
    [InlineData(1u << 16)] // fire refraction
    [InlineData(1u << 17)] // eye environment
    [InlineData(1u << 18)] // hair
    [InlineData(1u << 21)] // window environment
    [InlineData(1u << 26)] // decal
    [InlineData(1u << 27)] // dynamic decal
    [InlineData(1u << 28)] // POM
    [InlineData(1u << 29)] // external emittance
    public void Resolve_RejectsEveryNeighboringShaderFlag(uint excludedFlag)
    {
        Assert.Equal(
            FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(),
                Submesh(shaderFlags: 0x82000000u | excludedFlag)));
    }

    [Fact]
    public void Resolve_FailsClosedForOtherLayoutsPropertiesAndMaterialFamilies()
    {
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(new NifInfo { BsVersion = 83 }, Submesh()));
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(), Submesh(propertyType: "BSLightingShaderProperty")));
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), Submesh(shaderType: 14)));
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(), Submesh(shaderType: NifLighting30EmissionPolicy.Lighting30ShaderType)));
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), Submesh(shaderFlags2: 1u << 1)));
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), Submesh(includeTangents: false)));
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), Submesh(glowMap: "textures\\foo_g.dds")));
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(), Submesh(emission: (0.1f, 0f, 0f))));
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), Submesh(isParticle: true)));
        Assert.Equal(FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), Submesh(isSkinned: true)));
    }

    [Fact]
    public void Resolve_UsesEffectiveAlternateTexturePaths()
    {
        var missingBaseTextures = Submesh(diffusePath: null, normalPath: null);
        Assert.Equal(
            FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), missingBaseTextures));
        Assert.Equal(
            FnvClassicBasicShaderMode.Sls1009,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(),
                missingBaseTextures,
                "textures\\override.dds",
                "textures\\override_n.dds"));
    }

    [Fact]
    public void Resolve_RequiresFiniteUsableUvAndBasisAtEveryVertex()
    {
        Assert.Equal(
            FnvClassicBasicShaderMode.Sls1009,
            FnvClassicBasicShaderPolicy.Resolve(ClassicNif(), Submesh(withSecondVertex: true)));
        Assert.Equal(
            FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(), Submesh(withSecondVertex: true, secondNormal: Vector3.Zero)));
        Assert.Equal(
            FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(), Submesh(withSecondVertex: true, secondTangent: Vector3.Zero)));
        Assert.Equal(
            FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(), Submesh(
                    withSecondVertex: true,
                    secondBitangent: new Vector3(0f, float.PositiveInfinity, 0f))));
        Assert.Equal(
            FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(), Submesh(withSecondVertex: true, secondUv: new Vector2(float.NaN, 0.5f))));
    }

    [Fact]
    public void Resolve_RejectsInvalidIndicesAndUnusedMalformedVertices()
    {
        Assert.Equal(
            FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(), Submesh(withSecondVertex: true, triangles: [0, 1, 2])));
        Assert.Equal(
            FnvClassicBasicShaderMode.None,
            FnvClassicBasicShaderPolicy.Resolve(
                ClassicNif(), Submesh(
                    withSecondVertex: true,
                    secondNormal: new Vector3(float.NegativeInfinity, 0f, 1f),
                    triangles: [0, 0, 0])));
    }

    [Fact]
    public void RetailRuntimeSupportStaysDisabledBecauseRetailDoesNotSelectThePs1PassFamily()
    {
        Assert.False(FnvClassicBasicShaderPolicy.RetailRuntimeSupported);
    }

    [Fact]
    public void RecoveredEquationsKeepSignedDp3AndSls1013MultipliesVertexRgb()
    {
        var ambient = new Vector3(0.2f, 0.3f, 0.4f);
        var light = new Vector3(0.8f, 0.6f, 0.4f);
        var shade = FnvClassicBasicShaderPolicy.EvaluateShade(ambient, -0.25f, light);
        VectorAssert.Equal(new Vector3(0f, 0.15f, 0.3f), shade);

        var baseMap = new Vector3(0.5f, 0.25f, 0.75f);
        var baseLit = FnvClassicBasicShaderPolicy.Composite(
            FnvClassicBasicShaderMode.Sls1009,
            baseMap, shade, new Vector3(0.9f));
        VectorAssert.Equal(new Vector3(0f, 0.0375f, 0.225f), baseLit);

        var vertex = new Vector3(0.8f, 0.4f, 0.2f);
        var vertexComposite = FnvClassicBasicShaderPolicy.Composite(
            FnvClassicBasicShaderMode.Sls1013VertexColor,
            baseMap, shade, vertex);
        VectorAssert.Equal(Vector3.Multiply(baseLit, vertex), vertexComposite);
    }

    private static NifInfo ClassicNif()
    {
        return new NifInfo { BsVersion = 34 };
    }

    private static RenderableSubmesh Submesh(
        bool withVertexColors = false,
        uint shaderFlags = 0x82000000u,
        uint shaderFlags2 = 0u,
        uint shaderType = NifLighting30EmissionPolicy.StandardShaderType,
        string propertyType = "BSShaderPPLightingProperty",
        bool includeTangents = true,
        string? glowMap = null,
        (float R, float G, float B)? emission = null,
        bool isParticle = false,
        bool withSecondVertex = false,
        Vector3? secondNormal = null,
        Vector3? secondTangent = null,
        Vector3? secondBitangent = null,
        Vector2? secondUv = null,
        ushort[]? triangles = null,
        bool isSkinned = false,
        string? diffusePath = "textures\\foo.dds",
        string? normalPath = "textures\\foo_n.dds")
    {
        var normal2 = secondNormal ?? Vector3.UnitZ;
        var tangent2 = secondTangent ?? Vector3.UnitX;
        var bitangent2 = secondBitangent ?? Vector3.UnitY;
        var uv2 = secondUv ?? new Vector2(0.5f, 0.5f);
        var positions = withSecondVertex
            ? new[] { 0f, 0f, 0f, 1f, 0f, 0f }
            : new[] { 0f, 0f, 0f };
        var normals = withSecondVertex
            ? new[] { 0f, 0f, 1f, normal2.X, normal2.Y, normal2.Z }
            : new[] { 0f, 0f, 1f };
        float[]? tangents = null;
        if (includeTangents)
        {
            tangents = withSecondVertex
                ? new[] { 1f, 0f, 0f, tangent2.X, tangent2.Y, tangent2.Z }
                : new[] { 1f, 0f, 0f };
        }

        var bitangents = withSecondVertex
            ? new[] { 0f, 1f, 0f, bitangent2.X, bitangent2.Y, bitangent2.Z }
            : new[] { 0f, 1f, 0f };
        var uvs = withSecondVertex
            ? new[] { 0.25f, 0.75f, uv2.X, uv2.Y }
            : new[] { 0.25f, 0.75f };
        byte[]? vertexColors = null;
        if (withVertexColors)
        {
            vertexColors = withSecondVertex
                ? [64, 128, 192, 32, 255, 255, 255, 255]
                : [64, 128, 192, 32];
        }

        return new RenderableSubmesh
        {
            Positions = positions,
            Triangles = triangles ?? [],
            Normals = normals,
            UVs = uvs,
            Tangents = tangents,
            Bitangents = bitangents,
            VertexColors = vertexColors,
            BindPosePositions = isSkinned ? (float[])positions.Clone() : null,
            UseVertexColors = true,
            DiffuseTexturePath = diffusePath,
            NormalMapTexturePath = normalPath,
            ShaderMetadata = new NifShaderTextureMetadata
            {
                PropertyType = propertyType,
                ShaderType = shaderType,
                ShaderFlags = shaderFlags,
                ShaderFlags2 = shaderFlags2,
                TextureSlots =
                [
                    "textures\\foo.dds",
                    "textures\\foo_n.dds",
                    glowMap,
                    null,
                    null,
                    null,
                    null,
                    null
                ]
            },
            Lighting30GlowMapTexturePath = glowMap,
            Lighting30EmissionColor = emission,
            IsParticleCloud = isParticle
        };
    }
}