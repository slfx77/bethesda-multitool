#if WINDOWS_GUI
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.WinUI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     v3 Pass 4 — D3D12 swap chain bound to a WinUI 3 <see cref="SwapChainPanel" /> via
///     <c>ISwapChainPanelNative</c>. Mirrors the old <c>GpuSwapChainSurface</c>'s
///     interface (Width / Height / Resize / Present) but on D3D12.
///     <para>
///         Key D3D12 differences from the D3D11 surface:
///     </para>
///     <list type="bullet">
///         <item>
///             Swap chain is created from the <c>ID3D12CommandQueue</c>, not the device.
///             Presents flow through that same queue.
///         </item>
///         <item>
///             Back-buffer RTVs are pre-allocated into a small RTV descriptor heap (one per
///             swap-chain buffer). <see cref="AcquireBackBufferRtv" /> returns the
///             <c>(resource, cpuHandle)</c> for the current frame's back buffer.
///         </item>
///         <item>
///             Back-buffer resources need explicit state transitions PRESENT ↔ RENDER_TARGET
///             at frame start / end. The caller owns recording the barriers into the command
///             list; this surface exposes the current resource and tracks no state itself.
///         </item>
///         <item>
///             Depth-stencil is a single committed resource + DSV, since the back-buffer
///             cycle doesn't affect depth.
///         </item>
///     </list>
/// </summary>
internal sealed class GpuSwapChainSurface12 : IDisposable
{
    private const int BufferCount = 2;
    internal const Format SceneColorFormat = Format.B8G8R8A8_UNorm;
    private static readonly Logger Log = Logger.Instance;

    private readonly ID3D12Device _device;
    private readonly IDXGISwapChain3 _swapChain;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly uint _rtvDescriptorSize;
    private readonly int _sampleCount;
    private readonly ID3D12Resource[] _backBuffers;
    private ID3D12Resource? _depthTexture;
    // Scene-MSAA color target (null when _sampleCount == 1): the scene renders into this and is
    // ResolveSubresource'd into the current back buffer before Present. Lives at RTV-heap slot
    // BufferCount. Its matching MSAA depth IS _depthTexture when _sampleCount > 1.
    private ID3D12Resource? _msaaColor;
    private uint _width;
    private uint _height;

    private GpuSwapChainSurface12(
        ID3D12Device device,
        IDXGISwapChain3 swapChain,
        ID3D12DescriptorHeap rtvHeap,
        ID3D12DescriptorHeap dsvHeap,
        ID3D12Resource[] backBuffers,
        ID3D12Resource depthTexture,
        ID3D12Resource? msaaColor,
        int sampleCount,
        uint width,
        uint height)
    {
        _device = device;
        _swapChain = swapChain;
        _rtvHeap = rtvHeap;
        _dsvHeap = dsvHeap;
        _rtvDescriptorSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        _sampleCount = sampleCount;
        _backBuffers = backBuffers;
        _depthTexture = depthTexture;
        _msaaColor = msaaColor;
        _width = width;
        _height = height;
    }

    public uint Width => _width;
    public uint Height => _height;

    /// <summary>True when the scene renders into a multisampled color target that is resolved into
    /// the back buffer before Present (scene-wide MSAA).</summary>
    public bool IsMsaa => _sampleCount > 1;

    /// <summary>RTV for the scene's MSAA color target (valid only when <see cref="IsMsaa" />). Lives
    /// at RTV-heap slot <see cref="BufferCount" /> (after the per-frame back-buffer RTVs).</summary>
    public CpuDescriptorHandle MsaaColorRtv =>
        new(_rtvHeap.GetCPUDescriptorHandleForHeapStart(), BufferCount, _rtvDescriptorSize);

    /// <summary>The DSV for the current depth buffer. Bound alongside the back-buffer RTV
    /// every frame; depth resource is single-buffered so the handle is stable across frames.</summary>
    public CpuDescriptorHandle DepthStencilView => _dsvHeap.GetCPUDescriptorHandleForHeapStart();

    /// <summary>The depth resource (R32_TYPELESS, AllowDepthStencil). Exposed so the caller can
    /// create an R32_FLOAT SRV over it (the water shader samples scene depth for its depth-fade)
    /// and transition it DEPTH_WRITE ↔ PIXEL_SHADER_RESOURCE around that pass. Changes identity on
    /// <see cref="Resize" />, so any SRV over it must be recreated afterward.</summary>
    public ID3D12Resource? DepthResource => _depthTexture;

    /// <summary>
    ///     Returns the current back buffer's resource handle + RTV descriptor for the frame.
    ///     The caller is responsible for transitioning the resource PRESENT → RENDER_TARGET
    ///     before clearing and RENDER_TARGET → PRESENT before <see cref="Present" />.
    /// </summary>
    public (ID3D12Resource Resource, CpuDescriptorHandle RtvHandle) AcquireBackBufferRtv()
    {
        var index = _swapChain.CurrentBackBufferIndex;
        var heapStart = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        var handle = new CpuDescriptorHandle(heapStart, (int)index, _rtvDescriptorSize);
        return (_backBuffers[index], handle);
    }

    public void Dispose()
    {
        foreach (var b in _backBuffers) b.Dispose();
        _depthTexture?.Dispose();
        _depthTexture = null;
        _msaaColor?.Dispose();
        _msaaColor = null;
        _dsvHeap.Dispose();
        _rtvHeap.Dispose();
        _swapChain.Dispose();
    }

    /// <summary>
    ///     Creates a composition swap chain on <paramref name="gpu" />'s direct queue and
    ///     binds it to <paramref name="panel" />. Must be called on the UI thread (the
    ///     <c>ISwapChainPanelNative.SetSwapChain</c> call requires it).
    /// </summary>
    public static GpuSwapChainSurface12? Create(GpuDevice12 gpu, SwapChainPanel panel, uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            Log.Warn("GpuSwapChainSurface12: refusing to create with zero dimensions ({0}x{1})", width, height);
            return null;
        }

        var sampleCount = gpu.SceneSampleCount;

        ID3D12DescriptorHeap? rtvHeap = null;
        ID3D12DescriptorHeap? dsvHeap = null;
        IDXGISwapChain3? swapChain3 = null;
        ID3D12Resource[]? backBuffers = null;
        ID3D12Resource? depthTexture = null;
        ID3D12Resource? msaaColor = null;

        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();

            // The swap chain itself is ALWAYS single-sample (flip-model swap chains cannot be
            // multisampled); scene MSAA renders into a separate _msaaColor target that is resolved
            // into the back buffer before Present.
            var desc = new SwapChainDescription1
            {
                Width = width,
                Height = height,
                Format = SceneColorFormat,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = BufferCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None
            };

            // D3D12 swap chain is created from the queue, not the device. Composition swap
            // chain (no HWND) → bound to SwapChainPanel via ISwapChainPanelNative below.
            using var swapChain1 = factory.CreateSwapChainForComposition(gpu.DirectQueue, desc);
            swapChain3 = swapChain1.QueryInterface<IDXGISwapChain3>();

            using (var panelComObject = new ComObject(panel))
            {
                using var native = panelComObject.QueryInterface<Vortice.WinUI.ISwapChainPanelNative>();
                native.SetSwapChain(swapChain3).CheckError();
            }

            // +1 RTV slot after the per-frame back-buffer RTVs for the scene MSAA color target.
            rtvHeap = gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
            {
                Type = DescriptorHeapType.RenderTargetView,
                DescriptorCount = BufferCount + 1,
                Flags = DescriptorHeapFlags.None
            });

            dsvHeap = gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
            {
                Type = DescriptorHeapType.DepthStencilView,
                DescriptorCount = 1,
                Flags = DescriptorHeapFlags.None
            });

            var rtvDescriptorSize = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            backBuffers = AcquireBackBuffers(gpu.Device, swapChain3, rtvHeap);
            depthTexture = CreateDepthBuffer(gpu.Device, width, height, dsvHeap, sampleCount);
            msaaColor = CreateMsaaColor(gpu.Device, width, height, sampleCount, rtvHeap, rtvDescriptorSize);

            Log.Info("GpuSwapChainSurface12: bound {0}x{1} to SwapChainPanel ({2} buffers, MSAA {3}x)",
                width, height, BufferCount, sampleCount);
            return new GpuSwapChainSurface12(
                gpu.Device, swapChain3, rtvHeap, dsvHeap, backBuffers, depthTexture, msaaColor, sampleCount, width, height);
        }
        catch (SharpGenException ex)
        {
            Log.Warn("GpuSwapChainSurface12.Create failed: {0}", ex.Message);
            msaaColor?.Dispose();
            depthTexture?.Dispose();
            if (backBuffers is not null)
            {
                foreach (var b in backBuffers) b.Dispose();
            }
            dsvHeap?.Dispose();
            rtvHeap?.Dispose();
            swapChain3?.Dispose();
            return null;
        }
    }

    /// <summary>
    ///     Resizes the swap-chain buffers. Like the D3D11 surface, dimensions are physical
    ///     pixels — multiply layout pixels by <c>CompositionScale[X|Y]</c>.
    ///     <para>
    ///         IMPORTANT: caller must ensure the GPU is idle before calling (no in-flight
    ///         command lists referencing the back buffers). Otherwise <c>ResizeBuffers</c>
    ///         fails. The frame command recorder owns CPU↔GPU sync; resize is a UI-thread
    ///         action that should drain the frame queue first.
    ///     </para>
    /// </summary>
    public void Resize(uint width, uint height)
    {
        if (width == 0 || height == 0 || (_width == width && _height == height))
            return;

        foreach (var b in _backBuffers) b.Dispose();
        Array.Clear(_backBuffers);
        _depthTexture?.Dispose();
        _depthTexture = null;
        _msaaColor?.Dispose();
        _msaaColor = null;

        _swapChain.ResizeBuffers(BufferCount, width, height, Format.Unknown, SwapChainFlags.None).CheckError();

        var newBuffers = AcquireBackBuffers(_device, _swapChain, _rtvHeap);
        for (int i = 0; i < newBuffers.Length; i++) _backBuffers[i] = newBuffers[i];
        _depthTexture = CreateDepthBuffer(_device, width, height, _dsvHeap, _sampleCount);
        _msaaColor = CreateMsaaColor(_device, width, height, _sampleCount, _rtvHeap, _rtvDescriptorSize);

        _width = width;
        _height = height;
    }

    /// <summary>
    ///     Resolves the multisampled scene color into the current back buffer (no-op when not
    ///     <see cref="IsMsaa" />). Call after the scene draws and before <see cref="Present" />. The
    ///     MSAA color is returned to RENDER_TARGET and the back buffer to PRESENT, so the caller does
    ///     NOT additionally transition the back buffer to PRESENT on the MSAA path.
    /// </summary>
    public void ResolveTo(ID3D12GraphicsCommandList cmd, ID3D12Resource backBuffer)
    {
        if (_msaaColor is null) return;
        cmd.ResourceBarrierTransition(_msaaColor, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
        cmd.ResourceBarrierTransition(backBuffer, ResourceStates.Present, ResourceStates.ResolveDest);
        cmd.ResolveSubresource(backBuffer, 0, _msaaColor, 0, SceneColorFormat);
        cmd.ResourceBarrierTransition(backBuffer, ResourceStates.ResolveDest, ResourceStates.Present);
        cmd.ResourceBarrierTransition(_msaaColor, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
    }

    /// <summary>Presents the current back buffer with vsync. The back-buffer index advances
    /// automatically; the next <see cref="AcquireBackBufferRtv" /> picks up the new index.</summary>
    public void Present()
    {
        _swapChain.Present(1, PresentFlags.None).CheckError();
    }

    private static ID3D12Resource[] AcquireBackBuffers(
        ID3D12Device device,
        IDXGISwapChain3 swapChain,
        ID3D12DescriptorHeap rtvHeap)
    {
        var buffers = new ID3D12Resource[BufferCount];
        var rtvIncrement = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        var heapStart = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (uint i = 0; i < BufferCount; i++)
        {
            buffers[i] = swapChain.GetBuffer<ID3D12Resource>(i);
            var handle = new CpuDescriptorHandle(heapStart, (int)i, rtvIncrement);
            device.CreateRenderTargetView(buffers[i], null, handle);
        }
        return buffers;
    }

    private static ID3D12Resource CreateDepthBuffer(
        ID3D12Device device,
        uint width,
        uint height,
        ID3D12DescriptorHeap dsvHeap,
        int sampleCount)
    {
        // Non-MSAA: R32_TYPELESS so the water pass can read scene depth as an R32_FLOAT SRV (and
        // 4d's Hi-Z pyramid can CopyResource it). The DSV supplies the typed D32_Float view, which
        // every depth-test PSO (DepthStencilFormat = D32_Float) validates against.
        // MSAA: a multisampled depth can't be sampled as a plain Texture2D SRV, and the water
        // depth-fade is gated off under MSAA, so create a typed D32_Float multisampled depth and let
        // the DSV infer the Texture2DMS view (null desc).
        var msaa = sampleCount > 1;
        var resourceDesc = ResourceDescription.Texture2D(
            msaa ? Format.D32_Float : Format.R32_Typeless,
            width,
            height,
            arraySize: 1,
            mipLevels: 1,
            sampleCount: (uint)sampleCount,
            sampleQuality: 0,
            ResourceFlags.AllowDepthStencil);

        // ClearValue MUST use the typed format the DSV will see (D32_Float) so the runtime
        // can validate ClearDepthStencilView calls. A typeless ClearValue is rejected.
        var clearValue = new ClearValue(Format.D32_Float, new DepthStencilValue(0.0f, 0)); // reversed-Z: far = 0
        var depth = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties,
            HeapFlags.None,
            resourceDesc,
            ResourceStates.DepthWrite,
            clearValue);

        if (msaa)
        {
            // Typed D32_Float multisampled resource → null desc infers the Texture2DMS DSV.
            device.CreateDepthStencilView(depth, null, dsvHeap.GetCPUDescriptorHandleForHeapStart());
        }
        else
        {
            var dsvDesc = new DepthStencilViewDescription
            {
                Format = Format.D32_Float,
                ViewDimension = DepthStencilViewDimension.Texture2D,
                Flags = DepthStencilViewFlags.None
            };
            device.CreateDepthStencilView(depth, dsvDesc, dsvHeap.GetCPUDescriptorHandleForHeapStart());
        }
        return depth;
    }

    /// <summary>
    ///     Creates the multisampled scene color target (null when <paramref name="sampleCount" /> ==
    ///     1) and its RTV at heap slot <see cref="BufferCount" />. Created in RENDER_TARGET state and
    ///     kept there across frames (<see cref="ResolveTo" /> round-trips it through ResolveSource).
    /// </summary>
    private static ID3D12Resource? CreateMsaaColor(
        ID3D12Device device,
        uint width,
        uint height,
        int sampleCount,
        ID3D12DescriptorHeap rtvHeap,
        uint rtvDescriptorSize)
    {
        if (sampleCount <= 1) return null;

        var color = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties,
            HeapFlags.None,
            ResourceDescription.Texture2D(SceneColorFormat, width, height,
                arraySize: 1, mipLevels: 1, sampleCount: (uint)sampleCount, sampleQuality: 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget,
            // No optimized clear value: the scene re-clears the MSAA color every frame, and a fixed
            // optimized value that doesn't match the clear color only triggers a debug-layer warning.
            optimizedClearValue: null);

        var handle = new CpuDescriptorHandle(
            rtvHeap.GetCPUDescriptorHandleForHeapStart(), BufferCount, rtvDescriptorSize);
        device.CreateRenderTargetView(color, null, handle);
        return color;
    }
}
#endif
