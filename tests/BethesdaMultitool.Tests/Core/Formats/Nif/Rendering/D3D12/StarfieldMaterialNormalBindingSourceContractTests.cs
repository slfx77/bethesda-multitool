using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Pins the Windows-only upload wiring. Pure policy and cache-key behavior are exercised by
///     platform-neutral tests; constructing the live cache would require a D3D12 device.
/// </summary>
public sealed class StarfieldMaterialNormalBindingSourceContractTests
{
    [Fact]
    public void ReferenceUploadBindsDerivedNormalAndEnablesBothBumpFlags()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshCache12.cs");

        Assert.Contains("StarfieldMaterialNormalPolicy.Resolve(", source, StringComparison.Ordinal);
        Assert.Contains("GetOrUpload(normalBinding.TexturePath!, isNormalMap: true)", source,
            StringComparison.Ordinal);
        Assert.Contains("RenderState = BuildRenderState(sub, normalBinding.HasBump)", source,
            StringComparison.Ordinal);
        Assert.Contains("HasBump = normalBinding.HasBump", source, StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialPath = starfieldMaterialPath", source,
            StringComparison.Ordinal);
        Assert.Contains("HasDerivedStarfieldNormal = normalBinding.IsDerived", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CachedSubmeshAndRendererCarryExactStarfieldMaterialFacts()
    {
        var cachedSubmesh = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "CachedSubmesh12.cs");
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");

        Assert.Contains("public required string? StarfieldMaterialPath { get; init; }", cachedSubmesh,
            StringComparison.Ordinal);
        Assert.Contains("public required bool HasDerivedStarfieldNormal { get; init; }", cachedSubmesh,
            StringComparison.Ordinal);
        Assert.Contains("Game: _renderCache?.Game ?? Core.Games.BethesdaGame.Unknown", renderer,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldMaterialPath: sub.StarfieldMaterialPath", renderer,
            StringComparison.Ordinal);
        Assert.Contains("HasDerivedStarfieldNormal: sub.HasDerivedStarfieldNormal", renderer,
            StringComparison.Ordinal);
        Assert.Contains("SpecularExponent: sub.Specular.W", renderer, StringComparison.Ordinal);
        Assert.Contains("TryResolveStarfieldDiffuseLitVariant(variant", renderer,
            StringComparison.Ordinal);
        Assert.Contains("_pipelines.TryGetStarfieldDiffuseLitPso(alphaGreater, doubleSided", renderer,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            renderer,
            "_pipelines.TryGetStarfieldDiffuseLitPso(alphaGreater, doubleSided",
            "usesModernStandardShader = true;",
            "return starfieldPso!;");
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.StarfieldSingleSidedOpaque => (false, false)",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedOpaque => (false, true)",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.StarfieldSingleSidedGreaterCutout => (true, false)",
            renderer,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedGreaterCutout => (true, true)",
            renderer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NullEnvironmentMapIsTerminalForStaticPacketEligibility()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");

        Assert.Contains("var environmentTerminal = sub.EnvMap is null ||", renderer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SpecializedOpaqueTelemetryReflectsEitherMaterialFamily()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");

        Assert.Contains(
            "BethesdaGame.Fallout4 or BethesdaGame.Fallout76 =>\n" +
            "                _pipelines.ModernStandardOpaqueAvailable,\n" +
            "            BethesdaGame.Starfield => _pipelines.StarfieldDiffuseLitOpaqueAvailable",
            renderer,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolverSelectsNormalSlotWithoutAliasingDiffuseCacheIdentity()
    {
        var cache = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuTextureCache12.cs");
        var resolver = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "NifGpuTextureResolver.cs");

        Assert.Contains("NormalizeCacheKey(path, isNormalMap)", cache, StringComparison.Ordinal);
        Assert.Contains("BuildStarfieldNormalMapRequest(normalizedPath)", cache, StringComparison.Ordinal);
        Assert.Contains("!_resolver.IsUnauthoredStarfieldNormalMap(cacheKey)", cache,
            StringComparison.Ordinal);
        Assert.Contains("normalMap: starfieldNormalMap", resolver, StringComparison.Ordinal);
        Assert.Contains("var keyText = string.Concat(_sourceSetIdentity, \"|\", path);", resolver,
            StringComparison.Ordinal);
    }
}
