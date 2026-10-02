using System.Diagnostics;
using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Creates the fixed and lazy reference pipelines used by <c>ReferenceRenderer12</c> and native
///     Bethesda scene viewers. Shared families own every native pipeline; callers borrow the
///     established opaque, cutout, mirror, shadow and game-specialization handles.
/// </summary>
/// <remarks>This factory records no commands. Callers retain the device and root and establish GPU
/// retirement before replacing a profile or disposing the factory on its creating thread.</remarks>
internal sealed class ReferencePipelineFactory12 : IDisposable
{
    private readonly GpuDevice12 _gpu;
    private readonly GpuRootSignature12 _rootSignature;

    // Blend shader routes. The shared reference pair is the always-active default route; a per-game
    // pair becomes a route that is active only while its shaders compiled, so the fallback stays
    // structural. Each route owns PSO caches SEPARATE from every other route's — the blend keys are
    // identical between routes, only the shaders differ, so one shared cache would hand back another
    // route's pipeline after the first draw. Adding per-game pair #2 = one more route field + one
    // more Set method.
    private readonly ReferenceShaderRoute12 _sharedRoute;
    private readonly ReferenceShaderRoute12 _grassRoute;

    // The INSTANCED + BLENDED grass route. Its shaders are the same per-game grass pair as
    // _grassRoute, compiled with GRASS_INSTANCED so the VS takes its world matrix from the t8
    // instance buffer instead of the head of b1. It exists because TES4 grass authors NiAlphaProperty
    // 0x12ED (blend AND test) — one bit different from FNV's 0x12EC — and blend is forced with no
    // policy layer, so the whole carpet was stuck on the per-draw path at roughly one draw, one
    // 256-byte ring allocation and three full list re-scans per blade. Nothing about the PSO itself
    // is new: blend and instancing are argument values on the same CreatePipelineState, over the same
    // root signature and the same input layout.
    private readonly ReferenceShaderRoute12 _instancedGrassBlendRoute;

    // The INSTANCED grass route. Separate from _grassRoute because the ABIs differ: _grassRoute
    // serves blended per-draw PSOs (world matrix from the per-draw CB) while these shaders take
    // SV_InstanceID + the t8 instance buffer. Built lazily because the profile is only known once a
    // game loads, which is after this constructor runs.
    private GameShaderPair _instancedGrassProfile;
    private bool _instancedGrassCompileAttempted;
    private bool _instancedGrassProfileChangePending;
    private ShaderPipelineResources? _instancedGrassPipelineResources;
    private ID3D12PipelineState? _grassOpaqueBackA2CPso;
    private ID3D12PipelineState? _grassOpaqueDoubleA2CPso;

    // Exact-quality A/B: only a fail-closed classifier can select these PSOs. They are optional
    // resources so a compile/driver rejection leaves every batch on the established uber shader.
    private ID3D12PipelineState? _modernStandardBackPso;
    private ID3D12PipelineState? _modernStandardBackCutoutPso;
    private ID3D12PipelineState? _modernStandardDoubleCutoutPso;
    private ID3D12PipelineState? _starfieldDiffuseLitBackPso;
    private ID3D12PipelineState? _starfieldDiffuseLitDoublePso;
    private ID3D12PipelineState? _starfieldDiffuseLitBackCutoutPso;
    private ID3D12PipelineState? _starfieldDiffuseLitDoubleCutoutPso;
    // Native Mesh/NPC viewer twins use reference.vert.hlsl's per-draw b1 world matrix. They cannot
    // alias the established instanced family: its VS reads SV_InstanceID + t8 and has a different
    // stage/input ABI even though the compact pixel signatures are identical.
    private ID3D12PipelineState? _directModernStandardBackPso;
    private ID3D12PipelineState? _directModernStandardBackCutoutPso;
    private ID3D12PipelineState? _directModernStandardDoubleCutoutPso;
    private ID3D12PipelineState? _directStarfieldDiffuseLitBackPso;
    private ID3D12PipelineState? _directStarfieldDiffuseLitDoublePso;
    private ID3D12PipelineState? _directStarfieldDiffuseLitBackCutoutPso;
    private ID3D12PipelineState? _directStarfieldDiffuseLitDoubleCutoutPso;
    private ID3D12PipelineState? _directClassicSkinBackPso;
    private ID3D12PipelineState? _directClassicSkinDoublePso;
    private ReadOnlyMemory<byte>? _directClassicSkinVertexShader;
    private ID3D12PipelineState? _directClassicSkinFactorOneBackPso;
    private ID3D12PipelineState? _directClassicSkinFactorOneDoublePso;
    private readonly ShaderPipelineCache<(bool DoubleSided, bool FactorOne)> _independentSkinPipelines;
    private ID3D12PipelineState? _oblivionEyePso;

    // Borrowed PSO fields may alias; only the retained Shared families own native handles.
    private readonly List<ShaderPipelineResources> _pipelineFamilies = new(12);
    private ShaderPipelineResources? _pendingPipelineRelease;

    // Construction retains owners before compiling or allocating pipelines. Runtime retirement can
    // retry failed child releases, but a throwing constructor cannot publish its owner for a retry.
    private readonly ReferencePipelineConstructionTransaction12? _constructionTransaction = new();

    // DXBC precompilation removes FXC from startup, but CreateGraphicsPipelineState can still ask
    // the display driver to compile/link a hardware pipeline. Keep that cost separate from shader
    // lookup and scene materialization: native-viewer captures have shown multi-minute gaps after
    // the final shipped-DXBC hit, while ordinary profiler starts complete this same factory in a
    // few milliseconds. These counters also cover lazy blend/grass PSOs created after startup.
    private int _psoCreationCount;
    private double _psoCreationMilliseconds;
    private double _maxPsoCreationMilliseconds;

    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private RetiredResourceDisposal? _retiredResources;
    private bool _disposed;

    /// <summary>Creates fixed reference pipelines and retains empty Shared blend caches before native setup.</summary>
    /// <param name="gpu">Borrowed device and scene sampling configuration.</param>
    /// <param name="rootSignature">World root retained by every fixed family and dynamic cache entry.</param>
    /// <param name="game">Game selecting the existing optional shader specializations.</param>
    /// <remarks>Construction rolls back unpublished resources; a rollback failure cannot publish a retry owner.
    /// Runtime disposal retains failed child releases for retry on the creating thread.</remarks>
    public ReferencePipelineFactory12(
        GpuDevice12 gpu,
        GpuRootSignature12 rootSignature,
        BethesdaGame game)
    {
        var constructionStarted = Stopwatch.GetTimestamp();
        var constructionSucceeded = false;
        _gpu = gpu;
        _rootSignature = rootSignature;
        try
        {
        _grassRoute = TrackConstructionResource(new ReferenceShaderRoute12(rootSignature));
        _instancedGrassBlendRoute = TrackConstructionResource(new ReferenceShaderRoute12(rootSignature));
        _independentSkinPipelines = TrackConstructionResource(
            rootSignature.CreatePipelineCache<(bool DoubleSided, bool FactorOne)>());
        var fixedPipelineResources = RetainPipelineFamily(26);
        var shaderOverride =
            EnvironmentVariables.Get(EnvironmentVariables.Viewer.ReferenceModernStandardShader);
        var shaderActivation = ModernStandardShaderActivationPolicy.Resolve(game, shaderOverride);
        FalloutModernStandardRequested = shaderActivation.FalloutModernStandardRequested;
        StarfieldDiffuseLitRequested = shaderActivation.StarfieldDiffuseLitRequested;
        DirectModernStandardOpaqueRequested = FalloutModernStandardRequested;
        DirectStarfieldDiffuseLitRequested = StarfieldDiffuseLitRequested;
        DirectClassicSkinRequested = game == BethesdaGame.Oblivion;

        var blendedVsBytecode = CompileEmbeddedShader("reference.vert.hlsl", "main", "vs_5_1");
        var instancedVsBytecode = CompileEmbeddedShader("reference_instanced.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("reference.frag.hlsl", "main", "ps_5_1");
        _sharedRoute = TrackConstructionResource(
            new ReferenceShaderRoute12(rootSignature, blendedVsBytecode, psBytecode));
        var basePsoGroup = BeginPsoGroup("base-opaque", game);
        // Standalone Bethesda scenes carry one authored world transform per mesh part rather than
        // thousands of repeated placements. Reuse the established per-draw vertex ABI for their
        // opaque path instead of manufacturing one-element instance buffers in the viewer.
        DirectOpaqueBackPso = CreatePipelineState(fixedPipelineResources, 0,
                blendedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
        DirectOpaqueDoublePso = CreatePipelineState(fixedPipelineResources, 1,
                blendedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));
        DirectOpaqueBackDecalPso = CreatePipelineState(fixedPipelineResources, 2,
                blendedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true, Decal: true));
        DirectOpaqueDoubleDecalPso = CreatePipelineState(fixedPipelineResources, 3,
                blendedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true, Decal: true));
        DirectOpaqueBackNoDepthPso = CreatePipelineState(fixedPipelineResources, 4,
                blendedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: false, DepthTestEnabled: false));
        DirectOpaqueDoubleNoDepthPso = CreatePipelineState(fixedPipelineResources, 5,
                blendedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: false, DepthTestEnabled: false));
        DirectOpaqueBackDecalNoDepthPso = CreatePipelineState(fixedPipelineResources, 6,
                blendedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: false, Decal: true, DepthTestEnabled: false));
        DirectOpaqueDoubleDecalNoDepthPso = CreatePipelineState(fixedPipelineResources, 7,
                blendedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: false, Decal: true, DepthTestEnabled: false));
        OpaqueBackPso = CreatePipelineState(fixedPipelineResources, 8,
                instancedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
        OpaqueDoublePso = CreatePipelineState(fixedPipelineResources, 9,
                instancedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));
        OpaqueBackDecalPso = CreatePipelineState(fixedPipelineResources, 10,
                instancedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true, Decal: true));
        OpaqueDoubleDecalPso = CreatePipelineState(fixedPipelineResources, 11,
                instancedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true, Decal: true));
        CompletePsoGroup("base-opaque", game, basePsoGroup);

        if (FalloutModernStandardRequested)
        {
            var specializationPsoGroup = BeginPsoGroup("fallout-specializations", game);
            TryCreateModernStandardOpaquePipelines();
            TryCreateDirectModernStandardOpaquePipelines();
            CompletePsoGroup("fallout-specializations", game, specializationPsoGroup);
        }
        if (StarfieldDiffuseLitRequested)
        {
            var specializationPsoGroup = BeginPsoGroup("starfield-specializations", game);
            TryCreateStarfieldDiffuseLitPipelines();
            TryCreateDirectStarfieldDiffuseLitPipelines();
            CompletePsoGroup("starfield-specializations", game, specializationPsoGroup);
        }
        if (DirectClassicSkinRequested)
        {
            var specializationPsoGroup = BeginPsoGroup("classic-skin-specialization", game);
            TryCreateDirectClassicSkinPipelines();
            CompletePsoGroup("classic-skin-specialization", game, specializationPsoGroup);
        }

        BethesdaMultitool.Core.Diagnostics.Logger.Instance.Info(
            "ReferencePipelineFactory12: opaque shader profile game={0} override={1} " +
            "falloutRequested={2} falloutAvailable={3} falloutDirectAvailable={4} " +
            "starfieldRequested={5} starfieldAvailable={6} starfieldDirectAvailable={7} " +
            "classicSkinRequested={8} classicSkinDirectAvailable={9}.",
            game,
            shaderOverride ?? "<unset>",
            FalloutModernStandardRequested,
            ModernStandardOpaqueAvailable,
            DirectModernStandardOpaqueAvailable,
            StarfieldDiffuseLitRequested,
            StarfieldDiffuseLitOpaqueAvailable,
            DirectStarfieldDiffuseLitOpaqueAvailable,
            DirectClassicSkinRequested,
            DirectClassicSkinAvailable);

        // Grass cutouts and native-viewer hair/brow/lash: alpha-to-coverage variants (engine
        // mechanism — BSRenderState::SetAlphaToCoverageEnable) so MSAA converts authored alpha
        // into per-sample coverage instead of stacking transparent cards. The direct viewer set
        // retains the full culling/decal/depth-test matrix; the placed-world set keeps its existing
        // two variants. When the scene is single-sampled all A2C properties alias the corresponding
        // plain PSOs. The viewer detects that case and explicitly routes native A2C through blend.
        AlphaToCoverageAvailable = _gpu.SceneSampleCount > 1;
        if (AlphaToCoverageAvailable)
        {
            var a2cPsBytecode = CompileEmbeddedShader("reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("ALPHA_TO_COVERAGE", "1"));
            var alphaToCoveragePsoGroup = BeginPsoGroup("alpha-to-coverage", game);
            DirectOpaqueBackA2CPso = CreatePipelineState(fixedPipelineResources, 12,
                blendedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true, AlphaToCoverage: true));
            DirectOpaqueDoubleA2CPso = CreatePipelineState(fixedPipelineResources, 13,
                blendedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true, AlphaToCoverage: true));
            DirectOpaqueBackDecalA2CPso = CreatePipelineState(fixedPipelineResources, 14,
                blendedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true, Decal: true, AlphaToCoverage: true));
            DirectOpaqueDoubleDecalA2CPso = CreatePipelineState(fixedPipelineResources, 15,
                blendedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true, Decal: true, AlphaToCoverage: true));
            DirectOpaqueBackNoDepthA2CPso = CreatePipelineState(fixedPipelineResources, 16,
                blendedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true, AlphaToCoverage: true, DepthTestEnabled: false));
            DirectOpaqueDoubleNoDepthA2CPso = CreatePipelineState(fixedPipelineResources, 17,
                blendedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true, AlphaToCoverage: true, DepthTestEnabled: false));
            DirectOpaqueBackDecalNoDepthA2CPso = CreatePipelineState(fixedPipelineResources, 18,
                blendedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true, Decal: true, AlphaToCoverage: true, DepthTestEnabled: false));
            DirectOpaqueDoubleDecalNoDepthA2CPso = CreatePipelineState(fixedPipelineResources, 19,
                blendedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true, Decal: true, AlphaToCoverage: true, DepthTestEnabled: false));
            OpaqueBackA2CPso = CreatePipelineState(fixedPipelineResources, 20,
                instancedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true, AlphaToCoverage: true));
            OpaqueDoubleA2CPso = CreatePipelineState(fixedPipelineResources, 21,
                instancedVsBytecode, a2cPsBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true, AlphaToCoverage: true));
            CompletePsoGroup("alpha-to-coverage", game, alphaToCoveragePsoGroup);
        }
        else
        {
            DirectOpaqueBackA2CPso = DirectOpaqueBackPso;
            DirectOpaqueDoubleA2CPso = DirectOpaqueDoublePso;
            DirectOpaqueBackDecalA2CPso = DirectOpaqueBackDecalPso;
            DirectOpaqueDoubleDecalA2CPso = DirectOpaqueDoubleDecalPso;
            DirectOpaqueBackNoDepthA2CPso = DirectOpaqueBackNoDepthPso;
            DirectOpaqueDoubleNoDepthA2CPso = DirectOpaqueDoubleNoDepthPso;
            DirectOpaqueBackDecalNoDepthA2CPso = DirectOpaqueBackDecalNoDepthPso;
            DirectOpaqueDoubleDecalNoDepthA2CPso = DirectOpaqueDoubleDecalNoDepthPso;
            OpaqueBackA2CPso = OpaqueBackPso;
            OpaqueDoubleA2CPso = OpaqueDoublePso;
        }

        // Sun-shadow depth pass: the instanced VS replayed against the light's viewProj into a
        // depth-only target — compiled as the SHADOW_CARD_LIGHT_FACING variant, whose extended b0
        // carries a light-perpendicular leaf-card basis (camera-facing cards can be edge-on to the
        // sun and cast nothing). Opaque batches need no pixel shader at all; alpha-tested batches
        // (foliage/leaf cards) run a minimal PS that discards the transparent cutout texels.
        var shadowVsBytecode = CompileEmbeddedShader("reference_instanced.vert.hlsl", "main", "vs_5_1",
            new ShaderMacro("SHADOW_CARD_LIGHT_FACING", "1"));
        var shadowPsBytecode = CompileEmbeddedShader("shadow.frag.hlsl", "main", "ps_5_1");
        var shadowPsoGroup = BeginPsoGroup("shadow", game);
        ShadowOpaquePso = CreateShadowPipelineState(fixedPipelineResources, 22, shadowVsBytecode, psBytecode: null);
        ShadowAlphaTestPso = CreateShadowPipelineState(fixedPipelineResources, 23, shadowVsBytecode, shadowPsBytecode);
        CompletePsoGroup("shadow", game, shadowPsoGroup);

        // Mirror-winding twins for the water-reflection color replay: only the BACK-CULLED opaque
        // PSOs need one (a mirrored viewProj flips screen-space winding); CullMode.None PSOs are
        // winding-agnostic and map to themselves. Decals are excluded from the replay entirely.
        var mirrorPsoGroup = BeginPsoGroup("mirror", game);
        var mirrorBack = CreatePipelineState(fixedPipelineResources, 24,
                instancedVsBytecode, psBytecode, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true, MirrorWinding: true));
        _mirrorPsoMap = new Dictionary<ID3D12PipelineState, ID3D12PipelineState>
        {
            [OpaqueBackPso] = mirrorBack,
        };
        // Mirror capture is deliberately kept on the established uber shader. Its per-draw state
        // is identical, while the generic twins already encode the winding/clip-plane contract.
        if (_modernStandardBackPso is not null)
        {
            _mirrorPsoMap[_modernStandardBackPso] = mirrorBack;
        }
        if (_modernStandardBackCutoutPso is not null)
        {
            _mirrorPsoMap[_modernStandardBackCutoutPso] = mirrorBack;
        }
        if (_modernStandardDoubleCutoutPso is not null)
        {
            _mirrorPsoMap[_modernStandardDoubleCutoutPso] = OpaqueDoublePso;
        }
        if (_starfieldDiffuseLitBackPso is not null)
        {
            _mirrorPsoMap[_starfieldDiffuseLitBackPso] = mirrorBack;
        }
        if (_starfieldDiffuseLitDoublePso is not null)
        {
            _mirrorPsoMap[_starfieldDiffuseLitDoublePso] = OpaqueDoublePso;
        }
        if (_starfieldDiffuseLitBackCutoutPso is not null)
        {
            _mirrorPsoMap[_starfieldDiffuseLitBackCutoutPso] = mirrorBack;
        }
        if (_starfieldDiffuseLitDoubleCutoutPso is not null)
        {
            _mirrorPsoMap[_starfieldDiffuseLitDoubleCutoutPso] = OpaqueDoublePso;
        }
        if (AlphaToCoverageAvailable && !ReferenceEquals(OpaqueBackA2CPso, OpaqueBackPso))
        {
            var a2cPsBytecodeMirror = CompileEmbeddedShader("reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("ALPHA_TO_COVERAGE", "1"));
            var mirrorA2C = CreatePipelineState(fixedPipelineResources, 25,
                instancedVsBytecode, a2cPsBytecodeMirror, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true, AlphaToCoverage: true, MirrorWinding: true));
            _mirrorPsoMap[OpaqueBackA2CPso] = mirrorA2C;
        }
        CompletePsoGroup("mirror", game, mirrorPsoGroup);

        _constructionTransaction!.Commit();
        _constructionTransaction = null;
        constructionSucceeded = true;
        }
        catch (Exception creationError)
        {
            try
            {
                _constructionTransaction?.Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Reference pipeline construction and rollback failed; unpublished owners cannot be retried by the caller.",
                    creationError, cleanupError);
            }
            _constructionTransaction = null;
            throw;
        }
        finally
        {
            var totalMilliseconds = Stopwatch.GetElapsedTime(constructionStarted).TotalMilliseconds;
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Info(
                "ReferencePipelineFactory12: construction timing game={0} outcome={1} total={2:F2} ms " +
                "driverPso={3:F2} ms count={4} maxSingle={5:F2} ms.",
                game,
                constructionSucceeded ? "ready" : "failed",
                totalMilliseconds,
                _psoCreationMilliseconds,
                _psoCreationCount,
                _maxPsoCreationMilliseconds);
            RendererProfilerTrace.Event("d3d12-reference-pso-construction", new Dictionary<string, object?>
            {
                ["game"] = game.ToString(),
                ["outcome"] = constructionSucceeded ? "ready" : "failed",
                ["totalMilliseconds"] = totalMilliseconds,
                ["psoCreationMilliseconds"] = _psoCreationMilliseconds,
                ["psoCreationCount"] = _psoCreationCount,
                ["maxSinglePsoMilliseconds"] = _maxPsoCreationMilliseconds,
            });
        }
    }

    // Original opaque PSO -> winding-flipped twin for the mirrored reflection replay.
    private readonly Dictionary<ID3D12PipelineState, ID3D12PipelineState> _mirrorPsoMap;

    /// <summary>The winding-flipped twin of <paramref name="original" /> for a mirrored view, or
    /// the original itself when it is winding-agnostic (double-sided / CullMode.None).</summary>
    public ID3D12PipelineState GetMirrorPso(ID3D12PipelineState original) =>
        _mirrorPsoMap.TryGetValue(original, out var mirror) ? mirror : original;

    /// <summary>Instanced opaque PSO with back-face culling (single-sided submeshes).</summary>
    public ID3D12PipelineState OpaqueBackPso { get; }

    /// <summary>Instanced opaque PSO with no culling (double-sided submeshes).</summary>
    public ID3D12PipelineState OpaqueDoublePso { get; }

    /// <summary>Depth-biased variant of <see cref="OpaqueBackPso" /> for decal overlay submeshes.</summary>
    public ID3D12PipelineState OpaqueBackDecalPso { get; }

    /// <summary>Depth-biased variant of <see cref="OpaqueDoublePso" /> for decal overlay submeshes.</summary>
    public ID3D12PipelineState OpaqueDoubleDecalPso { get; }

    /// <summary>Non-instanced opaque PSOs sharing the reference per-draw b1 ABI. These are used by
    /// the native Mesh/NPC viewer, whose scene parts already have distinct authored transforms.</summary>
    public ID3D12PipelineState DirectOpaqueBackPso { get; }

    public ID3D12PipelineState DirectOpaqueDoublePso { get; }

    public ID3D12PipelineState DirectOpaqueBackDecalPso { get; }

    public ID3D12PipelineState DirectOpaqueDoubleDecalPso { get; }

    public ID3D12PipelineState DirectOpaqueBackNoDepthPso { get; }

    public ID3D12PipelineState DirectOpaqueDoubleNoDepthPso { get; }

    public ID3D12PipelineState DirectOpaqueBackDecalNoDepthPso { get; }

    public ID3D12PipelineState DirectOpaqueDoubleDecalNoDepthPso { get; }

    /// <summary>Non-instanced depth-writing A2C variants for native viewer hair/brow/lash geometry.</summary>
    public ID3D12PipelineState DirectOpaqueBackA2CPso { get; }

    public ID3D12PipelineState DirectOpaqueDoubleA2CPso { get; }

    public ID3D12PipelineState DirectOpaqueBackDecalA2CPso { get; }

    public ID3D12PipelineState DirectOpaqueDoubleDecalA2CPso { get; }

    public ID3D12PipelineState DirectOpaqueBackNoDepthA2CPso { get; }

    public ID3D12PipelineState DirectOpaqueDoubleNoDepthA2CPso { get; }

    public ID3D12PipelineState DirectOpaqueBackDecalNoDepthA2CPso { get; }

    public ID3D12PipelineState DirectOpaqueDoubleDecalNoDepthA2CPso { get; }

    public ID3D12PipelineState GetDirectAlphaToCoveragePipeline(
        bool doubleSided,
        bool decal,
        bool depthTestOff = false) =>
        (doubleSided, decal, depthTestOff) switch
        {
            (true, true, true) => DirectOpaqueDoubleDecalNoDepthA2CPso,
            (true, false, true) => DirectOpaqueDoubleNoDepthA2CPso,
            (false, true, true) => DirectOpaqueBackDecalNoDepthA2CPso,
            (false, false, true) => DirectOpaqueBackNoDepthA2CPso,
            (true, true, false) => DirectOpaqueDoubleDecalA2CPso,
            (true, false, false) => DirectOpaqueDoubleA2CPso,
            (false, true, false) => DirectOpaqueBackDecalA2CPso,
            _ => DirectOpaqueBackA2CPso,
        };

    public ID3D12PipelineState GetDirectOpaquePipeline(
        bool doubleSided,
        bool decal,
        bool depthTestOff = false) =>
        (doubleSided, decal, depthTestOff) switch
        {
            (true, true, true) => DirectOpaqueDoubleDecalNoDepthPso,
            (true, false, true) => DirectOpaqueDoubleNoDepthPso,
            (false, true, true) => DirectOpaqueBackDecalNoDepthPso,
            (false, false, true) => DirectOpaqueBackNoDepthPso,
            (true, true, false) => DirectOpaqueDoubleDecalPso,
            (true, false, false) => DirectOpaqueDoublePso,
            (false, true, false) => DirectOpaqueBackDecalPso,
            _ => DirectOpaqueBackPso,
        };

    /// <summary>True when the scene target is multisampled and the A2C PSOs are distinct variants;
    /// false = <see cref="OpaqueBackA2CPso" />/<see cref="OpaqueDoubleA2CPso" /> alias the plain PSOs.</summary>
    public bool AlphaToCoverageAvailable { get; }

    /// <summary>Alpha-to-coverage variant of <see cref="OpaqueBackPso" /> (grass cutout edges).</summary>
    public ID3D12PipelineState OpaqueBackA2CPso { get; }

    /// <summary>Alpha-to-coverage variant of <see cref="OpaqueDoublePso" /> (grass cutout edges).</summary>
    public ID3D12PipelineState OpaqueDoubleA2CPso { get; }

    /// <summary>Effective Fallout 4/76 opt-in resolved when this game-specific factory was built.</summary>
    public bool FalloutModernStandardRequested { get; }

    /// <summary>Effective Starfield default/override resolved when this game-specific factory was built.</summary>
    public bool StarfieldDiffuseLitRequested { get; }

    /// <summary>Whether the native direct-viewer Fallout specialization was requested. This is an
    /// explicit API (rather than making viewer integration infer it from the instanced family).</summary>
    public bool DirectModernStandardOpaqueRequested { get; }

    /// <summary>Whether the native direct-viewer Starfield specialization was requested.</summary>
    public bool DirectStarfieldDiffuseLitRequested { get; }

    /// <summary>Whether this game owns Oblivion's native direct-viewer SKIN2000 specialization.</summary>
    public bool DirectClassicSkinRequested { get; }

    /// <summary>True only when the A/B was requested and all three specialized PSOs were created.</summary>
    public bool ModernStandardOpaqueAvailable =>
        _modernStandardBackPso is not null &&
        _modernStandardBackCutoutPso is not null &&
        _modernStandardDoubleCutoutPso is not null;

    /// <summary>Resolves a classifier-approved specialized PSO; false preserves the uber path.</summary>
    public bool TryGetModernStandardOpaquePso(
        ModernStandardOpaqueShaderVariant variant,
        out ID3D12PipelineState? pso)
    {
        pso = variant switch
        {
            ModernStandardOpaqueShaderVariant.SingleSidedOpaque => _modernStandardBackPso,
            ModernStandardOpaqueShaderVariant.SingleSidedGreaterCutout => _modernStandardBackCutoutPso,
            ModernStandardOpaqueShaderVariant.DoubleSidedGreaterCutout => _modernStandardDoubleCutoutPso,
            _ => null
        };
        return pso is not null;
    }

    /// <summary>True only when all four fail-soft Starfield diffuse-lit PSOs were created.</summary>
    public bool StarfieldDiffuseLitOpaqueAvailable =>
        _starfieldDiffuseLitBackPso is not null &&
        _starfieldDiffuseLitDoublePso is not null &&
        _starfieldDiffuseLitBackCutoutPso is not null &&
        _starfieldDiffuseLitDoubleCutoutPso is not null;

    /// <summary>Resolves a future policy-approved Starfield variant; false preserves the uber path.</summary>
    public bool TryGetStarfieldDiffuseLitPso(
        bool alphaGreater,
        bool doubleSided,
        out ID3D12PipelineState? pso)
    {
        pso = (alphaGreater, doubleSided) switch
        {
            (false, false) => _starfieldDiffuseLitBackPso,
            (false, true) => _starfieldDiffuseLitDoublePso,
            (true, false) => _starfieldDiffuseLitBackCutoutPso,
            (true, true) => _starfieldDiffuseLitDoubleCutoutPso
        };
        return pso is not null;
    }

    /// <summary>True only when all three ordinary depth-writing, non-blended direct Fallout PSOs
    /// were published as one family.</summary>
    public bool DirectModernStandardOpaqueAvailable =>
        _directModernStandardBackPso is not null &&
        _directModernStandardBackCutoutPso is not null &&
        _directModernStandardDoubleCutoutPso is not null;

    /// <summary>Resolves a classifier-approved native-viewer Fallout PSO. Unsupported variants and
    /// a family compile failure return false so the caller can keep the direct uber shader.</summary>
    public bool TryGetDirectModernStandardOpaquePso(
        ModernStandardOpaqueShaderVariant variant,
        out ID3D12PipelineState? pso)
    {
        pso = variant switch
        {
            ModernStandardOpaqueShaderVariant.SingleSidedOpaque => _directModernStandardBackPso,
            ModernStandardOpaqueShaderVariant.SingleSidedGreaterCutout => _directModernStandardBackCutoutPso,
            ModernStandardOpaqueShaderVariant.DoubleSidedGreaterCutout => _directModernStandardDoubleCutoutPso,
            _ => null
        };
        return pso is not null;
    }

    /// <summary>True only when all four ordinary depth-writing, non-blended direct Starfield PSOs
    /// were published as one family.</summary>
    public bool DirectStarfieldDiffuseLitOpaqueAvailable =>
        _directStarfieldDiffuseLitBackPso is not null &&
        _directStarfieldDiffuseLitDoublePso is not null &&
        _directStarfieldDiffuseLitBackCutoutPso is not null &&
        _directStarfieldDiffuseLitDoubleCutoutPso is not null;

    /// <summary>Resolves a policy-approved native-viewer Starfield PSO; false keeps the direct uber
    /// shader without ever crossing into the instanced VS ABI.</summary>
    public bool TryGetDirectStarfieldDiffuseLitPso(
        bool alphaGreater,
        bool doubleSided,
        out ID3D12PipelineState? pso)
    {
        pso = (alphaGreater, doubleSided) switch
        {
            (false, false) => _directStarfieldDiffuseLitBackPso,
            (false, true) => _directStarfieldDiffuseLitDoublePso,
            (true, false) => _directStarfieldDiffuseLitBackCutoutPso,
            (true, true) => _directStarfieldDiffuseLitDoubleCutoutPso
        };
        return pso is not null;
    }

    /// <summary>True only when both culling forms of the native Oblivion SKIN2000 PSO exist.</summary>
    public bool DirectClassicSkinAvailable =>
        _directClassicSkinBackPso is not null &&
        _directClassicSkinDoublePso is not null;

    /// <summary>
    ///     Resolves an <c>IsFaceGen</c>-approved native-viewer classic-skin PSO. Failure is explicit
    ///     so the caller can retain the generic material shader without a partial family.
    /// </summary>
    public bool TryGetDirectClassicSkinPso(
        bool doubleSided,
        out ID3D12PipelineState? pso)
    {
        pso = doubleSided
            ? _directClassicSkinDoublePso
            : _directClassicSkinBackPso;
        return pso is not null;
    }

    /// <summary>
    ///     Lazily resolves the explicitly requested diagnostic. Ordinary sessions never call this
    ///     method. Its PSOs are separate cache entries; creation failure propagates to the requesting
    ///     session instead of silently substituting an ordinary image for the diagnostic.
    /// </summary>
    public ID3D12PipelineState GetDirectClassicSkinFactorOnePso(bool doubleSided)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!DirectClassicSkinRequested || !DirectClassicSkinAvailable ||
            _directClassicSkinVertexShader is null)
        {
            throw new InvalidOperationException(
                "The skin factor-one diagnostic requires the complete Oblivion classic-skin pair.");
        }

        if (_directClassicSkinFactorOneBackPso is null || _directClassicSkinFactorOneDoublePso is null)
        {
            CreateDirectClassicSkinFactorOnePipelines(_directClassicSkinVertexShader.Value);
        }

        return (doubleSided ? _directClassicSkinFactorOneDoublePso : _directClassicSkinFactorOneBackPso)
            ?? throw new InvalidOperationException("The skin factor-one diagnostic pipeline is unavailable.");
    }

    /// <summary>Returns the retained ordinary-skin state with the requested independent-albedo pixel entry.</summary>
    /// <param name="doubleSided">Disables culling for the requested skin pipeline.</param>
    /// <param name="factorOne">Uses the independent-albedo factor-one diagnostic pixel entry.</param>
    /// <returns>A borrowed Shared-cached handle, with no compilation or native creation on a cache hit.</returns>
    internal ID3D12PipelineState GetDirectClassicSkinIndependentPso(bool doubleSided, bool factorOne)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!DirectClassicSkinRequested || !DirectClassicSkinAvailable || _directClassicSkinVertexShader is null)
        {
            throw new InvalidOperationException("Independent skin sampling requires the complete Oblivion skin pipeline.");
        }
        var key = (DoubleSided: doubleSided, FactorOne: factorOne);
        return _independentSkinPipelines.GetOrCreate(key, (self: this, key), static (state, family) =>
        {
            var pixelShader = CompileEmbeddedShader(
                state.key.FactorOne ? "reference_classic_skin_independent_factor_one.frag.hlsl" : "reference_classic_skin_independent.frag.hlsl",
                state.key.FactorOne ? "mainIndependentFactorOne" : "mainIndependent", "ps_5_1");
            return state.self.CreatePipelineState(family, 0,
                state.self._directClassicSkinVertexShader!.Value, pixelShader,
                new ReferencePipelineRenderState12(state.key.DoubleSided, null, DepthWriteEnabled: true));
        });
    }

    /// <summary>Returns the ordinary TES4 eye overlay: ONE/ONE blending, depth testing and no depth write or stencil.</summary>
    /// <returns>A borrowed handle retained by its own Shared family.</returns>
    internal ID3D12PipelineState GetOblivionEyePso()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_oblivionEyePso is not null) { return _oblivionEyePso; }
        var pipelineResources = RetainPipelineFamily(1);
        var published = false;
        try
        {
            var vertexShader = CompileEmbeddedShader("reference_oblivion_eye.vert.hlsl", "main", "vs_5_1");
            var pixelShader = CompileEmbeddedShader("reference_oblivion_eye.frag.hlsl", "main", "ps_5_1");
            var blend = new D12.RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = D12.Blend.One,
                DestinationBlend = D12.Blend.One,
                BlendOperation = D12.BlendOperation.Add,
                SourceBlendAlpha = D12.Blend.One,
                DestinationBlendAlpha = D12.Blend.One,
                BlendOperationAlpha = D12.BlendOperation.Add,
                RenderTargetWriteMask = D12.ColorWriteEnable.All
            };
            _oblivionEyePso = CreatePipelineState(pipelineResources, 0, vertexShader, pixelShader,
                new ReferencePipelineRenderState12(DoubleSided: false, BlendAttachment: blend, DepthWriteEnabled: false));
            published = true;
            return _oblivionEyePso;
        }
        finally
        {
            if (!published) { ReleaseUnpublishedPipelineFamily(pipelineResources); }
        }
    }

    /// <summary>Depth-only shadow-pass PSO for opaque batches (instanced VS, no pixel shader).</summary>
    public ID3D12PipelineState ShadowOpaquePso { get; }

    /// <summary>Depth-only shadow-pass PSO for alpha-tested batches (cutout discard PS).</summary>
    public ID3D12PipelineState ShadowAlphaTestPso { get; }

    /// <summary>
    ///     Selects the per-game grass shader pair for the loaded game, compiling it on first use.
    ///     Call once per ESM load, before any draw. A no-op when the profile is unchanged, and
    ///     FAIL-SOFT: a compile failure logs and reverts to the shared shaders rather than throwing,
    ///     because the caller's catch would take down the entire reference pipeline (and with it every
    ///     placed object) over one game's grass.
    /// </summary>
    public void SetGrassShaderProfile(GameShaderPair profile) =>
        _grassRoute.Set(profile, nameof(ReferencePipelineFactory12) + " grass route");

    /// <summary>True when a per-game grass pair compiled and is available to <c>grassRoute</c> callers.</summary>
    public bool GrassShaderAvailable => _grassRoute.Active;

    /// <summary>
    ///     Selects the per-game grass pair for the INSTANCED + BLENDED route, compiling its VS with
    ///     GRASS_INSTANCED. Call once per ESM load, before any draw; FAIL-SOFT like every other route,
    ///     so a compile failure just leaves <see cref="InstancedBlendGrassShaderAvailable" /> false and
    ///     the renderer keeps grass on the per-draw path.
    /// </summary>
    public void SetInstancedBlendGrassShaderProfile(GameShaderPair profile) =>
        _instancedGrassBlendRoute.Set(
            profile,
            nameof(ReferencePipelineFactory12) + " instanced blended grass route",
            [new ShaderMacro("GRASS_INSTANCED", "1")]);

    /// <summary>True when the instanced+blended grass pair compiled. The renderer MUST consult this
    /// before routing grass to the batch path — the two paths need different vertex shaders, so
    /// batching grass without this would draw it through a VS reading the wrong cbuffer layout.</summary>
    public bool InstancedBlendGrassShaderAvailable => _instancedGrassBlendRoute.Active;

    /// <summary>
    ///     Selects the per-game INSTANCED grass pair (the opaque cutout route FO3/FNV grass draws
    ///     through). Compilation is deferred to the first grass draw; shader compilation failure
    ///     retains the shared instanced fallback. A failed previous cleanup is retried even when
    ///     the requested profile matches the old profile.
    /// </summary>
    /// <param name="profile">Shader pair selected by the game adapter.</param>
    /// <remarks>Retire GPU users of the previous profile before calling this method on the creating thread.</remarks>
    public void SetInstancedGrassShaderProfile(GameShaderPair profile)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_instancedGrassProfileChangePending && profile.Equals(_instancedGrassProfile)) { return; }

        _instancedGrassProfileChangePending = true;
        DisposeInstancedGrassPipelines();
        _instancedGrassProfile = profile;
        _instancedGrassCompileAttempted = false;
        _instancedGrassProfileChangePending = false;
    }

    /// <summary>True when the per-game instanced grass pair compiled and its complete family is live.</summary>
    public bool InstancedGrassShaderAvailable => EnsureInstancedGrassPipelines();

    /// <summary>Returns the complete per-game grass cutout family, or the established shared A2C fallback.</summary>
    /// <param name="doubleSided">Selects the culling-disabled member of the available family.</param>
    /// <returns>A borrowed handle whose lifetime is enclosed by this factory and its current grass profile.</returns>
    public ID3D12PipelineState GetGrassCutoutPso(bool doubleSided)
    {
        if (EnsureInstancedGrassPipelines())
        {
            return doubleSided ? _grassOpaqueDoubleA2CPso! : _grassOpaqueBackA2CPso!;
        }
        return doubleSided ? OpaqueDoubleA2CPso : OpaqueBackA2CPso;
    }

    /// <summary>Builds one retained instanced grass family and publishes borrowed fields only after every variant exists.</summary>
    /// <returns>False for disabled or uncompiled profiles, retaining the ordinary instanced fallback.</returns>
    private bool EnsureInstancedGrassPipelines()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_instancedGrassProfileChangePending) { return false; }
        if (_grassOpaqueBackA2CPso is not null) { return true; }
        if (_instancedGrassCompileAttempted || !_instancedGrassProfile.Enabled) { return false; }

        var pipelineResources = RetainPipelineFamily(4);
        _instancedGrassCompileAttempted = true;
        _instancedGrassPipelineResources = pipelineResources;
        var published = false;
        try
        {
            var plain = _instancedGrassProfile.TryCompile(
                nameof(ReferencePipelineFactory12) + " instanced grass route", [], []);
            if (plain is not ({ } vs, { } ps)) { return false; }

            var back = CreatePipelineState(pipelineResources, 0, vs, ps,
                new ReferencePipelineRenderState12(DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var doubleSided = CreatePipelineState(pipelineResources, 1, vs, ps,
                new ReferencePipelineRenderState12(DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));
            var backA2C = back;
            var doubleA2C = doubleSided;
            if (AlphaToCoverageAvailable)
            {
                var a2c = _instancedGrassProfile.TryCompile(
                    nameof(ReferencePipelineFactory12) + " instanced grass A2C route",
                    [], [new ShaderMacro("ALPHA_TO_COVERAGE", "1")]);
                if (a2c is not (_, { } a2cPs)) { return false; }
                backA2C = CreatePipelineState(pipelineResources, 2, vs, a2cPs,
                    new ReferencePipelineRenderState12(DoubleSided: false, BlendAttachment: null,
                        DepthWriteEnabled: true, AlphaToCoverage: true));
                doubleA2C = CreatePipelineState(pipelineResources, 3, vs, a2cPs,
                    new ReferencePipelineRenderState12(DoubleSided: true, BlendAttachment: null,
                        DepthWriteEnabled: true, AlphaToCoverage: true));
            }

            // Single-sample coverage handles alias the plain pair; the Shared owner still owns only two PSOs.
            _grassOpaqueBackA2CPso = backA2C;
            _grassOpaqueDoubleA2CPso = doubleA2C;
            published = true;
            return true;
        }
        finally
        {
            if (!published)
            {
                ReleaseUnpublishedPipelineFamily(pipelineResources);
                _instancedGrassPipelineResources = null;
            }
        }
    }

    /// <summary>Unpublishes the caller-retired grass family before releasing it; a failed owner remains reachable for retry.</summary>
    /// <remarks>The caller must retire GPU work before replacing the profile. No fence is waited here.</remarks>
    private void DisposeInstancedGrassPipelines()
    {
        _grassOpaqueBackA2CPso = null;
        _grassOpaqueDoubleA2CPso = null;
        if (_instancedGrassPipelineResources is not null)
        {
            ReleasePipelineFamily(_instancedGrassPipelineResources);
            _instancedGrassPipelineResources = null;
        }
        RetryPendingPipelineRelease();
    }

    /// <summary>Returns the route-local authored blend pipeline with depth writes disabled.</summary>
    /// <param name="srcBlendMode">Unmodified source blend factor byte.</param>
    /// <param name="dstBlendMode">Unmodified destination blend factor byte.</param>
    /// <param name="doubleSided">Disables back-face culling.</param>
    /// <param name="decal">Applies the established coplanar depth bias.</param>
    /// <param name="grassRoute">Requests the active game-specific grass route.</param>
    /// <param name="depthTestOff">Disables hardware depth testing.</param>
    /// <param name="instancedGrass">Requests the instanced grass ABI when available.</param>
    /// <returns>A borrowed cached pipeline, creating a Shared-owned family only on a miss.</returns>
    public ID3D12PipelineState GetBlendPipeline(
        byte srcBlendMode, byte dstBlendMode, bool doubleSided, bool decal = false, bool grassRoute = false,
        bool depthTestOff = false, bool instancedGrass = false)
    {
        var route = SelectBlendRoute(grassRoute, instancedGrass);
        var key = new ReferenceBlendPipelineKey(srcBlendMode, dstBlendMode, doubleSided, decal, depthTestOff);
        return route.GetOrCreate(key, depthWrite: false, (self: this, key),
            static (state, vs, ps, family) => state.self.CreateBlendPipelineState(
                family, vs, ps, state.key, depthWriteEnabled: false));
    }

    /// <summary>Returns the route-local authored blend pipeline that also writes depth before water is drawn.</summary>
    /// <param name="srcBlendMode">Unmodified source blend factor byte.</param>
    /// <param name="dstBlendMode">Unmodified destination blend factor byte.</param>
    /// <param name="doubleSided">Disables back-face culling.</param>
    /// <param name="decal">Applies the established coplanar depth bias.</param>
    /// <param name="grassRoute">Requests the active game-specific grass route.</param>
    /// <param name="depthTestOff">Disables hardware depth testing.</param>
    /// <param name="instancedGrass">Requests the instanced grass ABI when available.</param>
    /// <returns>A borrowed cached pipeline distinct from its non-depth-writing counterpart.</returns>
    public ID3D12PipelineState GetBlendDepthWritePipeline(byte srcBlendMode, byte dstBlendMode, bool doubleSided,
        bool decal = false, bool grassRoute = false, bool depthTestOff = false, bool instancedGrass = false)
    {
        var route = SelectBlendRoute(grassRoute, instancedGrass);
        var key = new ReferenceBlendPipelineKey(srcBlendMode, dstBlendMode, doubleSided, decal, depthTestOff);
        return route.GetOrCreate(key, depthWrite: true, (self: this, key),
            static (state, vs, ps, family) => state.self.CreateBlendPipelineState(
                family, vs, ps, state.key, depthWriteEnabled: true));
    }

    /// <summary>
    ///     Picks the shader route a blended draw compiles against. The instanced arm wins over the
    ///     per-draw grass arm because the two differ in the b1 LAYOUT the vertex shader reads, not
    ///     merely in lighting — handing an instanced batch a per-draw grass PSO would read the world
    ///     matrix out of AlphaState. Both fall back to the shared route when unavailable, so the
    ///     fallback stays structural and no call site branches.
    /// </summary>
    private ReferenceShaderRoute12 SelectBlendRoute(bool grassRoute, bool instancedGrass)
    {
        if (!grassRoute) return _sharedRoute;
        if (instancedGrass) return InstancedBlendGrassShaderAvailable ? _instancedGrassBlendRoute : _sharedRoute;
        return GrassShaderAvailable ? _grassRoute : _sharedRoute;
    }

    /// <summary>Creates one dynamic blend permutation in its already-retained slot-zero family.</summary>
    /// <param name="pipelineResources">Shared cache entry owning the native allocation and root dependency.</param>
    /// <param name="vsBytecode">Borrowed route vertex shader.</param>
    /// <param name="psBytecode">Borrowed route pixel shader.</param>
    /// <param name="key">Exact authored blend and draw-state identity.</param>
    /// <param name="depthWriteEnabled">Whether this permutation writes scene depth.</param>
    /// <returns>A borrowed native pipeline owned exclusively by the Shared cache entry.</returns>
    private ID3D12PipelineState CreateBlendPipelineState(
        ShaderPipelineResources pipelineResources,
        ReadOnlyMemory<byte> vsBytecode,
        ReadOnlyMemory<byte> psBytecode,
        ReferenceBlendPipelineKey key,
        bool depthWriteEnabled)
    {
        var state = new ReferencePipelineRenderState12(
            key.DoubleSided,
            ReferencePipelineRecipe12.CreateBlendAttachment(key.SrcBlendMode, key.DstBlendMode),
            depthWriteEnabled,
            Decal: key.Decal,
            DepthTestEnabled: !key.DepthTestOff);
        var description = ReferencePipelineRecipe12.CreateGraphicsDescription(
            pipelineResources.RootSignature, _gpu.SceneSampleCount, vsBytecode, psBytecode, state);
        return CreateNativePipeline(description, pipelineResources);
    }

    /// <summary>Creates one borrowed fixed-family pipeline with the production reference recipe.</summary>
    /// <param name="pipelineResources">Retained Shared owner for this complete shader family.</param>
    /// <param name="slot">Unique slot within that family.</param>
    /// <param name="vsBytecode">Borrowed direct or instanced vertex bytecode.</param>
    /// <param name="psBytecode">Borrowed game or material pixel bytecode.</param>
    /// <param name="state">Exact culling, blending, depth, decal, coverage and winding configuration.</param>
    /// <returns>A borrowed native handle that must not be released independently.</returns>
    private ID3D12PipelineState CreatePipelineState(
        ShaderPipelineResources pipelineResources,
        int slot,
        ReadOnlyMemory<byte> vsBytecode,
        ReadOnlyMemory<byte> psBytecode,
        ReferencePipelineRenderState12 state)
    {
        var description = ReferencePipelineRecipe12.CreateGraphicsDescription(
            pipelineResources.RootSignature, _gpu.SceneSampleCount, vsBytecode, psBytecode, state);
        return CreateNativePipeline(description, pipelineResources, slot);
    }

    /// <summary>Measures one native graphics creation inside a retained Shared family.</summary>
    /// <param name="description">Complete production graphics description.</param>
    /// <param name="pipelineResources">Shared owner retained before this call.</param>
    /// <param name="slot">Unoccupied family slot, or zero for a dynamic cache entry.</param>
    /// <returns>The created borrowed handle.</returns>
    private ID3D12PipelineState CreateNativePipeline(
        GraphicsPipelineStateDescription description, ShaderPipelineResources pipelineResources, int slot = 0)
    {
        var psoStarted = Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            var pipeline = pipelineResources.CreateGraphics(slot, description);
            succeeded = true;
            return pipeline;
        }
        finally
        {
            RecordPsoCreation("graphics", psoStarted, succeeded);
        }
    }

    /// <summary>Publishes the complete optional Oblivion direct-skin pair or retains the ordinary fallback.</summary>
    private void TryCreateDirectClassicSkinPipelines()
    {
        ShaderPipelineResources? pipelineResources = null;
        var published = false;
        try
        {
            pipelineResources = RetainPipelineFamily(2);
            // Only the TES4 FaceGen direct PSO receives the recovered centroid light/eye varyings.
            // The generic, FNV, modern and instanced vertex permutations retain their existing ABI.
            var vertexShader = CompileEmbeddedShader(
                "reference.vert.hlsl", "main", "vs_5_1",
                new ShaderMacro("REFERENCE_OBLIVION_CLASSIC_SKIN", "1"));
            var pixelShader = CompileEmbeddedShader(
                "reference_classic_skin.frag.hlsl", "main", "ps_5_1");

            var back = CreatePipelineState(pipelineResources, 0,
                vertexShader, pixelShader, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var doubleSided = CreatePipelineState(pipelineResources, 1,
                vertexShader, pixelShader, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));

            _directClassicSkinBackPso = back;
            _directClassicSkinDoublePso = doubleSided;
            _directClassicSkinVertexShader = vertexShader;
            published = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                "ReferencePipelineFactory12: direct classic-skin SKIN2000 specialization disabled: {0}",
                ex.Message);
        }
        finally
        {
            if (!published && pipelineResources is not null)
            {
                ReleaseUnpublishedPipelineFamily(pipelineResources);
            }
        }
    }

    /// <summary>Creates both culling variants of the classic-skin factor-one diagnostic from cached vertex bytecode.</summary>
    /// <param name="vertexShader">Read-only vertex bytecode borrowed during native pipeline creation.</param>
    private void CreateDirectClassicSkinFactorOnePipelines(ReadOnlyMemory<byte> vertexShader)
    {
        ShaderPipelineResources? pipelineResources = null;
        var published = false;
        try
        {
            pipelineResources = RetainPipelineFamily(2);
            // Reuse the exact ordinary SKIN vertex bytecode and all ordinary PSO state. Only the
            // separately inventoried pixel entry replaces the final RGB lighting factor with one.
            var pixelShader = CompileEmbeddedShader(
                "reference_classic_skin_factor_one.frag.hlsl", "mainFactorOne", "ps_5_1");
            var back = CreatePipelineState(pipelineResources, 0,
                vertexShader, pixelShader, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var doubleSided = CreatePipelineState(pipelineResources, 1,
                vertexShader, pixelShader, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));
            _directClassicSkinFactorOneBackPso = back;
            _directClassicSkinFactorOneDoublePso = doubleSided;
            published = true;
        }
        finally
        {
            if (!published && pipelineResources is not null)
            {
                ReleaseUnpublishedPipelineFamily(pipelineResources);
            }
        }
    }

    /// <summary>Publishes the complete optional instanced Fallout family or retains the ordinary fallback.</summary>
    private void TryCreateModernStandardOpaquePipelines()
    {
        ShaderPipelineResources? pipelineResources = null;
        var published = false;
        try
        {
            pipelineResources = RetainPipelineFamily(3);
            // FXC assigns physical stage-link registers densely. Compile the instanced VS with
            // the same family/alpha axes as its PS so their sparse TEXCOORD semantics land on
            // identical hardware registers; a generic VS cannot link to these compact PS inputs.
            var backVs = CompileEmbeddedShader(
                "reference_instanced.vert.hlsl", "main", "vs_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"));
            var cutoutVs = CompileEmbeddedShader(
                "reference_instanced.vert.hlsl", "main", "vs_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1"));
            var backPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"));
            var backCutoutPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1"));
            var doubleCutoutPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_DOUBLE_SIDED", "1"));

            var back = CreatePipelineState(pipelineResources, 0,
                backVs, backPs, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var backCutout = CreatePipelineState(pipelineResources, 1,
                cutoutVs, backCutoutPs, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var doubleCutout = CreatePipelineState(pipelineResources, 2,
                cutoutVs, doubleCutoutPs, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));

            _modernStandardBackPso = back;
            _modernStandardBackCutoutPso = backCutout;
            _modernStandardDoubleCutoutPso = doubleCutout;
            published = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                "ReferencePipelineFactory12: modern-standard opaque specialization disabled: {0}",
                ex.Message);
        }
        finally
        {
            if (!published && pipelineResources is not null)
            {
                ReleaseUnpublishedPipelineFamily(pipelineResources);
            }
        }
    }

    /// <summary>Publishes the complete optional instanced Starfield family or retains the ordinary fallback.</summary>
    private void TryCreateStarfieldDiffuseLitPipelines()
    {
        ShaderPipelineResources? pipelineResources = null;
        var published = false;
        try
        {
            pipelineResources = RetainPipelineFamily(4);
            var backVs = CompileEmbeddedShader(
                "reference_instanced.vert.hlsl", "main", "vs_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"));
            var cutoutVs = CompileEmbeddedShader(
                "reference_instanced.vert.hlsl", "main", "vs_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1"));
            var backPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"));
            var doublePs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED", "1"));
            var backCutoutPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1"));
            var doubleCutoutPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED", "1"));

            var back = CreatePipelineState(pipelineResources, 0,
                backVs, backPs, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var doubleSided = CreatePipelineState(pipelineResources, 1,
                backVs, doublePs, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));
            var backCutout = CreatePipelineState(pipelineResources, 2,
                cutoutVs, backCutoutPs, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var doubleCutout = CreatePipelineState(pipelineResources, 3,
                cutoutVs, doubleCutoutPs, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));

            _starfieldDiffuseLitBackPso = back;
            _starfieldDiffuseLitDoublePso = doubleSided;
            _starfieldDiffuseLitBackCutoutPso = backCutout;
            _starfieldDiffuseLitDoubleCutoutPso = doubleCutout;
            published = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                "ReferencePipelineFactory12: Starfield diffuse-lit specialization disabled: {0}",
                ex.Message);
        }
        finally
        {
            if (!published && pipelineResources is not null)
            {
                ReleaseUnpublishedPipelineFamily(pipelineResources);
            }
        }
    }

    /// <summary>Publishes the complete optional direct Fallout family without crossing the instanced shader ABI.</summary>
    private void TryCreateDirectModernStandardOpaquePipelines()
    {
        ShaderPipelineResources? pipelineResources = null;
        var published = false;
        try
        {
            pipelineResources = RetainPipelineFamily(3);
            // Compile the per-draw VS with the same family/alpha macros as the compact PS. This is
            // deliberately reference.vert.hlsl, never an alias of the t8/SV_InstanceID ABI.
            var backVs = CompileEmbeddedShader(
                "reference.vert.hlsl", "main", "vs_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"));
            var cutoutVs = CompileEmbeddedShader(
                "reference.vert.hlsl", "main", "vs_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1"));
            var backPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"));
            var backCutoutPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1"));
            var doubleCutoutPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_MODERN_STANDARD", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_ALPHA_GREATER", "1"),
                new ShaderMacro("REFERENCE_MODERN_STANDARD_DOUBLE_SIDED", "1"));

            var back = CreatePipelineState(pipelineResources, 0,
                backVs, backPs, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var backCutout = CreatePipelineState(pipelineResources, 1,
                cutoutVs, backCutoutPs, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var doubleCutout = CreatePipelineState(pipelineResources, 2,
                cutoutVs, doubleCutoutPs, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));

            // Publish only after the complete direct family exists. A partially-created family is
            // never observable; finally releases its retained owner or preserves pending cleanup.
            _directModernStandardBackPso = back;
            _directModernStandardBackCutoutPso = backCutout;
            _directModernStandardDoubleCutoutPso = doubleCutout;
            published = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                "ReferencePipelineFactory12: direct modern-standard opaque specialization disabled: {0}",
                ex.Message);
        }
        finally
        {
            if (!published && pipelineResources is not null)
            {
                ReleaseUnpublishedPipelineFamily(pipelineResources);
            }
        }
    }

    /// <summary>Publishes the complete optional direct Starfield family without crossing the instanced shader ABI.</summary>
    private void TryCreateDirectStarfieldDiffuseLitPipelines()
    {
        ShaderPipelineResources? pipelineResources = null;
        var published = false;
        try
        {
            pipelineResources = RetainPipelineFamily(4);
            var backVs = CompileEmbeddedShader(
                "reference.vert.hlsl", "main", "vs_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"));
            var cutoutVs = CompileEmbeddedShader(
                "reference.vert.hlsl", "main", "vs_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1"));
            var backPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"));
            var doublePs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED", "1"));
            var backCutoutPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1"));
            var doubleCutoutPs = CompileEmbeddedShader(
                "reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_ALPHA_GREATER", "1"),
                new ShaderMacro("REFERENCE_STARFIELD_DIFFUSE_LIT_DOUBLE_SIDED", "1"));

            var back = CreatePipelineState(pipelineResources, 0,
                backVs, backPs, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var doubleSided = CreatePipelineState(pipelineResources, 1,
                backVs, doublePs, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));
            var backCutout = CreatePipelineState(pipelineResources, 2,
                cutoutVs, backCutoutPs, new ReferencePipelineRenderState12(
                    DoubleSided: false, BlendAttachment: null, DepthWriteEnabled: true));
            var doubleCutout = CreatePipelineState(pipelineResources, 3,
                cutoutVs, doubleCutoutPs, new ReferencePipelineRenderState12(
                    DoubleSided: true, BlendAttachment: null, DepthWriteEnabled: true));

            _directStarfieldDiffuseLitBackPso = back;
            _directStarfieldDiffuseLitDoublePso = doubleSided;
            _directStarfieldDiffuseLitBackCutoutPso = backCutout;
            _directStarfieldDiffuseLitDoubleCutoutPso = doubleCutout;
            published = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                "ReferencePipelineFactory12: direct Starfield diffuse-lit specialization disabled: {0}",
                ex.Message);
        }
        finally
        {
            if (!published && pipelineResources is not null)
            {
                ReleaseUnpublishedPipelineFamily(pipelineResources);
            }
        }
    }

    /// <summary>
    ///     Depth-only PSO for the sun-shadow pass: no render targets, single-sample D32 depth
    ///     (the shadow map is never MSAA regardless of the scene's sample count), no culling
    ///     (single-sided planes must still occlude from the light's side), and a NEGATIVE
    ///     rasterizer depth bias — reversed-Z stores larger values nearer the light, so pushing
    ///     the stored depth away from the light (the acne fix) means biasing it SMALLER.
    /// </summary>
    /// <param name="pipelineResources">Retained mandatory family that owns both shadow variants.</param>
    /// <param name="slot">Distinct mandatory-family slot for this shadow pipeline.</param>
    /// <param name="vsBytecode">Instanced shadow-card vertex bytecode.</param>
    /// <param name="psBytecode">Alpha-test pixel bytecode, or null for opaque depth-only output.</param>
    /// <returns>A borrowed single-sampled depth pipeline with the established reversed-Z bias.</returns>
    private ID3D12PipelineState CreateShadowPipelineState(
        ShaderPipelineResources pipelineResources, int slot,
        ReadOnlyMemory<byte> vsBytecode, ReadOnlyMemory<byte>? psBytecode)
    {
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            DepthBias = -1000,
            DepthBiasClamp = 0f,
            SlopeScaledDepthBias = -2f,
        };

        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = D12.DepthWriteMask.All,
            DepthFunc = ComparisonFunction.GreaterEqual, // reversed-Z, same as the scene PSOs
            StencilEnable = false,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = pipelineResources.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode ?? ReadOnlyMemory<byte>.Empty,
            BlendState = D12.BlendDescription.Opaque,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(GpuMeshBufferFactory12.InputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = Array.Empty<Format>(),
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue,
        };
        var psoStarted = Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            var pipeline = pipelineResources.CreateGraphics(slot, psoDesc);
            succeeded = true;
            return pipeline;
        }
        finally
        {
            RecordPsoCreation("shadow", psoStarted, succeeded);
        }
    }

    private (int Count, double Milliseconds, long Timestamp) BeginPsoGroup(
        string group,
        BethesdaGame game)
    {
        BethesdaMultitool.Core.Diagnostics.Logger.Instance.Debug(
            "ReferencePipelineFactory12: driver PSO group started game={0} group={1}.",
            game,
            group);
        return (_psoCreationCount, _psoCreationMilliseconds, Stopwatch.GetTimestamp());
    }

    private void CompletePsoGroup(
        string group,
        BethesdaGame game,
        (int Count, double Milliseconds, long Timestamp) before)
    {
        var count = _psoCreationCount - before.Count;
        var psoMilliseconds = _psoCreationMilliseconds - before.Milliseconds;
        var wallMilliseconds = Stopwatch.GetElapsedTime(before.Timestamp).TotalMilliseconds;
        BethesdaMultitool.Core.Diagnostics.Logger.Instance.Debug(
            "ReferencePipelineFactory12: driver PSO group completed game={0} group={1} " +
            "count={2} driverPso={3:F2} ms wall={4:F2} ms.",
            game,
            group,
            count,
            psoMilliseconds,
            wallMilliseconds);
        RendererProfilerTrace.Event("d3d12-reference-pso-group", new Dictionary<string, object?>
        {
            ["game"] = game.ToString(),
            ["group"] = group,
            ["psoCreationCount"] = count,
            ["psoCreationMilliseconds"] = psoMilliseconds,
            ["wallMilliseconds"] = wallMilliseconds,
        });
    }

    private void RecordPsoCreation(string kind, long started, bool succeeded)
    {
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _psoCreationCount++;
        _psoCreationMilliseconds += elapsedMilliseconds;
        _maxPsoCreationMilliseconds = Math.Max(_maxPsoCreationMilliseconds, elapsedMilliseconds);

        // Keep routine startup quiet. A single call above this threshold is already several frames
        // and is exactly the driver-side stall that a shipped DXBC pack cannot eliminate.
        if (elapsedMilliseconds < 100d)
        {
            return;
        }

        BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
            "ReferencePipelineFactory12: slow driver PSO call kind={0} outcome={1} elapsed={2:F2} ms.",
            kind,
            succeeded ? "ready" : "failed",
            elapsedMilliseconds);
        RendererProfilerTrace.Event("d3d12-reference-pso-slow", new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["outcome"] = succeeded ? "ready" : "failed",
            ["elapsedMilliseconds"] = elapsedMilliseconds,
        });
    }

    /// <summary>Retains a partially initialized route owner before any native pipeline can be created.</summary>
    /// <param name="resource">Managed route whose future cache entries own their native allocations.</param>
    /// <returns>The same resource for assignment to the factory field.</returns>
    private T TrackConstructionResource<T>(T resource) where T : IDisposable
    {
        _constructionTransaction?.Track(resource);
        return resource;
    }

    /// <summary>Retains an independent Shared family before shader compilation or native pipeline creation.</summary>
    /// <param name="capacity">Maximum number of native pipelines in this complete family.</param>
    /// <returns>A retained owner initialized against the existing world root.</returns>
    /// <remarks>Pending unpublished cleanup must succeed before another family can be allocated.</remarks>
    private ShaderPipelineResources RetainPipelineFamily(int capacity)
    {
        RetryPendingPipelineRelease();
        _pipelineFamilies.EnsureCapacity(_pipelineFamilies.Count + 1);
        _constructionTransaction?.Reserve();
        var pipelineResources = _rootSignature.CreatePipelineResources(capacity);
        _pipelineFamilies.Add(pipelineResources);
        _constructionTransaction?.Track(pipelineResources);
        return pipelineResources;
    }

    /// <summary>Releases a retired or unpublished family, removing ownership only after successful cleanup.</summary>
    /// <param name="pipelineResources">Retained owner whose native users have already retired.</param>
    private void ReleasePipelineFamily(ShaderPipelineResources pipelineResources)
    {
        pipelineResources.Dispose();
        _constructionTransaction?.Forget(pipelineResources);
        _pipelineFamilies.Remove(pipelineResources);
    }

    /// <summary>Attempts unpublished cleanup without hiding the original creation failure or losing its owner.</summary>
    /// <param name="pipelineResources">Incomplete family that has never been used by a command list.</param>
    /// <remarks>A failed release is retried before new family creation and by final disposal.</remarks>
    private void ReleaseUnpublishedPipelineFamily(ShaderPipelineResources pipelineResources)
    {
        _pendingPipelineRelease = pipelineResources;
        try
        {
            RetryPendingPipelineRelease();
        }
        catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                "ReferencePipelineFactory12: unpublished pipeline cleanup remains pending: {0}", cleanupError.Message);
        }
    }

    /// <summary>Retries the retained unpublished family before permitting another native family allocation.</summary>
    private void RetryPendingPipelineRelease()
    {
        if (_pendingPipelineRelease is null) { return; }
        ReleasePipelineFamily(_pendingPipelineRelease);
        _pendingPipelineRelease = null;
    }

    /// <summary>Gets an embedded shader permutation through the application cache and Shared compiler.</summary>
    /// <param name="name">Embedded shader file name.</param>
    /// <param name="entryPoint">HLSL entry point.</param>
    /// <param name="profile">Native compiler target profile.</param>
    /// <param name="macros">Definitions selecting the shader permutation.</param>
    /// <returns>Read-only cached DXBC passed directly to native pipeline creation without a payload copy.</returns>
    private static ReadOnlyMemory<byte> CompileEmbeddedShader(
        string name, string entryPoint, string profile, params ShaderMacro[] macros) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile, macros);

    /// <summary>Releases caller-retired fixed pipelines and Shared route caches, retaining failed actions for retry.</summary>
    /// <remarks>Only family owners are released; borrowed aliases are never disposed. This method does not establish GPU retirement.</remarks>
    /// <exception cref="InvalidOperationException">Called from another managed thread.</exception>
    /// <exception cref="AggregateException">One or more releases remain pending.</exception>
    public void Dispose()
    {
        VerifyAccess();
        if (!_disposed)
        {
            _retiredResources = PrepareRetiredResources();
            _disposed = true;
        }
        _retiredResources!.Dispose();
        _pipelineFamilies.Clear();
        _pendingPipelineRelease = null;
        _instancedGrassPipelineResources = null;
        _directClassicSkinVertexShader = null;
        _oblivionEyePso = null;
    }

    /// <summary>Registers route caches and retained Shared families, including incomplete families with pending cleanup.</summary>
    /// <returns>A retained collection that independently retries failed child releases.</returns>
    private RetiredResourceDisposal PrepareRetiredResources()
    {
        var retired = new RetiredResourceDisposal();
        retired.Add(_sharedRoute, "shared reference blend route");
        retired.Add(_grassRoute, "direct grass blend route");
        retired.Add(_instancedGrassBlendRoute, "instanced grass blend route");
        retired.Add(_independentSkinPipelines, "independent classic-skin pipelines");
        for (var index = _pipelineFamilies.Count - 1; index >= 0; index--)
        {
            retired.Add(_pipelineFamilies[index], "reference pipeline family");
        }
        return retired;
    }

    /// <summary>Rejects cross-thread lifetime changes before native ownership or stopped state can mutate.</summary>
    /// <exception cref="InvalidOperationException">Called from a thread other than the creating thread.</exception>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Reference pipelines must be accessed on their creating thread.");
        }
    }

}
