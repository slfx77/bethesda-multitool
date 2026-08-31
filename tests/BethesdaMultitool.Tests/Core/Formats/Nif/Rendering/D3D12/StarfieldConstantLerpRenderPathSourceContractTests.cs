using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRendererConstants12;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Pins the bounded Starfield constant-Lerp operation across extraction, persistence, GPU
///     constants, and the specialized shader without widening the shared opaque-draw ABI.
/// </summary>
public sealed class StarfieldConstantLerpRenderPathSourceContractTests
{
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
        var disk = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "ReferenceDecodedMeshDiskCache12.cs");
        var cached = D3D12Source("CachedSubmesh12.cs");
        var cache = D3D12Source("ReferenceMeshCache12.cs");

        Assert.Contains("StarfieldMaterialColorRenderState StarfieldMaterialColor", renderable,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialColor = colorPolicy.ResolveRenderState()", extractor,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialColor = submesh.StarfieldMaterialColor", extractor,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialColorRenderState StarfieldMaterialColor = default", decoded,
            StringComparison.Ordinal);
        Assert.True(SourceContract.CountOccurrences(
            decoder, "StarfieldMaterialColor: sub.StarfieldMaterialColor") >= 3);
        SourceContract.AssertOrder(
            disk,
            "internal const int DecoderVersion = 86;",
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
    public void ConstantLaneAndBranchStayStarfieldOnlyAndKeepTheSharedAbiAt256Bytes()
    {
        Assert.Equal(256, Marshal.SizeOf<InstanceDrawConstants>());

        var cached = D3D12Source("CachedSubmesh12.cs");
        var constants = D3D12Source("ReferenceRendererConstants12.cs");
        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var vertex = SourceContract.ReadShaderSource("reference_instanced.vert.hlsl");
        var fragment = SourceContract.ReadShaderSource("reference.frag.hlsl");
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
        Assert.Contains("EffectFalloff: ResolveEffectFalloffConstants(sub)", renderer,
            StringComparison.Ordinal);
        Assert.Contains("return submesh.StarfieldMaterialColor.LinearTint;", renderer,
            StringComparison.Ordinal);
        Assert.Contains("or Starfield constant-Lerp", constants, StringComparison.Ordinal);
        Assert.Contains("o.vStarfieldMaterialColor = uEffectFalloff;", vertex,
            StringComparison.Ordinal);
        Assert.Contains("input.vTextureState.w == -2.0", starfieldBranch,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            starfieldBranch,
            "float3 albedo = input.vTextureState.w == -2.0",
            "lerp(sample.rgb, input.vStarfieldMaterialColor.rgb, input.vStarfieldMaterialColor.a)",
            "float3 lit = albedo * shade;");
        Assert.DoesNotContain("vStarfieldMaterialColor", genericBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("-2.0", genericBranch, StringComparison.Ordinal);
    }

    private static string D3D12Source(string fileName) => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", fileName);
}
