#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Owns the directional sun-shadow map: a single-sample D32 depth target the reference
///     renderer replays its opaque instance batches into (depth-only, from the light's view),
///     plus the persistent bindless R32_Float SRV the scene pixel shaders PCF-sample through
///     the shared atmosphere CB.
///     <para>
///         The map is CACHED: it re-renders only when its <see cref="SunShadowMath.ShadowKey" />
///         (quantized sun direction, snapped coverage center, radius, batch content version)
///         changes — a static scene with a static sun records zero shadow work per frame. The
///         pass is recorded at the END of the frame from that frame's just-built batches, so the
///         scene samples the PREVIOUS render's map (one frame of latency on a time-slider drag,
///         invisible in practice, in exchange for replaying the frame's own ring-buffer CB
///         allocations instead of duplicating the batch-build machinery).
///     </para>
///     <para>
///         Resource state ping-pongs between DEPTH_WRITE (render) and PIXEL_SHADER_RESOURCE
///         (the steady state between renders); a freshly created map is cleared-to-far, which
///         the reversed-Z compare reads as "nothing occludes" — fully lit until first render.
///     </para>
/// </summary>
internal sealed class ShadowMapRenderer12 : IDisposable
{
    private readonly GpuDevice12 _gpu;
    private readonly ID3D12Resource _depthTex;
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly CpuDescriptorHandle _dsvHandle;
    private readonly GpuDescriptorHeapAllocator12.PersistentAllocation _srv;
    private bool _inSrvState;
    private SunShadowMath.ShadowKey _renderedKey;
    private Matrix4x4 _renderedViewProj;
    private Vector3 _renderedOrigin;
    private float _renderedNormalizedBias;
    private bool _disposed;

    /// <summary>Shadow map dimension in texels (square). Env override <c>FALLOUT_VIEWER_SHADOW_RES</c>.
    /// The coverage half-extent (the texel-density / reach trade) is the HOST's: the lighting
    /// flyout's "Shadow distance" slider, or <c>FALLOUT_VIEWER_SHADOW_RADIUS</c> headless.</summary>
    public int Resolution { get; }

    /// <summary>True once the map has been rendered at least once (before that the sampling
    /// constants must stay disabled — the cleared map would be "fully lit" anyway, but the CB
    /// flag keeps the PS from paying the PCF cost for nothing).</summary>
    public bool HasContent { get; private set; }

    public ShadowMapRenderer12(GpuDevice12 gpu, GpuDescriptorHeapAllocator12 cbvSrvUavHeap)
    {
        _gpu = gpu;
        Resolution = ParseIntEnv("FALLOUT_VIEWER_SHADOW_RES", defaultValue: 4096, min: 512, max: 8192);

        _depthTex = gpu.Device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(Format.D32_Float, (uint)Resolution, (uint)Resolution,
                arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0,
                ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite,
            new ClearValue(Format.D32_Float, new DepthStencilValue(0.0f, 0))); // reversed-Z far

        _dsvHeap = gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.DepthStencilView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.None,
        });
        _dsvHandle = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        gpu.Device.CreateDepthStencilView(_depthTex, null, _dsvHandle);

        // Persistent bindless slot viewing the depth as R32_Float (same pattern as the water
        // pass's scene-depth SRV). The slot index rides in the atmosphere CB's ShadowParams.w.
        _srv = cbvSrvUavHeap.AllocatePersistent();
        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.R32_Float,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        };
        gpu.Device.CreateShaderResourceView(_depthTex, srvDesc, _srv.Cpu);
    }

    /// <summary>Bindless SRV slot of the shadow map (ShadowParams.w in the atmosphere CB).</summary>
    public uint BindlessIndex => _srv.BindlessIndex;

    /// <summary>Whether the map must be re-rendered for <paramref name="key" />.</summary>
    public bool NeedsRender(in SunShadowMath.ShadowKey key) => !HasContent || key != _renderedKey;

    /// <summary>
    ///     The sampling constants for the CURRENT frame: the rendered map's light matrix with the
    ///     difference between the frame's render origin and the map's render origin folded in
    ///     (see <see cref="SunShadowMath.FoldSampleMatrix" />), plus the packed params float4
    ///     (enabled, texel UV size, normalized depth bias, bindless slot).
    /// </summary>
    public (Matrix4x4 Matrix, Vector4 Params) GetSampleConstants(Vector3 currentOrigin)
    {
        var matrix = SunShadowMath.FoldSampleMatrix(_renderedViewProj, _renderedOrigin, currentOrigin);
        return (matrix, new Vector4(1f, 1f / Resolution, _renderedNormalizedBias, _srv.BindlessIndex));
    }

    /// <summary>
    ///     Transitions the map into DEPTH_WRITE, clears it to the reversed-Z far plane, and binds
    ///     it as the sole (depth-only) render target with a full-map viewport. The caller records
    ///     the depth draws, then calls <see cref="EndRender" />. Anything bound after this pass
    ///     that needs the screen viewport must rebind it (the live frame records this pass last).
    /// </summary>
    public void BeginRender(ID3D12GraphicsCommandList cmd)
    {
        if (_inSrvState)
        {
            cmd.ResourceBarrierTransition(_depthTex,
                ResourceStates.PixelShaderResource, ResourceStates.DepthWrite);
            _inSrvState = false;
        }

        cmd.ClearDepthStencilView(_dsvHandle, ClearFlags.Depth, 0f, 0); // reversed-Z far
        cmd.OMSetRenderTargets(Array.Empty<CpuDescriptorHandle>(), _dsvHandle);
        cmd.RSSetViewport(new Viewport(0, 0, Resolution, Resolution, 0f, 1f));
        cmd.RSSetScissorRect(Resolution, Resolution);
    }

    /// <summary>Transitions the map to PIXEL_SHADER_RESOURCE and records what it now contains so
    /// <see cref="GetSampleConstants" /> / <see cref="NeedsRender" /> reflect the new render.</summary>
    public void EndRender(
        ID3D12GraphicsCommandList cmd, in SunShadowMath.ShadowKey key,
        in SunShadowMath.LightFrustum frustum, Vector3 renderOrigin)
    {
        cmd.ResourceBarrierTransition(_depthTex,
            ResourceStates.DepthWrite, ResourceStates.PixelShaderResource);
        _inSrvState = true;
        _renderedKey = key;
        _renderedViewProj = frustum.ViewProj;
        _renderedOrigin = renderOrigin;
        _renderedNormalizedBias = frustum.NormalizedDepthBias;
        HasContent = true;
    }

    /// <summary>Closes a <see cref="BeginRender" /> whose replay drew NOTHING (ring exhaustion):
    /// transitions back to PIXEL_SHADER_RESOURCE without publishing a key, so the stale sampling
    /// constants stay in effect (the cleared map reads fully lit) and the next frame retries.</summary>
    public void EndRenderEmpty(ID3D12GraphicsCommandList cmd)
    {
        cmd.ResourceBarrierTransition(_depthTex,
            ResourceStates.DepthWrite, ResourceStates.PixelShaderResource);
        _inSrvState = true;
    }

    /// <summary>
    ///     DIAGNOSTICS: records a copy of the whole map into a fresh readback buffer (created here,
    ///     caller disposes) and returns it with its row pitch. Record inside an open command list;
    ///     map only after the submission fence. The map must be in its steady PSR state.
    /// </summary>
    public ID3D12Resource RecordDiagnosticReadback(ID3D12GraphicsCommandList cmd, out uint rowPitch)
    {
        var device = _gpu.Device;
        var copyDesc = ResourceDescription.Texture2D(Format.R32_Float, (uint)Resolution, (uint)Resolution,
            arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0, ResourceFlags.None);
        var footprints = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSize = new ulong[1];
        device.GetCopyableFootprints(copyDesc, 0, 1, 0, footprints, numRows, rowSize, out var totalBytes);
        var readback = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes),
            ResourceStates.CopyDest, optimizedClearValue: null);
        cmd.ResourceBarrierTransition(_depthTex,
            ResourceStates.PixelShaderResource, ResourceStates.CopySource);
        cmd.CopyTextureRegion(
            new TextureCopyLocation(readback, footprints[0]), 0, 0, 0,
            new TextureCopyLocation(_depthTex, 0));
        cmd.ResourceBarrierTransition(_depthTex,
            ResourceStates.CopySource, ResourceStates.PixelShaderResource);
        rowPitch = footprints[0].Footprint.RowPitch;
        return readback;
    }

    private static int ParseIntEnv(string name, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var v) ? Math.Clamp(v, min, max) : defaultValue;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The persistent SRV slot lives in the shared heap; it dies with the heap (same pattern
        // as the live/capture depth SRVs — see DisposeD3D12Backend).
        _dsvHeap.Dispose();
        _depthTex.Dispose();
    }
}
#endif
