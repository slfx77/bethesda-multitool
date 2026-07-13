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
///         Owns its own root signature (SRV table t0 + 4 root constants b0 + a linear-clamp static
///         sampler), PSO, and a small shader-visible SRV ring heap for the per-call HDR-texture view.
///         Both scene targets (<see cref="GpuOffscreenSceneTarget12" /> headless +
///         <see cref="GpuSwapChainSurface12" /> live) drive it once per frame; the ring survives the
///         in-flight frames. The caller owns the resource-state transitions (HDR source →
///         <see cref="ResourceStates.PixelShaderResource" />, LDR dest →
///         <see cref="ResourceStates.RenderTarget" />) around <see cref="Record" />.
///     </para>
/// </summary>
internal sealed class GpuTonemapPass12 : IDisposable
{
    // SRV ring depth: one 2-descriptor group (t0 = HDR scene, t1 = 1×1 avg color) per call, cycled so
    // a view is never overwritten while the previous frame's tonemap draw still reads it.
    // framesInFlight (2) × a couple of scene targets is comfortably under 8 groups.
    private const int SrvRingSlots = 8;
    private const int SrvsPerCall = 2;

    private readonly GpuDevice12 _gpu;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12PipelineState _pso;
    private readonly ID3D12PipelineState _avgPso;
    private readonly ID3D12DescriptorHeap _srvHeap;
    private readonly ID3D12DescriptorHeap _avgRtvHeap;
    private readonly ID3D12Resource _avgTexture;
    private readonly uint _srvDescriptorSize;
    private int _srvCursor;
    private bool _disposed;

    public GpuTonemapPass12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        var device = gpu.Device;

        // Root: [0] SRV table (t0 = HDR scene, t1 = 1×1 adapted average color); [1] 16×32-bit root
        // constants (b0, four float4s of tonemap/cinematic params); one linear-clamp static sampler (s0).
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

        _avgTexture = device.CreateCommittedResource<ID3D12Resource>(
            new HeapProperties(HeapType.Default),
            HeapFlags.None,
            ResourceDescription.Texture2D(Format.R16G16B16A16_Float, 1, 1, 1, 1, 1, 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.PixelShaderResource);
        _avgTexture.Name = "TonemapAvgColor1x1";

        _avgRtvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.None,
        });
        device.CreateRenderTargetView(_avgTexture, null, _avgRtvHeap.GetCPUDescriptorHandleForHeapStart());

        _srvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            DescriptorCount = SrvRingSlots * SrvsPerCall,
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

        // Cycle a 2-descriptor group: t0 = this call's HDR texture, t1 = the 1×1 adapted average.
        var slot = _srvCursor;
        _srvCursor = (_srvCursor + 1) % SrvRingSlots;
        var cpu = _srvHeap.GetCPUDescriptorHandleForHeapStart();
        cpu.Ptr += (nuint)(slot * SrvsPerCall * _srvDescriptorSize);
        var gpu = _srvHeap.GetGPUDescriptorHandleForHeapStart();
        gpu.Ptr += (ulong)(slot * SrvsPerCall * _srvDescriptorSize);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = hdrFormat,
            ViewDimension = D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        };
        _gpu.Device.CreateShaderResourceView(hdrTexture, srvDesc, cpu);

        var avgCpu = cpu;
        avgCpu.Ptr += _srvDescriptorSize;
        var avgSrvDesc = srvDesc with { Format = Format.R16G16B16A16_Float };
        _gpu.Device.CreateShaderResourceView(_avgTexture, avgSrvDesc, avgCpu);

        var p = stackalloc float[16]
        {
            settings.Exposure, enabled ? 1f : 0f, (float)settings.Mode, settings.TargetLum,
            settings.Saturation, settings.ContrastAvgLum, settings.Contrast, settings.Brightness,
            settings.TintR, settings.TintG, settings.TintB, settings.TintAmount,
            settings.UpperLumClamp, 0f, 0f, 0f,
        };

        cmd.SetGraphicsRootSignature(_rootSignature);
        cmd.SetDescriptorHeaps(1, new[] { _srvHeap });
        cmd.SetGraphicsRootDescriptorTable(0, gpu);
        cmd.SetGraphicsRoot32BitConstants(1, 16, p, 0);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Engine mode: render the 1×1 adapted average first (t1 sits in the table during this draw but
        // mainAvg never reads it, so the RENDER_TARGET state is legal).
        if (enabled && settings.Mode == GpuTonemapMode.EngineFo3Fnv)
        {
            cmd.ResourceBarrierTransition(
                _avgTexture, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            cmd.OMSetRenderTargets(_avgRtvHeap.GetCPUDescriptorHandleForHeapStart());
            cmd.RSSetViewport(new Viewport(0, 0, 1, 1, 0f, 1f));
            cmd.RSSetScissorRect(1, 1);
            cmd.SetPipelineState(_avgPso);
            cmd.DrawInstanced(3, 1, 0, 0);
            cmd.ResourceBarrierTransition(
                _avgTexture, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
        }

        cmd.OMSetRenderTargets(ldrRtv);
        cmd.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        cmd.RSSetScissorRect(width, height);
        cmd.SetPipelineState(_pso);
        cmd.DrawInstanced(3, 1, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _srvHeap.Dispose();
        _avgRtvHeap.Dispose();
        _avgTexture.Dispose();
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
