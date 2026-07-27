using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class RenderingShaderCompilationTests
{
    [Fact]
    public void FnvTallGrassReferenceRoutesCompileForNormalInstancedAndShadowPasses()
    {
        Compile("reference.vert.hlsl", "main", "vs_5_1", []);
        Compile("reference_instanced.vert.hlsl", "main", "vs_5_1", []);
        Compile(
            "reference_instanced.vert.hlsl",
            "main",
            "vs_5_1",
            [new ShaderMacro("SHADOW_CARD_LIGHT_FACING", "1")]);
        Compile("reference.frag.hlsl", "main", "ps_5_1", []);
        // Grass-cutout alpha-to-coverage PS variant (paired with the A2C opaque PSOs, MSAA only).
        Compile("reference.frag.hlsl", "main", "ps_5_1", [new ShaderMacro("ALPHA_TO_COVERAGE", "1")]);
        Compile("shadow.frag.hlsl", "main", "ps_5_1", []);
    }

    [Fact]
    public void FnvActiveAdtBaseSls2000SharedReferenceRoutesCompile()
    {
        // Runtime material bits choose the bounded active SLS2000 equation inside one PS, while both
        // direct/blended and instanced VSes supply its normalized tangent-space light. The classic
        // parallax route draws through these same three programs, so it is covered here too rather
        // than by a second test with an identical body.
        Compile("reference.vert.hlsl", "main", "vs_5_1", []);
        Compile("reference_instanced.vert.hlsl", "main", "vs_5_1", []);
        Compile("reference.frag.hlsl", "main", "ps_5_1", []);
    }

    /// <summary>
    ///     Compiles EVERY permutation the renderers build, by iterating the same production table the
    ///     factories are documented against — so test coverage cannot drift from what actually ships.
    ///     <para>
    ///         This replaces two hand-maintained lists. One was named
    ///         <c>EveryRemainingEmbeddedRenderingEntryPointCompiles</c> but was in fact 26 literal
    ///         tuples, so a newly added shader had zero coverage until someone remembered it. The
    ///         other carried a phantom <c>FO76_WATER</c> macro that appears in no <c>.hlsl</c> file —
    ///         a byte-identical duplicate of the FO4 permutation that implied FO76 was validated when
    ///         nothing about it was.
    ///     </para>
    ///     <para>
    ///         <see cref="ShaderInventoryTests.EveryEntryShaderIsReachedByAPermutation" /> keeps the
    ///         table honest (and runs UNGATED, so it holds even in CI).
    ///     </para>
    /// </summary>
    [Fact]
    public void EveryShaderPermutationCompiles()
    {
        var failures = new List<string>();
        foreach (var permutation in ShaderPermutations.All)
        {
            try
            {
                Compile(permutation.File, permutation.EntryPoint, permutation.Profile, permutation.Macros);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                // Collect rather than fail fast: one broken permutation should not hide the others.
                failures.Add($"{permutation.File} [{permutation.EntryPoint}/{permutation.Profile}] " +
                             $"({permutation.Purpose}): {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void FnvClassicBloomUsesAxisScaleInsteadOfDiagonalTapCoordinates()
    {
        var source = ReadEmbeddedShader("bloom.frag.hlsl");

        Assert.Equal(2, source.Split("float2 offset = tapIndex * uBloom1.xy;").Length - 1);
        Assert.DoesNotContain("float2(tapIndex, tapIndex)", source, StringComparison.Ordinal);
        Assert.Contains("float4 mainBlur(PSInput input)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SkyGeometryTextureLayersHonorAnyAuthoredBlendWeightChannel()
    {
        var source = ReadEmbeddedShader("sky_geo.frag.hlsl");

        // Oblivion Clouds.nif uses R for one cap and B for the other. A hard-coded vColor.r
        // multiplier makes most of the B-selected cap black even though its vertex alpha is visible.
        Assert.Contains(
            "max(input.vColor.r, max(input.vColor.g, input.vColor.b))",
            source,
            StringComparison.Ordinal);
        Assert.Equal(2, source.Split("* vertexWeight").Length - 1);
    }

    [Fact]
    public void FnvRtFreeWaterKeepsRecoveredWater003ReflectionAndBodyLightTerms()
    {
        var source = ReadEmbeddedShader("water.frag.hlsl");

        Assert.Contains(
            "float3 fnvBodyLightDir = normalize(float3(sunDir.x, 4.0 * sunDir.y, sunDir.z));",
            source,
            StringComparison.Ordinal);
        Assert.Contains("float3 refl = uReflection.rgb;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "float3 refl = uReflection.rgb * reflectivity;",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WaterDepthShaderMaxResolvesReversedZMsaaBeforeLinearization()
    {
        var source = ReadEmbeddedShader("water.frag.hlsl");
        var helperStart = source.IndexOf("float LoadSceneDepth(", StringComparison.Ordinal);
        var helperEnd = source.IndexOf("#if FO4_WATER_ARCHITECTURAL", helperStart, StringComparison.Ordinal);

        Assert.True(helperStart >= 0);
        Assert.True(helperEnd > helperStart);
        var helper = source[helperStart..helperEnd];
        Assert.Contains("if (suppliedSampleCount <= 1u)", helper, StringComparison.Ordinal);
        Assert.Contains(
            "gWaterTextures[NonUniformResourceIndex(depthIndex)].Load(int3(pixel, 0)).r",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "for (uint sampleIndex = 0; sampleIndex < descriptorSampleCount; sampleIndex++)",
            helper,
            StringComparison.Ordinal);
        Assert.Contains("nearestNdc = max(nearestNdc,", helper, StringComparison.Ordinal);

        Assert.Contains(
            "Texture2DMS<float> gWaterDepthTexturesMsaa[] : register(t0, space3);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("uint depthSampleCount = max((uint)uRenderOrigin.w, 1u);",
            source,
            StringComparison.Ordinal);
        var resolveCall = source.IndexOf(
            "float sceneNdc = LoadSceneDepth(depthIndex, (int2)input.Position.xy, depthSampleCount);",
            StringComparison.Ordinal);
        var linearizeCall = source.IndexOf(
            "float sceneDist = LinearizeDepth(sceneNdc, near, far);",
            StringComparison.Ordinal);
        Assert.True(resolveCall >= 0);
        Assert.True(linearizeCall > resolveCall);
    }

    [Theory]
    [InlineData("reference.frag.hlsl")]
    [InlineData("skin.frag.hlsl")]
    public void BethesdaDirectXNormalMapsKeepAuthoredGreenInDirectXShaders(string shaderName)
    {
        var source = ReadEmbeddedShader(shaderName);

        // Recovered FNV SLS1009: sample.rgb * 2 - 1, then dot with the authored tangent-space
        // light vector. Bethesda DDS normals are converted to glTF by flipping green exactly once
        // in NpcGlbNormalMapPacker, so doing it in the DirectX renderer would invert the relief.
        Assert.DoesNotContain("mapN.y = -mapN.y", source, StringComparison.Ordinal);
        Assert.Contains("* 2.0 - 1.0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FnvNoLightingDoesNotInheritLighting30HdrEmissiveMultiplier()
    {
        var source = ReadEmbeddedShader("reference.frag.hlsl");
        var branchStart = source.IndexOf("if (fullBright)", StringComparison.Ordinal);
        Assert.True(branchStart >= 0);

        var branchEnd = source.IndexOf("else", branchStart, StringComparison.Ordinal);
        Assert.True(branchEnd > branchStart);
        var fullBrightBranch = source[branchStart..branchEnd];
        Assert.Contains("shade = input.vSpecular.rgb;", fullBrightBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("uCameraOrigin.w", fullBrightBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void FnvLighting30UsesClassifiedGlowAndExactHdrInputs()
    {
        var source = ReadEmbeddedShader("reference.frag.hlsl");

        Assert.Contains("(MaterialTextureFlags(packedState) & 8u) != 0u", source, StringComparison.Ordinal);
        Assert.Contains("(MaterialTextureFlags(packedState) & 16u) != 0u", source, StringComparison.Ordinal);
        Assert.Contains("emission *= input.vEffectFalloff.w * uCameraOrigin.w;", source,
            StringComparison.Ordinal);
        Assert.Contains("ambient = min(ambient, 1.0);", source, StringComparison.Ordinal);
        Assert.Contains("shade += glow * emission;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lit += emission", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FnvClassicEnvironmentMappingKeepsRecoveredMaskEquationSeparateFromFo4()
    {
        var source = ReadEmbeddedShader("reference.frag.hlsl");
        var classicStart = source.IndexOf(
            "// FO3/FNV classic PP-lighting environment pass", StringComparison.Ordinal);
        var fo4Start = source.IndexOf(
            "// FO4 cubemap environment reflections", classicStart, StringComparison.Ordinal);

        Assert.True(classicStart >= 0);
        Assert.True(fo4Start > classicStart);
        var classic = source[classicStart..fo4Start];
        Assert.Contains("(MaterialTextureFlags(packedState) & 64u) != 0u", source,
            StringComparison.Ordinal);
        Assert.Contains("(MaterialTextureFlags(packedState) & 128u) != 0u", source,
            StringComparison.Ordinal);
        Assert.Contains("(MaterialTextureFlags(packedState) & 512u) != 0u", source,
            StringComparison.Ordinal);
        Assert.Contains("float classicEnvMask = normalMapAlpha;", classic, StringComparison.Ordinal);
        Assert.Contains("input.vTexIndices.z, input.vTexCoord, input.vTextureState.z).r", classic,
            StringComparison.Ordinal);
        Assert.Contains(".Sample(sPalette, reflectDir).rgb", classic, StringComparison.Ordinal);
        Assert.Contains(
            "classicEnvMask * input.vEnvMap.y * input.vAlphaState.z",
            classic,
            StringComparison.Ordinal);
        Assert.Contains("? reflect(V, normal)", classic, StringComparison.Ordinal);
        Assert.Contains(": reflect(-V, normal);", classic, StringComparison.Ordinal);
        Assert.DoesNotContain("specSmoothScale", classic, StringComparison.Ordinal);
        Assert.DoesNotContain("gEnv", classic, StringComparison.Ordinal);
        Assert.DoesNotContain("SampleLevel", classic, StringComparison.Ordinal);
    }

    [Fact]
    public void FnvClassicEnvironmentMappingEntryPointCompiles()
    {
        // This entry point is also the runtime compile gate for the new bindless cube/mask branch.
        Compile("reference.frag.hlsl", "main", "ps_5_1", []);
    }

    [Fact]
    public void FnvClassicParallaxUsesRecoveredSm3004UvEquationAcrossReferenceDrawPaths()
    {
        var source = ReadEmbeddedShader("reference.frag.hlsl");
        var parallaxStart = source.IndexOf(
            "// PC-final SM3004 simple parallax", StringComparison.Ordinal);
        var mainStart = source.IndexOf(
            "float4 main(PSInput input)", parallaxStart, StringComparison.Ordinal);

        Assert.True(parallaxStart >= 0);
        Assert.True(mainStart > parallaxStart);
        var parallax = source[parallaxStart..mainStart];
        Assert.Contains("(MaterialTextureFlags(packedState) & 256u) != 0u", source,
            StringComparison.Ordinal);
        Assert.Contains(
            "input.vTexIndices.z, input.vTexCoord, input.vTextureState.z).r",
            parallax,
            StringComparison.Ordinal);
        Assert.Contains("height * 0.04 - 0.02", parallax, StringComparison.Ordinal);
        Assert.Contains("float2 materialUv = ResolveClassicParallaxUv(input);", source,
            StringComparison.Ordinal);
        Assert.Contains("input.vTexIndices.x, materialUv, input.vTextureState.z", source,
            StringComparison.Ordinal);
        Assert.Contains("input.vTexIndices.y, materialUv, input.vTextureState.z", source,
            StringComparison.Ordinal);
        Assert.Contains("input.vTexIndices.z, materialUv, input.vTextureState.z", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ParallaxScale", parallax, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Compiles through the PRODUCTION compiler rather than a local copy. The copy this replaces
    ///     had its own resource lookup and its own regex-based rule for the unbounded-descriptor flag
    ///     — a third variant of a decision the runtime made two other ways — so a test could validate
    ///     a configuration that never ships. Sharing the code makes that impossible.
    /// </summary>
    private static void Compile(string name, string entryPoint, string profile, ShaderMacro[] macros)
    {
        ShaderCompileTestGuard.SkipUnlessEnabled();
        var bytecode = GpuShaderCompiler12.Compile(name, entryPoint, profile, macros);
        Assert.NotEmpty(bytecode);
    }

    private static string ReadEmbeddedShader(string name) => GpuShaderCompiler12.ReadSource(name);
}