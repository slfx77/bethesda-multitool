using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Diagnostics;
using Vortice;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A one-shot offscreen scene render target with a CPU readback path, used by every headless
///     render (the profiler's <c>--render-nif</c>, <c>--capture-frame</c>, the 2D-map top-down
///     overlay, and the 3D export). Renders into an HDR float scene color
///     (<see cref="GpuSceneFormats.SceneColor" />, MSAA), resolves it, runs the fullscreen
///     <see cref="GpuTonemapPass12" /> into an 8-bit <see cref="GpuSceneFormats.LdrOutput" /> target,
///     and reads THAT back — so emissive glow / sun specular / imagespace scales above 1 are rolled
///     off instead of clipped (matching the live swap-chain path). When the scene color is the legacy
///     8-bit format the tonemap runs in passthrough (clamp) so the output is bit-identical to before.
///     <para>
///         Lifetime: create at the (supersampled) target size, then drive one or more recorder
///         frames. A captured frame is <see cref="Bind" /> → renderer draws →
///         <see cref="RecordReadback" /> → EndFrame, followed by a fence wait and
///         <see cref="ReadbackToBytes" />. A render-only settlement frame substitutes
///         <see cref="FinishFrameWithoutReadback" /> and needs no immediate CPU fence wait. The
///         target is REUSED across requests at the same size; both endings restore reusable resource
///         state so a subsequent <see cref="Bind" /> is valid. <see cref="Dispose" /> on teardown.
///     </para>
/// </summary>
internal sealed unsafe class GpuOffscreenSceneTarget12 : IDisposable
{
    internal const Format LdrFormat = GpuSceneFormats.LdrOutput;
    internal const Format DepthFormat = Format.D32_Float;
    private static readonly Logger Log = Logger.Instance;

    // HDR scene color the renderers draw into (shared with the live swap-chain PSOs), and the 8-bit
    // format the tonemap writes + the readback consumes.
    internal static readonly Format ColorFormat = GpuSceneFormats.SceneColor;

    private static readonly bool HdrActive = GpuSceneFormats.SceneColor == Format.R16G16B16A16_Float;
    private readonly ID3D12Resource _colorTex; // scene color (HDR, MSAA or 1-sample) — render target
    private readonly CpuDescriptorHandle _dsvHandle;
    private readonly ID3D12DescriptorHeap _dsvHeap;

    private readonly GpuDevice12 _gpu;

    // 1-sample HDR resolve target (null when not MSAA): the MSAA scene color resolves into this, which
    // the tonemap then samples as an SRV.
    private readonly ID3D12Resource? _hdrResolveTex;

    // 1-sample 8-bit tonemap output = the readback copy source.
    private readonly ID3D12Resource _ldrOutputTex;
    private readonly CpuDescriptorHandle _ldrRtvHandle; // tonemap output RTV
    private readonly CpuDescriptorHandle _readOnlyDsvHandle;
    private readonly CpuDescriptorHandle _rtvHandle; // scene color RTV
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly GpuTonemapPass12 _tonemap;
    private readonly bool _tonemapEnabled;
    private bool _disposed;
    private ID3D12Resource? _readback;
    private PlacedSubresourceFootPrint _readbackFootprint;
    private uint _readbackRowPitch;
    private bool _resolvedDepthPrepared;

    private ID3D12Resource? _resolvedDepthTex;

    private bool _resolvedDepthUnavailable;

    // Dedicated 1-sample opaque-scene snapshot when MSAA is disabled. The MSAA path reuses
    // _hdrResolveTex, whose size/format/sample layout already matches WATER001's sampling contract.
    private ID3D12Resource? _waterOpaqueCopy;

    public GpuOffscreenSceneTarget12(GpuDevice12 gpu, int width, int height)
    {
        _gpu = gpu;
        var sampleCount = gpu.SceneSampleCount;
        Width = width;
        Height = height;
        var device = gpu.Device;
        var msaa = sampleCount > 1;
        IsMsaa = msaa;
        SampleCount = sampleCount;
        _tonemap = new GpuTonemapPass12(gpu);
        TonemapSettings = GpuTonemapSettings.ApplyOverrides(GpuTonemapSettings.GammaAcesDefaults);
        _tonemapEnabled = HdrActive && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0";

        _colorTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(ColorFormat, (uint)width, (uint)height,
                1, 1, (uint)sampleCount, 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget,
            new ClearValue(ColorFormat, new Color4(0f, 0f, 0f, 0f)));

        DepthResource = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(Format.R32_Typeless, (uint)width, (uint)height,
                1, 1, (uint)sampleCount, 0,
                ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite,
            new ClearValue(DepthFormat, new DepthStencilValue(0.0f))); // reversed-Z: far value = 0

        if (msaa)
        {
            // 1-sample HDR resolve destination + tonemap SRV source. Starts (and is restored to)
            // ResolveDest each cycle.
            _hdrResolveTex = device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Texture2D(ColorFormat, (uint)width, (uint)height,
                    1, 1),
                ResourceStates.ResolveDest);
        }

        // 8-bit tonemap output = readback source. Starts (and is restored to) RenderTarget.
        _ldrOutputTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(LdrFormat, (uint)width, (uint)height,
                1, 1, 1, 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget,
            new ClearValue(LdrFormat, new Color4(0f, 0f, 0f, 0f)));

        _rtvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = 2, // [0] scene color, [1] LDR tonemap output
            Flags = DescriptorHeapFlags.None
        });
        _dsvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.DepthStencilView,
            DescriptorCount = 2, // writable + read-only views of the same depth resource
            Flags = DescriptorHeapFlags.None
        });
        var rtvDescriptorSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        _rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        _ldrRtvHandle = _rtvHandle;
        _ldrRtvHandle.Ptr += rtvDescriptorSize;
        _dsvHandle = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        var dsvDescriptorSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        _readOnlyDsvHandle = new CpuDescriptorHandle(_dsvHandle, 1, dsvDescriptorSize);
        device.CreateRenderTargetView(_colorTex, null, _rtvHandle);
        device.CreateRenderTargetView(_ldrOutputTex, null, _ldrRtvHandle);
        var dsvDesc = new DepthStencilViewDescription
        {
            Format = DepthFormat,
            ViewDimension = msaa
                ? DepthStencilViewDimension.Texture2DMultisampled
                : DepthStencilViewDimension.Texture2D,
            Flags = DepthStencilViewFlags.None
        };
        device.CreateDepthStencilView(DepthResource, dsvDesc, _dsvHandle);
        dsvDesc.Flags = DepthStencilViewFlags.ReadOnlyDepth;
        device.CreateDepthStencilView(DepthResource, dsvDesc, _readOnlyDsvHandle);
    }

    /// <summary>
    ///     Tonemap operator + parameters for this target. Defaults to gamma-corrected ACES; world-aware
    ///     callers (frame/capture paths) override per game + active imagespace before rendering.
    ///     FALLOUT_VIEWER_TONEMAP / FALLOUT_VIEWER_EXPOSURE overrides are folded in at construction; a
    ///     setter value should already have <see cref="GpuTonemapSettings.ApplyOverrides" /> applied.
    /// </summary>
    public GpuTonemapSettings TonemapSettings { get; set; }

    /// <summary>Pixel width of the target.</summary>
    public int Width { get; }

    /// <summary>Pixel height of the target.</summary>
    public int Height { get; }

    /// <summary>True when the scene targets are multisampled.</summary>
    public bool IsMsaa { get; }

    /// <summary>Number of samples in the scene color and depth targets.</summary>
    public int SampleCount { get; }

    /// <summary>
    ///     Stable, single-sample HDR opaque-scene snapshot resource for bounded WATER001 refraction.
    ///     MSAA targets borrow the existing HDR resolve; 1x targets lazily own a dedicated copy. Null
    ///     before first eligible use and after disposal. Offscreen targets are fixed-size, so a size
    ///     change is represented by constructing a new target and therefore a new snapshot/SRV identity.
    /// </summary>
    public ID3D12Resource? WaterOpaqueSnapshotResource =>
        _disposed ? null : _hdrResolveTex ?? _waterOpaqueCopy;

    /// <summary>
    ///     True while <see cref="WaterOpaqueSnapshotResource" /> is in
    ///     PIXEL_SHADER_RESOURCE state between prepare and restore.
    /// </summary>
    public bool IsWaterOpaqueSnapshotPrepared { get; private set; }

    /// <summary>Whether the most recent readback tonemap invalidated eye-adaptation history.</summary>
    public bool TonemapHistoryReset => _tonemap.LastHistoryReset;

    /// <summary>Inputs that caused the most recent eye-adaptation history invalidation.</summary>
    public string? TonemapHistoryResetReason => _tonemap.LastHistoryResetReason;

    /// <summary>
    ///     The R32_Typeless depth texture, exposed so the capture path can bind it as an R32_Float
    ///     Texture2D or Texture2DMS SRV. The caller owns transitions around sampled draws.
    /// </summary>
    public ID3D12Resource DepthResource { get; }

    /// <summary>
    ///     Single-sample post-opaque depth copy: a MAX resolve of the MSAA depth (reversed-Z:
    ///     max = nearest covered sample — the exact semantics of the shader-side per-sample loop it
    ///     replaces), or a plain copy on 1x targets.
    ///     Null until <see cref="TryEnsureResolvedDepthResource" /> succeeds.
    /// </summary>
    public ID3D12Resource? ResolvedDepthResource => _disposed ? null : _resolvedDepthTex;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsWaterOpaqueSnapshotPrepared = false;
        _readback?.Dispose();
        _tonemap.Dispose();
        _dsvHeap.Dispose();
        _rtvHeap.Dispose();
        _ldrOutputTex.Dispose();
        _waterOpaqueCopy?.Dispose();
        _hdrResolveTex?.Dispose();
        _resolvedDepthTex?.Dispose();
        DepthResource.Dispose();
        _colorTex.Dispose();
    }

    /// <summary>
    ///     Ensures the single-sample snapshot exists. MSAA reuses its existing resolve resource; a
    ///     1x target allocates the full-resolution copy only after WATER001 eligibility succeeds.
    ///     Allocation failure simply keeps the established WATER003 fallback active.
    /// </summary>
    public bool TryEnsureWaterOpaqueSnapshotResource()
    {
        if (_disposed) return false;
        if (_hdrResolveTex is not null || _waterOpaqueCopy is not null) return true;

        try
        {
            _waterOpaqueCopy = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Texture2D(ColorFormat, (uint)Width, (uint)Height,
                    arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0),
                ResourceStates.CopyDest);
            return true;
        }
        catch (Exception ex)
        {
            _waterOpaqueCopy?.Dispose();
            _waterOpaqueCopy = null;
            Log.Warn("GpuOffscreenSceneTarget12: WATER001 opaque snapshot allocation failed: {0}", ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Clears CPU preparation state for a command list that is being discarded without submission.
    ///     The GPU resource remains in its prior ResolveDest/CopyDest baseline because the barriers were
    ///     never executed.
    /// </summary>
    public void DiscardWaterOpaqueSnapshotPreparation()
    {
        IsWaterOpaqueSnapshotPrepared = false;
    }

    /// <summary>
    ///     Releases a dedicated lazy 1x copy after host descriptor creation fails. The borrowed MSAA
    ///     resolve target remains owned by the offscreen target.
    /// </summary>
    public bool ReleaseDedicatedWaterOpaqueSnapshotResource()
    {
        if (_disposed || IsWaterOpaqueSnapshotPrepared || _waterOpaqueCopy is null) return false;

        _waterOpaqueCopy.Dispose();
        _waterOpaqueCopy = null;
        return true;
    }

    /// <summary>
    ///     Lazily creates the 1-sample R32 destination for the post-opaque depth copy: a MAX
    ///     resolve on MSAA targets, a plain CopyResource on 1x targets (the unified transparency
    ///     stream samples this copy while the live DSV stays WRITABLE — the same model as
    ///     GpuSwapChainSurface12's opaque depth snapshot). The
    ///     capture path binds THIS as water/reference scene depth (as a plain Texture2D with
    ///     sampleCount 1) instead of the multisampled alias: descriptor reads through the bindless
    ///     Texture2DMS space-3 alias proved unreliable for late-written slots on shipped drivers
    ///     (stale prior content — the live-window-sized "rectangle" capture corruption), while the
    ///     space-1 Texture2D path never misresolved. Allocation failure falls back to the legacy
    ///     multisampled binding.
    /// </summary>
    public bool TryEnsureResolvedDepthResource()
    {
        if (_disposed || _resolvedDepthUnavailable) return false;
        if (_resolvedDepthTex is not null) return true;

        try
        {
            // Baseline state matches the record step's source op: ResolveDest for the MSAA MAX
            // resolve, CopyDest for the 1x CopyResource arm.
            _resolvedDepthTex = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Texture2D(Format.R32_Typeless, (uint)Width, (uint)Height,
                    1, 1),
                IsMsaa ? ResourceStates.ResolveDest : ResourceStates.CopyDest);
            _resolvedDepthTex.Name = "Capture Depth MAX Resolve";
            return true;
        }
        catch (Exception ex)
        {
            _resolvedDepthTex?.Dispose();
            _resolvedDepthTex = null;
            _resolvedDepthUnavailable = true;
            Log.Warn("GpuOffscreenSceneTarget12: resolved-depth allocation failed: {0}", ex.Message);
            return false;
        }
    }

    /// <summary>
    ///     Records the MAX depth resolve. Call while the depth texture is in DEPTH_WRITE, after
    ///     every opaque depth writer and before the depth-sampling water/reference passes; leaves
    ///     depth back in DEPTH_WRITE and the resolved copy in PIXEL_SHADER_RESOURCE until
    ///     <see cref="RestoreResolvedDepth" />.
    /// </summary>
    public bool TryRecordDepthMaxResolve(ID3D12GraphicsCommandList cmd)
    {
        if (_disposed || _resolvedDepthTex is null || _resolvedDepthPrepared)
        {
            return false;
        }

        if (!IsMsaa)
        {
            // 1x arm: a straight copy carries the identical per-texel depth; there is nothing to
            // resolve. Same DepthWrite round-trip so later passes see the documented baseline.
            cmd.ResourceBarrierTransition(DepthResource,
                ResourceStates.DepthWrite, ResourceStates.CopySource);
            cmd.CopyResource(_resolvedDepthTex, DepthResource);
            cmd.ResourceBarrierTransition(DepthResource,
                ResourceStates.CopySource, ResourceStates.DepthWrite);
            cmd.ResourceBarrierTransition(_resolvedDepthTex,
                ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
            _resolvedDepthPrepared = true;
            return true;
        }

        using var cmd1 = cmd.QueryInterfaceOrNull<ID3D12GraphicsCommandList1>();
        if (cmd1 is null)
        {
            _resolvedDepthUnavailable = true;
            Log.Warn("GpuOffscreenSceneTarget12: ID3D12GraphicsCommandList1 unavailable; " +
                     "captures keep the multisampled depth binding.");
            return false;
        }

        cmd.ResourceBarrierTransition(DepthResource,
            ResourceStates.DepthWrite, ResourceStates.ResolveSource);
        cmd1.ResolveSubresourceRegion(
            _resolvedDepthTex, 0, 0, 0, DepthResource, 0,
            new RawRect(0, 0, Width, Height), Format.D32_Float, ResolveMode.Max);
        cmd.ResourceBarrierTransition(DepthResource,
            ResourceStates.ResolveSource, ResourceStates.DepthWrite);
        cmd.ResourceBarrierTransition(_resolvedDepthTex,
            ResourceStates.ResolveDest, ResourceStates.PixelShaderResource);
        _resolvedDepthPrepared = true;
        return true;
    }

    /// <summary>
    ///     Returns the resolved depth to its baseline (ResolveDest on MSAA targets, CopyDest
    ///     on 1x — records the barrier).
    /// </summary>
    public void RestoreResolvedDepth(ID3D12GraphicsCommandList cmd)
    {
        if (!_resolvedDepthPrepared || _resolvedDepthTex is null) return;
        cmd.ResourceBarrierTransition(_resolvedDepthTex,
            ResourceStates.PixelShaderResource,
            IsMsaa ? ResourceStates.ResolveDest : ResourceStates.CopyDest);
        _resolvedDepthPrepared = false;
    }

    /// <summary>
    ///     Clears resolve preparation state for a command list discarded without submission
    ///     (the barriers never executed, so the GPU resource keeps its ResolveDest baseline).
    /// </summary>
    public void DiscardResolvedDepthPreparation()
    {
        _resolvedDepthPrepared = false;
    }

    /// <summary>
    ///     Binds the offscreen color + depth as the render targets, sets the full-target viewport
    ///     and scissor, and clears color (transparent by default) + depth to the far plane. Call
    ///     after the shared descriptor heap + root signature are bound and before any renderer draws.
    ///     <paramref name="clearColor" /> supports harness renders that need an OPAQUE backdrop
    ///     already present at draw time (multiplicative decals compute <c>fb·srcColor</c>, which over
    ///     a transparent black clear is unconditionally black).
    /// </summary>
    public void Bind(ID3D12GraphicsCommandList cmd, Color4? clearColor = null)
    {
        cmd.OMSetRenderTargets(_rtvHandle, _dsvHandle);
        cmd.ClearRenderTargetView(_rtvHandle, clearColor ?? new Color4(0f, 0f, 0f, 0f));
        cmd.ClearDepthStencilView(_dsvHandle, ClearFlags.Depth, 0f, 0); // reversed-Z: far value = 0
        cmd.RSSetViewport(new Viewport(0, 0, Width, Height, 0f, 1f));
        cmd.RSSetScissorRect(Width, Height);
    }

    /// <summary>
    ///     Re-binds the color target WITHOUT the depth target and without clearing — used while
    ///     the depth texture is transitioned to a shader resource for the water depth-fade pass.
    /// </summary>
    public void BindColorOnly(ID3D12GraphicsCommandList cmd)
    {
        cmd.OMSetRenderTargets(_rtvHandle);
    }

    /// <summary>Re-binds color with a read-only depth view while depth is also an SRV.</summary>
    public void BindColorReadOnlyDepth(ID3D12GraphicsCommandList cmd)
    {
        cmd.OMSetRenderTargets(_rtvHandle, _readOnlyDsvHandle);
    }

    /// <summary>Re-binds color + depth without clearing (restores the targets after the water pass).</summary>
    public void Rebind(ID3D12GraphicsCommandList cmd)
    {
        cmd.OMSetRenderTargets(_rtvHandle, _dsvHandle);
    }

    /// <summary>
    ///     Captures the opaque HDR scene and leaves <see cref="WaterOpaqueSnapshotResource" /> in
    ///     PIXEL_SHADER_RESOURCE state. Every output-merger target is unbound before the scene color
    ///     transitions away from RenderTarget, and the scene resource is restored to RenderTarget
    ///     afterward. The output merger intentionally remains unbound; the caller chooses whether to
    ///     rebind writable depth, read-only depth, or color only for the following water pass.
    /// </summary>
    /// <returns>False without recording commands when disposed, unavailable, or already prepared.</returns>
    public bool TryPrepareWaterOpaqueSnapshot(ID3D12GraphicsCommandList cmd)
    {
        var snapshot = WaterOpaqueSnapshotResource;
        if (_disposed || snapshot is null || IsWaterOpaqueSnapshotPrepared)
        {
            return false;
        }

        cmd.UnsetRenderTargets();

        if (_hdrResolveTex is not null)
        {
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
            cmd.ResolveSubresource(snapshot, 0, _colorTex, 0, ColorFormat);
            cmd.ResourceBarrierTransition(snapshot, ResourceStates.ResolveDest, ResourceStates.PixelShaderResource);
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
        }
        else
        {
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.RenderTarget, ResourceStates.CopySource);
            cmd.CopyResource(snapshot, _colorTex);
            cmd.ResourceBarrierTransition(snapshot, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.CopySource, ResourceStates.RenderTarget);
        }

        IsWaterOpaqueSnapshotPrepared = true;
        return true;
    }

    /// <summary>Returns a prepared snapshot to ResolveDest (MSAA) or CopyDest (1x).</summary>
    /// <returns>False without recording commands when no prepared snapshot exists.</returns>
    public bool RestoreWaterOpaqueSnapshot(ID3D12GraphicsCommandList cmd)
    {
        var snapshot = WaterOpaqueSnapshotResource;
        if (snapshot is null || !IsWaterOpaqueSnapshotPrepared)
        {
            return false;
        }

        var idleState = _hdrResolveTex is not null
            ? ResourceStates.ResolveDest
            : ResourceStates.CopyDest;
        cmd.ResourceBarrierTransition(snapshot, ResourceStates.PixelShaderResource, idleState);
        IsWaterOpaqueSnapshotPrepared = false;
        return true;
    }

    /// <summary>
    ///     Records: (MSAA) resolve the scene color into the 1-sample HDR target; run the fullscreen
    ///     tonemap into the 8-bit output; copy that into the readback buffer. All states round-trip so
    ///     the target is reusable next cycle. Must be the last thing recorded before EndFrame.
    /// </summary>
    public void RecordReadback(ID3D12GraphicsCommandList cmd)
    {
        // The MSAA WATER001 path borrows _hdrResolveTex. Restore its ordinary ResolveDest baseline
        // defensively before the final capture resolve/tonemap cycle.
        RestoreWaterOpaqueSnapshot(cmd);

        var device = _gpu.Device;
        EnsureReadback(device);

        // 1. Get the 1-sample HDR image the tonemap samples. MSAA → resolve; single-sample → the
        //    scene color itself, transitioned to a shader resource.
        ID3D12Resource hdrSource;
        if (_hdrResolveTex is not null)
        {
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
            cmd.ResolveSubresource(_hdrResolveTex, 0, _colorTex, 0, ColorFormat);
            cmd.ResourceBarrierTransition(_hdrResolveTex, ResourceStates.ResolveDest,
                ResourceStates.PixelShaderResource);
            hdrSource = _hdrResolveTex;
        }
        else
        {
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
            hdrSource = _colorTex;
        }

        // 2. Tonemap HDR → LDR output.
        _tonemap.Record(cmd, hdrSource, ColorFormat, _ldrRtvHandle, Width, Height, TonemapSettings, _tonemapEnabled);

        // 3. Copy LDR output → readback, restoring every state.
        cmd.ResourceBarrierTransition(_ldrOutputTex, ResourceStates.RenderTarget, ResourceStates.CopySource);
        cmd.CopyTextureRegion(
            new TextureCopyLocation(_readback!, _readbackFootprint), 0, 0, 0,
            new TextureCopyLocation(_ldrOutputTex));
        cmd.ResourceBarrierTransition(_ldrOutputTex, ResourceStates.CopySource, ResourceStates.RenderTarget);

        if (_hdrResolveTex is not null)
        {
            cmd.ResourceBarrierTransition(_hdrResolveTex, ResourceStates.PixelShaderResource,
                ResourceStates.ResolveDest);
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
        }
        else
        {
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
        }
    }

    /// <summary>
    ///     Finishes a render-only frame without resolving, tonemapping, or copying pixels to the CPU.
    ///     Restores optional per-frame snapshot state so the next <see cref="Bind" /> starts from the
    ///     same reusable baseline as a captured frame. Must be the last target operation before EndFrame.
    /// </summary>
    public void FinishFrameWithoutReadback(ID3D12GraphicsCommandList cmd)
    {
        RestoreWaterOpaqueSnapshot(cmd);
    }

    private void EnsureReadback(ID3D12Device device)
    {
        if (_readback is not null)
        {
            return;
        }

        // Footprint of the 1-sample 8-bit LDR output (the tonemap result the readback copies).
        var copyDesc = ResourceDescription.Texture2D(LdrFormat, (uint)Width, (uint)Height,
            1, 1);
        var footprints = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSize = new ulong[1];
        device.GetCopyableFootprints(copyDesc, 0, 1, 0, footprints, numRows, rowSize, out var readbackBytes);

        _readback = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(readbackBytes),
            ResourceStates.CopyDest);
        _readbackFootprint = footprints[0];
        _readbackRowPitch = footprints[0].Footprint.RowPitch;
    }

    /// <summary>
    ///     Maps the readback buffer and copies out a tightly-packed BGRA byte array
    ///     (<see cref="Width" /> × <see cref="Height" /> × 4). Call only after the submission fence
    ///     that contained <see cref="RecordReadback" /> has completed.
    /// </summary>
    public byte[] ReadbackToBytes()
    {
        if (_readback is null)
        {
            throw new InvalidOperationException("ReadbackToBytes called before RecordReadback.");
        }

        void* cpuPtr = null;
        _readback.Map(0, &cpuPtr).CheckError();
        try
        {
            var pixels = new byte[Width * Height * 4];
            var rowSize = Width * 4;
            for (var y = 0; y < Height; y++)
            {
                var srcOffset = (int)(y * _readbackRowPitch);
                var dstOffset = y * rowSize;
                Marshal.Copy((nint)cpuPtr + srcOffset, pixels, dstOffset, rowSize);
            }

            return pixels;
        }
        finally
        {
            _readback.Unmap(0);
        }
    }
}
