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
///         In engine mode the pass also records the BrightPassBlur bloom chain
///         (<c>bloom.frag.hlsl</c>): fused bright-pass + 1D blur passes ping-ponging a ¼×¼-resolution
///         float pair, composited as <c>bloom·(0.5/denom)</c> per the ISHDRBLENDINSHADER decompile.
///     </para>
///     <para>
///         Owns its own root signature (SRV table t0–t2 + 16 root constants b0 + a linear-clamp static
///         sampler), PSOs, and a small shader-visible SRV ring heap for the per-call texture views.
///         Both scene targets (<see cref="GpuOffscreenSceneTarget12" /> headless +
///         <see cref="GpuSwapChainSurface12" /> live) drive it once per frame; the ring survives the
///         in-flight frames. The caller owns the resource-state transitions (HDR source →
///         <see cref="ResourceStates.PixelShaderResource" />, LDR dest →
///         <see cref="ResourceStates.RenderTarget" />) around <see cref="Record" />.
///     </para>
/// </summary>
internal sealed class GpuTonemapPass12 : IDisposable
{
    // SRV ring depth: one call = a fixed layout of 3-descriptor groups (t0/t1/t2) — group 0 = the avg
    // pass, groups 1..MaxBloomPasses = the bloom chain, the last group = the main composite — cycled
    // so a view is never overwritten while the previous frame's tonemap draw still reads it.
    // framesInFlight (2) × a couple of scene targets is comfortably under 8 ring slots.
    private const int SrvRingSlots = 8;
    private const int SrvsPerCall = 3;
    private const int MaxBloomPasses = 8;
    private const int GroupsPerCall = 2 + MaxBloomPasses;

    private readonly GpuDevice12 _gpu;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pso;
    private readonly ID3D12PipelineState _avgPso;
    private readonly ID3D12PipelineState _bloomPso;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly ID3D12DescriptorHeap _avgRtvHeap;
    // Ping-pong pair of 1×1 adapted-average targets: each engine-mode frame reads the PREVIOUS
    // frame's adapted average (temporal eye adaptation) while writing the new one; the main pass
    // then samples the freshly written side. Both start in PixelShaderResource.
    private readonly ID3D12Resource[] _avgTextures = new ID3D12Resource[2];
    // Ping-pong pair of ¼×¼-resolution bloom targets, created lazily on first engine-mode frame with
    // bloom enabled (RTVs live in _avgRtvHeap slots 2–3). Both held in PixelShaderResource between
    // passes.
    private readonly ID3D12Resource?[] _bloomTextures = new ID3D12Resource?[2];
    private readonly uint _avgRtvDescriptorSize;
    private readonly uint _srvDescriptorSize;
    private int _bloomWidth;
    private int _bloomHeight;
    private int _avgWriteIndex;
    private bool _adaptPrimed;
    private int _srvCursor;
    private bool _disposed;

    public GpuTonemapPass12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;

        // Root: [0] SRV table (t0 = HDR scene, t1 = 1×1 adapted average color, t2 = bloom); [1]
        // 16×32-bit root constants (b0, four float4s — tonemap/cinematic params for the avg + main
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
            new RootConstants(shaderRegister: 0, registerSpace: 0, num32BitValues: 16),
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

        // Average-scene-color subpass (engine mode): same fullscreen VS, mainAvg PS, into a private
        // 1×1 float target the main pass then samples as t1 (the steady-state adapted eye).
        var avgPs = CompileEmbeddedShader("tonemap.frag.hlsl", "mainAvg", "ps_5_1");
        var avgPsoDesc = psoDesc;
        avgPsoDesc.PixelShader = avgPs;
        avgPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
        _avgPso = device.CreateGraphicsPipelineState(avgPsoDesc);

        // BrightPassBlur pass (engine bloom): fused bright-pass + 1D blur into the ¼-res ping-pong
        // pair; same fullscreen VS + root signature (b0 is repacked per bloom draw).
        var bloomPs = CompileEmbeddedShader("bloom.frag.hlsl", "main", "ps_5_1");
        var bloomPsoDesc = psoDesc;
        bloomPsoDesc.PixelShader = bloomPs;
        bloomPsoDesc.RenderTargetFormats = new[] { Format.R16G16B16A16_Float };
        _bloomPso = device.CreateGraphicsPipelineState(bloomPsoDesc);

        // RTV heap: slots 0–1 = the 1×1 adapted-average pair, slots 2–3 = the lazily created bloom pair.
        _avgRtvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = 4,
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

        // Cycle a fixed group layout per call: group 0 = avg pass (t0 = HDR, t1 = PREVIOUS adapted
        // average — the temporal-blend history), groups 1..passes = the bloom chain, last group =
        // the main pass (t0 = HDR, t1 = the freshly written adapted average, t2 = final bloom).
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
        var passes = Math.Clamp((int)MathF.Round(settings.BlurPasses), 1, MaxBloomPasses);
        var bloomActive = engineMode && settings.BloomEnabled && settings.BrightScale > 0f;
        if (bloomActive)
        {
            EnsureBloomTargets(width, height);
        }

        // Group 0 (avg pass). t2 is filled with the always-PixelShaderResource avg texture — the
        // shader never samples it, but the volatile range still wants live views.
        var cpuA = heapCpu; cpuA.Ptr += GroupOffset(0);
        _gpu.Device.CreateShaderResourceView(hdrTexture, srvDesc, cpuA);
        cpuA.Ptr += _srvDescriptorSize;
        _gpu.Device.CreateShaderResourceView(_avgTextures[readIdx], avgSrvDesc, cpuA);
        cpuA.Ptr += _srvDescriptorSize;
        _gpu.Device.CreateShaderResourceView(_avgTextures[readIdx], avgSrvDesc, cpuA);

        // Bloom groups: t0 = the pass source (full-res scene for pass 0, then the ping-pong pair).
        if (bloomActive)
        {
            for (var pass = 0; pass < passes; pass++)
            {
                var cpuP = heapCpu; cpuP.Ptr += GroupOffset(1 + pass);
                var source = pass == 0 ? hdrTexture : _bloomTextures[(pass - 1) & 1]!;
                _gpu.Device.CreateShaderResourceView(source, pass == 0 ? srvDesc : avgSrvDesc, cpuP);
                cpuP.Ptr += _srvDescriptorSize;
                _gpu.Device.CreateShaderResourceView(_avgTextures[readIdx], avgSrvDesc, cpuP);
                cpuP.Ptr += _srvDescriptorSize;
                _gpu.Device.CreateShaderResourceView(_avgTextures[readIdx], avgSrvDesc, cpuP);
            }
        }

        // Main group: t2 = the final bloom side, or the avg texture as a benign always-valid filler
        // when bloom is off (uParams3.z gates the term to zero).
        var cpuB = heapCpu; cpuB.Ptr += GroupOffset(GroupsPerCall - 1);
        _gpu.Device.CreateShaderResourceView(hdrTexture, srvDesc, cpuB);
        cpuB.Ptr += _srvDescriptorSize;
        _gpu.Device.CreateShaderResourceView(_avgTextures[writeIdx], avgSrvDesc, cpuB);
        cpuB.Ptr += _srvDescriptorSize;
        var finalBloom = bloomActive ? _bloomTextures[(passes - 1) & 1]! : _avgTextures[writeIdx];
        _gpu.Device.CreateShaderResourceView(finalBloom, avgSrvDesc, cpuB);

        // First engine-mode frame has no valid history — force instant adaptation regardless of the
        // caller's factor (the engine's ClearAdaptedLight equivalent).
        var adaptFactor = _adaptPrimed ? settings.AdaptFactor : 0f;

        var p = stackalloc float[16]
        {
            settings.Exposure, enabled ? 1f : 0f, (float)settings.Mode, settings.TargetLum,
            settings.Saturation, settings.ContrastAvgLum, settings.Contrast, settings.Brightness,
            settings.TintR, settings.TintG, settings.TintB, settings.TintAmount,
            settings.UpperLumClamp, adaptFactor, bloomActive ? 1f : 0f, 0f,
        };

        cmd.SetGraphicsRootSignature(_rootSignature);
        cmd.SetDescriptorHeaps(1, new[] { _srvHeap });
        cmd.SetGraphicsRoot32BitConstants(1, 16, p, 0);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Engine mode: render the 1×1 adapted average first (reads the previous frame's average as
        // t1 while writing the other ping-pong side).
        if (engineMode)
        {
            _adaptPrimed = true;
            var writeRtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
            writeRtv.Ptr += (nuint)(writeIdx * _avgRtvDescriptorSize);
            var gpuA = heapGpu; gpuA.Ptr += GroupOffset(0);
            cmd.SetGraphicsRootDescriptorTable(0, gpuA);
            cmd.ResourceBarrierTransition(
                _avgTextures[writeIdx], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            cmd.OMSetRenderTargets(writeRtv);
            cmd.RSSetViewport(new Viewport(0, 0, 1, 1, 0f, 1f));
            cmd.RSSetScissorRect(1, 1);
            cmd.SetPipelineState(_avgPso);
            cmd.DrawInstanced(3, 1, 0, 0);
            cmd.ResourceBarrierTransition(
                _avgTextures[writeIdx], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
        }

        // BrightPassBlur chain: one fused 1D bright-pass+blur per IMGS BlurPasses, alternating H/V
        // (shipped passes = 2 → an H+V separable pair), ping-ponging the ¼-res targets. b0 is
        // repacked per bloom draw and the tonemap constants restored before the main composite.
        if (bloomActive)
        {
            var taps = Math.Clamp((int)MathF.Ceiling(settings.BlurRadius), 1, 7);
            var b = stackalloc float[16];
            b[0] = settings.BrightClamp;
            b[1] = settings.BrightScale;
            b[2] = taps;
            b[4] = 1f / _bloomWidth;
            b[5] = 1f / _bloomHeight;
            cmd.SetPipelineState(_bloomPso);
            cmd.RSSetViewport(new Viewport(0, 0, _bloomWidth, _bloomHeight, 0f, 1f));
            cmd.RSSetScissorRect(_bloomWidth, _bloomHeight);
            for (var pass = 0; pass < passes; pass++)
            {
                b[6] = pass % 2 == 0 ? 1f : 0f;
                b[7] = pass % 2 == 0 ? 0f : 1f;
                cmd.SetGraphicsRoot32BitConstants(1, 16, b, 0);

                var target = _bloomTextures[pass & 1]!;
                var rtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
                rtv.Ptr += (nuint)((2 + (pass & 1)) * _avgRtvDescriptorSize);
                var gpuP = heapGpu; gpuP.Ptr += GroupOffset(1 + pass);
                cmd.SetGraphicsRootDescriptorTable(0, gpuP);
                cmd.ResourceBarrierTransition(
                    target, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
                cmd.OMSetRenderTargets(rtv);
                cmd.DrawInstanced(3, 1, 0, 0);
                cmd.ResourceBarrierTransition(
                    target, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
            }

            cmd.SetGraphicsRoot32BitConstants(1, 16, p, 0);
        }

        var gpuB = heapGpu; gpuB.Ptr += GroupOffset(GroupsPerCall - 1);
        cmd.SetGraphicsRootDescriptorTable(0, gpuB);
        cmd.OMSetRenderTargets(ldrRtv);
        cmd.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        cmd.RSSetScissorRect(width, height);
        cmd.SetPipelineState(_pso);
        cmd.DrawInstanced(3, 1, 0, 0);
    }

    /// <summary>
    ///     Lazily (re)creates the ¼×¼-resolution bloom ping-pong pair for the current output size
    ///     (one engine DownSample16 step; the bright pass bilinear-samples the full-res scene into
    ///     it). Dispose-and-recreate on size change is safe: the live path resizes only via
    ///     <see cref="GpuSwapChainSurface12" />'s GPU-idle Resize, and offscreen targets are
    ///     fixed-size for their lifetime.
    /// </summary>
    private void EnsureBloomTargets(int width, int height)
    {
        var w = Math.Max(1, width / 4);
        var h = Math.Max(1, height / 4);
        if (_bloomTextures[0] != null && _bloomWidth == w && _bloomHeight == h)
        {
            return;
        }

        _bloomTextures[0]?.Dispose();
        _bloomTextures[1]?.Dispose();
        _bloomWidth = w;
        _bloomHeight = h;
        for (var i = 0; i < 2; i++)
        {
            _bloomTextures[i] = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                new HeapProperties(HeapType.Default),
                HeapFlags.None,
                ResourceDescription.Texture2D(Format.R16G16B16A16_Float, (uint)w, (uint)h, 1, 1, 1, 0,
                    ResourceFlags.AllowRenderTarget),
                ResourceStates.PixelShaderResource);
            _bloomTextures[i]!.Name = $"TonemapBloomQuarterRes_{i}";
            var rtv = _avgRtvHeap.GetCPUDescriptorHandleForHeapStart();
            rtv.Ptr += (nuint)((2 + i) * _avgRtvDescriptorSize);
            _gpu.Device.CreateRenderTargetView(_bloomTextures[i], null, rtv);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _srvHeap.Dispose();
        _avgRtvHeap.Dispose();
        _avgTextures[0].Dispose();
        _avgTextures[1].Dispose();
        _bloomTextures[0]?.Dispose();
        _bloomTextures[1]?.Dispose();
        _bloomPso.Dispose();
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
