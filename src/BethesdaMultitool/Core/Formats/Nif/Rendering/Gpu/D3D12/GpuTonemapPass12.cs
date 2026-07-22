using System.Reflection;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Self-contained fullscreen tonemap pass: samples the HDR scene color
///     (<see cref="GpuSceneFormats.SceneColor" /> — a float target that preserves values &gt; 1) and
///     maps it to the 8-bit display target (<see cref="GpuSceneFormats.LdrOutput" />) via an exposure
///     multiply + ACES filmic curve (<c>tonemap.frag.hlsl</c>). This is the HDR/imagespace resolve
///     stage: emissive glow (neon ×7, radioactive goo ×2), sun specular, and future per-game
///     imagespace scales all live above 1 in the scene target and are rolled off here instead of being
///     clipped flat white by an 8-bit render target — which also lifts midtones out of the old
///     "too dark" look.
///     <para>
///         In engine mode the pass also records the recovered bloom chain
///         (<c>bloom.frag.hlsl</c>): recursive DownSample16 reduction, vertical BrightPassBlur plus
///         horizontal plain blur at the retained quarter-resolution level, then
///         <c>bloom·(0.5/denom)</c> composite.
///     </para>
///     <para>
///         Owns its own root signature (SRV table t0–t2 + 24 root constants b0 + a linear-clamp static
///         sampler), PSOs, and a small shader-visible SRV ring heap for the per-call texture views.
///         Both scene targets (<see cref="GpuOffscreenSceneTarget12" /> headless +
///         <c>GpuSwapChainSurface12</c> live) drive it once per frame; the ring survives the
///         in-flight frames. The caller owns the resource-state transitions (HDR source →
///         <see cref="ResourceStates.PixelShaderResource" />, LDR dest →
///         <see cref="ResourceStates.RenderTarget" />) around <see cref="Record" />.
///     </para>
/// </summary>
internal sealed class GpuTonemapPass12 : IDisposable
{
    // SRV ring depth: one call = fixed 3-descriptor groups (t0/t1/t2) for ADAPT, every possible
    // recursive DownSample16 level, both bloom rows, and the main composite. Groups are cycled so a
    // view is never overwritten while the previous frame's tonemap draw still reads it.
    // framesInFlight (2) × a couple of scene targets is comfortably under 8 ring slots.
    private const int SrvRingSlots = 8;
    private const int SrvsPerCall = 3;
    private const int AdaptGroup = 0;
    private const int DownsampleGroupStart = 1;
    private const int BrightPassBlurGroup = DownsampleGroupStart + ClassicHdrPassPlan.MaxReductionLevels;
    private const int BlurGroup = BrightPassBlurGroup + 1;
    private const int CompositeGroup = BlurGroup + 1;
    private const int GroupsPerCall = CompositeGroup + 1;
    private const int ReductionRtvStart = 2;
    private const int BrightPassBlurRtvSlot = ReductionRtvStart + ClassicHdrPassPlan.MaxReductionLevels;
    private const int BlurRtvSlot = BrightPassBlurRtvSlot + 1;
    private const int RtvDescriptorCount = BlurRtvSlot + 1;

    private readonly GpuDevice12 _gpu;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pso;
    private readonly ID3D12PipelineState _avgPso;
    private readonly ID3D12PipelineState _adaptPso;
    private readonly ID3D12PipelineState _downsamplePso;
    private readonly ID3D12PipelineState _bloomPso;
    private readonly ID3D12PipelineState _blurPso;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly ID3D12DescriptorHeap _avgRtvHeap;
    // Ping-pong pair of 1×1 adapted-average targets: each engine-mode frame reads the PREVIOUS
    // frame's adapted average (temporal eye adaptation) while writing the new one; the main pass
    // then samples the freshly written side. Both start in PixelShaderResource.
    private readonly ID3D12Resource[] _avgTextures = new ID3D12Resource[2];
    // Recursive /4 DownSample16 targets. Level 0 is retained as the BrightPassBlur source; the final
    // level is 1x1 and feeds ADAPT. The two bloom targets hold vertical BPBLUR and horizontal BLUR.
    private readonly ID3D12Resource?[] _reductionTextures =
        new ID3D12Resource?[ClassicHdrPassPlan.MaxReductionLevels];
    private ID3D12Resource? _brightPassBlurTexture;
    private ID3D12Resource? _bloomTexture;
    private readonly uint _avgRtvDescriptorSize;
    private readonly uint _srvDescriptorSize;
    private int _bloomWidth;
    private int _bloomHeight;
    private int _classicSourceWidth;
    private int _classicSourceHeight;
    private int _reductionLevelCount;
    private int _avgWriteIndex;
    private bool _adaptPrimed;
    private ulong _lastHistoryKey = ulong.MaxValue;
    private bool _lastAdaptiveMode;
    private ID3D12Resource? _lastHistoryTarget;
    private int _lastHistoryWidth;
    private int _lastHistoryHeight;
    private Format _lastHistoryFormat = Format.Unknown;
    private int _srvCursor;
    private bool _disposed;

    /// <summary>Whether the most recently recorded pass invalidated its eye-adaptation history.</summary>
    internal bool LastHistoryReset { get; private set; }

    /// <summary>Comma-separated authoritative invalidation inputs for <see cref="LastHistoryReset"/>.</summary>
    internal string? LastHistoryResetReason { get; private set; }

    public GpuTonemapPass12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;

        // Root: [0] SRV table (t0 = HDR scene, t1 = 1×1 adapted average color, t2 = bloom); [1]
        // 24×32-bit root constants (b0, six float4s — tonemap/cinematic + modern semantic params
        // draws, repacked as bloom params for the bloom draws); one linear-clamp static sampler (s0).
        var srvRange = new DescriptorRange1
        {
            RangeType = DescriptorRangeType.ShaderResourceView,
            NumDescriptors = SrvsPerCall,
            BaseShaderRegister = 0,
            RegisterSpace = 0,
            Flags = DescriptorRangeFlags.DescriptorsVolatile,
            OffsetInDescriptorsFromTableStart = 0,
        };
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);
        var rootConstants = new RootParameter1(
            new RootConstants(shaderRegister: 0, registerSpace: 0, num32BitValues: 24),
            ShaderVisibility.Pixel);

        var sampler = new StaticSamplerDescription(
            shaderRegister: 0,
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
            ShaderVisibility.Pixel,
            registerSpace: 0);

        var desc = new RootSignatureDescription1(
            RootSignatureFlags.None,
            new[] { srvTable, rootConstants },
            new[] { sampler });
        _rootSignature = device.CreateRootSignature(desc);

        var vs = CompileEmbeddedShader("tonemap.vert.hlsl", "main", "vs_5_1");
        var ps = CompileEmbeddedShader("tonemap.frag.hlsl", "main", "ps_5_1");

        var blend = new D12.BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
        {
            BlendEnable = false,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = vs,
            PixelShader = ps,
            BlendState = blend,
            RasterizerState = new D12.RasterizerDescription
            {
                FillMode = D12.FillMode.Solid,
                CullMode = D12.CullMode.None,
                DepthClipEnable = false,
            },
            DepthStencilState = new D12.DepthStencilDescription
            {
                DepthEnable = false,
                DepthWriteMask = D12.DepthWriteMask.Zero,
                DepthFunc = ComparisonFunction.Always,
                StencilEnable = false,
            },
            InputLayout = new InputLayoutDescription(Array.Empty<InputElementDescription>()),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { GpuSceneFormats.LdrOutput },
            DepthStencilFormat = Format.Unknown,
            SampleDescription = new SampleDescription(1, 0), // the LDR output/backbuffer is single-sample
            SampleMask = uint.MaxValue,
        };
        _pso = device.CreateGraphicsPipelineState(psoDesc);

        // The modern stand-in still computes a sparse average in mainAvg. Classic mode instead runs
        // the recursive reduction below and mainAdapt only performs temporal adaptation/clamping.
        var avgPs = CompileEmbeddedShader("tonemap.frag.hlsl", "mainAvg", "ps_5_1");
        var avgPsoDesc = psoDesc;
        avgPsoDesc.PixelShader = avgPs;
        avgPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
        _avgPso = device.CreateGraphicsPipelineState(avgPsoDesc);

        var adaptPs = CompileEmbeddedShader("tonemap.frag.hlsl", "mainAdapt", "ps_5_1");
        var adaptPsoDesc = psoDesc;
        adaptPsoDesc.PixelShader = adaptPs;
        adaptPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
        _adaptPso = device.CreateGraphicsPipelineState(adaptPsoDesc);

        // Recovered engine bloom: explicit DownSample16, then the two rows inside the selected
        // ImageSpaceEffectBlur effect: vertical BrightPassBlur followed by horizontal plain blur.
        var downsamplePs = CompileEmbeddedShader("bloom.frag.hlsl", "mainDownsample16", "ps_5_1");
        var downsamplePsoDesc = psoDesc;
        downsamplePsoDesc.PixelShader = downsamplePs;
        downsamplePsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
        _downsamplePso = device.CreateGraphicsPipelineState(downsamplePsoDesc);

        var bloomPs = CompileEmbeddedShader("bloom.frag.hlsl", "main", "ps_5_1");
        var bloomPsoDesc = psoDesc;
        bloomPsoDesc.PixelShader = bloomPs;
        bloomPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
        _bloomPso = device.CreateGraphicsPipelineState(bloomPsoDesc);

        var blurPs = CompileEmbeddedShader("bloom.frag.hlsl", "mainBlur", "ps_5_1");
        var blurPsoDesc = psoDesc;
        blurPsoDesc.PixelShader = blurPs;
        blurPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
        _blurPso = device.CreateGraphicsPipelineState(blurPsoDesc);

        // RTV heap: slots 0–1 = adapted-average ping-pong; then every possible reduction level;
        // final two slots = vertical BrightPassBlur intermediate + horizontal plain-blur output.
        _avgRtvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = RtvDescriptorCount,
            Flags = DescriptorHeapFlags.None,
        });
        _avgRtvDescriptorSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        for (var i = 0; i < 2; i++)
        {
            _avgTextures[i] = device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                ResourceDescription.Texture2D(Format.R16G16B16A16_Float, 1, 1, 1, 1, 1, 0,
                    ResourceFlags.AllowRenderTarget),
                ResourceStates.PixelShaderResource);
            _avgTextures[i].Name = $"TonemapAvgColor1x1_{i}";
            var rtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
            rtv.Ptr += (nuint)(i * _avgRtvDescriptorSize);
            device.CreateRenderTargetView(_avgTextures[i], null, rtv);
        }

        _srvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            DescriptorCount = SrvRingSlots * GroupsPerCall * SrvsPerCall,
            Flags = DescriptorHeapFlags.ShaderVisible,
        });
        _srvDescriptorSize = device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    /// <summary>
    ///     Records the fullscreen tonemap: samples <paramref name="hdrTexture" /> (must already be in
    ///     <see cref="ResourceStates.PixelShaderResource" />, single-sample, format
    ///     <paramref name="hdrFormat" />) and writes the tonemapped result to <paramref name="ldrRtv" />
    ///     (must already be a bound-able <see cref="ResourceStates.RenderTarget" /> of format
    ///     <see cref="GpuSceneFormats.LdrOutput" />). Sets its own descriptor heap + PSO; the caller
    ///     should re-establish its own heap afterward if it records further work.
    /// </summary>
    /// <param name="settings">Operator + engine/cinematic parameters (see <see cref="GpuTonemapSettings" />).</param>
    /// <param name="enabled">False → passthrough clamp (bit-identical to the legacy LDR path).</param>
    public unsafe void Record(
        ID3D12GraphicsCommandList cmd,
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

        // Cycle a fixed group layout per call. ADAPT reads the final reduction + previous history;
        // DS16 groups form the recursive /4 chain; BPBLUR reads level 0 + the fresh adapted average;
        // BLUR reads the BPBLUR intermediate; the composite reads HDR + adapted average + bloom.
        var slot = _srvCursor;
        _srvCursor = (_srvCursor + 1) % SrvRingSlots;
        var callBase = (nuint)(slot * GroupsPerCall * SrvsPerCall * _srvDescriptorSize);
        var heapCpu = _srvHeap.GetCPUDescriptorHandleForHeapStart();
        var heapGpu = _srvHeap.GetGPUDescriptorHandleForHeapStart();
        nuint GroupOffset(int group) => callBase + (nuint)(group * SrvsPerCall * _srvDescriptorSize);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = hdrFormat,
            ViewDimension = D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        };
        var avgSrvDesc = srvDesc with { Format = Format.R16G16B16A16_Float };

        var writeIdx = _avgWriteIndex;
        var readIdx = 1 - writeIdx;
        _avgWriteIndex = readIdx; // swap for the next call

        var engineMode = enabled && settings.Mode == GpuTonemapMode.EngineFo3Fnv;
        var adaptiveMode = engineMode || (enabled && settings.Mode == GpuTonemapMode.CreationModern);
        var historyKeyChanged = settings.HistoryKey != _lastHistoryKey;
        var adaptiveModeChanged = adaptiveMode != _lastAdaptiveMode;
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
            _lastAdaptiveMode = adaptiveMode;
            _lastHistoryTarget = hdrTexture;
            _lastHistoryWidth = width;
            _lastHistoryHeight = height;
            _lastHistoryFormat = hdrFormat;
        }
        var bloomActive = engineMode && settings.BloomEnabled && settings.BrightScale > 0f;
        var classicPlan = engineMode
            ? ClassicHdrPassPlan.Create(width, height, bloomActive, settings.BlurPasses)
            : default;
        if (engineMode)
        {
            EnsureClassicTargets(classicPlan);
        }

        // ADAPT group. Classic t0 is the recursive chain's final 1x1 value; the modern stand-in keeps
        // t0 as the full scene for mainAvg. t1 is always the previous adapted history.
        var cpuA = heapCpu; cpuA.Ptr += GroupOffset(AdaptGroup);
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
                var cpuP = heapCpu; cpuP.Ptr += GroupOffset(DownsampleGroupStart + level);
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
            var cpuBrightPass = heapCpu; cpuBrightPass.Ptr += GroupOffset(BrightPassBlurGroup);
            _gpu.Device.CreateShaderResourceView(_reductionTextures[0]!, avgSrvDesc, cpuBrightPass);
            cpuBrightPass.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuBrightPass);
            cpuBrightPass.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuBrightPass);

            var cpuBlur = heapCpu; cpuBlur.Ptr += GroupOffset(BlurGroup);
            _gpu.Device.CreateShaderResourceView(_brightPassBlurTexture!, avgSrvDesc, cpuBlur);
            cpuBlur.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuBlur);
            cpuBlur.Ptr += _srvDescriptorSize;
            _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuBlur);
        }

        // Main group: t2 = BPBLUR output, or the avg texture as a benign always-valid filler
        // when bloom is off (uParams3.z gates the term to zero).
        var cpuB = heapCpu; cpuB.Ptr += GroupOffset(CompositeGroup);
        _gpu.Device.CreateShaderResourceView(hdrTexture, srvDesc, cpuB);
        cpuB.Ptr += _srvDescriptorSize;
        _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuB);
        cpuB.Ptr += _srvDescriptorSize;
        var finalBloom = bloomActive ? _bloomTexture! : _avgTextures[writeIdx];
        _gpu.Device.CreateShaderResourceView(finalBloom, avgSrvDesc, cpuB);

        // First engine-mode frame has no valid history. Use >1 as an explicit no-history sentinel so
        // ADAPT does not even sample the undefined freshly-created/reset texture; lerp(..., 1) is not
        // sufficient because a compiler MAD can propagate NaN from the unused endpoint.
        var adaptFactor = _adaptPrimed ? settings.AdaptFactor : 2f;

        var modernFamily = settings.ModernFamily ==
            BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc.ImageSpaceModernFamily.Fallout4 ? 1f : 0f;
        var p = stackalloc float[24]
        {
            settings.Exposure, enabled ? 1f : 0f, (float)settings.Mode, settings.TargetLum,
            settings.Saturation, settings.ContrastAvgLum, settings.Contrast, settings.Brightness,
            settings.TintR, settings.TintG, settings.TintB, settings.TintAmount,
            settings.UpperLumClamp, adaptFactor, bloomActive ? 1f : 0f, (float)settings.CinematicFlags,
            settings.AutoExposureMin, settings.AutoExposureMax, settings.MiddleGray, settings.TonemapE,
            settings.White, settings.EyeAdaptStrength, settings.ReceiveBloomThreshold, modernFamily,
        };

        cmd.SetGraphicsRootSignature(_rootSignature);
        cmd.SetDescriptorHeaps(1, new[] { _srvHeap });
        cmd.SetGraphicsRoot32BitConstants(1, 24, p, 0);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Classic reduction: retain level zero for bloom, then continue recursively to the 1x1 value
        // consumed by ADAPT. The authored BlurPasses scalar never changes this draw count.
        if (engineMode)
        {
            var downsampleConstants = stackalloc float[16];
            for (var i = 0; i < 16; i++) downsampleConstants[i] = 0f;
            cmd.SetPipelineState(_downsamplePso);
            for (var levelIndex = 0; levelIndex < classicPlan.DownsampleDrawCount; levelIndex++)
            {
                var level = classicPlan.GetReductionLevel(levelIndex);
                downsampleConstants[4] = 1f / level.SourceWidth;
                downsampleConstants[5] = 1f / level.SourceHeight;
                cmd.SetGraphicsRoot32BitConstants(1, 16, downsampleConstants, 0);

                var rtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
                rtv.Ptr += (nuint)((ReductionRtvStart + levelIndex) * _avgRtvDescriptorSize);
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
            var gpuA = heapGpu; gpuA.Ptr += GroupOffset(AdaptGroup);
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

        // Recovered bloom topology: ImageSpaceEffectHDR invokes one selected ImageSpaceEffectBlur.
        // That effect records vertical BPBLUR into an intermediate, then horizontal BLUR into its
        // output. Authored BlurPasses remains retained data and does not repeat this two-draw pair.
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
            // ImageSpaceEffectBlur::UpdateParams uploads (0, 1/height) to the bright-pass shader.
            b[4] = 0f;
            b[5] = 1f / _bloomHeight;
            cmd.RSSetViewport(new Viewport(0, 0, _bloomWidth, _bloomHeight, 0f, 1f));
            cmd.RSSetScissorRect(_bloomWidth, _bloomHeight);
            cmd.SetGraphicsRoot32BitConstants(1, 16, b, 0);
            var brightPassRtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
            brightPassRtv.Ptr += (nuint)(BrightPassBlurRtvSlot * _avgRtvDescriptorSize);
            var brightPassGpu = heapGpu; brightPassGpu.Ptr += GroupOffset(BrightPassBlurGroup);
            cmd.SetGraphicsRootDescriptorTable(0, brightPassGpu);
            cmd.ResourceBarrierTransition(
                _brightPassBlurTexture!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            cmd.OMSetRenderTargets(brightPassRtv);
            cmd.SetPipelineState(_bloomPso);
            cmd.DrawInstanced(3, 1, 0, 0);
            cmd.ResourceBarrierTransition(
                _brightPassBlurTexture!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);

            // The effect's second shader receives (1/width, 0), uses the same exact weight row, and
            // does not repeat the bright-pass threshold/scale operation.
            b[4] = 1f / _bloomWidth;
            b[5] = 0f;
            cmd.SetGraphicsRoot32BitConstants(1, 16, b, 0);
            var blurRtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
            blurRtv.Ptr += (nuint)(BlurRtvSlot * _avgRtvDescriptorSize);
            var blurGpu = heapGpu; blurGpu.Ptr += GroupOffset(BlurGroup);
            cmd.SetGraphicsRootDescriptorTable(0, blurGpu);
            cmd.ResourceBarrierTransition(
                _bloomTexture!, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            cmd.OMSetRenderTargets(blurRtv);
            cmd.SetPipelineState(_blurPso);
            cmd.DrawInstanced(3, 1, 0, 0);
            cmd.ResourceBarrierTransition(
                _bloomTexture!, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);

            cmd.SetGraphicsRoot32BitConstants(1, 24, p, 0);
        }

        var gpuB = heapGpu; gpuB.Ptr += GroupOffset(CompositeGroup);
        cmd.SetGraphicsRootDescriptorTable(0, gpuB);
        cmd.OMSetRenderTargets(ldrRtv);
        cmd.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        cmd.RSSetScissorRect(width, height);
        cmd.SetPipelineState(_pso);
        cmd.DrawInstanced(3, 1, 0, 0);
    }

    /// <summary>
    ///     Lazily (re)creates every recursive /4 reduction target plus the quarter-resolution
    ///     vertical BrightPassBlur intermediate and horizontal plain-blur output. Dispose-and-recreate
    ///     on size change is safe: the live path resizes via <c>GpuSwapChainSurface12</c>'s
    ///     GPU-idle Resize, and offscreen targets are fixed-size for their lifetime.
    /// </summary>
    private void EnsureClassicTargets(in ClassicHdrPassPlan plan)
    {
        if (_reductionTextures[0] != null
            && _brightPassBlurTexture != null
            && _bloomTexture != null
            && _classicSourceWidth == plan.SourceWidth
            && _classicSourceHeight == plan.SourceHeight
            && _reductionLevelCount == plan.DownsampleDrawCount)
        {
            return;
        }

        for (var i = 0; i < _reductionTextures.Length; i++)
        {
            _reductionTextures[i]?.Dispose();
            _reductionTextures[i] = null;
        }
        _brightPassBlurTexture?.Dispose();
        _brightPassBlurTexture = null;
        _bloomTexture?.Dispose();
        _bloomTexture = null;

        _classicSourceWidth = plan.SourceWidth;
        _classicSourceHeight = plan.SourceHeight;
        _reductionLevelCount = plan.DownsampleDrawCount;
        for (var levelIndex = 0; levelIndex < plan.DownsampleDrawCount; levelIndex++)
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
            texture.Name = $"TonemapClassicDownsample_{levelIndex}_{level.TargetWidth}x{level.TargetHeight}";
            _reductionTextures[levelIndex] = texture;
            var rtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
            rtv.Ptr += (nuint)((ReductionRtvStart + levelIndex) * _avgRtvDescriptorSize);
            _gpu.Device.CreateRenderTargetView(texture, null, rtv);
        }

        var bloomLevel = plan.GetReductionLevel(0);
        _bloomWidth = bloomLevel.TargetWidth;
        _bloomHeight = bloomLevel.TargetHeight;
        _brightPassBlurTexture = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            ResourceDescription.Texture2D(
                Format.R16G16B16A16_Float,
                (uint)_bloomWidth,
                (uint)_bloomHeight,
                1, 1, 1, 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource);
        _brightPassBlurTexture.Name = $"TonemapClassicBrightPassVertical_{_bloomWidth}x{_bloomHeight}";
        var brightPassRtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
        brightPassRtv.Ptr += (nuint)(BrightPassBlurRtvSlot * _avgRtvDescriptorSize);
        _gpu.Device.CreateRenderTargetView(_brightPassBlurTexture, null, brightPassRtv);

        _bloomTexture = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            ResourceDescription.Texture2D(
                Format.R16G16B16A16_Float,
                (uint)_bloomWidth,
                (uint)_bloomHeight,
                1, 1, 1, 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource);
        _bloomTexture.Name = $"TonemapClassicBlurHorizontal_{_bloomWidth}x{_bloomHeight}";
        var blurRtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
        blurRtv.Ptr += (nuint)(BlurRtvSlot * _avgRtvDescriptorSize);
        _gpu.Device.CreateRenderTargetView(_bloomTexture, null, blurRtv);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _srvHeap.Dispose();
        _avgRtvHeap.Dispose();
        _avgTextures[0].Dispose();
        _avgTextures[1].Dispose();
        for (var i = 0; i < _reductionTextures.Length; i++)
        {
            _reductionTextures[i]?.Dispose();
        }
        _brightPassBlurTexture?.Dispose();
        _bloomTexture?.Dispose();
        _blurPso.Dispose();
        _bloomPso.Dispose();
        _downsamplePso.Dispose();
        _adaptPso.Dispose();
        _avgPso.Dispose();
        _pso.Dispose();
        _rootSignature.Dispose();
    }

    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded shader resource not found: {name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        var result = Compiler.Compile(
            source, Array.Empty<ShaderMacro>(), include: null!, entryPoint, sourceName: name, profile,
            ShaderFlags.None, EffectFlags.None, out Blob? bytecode, out Blob? errors);

        if (result.Failure || bytecode is null)
        {
            var errorText = errors?.AsString() ?? "(no error blob)";
            errors?.Dispose();
            bytecode?.Dispose();
            throw new InvalidOperationException($"HLSL compile failed for {name} ({profile}): {errorText}");
        }

        errors?.Dispose();
        try { return bytecode.AsBytes().ToArray(); }
        finally { bytecode.Dispose(); }
    }
}
