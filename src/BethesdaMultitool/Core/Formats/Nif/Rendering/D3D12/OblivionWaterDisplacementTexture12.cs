using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

internal enum OblivionWaterDisplacementTextureRole
{
    CallerHeight,
    ScratchHeight,
    MixedHeight,
    FftIntermediate,
    OrdinaryNormal,
    WadingNormal,
    RawFftHeight
}

/// <summary>
///     Owns a real one-level GPU texture, RTV and persistent SRV. Public retirement is delayed;
///     immediate disposal requires an idle/device-removal boundary. Raw FFT imports AddRef their
///     actual R32_FLOAT resource; this class never synthesizes height from a normal texture.
/// </summary>
internal sealed class OblivionWaterDisplacementTexture12 : IDisposable
{
    private readonly IDisposable _footprint;
    private readonly GpuDescriptorHeapAllocator12 _heap;
    private readonly ID3D12DescriptorHeap? _rtvHeap;
    private int _recordingLeases;
    private bool _released;

    private OblivionWaterDisplacementTexture12(GpuDevice12 gpu, GpuDescriptorHeapAllocator12 heap,
        OblivionWaterSimulationResource identity, OblivionWaterDisplacementTextureRole role,
        ID3D12Resource texture, ID3D12DescriptorHeap? rtvHeap,
        GpuDescriptorHeapAllocator12.PersistentAllocation srv, ResourceStates state, string? rawProvenance)
    {
        _heap = heap;
        _rtvHeap = rtvHeap;
        Identity = identity;
        Role = role;
        Texture = texture;
        Srv = srv;
        State = state;
        Initialized = role == OblivionWaterDisplacementTextureRole.RawFftHeight;
        RawHeightProvenance = rawProvenance;
        _footprint = GpuFixedFootprintTracker12.LocalInstance.Add("tes4-displacement-replay",
            role == OblivionWaterDisplacementTextureRole.RawFftHeight
                ? 0
                : (long)gpu.Device.GetResourceAllocationInfo(0, texture.Description).SizeInBytes);
    }

    internal OblivionWaterSimulationResource Identity { get; }
    internal OblivionWaterDisplacementTextureRole Role { get; }
    internal ID3D12Resource Texture { get; }
    internal GpuDescriptorHeapAllocator12.PersistentAllocation Srv { get; }

    internal CpuDescriptorHandle Rtv => _rtvHeap?.GetCPUDescriptorHandleForHeapStart() ??
                                        throw new InvalidOperationException("Raw FFT input has no render-target view.");

    internal Format Format => Texture.Description.Format;
    internal uint Size => (uint)Texture.Description.Width;
    internal ResourceStates State { get; private set; }
    internal bool Initialized { get; private set; }
    internal bool Unavailable { get; private set; }
    internal bool Quarantined { get; private set; }
    internal string? RawHeightProvenance { get; }

    /// <summary>Caller must have completed GPU idle/device removal, or be the deferred deletion queue.</summary>
    [SuppressMessage("Major Code Smell", "S3877",
        Justification =
            "An open recording lease forbids releasing GPU-referenced resources. The owner must finish or abort its transaction before disposal.")]
    public void Dispose()
    {
        if (_released) return;
        if (_recordingLeases != 0) throw new InvalidOperationException("Abort or finish recording before disposal.");
        _released = true;
        Unavailable = true;
        _heap.FreePersistent(Srv.BindlessIndex);
        _rtvHeap?.Dispose();
        Texture.Dispose();
        _footprint.Dispose();
    }

    internal static OblivionWaterDisplacementTexture12 Create(GpuDevice12 gpu,
        GpuDescriptorHeapAllocator12 heap, OblivionWaterSimulationResource identity,
        OblivionWaterDisplacementTextureRole role, uint size = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(identity.Value);
        var normal = role is OblivionWaterDisplacementTextureRole.OrdinaryNormal
            or OblivionWaterDisplacementTextureRole.WadingNormal;
        var expectedSize = role == OblivionWaterDisplacementTextureRole.FftIntermediate ? 128u : 256u;
        if (!Enum.IsDefined(role) || role == OblivionWaterDisplacementTextureRole.RawFftHeight ||
            (size != expectedSize && !(role == OblivionWaterDisplacementTextureRole.OrdinaryNormal && size == 128)))
            throw new ArgumentException("Resource role/size is outside the recovered target cohort.", nameof(role));
        var format = normal ? Format.R8G8B8A8_UNorm : Format.R16G16B16A16_UNorm;
        RequireFormat(gpu.Device, format, true);
        ID3D12Resource? texture = null;
        ID3D12DescriptorHeap? rtv = null;
        GpuDescriptorHeapAllocator12.PersistentAllocation? srv = null;
        try
        {
            texture = gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.DefaultHeapProperties,
                HeapFlags.None,
                ResourceDescription.Texture2D(format, size, size, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
                ResourceStates.RenderTarget);
            texture.Name = $"TES4 recorded displacement {identity.Value}/{role}";
            rtv = gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
            {
                Type = DescriptorHeapType.RenderTargetView, DescriptorCount = 1, Flags = DescriptorHeapFlags.None
            });
            gpu.Device.CreateRenderTargetView(texture, null, rtv.GetCPUDescriptorHandleForHeapStart());
            srv = heap.AllocatePersistent();
            gpu.Device.CreateShaderResourceView(texture, GpuTextureFormatHelpers12.MakeSrvDesc(1, format),
                srv.Value.Cpu);
            return new OblivionWaterDisplacementTexture12(gpu, heap, identity, role, texture, rtv, srv.Value,
                ResourceStates.RenderTarget, null);
        }
        catch
        {
            if (srv is { } allocated) heap.FreePersistent(allocated.BindlessIndex);
            rtv?.Dispose();
            texture?.Dispose();
            throw;
        }
    }

    internal static OblivionWaterDisplacementTexture12 ImportRawHeight(GpuDevice12 gpu,
        GpuDescriptorHeapAllocator12 heap, OblivionWaterSimulationResource identity,
        ID3D12Resource rawHeight, ResourceStates recordedState, string provenance)
    {
        ArgumentNullException.ThrowIfNull(rawHeight);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(identity.Value);
        var description = rawHeight.Description;
        if (description.Dimension != ResourceDimension.Texture2D || description.Format != Format.R32_Float ||
            description.Width is not (128 or 256) || description.Height != description.Width ||
            description.DepthOrArraySize != 1 || description.MipLevels != 1 || description.SampleDescription.Count != 1)
            throw new ArgumentException("Raw height must be an explicit 128/256-square one-level R32_FLOAT texture.",
                nameof(rawHeight));
        RequireFormat(gpu.Device, Format.R32_Float, false);
        var retained = rawHeight.QueryInterface<ID3D12Resource>();
        GpuDescriptorHeapAllocator12.PersistentAllocation? srv = null;
        try
        {
            srv = heap.AllocatePersistent();
            gpu.Device.CreateShaderResourceView(retained, GpuTextureFormatHelpers12.MakeSrvDesc(1, Format.R32_Float),
                srv.Value.Cpu);
            return new OblivionWaterDisplacementTexture12(gpu, heap, identity,
                OblivionWaterDisplacementTextureRole.RawFftHeight,
                retained, null, srv.Value, recordedState, provenance);
        }
        catch
        {
            if (srv is { } allocated) heap.FreePersistent(allocated.BindlessIndex);
            retained.Dispose();
            throw;
        }
    }

    private static void RequireFormat(ID3D12Device device, Format format, bool renderTarget)
    {
        if (!device.CheckFormatSupport(format, out var support, out _))
        {
            throw new NotSupportedException($"Unable to query displacement format support for {format}.");
        }

        var required = FormatSupport1.Texture2D | FormatSupport1.ShaderSample;
        if (renderTarget) required |= FormatSupport1.RenderTarget;
        if ((support & required) != required)
            throw new NotSupportedException($"The recorded displacement cohort requires {format}: {required}.");
        // FP16 fallback is an explicit later readback cohort, never an automatic precision change.
    }

    internal void Commit(ResourceStates state, bool initialized)
    {
        State = state;
        Initialized = initialized;
    }

    internal void Quarantine()
    {
        Quarantined = true;
        Unavailable = true;
    }

    internal void AcquireRecording()
    {
        if (Unavailable || _recordingLeases != 0)
            throw new InvalidOperationException(
                "Unavailable or already recorded texture cannot join another transaction.");
        _recordingLeases++;
    }

    internal void ReleaseRecording()
    {
        _recordingLeases--;
    }

    internal void Retire(GpuDeletionQueue12 queue)
    {
        if (Unavailable || _recordingLeases != 0)
            throw new InvalidOperationException(
                "Recorded, retired or quarantined texture needs its owner transaction/teardown.");
        Unavailable = true;
        queue.EnqueueDispose(this); // The heap slot and native resource return at the same safe point.
    }
}
