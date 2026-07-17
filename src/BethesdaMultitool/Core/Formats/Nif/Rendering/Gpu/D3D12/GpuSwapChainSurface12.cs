#if WINDOWS_GUI
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.WinUI;
using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

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
    // HDR scene color the renderers draw into (shared with the offscreen target's PSOs). The swap
    // chain / back buffer itself stays an 8-bit display format; the tonemap pass maps the HDR scene
    // into it before Present.
    internal static readonly Format SceneColorFormat = GpuSceneFormats.SceneColor;
    internal const Format BackBufferFormat = GpuSceneFormats.LdrOutput;
    private static readonly Logger Log = Logger.Instance;

    private readonly ID3D12Device _device;
    private readonly IDXGISwapChain3 _swapChain;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly uint _rtvDescriptorSize;
    private readonly uint _dsvDescriptorSize;
    private readonly int _sampleCount;
    private readonly ID3D12Resource[] _backBuffers;
    private ID3D12Resource? _depthTexture;
    // HDR scene color target (float): the scene ALWAYS renders into this (MSAA or 1-sample) and is
    // tonemapped into the current back buffer before Present. Lives at RTV-heap slot BufferCount. Its
    // matching depth IS _depthTexture (MSAA when _sampleCount > 1).
    private ID3D12Resource? _msaaColor;
    // 1-sample HDR resolve dest (MSAA only): the scene MSAA color resolves into this, which the
    // tonemap then samples. Null for the 1-sample path (the scene color is sampled directly).
    private ID3D12Resource? _hdrResolve;
    // 1-sample opaque-scene snapshot for WATER001 refraction when scene MSAA is disabled. The 4x
    // path reuses _hdrResolve instead, because it already has the exact required size/format/sample
    // layout and is idle in ResolveDest until the ordinary final resolve.
    private ID3D12Resource? _waterOpaqueCopy;
    private bool _waterOpaqueSnapshotPrepared;
    private readonly GpuTonemapPass12 _tonemap;
    private readonly bool _tonemapEnabled;
    private uint _width;
    private uint _height;

    /// <summary>
    ///     Tonemap operator + parameters for the live viewer. Defaults to gamma-corrected ACES; the
    ///     frame path overrides per game + active imagespace. Setter values should already have
    ///     <see cref="GpuTonemapSettings.ApplyOverrides" /> applied.
    /// </summary>
    public GpuTonemapSettings TonemapSettings { get; set; }

    private GpuSwapChainSurface12(
        GpuDevice12 gpu,
        IDXGISwapChain3 swapChain,
        ID3D12DescriptorHeap rtvHeap,
        ID3D12DescriptorHeap dsvHeap,
        ID3D12Resource[] backBuffers,
        ID3D12Resource depthTexture,
        ID3D12Resource? msaaColor,
        ID3D12Resource? hdrResolve,
        int sampleCount,
        uint width,
        uint height)
    {
        _device = gpu.Device;
        _swapChain = swapChain;
        _rtvHeap = rtvHeap;
        _dsvHeap = dsvHeap;
        _rtvDescriptorSize = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        _dsvDescriptorSize = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        _sampleCount = sampleCount;
        _backBuffers = backBuffers;
        _depthTexture = depthTexture;
        _msaaColor = msaaColor;
        _hdrResolve = hdrResolve;
        _tonemap = new GpuTonemapPass12(gpu);
        TonemapSettings = GpuTonemapSettings.ApplyOverrides(GpuTonemapSettings.GammaAcesDefaults);
        _tonemapEnabled = SceneColorFormat == Format.R16G16B16A16_Float
                          && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0";
        _width = width;
        _height = height;
    }

    public uint Width => _width;
    public uint Height => _height;

    /// <summary>True when the scene renders into a multisampled color target that is resolved into
    /// the back buffer before Present (scene-wide MSAA).</summary>
    public bool IsMsaa => _sampleCount > 1;

    /// <summary>Number of samples in the scene color and depth targets.</summary>
    public int SampleCount => _sampleCount;

    /// <summary>
    ///     Stable 1-sample HDR resource used to sample the opaque scene during bounded WATER001
    ///     refraction. At MSAA 4x this is the existing final-resolve destination; at 1x it is a
    ///     dedicated copy. The resource changes identity on <see cref="Resize" />, so persistent
    ///     SRV owners must recreate their descriptor after a resize. The 1x copy is allocated lazily
    ///     by <see cref="TryEnsureWaterOpaqueSnapshotResource" /> only for an eligible WATER001 frame.
    /// </summary>
    public ID3D12Resource? WaterOpaqueSnapshotResource =>
        _hdrResolve ?? _waterOpaqueCopy;

    /// <summary>
    ///     Ensures a one-sample snapshot exists without charging every 1x surface for another
    ///     full-resolution HDR texture. MSAA already owns the compatible resolve target. Allocation
    ///     failure is a local WATER001 fallback, not a frame or device failure.
    /// </summary>
    public bool TryEnsureWaterOpaqueSnapshotResource()
    {
        if (_hdrResolve is not null || _waterOpaqueCopy is not null) return true;
        if (_msaaColor is null || _sampleCount > 1 || _width == 0 || _height == 0) return false;

        try
        {
            _waterOpaqueCopy = CreateWaterOpaqueCopy(_device, _width, _height, _sampleCount);
            return _waterOpaqueCopy is not null;
        }
        catch (Exception ex)
        {
            _waterOpaqueCopy?.Dispose();
            _waterOpaqueCopy = null;
            Log.Warn("GpuSwapChainSurface12: WATER001 opaque snapshot allocation failed: {0}", ex.Message);
            return false;
        }
    }

    /// <summary>True only between a successful <see cref="TryPrepareWaterOpaqueSnapshot" /> and
    /// <see cref="RestoreWaterOpaqueSnapshot" /> call, while the exposed snapshot is in
    /// PIXEL_SHADER_RESOURCE state.</summary>
    public bool IsWaterOpaqueSnapshotPrepared => _waterOpaqueSnapshotPrepared;

    /// <summary>
    ///     Forgets preparation recorded into a command list that the host is abandoning without
    ///     submission. No barrier is emitted: the discarded list never changed the GPU resource from
    ///     its ResolveDest/CopyDest baseline.
    /// </summary>
    public void DiscardWaterOpaqueSnapshotPreparation() =>
        _waterOpaqueSnapshotPrepared = false;

    /// <summary>
    ///     Releases the lazily allocated 1x copy when host-side descriptor creation fails before any
    ///     command list can reference it. The MSAA resolve target is surface-owned and is never released
    ///     by this fallback path.
    /// </summary>
    public bool ReleaseDedicatedWaterOpaqueSnapshotResource()
    {
        if (_waterOpaqueSnapshotPrepared || _waterOpaqueCopy is null) return false;

        _waterOpaqueCopy.Dispose();
        _waterOpaqueCopy = null;
        return true;
    }

    /// <summary>RTV for the scene's MSAA color target (valid only when <see cref="IsMsaa" />). Lives
    /// at RTV-heap slot <see cref="BufferCount" /> (after the per-frame back-buffer RTVs).</summary>
    public CpuDescriptorHandle MsaaColorRtv =>
        new(_rtvHeap.GetCPUDescriptorHandleForHeapStart(), BufferCount, _rtvDescriptorSize);

    /// <summary>The DSV for the current depth buffer. Bound alongside the back-buffer RTV
    /// every frame; depth resource is single-buffered so the handle is stable across frames.</summary>
    public CpuDescriptorHandle DepthStencilView => _dsvHeap.GetCPUDescriptorHandleForHeapStart();

    /// <summary>Read-only DSV for simultaneous hardware depth testing and depth-SRV sampling.</summary>
    public CpuDescriptorHandle ReadOnlyDepthStencilView =>
        new(_dsvHeap.GetCPUDescriptorHandleForHeapStart(), 1, _dsvDescriptorSize);

    /// <summary>The depth resource (R32_TYPELESS, AllowDepthStencil). Exposed so the caller can
    /// create an R32_FLOAT Texture2D or Texture2DMS SRV over it and transition it
    /// DEPTH_WRITE ↔ DEPTH_READ|PIXEL_SHADER_RESOURCE around sampled draws. Changes identity on
    /// <see cref="Resize" />, so any SRV over it must be recreated afterward.</summary>
    public ID3D12Resource? DepthResource => _depthTexture;

    /// <summary>
    ///     Returns the current back buffer's resource handle + RTV descriptor for the frame.
    ///     <see cref="ResolveTo" /> transitions it PRESENT → RENDER_TARGET and
    ///     <see cref="FinishBackBuffer" /> transitions it back before <see cref="Present" />.
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
        _hdrResolve?.Dispose();
        _hdrResolve = null;
        _waterOpaqueCopy?.Dispose();
        _waterOpaqueCopy = null;
        _waterOpaqueSnapshotPrepared = false;
        _tonemap.Dispose();
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
        ID3D12Resource? hdrResolve = null;

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
                Format = BackBufferFormat, // 8-bit display format; the scene renders HDR + tonemaps into this
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
                DescriptorCount = 2, // writable + read-only views of the same depth resource
                Flags = DescriptorHeapFlags.None
            });

            var rtvDescriptorSize = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
            backBuffers = AcquireBackBuffers(gpu.Device, swapChain3, rtvHeap);
            depthTexture = CreateDepthBuffer(gpu.Device, width, height, dsvHeap, sampleCount);
            msaaColor = CreateSceneColor(gpu.Device, width, height, sampleCount, rtvHeap, rtvDescriptorSize);
            hdrResolve = CreateHdrResolve(gpu.Device, width, height, sampleCount);

            Log.Info("GpuSwapChainSurface12: bound {0}x{1} to SwapChainPanel ({2} buffers, MSAA {3}x)",
                width, height, BufferCount, sampleCount);
            return new GpuSwapChainSurface12(
                gpu, swapChain3, rtvHeap, dsvHeap, backBuffers, depthTexture, msaaColor, hdrResolve,
                sampleCount, width, height);
        }
        catch (SharpGenException ex)
        {
            Log.Warn("GpuSwapChainSurface12.Create failed: {0}", ex.Message);
            hdrResolve?.Dispose();
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
        _hdrResolve?.Dispose();
        _hdrResolve = null;
        _waterOpaqueCopy?.Dispose();
        _waterOpaqueCopy = null;
        _waterOpaqueSnapshotPrepared = false;

        _swapChain.ResizeBuffers(BufferCount, width, height, Format.Unknown, SwapChainFlags.None).CheckError();

        var newBuffers = AcquireBackBuffers(_device, _swapChain, _rtvHeap);
        for (int i = 0; i < newBuffers.Length; i++) _backBuffers[i] = newBuffers[i];
        _depthTexture = CreateDepthBuffer(_device, width, height, _dsvHeap, _sampleCount);
        _msaaColor = CreateSceneColor(_device, width, height, _sampleCount, _rtvHeap, _rtvDescriptorSize);
        _hdrResolve = CreateHdrResolve(_device, width, height, _sampleCount);

        _width = width;
        _height = height;
    }

    /// <summary>
    ///     Captures the opaque HDR scene into <see cref="WaterOpaqueSnapshotResource" /> and leaves
    ///     that resource in PIXEL_SHADER_RESOURCE state. The method first unbinds every output-merger
    ///     target, so the scene color can never remain an active RTV while it is used as a resolve or
    ///     copy source. It restores the scene color resource to RENDER_TARGET but intentionally leaves
    ///     the output merger unbound; the caller must bind the scene RTV with the DSV state appropriate
    ///     for its water pass.
    /// </summary>
    /// <returns>False without recording commands when disposed, unavailable, or already prepared.</returns>
    public bool TryPrepareWaterOpaqueSnapshot(ID3D12GraphicsCommandList cmd)
    {
        var sceneColor = _msaaColor;
        var snapshot = WaterOpaqueSnapshotResource;
        if (sceneColor is null || snapshot is null || _waterOpaqueSnapshotPrepared)
        {
            return false;
        }

        // Do not let a resource that is still output-merger-bound participate in Resolve/Copy. The
        // caller explicitly rebinds the scene RTV (and writable/read-only/no DSV) after this method.
        cmd.UnsetRenderTargets();

        if (_hdrResolve is not null)
        {
            cmd.ResourceBarrierTransition(sceneColor, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
            cmd.ResolveSubresource(snapshot, 0, sceneColor, 0, SceneColorFormat);
            cmd.ResourceBarrierTransition(snapshot, ResourceStates.ResolveDest, ResourceStates.PixelShaderResource);
            cmd.ResourceBarrierTransition(sceneColor, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
        }
        else
        {
            cmd.ResourceBarrierTransition(sceneColor, ResourceStates.RenderTarget, ResourceStates.CopySource);
            cmd.CopyResource(snapshot, sceneColor);
            cmd.ResourceBarrierTransition(snapshot, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
            cmd.ResourceBarrierTransition(sceneColor, ResourceStates.CopySource, ResourceStates.RenderTarget);
        }

        _waterOpaqueSnapshotPrepared = true;
        return true;
    }

    /// <summary>
    ///     Ends WATER001 sampling and returns the snapshot to its normal idle state: ResolveDest for
    ///     the borrowed MSAA HDR resolve, CopyDest for the dedicated 1x copy. The scene color remains
    ///     in RenderTarget throughout this operation.
    /// </summary>
    /// <returns>False without recording commands when no prepared snapshot exists.</returns>
    public bool RestoreWaterOpaqueSnapshot(ID3D12GraphicsCommandList cmd)
    {
        var snapshot = WaterOpaqueSnapshotResource;
        if (snapshot is null || !_waterOpaqueSnapshotPrepared)
        {
            return false;
        }

        var idleState = _hdrResolve is not null
            ? ResourceStates.ResolveDest
            : ResourceStates.CopyDest;
        cmd.ResourceBarrierTransition(snapshot, ResourceStates.PixelShaderResource, idleState);
        _waterOpaqueSnapshotPrepared = false;
        return true;
    }

    /// <summary>
    ///     Maps the HDR scene color into the current back buffer: (MSAA) resolve the multisampled
    ///     scene color into the 1-sample HDR target, then run the fullscreen tonemap into the back
    ///     buffer's RTV; (1-sample) tonemap the scene color directly. Call after the scene draws and
    ///     before <see cref="Present" />. Leaves the back buffer in RENDER_TARGET so callers can
    ///     composite LDR editor/debug overlays that must not participate in scene exposure or bloom.
    ///     Call <see cref="FinishBackBuffer" /> after those overlays. Every HDR scene target is
    ///     restored to its render state for the next frame.
    /// </summary>
    public void ResolveTo(ID3D12GraphicsCommandList cmd, ID3D12Resource backBuffer)
    {
        if (_msaaColor is null) return;

        // WATER001 borrows _hdrResolve in the MSAA path. Restore its ordinary ResolveDest baseline
        // defensively so a missed host cleanup cannot make the final resolve use a PSR-state dest.
        RestoreWaterOpaqueSnapshot(cmd);

        // 1. Obtain the 1-sample HDR image the tonemap samples.
        ID3D12Resource hdrSource;
        if (_hdrResolve is not null)
        {
            cmd.ResourceBarrierTransition(_msaaColor, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
            cmd.ResolveSubresource(_hdrResolve, 0, _msaaColor, 0, SceneColorFormat);
            cmd.ResourceBarrierTransition(_hdrResolve, ResourceStates.ResolveDest, ResourceStates.PixelShaderResource);
            hdrSource = _hdrResolve;
        }
        else
        {
            cmd.ResourceBarrierTransition(_msaaColor, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
            hdrSource = _msaaColor;
        }

        // 2. Tonemap HDR → back buffer (as an RTV). Recompute the current back buffer's RTV so the
        //    caller need not thread it through (index is stable until Present).
        var index = _swapChain.CurrentBackBufferIndex;
        var backRtv = new CpuDescriptorHandle(_rtvHeap.GetCPUDescriptorHandleForHeapStart(), (int)index, _rtvDescriptorSize);
        cmd.ResourceBarrierTransition(backBuffer, ResourceStates.Present, ResourceStates.RenderTarget);
        _tonemap.Record(cmd, hdrSource, SceneColorFormat, backRtv, (int)_width, (int)_height, TonemapSettings, _tonemapEnabled);

        // 3. Restore scene-target states for next frame.
        if (_hdrResolve is not null)
        {
            cmd.ResourceBarrierTransition(_hdrResolve, ResourceStates.PixelShaderResource, ResourceStates.ResolveDest);
            cmd.ResourceBarrierTransition(_msaaColor, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
        }
        else
        {
            cmd.ResourceBarrierTransition(_msaaColor, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
        }
    }

    /// <summary>
    ///     Completes a frame after <see cref="ResolveTo" /> and any post-tonemap LDR overlays by
    ///     transitioning the current back buffer to PRESENT. Kept separate so diagnostic overlays
    ///     (collision cages, editor guides) cannot contaminate HDR adaptation or bloom.
    /// </summary>
    public void FinishBackBuffer(ID3D12GraphicsCommandList cmd, ID3D12Resource backBuffer) =>
        cmd.ResourceBarrierTransition(backBuffer, ResourceStates.RenderTarget, ResourceStates.Present);

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
        // R32_TYPELESS permits both a typed D32_Float DSV and an R32_Float SRV. Under MSAA the
        // latter is a Texture2DMS view used by soft particles in the normal 4x GUI path.
        var msaa = sampleCount > 1;
        var resourceDesc = ResourceDescription.Texture2D(
            Format.R32_Typeless,
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

        var dsvSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        var writableDsv = dsvHeap.GetCPUDescriptorHandleForHeapStart();
        var readOnlyDsv = new CpuDescriptorHandle(writableDsv, 1, dsvSize);
        var dsvDesc = new DepthStencilViewDescription
        {
            Format = Format.D32_Float,
            ViewDimension = msaa
                ? DepthStencilViewDimension.Texture2DMultisampled
                : DepthStencilViewDimension.Texture2D,
            Flags = DepthStencilViewFlags.None
        };
        device.CreateDepthStencilView(depth, dsvDesc, writableDsv);
        dsvDesc.Flags = DepthStencilViewFlags.ReadOnlyDepth;
        device.CreateDepthStencilView(depth, dsvDesc, readOnlyDsv);
        return depth;
    }

    /// <summary>
    ///     Creates the HDR scene color target (float, MSAA or 1-sample) and its RTV at heap slot
    ///     <see cref="BufferCount" />. The scene ALWAYS renders into this (the back buffer is now an
    ///     8-bit display target the tonemap writes), so — unlike the old MSAA-only color — it exists
    ///     even at 1-sample. Created in RENDER_TARGET state and kept there across frames
    ///     (<see cref="ResolveTo" /> round-trips it).
    /// </summary>
    private static ID3D12Resource CreateSceneColor(
        ID3D12Device device,
        uint width,
        uint height,
        int sampleCount,
        ID3D12DescriptorHeap rtvHeap,
        uint rtvDescriptorSize)
    {
        var color = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties,
            HeapFlags.None,
            ResourceDescription.Texture2D(SceneColorFormat, width, height,
                arraySize: 1, mipLevels: 1, sampleCount: (uint)sampleCount, sampleQuality: 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget,
            // No optimized clear value: the scene re-clears every frame, and a fixed optimized value
            // that doesn't match the clear color only triggers a debug-layer warning.
            optimizedClearValue: null);

        var handle = new CpuDescriptorHandle(
            rtvHeap.GetCPUDescriptorHandleForHeapStart(), BufferCount, rtvDescriptorSize);
        device.CreateRenderTargetView(color, null, handle);
        return color;
    }

    /// <summary>
    ///     1-sample HDR resolve destination for the MSAA path (null when 1-sample). The MSAA scene
    ///     color resolves into this, which the tonemap then samples as an SRV. Starts (and is restored
    ///     to) ResolveDest each frame.
    /// </summary>
    private static ID3D12Resource? CreateHdrResolve(ID3D12Device device, uint width, uint height, int sampleCount)
    {
        if (sampleCount <= 1) return null;

        return device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties,
            HeapFlags.None,
            ResourceDescription.Texture2D(SceneColorFormat, width, height,
                arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0, ResourceFlags.None),
            ResourceStates.ResolveDest,
            optimizedClearValue: null);
    }

    /// <summary>
    ///     Dedicated 1-sample opaque-scene copy for WATER001 when the scene itself is single-sample.
    ///     MSAA reuses <see cref="_hdrResolve" /> and therefore needs no additional allocation.
    /// </summary>
    private static ID3D12Resource? CreateWaterOpaqueCopy(
        ID3D12Device device,
        uint width,
        uint height,
        int sampleCount)
    {
        if (sampleCount > 1) return null;

        return device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties,
            HeapFlags.None,
            ResourceDescription.Texture2D(SceneColorFormat, width, height,
                arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0, ResourceFlags.None),
            ResourceStates.CopyDest,
            optimizedClearValue: null);
    }
}
#endif
