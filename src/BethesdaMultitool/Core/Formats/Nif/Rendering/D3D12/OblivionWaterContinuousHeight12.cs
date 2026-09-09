using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Owns a continuous signed FFT height texture. Record before the recovered HMAP prepass in
///     the same direct command list. Upload memory retires on the actual submission fence; the
///     persistent texture and descriptor stay with this owner until an idle/deferred teardown.
/// </summary>
internal sealed class OblivionWaterContinuousHeight12 : IDisposable, IGpuCommandSubmissionParticipant12
{
    private readonly PlacedSubresourceFootPrint _copyFootprint;
    private readonly IDisposable _footprint;
    private readonly GpuDevice12 _gpu;
    private readonly OblivionWaterDisplacementTexture12 _height;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly OblivionWaterSurfaceSynthesizer.ContinuousSurface _surface;
    private readonly ulong _uploadBytes;
    private readonly float[] _values;
    private bool _disposed;
    private Pending? _pending;
    private bool _poisoned;

    private OblivionWaterContinuousHeight12(GpuDevice12 gpu, GpuCommandRecorder12 recorder,
        OblivionWaterSurfaceSynthesizer.ContinuousSurface surface,
        OblivionWaterDisplacementTexture12 height, IDisposable footprint,
        PlacedSubresourceFootPrint copyFootprint, ulong uploadBytes)
    {
        _gpu = gpu;
        _recorder = recorder;
        _surface = surface;
        _height = height;
        _footprint = footprint;
        _copyFootprint = copyFootprint;
        _uploadBytes = uploadBytes;
        _values = new float[surface.Size * surface.Size];
    }

    internal int Size => _surface.Size;
    internal float? LastCommittedElapsedSeconds { get; private set; }
    internal ulong LastCommittedFence { get; private set; }

    /// <summary>Requires GPU idle/device removal or a fence-safe deferred deletion boundary.</summary>
    [SuppressMessage("Major Code Smell", "S3877",
        Justification =
            "An open upload transaction forbids releasing GPU-referenced resources. The owner must finish or abort before teardown.")]
    public void Dispose()
    {
        if (_disposed) return;
        if (_pending is not null)
            throw new InvalidOperationException("Finish or abort the height upload before teardown.");
        _height.Dispose();
        _footprint.Dispose();
        _disposed = true;
    }

    void IGpuCommandSubmissionParticipant12.OnCommandListSubmitted()
    {
        var pending = _pending;
        _pending = null;
        if (pending is null) return;
        if (_recorder.LastSubmittedFenceValue <= pending.PrecedingFence)
        {
            // Execute without a successful Signal can have changed the texture. Retain resources
            // for idle/device teardown and reject both the old and prospective publication.
            _poisoned = true;
            _height.Quarantine();
            LastCommittedElapsedSeconds = null;
            LastCommittedFence = 0;
            return;
        }

        LastCommittedElapsedSeconds = pending.ElapsedSeconds;
        LastCommittedFence = _recorder.LastSubmittedFenceValue;
    }

    void IGpuCommandSubmissionParticipant12.OnCommandListAborted()
    {
        _pending = null;
    }

    internal static OblivionWaterContinuousHeight12 Create(GpuDevice12 gpu, GpuCommandRecorder12 recorder,
        GpuDescriptorHeapAllocator12 heap, OblivionWaterSimulationResource identity,
        WaterSurfaceParams? surface, bool useHighResolution, string sourceReceipt)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(heap);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceReceipt);
        var producer = OblivionWaterSurfaceSynthesizer.CreateContinuousSurface(surface, useHighResolution);
        var description = ResourceDescription.Texture2D(Format.R32_Float,
            (uint)producer.Size, (uint)producer.Size, 1, 1);
        ID3D12Resource? raw = null;
        OblivionWaterDisplacementTexture12? imported = null;
        IDisposable? footprint = null;
        try
        {
            // Initial contents are undefined. Only Record exposes this import, after recording
            // its complete first copy. Every submitted update returns to the same SRV state.
            raw = gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.DefaultHeapProperties,
                HeapFlags.None, description, ResourceStates.PixelShaderResource);
            raw.Name = "TES4 continuous signed FFT height";
            footprint = GpuFixedFootprintTracker12.LocalInstance.Add("tes4-continuous-height",
                (long)gpu.Device.GetResourceAllocationInfo(0, description).SizeInBytes);
            imported = OblivionWaterDisplacementTexture12.ImportRawHeight(gpu, heap, identity,
                raw, ResourceStates.PixelShaderResource, sourceReceipt);
            var layouts = new PlacedSubresourceFootPrint[1];
            gpu.Device.GetCopyableFootprints(description, 0, 1, 0, layouts,
                new uint[1], new ulong[1], out var totalBytes);
            var result = new OblivionWaterContinuousHeight12(gpu, recorder, producer, imported,
                footprint, layouts[0], totalBytes);
            imported = null;
            footprint = null;
            return result;
        }
        finally
        {
            // Import owns its own COM reference. Constructor failure precedes any GPU commands.
            imported?.Dispose();
            raw?.Dispose();
            footprint?.Dispose();
        }
    }

    /// <summary>
    ///     Returns a borrowed, same-command-list input for HMAP005/006. Callers must not dispose
    ///     the import or publish a new clock before submission. An aborted first upload is not a
    ///     valid height; retry Record before any later use. Paused committed times reuse the input.
    /// </summary>
    internal OblivionWaterDisplacementTexture12 Record(float elapsedSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned || _height.Unavailable || _pending is not null)
            throw new InvalidOperationException("The preceding height transaction must finish before another update.");
        _recorder.EnlistCurrentFrame(this); // Reject a closed list even for an unchanged clock.
        if (LastCommittedElapsedSeconds is { } committed && committed.Equals(elapsedSeconds))
            return _height;
        _surface.Evaluate(elapsedSeconds, _values);

        Upload? upload = null;
        var transferred = false;
        try
        {
            upload = CreateUpload();
            _recorder.EnqueueDisposeAfterCurrentFrame(upload);
            transferred = true;
            _pending = new Pending(elapsedSeconds, _recorder.LastSubmittedFenceValue);
            var command = _recorder.CommandList;
            command.ResourceBarrierTransition(_height.Texture,
                ResourceStates.PixelShaderResource, ResourceStates.CopyDest);
            command.CopyTextureRegion(new TextureCopyLocation(_height.Texture), 0, 0, 0,
                new TextureCopyLocation(upload.Resource, _copyFootprint));
            command.ResourceBarrierTransition(_height.Texture,
                ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
            return _height;
        }
        catch
        {
            if (_pending is not null)
            {
                try
                {
                    _recorder.AbortFrame();
                }
                finally
                {
                    _pending = null;
                }
            }

            throw;
        }
        finally
        {
            if (!transferred) upload?.Dispose();
        }
    }

    private Upload CreateUpload()
    {
        ID3D12Resource? staging = null;
        IDisposable? footprint = null;
        try
        {
            staging = _gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.UploadHeapProperties,
                HeapFlags.None, ResourceDescription.Buffer(_uploadBytes), ResourceStates.GenericRead);
            footprint = GpuFixedFootprintTracker12.NonLocalInstance.Add("tes4-continuous-height-upload",
                (long)_gpu.Device.GetResourceAllocationInfo(0, staging.Description).SizeInBytes);
            var mapped = staging.Map<byte>(0, checked((int)_uploadBytes));
            try
            {
                mapped.Clear();
                var bytes = MemoryMarshal.AsBytes(_values.AsSpan());
                for (var row = 0; row < Size; row++)
                    bytes.Slice(row * Size * sizeof(float), Size * sizeof(float)).CopyTo(mapped.Slice(
                        checked((int)_copyFootprint.Offset + row * (int)_copyFootprint.Footprint.RowPitch)));
            }
            finally
            {
                staging.Unmap(0);
            }

            var result = new Upload(staging, footprint);
            staging = null;
            footprint = null;
            return result;
        }
        finally
        {
            staging?.Dispose();
            footprint?.Dispose();
        }
    }

    private sealed record Pending(float ElapsedSeconds, ulong PrecedingFence);

    private sealed record Upload(ID3D12Resource Resource, IDisposable Footprint) : IDisposable
    {
        public void Dispose()
        {
            Resource.Dispose();
            Footprint.Dispose();
        }
    }
}
