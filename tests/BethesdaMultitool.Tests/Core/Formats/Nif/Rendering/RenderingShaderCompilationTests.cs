using System.Reflection;
using BethesdaMultitool.Core.Formats.SpeedTree;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class RenderingShaderCompilationTests
{
    private const ShaderFlags EnableUnboundedDescriptorTables = (ShaderFlags)0x00100000;

    [Fact]
    public void WaterAndSpeedTreeEntryPointsCompileForEveryAffectedPermutation()
    {
        var permutations = new (string Name, string EntryPoint, string Profile, ShaderMacro[] Macros)[]
        {
            ("water.vert.hlsl", "main", "vs_5_1", []),
            ("water_noise.comp.hlsl", "mainScrollBlend", "cs_5_1", []),
            ("water_noise.comp.hlsl", "mainNormal", "cs_5_1", []),
            ("water_modern.comp.hlsl", "mainBodyCoverage", "cs_5_1", []),
            ("water_modern.comp.hlsl", "mainNormal", "cs_5_1", []),
            ("water_modern.comp.hlsl", "mainGloss", "cs_5_1", []),
            ("water_modern.comp.hlsl", "mainDepthLut", "cs_5_1", []),
            ("water.frag.hlsl", "main", "ps_5_1", []),
            ("water.frag.hlsl", "main", "ps_5_1", [new ShaderMacro("OBLIVION_WATER", "1")]),
            ("water.frag.hlsl", "main", "ps_5_1", [new ShaderMacro("FO4_WATER", "1")]),
            ("water.frag.hlsl", "main", "ps_5_1",
                [new ShaderMacro("FO4_WATER", "1"), new ShaderMacro("FO4_WATER_ARCHITECTURAL", "1")]),
            ("water.frag.hlsl", "main", "ps_5_1",
                [new ShaderMacro("FO4_WATER", "1"), new ShaderMacro("FO4_WATER_ARCHITECTURAL", "1"),
                    new ShaderMacro("FO76_WATER", "1")]),
            ("water.frag.hlsl", "main", "ps_5_1", [new ShaderMacro("MORROWIND_WATER", "1")]),
            ("reference_instanced.vert.hlsl", "main", "vs_5_1", []),
            ("reference_instanced.vert.hlsl", "main", "vs_5_1",
                [new ShaderMacro("SHADOW_CARD_LIGHT_FACING", "1")])
        };

        foreach (var permutation in permutations)
        {
            Compile(permutation.Name, permutation.EntryPoint, permutation.Profile, permutation.Macros);
        }
    }

    [Fact]
    public void EveryRemainingEmbeddedRenderingEntryPointCompiles()
    {
        // Particles use the non-instanced reference vertex/pixel pair. SpeedTree bark, frond and leaf
        // behavior is selected at runtime in the shared reference shaders, so compiling both the normal
        // and SHADOW_CARD_LIGHT_FACING forms above covers every preprocessor permutation currently used.
        var entryPoints = new (string Name, string EntryPoint, string Profile)[]
        {
            ("tonemap.vert.hlsl", "main", "vs_5_1"),
            ("tonemap.frag.hlsl", "main", "ps_5_1"),
            ("tonemap.frag.hlsl", "mainAvg", "ps_5_1"),
            ("tonemap.frag.hlsl", "mainAdapt", "ps_5_1"),
            ("bloom.frag.hlsl", "mainDownsample16", "ps_5_1"),
            ("bloom.frag.hlsl", "main", "ps_5_1"),
            ("reference.vert.hlsl", "main", "vs_5_1"),
            ("reference.frag.hlsl", "main", "ps_5_1"),
            ("shadow.frag.hlsl", "main", "ps_5_1"),
            ("skin.vert.hlsl", "main", "vs_5_1"),
            ("skin.frag.hlsl", "main", "ps_5_1"),
            ("sky_billboard.vert.hlsl", "main", "vs_5_1"),
            ("sky_billboard.frag.hlsl", "main", "ps_5_1"),
            ("sky_geo.vert.hlsl", "main", "vs_5_1"),
            ("sky_geo.frag.hlsl", "main", "ps_5_1"),
            ("terrain.vert.hlsl", "main", "vs_5_1"),
            ("terrain.frag.hlsl", "main", "ps_5_1"),
            ("terrain_textured.vert.hlsl", "main", "vs_5_1"),
            ("terrain_textured.frag.hlsl", "main", "ps_5_1"),
            ("cellgrid.vert.hlsl", "main", "vs_5_1"),
            ("cellgrid.frag.hlsl", "main", "ps_5_1"),
            ("triangle.vert.hlsl", "main", "vs_5_1"),
            ("triangle.frag.hlsl", "main", "ps_5_1")
        };

        foreach (var entryPoint in entryPoints)
        {
            Compile(entryPoint.Name, entryPoint.EntryPoint, entryPoint.Profile, []);
        }
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
        Assert.Equal(2, source.Split("* vertexWeight", StringSplitOptions.None).Length - 1);
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

    private static void Compile(string name, string entryPoint, string profile, ShaderMacro[] macros)
    {
        var source = ReadEmbeddedShader(name);
        // Match the runtime compiler's declaration-shape check so named tables such as
        // gWaterTextures[] and textures[] both receive D3DCOMPILE_ENABLE_UNBOUNDED_DESCRIPTOR_TABLES.
        var flags = System.Text.RegularExpressions.Regex.IsMatch(
            source, @"\[\]\s*:\s*register", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            ? EnableUnboundedDescriptorTables
            : ShaderFlags.None;

        Blob? bytecode = null;
        Blob? errors = null;
        try
        {
            var result = Compiler.Compile(
                source,
                macros,
                include: null!,
                entryPoint,
                sourceName: name,
                profile,
                flags,
                EffectFlags.None,
                out bytecode,
                out errors);

            Assert.False(result.Failure,
                $"{name}:{entryPoint} ({profile}) failed: {errors?.AsString() ?? "(no compiler diagnostics)"}");
            Assert.NotNull(bytecode);
        }
        finally
        {
            errors?.Dispose();
            bytecode?.Dispose();
        }
    }

    private static string ReadEmbeddedShader(string name)
    {
        var assembly = typeof(SptGeometryOptions).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
