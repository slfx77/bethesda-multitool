using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

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
    public void FnvRtFreeWaterKeepsRecoveredWater003BodyFogAndSkyReflectionContract()
    {
        var source = ReadEmbeddedShader("water_fnv.frag.hlsl");

        Assert.Contains(
            "float3 fnvBodyLightDir = normalize(float3(sunDir.x, 4.0 * sunDir.y, sunDir.z));",
            source,
            StringComparison.Ordinal);
        // WATER000 asm 109-111 `mad_pp r0.xyw, c8.y, r0, r6.xyzz`:
        //   refl = lerp(ReflectionColor, ReflectionMap_RT, VarAmounts.y)
        // c8.y = VarAmounts.y = DNAM@20 ReflectivityAmount is a LERP WEIGHT toward the mirror, NOT a
        // brightness multiplier — FresnelRI.w (c5.w) is the separate DNAM@160 ReflectionHDRMult,
        // which the engine clamps to >= 1.0 and so can never darken. Multiplying by
        // ReflectivityAmount made every "no reflect" record (NVCleanWaterNoReflect authors 0.0)
        // reflect pure black, which with FresnelAmount 0.75 owned ~79% of the pixel and rendered the
        // Colorado near-black. The sky-off fallback keeps WATER003's unscaled ReflectionColor.
        Assert.Contains(
            "? lerp(uReflection.rgb, skyReflectionStandIn, reflectivity)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(": uReflection.rgb;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("saturate(R.z)) * reflectivity", source, StringComparison.Ordinal);
        // Engine shoreline edge gate on the reflection weight (WATER003 asm 96): reflections die
        // over thin water so a down-look into shallows shows the body + bed.
        Assert.Contains("float fresneled = saturate(F * corrD.x);", source, StringComparison.Ordinal);
        // WATER000's composite solved for destination blending. Retail's WATER000 is opaque and gets
        // its see-through by sampling the RefractionMap in RGB; our destination already holds that
        // scene, so matching the refraction coefficient gives alpha = 1-(1-Fe)(1-W) and a
        // premultiplied source. WATER003's alpha (= W alone) is NOT the right port while a reflection
        // term is present — it drops the reflection's share and the bed shows through at grazing
        // angles where retail is opaque.
        Assert.Contains(
            "float alpha = saturate(1.0 - (1.0 - fresneled) * (1.0 - W));",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "float3 premultiplied = (1.0 - fresneled) * W * body + fresneled * refl + spec;",
            source,
            StringComparison.Ordinal);
        // Straight (non-premultiplied) SrcAlpha/InvSrcAlpha blending — the source colour must be
        // divided back out by the coverage it was premultiplied against.
        Assert.Contains("premultiplied / max(alpha, 1e-4)", source, StringComparison.Ordinal);
        // Specular is added to the NUMERATOR only and must never reach alpha. SunPower reaches 826 on
        // these records, so the glint lobe flips 0->1 as the animated ripple normal moves; as a
        // coverage term it swung alpha per-frame and the premultiplied divide amplified it into a
        // visible shimmer. Scoped to the expression, not the prose in the header comment.
        Assert.DoesNotContain("float specCoverage =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(1.0 - specCoverage)", source, StringComparison.Ordinal);
        // Body color deepens over the vertical fog lane (corrD.y), not the shoreline feather.
        Assert.Contains(
            "float3 body = lerp(uShallow.rgb, uDeep.rgb, corrD.y);",
            source,
            StringComparison.Ordinal);
        // The fog weight W (WATER000 asm 136-141, and WATER003's whole alpha): depthT x linear fog
        // of the slant column over the above-water FogNear/Far x FogAmount. Here it is one of the
        // two coverage terms rather than the alpha itself.
        Assert.Contains(
            "float aboveFog = 1.0 - saturate(fogFar * (1.0 - corrD.x) / max(fogFar - fogNear, 1e-3));",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "float W = saturate(depthT * aboveFog * saturate(uFnvWater001Surface.z));",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "max(max(saturate(depthT), fresneled), 0.15)",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WaterDepthShaderMaxResolvesReversedZMsaaBeforeLinearization()
    {
        // The depth helpers + MSAA descriptor alias live in the shared water header; the depth
        // block that consumes them lives in each per-game main (asserted via the FNV file).
        var common = ReadEmbeddedShader("water_common.hlsli");
        var helperStart = common.IndexOf("float LoadSceneDepth(", StringComparison.Ordinal);
        var helperEnd = common.IndexOf("#endif // WATER_COMMON_HLSLI", helperStart, StringComparison.Ordinal);

        Assert.True(helperStart >= 0);
        Assert.True(helperEnd > helperStart);
        var helper = common[helperStart..helperEnd];
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

        // Bounded to the heap's persistent region: the unbounded `[]` Texture2DMS alias
        // misresolved late-written descriptor slots on shipped drivers (see water_common.hlsli).
        Assert.Contains(
            "Texture2DMS<float> gWaterDepthTexturesMsaa[16384] : register(t0, space3);",
            common,
            StringComparison.Ordinal);
        var source = ReadEmbeddedShader("water_fnv.frag.hlsl");
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