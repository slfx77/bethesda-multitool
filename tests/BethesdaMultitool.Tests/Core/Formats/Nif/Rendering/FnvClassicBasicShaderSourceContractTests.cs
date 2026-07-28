using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class FnvClassicBasicShaderSourceContractTests
{
    [Fact]
    public void DormantPs1RouteFailsClosedAndBothReferenceDrawPathsShareTheGate()
    {
        var policy = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Materials",
            "FnvClassicBasicShaderPolicy.cs");
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceRenderer12.cs");

        Assert.Contains("internal const bool RetailRuntimeSupported = false", policy,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyRuntimeFlags", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeMaterialFlag", policy, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeVertexColorFlag", policy, StringComparison.Ordinal);
        Assert.Contains("private Vector4 ResolveTextureState(CachedSubmesh12 submesh)", renderer,
            StringComparison.Ordinal);
        Assert.Contains("FnvActiveAdtBasePolicy.ApplyRuntimeFlags", renderer,
            StringComparison.Ordinal);
        Assert.Equal(1, renderer.Split("ResolveTextureState(sub)").Length - 1);
        Assert.Equal(1, renderer.Split("ResolveTextureState(draw.Submesh)").Length - 1);
        Assert.DoesNotContain("submesh.TextureState =", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void VertexShadersReproduceTheActiveSls2000NormalizedLightInterpolator()
    {
        foreach (var shaderName in new[] { "reference.vert.hlsl", "reference_instanced.vert.hlsl" })
        {
            var shader = ReadShader(shaderName);
            Assert.Contains("float3 vFnvActiveAdtBaseLight : TEXCOORD16;", shader,
                StringComparison.Ordinal);
            Assert.Contains("return normalize(lightTs);", shader,
                StringComparison.Ordinal);
            Assert.Contains("dot(tangent, uSunDirIntensity.xyz)", shader, StringComparison.Ordinal);
            Assert.Contains("dot(bitangent, uSunDirIntensity.xyz)", shader, StringComparison.Ordinal);
            Assert.Contains("dot(normal, uSunDirIntensity.xyz)) / uniformScale", shader,
                StringComparison.Ordinal);
            Assert.DoesNotContain("tangent * rsqrt", shader, StringComparison.Ordinal);
            Assert.DoesNotContain("bitangent * rsqrt", shader, StringComparison.Ordinal);
            Assert.DoesNotContain("normal * rsqrt", shader, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PixelShaderUsesExactActiveSls2000SignedDp3AggregateClampAndToggleComposite()
    {
        var shader = ReadShader("reference.frag.hlsl");
        var exactStart = shader.IndexOf(
            "// PC-final package013 SLS2000.pso", StringComparison.Ordinal);
        var exactEnd = shader.IndexOf("// Bethesda's DDS normals", exactStart, StringComparison.Ordinal);
        Assert.True(exactStart >= 0 && exactEnd > exactStart);
        var exactDot = shader[exactStart..exactEnd];
        Assert.Contains("normalize(normalSample.rgb * 2.0 - 1.0)", exactDot,
            StringComparison.Ordinal);
        Assert.Contains(
            "fnvActiveAdtBaseNdotL = dot(activeAdtNormal, input.vFnvActiveAdtBaseLight);",
            exactDot,
            StringComparison.Ordinal);
        Assert.DoesNotContain("saturate(", exactDot, StringComparison.Ordinal);

        var compositeStart = shader.IndexOf("// SLS2000 Toggles.x", StringComparison.Ordinal);
        var compositeEnd = shader.IndexOf("// FNV sun specular", compositeStart, StringComparison.Ordinal);
        Assert.True(compositeStart >= 0 && compositeEnd > compositeStart);
        var composite = shader[compositeStart..compositeEnd];
        Assert.Contains("? baseLit * vertexRgb", composite,
            StringComparison.Ordinal);
        Assert.Contains(": baseLit * vertexRgb;", composite, StringComparison.Ordinal);
        Assert.DoesNotContain("input.vVertexColor.a * (vertexRgb - baseLit)", shader,
            StringComparison.Ordinal);

        var shadeStart = shader.IndexOf("// Active ID193/BSSM_ADT", StringComparison.Ordinal);
        var shadeEnd = shader.IndexOf("else", shadeStart, StringComparison.Ordinal);
        Assert.True(shadeStart >= 0 && shadeEnd > shadeStart);
        var exactShade = shader[shadeStart..shadeEnd];
        Assert.Contains("shade = max(", exactShade, StringComparison.Ordinal);
        Assert.Contains(
            "uAmbientColor.rgb + uSunColorLighting.rgb * fnvActiveAdtBaseNdotL",
            exactShade, StringComparison.Ordinal);
        Assert.Contains("0.0);", exactShade, StringComparison.Ordinal);
        Assert.DoesNotContain("sunShadow", exactShade, StringComparison.Ordinal);
        Assert.DoesNotContain("PlacedLightContribution", exactShade, StringComparison.Ordinal);
        Assert.DoesNotContain("ambientScale", exactShade, StringComparison.Ordinal);

        Assert.Contains("float outAlpha = fnvActiveAdtBase", shader, StringComparison.Ordinal);
        Assert.Contains("? sampleAlpha", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveredArtifactSeparatesFogAndVertexColorPermutations()
    {
        var artifact = SourceContract.ReadSource(
            "docs", "fnv_basic_sls_shader_disassembly.txt");

        Assert.Contains("PS 1010 = SLS_PS1_PPAMBDIFFUSETEXTUREDIR_F   (fog)", artifact,
            StringComparison.Ordinal);
        Assert.Contains("PS 1013 = SLS_PS1_PPAMBDIFFUSETEXTUREDIR_Vc  (vertex color)", artifact,
            StringComparison.Ordinal);
        Assert.Contains("VS 1012 = SLS_VS1_PPAMBDIFFUSETEXTUREDIR_Vc  (vertex color)", artifact,
            StringComparison.Ordinal);

        var fogPs = Section(artifact, "; === SLS1010.pso", "; === SLS1013.pso");
        Assert.Contains("mad r0.xyz, v0.wwww, r1, r2", fogPs, StringComparison.Ordinal);

        var vertexColorPs = Section(artifact, "; === SLS1013.pso", "; === SLS1010.vso");
        Assert.Contains("dcl_u00 v0.xyz", vertexColorPs, StringComparison.Ordinal);
        Assert.Contains("mul r2.xyz, r0, v0", vertexColorPs, StringComparison.Ordinal);
        Assert.DoesNotContain("v0.wwww", vertexColorPs, StringComparison.Ordinal);

        var vertexColorVs = Section(artifact, "; === SLS1012.vso", "; === SLS1021.pso");
        Assert.Contains("dcl_color0 v5", vertexColorVs, StringComparison.Ordinal);
        Assert.Contains("mov aout0, v5", vertexColorVs, StringComparison.Ordinal);
    }

    [Fact]
    public void EligibilityExcludesNeighboringMaterialFamiliesBeforePersistence()
    {
        var policy = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Materials",
            "FnvClassicBasicShaderPolicy.cs");
        var decoder = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
            "ReferenceMeshDecoder12.cs");

        foreach (var token in new[]
                 {
                     "SpecularFlag", "EnvironmentMappingFlag", "ParallaxFlag", "FaceGenFlag",
                     "HairFlag", "RefractionFlags", "DecalFlags", "ExternalEmittanceFlag",
                     "Lighting30GlowMapTexturePath", "Lighting30EmissionColor", "IsParticleCloud",
                     "IsSpeedTreeBranch"
                 })
        {
            Assert.Contains(token, policy, StringComparison.Ordinal);
        }

        Assert.Contains("FnvClassicBasicShaderPolicy.Resolve(nif, sub, diffusePath, normalPath)", decoder,
            StringComparison.Ordinal);
        Assert.Contains("submesh.BindPosePositions is not null", policy, StringComparison.Ordinal);
    }

    private static string ReadShader(string fileName)
    {
        return SourceContract.ReadShaderSource(fileName);
    }

    private static string Section(string source, string startToken, string endToken)
    {
        var start = source.IndexOf(startToken, StringComparison.Ordinal);
        var end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }
}