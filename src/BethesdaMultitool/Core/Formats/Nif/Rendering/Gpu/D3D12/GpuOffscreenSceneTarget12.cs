using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Diagnostics;
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
///         frames (<see cref="Bind" /> → renderer draws → <see cref="RecordReadback" /> → EndFrame),
///         waiting on each submission fence then calling <see cref="ReadbackToBytes" /> before the
///         next cycle. The target is REUSED across requests at the same size; every state round-trips
///         at the end of a cycle so a subsequent <see cref="Bind" /> is valid. <see cref="Dispose" />
///         on teardown.
///     </para>
/// </summary>
internal sealed unsafe class GpuOffscreenSceneTarget12 : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    // HDR scene color the renderers draw into (shared with the live swap-chain PSOs), and the 8-bit
    // format the tonemap writes + the readback consumes.
    internal static readonly Format ColorFormat = GpuSceneFormats.SceneColor;
    internal const Format LdrFormat = GpuSceneFormats.LdrOutput;
    internal const Format DepthFormat = Format.D32_Float;

    private static readonly bool HdrActive = GpuSceneFormats.SceneColor == Format.R16G16B16A16_Float;

    private readonly GpuDevice12 _gpu;
    private readonly ID3D12Resource _colorTex;   // scene color (HDR, MSAA or 1-sample) — render target
    private readonly ID3D12Resource _depthTex;
    // 1-sample HDR resolve target (null when not MSAA): the MSAA scene color resolves into this, which
    // the tonemap then samples as an SRV.
    private readonly ID3D12Resource? _hdrResolveTex;
    // Dedicated 1-sample opaque-scene snapshot when MSAA is disabled. The MSAA path reuses
    // _hdrResolveTex, whose size/format/sample layout already matches WATER001's sampling contract.
    private ID3D12Resource? _waterOpaqueCopy;
    private bool _waterOpaqueSnapshotPrepared;
    // 1-sample 8-bit tonemap output = the readback copy source.
    private readonly ID3D12Resource _ldrOutputTex;
    private readonly GpuTonemapPass12 _tonemap;
    private readonly bool _tonemapEnabled;

    /// <summary>
    ///     Tonemap operator + parameters for this target. Defaults to gamma-corrected ACES; world-aware
    ///     callers (frame/capture paths) override per game + active imagespace before rendering.
    ///     FALLOUT_VIEWER_TONEMAP / FALLOUT_VIEWER_EXPOSURE overrides are folded in at construction; a
    ///     setter value should already have <see cref="GpuTonemapSettings.ApplyOverrides" /> applied.
    /// </summary>
    public GpuTonemapSettings TonemapSettings { get; set; }
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly CpuDescriptorHandle _rtvHandle;    // scene color RTV
    private readonly CpuDescriptorHandle _ldrRtvHandle; // tonemap output RTV
    private readonly CpuDescriptorHandle _dsvHandle;
    private readonly CpuDescriptorHandle _readOnlyDsvHandle;
    private ID3D12Resource? _readback;
    private PlacedSubresourceFootPrint _readbackFootprint;
    private uint _readbackRowPitch;
    private bool _disposed;

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
                    arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0,
                    ResourceFlags.None),
                ResourceStates.CopyDest, optimizedClearValue: null);
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

    /// <summary>True while <see cref="WaterOpaqueSnapshotResource" /> is in
    /// PIXEL_SHADER_RESOURCE state between prepare and restore.</summary>
    public bool IsWaterOpaqueSnapshotPrepared => _waterOpaqueSnapshotPrepared;

    /// <summary>
    ///     Clears CPU preparation state for a command list that is being discarded without submission.
    ///     The GPU resource remains in its prior ResolveDest/CopyDest baseline because the barriers were
    ///     never executed.
    /// </summary>
    public void DiscardWaterOpaqueSnapshotPreparation() =>
        _waterOpaqueSnapshotPrepared = false;

    /// <summary>
    ///     Releases a dedicated lazy 1x copy after host descriptor creation fails. The borrowed MSAA
    ///     resolve target remains owned by the offscreen target.
    /// </summary>
    public bool ReleaseDedicatedWaterOpaqueSnapshotResource()
    {
        if (_disposed || _waterOpaqueSnapshotPrepared || _waterOpaqueCopy is null) return false;

        _waterOpaqueCopy.Dispose();
        _waterOpaqueCopy = null;
        return true;
    }

    /// <summary>Whether the most recent readback tonemap invalidated eye-adaptation history.</summary>
    public bool TonemapHistoryReset => _tonemap.LastHistoryReset;

    /// <summary>Inputs that caused the most recent eye-adaptation history invalidation.</summary>
    public string? TonemapHistoryResetReason => _tonemap.LastHistoryResetReason;

    /// <summary>
    ///     The R32_Typeless depth texture, exposed so the capture path can bind it as an R32_Float
    ///     Texture2D or Texture2DMS SRV. The caller owns transitions around sampled draws.
    /// </summary>
    public ID3D12Resource DepthResource => _depthTex;

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
                arraySize: 1, mipLevels: 1, sampleCount: (uint)sampleCount, sampleQuality: 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget,
            new ClearValue(ColorFormat, new Color4(0f, 0f, 0f, 0f)));

        _depthTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(Format.R32_Typeless, (uint)width, (uint)height,
                arraySize: 1, mipLevels: 1, sampleCount: (uint)sampleCount, sampleQuality: 0,
                ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite,
            new ClearValue(DepthFormat, new DepthStencilValue(0.0f, 0))); // reversed-Z: far value = 0

        if (msaa)
        {
            // 1-sample HDR resolve destination + tonemap SRV source. Starts (and is restored to)
            // ResolveDest each cycle.
            _hdrResolveTex = device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Texture2D(ColorFormat, (uint)width, (uint)height,
                    arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0,
                    ResourceFlags.None),
                ResourceStates.ResolveDest, optimizedClearValue: null);
        }

        // 8-bit tonemap output = readback source. Starts (and is restored to) RenderTarget.
        _ldrOutputTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(LdrFormat, (uint)width, (uint)height,
                arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget,
            new ClearValue(LdrFormat, new Color4(0f, 0f, 0f, 0f)));

        _rtvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = 2, // [0] scene color, [1] LDR tonemap output
            Flags = DescriptorHeapFlags.None,
        });
        _dsvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.DepthStencilView,
            DescriptorCount = 2, // writable + read-only views of the same depth resource
            Flags = DescriptorHeapFlags.None,
        });
        var rtvDescriptorSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        _rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        _ldrRtvHandle = _rtvHandle;
        _ldrRtvHandle.Ptr += (nuint)rtvDescriptorSize;
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
            Flags = DepthStencilViewFlags.None,
        };
        device.CreateDepthStencilView(_depthTex, dsvDesc, _dsvHandle);
        dsvDesc.Flags = DepthStencilViewFlags.ReadOnlyDepth;
        device.CreateDepthStencilView(_depthTex, dsvDesc, _readOnlyDsvHandle);
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

    /// <summary>Re-binds the color target WITHOUT the depth target and without clearing — used while
    /// the depth texture is transitioned to a shader resource for the water depth-fade pass.</summary>
    public void BindColorOnly(ID3D12GraphicsCommandList cmd) => cmd.OMSetRenderTargets(_rtvHandle);

    /// <summary>Re-binds color with a read-only depth view while depth is also an SRV.</summary>
    public void BindColorReadOnlyDepth(ID3D12GraphicsCommandList cmd) =>
        cmd.OMSetRenderTargets(_rtvHandle, _readOnlyDsvHandle);

    /// <summary>Re-binds color + depth without clearing (restores the targets after the water pass).</summary>
    public void Rebind(ID3D12GraphicsCommandList cmd) => cmd.OMSetRenderTargets(_rtvHandle, _dsvHandle);

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
        if (_disposed || snapshot is null || _waterOpaqueSnapshotPrepared)
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

        _waterOpaqueSnapshotPrepared = true;
        return true;
    }

    /// <summary>Returns a prepared snapshot to ResolveDest (MSAA) or CopyDest (1x).</summary>
    /// <returns>False without recording commands when no prepared snapshot exists.</returns>
    public bool RestoreWaterOpaqueSnapshot(ID3D12GraphicsCommandList cmd)
    {
        var snapshot = WaterOpaqueSnapshotResource;
        if (snapshot is null || !_waterOpaqueSnapshotPrepared)
        {
            return false;
        }

        var idleState = _hdrResolveTex is not null
            ? ResourceStates.ResolveDest
            : ResourceStates.CopyDest;
        cmd.ResourceBarrierTransition(snapshot, ResourceStates.PixelShaderResource, idleState);
        _waterOpaqueSnapshotPrepared = false;
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
            cmd.ResourceBarrierTransition(_hdrResolveTex, ResourceStates.ResolveDest, ResourceStates.PixelShaderResource);
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
            new TextureCopyLocation(_ldrOutputTex, 0));
        cmd.ResourceBarrierTransition(_ldrOutputTex, ResourceStates.CopySource, ResourceStates.RenderTarget);

        if (_hdrResolveTex is not null)
        {
            cmd.ResourceBarrierTransition(_hdrResolveTex, ResourceStates.PixelShaderResource, ResourceStates.ResolveDest);
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
        }
        else
        {
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
        }
    }

    private void EnsureReadback(ID3D12Device device)
    {
        if (_readback is not null)
        {
            return;
        }

        // Footprint of the 1-sample 8-bit LDR output (the tonemap result the readback copies).
        var copyDesc = ResourceDescription.Texture2D(LdrFormat, (uint)Width, (uint)Height,
            arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0, ResourceFlags.None);
        var footprints = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSize = new ulong[1];
        device.GetCopyableFootprints(copyDesc, 0, 1, 0, footprints, numRows, rowSize, out var readbackBytes);

        _readback = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(readbackBytes),
            ResourceStates.CopyDest, optimizedClearValue: null);
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
            _readback.Unmap(0, null);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _waterOpaqueSnapshotPrepared = false;
        _readback?.Dispose();
        _tonemap.Dispose();
        _dsvHeap.Dispose();
        _rtvHeap.Dispose();
        _ldrOutputTex.Dispose();
        _waterOpaqueCopy?.Dispose();
        _hdrResolveTex?.Dispose();
        _depthTex.Dispose();
        _colorTex.Dispose();
    }
}
