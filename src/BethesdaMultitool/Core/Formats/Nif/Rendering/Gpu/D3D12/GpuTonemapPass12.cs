using BethesdaMultitool.Core.Games;
using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Self-contained fullscreen display-composite pass: samples the float scene color
///     (<see cref="GpuSceneFormats.SceneColor" /> — a float target that preserves values &gt; 1) and
///     maps it to the 8-bit display target (<see cref="GpuSceneFormats.LdrOutput" />). The selected
///     operator is LegacyClamp, GammaAces, recovered classic FO3/FNV HDR, the partial Creation-era
///     path, or FO3/FNV's standalone non-HDR cinematic grade (<c>tonemap.frag.hlsl</c>). HDR modes
///     retain above-1 scene energy for exposure/bloom; the standalone cinematic path instead grades
///     a clamped LDR input without adaptation or bloom.
///     <para>
///         In engine mode the pass also records the recovered bloom chain
///         (<c>bloom.frag.hlsl</c>): recursive DownSample16 reduction, then the family-specific
///         quarter-resolution graph. FO3/FNV and Oldrim use fused BrightPassBlur plus one plain
///         axis; TES4 uses a separate bright pass plus cumulative two-axis blur pairs.
///     </para>
///     <para>
///         Retains its root signature (SRV table t0–t2 + 24 root constants b0 + linear/point-clamp static
///         samplers) and ten PSOs through Shared's pipeline owner, and owns a small shader-visible
///         SRV ring heap for the per-call texture views.
///         Both scene targets (<see cref="GpuOffscreenSceneTarget12" /> headless +
///         <c>GpuSwapChainSurface12</c> live) drive it once per frame; the ring survives the
///         in-flight frames. The caller owns the resource-state transitions (HDR source →
///         <see cref="ResourceStates.PixelShaderResource" />, LDR dest →
///         <see cref="ResourceStates.RenderTarget" />) around <see cref="Record" />.
///     </para>
/// </summary>
internal sealed class GpuTonemapPass12 : IDisposable, IGpuCommandSubmissionParticipant12
{
    // SRV ring depth: one call = fixed 3-descriptor groups (t0/t1/t2) for ADAPT, every possible
    // recursive DownSample16 level, all bloom ping-pong sources, and the main composite. Groups are cycled so a
    // view is never overwritten while the previous frame's tonemap draw still reads it.
    // framesInFlight (2) × a couple of scene targets is comfortably under 8 ring slots.
    private const int SrvRingSlots = 8;
    private const int SrvsPerCall = 3;
    private const int AdaptGroup = 0;
    private const int DownsampleGroupStart = 1;
    private const int BrightPassBlurGroup = DownsampleGroupStart + ClassicHdrPassPlan.MaxReductionLevels;
    private const int BlurGroup = BrightPassBlurGroup + 1;
    private const int ReverseBlurGroup = BlurGroup + 1;
    private const int CompositeGroup = ReverseBlurGroup + 1;
    private const int GroupsPerCall = CompositeGroup + 1;
    private const int ReductionRtvStart = 2;
    private const int BrightPassBlurRtvSlot = ReductionRtvStart + ClassicHdrPassPlan.MaxReductionLevels;
    private const int BlurRtvSlot = BrightPassBlurRtvSlot + 1;

    private const int RtvBankSize = BlurRtvSlot + 1;

    // Two banks retain the floor-quarter and ceiling-quarter target families concurrently. A live
    // cross-game switch can therefore stop using one family without rewriting descriptors or
    // destroying resources still referenced by an in-flight frame.
    private const int RtvDescriptorCount = RtvBankSize * 2;
    private readonly ID3D12PipelineState _adaptPso;
    private readonly ID3D12PipelineState _avgPso;
    private readonly uint _avgRtvDescriptorSize;

    private readonly ID3D12DescriptorHeap _avgRtvHeap;

    // Ping-pong pair of 1×1 adapted-average targets: each engine-mode frame reads the PREVIOUS
    // frame's adapted average (temporal eye adaptation) while writing the new one; the main pass
    // then samples the freshly written side. Both start in PixelShaderResource.
    private readonly ID3D12Resource[] _avgTextures = new ID3D12Resource[2];
    private readonly ID3D12PipelineState _bloomPso;

    private readonly ID3D12PipelineState _blurPso;

    // A failed constructor never reaches GpuSwapChainSurface12 ownership. Track each COM resource
    // until the whole immutable tonemap graph exists, then transfer it to the normal Dispose path.
    private readonly TonemapConstructionTransaction? _constructionTransaction = new();
    private readonly ID3D12PipelineState _downsamplePso;

    private readonly GpuDevice12 _gpu;

    // Root and PSO fields borrow their handles from this owner; only it releases them.
    private readonly ShaderPipelineResources _pipelineResources;
    private readonly ID3D12PipelineState _pso;

    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _skyrimDownsamplePso;
    private readonly ID3D12PipelineState _skyrimLuminancePso;
    private readonly uint _srvDescriptorSize;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly ID3D12PipelineState _tes4BlurPso;
    private readonly ID3D12PipelineState _tes4BrightPassPso;
    private bool _adaptPrimed;
    private int _alternateBloomHeight;
    private ID3D12Resource? _alternateBloomTexture;
    private int _alternateBloomWidth;
    private ID3D12Resource? _alternateBrightPassBlurTexture;
    private HdrReductionDimensionRule _alternateClassicDimensionRule;
    private int _alternateClassicSourceHeight;
    private int _alternateClassicSourceWidth;
    private int _alternateReductionLevelCount;

    private ID3D12Resource?[] _alternateReductionTextures =
        new ID3D12Resource?[ClassicHdrPassPlan.MaxReductionLevels];

    private int _alternateRtvBank = 1;
    private int _avgWriteIndex;
    private int _bloomHeight;
    private ID3D12Resource? _bloomTexture;
    private int _bloomWidth;
    private ID3D12Resource? _brightPassBlurTexture;
    private HdrReductionDimensionRule _classicDimensionRule;
    private int _classicRtvBank;
    private int _classicSourceHeight;
    private int _classicSourceWidth;
    private bool _disposed;
    private RetiredResourceDisposal? _retiredResources;
    private TonemapLogicalHistoryState? _historyBeforeCurrentCommandList;

    private GpuCommandRecorder12? _historyTransactionRecorder;

    // Null represents a non-adaptive composite. Keep the exact adaptive operator identity rather
    // than only a bool: Skyrim stores two scalar luminance lanes in the history texture, whereas
    // the classic FO3/FNV/Bloom path stores RGB. Reinterpreting either layout after a live mode
    // switch corrupts exposure until it happens to converge again.
    private GpuTonemapMode? _lastAdaptiveMode;
    private Format _lastHistoryFormat = Format.Unknown;
    private int _lastHistoryHeight;
    private ulong _lastHistoryKey = ulong.MaxValue;
    private ID3D12Resource? _lastHistoryTarget;
    private int _lastHistoryWidth;
    private int _reductionLevelCount;

    // Recursive /4 DownSample16 targets. Level 0 is retained as the BrightPassBlur source. Classic
    // ends at 1x1; primed Skyrim frames retain the preceding level for fused reduction + ADAPT.
    private ID3D12Resource?[] _reductionTextures =
        new ID3D12Resource?[ClassicHdrPassPlan.MaxReductionLevels];

    private int _srvCursor;

    /// <summary>Creates the complete display pipeline family and initial HDR history on the calling thread.</summary>
    /// <param name="gpu">Borrowed device whose lifetime encloses this pass and all submitted uses.</param>
    /// <remarks>The caller must retire GPU uses before disposal and dispose on this creating thread.</remarks>
    public GpuTonemapPass12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;
        try
        {
            // Retain the shared owner before native initialization so partial pipeline creation is
            // included in constructor rollback. Its handles are borrowed below, never separately owned.
            _pipelineResources = TrackConstructionResource(new ShaderPipelineResources(device, 10));
            // Root: [0] SRV table (t0 = HDR scene, t1 = 1×1 adapted average color, t2 = bloom); [1]
            // 24×32-bit root constants (b0, six float4s — tonemap/cinematic + modern semantic params
            // draws, repacked as bloom params for the bloom draws); linear-clamp s0 plus point-clamp s1
            // for Skyrim's explicitly unfiltered slot-6 reduction.
            var srvRange = new DescriptorRange1
            {
                RangeType = DescriptorRangeType.ShaderResourceView,
                NumDescriptors = SrvsPerCall,
                BaseShaderRegister = 0,
                RegisterSpace = 0,
                Flags = DescriptorRangeFlags.DescriptorsVolatile,
                OffsetInDescriptorsFromTableStart = 0
            };
            var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
            var rootConstants = new RootParameter1(
                new RootConstants(0, 0, 24),
                ShaderVisibility.Pixel);

            var linearSampler = new StaticSamplerDescription(
                0,
                Filter.MinMagMipLinear,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                0f,
                1,
                ComparisonFunction.Never,
                StaticBorderColor.OpaqueBlack,
                0f,
                float.MaxValue,
                ShaderVisibility.Pixel);
            var pointSampler = new StaticSamplerDescription(
                1,
                Filter.MinMagMipPoint,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                TextureAddressMode.Clamp,
                0f,
                1,
                ComparisonFunction.Never,
                StaticBorderColor.OpaqueBlack,
                0f,
                float.MaxValue,
                ShaderVisibility.Pixel);

            var desc = new RootSignatureDescription1(
                RootSignatureFlags.None,
                new[] { srvTable, rootConstants },
                new[] { linearSampler, pointSampler });
            _pipelineResources.Initialize(new VersionedRootSignatureDescription(desc));
            _rootSignature = _pipelineResources.RootSignature;

            var vs = CompileEmbeddedShader("tonemap.vert.hlsl", "main", "vs_5_1");
            var ps = CompileEmbeddedShader("tonemap.frag.hlsl", "main", "ps_5_1");

            var blend = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = false,
                RenderTargetWriteMask = ColorWriteEnable.All
            };

            var psoDesc = new GraphicsPipelineStateDescription
            {
                RootSignature = _rootSignature,
                VertexShader = vs,
                PixelShader = ps,
                BlendState = blend,
                RasterizerState = new RasterizerDescription
                {
                    FillMode = FillMode.Solid,
                    CullMode = CullMode.None,
                    DepthClipEnable = false
                },
                DepthStencilState = new DepthStencilDescription
                {
                    DepthEnable = false,
                    DepthWriteMask = DepthWriteMask.Zero,
                    DepthFunc = ComparisonFunction.Always,
                    StencilEnable = false
                },
                InputLayout = new InputLayoutDescription(Array.Empty<InputElementDescription>()),
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                RenderTargetFormats = new[] { GpuSceneFormats.LdrOutput },
                DepthStencilFormat = Format.Unknown,
                SampleDescription = new SampleDescription(1, 0), // the LDR output/backbuffer is single-sample
                SampleMask = uint.MaxValue
            };
            _pso = _pipelineResources.CreateGraphics(0, psoDesc);

            // The modern stand-in still computes a sparse average in mainAvg. Engine modes instead run
            // their recursive reductions below; Skyrim's mainAdapt also fuses the final retail step.
            var avgPs = CompileEmbeddedShader("tonemap.frag.hlsl", "mainAvg", "ps_5_1");
            var avgPsoDesc = psoDesc;
            avgPsoDesc.PixelShader = avgPs;
            avgPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
            _avgPso = _pipelineResources.CreateGraphics(1, avgPsoDesc);

            var adaptPs = CompileEmbeddedShader("tonemap.frag.hlsl", "mainAdapt", "ps_5_1");
            var adaptPsoDesc = psoDesc;
            adaptPsoDesc.PixelShader = adaptPs;
            adaptPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
            _adaptPso = _pipelineResources.CreateGraphics(2, adaptPsoDesc);

            // Recovered engine bloom: explicit DownSample16, then the two rows inside the selected
            // ImageSpaceEffectBlur effect: vertical BrightPassBlur followed by horizontal plain blur.
            var downsamplePs = CompileEmbeddedShader("bloom.frag.hlsl", "mainDownsample16", "ps_5_1");
            var downsamplePsoDesc = psoDesc;
            downsamplePsoDesc.PixelShader = downsamplePs;
            downsamplePsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
            _downsamplePso = _pipelineResources.CreateGraphics(3, downsamplePsoDesc);

            var skyrimLuminancePs = CompileEmbeddedShader(
                "bloom.frag.hlsl", "mainSkyrimLuminance4", "ps_5_1");
            var skyrimLuminancePsoDesc = downsamplePsoDesc;
            skyrimLuminancePsoDesc.PixelShader = skyrimLuminancePs;
            _skyrimLuminancePso = _pipelineResources.CreateGraphics(4, skyrimLuminancePsoDesc);

            var skyrimDownsamplePs = CompileEmbeddedShader(
                "bloom.frag.hlsl", "mainSkyrimDownsample16", "ps_5_1");
            var skyrimDownsamplePsoDesc = downsamplePsoDesc;
            skyrimDownsamplePsoDesc.PixelShader = skyrimDownsamplePs;
            _skyrimDownsamplePso = _pipelineResources.CreateGraphics(5, skyrimDownsamplePsoDesc);

            var bloomPs = CompileEmbeddedShader("bloom.frag.hlsl", "main", "ps_5_1");
            var bloomPsoDesc = psoDesc;
            bloomPsoDesc.PixelShader = bloomPs;
            bloomPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
            _bloomPso = _pipelineResources.CreateGraphics(6, bloomPsoDesc);

            var blurPs = CompileEmbeddedShader("bloom.frag.hlsl", "mainBlur", "ps_5_1");
            var blurPsoDesc = psoDesc;
            blurPsoDesc.PixelShader = blurPs;
            blurPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
            _blurPso = _pipelineResources.CreateGraphics(7, blurPsoDesc);

            var tes4BrightPassPs = CompileEmbeddedShader(
                "bloom.frag.hlsl", "mainTes4BrightPass", "ps_5_1");
            var tes4BrightPassPsoDesc = psoDesc;
            tes4BrightPassPsoDesc.PixelShader = tes4BrightPassPs;
            tes4BrightPassPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
            _tes4BrightPassPso = _pipelineResources.CreateGraphics(8, tes4BrightPassPsoDesc);

            var tes4BlurPs = CompileEmbeddedShader("bloom.frag.hlsl", "mainTes4Blur", "ps_5_1");
            var tes4BlurPsoDesc = psoDesc;
            tes4BlurPsoDesc.PixelShader = tes4BlurPs;
            tes4BlurPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
            _tes4BlurPso = _pipelineResources.CreateGraphics(9, tes4BlurPsoDesc);

            // RTV heap: slots 0–1 = adapted-average ping-pong; then every possible reduction level;
            // final two slots = vertical BrightPassBlur intermediate + horizontal plain-blur output.
            _avgRtvHeap = TrackConstructionResource(
                device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
                {
                    Type = DescriptorHeapType.RenderTargetView,
                    DescriptorCount = RtvDescriptorCount,
                    Flags = DescriptorHeapFlags.None
                }));
            _avgRtvDescriptorSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            for (var i = 0; i < 2; i++)
            {
                _avgTextures[i] = TrackConstructionResource(device.CreateCommittedResource<ID3D12Resource>(
                    new HeapProperties(HeapType.Default),
                    HeapFlags.None,
                    ResourceDescription.Texture2D(Format.R16G16B16A16_Float, 1, 1, 1, 1, 1, 0,
                        ResourceFlags.AllowRenderTarget),
                    ResourceStates.PixelShaderResource));
                _avgTextures[i].Name = $"TonemapAvgColor1x1_{i}";
                var rtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
                rtv.Ptr += (nuint)(i * _avgRtvDescriptorSize);
                device.CreateRenderTargetView(_avgTextures[i], null, rtv);
            }

            _srvHeap = TrackConstructionResource(
                device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
                {
                    Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    DescriptorCount = SrvRingSlots * GroupsPerCall * SrvsPerCall,
                    Flags = DescriptorHeapFlags.ShaderVisible
                }));
            _srvDescriptorSize = device.GetDescriptorHandleIncrementSize(
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

            _constructionTransaction!.Commit();
            _constructionTransaction = null;
        }
        catch
        {
            _constructionTransaction?.Dispose();
            _constructionTransaction = null;
            throw;
        }
    }

    /// <summary>
    ///     Whether the latest recorded pass invalidated eye-adaptation history. Restored to the
    ///     preceding submitted value if that pass's command list is aborted.
    /// </summary>
    internal bool LastHistoryReset { get; private set; }

    /// <summary>
    ///     Comma-separated authoritative invalidation inputs for <see cref="LastHistoryReset" />;
    ///     transactional with the same command list.
    /// </summary>
    internal string? LastHistoryResetReason { get; private set; }

    /// <summary>Releases caller-retired HDR resources before their shared pipeline family, retaining failures for retry.</summary>
    /// <remarks>GPU completion or device removal must already be proven; this method does not wait on a fence.</remarks>
    /// <exception cref="AggregateException">A release failed; retain this pass and call disposal again on its creating thread.</exception>
    public void Dispose()
    {
        if (_retiredResources is null)
        {
            // Stop recording immediately, but do not let this marker suppress failed-release retries.
            // Register each lazy target separately so successful siblings are never disposed twice.
            _disposed = true;
            var retired = new RetiredResourceDisposal();
            retired.Add(_srvHeap, "tonemap SRV heap");
            retired.Add(_avgRtvHeap, "tonemap RTV heap");
            retired.Add(_avgTextures[0], "tonemap first average history");
            retired.Add(_avgTextures[1], "tonemap second average history");
            foreach (var texture in _reductionTextures)
            {
                retired.Add(texture, "tonemap active reduction target");
            }
            retired.Add(_brightPassBlurTexture, "tonemap active bright-pass target");
            retired.Add(_bloomTexture, "tonemap active bloom target");
            foreach (var texture in _alternateReductionTextures)
            {
                retired.Add(texture, "tonemap alternate reduction target");
            }
            retired.Add(_alternateBrightPassBlurTexture, "tonemap alternate bright-pass target");
            retired.Add(_alternateBloomTexture, "tonemap alternate bloom target");
            retired.Add(_pipelineResources, "tonemap pipeline family", 1);
            _retiredResources = retired;
        }
        _retiredResources.Dispose();
    }

    void IGpuCommandSubmissionParticipant12.OnCommandListSubmitted()
    {
        // Record mutated the working logical state in command order. Submission makes that state
        // authoritative, so committing only has to discard the rollback snapshot.
        ClearLogicalHistoryTransaction();
    }

    void IGpuCommandSubmissionParticipant12.OnCommandListAborted()
    {
        if (_historyBeforeCurrentCommandList is { } snapshot)
        {
            RestoreLogicalHistory(snapshot);
        }

        // Deliberately retain lazy reduction/bloom allocations and descriptor-ring progress. They
        // are valid reusable CPU allocations; only state that claimed GPU history is rolled back.
        ClearLogicalHistoryTransaction();
    }

    /// <summary>Transfers a newly created resource to rollback until the whole pass is constructed.</summary>
    /// <typeparam name="T">Owned disposable resource type.</typeparam>
    /// <param name="resource">Resource retained before any later initialization can fail.</param>
    /// <returns>The same resource, ready to store in its owning field.</returns>
    private T TrackConstructionResource<T>(T resource) where T : IDisposable
    {
        _constructionTransaction?.Track(resource);
        return resource;
    }

    /// <summary>
    ///     Records the fullscreen tonemap: samples <paramref name="hdrTexture" /> (must already be in
    ///     <see cref="ResourceStates.PixelShaderResource" />, single-sample, format
    ///     <paramref name="hdrFormat" />) and writes the tonemapped result to <paramref name="ldrRtv" />
    ///     (must already be a bound-able <see cref="ResourceStates.RenderTarget" /> of format
    ///     <see cref="GpuSceneFormats.LdrOutput" />). Sets its own descriptor heap + PSO; the caller
    ///     should re-establish its own heap afterward if it records further work.
    /// </summary>
    /// <param name="recorder">Owns the open command list and its submit/abort transaction.</param>
    /// <param name="settings">Operator + engine/cinematic parameters (see <see cref="GpuTonemapSettings" />).</param>
    /// <param name="enabled">
    ///     False → passthrough clamp. Bit identity with the legacy LDR path additionally requires
    ///     the static 8-bit scene-target kill-switch; a float MSAA target clamps after resolve.
    /// </param>
    public unsafe void Record(
        GpuCommandRecorder12 recorder,
        ID3D12Resource hdrTexture,
        Format hdrFormat,
        CpuDescriptorHandle ldrRtv,
        int width,
        int height,
        in GpuTonemapSettings settings,
        bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        // The descriptor/resource caches below are safe to retain if this list is abandoned, but
        // ping-pong selection and history identity describe GPU work that does not exist until the
        // list reaches the queue. Snapshot those logical fields once per open command list.
        BeginLogicalHistoryTransaction(recorder);
        var cmd = recorder.CommandList;

        // Cycle a fixed group layout per call. ADAPT reads the final reduction + previous history;
        // DS16 groups form the recursive /4 chain. The bloom groups read level zero and both
        // ping-pong targets; the composite reads HDR + adapted average + the family's final bloom.
        var slot = _srvCursor;
        _srvCursor = (_srvCursor + 1) % SrvRingSlots;
        var callBase = (nuint)(slot * GroupsPerCall * SrvsPerCall * _srvDescriptorSize);
        var heapCpu = _srvHeap.GetCPUDescriptorHandleForHeapStart();
        var heapGpu = _srvHeap.GetGPUDescriptorHandleForHeapStart();

        nuint GroupOffset(int group)
        {
            return callBase + (nuint)(group * SrvsPerCall * _srvDescriptorSize);
        }

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = hdrFormat,
            ViewDimension = D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 }
        };
        var avgSrvDesc = srvDesc with { Format = Format.R16G16B16A16_Float };

        var writeIdx = _avgWriteIndex;
        var readIdx = 1 - writeIdx;
        _avgWriteIndex = readIdx; // swap for the next call

        var executionPlan = GpuTonemapExecutionPlan.Create(
            enabled, settings.Mode, settings.BloomEnabled, settings.BrightScale);
        var engineMode = executionPlan.EngineMode;
        var adaptiveMode = executionPlan.AdaptiveMode;
        var historyKeyChanged = settings.HistoryKey != _lastHistoryKey;
        var adaptiveModeIdentity = adaptiveMode ? settings.Mode : (GpuTonemapMode?)null;
        var adaptiveModeChanged = adaptiveModeIdentity != _lastAdaptiveMode;
        var targetResourceChanged = !ReferenceEquals(hdrTexture, _lastHistoryTarget);
        var targetSizeChanged = width != _lastHistoryWidth || height != _lastHistoryHeight;
        var targetFormatChanged = hdrFormat != _lastHistoryFormat;
        var targetChanged = targetResourceChanged || targetSizeChanged || targetFormatChanged;
        LastHistoryReset = historyKeyChanged || adaptiveModeChanged || targetChanged;
        if (LastHistoryReset)
        {
            var resetReasons = new List<string>(5);
            if (historyKeyChanged) resetReasons.Add("history-key");
            if (adaptiveModeChanged) resetReasons.Add("adaptive-mode");
            if (targetResourceChanged) resetReasons.Add("target-resource");
            if (targetSizeChanged) resetReasons.Add("target-size");
            if (targetFormatChanged) resetReasons.Add("target-format");
            LastHistoryResetReason = string.Join(",", resetReasons);
        }
        else
        {
            LastHistoryResetReason = null;
        }

        if (LastHistoryReset)
        {
            _adaptPrimed = false;
            _lastHistoryKey = settings.HistoryKey;
            _lastAdaptiveMode = adaptiveModeIdentity;
            _lastHistoryTarget = hdrTexture;
            _lastHistoryWidth = width;
            _lastHistoryHeight = height;
            _lastHistoryFormat = hdrFormat;
        }

        var bloomActive = executionPlan.BloomActive;
        var skyrimRetailMode = engineMode && settings.Mode == GpuTonemapMode.EngineSkyrim;
        var tes4HdrBloom = engineMode
                           && settings.Mode == GpuTonemapMode.EngineFo3Fnv
                           && settings.ClassicBloomTopology ==
                           ClassicHdrBloomTopology.Tes4SeparateBrightPassCumulative;
        var historyWasPrimed = _adaptPrimed;
        ClassicHdrPassPlan classicPlan;
        if (!engineMode)
        {
            classicPlan = default;
        }
        else if (skyrimRetailMode)
        {
            classicPlan = ClassicHdrPassPlan.CreateSkyrim(
                width, height, historyWasPrimed, bloomActive);
        }
        else if (tes4HdrBloom)
        {
            classicPlan = ClassicHdrPassPlan.CreateTes4(
                width, height, bloomActive, settings.BlurPasses);
        }
        else
        {
            classicPlan = ClassicHdrPassPlan.Create(
                width, height, bloomActive, settings.BlurPasses);
        }

        if (engineMode)
        {
            // Allocate the complete no-history chain once. Later Skyrim frames omit the final 1x1
            // target draw and fuse it into ADAPT, but must not dispose in-flight first-frame targets.
            // bloomActive mirrors the execution plan, exactly as the non-Skyrim arm does by
            // reusing classicPlan: the allocation must cover the same slots the frame will draw.
            var allocationPlan = skyrimRetailMode
                ? ClassicHdrPassPlan.CreateSkyrim(width, height, false, bloomActive)
                : classicPlan;
            EnsureClassicTargets(allocationPlan);
        }

        // ADAPT group. Classic t0 is the recursive chain's final 1x1 value; the modern stand-in keeps
        // t0 as the full scene for mainAvg. t1 is always the previous adapted history.
        var cpuA = heapCpu;
        cpuA.Ptr += GroupOffset(AdaptGroup);
        var adaptSource = engineMode
            ? _reductionTextures[classicPlan.DownsampleDrawCount - 1]!
            : hdrTexture;
        _gpu.Device.CreateShaderResourceView(adaptSource, engineMode ? avgSrvDesc : srvDesc, cpuA);
        cpuA.Ptr += _srvDescriptorSize;
        _gpu.Device.CreateShaderResourceView(_avgTextures[readIdx], avgSrvDesc, cpuA);
        cpuA.Ptr += _srvDescriptorSize;
        _gpu.Device.CreateShaderResourceView(_avgTextures[readIdx], avgSrvDesc, cpuA);

        // Recursive DS16 groups. Pass zero reads the full-resolution scene; every later pass reads the
        // preceding /4 target. t1/t2 are benign live fillers for the shared descriptor table shape.
        if (engineMode)
        {
            for (var level = 0; level < classicPlan.DownsampleDrawCount; level++)
            {
                var cpuP = heapCpu;
                cpuP.Ptr += GroupOffset(DownsampleGroupStart + level);
                var source = level == 0 ? hdrTexture : _reductionTextures[level - 1]!;
                _gpu.Device.CreateShaderResourceView(source, level == 0 ? srvDesc : avgSrvDesc, cpuP);
                cpuP.Ptr += _srvDescriptorSize;
                _gpu.Device.CreateShaderResourceView(_avgTextures[readIdx], avgSrvDesc, cpuP);
                cpuP.Ptr += _srvDescriptorSize;
                _gpu.Device.CreateShaderResourceView(_avgTextures[readIdx], avgSrvDesc, cpuP);
            }
        }

        // BPBLUR consumes retained reduction level zero plus the freshly written adapted average.
        // BLUR consumes its intermediate. These descriptors can be created before either draw records;
        // both resources are transitioned back to SRV before their consumers execute.
        if (bloomActive)
        {
            var cpuBrightPass = heapCpu;
            cpuBrightPass.Ptr += GroupOffset(BrightPassBlurGroup);
            _gpu.Device.CreateShaderResourceView(_reductionTextures[0]!, avgSrvDesc, cpuBrightPass);
            cpuBrightPass.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuBrightPass);
            cpuBrightPass.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuBrightPass);

            var cpuBlur = heapCpu;
            cpuBlur.Ptr += GroupOffset(BlurGroup);
            _gpu.Device.CreateShaderResourceView(_brightPassBlurTexture!, avgSrvDesc, cpuBlur);
            cpuBlur.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuBlur);
            cpuBlur.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuBlur);

            var cpuReverseBlur = heapCpu;
            cpuReverseBlur.Ptr += GroupOffset(ReverseBlurGroup);
            _gpu.Device.CreateShaderResourceView(_bloomTexture!, avgSrvDesc, cpuReverseBlur);
            cpuReverseBlur.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuReverseBlur);
            cpuReverseBlur.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuReverseBlur);
        }

        // Main group: t2 = BPBLUR output, or the avg texture as a benign always-valid filler
        // when bloom is off (uParams3.z gates the term to zero).
        var cpuB = heapCpu;
        cpuB.Ptr += GroupOffset(CompositeGroup);
        _gpu.Device.CreateShaderResourceView(hdrTexture, srvDesc, cpuB);
        cpuB.Ptr += _srvDescriptorSize;
        _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuB);
        cpuB.Ptr += _srvDescriptorSize;
        var finalBloom = _avgTextures[writeIdx];
        if (bloomActive)
        {
            finalBloom = tes4HdrBloom ? _brightPassBlurTexture! : _bloomTexture!;
        }

        _gpu.Device.CreateShaderResourceView(finalBloom, avgSrvDesc, cpuB);

        // First engine-mode frame has no valid history. Use >1 as an explicit no-history sentinel so
        // ADAPT does not even sample the undefined freshly-created/reset texture; lerp(..., 1) is not
        // sufficient because a compiler MAD can propagate NaN from the unused endpoint.
        var adaptFactor = _adaptPrimed ? settings.AdaptFactor : 2f;
        var adaptFactorFast = _adaptPrimed ? settings.AdaptFactorFast : 2f;

        var modernFamily = settings.ModernFamily == ImageSpaceModernFamily.Fallout4 ? 1f : 0f;
        if (tes4HdrBloom)
        {
            modernFamily = -1f;
        }

        var p = stackalloc float[24]
        {
            settings.Exposure, enabled ? 1f : 0f, (float)settings.Mode, settings.TargetLum,
            settings.Saturation, settings.ContrastAvgLum, settings.Contrast, settings.Brightness,
            settings.TintR, settings.TintG, settings.TintB, settings.TintAmount,
            settings.UpperLumClamp, adaptFactor, bloomActive ? 1f : 0f, (float)settings.CinematicFlags,
            settings.Mode == GpuTonemapMode.EngineSkyrim ? adaptFactorFast : settings.AutoExposureMin,
            settings.AutoExposureMax, settings.MiddleGray, settings.TonemapE,
            settings.White, settings.EyeAdaptStrength, settings.ReceiveBloomThreshold, modernFamily
        };
        if (skyrimRetailMode)
        {
            var adaptSourceLevel = classicPlan.GetReductionLevel(classicPlan.DownsampleDrawCount - 1);
            p[17] = 1f / adaptSourceLevel.TargetWidth;
            p[18] = 1f / adaptSourceLevel.TargetHeight;
        }

        cmd.SetGraphicsRootSignature(_rootSignature);
        cmd.SetDescriptorHeaps(1, new[] { _srvHeap });
        cmd.SetGraphicsRoot32BitConstants(1, 24, p, 0);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Engine reduction: classic continues to 1x1; Skyrim uses its RGB/luminance/scalar stages and
        // on primed frames leaves the last 16 taps for ADAPT. Only TES4 consumes BlurPasses as a loop count.
        if (engineMode)
        {
            var downsampleConstants = stackalloc float[16];
            for (var i = 0; i < 16; i++) downsampleConstants[i] = 0f;
            for (var levelIndex = 0; levelIndex < classicPlan.DownsampleDrawCount; levelIndex++)
            {
                cmd.SetPipelineState(skyrimRetailMode
                    ? levelIndex switch
                    {
                        0 => _downsamplePso,
                        1 => _skyrimLuminancePso,
                        _ => _skyrimDownsamplePso
                    }
                    : _downsamplePso);
                var level = classicPlan.GetReductionLevel(levelIndex);
                downsampleConstants[4] = 1f / level.SourceWidth;
                downsampleConstants[5] = 1f / level.SourceHeight;
                cmd.SetGraphicsRoot32BitConstants(1, 16, downsampleConstants, 0);

                var rtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
                rtv.Ptr += (nuint)((_classicRtvBank * RtvBankSize + ReductionRtvStart + levelIndex) *
                                   _avgRtvDescriptorSize);
                var gpuDownsample = heapGpu;
                gpuDownsample.Ptr += GroupOffset(DownsampleGroupStart + levelIndex);
                cmd.SetGraphicsRootDescriptorTable(0, gpuDownsample);

                var target = _reductionTextures[levelIndex]!;
                cmd.ResourceBarrierTransition(
                    target, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
                cmd.OMSetRenderTargets(rtv);
                cmd.RSSetViewport(new Viewport(
                    0, 0, level.TargetWidth, level.TargetHeight, 0f, 1f));
                cmd.RSSetScissorRect(level.TargetWidth, level.TargetHeight);
                cmd.DrawInstanced(3, 1, 0, 0);
                cmd.ResourceBarrierTransition(
                    target, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
            }

            cmd.SetGraphicsRoot32BitConstants(1, 24, p, 0);
        }

        // Render the 1x1 adapted average (reads previous history at t1 while writing the other side).
        // Classic uses the recursively reduced t0; the modern stand-in still computes its own grid.
        if (adaptiveMode)
        {
            _adaptPrimed = true;
            var writeRtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
            writeRtv.Ptr += (nuint)(writeIdx * _avgRtvDescriptorSize);
            var gpuA = heapGpu;
            gpuA.Ptr += GroupOffset(AdaptGroup);
            cmd.SetGraphicsRootDescriptorTable(0, gpuA);
            cmd.ResourceBarrierTransition(
                _avgTextures[writeIdx], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            cmd.OMSetRenderTargets(writeRtv);
            cmd.RSSetViewport(new Viewport(0, 0, 1, 1, 0f, 1f));
            cmd.RSSetScissorRect(1, 1);
            cmd.SetPipelineState(engineMode ? _adaptPso : _avgPso);
            cmd.DrawInstanced(3, 1, 0, 0);
            cmd.ResourceBarrierTransition(
                _avgTextures[writeIdx], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
        }

        // Recovered family-specific bloom topology. FO3/FNV/Oldrim fuse bright extraction into the
        // first vertical blur and record one horizontal blur. TES4 records HDR005 once, then
        // cumulatively ping-pongs HDR000/1/2 on both axes abs(iNumBlurpasses) times.
        if (bloomActive)
        {
            // ImageSpaceEffectHDR truncates BlurRadius before selecting BPBLUR3..15, then clamps the
            // resulting radius to 1..7. In particular, authored 3.9 selects BPBLUR7, not BPBLUR9.
            var kernelRadius = Math.Clamp((int)settings.BlurRadius, 1, 7);
            var b = stackalloc float[16];
            for (var i = 0; i < 16; i++) b[i] = 0f;
            b[0] = settings.BrightClamp;
            b[1] = settings.BrightScale;
            b[2] = kernelRadius;
            cmd.RSSetViewport(new Viewport(0, 0, _bloomWidth, _bloomHeight, 0f, 1f));
            cmd.RSSetScissorRect(_bloomWidth, _bloomHeight);

            void RecordBloomDraw(
                int sourceGroup,
                ID3D12Resource target,
                int targetRtvSlot,
                ID3D12PipelineState pipelineState)
            {
                var targetRtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
                targetRtv.Ptr += (nuint)((_classicRtvBank * RtvBankSize + targetRtvSlot) *
                                         _avgRtvDescriptorSize);
                var sourceGpu = heapGpu;
                sourceGpu.Ptr += GroupOffset(sourceGroup);
                cmd.SetGraphicsRootDescriptorTable(0, sourceGpu);
                cmd.ResourceBarrierTransition(
                    target, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
                cmd.OMSetRenderTargets(targetRtv);
                cmd.SetPipelineState(pipelineState);
                cmd.DrawInstanced(3, 1, 0, 0);
                cmd.ResourceBarrierTransition(
                    target, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
            }

            if (tes4HdrBloom)
            {
                // HDR005 is a one-sample bright filter; no blur axis participates in this draw.
                b[4] = 0f;
                b[5] = 0f;
                cmd.SetGraphicsRoot32BitConstants(1, 16, b, 0);
                RecordBloomDraw(
                    BrightPassBlurGroup,
                    _brightPassBlurTexture!,
                    BrightPassBlurRtvSlot,
                    _tes4BrightPassPso);

                for (var blurPair = 0; blurPair < classicPlan.BlurPairCount; blurPair++)
                {
                    b[4] = 0f;
                    b[5] = 1f / _bloomHeight;
                    cmd.SetGraphicsRoot32BitConstants(1, 16, b, 0);
                    RecordBloomDraw(
                        BlurGroup,
                        _bloomTexture!,
                        BlurRtvSlot,
                        _tes4BlurPso);

                    b[4] = 1f / _bloomWidth;
                    b[5] = 0f;
                    cmd.SetGraphicsRoot32BitConstants(1, 16, b, 0);
                    RecordBloomDraw(
                        ReverseBlurGroup,
                        _brightPassBlurTexture!,
                        BrightPassBlurRtvSlot,
                        _tes4BlurPso);
                }
            }
            else
            {
                // ImageSpaceEffectBlur::UpdateParams uploads (0, 1/height) to BPBLUR first.
                b[4] = 0f;
                b[5] = 1f / _bloomHeight;
                cmd.SetGraphicsRoot32BitConstants(1, 16, b, 0);
                RecordBloomDraw(
                    BrightPassBlurGroup,
                    _brightPassBlurTexture!,
                    BrightPassBlurRtvSlot,
                    _bloomPso);

                // The second shader receives (1/width, 0) and does not repeat bright extraction.
                b[4] = 1f / _bloomWidth;
                b[5] = 0f;
                cmd.SetGraphicsRoot32BitConstants(1, 16, b, 0);
                RecordBloomDraw(
                    BlurGroup,
                    _bloomTexture!,
                    BlurRtvSlot,
                    _blurPso);
            }

            cmd.SetGraphicsRoot32BitConstants(1, 24, p, 0);
        }

        var gpuB = heapGpu;
        gpuB.Ptr += GroupOffset(CompositeGroup);
        cmd.SetGraphicsRootDescriptorTable(0, gpuB);
        cmd.OMSetRenderTargets(ldrRtv);
        cmd.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        cmd.RSSetScissorRect(width, height);
        cmd.SetPipelineState(_pso);
        cmd.DrawInstanced(3, 1, 0, 0);
    }

    private void BeginLogicalHistoryTransaction(GpuCommandRecorder12 recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        if (_historyTransactionRecorder is not null)
        {
            if (!ReferenceEquals(_historyTransactionRecorder, recorder))
            {
                throw new InvalidOperationException(
                    "A tonemap pass cannot participate in multiple open command lists.");
            }

            return;
        }

        var snapshot = CaptureLogicalHistory();
        recorder.EnlistCurrentFrame(this);
        _historyBeforeCurrentCommandList = snapshot;
        _historyTransactionRecorder = recorder;
    }

    private TonemapLogicalHistoryState CaptureLogicalHistory()
    {
        return new TonemapLogicalHistoryState(
            _adaptPrimed,
            _avgWriteIndex,
            _lastAdaptiveMode,
            _lastHistoryFormat,
            _lastHistoryHeight,
            _lastHistoryKey,
            _lastHistoryTarget,
            _lastHistoryWidth,
            LastHistoryReset,
            LastHistoryResetReason);
    }

    private void RestoreLogicalHistory(in TonemapLogicalHistoryState snapshot)
    {
        _adaptPrimed = snapshot.AdaptPrimed;
        _avgWriteIndex = snapshot.AvgWriteIndex;
        _lastAdaptiveMode = snapshot.LastAdaptiveMode;
        _lastHistoryFormat = snapshot.LastHistoryFormat;
        _lastHistoryHeight = snapshot.LastHistoryHeight;
        _lastHistoryKey = snapshot.LastHistoryKey;
        _lastHistoryTarget = snapshot.LastHistoryTarget;
        _lastHistoryWidth = snapshot.LastHistoryWidth;
        LastHistoryReset = snapshot.LastHistoryReset;
        LastHistoryResetReason = snapshot.LastHistoryResetReason;
    }

    private void ClearLogicalHistoryTransaction()
    {
        _historyBeforeCurrentCommandList = null;
        _historyTransactionRecorder = null;
    }

    /// <summary>
    ///     Lazily creates every recursive /4 reduction target plus the quarter-resolution vertical
    ///     BrightPassBlur intermediate and horizontal plain-blur output. Floor- and ceiling-quarter
    ///     families are retained in separate descriptor banks, so a live game/mode switch never
    ///     destroys resources still referenced by an in-flight frame. Dispose-and-recreate on size
    ///     change remains safe: the live path resizes via <c>GpuSwapChainSurface12</c>'s GPU-idle
    ///     Resize, and offscreen targets are fixed-size for their lifetime.
    /// </summary>
    private void EnsureClassicTargets(in ClassicHdrPassPlan plan)
    {
        if (ActiveClassicTargetsMatch(plan))
        {
            return;
        }

        var activeAllocated = _reductionTextures[0] is not null;
        if (activeAllocated &&
            (_classicSourceWidth != plan.SourceWidth || _classicSourceHeight != plan.SourceHeight))
        {
            // The size-change contract guarantees GPU idle, so both cached families can be
            // reclaimed here. Rule-only switches below deliberately retain the old family.
            DisposeActiveClassicTargets();
            DisposeAlternateClassicTargets();
            activeAllocated = false;
        }

        if (AlternateClassicFamilyMatches(plan))
        {
            SwapClassicTargetSets();
            if (ActiveClassicTargetsMatch(plan))
            {
                return;
            }

            activeAllocated = true;
        }
        else if (activeAllocated && _classicDimensionRule != plan.DimensionRule)
        {
            if (_alternateReductionTextures[0] is not null)
            {
                throw new InvalidOperationException(
                    "Both retained HDR target families are occupied by an unexpected layout.");
            }

            // Move the old family, its metadata, and its untouched RTV bank aside. Its resources
            // remain alive until a GPU-idle resize or pass disposal.
            SwapClassicTargetSets();
            activeAllocated = false;
        }

        if (!activeAllocated)
        {
            _classicSourceWidth = plan.SourceWidth;
            _classicSourceHeight = plan.SourceHeight;
            _classicDimensionRule = plan.DimensionRule;
            _reductionLevelCount = 0;
        }

        for (var levelIndex = _reductionLevelCount;
             levelIndex < plan.DownsampleDrawCount;
             levelIndex++)
        {
            var level = plan.GetReductionLevel(levelIndex);
            var texture = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                ResourceDescription.Texture2D(
                    Format.R16G16B16A16_Float,
                    (uint)level.TargetWidth,
                    (uint)level.TargetHeight,
                    1, 1, 1, 0,
                    ResourceFlags.AllowRenderTarget),
                ResourceStates.PixelShaderResource);
            try
            {
                texture.Name =
                    $"TonemapClassicDownsample_Bank{_classicRtvBank}_{levelIndex}_{level.TargetWidth}x{level.TargetHeight}";
                var rtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
                rtv.Ptr += (nuint)((_classicRtvBank * RtvBankSize + ReductionRtvStart + levelIndex) *
                                   _avgRtvDescriptorSize);
                _gpu.Device.CreateRenderTargetView(texture, null, rtv);
            }
            catch
            {
                // Do not leak an uninstalled COM resource when naming/RTV creation fails. Earlier
                // levels remain cached and their count below lets a later attempt resume precisely.
                texture.Dispose();
                throw;
            }

            _reductionTextures[levelIndex] = texture;
            _reductionLevelCount = levelIndex + 1;
        }

        _reductionLevelCount = Math.Max(_reductionLevelCount, plan.DownsampleDrawCount);

        var bloomLevel = plan.GetReductionLevel(0);
        _bloomWidth = bloomLevel.TargetWidth;
        _bloomHeight = bloomLevel.TargetHeight;
        if (plan.BloomEnabled && _brightPassBlurTexture is null)
        {
            _brightPassBlurTexture = CreateClassicBloomTarget(
                $"TonemapClassicBrightPassVertical_Bank{_classicRtvBank}_{_bloomWidth}x{_bloomHeight}");
            var brightPassRtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
            brightPassRtv.Ptr +=
                (nuint)((_classicRtvBank * RtvBankSize + BrightPassBlurRtvSlot) * _avgRtvDescriptorSize);
            _gpu.Device.CreateRenderTargetView(_brightPassBlurTexture, null, brightPassRtv);
        }

        if (plan.BloomEnabled && _bloomTexture is null)
        {
            _bloomTexture = CreateClassicBloomTarget(
                $"TonemapClassicBlurHorizontal_Bank{_classicRtvBank}_{_bloomWidth}x{_bloomHeight}");
            var blurRtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
            blurRtv.Ptr +=
                (nuint)((_classicRtvBank * RtvBankSize + BlurRtvSlot) * _avgRtvDescriptorSize);
            _gpu.Device.CreateRenderTargetView(_bloomTexture, null, blurRtv);
        }
    }

    private ID3D12Resource CreateClassicBloomTarget(string name)
    {
        var texture = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            ResourceDescription.Texture2D(
                Format.R16G16B16A16_Float,
                (uint)_bloomWidth,
                (uint)_bloomHeight,
                1, 1, 1, 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource);
        texture.Name = name;
        return texture;
    }

    private bool ActiveClassicTargetsMatch(in ClassicHdrPassPlan plan)
    {
        return _reductionTextures[0] is not null
               && (!plan.BloomEnabled ||
                   (_brightPassBlurTexture is not null && _bloomTexture is not null))
               && _classicSourceWidth == plan.SourceWidth
               && _classicSourceHeight == plan.SourceHeight
               && _classicDimensionRule == plan.DimensionRule
               && _reductionLevelCount >= plan.DownsampleDrawCount;
    }

    private bool AlternateClassicFamilyMatches(in ClassicHdrPassPlan plan)
    {
        return _alternateReductionTextures[0] is not null
               && _alternateClassicSourceWidth == plan.SourceWidth
               && _alternateClassicSourceHeight == plan.SourceHeight
               && _alternateClassicDimensionRule == plan.DimensionRule;
    }

    private void SwapClassicTargetSets()
    {
        (_reductionTextures, _alternateReductionTextures) =
            (_alternateReductionTextures, _reductionTextures);
        (_brightPassBlurTexture, _alternateBrightPassBlurTexture) =
            (_alternateBrightPassBlurTexture, _brightPassBlurTexture);
        (_bloomTexture, _alternateBloomTexture) = (_alternateBloomTexture, _bloomTexture);
        (_bloomWidth, _alternateBloomWidth) = (_alternateBloomWidth, _bloomWidth);
        (_bloomHeight, _alternateBloomHeight) = (_alternateBloomHeight, _bloomHeight);
        (_classicSourceWidth, _alternateClassicSourceWidth) =
            (_alternateClassicSourceWidth, _classicSourceWidth);
        (_classicSourceHeight, _alternateClassicSourceHeight) =
            (_alternateClassicSourceHeight, _classicSourceHeight);
        (_classicDimensionRule, _alternateClassicDimensionRule) =
            (_alternateClassicDimensionRule, _classicDimensionRule);
        (_reductionLevelCount, _alternateReductionLevelCount) =
            (_alternateReductionLevelCount, _reductionLevelCount);
        (_classicRtvBank, _alternateRtvBank) = (_alternateRtvBank, _classicRtvBank);
    }

    private void DisposeActiveClassicTargets()
    {
        DisposeClassicResources(_reductionTextures, _brightPassBlurTexture, _bloomTexture);
        _brightPassBlurTexture = null;
        _bloomTexture = null;
        _classicSourceWidth = 0;
        _classicSourceHeight = 0;
        _reductionLevelCount = 0;
        _bloomWidth = 0;
        _bloomHeight = 0;
    }

    private void DisposeAlternateClassicTargets()
    {
        DisposeClassicResources(
            _alternateReductionTextures,
            _alternateBrightPassBlurTexture,
            _alternateBloomTexture);
        _alternateBrightPassBlurTexture = null;
        _alternateBloomTexture = null;
        _alternateClassicSourceWidth = 0;
        _alternateClassicSourceHeight = 0;
        _alternateReductionLevelCount = 0;
        _alternateBloomWidth = 0;
        _alternateBloomHeight = 0;
    }

    private static void DisposeClassicResources(
        ID3D12Resource?[] reductionTextures,
        ID3D12Resource? brightPassBlurTexture,
        ID3D12Resource? bloomTexture)
    {
        for (var i = 0; i < reductionTextures.Length; i++)
        {
            reductionTextures[i]?.Dispose();
            reductionTextures[i] = null;
        }

        brightPassBlurTexture?.Dispose();
        bloomTexture?.Dispose();
    }

    /// <summary>Gets an embedded shader permutation through the application cache and Shared compiler.</summary>
    /// <param name="name">Embedded shader file name.</param>
    /// <param name="entryPoint">HLSL entry point.</param>
    /// <param name="profile">Native compiler target profile.</param>
    /// <returns>Read-only cached DXBC passed directly to native pipeline creation without a payload copy.</returns>
    private static ReadOnlyMemory<byte> CompileEmbeddedShader(string name, string entryPoint, string profile)
    {
        return GpuShaderCompiler12.Compile(name, entryPoint, profile);
    }

    private readonly record struct TonemapLogicalHistoryState(
        bool AdaptPrimed,
        int AvgWriteIndex,
        GpuTonemapMode? LastAdaptiveMode,
        Format LastHistoryFormat,
        int LastHistoryHeight,
        ulong LastHistoryKey,
        ID3D12Resource? LastHistoryTarget,
        int LastHistoryWidth,
        bool LastHistoryReset,
        string? LastHistoryResetReason);

    private sealed class TonemapConstructionTransaction : IDisposable
    {
        private readonly List<IDisposable> _creationOrder = new(16);

        private readonly HashSet<IDisposable> _owned =
            new(16, ReferenceEqualityComparer.Instance);

        public void Dispose()
        {
            for (var index = _creationOrder.Count - 1; index >= 0; index--)
            {
                var resource = _creationOrder[index];
                if (_owned.Remove(resource))
                {
                    resource.Dispose();
                }
            }

            _creationOrder.Clear();
        }

        internal void Track(IDisposable resource)
        {
            if (_owned.Add(resource))
            {
                _creationOrder.Add(resource);
            }
        }

        internal void Commit()
        {
            _owned.Clear();
            _creationOrder.Clear();
        }
    }
}
