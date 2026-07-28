#if WINDOWS_GUI
using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Owns the GPU pipeline-state objects (PSOs) used by <see cref="ReferenceRenderer12" />:
///     compiles the reference shaders once, builds the two opaque (back-face / double-sided)
///     instanced PSOs up front, and lazily creates + caches blended PSOs keyed by blend mode.
///     This is a pure GPU-resource factory — it records no command-list work, so the renderer's
///     draw ordering is unaffected by the extraction.
/// </summary>
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
    private readonly ShaderRoutePsos _sharedRoute;
    private readonly ShaderRoutePsos _grassRoute = new();

    private bool _disposed;

    public ReferencePipelineFactory12(GpuDevice12 gpu, GpuRootSignature12 rootSignature)
    {
        _gpu = gpu;
        _rootSignature = rootSignature;

        var blendedVsBytecode = CompileEmbeddedShader("reference.vert.hlsl", "main", "vs_5_1");
        var instancedVsBytecode = CompileEmbeddedShader("reference_instanced.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("reference.frag.hlsl", "main", "ps_5_1");
        _sharedRoute = new ShaderRoutePsos(blendedVsBytecode, psBytecode);
        OpaqueBackPso = CreatePipelineState(instancedVsBytecode, psBytecode, doubleSided: false, blendAttachment: null,
            depthWriteEnabled: true);
        OpaqueDoublePso = CreatePipelineState(instancedVsBytecode, psBytecode, doubleSided: true, blendAttachment: null,
            depthWriteEnabled: true);
        OpaqueBackDecalPso = CreatePipelineState(instancedVsBytecode, psBytecode, doubleSided: false,
            blendAttachment: null, depthWriteEnabled: true, decal: true);
        OpaqueDoubleDecalPso = CreatePipelineState(instancedVsBytecode, psBytecode, doubleSided: true,
            blendAttachment: null, depthWriteEnabled: true, decal: true);

        // Grass cutouts: alpha-to-coverage variants (engine mechanism — BSRenderState::
        // SetAlphaToCoverageEnable) so MSAA dithers the alpha-test edge instead of the binary
        // discard's hard staircase. The paired ALPHA_TO_COVERAGE pixel shader converts the
        // authored NiAlphaProperty threshold into a sharpened coverage gradient. When the scene
        // is single-sampled A2C is a hardware no-op AND the gradient PS would render solid
        // cards, so the properties alias the plain opaque PSOs — renderer routing never branches.
        AlphaToCoverageAvailable = _gpu.SceneSampleCount > 1;
        if (AlphaToCoverageAvailable)
        {
            var a2cPsBytecode = CompileEmbeddedShader("reference.frag.hlsl", "main", "ps_5_1",
                new ShaderMacro("ALPHA_TO_COVERAGE", "1"));
            OpaqueBackA2CPso = CreatePipelineState(instancedVsBytecode, a2cPsBytecode, doubleSided: false,
                blendAttachment: null, depthWriteEnabled: true, alphaToCoverage: true);
            OpaqueDoubleA2CPso = CreatePipelineState(instancedVsBytecode, a2cPsBytecode, doubleSided: true,
                blendAttachment: null, depthWriteEnabled: true, alphaToCoverage: true);
        }
        else
        {
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
        ShadowOpaquePso = CreateShadowPipelineState(shadowVsBytecode, psBytecode: null);
        ShadowAlphaTestPso = CreateShadowPipelineState(shadowVsBytecode, shadowPsBytecode);
    }

    /// <summary>Instanced opaque PSO with back-face culling (single-sided submeshes).</summary>
    public ID3D12PipelineState OpaqueBackPso { get; }

    /// <summary>Instanced opaque PSO with no culling (double-sided submeshes).</summary>
    public ID3D12PipelineState OpaqueDoublePso { get; }

    /// <summary>Depth-biased variant of <see cref="OpaqueBackPso" /> for decal overlay submeshes.</summary>
    public ID3D12PipelineState OpaqueBackDecalPso { get; }

    /// <summary>Depth-biased variant of <see cref="OpaqueDoublePso" /> for decal overlay submeshes.</summary>
    public ID3D12PipelineState OpaqueDoubleDecalPso { get; }

    /// <summary>True when the scene target is multisampled and the A2C PSOs are distinct variants;
    /// false = <see cref="OpaqueBackA2CPso" />/<see cref="OpaqueDoubleA2CPso" /> alias the plain PSOs.</summary>
    public bool AlphaToCoverageAvailable { get; }

    /// <summary>Alpha-to-coverage variant of <see cref="OpaqueBackPso" /> (grass cutout edges).</summary>
    public ID3D12PipelineState OpaqueBackA2CPso { get; }

    /// <summary>Alpha-to-coverage variant of <see cref="OpaqueDoublePso" /> (grass cutout edges).</summary>
    public ID3D12PipelineState OpaqueDoubleA2CPso { get; }

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

    public ID3D12PipelineState GetBlendPipeline(
        byte srcBlendMode, byte dstBlendMode, bool doubleSided, bool decal = false, bool grassRoute = false)
    {
        var route = grassRoute && GrassShaderAvailable ? _grassRoute : _sharedRoute;
        return route.GetOrCreate(
            new BlendPipelineKey(srcBlendMode, dstBlendMode, doubleSided, decal),
            depthWrite: false,
            (self: this, srcBlendMode, dstBlendMode, doubleSided, decal),
            static (s, vs, ps) => s.self.CreateBlendPipelineState(
                vs, ps, s.srcBlendMode, s.dstBlendMode, s.doubleSided, s.decal, depthWriteEnabled: false));
    }

    /// <summary>
    ///     Depth-WRITING variant of <see cref="GetBlendPipeline" />: the same alpha blend, but it also
    ///     writes depth. Used for effects-folder foliage the engine marks ZBuffer_Write (e.g. NVSeaPlant02).
    ///     The renderer draws these inline BEFORE the water pass so the water surface occludes them from
    ///     above — which a no-depth blend (drawn after water) can't do.
    /// </summary>
    public ID3D12PipelineState GetBlendDepthWritePipeline(byte srcBlendMode, byte dstBlendMode, bool doubleSided,
        bool decal = false, bool grassRoute = false)
    {
        var route = grassRoute && GrassShaderAvailable ? _grassRoute : _sharedRoute;
        return route.GetOrCreate(
            new BlendPipelineKey(srcBlendMode, dstBlendMode, doubleSided, decal),
            depthWrite: true,
            (self: this, srcBlendMode, dstBlendMode, doubleSided, decal),
            static (s, vs, ps) => s.self.CreateBlendPipelineState(
                vs, ps, s.srcBlendMode, s.dstBlendMode, s.doubleSided, s.decal, depthWriteEnabled: true));
    }

    /// <summary>
    ///     The alpha-blend PSO recipe shared by both blend getters and every shader route: the
    ///     authored src/dst colour blend, One/Max alpha accumulation, and the standard depth/decal
    ///     handling from <see cref="CreatePipelineState" />.
    /// </summary>
    private ID3D12PipelineState CreateBlendPipelineState(
        byte[] vsBytecode, byte[] psBytecode, byte srcBlendMode, byte dstBlendMode, bool doubleSided,
        bool decal, bool depthWriteEnabled)
    {
        var rtBlend = new D12.RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = NifD3D12BlendMapper.ResolveBlendFactor(srcBlendMode),
            DestinationBlend = NifD3D12BlendMapper.ResolveBlendFactor(dstBlendMode),
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.One,
            BlendOperationAlpha = D12.BlendOperation.Max,
            RenderTargetWriteMask = D12.ColorWriteEnable.All
        };

        return CreatePipelineState(vsBytecode, psBytecode, doubleSided, rtBlend, depthWriteEnabled, decal);
    }

    private ID3D12PipelineState CreatePipelineState(
        byte[] vsBytecode,
        byte[] psBytecode,
        bool doubleSided,
        D12.RenderTargetBlendDescription? blendAttachment,
        bool depthWriteEnabled,
        bool decal = false,
        bool alphaToCoverage = false)
    {
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = doubleSided ? D12.CullMode.None : D12.CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            // Antialias triangle edges on the multisampled scene RT (no-op when scene isn't MSAA).
            MultisampleEnable = _gpu.SceneSampleCount > 1,
        };

        if (decal)
        {
            // Decal overlays are authored coplanar with their backing surface; bias them TOWARD the
            // camera so they win the depth tie. Reversed-Z (GreaterEqual, near→1) means "closer" is a
            // LARGER depth value, so the bias is positive. Small relative to the navmesh overlay's
            // 2000/2.0 — a decal must only clear its own backing wall, not float over nearby props.
            rasterizer.DepthBias = 64;
            rasterizer.DepthBiasClamp = 0f;
            rasterizer.SlopeScaledDepthBias = 1f;
        }

        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = depthWriteEnabled ? D12.DepthWriteMask.All : D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.GreaterEqual, // reversed-Z (near→1, far→0); depth clear = 0
            StencilEnable = false,
        };

        var blend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = alphaToCoverage,
            IndependentBlendEnable = false,
        };
        blend.RenderTarget[0] = blendAttachment ?? new D12.RenderTargetBlendDescription
        {
            BlendEnable = false,
            SourceBlend = D12.Blend.One,
            DestinationBlend = D12.Blend.Zero,
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.Zero,
            BlendOperationAlpha = D12.BlendOperation.Add,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(GpuMeshBufferFactory12.InputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)_gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return _gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    /// <summary>
    ///     Depth-only PSO for the sun-shadow pass: no render targets, single-sample D32 depth
    ///     (the shadow map is never MSAA regardless of the scene's sample count), no culling
    ///     (single-sided planes must still occlude from the light's side), and a NEGATIVE
    ///     rasterizer depth bias — reversed-Z stores larger values nearer the light, so pushing
    ///     the stored depth away from the light (the acne fix) means biasing it SMALLER.
    /// </summary>
    private ID3D12PipelineState CreateShadowPipelineState(byte[] vsBytecode, byte[]? psBytecode)
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
            RootSignature = _rootSignature.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
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
        return _gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    /// <summary>
    ///     Forwards to the one shared compiler. This used to be a private copy — one of a dozen that
    ///     had drifted apart on flags and resource lookup; see <see cref="GpuShaderCompiler12" />.
    /// </summary>
    private static byte[] CompileEmbeddedShader(
        string name, string entryPoint, string profile, params ShaderMacro[] macros) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile, macros);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sharedRoute.DisposeAll();
        _grassRoute.DisposeAll();
        ShadowAlphaTestPso.Dispose();
        ShadowOpaquePso.Dispose();
        if (AlphaToCoverageAvailable)
        {
            // Distinct variants only — when MSAA is off these alias the plain opaque PSOs below.
            OpaqueDoubleA2CPso.Dispose();
            OpaqueBackA2CPso.Dispose();
        }
        OpaqueDoubleDecalPso.Dispose();
        OpaqueBackDecalPso.Dispose();
        OpaqueDoublePso.Dispose();
        OpaqueBackPso.Dispose();
    }

    private readonly record struct BlendPipelineKey(byte SrcBlendMode, byte DstBlendMode, bool DoubleSided, bool Decal);

    /// <summary>
    ///     One blend shader route: a vertex+pixel bytecode pair plus its OWN blend and
    ///     depth-writing-blend PSO caches. The shared reference pair is the always-active route; a
    ///     per-game <see cref="GameShaderPair" /> (grass is pair #1) becomes a route that is active
    ///     only while its shaders compiled. Routes never share a cache: the
    ///     <see cref="BlendPipelineKey" />s are identical between routes — only the shaders differ —
    ///     so one cache would hand back another route's pipeline after the first draw.
    /// </summary>
    private sealed class ShaderRoutePsos
    {
        private readonly Dictionary<BlendPipelineKey, ID3D12PipelineState> _blendPsos = new();
        private readonly Dictionary<BlendPipelineKey, ID3D12PipelineState> _blendDepthWritePsos = new();
        private GameShaderPair _profile;
        private byte[]? _vsBytecode;
        private byte[]? _psBytecode;

        /// <summary>A per-game route: inactive (= shared path) until <see cref="Set" /> compiles a pair.</summary>
        public ShaderRoutePsos()
        {
        }

        /// <summary>The always-active route over the pre-compiled shared shaders.</summary>
        public ShaderRoutePsos(byte[] vsBytecode, byte[] psBytecode)
        {
            _vsBytecode = vsBytecode;
            _psBytecode = psBytecode;
        }

        /// <summary>True when this route's shaders are available to draw with.</summary>
        public bool Active => _vsBytecode is not null && _psBytecode is not null;

        /// <summary>
        ///     Selects this route's shader pair, compiling it on first use. A no-op when the profile
        ///     is unchanged; a change drops the bytecode and every cached PSO. FAIL-SOFT on a compile
        ///     failure (<see cref="GameShaderPair.TryCompile" /> logs and returns null): the route
        ///     goes inactive so callers keep the shared shaders, and the stored profile resets so a
        ///     later Set of the same pair retries the compile instead of no-oping on the equality
        ///     check.
        /// </summary>
        public void Set(GameShaderPair profile, string consumerName)
        {
            if (profile.Equals(_profile)) return;

            _profile = profile;
            _vsBytecode = null;
            _psBytecode = null;
            DisposeAll();

            if (!profile.Enabled) return;

            var compiled = profile.TryCompile(consumerName);
            if (compiled is null)
            {
                _profile = default;
                return;
            }

            (_vsBytecode, _psBytecode) = compiled.Value;
        }

        /// <summary>
        ///     Route-local PSO lookup; builds via <paramref name="create" /> (handed this route's
        ///     bytecode) on first miss. <paramref name="state" /> keeps call sites closure-free —
        ///     this runs per blended draw, so a capturing lambda would allocate every frame.
        /// </summary>
        public ID3D12PipelineState GetOrCreate<TState>(
            BlendPipelineKey key,
            bool depthWrite,
            TState state,
            Func<TState, byte[], byte[], ID3D12PipelineState> create)
        {
            var cache = depthWrite ? _blendDepthWritePsos : _blendPsos;
            if (cache.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var pso = create(state, _vsBytecode!, _psBytecode!);
            cache[key] = pso;
            return pso;
        }

        public void DisposeAll()
        {
            foreach (var pso in _blendPsos.Values)
            {
                pso.Dispose();
            }
            _blendPsos.Clear();
            foreach (var pso in _blendDepthWritePsos.Values)
            {
                pso.Dispose();
            }
            _blendDepthWritePsos.Clear();
        }
    }
}
#endif
