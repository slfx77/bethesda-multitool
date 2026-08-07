#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Owns the CASCADED directional sun-shadow maps: <see cref="CascadeCount" /> single-sample
///     D32 targets the reference batches + terrain cells are replayed into depth-only from the
///     light's view — a tight near cascade for sharp close-to-camera shadows, a mid cascade, and a
///     far cascade covering the full render distance, so quality scales with closeness to the
///     camera instead of degrading uniformly with coverage. The scene pixel shaders pick the
///     SMALLEST cascade containing the sample (ShadowFactor in reference/terrain frag) and PCF
///     through the per-cascade bindless R32_Float SRVs carried in the shared atmosphere CB.
///     <para>
///         The cascade set is CACHED under one <see cref="SunShadowMath.ShadowKey" /> (quantized
///         sun direction, snapped coverage center, far radius, content version): a static scene
///         with a static sun records zero shadow work per frame; wind-animated foliage in view
///         re-renders every frame (the host folds a tick into the key). The pass records at the
///         END of the frame from that frame's just-built batches, so the scene samples the
///         PREVIOUS render (one frame of latency, invisible under the cache).
///     </para>
///     <para>
///         Each cascade's resource state ping-pongs between DEPTH_WRITE (render) and
///         PIXEL_SHADER_RESOURCE (steady state between renders); a freshly created map is
///         cleared-to-far, which the reversed-Z compare reads as "nothing occludes".
///     </para>
/// </summary>
internal sealed class ShadowMapRenderer12 : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    /// <summary>Number of cascades (near → far). Radii ladder is the host's (RecordSunShadowPass)
    /// and is FIXED — deliberately decoupled from the render-distance setting, which must only
    /// bound where geometry (and therefore any shadow) exists at all.</summary>
    public const int CascadeCount = 4;

    private readonly GpuDevice12 _gpu;
    private readonly ID3D12Resource[] _depthTex = new ID3D12Resource[CascadeCount];
    private readonly ID3D12DescriptorHeap _dsvHeap;
    private readonly CpuDescriptorHandle[] _dsvHandles = new CpuDescriptorHandle[CascadeCount];
    private readonly GpuDescriptorHeapAllocator12.PersistentAllocation[] _srvs =
        new GpuDescriptorHeapAllocator12.PersistentAllocation[CascadeCount];
    private readonly bool[] _inSrvState = new bool[CascadeCount];
    private readonly Matrix4x4[] _renderedViewProj = new Matrix4x4[CascadeCount];
    private readonly Vector4[] _renderedParams = new Vector4[CascadeCount];

    // Render origin PER CASCADE. Cascades re-render on independent schedules (each has its own
    // drift quantum, scaled to what its texels can resolve), so at any moment they can have been
    // rendered against different camera-relative origins. A single shared origin would fold the
    // wrong translation into the older cascades' sampling matrices and slide their shadows.
    private readonly Vector3[] _renderedOrigins = new Vector3[CascadeCount];
    private readonly bool[] _cascadePublished = new bool[CascadeCount];
    private bool _disposed;

    /// <summary>Per-cascade map dimension in texels (square). Defaults are VRAM-SCALED from the
    /// OS-assigned local memory budget (see <see cref="ResolveResolution" />);
    /// <c>FALLOUT_VIEWER_SHADOW_RES</c> overrides explicitly.</summary>
    public int Resolution { get; }

    /// <summary>True once the cascades have been rendered at least once (before that the sampling
    /// constants must stay disabled). Individual cascades can still be disabled when a refresh
    /// submitted no casters, allowing the shader to fall through to a populated farther map.</summary>
    public bool HasContent { get; private set; }

    public ShadowMapRenderer12(GpuDevice12 gpu, GpuDescriptorHeapAllocator12 cbvSrvUavHeap)
    {
        _gpu = gpu;
        Resolution = ResolveResolution(gpu);

        _dsvHeap = gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.DepthStencilView,
            DescriptorCount = CascadeCount,
            Flags = DescriptorHeapFlags.None,
        });
        var dsvStride = gpu.Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        var dsvStart = _dsvHeap.GetCPUDescriptorHandleForHeapStart();

        for (var i = 0; i < CascadeCount; i++)
        {
            _depthTex[i] = gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties, HeapFlags.None,
                ResourceDescription.Texture2D(Format.D32_Float, (uint)Resolution, (uint)Resolution,
                    arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0,
                    ResourceFlags.AllowDepthStencil),
                ResourceStates.DepthWrite,
                new ClearValue(Format.D32_Float, new DepthStencilValue(0.0f, 0))); // reversed-Z far

            _dsvHandles[i] = new CpuDescriptorHandle { Ptr = dsvStart.Ptr + (nuint)(i * dsvStride) };
            gpu.Device.CreateDepthStencilView(_depthTex[i], null, _dsvHandles[i]);

            // Persistent bindless slot viewing the cascade depth as R32_Float (same pattern as the
            // water pass's scene-depth SRV). The slot index rides in the cascade's ShadowParams.w.
            _srvs[i] = cbvSrvUavHeap.AllocatePersistent();
            var srvDesc = new ShaderResourceViewDescription
            {
                Format = Format.R32_Float,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
            };
            gpu.Device.CreateShaderResourceView(_depthTex[i], srvDesc, _srvs[i].Cpu);
        }
    }

    /// <summary>
    ///     Per-cascade sampling constants for the CURRENT frame: each rendered cascade's light
    ///     matrix with the render-origin delta folded in (<see cref="SunShadowMath.FoldSampleMatrix" />)
    ///     plus its packed params (enabled, texel UV size, normalized depth bias, bindless slot).
    ///     All-disabled until the first render publishes content.
    /// </summary>
    public ShadowSampleConstants GetSampleConstants(Vector3 currentOrigin)
    {
        if (!HasContent)
        {
            return default;
        }

        return new ShadowSampleConstants
        {
            Matrix0 = SunShadowMath.FoldSampleMatrix(_renderedViewProj[0], _renderedOrigins[0], currentOrigin),
            Matrix1 = SunShadowMath.FoldSampleMatrix(_renderedViewProj[1], _renderedOrigins[1], currentOrigin),
            Matrix2 = SunShadowMath.FoldSampleMatrix(_renderedViewProj[2], _renderedOrigins[2], currentOrigin),
            Matrix3 = SunShadowMath.FoldSampleMatrix(_renderedViewProj[3], _renderedOrigins[3], currentOrigin),
            Params0 = _renderedParams[0],
            Params1 = _renderedParams[1],
            Params2 = _renderedParams[2],
            Params3 = _renderedParams[3],
        };
    }

    /// <summary>CPU mirror of the atmosphere CB's shadow tail (4 matrices + 4 params float4s).</summary>
    public struct ShadowSampleConstants
    {
        public Matrix4x4 Matrix0;
        public Matrix4x4 Matrix1;
        public Matrix4x4 Matrix2;
        public Matrix4x4 Matrix3;
        public Vector4 Params0;
        public Vector4 Params1;
        public Vector4 Params2;
        public Vector4 Params3;
    }

    /// <summary>
    ///     Transitions cascade <paramref name="index" /> into DEPTH_WRITE, clears it to the
    ///     reversed-Z far plane, and binds it as the sole (depth-only) render target with a
    ///     full-map viewport. The caller records the depth draws, then calls
    ///     <see cref="EndCascade" />; after all cascades, <see cref="Publish" /> commits the key.
    /// </summary>
    public void BeginCascade(ID3D12GraphicsCommandList cmd, int index)
    {
        if (_inSrvState[index])
        {
            cmd.ResourceBarrierTransition(_depthTex[index],
                ResourceStates.PixelShaderResource, ResourceStates.DepthWrite);
            _inSrvState[index] = false;
        }

        cmd.ClearDepthStencilView(_dsvHandles[index], ClearFlags.Depth, 0f, 0); // reversed-Z far
        cmd.OMSetRenderTargets(Array.Empty<CpuDescriptorHandle>(), _dsvHandles[index]);
        cmd.RSSetViewport(new Viewport(0, 0, Resolution, Resolution, 0f, 1f));
        cmd.RSSetScissorRect(Resolution, Resolution);
    }

    /// <summary>Transitions cascade <paramref name="index" /> back to PIXEL_SHADER_RESOURCE.</summary>
    public void EndCascade(ID3D12GraphicsCommandList cmd, int index)
    {
        cmd.ResourceBarrierTransition(_depthTex[index],
            ResourceStates.DepthWrite, ResourceStates.PixelShaderResource);
        _inSrvState[index] = true;
    }

    /// <summary>
    ///     Commits a completed render of all cascades: records what the maps now contain so
    ///     <see cref="GetSampleConstants" /> reflects it. The re-render POLICY (pose keys, content
    ///     throttling, animation ticks) is the host's — see RecordSunShadowPass. A cascade whose
    ///     <paramref name="cascadeHasDraws" /> entry is false is published disabled, so the scene
    ///     shaders try the next containing cascade instead of sampling its cleared map as fully lit.
    /// </summary>
    public void Publish(
        ReadOnlySpan<SunShadowMath.LightFrustum> frustums,
        Vector3 renderOrigin,
        ReadOnlySpan<bool> cascadeHasDraws)
    {
        if (frustums.Length < CascadeCount)
            throw new ArgumentException($"Expected {CascadeCount} cascade frustums.", nameof(frustums));
        if (cascadeHasDraws.Length < CascadeCount)
            throw new ArgumentException(
                $"Expected {CascadeCount} cascade availability values.", nameof(cascadeHasDraws));

        for (var i = 0; i < CascadeCount; i++)
        {
            _renderedViewProj[i] = frustums[i].ViewProj;
            _renderedParams[i] = new Vector4(
                cascadeHasDraws[i] ? 1f : 0f,
                1f / Resolution,
                frustums[i].NormalizedDepthBias,
                _srvs[i].BindlessIndex);
            _renderedOrigins[i] = renderOrigin;
            _cascadePublished[i] = true;
        }

        HasContent = true;
    }

    /// <summary>
    ///     Commits a render of ONE cascade. The per-cascade twin of <see cref="Publish" />, used by the
    ///     staggered re-render cadence: each cascade has its own drift quantum (scaled to the world
    ///     size of its texels) so they come due independently, and rendering them together purely to
    ///     publish them together is exactly the cost that cadence exists to avoid.
    ///     <para>
    ///         A cascade that has never published keeps <c>Params.x = 0</c> and the scene shaders fall
    ///         through to the next containing cascade, so a partially-published ladder is always safe
    ///         to sample.
    ///     </para>
    /// </summary>
    public void PublishCascade(
        int index, in SunShadowMath.LightFrustum frustum, Vector3 renderOrigin, bool hasDraws)
    {
        if ((uint)index >= CascadeCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        _renderedViewProj[index] = frustum.ViewProj;
        _renderedParams[index] = new Vector4(
            hasDraws ? 1f : 0f,
            1f / Resolution,
            frustum.NormalizedDepthBias,
            _srvs[index].BindlessIndex);
        _renderedOrigins[index] = renderOrigin;
        _cascadePublished[index] = true;
        HasContent = true;
    }

    /// <summary>True once cascade <paramref name="index" /> has been rendered and published at least
    /// once. Before that its sampling constants are disabled and must not be replayed.</summary>
    public bool IsCascadePublished(int index) =>
        (uint)index < CascadeCount && _cascadePublished[index];

    /// <summary>
    ///     Updates the availability bit after an in-place animated refresh of one published
    ///     cascade. Its matrix and descriptor remain unchanged; an empty refresh disables only
    ///     this map until a later successful refresh repopulates it.
    /// </summary>
    public void SetCascadeAvailability(int index, bool hasDraws)
    {
        if ((uint)index >= CascadeCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        _renderedParams[index].X = hasDraws ? 1f : 0f;
    }

    /// <summary>
    ///     DIAGNOSTICS: records a copy of one cascade into a fresh readback buffer (created here,
    ///     caller disposes) and returns it with its row pitch. Record inside an open command list;
    ///     map only after the submission fence. The cascade must be in its steady PSR state.
    /// </summary>
    public ID3D12Resource RecordDiagnosticReadback(ID3D12GraphicsCommandList cmd, int index, out uint rowPitch)
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
        cmd.ResourceBarrierTransition(_depthTex[index],
            ResourceStates.PixelShaderResource, ResourceStates.CopySource);
        cmd.CopyTextureRegion(
            new TextureCopyLocation(readback, footprints[0]), 0, 0, 0,
            new TextureCopyLocation(_depthTex[index], 0));
        cmd.ResourceBarrierTransition(_depthTex[index],
            ResourceStates.CopySource, ResourceStates.PixelShaderResource);
        rowPitch = footprints[0].Footprint.RowPitch;
        return readback;
    }

    /// <summary>
    ///     Picks the per-cascade resolution: an explicit <c>FALLOUT_VIEWER_SHADOW_RES</c> wins;
    ///     otherwise the default SCALES WITH THE ADAPTER'S VRAM (the OS-assigned local budget, so
    ///     other apps' usage is already accounted for). Total depth-target cost is
    ///     <see cref="CascadeCount" /> × res² × 4 bytes: 4096 ⇒ 256 MB (fine at a ≥ 8 GB budget,
    ///     ~3%), 2048 ⇒ 64 MB, 1024 ⇒ 16 MB. A failed query (no IDXGIAdapter3 — WARP / ancient
    ///     drivers) falls back to the conservative middle tier.
    /// </summary>
    private static int ResolveResolution(GpuDevice12 gpu)
    {
        var raw = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_SHADOW_RES");
        if (int.TryParse(raw, out var explicitRes))
        {
            return Math.Clamp(explicitRes, 512, 8192);
        }

        if (!gpu.TryQueryLocalVideoMemoryMb(out _, out var budgetMb) || budgetMb <= 0)
        {
            return 2048;
        }

        var resolution = budgetMb switch
        {
            >= 8192 => 4096,
            >= 3072 => 2048,
            _ => 1024,
        };
        Log.Info("[Shadow] cascade resolution {0}² × {1} ({2} MB) from VRAM budget {3} MB",
            resolution, CascadeCount,
            (long)resolution * resolution * 4 * CascadeCount / (1024 * 1024), budgetMb);
        return resolution;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The persistent SRV slots live in the shared heap; they die with the heap (same pattern
        // as the live/capture depth SRVs — see DisposeD3D12Backend).
        _dsvHeap.Dispose();
        foreach (var tex in _depthTex)
        {
            tex.Dispose();
        }
    }
}
#endif
