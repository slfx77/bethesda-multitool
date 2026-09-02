using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     End-to-end contracts around the optional modern-standard opaque shader. The classifier's
///     material matrix is covered separately; these tests pin the production seams that can leave a
///     correct classifier compiled but unused, incorrectly batched, or invisible in an A/B capture.
/// </summary>
public sealed class ModernStandardOpaqueShaderIntegrationContractTests
{
    private const string ModernMacro = "REFERENCE_MODERN_STANDARD";
    private const string CutoutMacro = "REFERENCE_MODERN_STANDARD_ALPHA_GREATER";
    private const string DoubleSidedMacro = "REFERENCE_MODERN_STANDARD_DOUBLE_SIDED";

    [Fact]
    public void ProductionPermutationInventoryCoversEveryRuntimeVariantAndItsPcfCrossProduct()
    {
        var expectedVariantKeys = new[]
        {
            ModernMacro,
            $"{ModernMacro},{CutoutMacro}",
            $"{ModernMacro},{CutoutMacro},{DoubleSidedMacro}"
        };

        var referenceVariants = ShaderPermutations.Reference
            .Where(permutation => IsModernStandard(permutation) && permutation.Profile == "ps_5_1")
            .ToArray();
        Assert.Equal(expectedVariantKeys, referenceVariants.Select(VariantKey).Order(StringComparer.Ordinal));
        Assert.All(referenceVariants, AssertReferencePixelShader);

        var instancedVertexVariants = ShaderPermutations.Reference
            .Where(permutation =>
                IsModernStandard(permutation) &&
                permutation.Profile == "vs_5_1" &&
                permutation.File == "reference_instanced.vert.hlsl")
            .ToArray();
        Assert.Equal(
            new[] { ModernMacro, $"{ModernMacro},{CutoutMacro}" },
            instancedVertexVariants.Select(VariantKey).Order(StringComparer.Ordinal));
        Assert.All(instancedVertexVariants, AssertReferenceInstancedVertexShader);

        var directVertexVariants = ShaderPermutations.Reference
            .Where(permutation =>
                IsModernStandard(permutation) &&
                permutation.Profile == "vs_5_1" &&
                permutation.File == "reference.vert.hlsl")
            .ToArray();
        Assert.Equal(
            new[] { ModernMacro, $"{ModernMacro},{CutoutMacro}" },
            directVertexVariants.Select(VariantKey).Order(StringComparer.Ordinal));
        Assert.All(directVertexVariants, AssertReferenceDirectVertexShader);

        var pcfVariants = ShaderPermutations.ShadowComparisonPcf
            .Where(IsModernStandard)
            .ToArray();
        Assert.Equal(expectedVariantKeys, pcfVariants.Select(VariantKey).Order(StringComparer.Ordinal));
        Assert.All(pcfVariants, permutation =>
            Assert.Contains(
                permutation.Macros,
                macro => macro.Name == ShadowComparisonPcf12.ShaderMacroName && macro.Definition == "1"));

        // RenderingShaderCompilationTests iterates All. Pinning all ten entries here makes that
        // loop a compile gate for both direct and instanced stage-matched VS pairs plus all three
        // base and PCF PS forms.
        Assert.Equal(10, ShaderPermutations.All.Count(IsModernStandard));
    }

    [Fact]
    public void PipelineFactoryPublishesAllThreePsosAtomicallyAndRoutesEveryClassifierVariant()
    {
        var source = D3D12Source("ReferencePipelineFactory12.cs");
        var create = SourceContract.Extract(
            source,
            "private void TryCreateModernStandardOpaquePipelines(",
            "private void TryCreateStarfieldDiffuseLitPipelines(");
        var directCreate = SourceContract.Extract(
            source,
            "private void TryCreateDirectModernStandardOpaquePipelines(",
            "private void TryCreateDirectStarfieldDiffuseLitPipelines(");
        var route = SourceContract.Extract(
            source,
            "public bool TryGetModernStandardOpaquePso(",
            "/// <summary>Depth-only shadow-pass PSO");
        var dispose = SourceContract.Extract(
            source,
            "public void Dispose()",
            "private readonly record struct BlendPipelineKey(");

        Assert.Contains(
            "EnvironmentVariables.Get(EnvironmentVariables.Viewer.ReferenceModernStandardShader)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardShaderActivationPolicy.Resolve(game, shaderOverride)",
            source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "if (FalloutModernStandardRequested)",
            "TryCreateModernStandardOpaquePipelines();");

        Assert.Equal(5, SourceContract.CountOccurrences(
            create, $"new ShaderMacro(\"{ModernMacro}\", \"1\")"));
        Assert.Equal(3, SourceContract.CountOccurrences(
            create, $"new ShaderMacro(\"{CutoutMacro}\", \"1\")"));
        Assert.Equal(1, SourceContract.CountOccurrences(
            create, $"new ShaderMacro(\"{DoubleSidedMacro}\", \"1\")"));
        Assert.Equal(2, SourceContract.CountOccurrences(
            create, "\"reference_instanced.vert.hlsl\", \"main\", \"vs_5_1\""));
        Assert.Contains("backVs, backPs, doubleSided: false", create, StringComparison.Ordinal);
        Assert.Contains("cutoutVs, backCutoutPs, doubleSided: false", create,
            StringComparison.Ordinal);
        Assert.Contains("cutoutVs, doubleCutoutPs, doubleSided: true", create,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            create,
            "back = CreatePipelineState(",
            "backCutout = CreatePipelineState(",
            "doubleCutout = CreatePipelineState(",
            "_modernStandardBackPso = back;",
            "_modernStandardBackCutoutPso = backCutout;",
            "_modernStandardDoubleCutoutPso = doubleCutout;",
            "finally",
            "DisposeAbandonedConstructionPipeline(ref doubleCutout);",
            "DisposeAbandonedConstructionPipeline(ref backCutout);",
            "DisposeAbandonedConstructionPipeline(ref back);");
        Assert.Contains("catch (Exception ex) when (ex is not OutOfMemoryException)", create,
            StringComparison.Ordinal);

        Assert.Contains(
            "_modernStandardBackPso is not null &&\n" +
            "        _modernStandardBackCutoutPso is not null &&\n" +
            "        _modernStandardDoubleCutoutPso is not null;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.SingleSidedOpaque => _modernStandardBackPso",
            route,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.SingleSidedGreaterCutout => _modernStandardBackCutoutPso",
            route,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.DoubleSidedGreaterCutout => _modernStandardDoubleCutoutPso",
            route,
            StringComparison.Ordinal);
        Assert.Contains("_ => null", route, StringComparison.Ordinal);
        Assert.Contains("return pso is not null;", route, StringComparison.Ordinal);

        Assert.Contains(
            "DirectModernStandardOpaqueRequested = FalloutModernStandardRequested;",
            source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "TryCreateModernStandardOpaquePipelines();",
            "TryCreateDirectModernStandardOpaquePipelines();");
        Assert.Equal(2, SourceContract.CountOccurrences(
            directCreate, "\"reference.vert.hlsl\", \"main\", \"vs_5_1\""));
        Assert.DoesNotContain("reference_instanced.vert.hlsl", directCreate, StringComparison.Ordinal);
        Assert.Equal(3, SourceContract.CountOccurrences(directCreate, "blendAttachment: null"));
        Assert.Equal(3, SourceContract.CountOccurrences(directCreate, "depthWriteEnabled: true"));
        Assert.DoesNotContain("alphaToCoverage:", directCreate, StringComparison.Ordinal);
        Assert.DoesNotContain("decal:", directCreate, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            directCreate,
            "back = CreatePipelineState(",
            "backCutout = CreatePipelineState(",
            "doubleCutout = CreatePipelineState(",
            "_directModernStandardBackPso = back;",
            "_directModernStandardBackCutoutPso = backCutout;",
            "_directModernStandardDoubleCutoutPso = doubleCutout;",
            "finally",
            "DisposeAbandonedConstructionPipeline(ref doubleCutout);",
            "DisposeAbandonedConstructionPipeline(ref backCutout);",
            "DisposeAbandonedConstructionPipeline(ref back);");
        Assert.Contains("public bool DirectModernStandardOpaqueAvailable =>", route,
            StringComparison.Ordinal);
        Assert.Contains("public bool TryGetDirectModernStandardOpaquePso(", route,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.SingleSidedOpaque => _directModernStandardBackPso",
            route,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.SingleSidedGreaterCutout => _directModernStandardBackCutoutPso",
            route,
            StringComparison.Ordinal);
        Assert.Contains(
            "ModernStandardOpaqueShaderVariant.DoubleSidedGreaterCutout => _directModernStandardDoubleCutoutPso",
            route,
            StringComparison.Ordinal);

        Assert.Contains("_modernStandardDoubleCutoutPso?.Dispose();", dispose, StringComparison.Ordinal);
        Assert.Contains("_modernStandardBackCutoutPso?.Dispose();", dispose, StringComparison.Ordinal);
        Assert.Contains("_modernStandardBackPso?.Dispose();", dispose, StringComparison.Ordinal);
        Assert.Contains("_directModernStandardDoubleCutoutPso?.Dispose();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("_directModernStandardBackCutoutPso?.Dispose();", dispose,
            StringComparison.Ordinal);
        Assert.Contains("_directModernStandardBackPso?.Dispose();", dispose,
            StringComparison.Ordinal);

        // Specialized main-pass shaders intentionally omit the neutral clip instruction. Every
        // possible mirror replay route must therefore map back to an uber PSO that consumes b3.
        Assert.Contains("_mirrorPsoMap[_modernStandardBackPso] = mirrorBack;", source,
            StringComparison.Ordinal);
        Assert.Contains("_mirrorPsoMap[_modernStandardBackCutoutPso] = mirrorBack;", source,
            StringComparison.Ordinal);
        Assert.Contains("_mirrorPsoMap[_modernStandardDoubleCutoutPso] = OpaqueDoublePso;", source,
            StringComparison.Ordinal);

        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var mirrorReplay = SourceContract.Extract(
            renderer,
            "public bool RenderMirrorColor(",
            "public void Dispose()");
        Assert.Contains("var pso = _pipelines.GetMirrorPso(draw.Pso);", mirrorReplay,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SpecializedMainPassOmitsTheNeutralMirrorClipButRetainsAuthoredCutoutDiscard()
    {
        var shader = SourceContract.ReadShaderSource("reference.frag.hlsl");
        var branch = SourceContract.Extract(
            shader,
            "#if REFERENCE_MODERN_STANDARD\nfloat4 main(ModernStandardPSInput input)",
            "#elif REFERENCE_STARFIELD_DIFFUSE_LIT\n");

        Assert.DoesNotContain("uClipPlane", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("clip(", branch, StringComparison.Ordinal);
        Assert.Contains("if (!(sampleAlpha > input.vAlphaState.x)) discard;", branch,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DirectVertexShaderPublishesTheSameSparseModernSignatureWithoutTheInstancedAbi()
    {
        var shader = SourceContract.ReadShaderSource("reference.vert.hlsl");
        var output = SourceContract.Extract(shader, "// The specialized PS inputs", "VSOutput main(");
        var main = SourceContract.Extract(shader, "VSOutput main(", "return o;\n}");

        Assert.Contains("#define REFERENCE_SPECIALIZED_DIRECT_VERTEX 1", output,
            StringComparison.Ordinal);
        Assert.Contains("#ifdef REFERENCE_DIRECT_OUTPUT_ALPHA_STATE", output,
            StringComparison.Ordinal);
        Assert.Contains("#ifdef REFERENCE_DIRECT_OUTPUT_MODERN_STATE", output,
            StringComparison.Ordinal);
        Assert.Contains("nointerpolation float4 vSpecular   : TEXCOORD10;", output,
            StringComparison.Ordinal);
        Assert.Contains("nointerpolation float4 vEnvMap        : TEXCOORD13;", output,
            StringComparison.Ordinal);
        Assert.Contains("nointerpolation float vSpecularLodFade : TEXCOORD15;", output,
            StringComparison.Ordinal);
        Assert.Contains("o.vSpecularLodFade = uUvScroll.z;", main, StringComparison.Ordinal);
        Assert.Contains("#ifndef REFERENCE_SPECIALIZED_DIRECT_VERTEX\n    // FormID-heatmap payload", main,
            StringComparison.Ordinal);
        Assert.Contains("#ifndef REFERENCE_SPECIALIZED_DIRECT_VERTEX\n    // The per-draw path uses", main,
            StringComparison.Ordinal);
        Assert.Contains("#ifndef REFERENCE_SPECIALIZED_DIRECT_VERTEX\n    float activeAdtUniformScale", main,
            StringComparison.Ordinal);
        Assert.Contains("#if !defined(REFERENCE_MODERN_STANDARD) && !defined(REFERENCE_STARFIELD_DIFFUSE_LIT)",
            shader, StringComparison.Ordinal);
        Assert.DoesNotContain("SV_InstanceID", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("uInstanceWorlds", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererAdmissionUsesAuthoredFactsAndFallsBackUnlessTheSpecializedPsoExists()
    {
        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var route = SourceContract.Extract(
            renderer,
            "private ID3D12PipelineState ResolveOpaquePipeline(",
            "private void ObserveBatchBuildActivity(");

        foreach (var factBinding in new[]
                 {
                     "HeatmapEnabled: _formIdHeatmapEnabled",
                     "IsScatteredGrass: isScatteredGrass",
                     "AlphaBlend: sub.AlphaBlend",
                     "IsDecal: sub.IsDecal",
                     "IsEmissive: sub.IsEmissive",
                     "IsLighting30: sub.IsLighting30",
                     "HasLighting30GlowMap: sub.Lighting30GlowMap is not null",
                     "HasEffectFalloff: sub.HasEffectFalloff",
                     "IsEffectTintNeutral: sub.EffectTint == Vector3.One",
                     "HasSoftParticle: sub.SoftParticle.Enabled",
                     "IsBillboard: sub.IsBillboard",
                     "IsLeafBillboard: sub.IsLeafBillboard",
                     "IsTallGrass: sub.IsTallGrass",
                     "IsParticle: sub.ParticleCenters is not null || sub.LiveParticles is not null",
                     "HasRuntimeSpeedTreeLod: sub.SpeedTreeLod is not null",
                     "HasClassicBasicShader: sub.ClassicBasicShaderMode != FnvClassicBasicShaderMode.None",
                     "HasClassicParallax: sub.ClassicParallaxHeightMap is not null",
                     "HasGradientMap: sub.GradientMap is not null",
                     "TextureFeatureMask: (uint)MathF.Round(sub.TextureState.Z)",
                     "HasBump: sub.HasBump",
                     "HasSpecularMap: sub.SpecularMap is not null",
                     "ModernEnvironmentMapDeclared: sub.EnvMap is not null",
                     "ModernEnvironmentMapScale: sub.EnvMapScale",
                     "WrapTextureU: !sub.ClampTextureU",
                     "WrapTextureV: !sub.ClampTextureV",
                     "AlphaTestEnabled: sub.AlphaState.Y >= 0f",
                     "AlphaTestFunction: sub.AlphaTestFunction",
                     "DoubleSided: sub.DoubleSided"
                 })
        {
            Assert.Contains(factBinding, route, StringComparison.Ordinal);
        }

        Assert.Contains("sub.ClassicEnvMap is not null ||", route, StringComparison.Ordinal);
        Assert.Contains("sub.ClassicEnvMask is not null ||", route, StringComparison.Ordinal);
        Assert.Contains("sub.ClassicEnvMapUsesWindowReflection ||", route, StringComparison.Ordinal);
        Assert.Contains("sub.ClassicEnvMapIsSphereMap", route, StringComparison.Ordinal);
        Assert.DoesNotContain("Resident", route, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ready", route, StringComparison.OrdinalIgnoreCase);

        SourceContract.AssertOrder(
            route,
            "var facts = new ModernStandardOpaqueShaderFacts(",
            "var variant = ModernStandardOpaqueShaderPolicy.Resolve(in facts);",
            "if (_pipelines.TryGetModernStandardOpaquePso(variant, out var specializedPso))",
            "usesModernStandardShader = true;",
            "return specializedPso!;",
            "usesModernStandardShader = false;",
            "return (sub.DoubleSided, sub.IsDecal) switch");

        // Both the normal build and shadow-only materialization must carry the immutable routing bit
        // into their batch. Residency may change later, but the authored classification must not.
        Assert.Equal(2, SourceContract.CountOccurrences(
            renderer, "ResolveOpaquePipeline(sub, r.IsGrass, out var usesModernStandardShader);"));
        Assert.Equal(2, SourceContract.CountOccurrences(
            renderer, "state.Target.OpaqueBatches.GetOrCreate("));
        Assert.Equal(2, SourceContract.CountOccurrences(
            renderer, "\n                usesModernStandardShader);"));
    }

    [Fact]
    public void BatchIdentityFreezesSpecializationAndRendererReportsThePublicationCensus()
    {
        var registry = D3D12Source("OpaqueBatchRegistry12.cs");
        var order = SourceContract.Extract(
            registry,
            "public void OrderForSubmission(",
            "private static OpaqueSubmissionLane SubmissionLane(");
        var getOrCreate = SourceContract.Extract(
            registry,
            "public OpaqueBatchState GetOrCreate(",
            "private void PruneStaleBatches()");
        var renderer = D3D12Source("ReferenceRenderer12.cs");
        var draw = SourceContract.Extract(
            renderer,
            "private void DrawOpaqueBatches(",
            "private void DrawBlended(");

        Assert.Contains("bool UsesModernStandardShader);", registry, StringComparison.Ordinal);
        Assert.Contains("public bool UsesModernStandardShader { get; } = usesModernStandardShader;",
            registry, StringComparison.Ordinal);
        Assert.Contains("usesModernStandardShader);", getOrCreate, StringComparison.Ordinal);
        Assert.Contains("if (!batch.UsesModernStandardShader || batch.Instances.Count == 0)", order,
            StringComparison.Ordinal);
        Assert.Contains("ModernStandardBatchCount++;", order, StringComparison.Ordinal);
        Assert.Contains("ModernStandardInstanceCount += batch.Instances.Count;", order,
            StringComparison.Ordinal);

        SourceContract.AssertOrder(
            draw,
            "var activeBatches = _opaqueBatches.ActiveBatches;",
            "LastStats.ReferenceModernStandardShaderActive = _renderCache?.Game switch",
            "BethesdaGame.Fallout4 or BethesdaGame.Fallout76 =>",
            "BethesdaGame.Starfield => _pipelines.StarfieldDiffuseLitOpaqueAvailable",
            "LastStats.ReferenceModernStandardBatches = _opaqueBatches.ModernStandardBatchCount;",
            "LastStats.ReferenceModernStandardInstances = _opaqueBatches.ModernStandardInstanceCount;",
            "var packetCensusDrawCursor = 0;");
        Assert.DoesNotContain("ReferenceModernStandardBatches++", draw, StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceModernStandardInstances +=", draw, StringComparison.Ordinal);
    }

    [Fact]
    public void ModernTelemetrySurvivesSnapshotTraceAndAggregateReset()
    {
        var stats = new WorldRenderStats
        {
            ReferenceModernStandardShaderActive = true,
            ReferenceModernStandardBatches = 17,
            ReferenceModernStandardInstances = 4096
        };

        var snapshot = stats.Snapshot();
        Assert.True(snapshot.ReferenceModernStandardShaderActive);
        Assert.Equal(17, snapshot.ReferenceModernStandardBatches);
        Assert.Equal(4096, snapshot.ReferenceModernStandardInstances);

        var fields = RendererProfilerTrace.StatsFields("refs.", snapshot);
        Assert.True(Assert.IsType<bool>(fields["refs.refModernStandardShaderActive"]));
        Assert.Equal(17, Assert.IsType<int>(fields["refs.refModernStandardBatches"]));
        Assert.Equal(4096, Assert.IsType<int>(fields["refs.refModernStandardInstances"]));

        stats.Reset();
        Assert.False(stats.ReferenceModernStandardShaderActive);
        Assert.Equal(0, stats.ReferenceModernStandardBatches);
        Assert.Equal(0, stats.ReferenceModernStandardInstances);

        var accumulator = SourceContract.ReadAppSource("FrameProfileAccumulator.cs");
        var add = SourceContract.Extract(accumulator, "internal void Add(", "internal bool TryFlush(");
        var reset = SourceContract.Extract(
            accumulator,
            "private void Reset()",
            "private static void IncrementHistogram");

        Assert.Contains("if (references.ReferenceModernStandardShaderActive)", add,
            StringComparison.Ordinal);
        Assert.Contains("_refModernStandardShaderActiveFrames++;", add, StringComparison.Ordinal);
        Assert.Contains("_refModernStandardBatches += references.ReferenceModernStandardBatches;", add,
            StringComparison.Ordinal);
        Assert.Contains("_refModernStandardInstances += references.ReferenceModernStandardInstances;", add,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"refsModernStandardShaderActiveRate\"] =\n" +
            "                Rate(_refModernStandardShaderActiveFrames, _refSampleFrames),",
            accumulator,
            StringComparison.Ordinal);
        Assert.Contains("[\"refsModernStandardBatchesAvg\"] = Avg(_refModernStandardBatches),",
            accumulator, StringComparison.Ordinal);
        Assert.Contains("[\"refsModernStandardInstancesAvg\"] = Avg(_refModernStandardInstances),",
            accumulator, StringComparison.Ordinal);
        Assert.Contains("modern={Avg(_refModernStandardBatches):0.0}/{Avg(_refModernStandardInstances):0.0}",
            accumulator, StringComparison.Ordinal);
        Assert.Contains("_refModernStandardShaderActiveFrames = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_refModernStandardBatches = 0;", reset, StringComparison.Ordinal);
        Assert.Contains("_refModernStandardInstances = 0;", reset, StringComparison.Ordinal);
    }

    private static bool IsModernStandard(ShaderPermutation permutation) =>
        permutation.Macros.Any(macro => macro.Name == ModernMacro && macro.Definition == "1");

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
