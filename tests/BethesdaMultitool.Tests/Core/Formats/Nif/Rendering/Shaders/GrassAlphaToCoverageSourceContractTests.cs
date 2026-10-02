using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Source contracts for the grass-cutout alpha-to-coverage route: the factory owns the paired
///     PSO/PS-variant lifecycle (including the MSAA-off aliasing that keeps the renderer branch-free),
///     the renderer routes grass at both batch-assembly sites so batches merge, and the shader keeps
///     the legacy discard byte-identical when the macro is absent.
/// </summary>
public sealed class GrassAlphaToCoverageSourceContractTests
{
    [Fact]
    public void FactoryBuildsPairedA2CVariantsAndAliasesThemWhenSceneIsSingleSampled()
    {
        var factory = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferencePipelineFactory12.cs");

        // The blend state and the coverage-writing PS must never desynchronize: both come from the
        // same PSO, gated on one availability flag.
        var recipe = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferencePipelineRecipe12.cs");
        Assert.Contains("AlphaToCoverageEnable = state.AlphaToCoverage", recipe, StringComparison.Ordinal);
        Assert.Contains(
            "new ShaderMacro(\"ALPHA_TO_COVERAGE\", \"1\")", factory, StringComparison.Ordinal);
        Assert.Contains(
            "AlphaToCoverageAvailable = _gpu.SceneSampleCount > 1", factory, StringComparison.Ordinal);
        // MSAA off: A2C is a hardware no-op AND the gradient PS would render solid cards — the
        // properties alias the plain PSOs instead so the routing sites need no fallback branch.
        Assert.Contains("OpaqueBackA2CPso = OpaqueBackPso;", factory, StringComparison.Ordinal);
        Assert.Contains("OpaqueDoubleA2CPso = OpaqueDoublePso;", factory, StringComparison.Ordinal);
        // Dispose only the distinct variants (aliases would double-dispose the plain PSOs).
        Assert.Contains("if (AlphaToCoverageAvailable)", factory, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchKeyCarriesThePipelineState()
    {
        var batches = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "OpaqueBatchRegistry12.cs");

        // Grass rerouting means the PSO is no longer derivable from the submesh alone; without it
        // in the key, a grass and a non-grass placement of one submesh silently alias one batch.
        Assert.Contains(
            "submesh, usesGrassDistanceEnvelope, effectiveTallGrassWind, effectiveWaveMultiplier, pso",
            batches,
            StringComparison.Ordinal);
        // The registry's batch record gained members after Pso, so the constructor line no longer
        // ends at it; the property is the stable place the key exposes it.
        Assert.Contains("public ID3D12PipelineState Pso { get; } = pso;", batches, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderVariantSharpensCoverageAndPreservesTheLegacyDiscardWhenUndefined()
    {
        var shader = SourceContract.ReadShaderSource("reference.frag.hlsl");

        Assert.Contains("#if ALPHA_TO_COVERAGE", shader, StringComparison.Ordinal);
        // GREATER/GEQUAL only — every other comparison function keeps the binary discard.
        Assert.Contains("a2cFn == 4 || a2cFn == 6", shader, StringComparison.Ordinal);
        // fwidth-sharpened gradient centered on the authored threshold, with the load-bearing
        // Castaño floor shared with the SPT leaf path.
        Assert.Contains("max(fwidth(a2cAlpha), 1e-4)", shader, StringComparison.Ordinal);
        Assert.Contains("if (a2cAlpha > 0.25)", shader, StringComparison.Ordinal);
        // The macro-off branch must retain the legacy discard; indentation is immaterial.
        SourceContract.AssertContainsIgnoringWhitespace(
            "#else\n    if (!PassAlphaTest(testAlpha, input.vAlphaState.x, input.vAlphaState.y)) discard;\n#endif",
            shader);
        // Coverage is written through SV_Target.a only on the A2C draw.
        Assert.Contains("outAlpha = a2cCoverage;", shader, StringComparison.Ordinal);
    }

}
