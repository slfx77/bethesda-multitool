using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRendererConstants12;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Pins the bounded Starfield constant- and vertex-Lerp operations across extraction,
///     persistence, GPU constants, and both specialized and fallback shaders without widening the
///     shared opaque-draw ABI.
/// </summary>
public sealed class StarfieldConstantLerpRenderPathSourceContractTests
{
    [Fact]
    public void VendoredReferencePinsVertexRgbAndAlphaAsTheLerpInputs()
    {
        var reference = SourceContract.ReadSource(
            "Sample", "Reference_Code", "nifskope", "res", "shaders", "stf_default.frag");

        SourceContract.AssertOrder(
            reference,
            "tintColor = ( (lm.layers[i].material.flags & 2) == 0 ? lm.layers[i].material.color : C );",
            "if ( (lm.layers[i].material.flags & 1) == 0 )",
            "layerBaseMap *= tintColor.rgb;",
            "else",
            "layerBaseMap = mix( layerBaseMap, tintColor.rgb, tintColor.a );");
    }

    [Fact]
    public void StateFlowsFromRenderableThroughDecodedCacheAndCachedSubmesh()
    {
        var renderable = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering",
            "RenderableSubmesh.cs");
        var extractor = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Geometry",
            "NifSubmeshExtractor.cs");
        var decoded = D3D12Source("ReferenceDecodedMesh12.cs");
        var decoder = D3D12Source("ReferenceMeshDecoder12.cs");
        var submeshDecoder = D3D12Source("ReferenceSubmeshDecoder12.cs");
        var disk = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "ReferenceDecodedMeshDiskCache12.cs");
        var cached = D3D12Source("CachedSubmesh12.cs");
        var cache = D3D12Source("ReferenceMeshCache12.cs");

        Assert.Contains("StarfieldMaterialColorRenderState StarfieldMaterialColor", renderable,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialColor = colorPolicy.ResolveRenderState(materialVertexColors, vertexCount)", extractor,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialColor = submesh.StarfieldMaterialColor", extractor,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialColorRenderState StarfieldMaterialColor = default", decoded,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialColor: submesh.StarfieldMaterialColor", submeshDecoder,
            StringComparison.Ordinal);
        Assert.True(SourceContract.CountOccurrences(
            decoder, "StarfieldMaterialColor: sub.StarfieldMaterialColor") >= 2);
        SourceContract.AssertOrder(
            disk,
            "internal const int DecoderVersion = 89;",
            "writer.Write(submesh.ClassicEnvironmentMapIsSphereMap);",
            "WriteStarfieldMaterialColor(writer, submesh.StarfieldMaterialColor);",
            "WriteStarfieldMaterialAlpha(writer, submesh.StarfieldMaterialAlpha);");
        Assert.Contains("ReadStarfieldMaterialColor(reader)", disk, StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialColorRenderState StarfieldMaterialColor", cached,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialColor = sub.StarfieldMaterialColor", cache,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LerpModesAndBranchStayStarfieldOnlyAndKeepTheSharedAbiAt256Bytes()
    {
        Assert.Equal(256, Marshal.SizeOf<InstanceDrawConstants>());

        var cached = D3D12Source("CachedSubmesh12.cs");
        var constants = D3D12Source("ReferenceRendererConstants12.cs");
        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var vertex = SourceContract.ReadShaderSource("reference_instanced.vert.hlsl");
        var fragment = SourceContract.ReadShaderSource("reference.frag.hlsl");
        var shadow = SourceContract.ReadShaderSource("shadow.frag.hlsl");
        var starfieldBranch = SourceContract.Extract(
            fragment,
            "#elif REFERENCE_STARFIELD_DIFFUSE_LIT\n",
            "#else\nfloat4 main(PSInput input)");
        var genericBranch = SourceContract.Extract(
            fragment,
            "#else\nfloat4 main(PSInput input)",
            "#endif");

        Assert.Contains("StarfieldConstantLerpTextureState = -2f", cached,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldVertexLerpTextureState = -3f", cached,
            StringComparison.Ordinal);
        Assert.Contains("EffectFalloff: ResolveEffectFalloffConstants(sub)", renderer,
            StringComparison.Ordinal);
        Assert.Contains("return submesh.StarfieldMaterialColor.LinearTint;", renderer,
            StringComparison.Ordinal);
        Assert.Contains("or Starfield constant-Lerp", constants, StringComparison.Ordinal);
        Assert.Contains("o.vStarfieldMaterialColor = uEffectFalloff;", vertex,
            StringComparison.Ordinal);
        Assert.Contains("input.vTextureState.w == -2.0", starfieldBranch,
            StringComparison.Ordinal);
        Assert.Contains("input.vTextureState.w == -3.0", starfieldBranch,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            starfieldBranch,
            "float3 albedo = input.vTextureState.w == -2.0",
            "lerp(sample.rgb, input.vStarfieldMaterialColor.rgb, input.vStarfieldMaterialColor.a)",
            "input.vTextureState.w == -3.0",
            "lerp(sample.rgb, input.vVertexColor.rgb, input.vVertexColor.a)",
            "float3 lit = albedo * shade;");
        SourceContract.AssertOrder(
            genericBranch,
            "bool starfieldConstantLerp = input.vTextureState.w == -2.0;",
            "bool starfieldVertexLerp = input.vTextureState.w == -3.0;",
            "sample.rgb = lerp(sample.rgb, input.vEffectFalloff.rgb, input.vEffectFalloff.a);",
            "sample.rgb = lerp(sample.rgb, input.vVertexColor.rgb, input.vVertexColor.a);",
            "vertexRgb = 1.0;",
            "fnvActiveAdtBase || starfieldMaterialLerp");
        SourceContract.AssertOrder(
            shadow,
            "bool starfieldMaterialLerp = input.vTextureState.w == -2.0 || input.vTextureState.w == -3.0;",
            "input.vAlphaState.w > 0.5 || starfieldMaterialLerp",
            "saturate(alpha * input.vVertexColor.a)");
    }

    private static string D3D12Source(string fileName) => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", fileName);
}
