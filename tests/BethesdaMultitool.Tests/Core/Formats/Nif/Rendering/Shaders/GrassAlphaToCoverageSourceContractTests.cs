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
        Assert.Contains("AlphaToCoverageEnable = alphaToCoverage", factory, StringComparison.Ordinal);
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
    public void RendererRoutesGrassCutoutsToA2CAtBothBatchAssemblySites()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var compact = RemoveWhitespace(renderer);

        // Main pass gate (decals keep their depth-bias PSO) + shadow-only caster resolve mirror —
        // both must reroute so the shared grass submesh lands in ONE batch (PSO is in the key).
        // Both sites now go through the factory seam, which returns the per-game instanced grass
        // PSOs when the loaded game has a recovered pair and the shared A2C pipelines otherwise;
        // the A2C-vs-plain aliasing moved inside it (see GetGrassCutoutPso).
        Assert.Contains("if(r.IsGrass&&sub.AlphaTest&&!sub.IsDecal)", compact, StringComparison.Ordinal);
        Assert.Equal(
            2,
            SourceContract.CountOccurrences(
                compact,
                "pso=_pipelines.GetGrassCutoutPso(sub.DoubleSided);"));

        var factory = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferencePipelineFactory12.cs");
        Assert.Contains(
            "return doubleSided ? OpaqueDoubleA2CPso : OpaqueBackA2CPso;",
            factory,
            StringComparison.Ordinal);
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
        Assert.Contains("ID3D12PipelineState Pso);", batches, StringComparison.Ordinal);
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
        // The macro-off compile keeps the exact legacy discard line (default path byte-identical).
        Assert.Contains(
            "#else\n    if (!PassAlphaTest(testAlpha, input.vAlphaState.x, input.vAlphaState.y)) discard;\n#endif",
            shader.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        // Coverage is written through SV_Target.a only on the A2C draw.
        Assert.Contains("outAlpha = a2cCoverage;", shader, StringComparison.Ordinal);
    }

    private static string RemoveWhitespace(string source)
    {
        return new string(source.Where(static character => !char.IsWhiteSpace(character)).ToArray());
    }
}