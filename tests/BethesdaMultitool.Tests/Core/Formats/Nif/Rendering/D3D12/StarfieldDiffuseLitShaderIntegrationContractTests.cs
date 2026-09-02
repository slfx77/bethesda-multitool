using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Compile-inventory and production-seam contracts for the Starfield diffuse-lit shader family.
/// </summary>
public sealed class StarfieldDiffuseLitShaderIntegrationContractTests
{
    private const string FamilyMacro = "REFERENCE_STARFIELD_DIFFUSE_LIT";
    private const string CutoutMacro = "REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER";
    private const string DoubleSidedMacro = "REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED";

    [Fact]
    public void ProductionPermutationInventoryCoversAllFourVariantsAndTheirPcfCrossProduct()
    {
        var expectedVariantKeys = new[]
        {
            FamilyMacro,
            $"{FamilyMacro},{CutoutMacro}",
            $"{FamilyMacro},{CutoutMacro},{DoubleSidedMacro}",
            $"{FamilyMacro},{DoubleSidedMacro}"
        }.Order(StringComparer.Ordinal).ToArray();

        var referenceVariants = ShaderPermutations.Reference
            .Where(permutation => IsStarfieldDiffuseLit(permutation) && permutation.Profile == "ps_5_1")
            .ToArray();
        Assert.Equal(
            expectedVariantKeys,
            referenceVariants.Select(VariantKey).Order(StringComparer.Ordinal));
        Assert.All(referenceVariants, AssertReferencePixelShader);

        var instancedVertexVariants = ShaderPermutations.Reference
            .Where(permutation =>
                IsStarfieldDiffuseLit(permutation) &&
                permutation.Profile == "vs_5_1" &&
                permutation.File == "reference_instanced.vert.hlsl")
            .ToArray();
        Assert.Equal(
            new[] { FamilyMacro, $"{FamilyMacro},{CutoutMacro}" },
            instancedVertexVariants.Select(VariantKey).Order(StringComparer.Ordinal));
        Assert.All(instancedVertexVariants, AssertReferenceInstancedVertexShader);

        var directVertexVariants = ShaderPermutations.Reference
            .Where(permutation =>
                IsStarfieldDiffuseLit(permutation) &&
                permutation.Profile == "vs_5_1" &&
                permutation.File == "reference.vert.hlsl")
            .ToArray();
        Assert.Equal(
            new[] { FamilyMacro, $"{FamilyMacro},{CutoutMacro}" },
            directVertexVariants.Select(VariantKey).Order(StringComparer.Ordinal));
        Assert.All(directVertexVariants, AssertReferenceDirectVertexShader);

        var pcfVariants = ShaderPermutations.ShadowComparisonPcf
            .Where(IsStarfieldDiffuseLit)
            .ToArray();
        Assert.Equal(
            expectedVariantKeys,
            pcfVariants.Select(VariantKey).Order(StringComparer.Ordinal));
        Assert.All(pcfVariants, permutation =>
        {
            AssertReferencePixelShader(permutation);
            Assert.Contains(
                permutation.Macros,
                macro => macro.Name == ShadowComparisonPcf12.ShaderMacroName && macro.Definition == "1");
        });

        // RenderingShaderCompilationTests compiles All, making these twelve inventory entries the
        // gate for both direct and instanced compact VS pairs and every PS under both shadow modes.
        Assert.Equal(12, ShaderPermutations.All.Count(IsStarfieldDiffuseLit));
    }

    [Fact]
    public void ShaderRetainsDiffuseOptionalNormalShadowAtmosphereAndFogOnly()
    {
        var shader = SourceContract.ReadShaderSource("reference.frag.hlsl");
        var input = SourceContract.Extract(
            shader,
            "struct StarfieldDiffuseLitPSInput",
            "// Reversed-Z [1,0] depth");
        var branch = SourceContract.Extract(
            shader,
            "#elif REFERENCE_STARFIELD_DIFFUSE_LIT\n",
            "#else\nfloat4 main(PSInput input)");

        Assert.Contains("float4 Position     : SV_Position;", input, StringComparison.Ordinal);
        Assert.Contains("float3 vWorldNormal : TEXCOORD0;", input, StringComparison.Ordinal);
        Assert.Contains("float2 vTexCoord    : TEXCOORD1;", input, StringComparison.Ordinal);
        Assert.Contains("float4 vVertexColor : TEXCOORD2;", input, StringComparison.Ordinal);
        Assert.Contains("float3 vTangent     : TEXCOORD3;", input, StringComparison.Ordinal);
        Assert.Contains("float3 vBitangent   : TEXCOORD4;", input, StringComparison.Ordinal);
        Assert.Contains("nointerpolation float4 vRenderState : TEXCOORD6;", input,
            StringComparison.Ordinal);
        Assert.Contains("nointerpolation float4 vTextureState : TEXCOORD7;", input,
            StringComparison.Ordinal);
        Assert.Contains("nointerpolation uint4 vTexIndices : TEXCOORD8;", input,
            StringComparison.Ordinal);
        Assert.Contains("float3 vWorldPos : TEXCOORD9;", input, StringComparison.Ordinal);
        Assert.Contains(
            "nointerpolation float4 vStarfieldMaterialColor : TEXCOORD10;",
            input,
            StringComparison.Ordinal);
        Assert.DoesNotContain("vSpecular", input, StringComparison.Ordinal);
        Assert.DoesNotContain("vEnvMap", input, StringComparison.Ordinal);
        Assert.DoesNotContain("vSpecularLodFade", input, StringComparison.Ordinal);

        SourceContract.AssertOrder(
            branch,
            ".Sample(sDiffuse, input.vTexCoord);",
            "#if REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER",
            "float sampleAlpha = HasStarfieldOpacityMap(input.vTextureState.z)",
            "textures[NonUniformResourceIndex(input.vTexIndices.z)].Sample(sDiffuse, input.vTexCoord).r",
            ": saturate(sample.a);",
            "if (!(sampleAlpha > input.vAlphaState.x)) discard;",
            "float3 normal = normalize(input.vWorldNormal);",
            "#if REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED",
            "if (input.vRenderState.y > 0.5)",
            ".Sample(sNormalMap, input.vTexCoord);",
            "if (input.vTextureState.x > 0.5)",
            "mapN.xy *= input.vRenderState.z;",
            "if (tLenSq > 1e-6 && bLenSq > 1e-6)",
            "? ShadowFactor(input.vWorldPos)",
            "float3 shade = AtmosphereLight(",
            "float3 albedo = input.vTextureState.w == -2.0",
            "? lerp(sample.rgb, input.vStarfieldMaterialColor.rgb, input.vStarfieldMaterialColor.a)",
            ": input.vTextureState.w == -3.0",
            "? lerp(sample.rgb, input.vVertexColor.rgb, input.vVertexColor.a)",
            ": sample.rgb * input.vVertexColor.rgb;",
            "float3 lit = albedo * shade;",
            "lit = min(lit, 1.0);",
            "ApplyFog(lit, input.vWorldPos, 0.0)");
        Assert.Equal(3, SourceContract.CountOccurrences(branch, ".Sample("));
        Assert.DoesNotContain("sample.a * input.vVertexColor.a", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("input.vVertexColor.a", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("uClipPlane", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("clip(", branch, StringComparison.Ordinal);

        foreach (var omittedTerm in new[]
                 {
                     "vSpecular",
                     "vEnvMap",
                     "vSpecularLodFade",
                     "specSample",
                     "specMask",
                     "specTerm",
                     "cubemaps[",
                     ".GetDimensions(",
                     ".SampleLevel(",
                     "pow(",
                     "reflect("
                 })
        {
            Assert.DoesNotContain(omittedTerm, branch, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DirectVertexShaderPublishesTheSparseStarfieldColorUnionOnThePerDrawAbi()
    {
        var shader = SourceContract.ReadShaderSource("reference.vert.hlsl");
        var output = SourceContract.Extract(shader, "// The specialized PS inputs", "VSOutput main(");
        var main = SourceContract.Extract(shader, "VSOutput main(", "return o;\n}");

        Assert.Contains("#define REFERENCE_SPECIALIZED_DIRECT_VERTEX 1", output,
            StringComparison.Ordinal);
        Assert.Contains("#ifdef REFERENCE_STARFIELD_DIFFUSE_LIT", output,
            StringComparison.Ordinal);
        Assert.Contains(
            "nointerpolation float4 vStarfieldMaterialColor : TEXCOORD10;",
            output,
            StringComparison.Ordinal);
        Assert.Contains("#ifndef REFERENCE_SPECIALIZED_DIRECT_VERTEX", output,
            StringComparison.Ordinal);
        Assert.Contains("o.vStarfieldMaterialColor = uEffectFalloff;", main,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SV_InstanceID", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("uInstanceWorlds", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineFactoryPublishesFourPsosAtomicallyAndExposesEveryVariant()
    {
        var source = D3D12Source("ReferencePipelineFactory12.cs");
        var create = SourceContract.Extract(
            source,
            "private void TryCreateStarfieldDiffuseLitPipelines(",
            "private void TryCreateDirectModernStandardOpaquePipelines(");
        var directCreate = SourceContract.Extract(
            source,
            "private void TryCreateDirectStarfieldDiffuseLitPipelines(",
            "private ID3D12PipelineState CreateShadowPipelineState(");
        var route = SourceContract.Extract(
            source,
            "public bool StarfieldDiffuseLitOpaqueAvailable",
            "/// <summary>Depth-only shadow-pass PSO");
        var dispose = SourceContract.Extract(
            source,
            "public void Dispose()",
            "private readonly record struct BlendPipelineKey(");

        SourceContract.AssertOrder(
            source,
            "var shaderActivation = ModernStandardShaderActivationPolicy.Resolve(game, shaderOverride);",
            "StarfieldDiffuseLitRequested = shaderActivation.StarfieldDiffuseLitRequested;",
            "if (FalloutModernStandardRequested)",
            "TryCreateModernStandardOpaquePipelines();",
            "if (StarfieldDiffuseLitRequested)",
            "TryCreateStarfieldDiffuseLitPipelines();");
        Assert.Contains(
            "ReferencePipelineFactory12: opaque shader profile game={0} override={1}",
            source,
            StringComparison.Ordinal);
        Assert.Equal(6, SourceContract.CountOccurrences(
            create, $"new ShaderMacro(\"{FamilyMacro}\", \"1\")"));
        Assert.Equal(3, SourceContract.CountOccurrences(
            create, $"new ShaderMacro(\"{CutoutMacro}\", \"1\")"));
        Assert.Equal(2, SourceContract.CountOccurrences(
            create, $"new ShaderMacro(\"{DoubleSidedMacro}\", \"1\")"));
        Assert.Equal(2, SourceContract.CountOccurrences(
            create, "\"reference_instanced.vert.hlsl\", \"main\", \"vs_5_1\""));
        Assert.Contains("backVs, backPs, doubleSided: false", create, StringComparison.Ordinal);
        Assert.Contains("backVs, doublePs, doubleSided: true", create, StringComparison.Ordinal);
        Assert.Contains("cutoutVs, backCutoutPs, doubleSided: false", create,
            StringComparison.Ordinal);
        Assert.Contains("cutoutVs, doubleCutoutPs, doubleSided: true", create,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            create,
            "back = CreatePipelineState(",
            "doubleSided = CreatePipelineState(",
            "backCutout = CreatePipelineState(",
            "doubleCutout = CreatePipelineState(",
            "_starfieldDiffuseLitBackPso = back;",
            "_starfieldDiffuseLitDoublePso = doubleSided;",
            "_starfieldDiffuseLitBackCutoutPso = backCutout;",
            "_starfieldDiffuseLitDoubleCutoutPso = doubleCutout;",
            "finally",
            "DisposeAbandonedConstructionPipeline(ref doubleCutout);",
            "DisposeAbandonedConstructionPipeline(ref backCutout);",
            "DisposeAbandonedConstructionPipeline(ref doubleSided);",
            "DisposeAbandonedConstructionPipeline(ref back);");
        Assert.Contains("catch (Exception ex) when (ex is not OutOfMemoryException)", create,
            StringComparison.Ordinal);

        Assert.Contains(
            "_starfieldDiffuseLitBackPso is not null &&\n" +
            "        _starfieldDiffuseLitDoublePso is not null &&\n" +
            "        _starfieldDiffuseLitBackCutoutPso is not null &&\n" +
            "        _starfieldDiffuseLitDoubleCutoutPso is not null;",
            route,
            StringComparison.Ordinal);
        Assert.Contains("(false, false) => _starfieldDiffuseLitBackPso", route,
            StringComparison.Ordinal);
        Assert.Contains("(false, true) => _starfieldDiffuseLitDoublePso", route,
            StringComparison.Ordinal);
        Assert.Contains("(true, false) => _starfieldDiffuseLitBackCutoutPso", route,
            StringComparison.Ordinal);
        Assert.Contains("(true, true) => _starfieldDiffuseLitDoubleCutoutPso", route,
            StringComparison.Ordinal);
        Assert.Contains("return pso is not null;", route, StringComparison.Ordinal);

        Assert.Contains(
            "DirectStarfieldDiffuseLitRequested = StarfieldDiffuseLitRequested;",
            source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "TryCreateStarfieldDiffuseLitPipelines();",
            "TryCreateDirectStarfieldDiffuseLitPipelines();");
        Assert.Equal(2, SourceContract.CountOccurrences(
            directCreate, "\"reference.vert.hlsl\", \"main\", \"vs_5_1\""));
        Assert.DoesNotContain("reference_instanced.vert.hlsl", directCreate, StringComparison.Ordinal);
        Assert.Equal(4, SourceContract.CountOccurrences(directCreate, "blendAttachment: null"));
        Assert.Equal(4, SourceContract.CountOccurrences(directCreate, "depthWriteEnabled: true"));
        Assert.DoesNotContain("alphaToCoverage:", directCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("decal:", directCreate, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            directCreate,
            "back = CreatePipelineState(",
            "doubleSided = CreatePipelineState(",
            "backCutout = CreatePipelineState(",
            "doubleCutout = CreatePipelineState(",
            "_directStarfieldDiffuseLitBackPso = back;",
            "_directStarfieldDiffuseLitDoublePso = doubleSided;",
            "_directStarfieldDiffuseLitBackCutoutPso = backCutout;",
            "_directStarfieldDiffuseLitDoubleCutoutPso = doubleCutout;",
            "finally",
            "DisposeAbandonedConstructionPipeline(ref doubleCutout);",
            "DisposeAbandonedConstructionPipeline(ref backCutout);",
            "DisposeAbandonedConstructionPipeline(ref doubleSided);",
            "DisposeAbandonedConstructionPipeline(ref back);");
        Assert.Contains("public bool DirectStarfieldDiffuseLitOpaqueAvailable =>", route,
            StringComparison.Ordinal);
        Assert.Contains("public bool TryGetDirectStarfieldDiffuseLitPso(", route,
            StringComparison.Ordinal);
        Assert.Contains("(false, false) => _directStarfieldDiffuseLitBackPso", route,
            StringComparison.Ordinal);
        Assert.Contains("(false, true) => _directStarfieldDiffuseLitDoublePso", route,
            StringComparison.Ordinal);
        Assert.Contains("(true, false) => _directStarfieldDiffuseLitBackCutoutPso", route,
            StringComparison.Ordinal);
        Assert.Contains("(true, true) => _directStarfieldDiffuseLitDoubleCutoutPso", route,
            StringComparison.Ordinal);

        Assert.Contains("_mirrorPsoMap[_starfieldDiffuseLitBackPso] = mirrorBack;", source,
            StringComparison.Ordinal);
        Assert.Contains("_mirrorPsoMap[_starfieldDiffuseLitDoublePso] = OpaqueDoublePso;", source,
            StringComparison.Ordinal);
        Assert.Contains("_mirrorPsoMap[_starfieldDiffuseLitBackCutoutPso] = mirrorBack;", source,
            StringComparison.Ordinal);
        Assert.Contains("_mirrorPsoMap[_starfieldDiffuseLitDoubleCutoutPso] = OpaqueDoublePso;", source,
            StringComparison.Ordinal);
        Assert.Contains("_starfieldDiffuseLitDoubleCutoutPso?.Dispose();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("_starfieldDiffuseLitBackCutoutPso?.Dispose();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("_starfieldDiffuseLitDoublePso?.Dispose();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("_starfieldDiffuseLitBackPso?.Dispose();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("_directStarfieldDiffuseLitDoubleCutoutPso?.Dispose();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("_directStarfieldDiffuseLitBackCutoutPso?.Dispose();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("_directStarfieldDiffuseLitDoublePso?.Dispose();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("_directStarfieldDiffuseLitBackPso?.Dispose();", dispose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GameSpecificDefaultIsResolvedBeforePipelineCreationAndReportedByExistingTelemetry()
    {
        var factory = D3D12Source("ReferencePipelineFactory12.cs");
        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var livePipeline = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Pipeline.cs");
        var headless = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");
        var environment = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "EnvironmentVariables.cs");

        Assert.Contains("GpuRootSignature12 rootSignature,\n        BethesdaGame game)", factory,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            factory,
            "ModernStandardShaderActivationPolicy.Resolve(game, shaderOverride)",
            "if (FalloutModernStandardRequested)",
            "TryCreateModernStandardOpaquePipelines();",
            "if (StarfieldDiffuseLitRequested)",
            "TryCreateStarfieldDiffuseLitPipelines();",
            "_mirrorPsoMap = new Dictionary<ID3D12PipelineState, ID3D12PipelineState>");
        Assert.Contains("_pipelines = new ReferencePipelineFactory12(gpu, rootSignature, game);",
            renderer, StringComparison.Ordinal);
        Assert.Contains("_deletionQueue12, _data.Game,", livePipeline, StringComparison.Ordinal);
        Assert.Contains("gpu, recorder, ring, rootSig, heap, meshCache, deletion, game)", headless,
            StringComparison.Ordinal);
        Assert.Contains("ReferenceModernStandardShaderActive = _renderCache?.Game switch", renderer,
            StringComparison.Ordinal);
        Assert.Contains("BethesdaGame.Starfield => _pipelines.StarfieldDiffuseLitOpaqueAvailable",
            renderer, StringComparison.Ordinal);
        Assert.Contains("Starfield's diffuse-lit <c>.mat</c>", environment, StringComparison.Ordinal);
        Assert.Contains("exact \"0\" disables it", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void SpecializedMainPsosCannotReachANonNeutralMirrorOrUndefinedHeadlessClipPlane()
    {
        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var mirrorReplay = SourceContract.Extract(
            renderer,
            "public bool RenderMirrorColor(",
            "public void Dispose()");
        var shadowReplay = SourceContract.Extract(
            renderer,
            "public bool RenderShadowDepth(",
            "public bool RenderMirrorColor(");
        var headless = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");

        Assert.Contains("var pso = _pipelines.GetMirrorPso(draw.Pso);", mirrorReplay,
            StringComparison.Ordinal);
        Assert.Contains("? _pipelines.ShadowAlphaTestPso", shadowReplay,
            StringComparison.Ordinal);
        Assert.Contains(": _pipelines.ShadowOpaquePso", shadowReplay,
            StringComparison.Ordinal);

        Assert.Contains("private const int AtmosphereClipPlaneFloat4Slot = 37;", headless,
            StringComparison.Ordinal);
        Assert.Contains(
            "private const int AtmosphereBytes = (AtmosphereClipPlaneFloat4Slot + 1) * 16;",
            headless,
            StringComparison.Ordinal);
        Assert.Contains("cb[AtmosphereClipPlaneFloat4Slot * 4 + 3] = 1f;", headless,
            StringComparison.Ordinal);
        Assert.Contains("Put(AtmosphereClipPlaneFloat4Slot, 0f, 0f, 0f, 1f);", headless,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "const int atmosphereBytes = 10 * 16 + 4 * 64 + 4 * 16 + 6 * 16;",
            headless,
            StringComparison.Ordinal);
        Assert.Contains("case \"--game\": gameSpec = Next(args, ref i);", headless,
            StringComparison.Ordinal);
        Assert.Contains("value.Equals(\"FO76\", StringComparison.OrdinalIgnoreCase)", headless,
            StringComparison.Ordinal);
        Assert.Contains("new WorldRenderCache { Game = game }", headless,
            StringComparison.Ordinal);
        Assert.Contains("s.ReferenceModernStandardBatches", headless,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OpacityTextureReferencesAreReleasedOnRollbackAndResidentMeshEviction()
    {
        var cache = D3D12Source("ReferenceMeshCache12.cs");
        var rollback = SourceContract.Extract(
            cache,
            "private static void ReleaseSubmeshTextures(",
            "private static Vector3[]? ExtractParticleCenters(");
        var residentMesh = D3D12Source("CachedNifMesh12.cs");
        var dispose = SourceContract.Extract(
            residentMesh,
            "public void Dispose()",
            "}\n#endif");

        Assert.Contains("textureCache.Release(submesh.StarfieldOpacity);", rollback,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            dispose,
            "if (submesh.StarfieldOpacity is { } starfieldOpacity)",
            "_textureCache.Release(starfieldOpacity);");
    }

    private static bool IsStarfieldDiffuseLit(ShaderPermutation permutation) =>
        permutation.Macros.Any(macro => macro.Name == FamilyMacro && macro.Definition == "1");

    private static string VariantKey(ShaderPermutation permutation) =>
        string.Join(",", permutation.Macros
            .Where(macro => macro.Name != ShadowComparisonPcf12.ShaderMacroName)
            .Select(macro => macro.Name)
            .Order(StringComparer.Ordinal));

    private static void AssertReferencePixelShader(ShaderPermutation permutation)
    {
        Assert.Equal("reference.frag.hlsl", permutation.File);
        Assert.Equal("main", permutation.EntryPoint);
        Assert.Equal("ps_5_1", permutation.Profile);
    }

    private static void AssertReferenceInstancedVertexShader(ShaderPermutation permutation)
    {
        Assert.Equal("reference_instanced.vert.hlsl", permutation.File);
        Assert.Equal("main", permutation.EntryPoint);
        Assert.Equal("vs_5_1", permutation.Profile);
    }

    private static void AssertReferenceDirectVertexShader(ShaderPermutation permutation)
    {
        Assert.Equal("reference.vert.hlsl", permutation.File);
        Assert.Equal("main", permutation.EntryPoint);
        Assert.Equal("vs_5_1", permutation.Profile);
    }

    private static string D3D12Source(string fileName) => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12", fileName);
}
