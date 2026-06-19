using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A one-shot offscreen color + depth render target with a CPU readback path, used to render
///     a full scene (terrain depth pre-pass + placed references) top-down for the 2D map's
///     "Rendered models" overlay. Factored from <see cref="GpuSpriteRenderer12" />'s offscreen
///     machinery, but sized for a whole viewport and matched to the live renderers' PSO formats
///     (<see cref="Format.B8G8R8A8_UNorm" /> color, <see cref="Format.D32_Float" /> depth, single
///     sample — see <c>TerrainRenderer12</c>/<c>ReferenceRenderer12</c>). Anti-aliasing is done by
///     the caller via supersize-then-downsample rather than MSAA, since the live PSOs are 1-sample.
///     <para>
///         Lifetime: create at the (supersampled) target size, then drive one or more recorder
///         frames (<see cref="Bind" /> → renderer draws → <see cref="RecordReadback" /> → EndFrame),
///         waiting on each submission fence then calling <see cref="ReadbackToBytes" /> before the
///         next cycle. The target is REUSED across overlay requests at the same size (the caller
///         re-creates it only when the size changes), so <see cref="RecordReadback" /> restores the
///         color target to <see cref="ResourceStates.RenderTarget" /> at the end of each cycle and
///         the readback buffer is allocated once and reused. <see cref="Dispose" /> on teardown.
///         Reuse is single-flighted by the caller (one in-flight render + readback at a time), so
///         the shared readback buffer is never mapped while the next copy is recorded.
///     </para>
/// </summary>
internal sealed unsafe class GpuOffscreenSceneTarget12 : IDisposable
{
    // Match the live terrain/reference PSOs (TerrainRenderer12 + ReferenceRenderer12).
    internal const Format ColorFormat = Format.B8G8R8A8_UNorm;
    internal const Format DepthFormat = Format.D32_Float;

    private readonly GpuDevice12 _gpu;
    private readonly int _sampleCount;
    private readonly ID3D12Resource _colorTex;
    private readonly ID3D12Resource _depthTex;
    // Single-sample resolve target (null when not MSAA): the MSAA color is resolved into this, which
    // is then the CopySource for the readback. Matches the scene's MSAA so terrain/reference PSOs
    // (SampleDescription = GpuDevice12.SceneSampleCount) draw into this target unchanged.
    private readonly ID3D12Resource? _resolveTex;
    private readonly ID3D12DescriptorHeap _rtvHeap;
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly CpuDescriptorHandle _rtvHandle;
    private readonly CpuDescriptorHandle _dsvHandle;
    private ID3D12Resource? _readback;
    private PlacedSubresourceFootPrint _readbackFootprint;
    private uint _readbackRowPitch;
    private bool _disposed;

    /// <summary>Pixel width of the target.</summary>
    public int Width { get; }

    /// <summary>Pixel height of the target.</summary>
    public int Height { get; }

    public GpuOffscreenSceneTarget12(GpuDevice12 gpu, int width, int height)
    {
        _gpu = gpu;
        _sampleCount = gpu.SceneSampleCount;
        Width = width;
        Height = height;
        var device = gpu.Device;
        var msaa = _sampleCount > 1;

        _colorTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(ColorFormat, (uint)width, (uint)height,
                arraySize: 1, mipLevels: 1, sampleCount: (uint)_sampleCount, sampleQuality: 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget,
            new ClearValue(ColorFormat, new Color4(0f, 0f, 0f, 0f)));

        _depthTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(DepthFormat, (uint)width, (uint)height,
                arraySize: 1, mipLevels: 1, sampleCount: (uint)_sampleCount, sampleQuality: 0,
                ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite,
            new ClearValue(DepthFormat, new DepthStencilValue(0.0f, 0))); // reversed-Z: far value = 0

        if (msaa)
        {
            // Single-sample resolve destination + readback source. Starts (and is restored to)
            // ResolveDest each cycle (see RecordReadback).
            _resolveTex = device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Texture2D(ColorFormat, (uint)width, (uint)height,
                    arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0,
                    ResourceFlags.None),
                ResourceStates.ResolveDest, optimizedClearValue: null);
        }

        _rtvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.None,
        });
        _dsvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.DepthStencilView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.None,
        });
        _rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        _dsvHandle = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        // Null view desc infers Texture2D (1 sample) or Texture2DMS (MSAA) automatically.
        device.CreateRenderTargetView(_colorTex, null, _rtvHandle);
        device.CreateDepthStencilView(_depthTex, null, _dsvHandle);
    }

    /// <summary>
    ///     Binds the offscreen color + depth as the render targets, sets the full-target viewport
    ///     and scissor, and clears color to transparent + depth to the far plane. Call after the
    ///     shared descriptor heap + root signature are bound and before any renderer draws.
    /// </summary>
    public void Bind(ID3D12GraphicsCommandList cmd)
    {
        cmd.OMSetRenderTargets(_rtvHandle, _dsvHandle);
        cmd.ClearRenderTargetView(_rtvHandle, new Color4(0f, 0f, 0f, 0f));
        cmd.ClearDepthStencilView(_dsvHandle, ClearFlags.Depth, 0f, 0); // reversed-Z: far value = 0
        cmd.RSSetViewport(new Viewport(0, 0, Width, Height, 0f, 1f));
        cmd.RSSetScissorRect(Width, Height);
    }

    /// <summary>
    ///     Records the color → readback-buffer copy (transitioning the color target to
    ///     <see cref="ResourceStates.CopySource" />, copying into the readback heap buffer, then
    ///     transitioning the color target back to <see cref="ResourceStates.RenderTarget" /> so a
    ///     subsequent <see cref="Bind" /> on the reused target is state-valid). The readback buffer
    ///     is allocated once on first use and reused (the target's size is fixed for its lifetime —
    ///     the caller recreates the whole target on resize). Must be the last thing recorded before
    ///     the recorder's EndFrame, so the copy + restore are part of the submitted command list.
    /// </summary>
    public void RecordReadback(ID3D12GraphicsCommandList cmd)
    {
        var device = _gpu.Device;

        if (_readback is null)
        {
            // Footprint of the 1-sample BGRA copy source (resolveTex when MSAA, else colorTex) — both
            // are Width×Height B8G8R8A8_UNorm, so a 1-sample colorDesc gives the right footprint.
            var copyDesc = ResourceDescription.Texture2D(ColorFormat, (uint)Width, (uint)Height,
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

        if (_resolveTex is not null)
        {
            // MSAA: resolve the multisampled color into the 1-sample resolve target, then copy that
            // into the readback buffer. States are round-tripped so the target is reusable next cycle:
            // colorTex → RenderTarget, resolveTex → ResolveDest.
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
            cmd.ResolveSubresource(_resolveTex, 0, _colorTex, 0, ColorFormat);
            cmd.ResourceBarrierTransition(_resolveTex, ResourceStates.ResolveDest, ResourceStates.CopySource);
            cmd.CopyTextureRegion(
                new TextureCopyLocation(_readback, _readbackFootprint), 0, 0, 0,
                new TextureCopyLocation(_resolveTex, 0));
            cmd.ResourceBarrierTransition(_resolveTex, ResourceStates.CopySource, ResourceStates.ResolveDest);
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
        }
        else
        {
            // Single-sample: copy the color target straight into the readback buffer, restoring it to
            // RenderTarget so the next Bind's clear/render is state-valid (the target is reused).
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.RenderTarget, ResourceStates.CopySource);
            cmd.CopyTextureRegion(
                new TextureCopyLocation(_readback, _readbackFootprint), 0, 0, 0,
                new TextureCopyLocation(_colorTex, 0));
            cmd.ResourceBarrierTransition(_colorTex, ResourceStates.CopySource, ResourceStates.RenderTarget);
        }
    }

    /// <summary>
    ///     Maps the readback buffer and copies out a tightly-packed BGRA byte array
    ///     (<see cref="Width" /> × <see cref="Height" /> × 4). Call only after the submission fence
    ///     that contained <see cref="RecordReadback" /> has completed. Safe to call off the UI
    ///     thread (fence/Map are thread-safe).
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
        _readback?.Dispose();
        _dsvHeap.Dispose();
        _rtvHeap.Dispose();
        _resolveTex?.Dispose();
        _depthTex.Dispose();
        _colorTex.Dispose();
    }
}
