using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

public sealed class StarfieldVertexLerpSpriteSourceContractTests
{
    [Fact]
    public void GpuSpriteCarriesDedicatedModeAndKeepsLerpAlphaOutOfCoverage()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuSpriteRenderer12.cs");
        var shader = SourceContract.ReadShaderSource("skin.frag.hlsl");
        var uploader = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu",
            "GpuMeshUploader.cs");

        Assert.Contains("sub.StarfieldMaterialColor.IsVertexLerp) flags |= 4096", renderer,
            StringComparison.Ordinal);
        Assert.Contains("var color = sub.StarfieldMaterialColor.IsVertexLerp", uploader,
            StringComparison.Ordinal);
        Assert.Contains("R: sub.VertexColors[ci]", uploader, StringComparison.Ordinal);
        Assert.Contains("static const uint IS_STARFIELD_VERTEX_LERP = 4096u", shader,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            shader,
            "(flags & HAS_VCOL) != 0u && (flags & IS_STARFIELD_VERTEX_LERP) == 0u",
            "if ((flags & IS_STARFIELD_VERTEX_LERP) != 0u)",
            "texColor.rgb = lerp(texColor.rgb, input.vVertexColor.rgb, input.vVertexColor.a);",
            "else if ((flags & HAS_TINT) != 0u)",
            "else if ((flags & HAS_VCOL) != 0u)");
    }

    [Fact]
    public void CpuSpriteCarriesRawLerpAlphaSeparatelyFromOpacity()
    {
        var triangle = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "TriangleData.cs");
        var collector = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Rasterization",
            "NifSpriteRenderer.cs");
        var rasterizer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Rasterization",
            "NifScanlineRasterizer.cs");

        Assert.Contains("public bool IsStarfieldVertexLerp", triangle, StringComparison.Ordinal);
        Assert.Contains("public float StarfieldVertexLerpA0, StarfieldVertexLerpA1, StarfieldVertexLerpA2", triangle,
            StringComparison.Ordinal);
        Assert.Contains("IsStarfieldVertexLerp = submesh.StarfieldMaterialColor.IsVertexLerp", collector,
            StringComparison.Ordinal);
        Assert.Contains("tri.StarfieldVertexLerpA0 = vcol[ci0 + 3]", collector, StringComparison.Ordinal);
        Assert.Contains("tri.HasVertexColors && !tri.IsStarfieldVertexLerp", rasterizer,
            StringComparison.Ordinal);
        Assert.Contains("var vertexAlpha = tri.HasVertexColors && !tri.IsStarfieldVertexLerp", rasterizer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SpriteOpacityGapUsesEstablishedCe2Slot2RedContract()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuSpriteRenderer12.cs");
        var shader = SourceContract.ReadShaderSource("skin.frag.hlsl");
        var referenceShader = SourceContract.ReadShaderSource("reference.frag.hlsl");
        var resolver = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Textures",
            "MaterialTexturePathResolver.cs");
        var rasterizer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Rasterization",
            "NifScanlineRasterizer.cs");

        Assert.Contains("BuildStarfieldOpacityMapRequest(starfieldMaterialPath)", renderer,
            StringComparison.Ordinal);
        Assert.Contains("BuildStarfieldOpacityMapRequest", resolver, StringComparison.Ordinal);
        Assert.Contains("HasStarfieldOpacityMap(input.vTextureState.z)", referenceShader,
            StringComparison.Ordinal);

        SourceContract.AssertOrder(
            shader,
            "texColor.rgb = lerp(texColor.rgb, input.vVertexColor.rgb, input.vVertexColor.a)",
            "texColor.rgb *= uEffectTint.rgb");
        Assert.Contains("tri.StarfieldOpacityMap != null", rasterizer, StringComparison.Ordinal);
        Assert.Contains("const float defaultGrey = 199f", rasterizer, StringComparison.Ordinal);
        Assert.Contains(".Sample(sDiffuse, input.vTexCoord).r", referenceShader,
            StringComparison.Ordinal);
        Assert.Contains("texColor.a = tStarfieldOpacity.Sample(sStarfieldOpacity, input.vTexCoord).r", shader,
            StringComparison.Ordinal);
        Assert.Contains("var (opacity, _, _, _) = NifTextureSampler.SampleTexture", rasterizer,
            StringComparison.Ordinal);
        Assert.Contains("a = opacity", rasterizer, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalOnlySpritesRemainDrawableAndReportTextureParityAcrossCpuAndGpu()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuSpriteRenderer12.cs");
        var rasterizer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Rasterization",
            "NifScanlineRasterizer.cs");

        Assert.Contains("normalTexture == null", renderer, StringComparison.Ordinal);
        Assert.Contains("bool HasNormalTexture", renderer, StringComparison.Ordinal);
        Assert.Contains(
            "item.HasDiffuseTexture || item.HasNormalTexture || item.StarfieldOpacityPath is not null",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "tex != null || normalMap != null || tri.StarfieldOpacityMap != null",
            rasterizer,
            StringComparison.Ordinal);
    }
}
