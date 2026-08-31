using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Compiler-free contracts for the dedicated Starfield path. These deliberately pin both the
///     new source-backed behavior and its approximation labels, so future work cannot quietly turn
///     an inferred slot/equation into an engine-parity claim.
/// </summary>
public sealed class StarfieldWaterApproximationSourceContractTests
{
    [Fact]
    public void RendererOwnsDedicatedPsoPairAndExplicitApproximationTelemetry()
    {
        var renderer = ReadRenderer();

        Assert.Contains("_psoStarfield = gpu.Device.CreateGraphicsPipelineState", renderer,
            StringComparison.Ordinal);
        Assert.Contains("_psoStarfieldDepthSample = gpu.Device.CreateGraphicsPipelineState", renderer,
            StringComparison.Ordinal);
        Assert.Contains("depthSample ? _psoStarfieldDepthSample : _psoStarfield", renderer,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldWaterApproximation.TelemetryName", renderer,
            StringComparison.Ordinal);
        Assert.Contains("global texture-slot assignment inferred", renderer,
            StringComparison.Ordinal);
        Assert.Contains("_psoStarfield.Dispose();", renderer, StringComparison.Ordinal);
        Assert.Contains("_psoStarfieldDepthSample.Dispose();", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderConsumesExactNormalMotionDepthAndRoughnessInputsWithoutInventingOptics()
    {
        var shader = SourceContract.ReadShaderSource("water_starfield.frag.hlsl");

        Assert.Contains("Starfield WATR source-backed water approximation", shader,
            StringComparison.Ordinal);
        Assert.Contains("This is NOT a recovered Creation Engine 2 Water shader", shader,
            StringComparison.Ordinal);
        Assert.Contains("uStarfieldLayer1", shader, StringComparison.Ordinal);
        Assert.Contains("uStarfieldLayer2", shader, StringComparison.Ordinal);
        Assert.Contains("uStarfieldLayer3", shader, StringComparison.Ordinal);
        Assert.Contains("uStarfieldLinearVelocity.xy", shader, StringComparison.Ordinal);
        Assert.Contains("uStarfieldDepthFlow.x", shader, StringComparison.Ordinal);
        Assert.Contains("uStarfieldSurface.x", shader, StringComparison.Ordinal);
        Assert.Contains("uStarfieldSurface.y", shader, StringComparison.Ordinal);
        Assert.Contains("uStarfieldLayerFalloffsFlags.xyz", shader, StringComparison.Ordinal);
        Assert.Contains("uNormalIndices.z", shader, StringComparison.Ordinal);
        Assert.Contains("float alpha = saturate(asfloat(uNoiseParams.w));", shader,
            StringComparison.Ordinal);

        // These exact source values are uploaded for future recovered equations, but the initial
        // surface shader must not pretend they already establish colour/transmission/refraction.
        Assert.DoesNotContain("uStarfieldAbsorption", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("uStarfieldConcentrations", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("uStarfieldUnderwaterColor", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderInventoryCoversPlainAndHardwareOcclusionPermutations()
    {
        var permutations = ShaderPermutations.Water
            .Where(permutation => permutation.File == "water_starfield.frag.hlsl")
            .ToArray();

        Assert.Equal(2, permutations.Length);
        Assert.Single(permutations, permutation => permutation.Macros.All(
            macro => macro.Name != "WATER_HARDWARE_OCCLUSION"));
        Assert.Single(permutations, permutation => permutation.Macros.Any(
            macro => macro.Name == "WATER_HARDWARE_OCCLUSION" && macro.Definition == "1"));
    }

    [Fact]
    public void WorldHostRetainsTypedWatrAndResolvesTheThreeLabelledGlobalAssets()
    {
        var cells = SourceContract.ReadAppSource("WorldView3DControl.Cells.cs");

        Assert.Contains(
            "StarfieldWaterApproximation.FromWaterRecord(initialWaterSelection.Water)",
            cells,
            StringComparison.Ordinal);
        Assert.Contains(
            "StarfieldWaterApproximation.InferredGlobalTexturePaths",
            cells,
            StringComparison.Ordinal);
        Assert.Contains("ResolveNormalMapBindlessIndex(path)", cells, StringComparison.Ordinal);
        Assert.Contains("_water?.SetStarfieldApproximation(starfieldApproximation);", cells,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WorldHostRefreshesRetailStarfieldCellXcwtInsteadOfFreezingWorldspaceNam2()
    {
        var cells = SourceContract.ReadAppSource("WorldView3DControl.Cells.cs");
        var refreshStart = cells.IndexOf(
            "private void RefreshWaterAppearanceForCurrentCell", StringComparison.Ordinal);
        var refreshEnd = cells.IndexOf(
            "// ── Interior cell browser", refreshStart, StringComparison.Ordinal);

        Assert.True(refreshStart >= 0 && refreshEnd > refreshStart);
        var refresh = cells[refreshStart..refreshEnd];
        Assert.Contains("or BethesdaMultitool.Core.Games.BethesdaGame.Starfield", refresh,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldWaterApproximation.FromWaterRecord(selection.Water)", refresh,
            StringComparison.Ordinal);
        Assert.Contains("ResolveWaterNormalIndices(appearance, starfieldApproximation)", refresh,
            StringComparison.Ordinal);
        Assert.Contains("_water.SetStarfieldApproximation(starfieldApproximation);", refresh,
            StringComparison.Ordinal);
    }

    private static string ReadRenderer() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
        "WaterRenderer12.cs");
}
