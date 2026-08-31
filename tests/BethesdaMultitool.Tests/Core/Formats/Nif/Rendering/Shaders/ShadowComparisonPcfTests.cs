using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

public sealed class ShadowComparisonPcfTests
{
    [Fact]
    public void RuntimeOptInIsExactAndPixelShaderOnly()
    {
        var enabled = ShadowComparisonPcf12.Apply("ps_5_1", [], "1");
        var macro = Assert.Single(enabled);
        Assert.Equal(ShadowComparisonPcf12.ShaderMacroName, macro.Name);
        Assert.Equal("1", macro.Definition);

        Assert.Empty(ShadowComparisonPcf12.Apply("vs_5_1", [], "1"));
        Assert.Empty(ShadowComparisonPcf12.Apply("cs_5_1", [], "1"));
        Assert.Empty(ShadowComparisonPcf12.Apply("ps_5_1", [], null));
        Assert.Empty(ShadowComparisonPcf12.Apply("ps_5_1", [], "0"));
        Assert.Empty(ShadowComparisonPcf12.Apply("ps_5_1", [], "true"));
        Assert.Empty(ShadowComparisonPcf12.Apply("ps_5_1", [], " 1"));

        var explicitOff = new[]
        {
            new ShaderMacro(ShadowComparisonPcf12.ShaderMacroName, "0")
        };
        Assert.Same(explicitOff, ShadowComparisonPcf12.Apply("ps_5_1", explicitOff, "1"));
    }

    [Fact]
    public void FourLinearSamplesReproduceTheLegacySeparableKernelWeights()
    {
        // Legacy's three bilinear taps produce four 1-D texel weights [1-f, 1, 1, f].
        // The optimized shader groups those into two linear taps. Prove both axes over a dense
        // fraction grid; their Cartesian product is therefore the same 4x4 PCF kernel.
        for (var step = 0; step <= 10_000; step++)
        {
            var f = step / 10_000.0;
            var lowWeight = 2.0 - f;
            var highWeight = 1.0 + f;
            var lowFraction = 1.0 / lowWeight;
            var highFraction = f / highWeight;

            var optimized = new[]
            {
                lowWeight * (1.0 - lowFraction),
                lowWeight * lowFraction,
                highWeight * (1.0 - highFraction),
                highWeight * highFraction
            };
            var legacy = new[] { 1.0 - f, 1.0, 1.0, f };

            for (var texel = 0; texel < legacy.Length; texel++)
            {
                Assert.InRange(Math.Abs(optimized[texel] - legacy[texel]), 0.0, 1e-12);
            }
            Assert.InRange(Math.Abs(optimized.Sum() - 3.0), 0.0, 1e-12);
        }
    }

    [Theory]
    [InlineData(0.25f, 0.25f)]
    [InlineData(0.25f, 0.5f)]
    [InlineData(0.25f, 0.75f)]
    [InlineData(0.5f, 0.25f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.5f, 0.75f)]
    [InlineData(0.75f, 0.25f)]
    [InlineData(0.75f, 0.5f)]
    [InlineData(0.75f, 0.75f)]
    public void StrictGreaterComparisonMatchesLegacyReversedZVisibility(
        float reference, float stored)
    {
        // HLSL step(reference, stored) is one at equality, so legacy equality is shadowed.
        var legacyVisible = stored < reference ? 1.0f : 0.0f;
        var comparisonSamplerVisible = reference > stored ? 1.0f : 0.0f;
        Assert.Equal(legacyVisible, comparisonSamplerVisible);
    }

    [Fact]
    public void ShaderKeepsLegacyDefaultAndUsesFourLevelZeroComparisonSamplesOnlyWhenDefined()
    {
        var shader = SourceContract.ReadShaderSource("shadow_sampling.hlsli");
        SourceContract.AssertOrder(
            shader,
            "#if SHADOW_COMPARISON_PCF",
            "SamplerComparisonState sShadowComparison : register(s7);",
            "#if SHADOW_COMPARISON_PCF",
            "SampleCmpLevelZero",
            "#else",
            ".GatherRed(",
            "#endif");

        Assert.Equal(4, SourceContract.CountOccurrences(shader, ".SampleCmpLevelZero("));
        Assert.Equal(1, SourceContract.CountOccurrences(shader, ".GatherRed("));
        Assert.Contains("float2 lowWeight = 2.0 - f;", shader, StringComparison.Ordinal);
        Assert.Contains("float2 highWeight = 1.0 + f;", shader, StringComparison.Ordinal);
        Assert.Contains("visibility = dot(filtered, weights) / 9.0;", shader, StringComparison.Ordinal);
        Assert.Contains("visibility = lit / 9.0;", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void RootSignatureProvidesASeparateStrictReversedZComparisonSampler()
    {
        var root = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuRootSignature12.cs");
        var sampler = SourceContract.Extract(root, "// s7: opt-in comparison-linear PCF sampler.",
            "// s4-s6: material-addressing permutations");

        Assert.Contains("new StaticSamplerDescription(\n                7,", sampler, StringComparison.Ordinal);
        Assert.Contains("Filter.ComparisonMinMagLinearMipPoint", sampler, StringComparison.Ordinal);
        Assert.Contains("ComparisonFunction.Greater", sampler, StringComparison.Ordinal);
        Assert.Contains("0f,\n                0f,\n                ShaderVisibility.Pixel", sampler,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryShadowReceiverUsesTheSharedImplementation()
    {
        foreach (var shaderName in new[]
                 {
                     "reference.frag.hlsl",
                     "terrain_textured.frag.hlsl",
                     "reference_grass_oblivion.frag.hlsl",
                     "reference_grass_fnv.frag.hlsl",
                     "water_fo4.frag.hlsl"
                 })
        {
            Assert.Contains("#include \"shadow_sampling.hlsli\"",
                SourceContract.ReadShaderSource(shaderName), StringComparison.Ordinal);
        }

        var water = SourceContract.ReadShaderSource("water_fo4.frag.hlsl");
        SourceContract.AssertOrder(water,
            "#define SHADOW_ATLAS gWaterTextures",
            "#define SHADOW_SAMPLER gWaterShadowSampler",
            "#include \"shadow_sampling.hlsli\"",
            "#undef SHADOW_SAMPLER",
            "#undef SHADOW_ATLAS",
            "float shadow = lit ? ShadowFactor(relativeWorldPos) : 1.0;");
        Assert.DoesNotContain("TryModernCascadeShadow", water, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionCompilerAppliesTheOptInBeforeBuildingItsCacheKey()
    {
        var compiler = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuShaderCompiler12.cs");
        SourceContract.AssertOrder(compiler,
            "var effectiveMacros = ShadowComparisonPcf12.ApplyRuntimeOptIn(profile, macros);",
            "BuildCacheKey(fileName, entryPoint, profile, effectiveMacros);",
            "entryPoint, profile, effectiveMacros);");
    }

    [Fact]
    public void RuntimeProofIdentifiesEffectiveAndControlBytecodeAndDeduplicatesEachPermutation()
    {
        var sessionId = Guid.NewGuid().ToString("N");
        var bytecode = new byte[] { 0x44, 0x58, 0x42, 0x43 };
        var enabledMacros = new[]
        {
            new ShaderMacro("ALPHA_TO_COVERAGE", "1"),
            new ShaderMacro(ShadowComparisonPcf12.ShaderMacroName, "1")
        };

        Assert.True(ShadowComparisonPcf12.TryBuildTraceProof(
            sessionId,
            "reference.frag.hlsl",
            "main",
            "ps_5_1",
            enabledMacros,
            "enabled-cache-key",
            bytecode,
            false,
            out var enabled));
        Assert.NotNull(enabled);
        Assert.Equal("reference.frag.hlsl", enabled["shaderFile"]);
        Assert.Equal("main", enabled["entryPoint"]);
        Assert.Equal("ps_5_1", enabled["profile"]);
        Assert.Equal("ALPHA_TO_COVERAGE=1,SHADOW_COMPARISON_PCF=1", enabled["macros"]);
        Assert.Equal(true, enabled["shadowComparisonPcfEffective"]);
        Assert.Equal("1", enabled["shadowComparisonPcfMacro"]);
        Assert.Equal("15D18EA0CC5BD2665B7C4EB39805771377E3CB87831EEA48EDEDA448B2815337",
            enabled["bytecodeSha256"]);
        Assert.Equal(bytecode.Length, enabled["bytecodeBytes"]);
        Assert.Equal(false, enabled["cacheHit"]);

        Assert.False(ShadowComparisonPcf12.TryBuildTraceProof(
            sessionId,
            "reference.frag.hlsl",
            "main",
            "ps_5_1",
            enabledMacros,
            "enabled-cache-key",
            bytecode,
            true,
            out var duplicate));
        Assert.Null(duplicate);

        Assert.True(ShadowComparisonPcf12.TryBuildTraceProof(
            sessionId,
            "reference.frag.hlsl",
            "main",
            "ps_5_1",
            [],
            "control-cache-key",
            bytecode,
            true,
            out var control));
        Assert.NotNull(control);
        Assert.Equal(false, control["shadowComparisonPcfEffective"]);
        Assert.Null(control["shadowComparisonPcfMacro"]);
        Assert.Equal(true, control["cacheHit"]);

        Assert.False(ShadowComparisonPcf12.TryBuildTraceProof(
            sessionId,
            "reference.vert.hlsl",
            "main",
            "vs_5_1",
            [],
            "vertex-cache-key",
            bytecode,
            false,
            out _));
        Assert.False(ShadowComparisonPcf12.TryBuildTraceProof(
            sessionId,
            "sky.frag.hlsl",
            "main",
            "ps_5_1",
            [],
            "non-receiver-cache-key",
            bytecode,
            false,
            out _));
    }

    [Fact]
    public void ProductionCompilerTracesOnlySuccessfullyReturnedBytecode()
    {
        var compiler = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuShaderCompiler12.cs");

        SourceContract.AssertOrder(compiler,
            "if (BytecodeCache.TryGetValue(key, out var cached))",
            "ShadowComparisonPcf12.TraceSuccessfulShader(",
            "cacheHit: true);",
            "var bytecode = CompileSource(",
            "var selectedBytecode = BytecodeCache.GetOrAdd(key, bytecode);",
            "ShadowComparisonPcf12.TraceSuccessfulShader(",
            "return selectedBytecode;");
        Assert.Equal(2,
            SourceContract.CountOccurrences(compiler, "ShadowComparisonPcf12.TraceSuccessfulShader("));
    }
}
