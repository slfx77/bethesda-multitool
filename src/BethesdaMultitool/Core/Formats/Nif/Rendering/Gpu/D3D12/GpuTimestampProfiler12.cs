using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Named GPU timestamp query slots marking the start/end of each render pass within a frame.</summary>
internal enum GpuTimestampRegion
{
    FrameStart = 0,
    TerrainStart = 1,
    TerrainEnd = 2,
    ReferencesStart = 3,
    ReferencesEnd = 4,
    WaterStart = 5,
    WaterEnd = 6,
    WireframeStart = 7,
    WireframeEnd = 8,
    FrameEnd = 9
}

/// <summary>Resolved per-pass GPU timings (in milliseconds) for a completed frame.</summary>
internal readonly record struct GpuFrameTimings(
    long FrameNumber,
    double FrameMilliseconds,
    double TerrainMilliseconds,
    double ReferencesMilliseconds,
    double WaterMilliseconds,
    double WireframeMilliseconds);

/// <summary>Converts raw GPU timestamp ticks into elapsed milliseconds using the queue's tick frequency.</summary>
internal static class GpuTimestampMath
{
    internal static double TicksToMilliseconds(ulong start, ulong end, ulong frequency)
    {
        if (frequency == 0 || end <= start)
        {
            return 0;
        }

        return (end - start) * 1000.0 / frequency;
    }
}

/// <summary>
///     Records GPU timestamp queries around each render pass and resolves them into per-frame timings once
///     the frame's fence completes. Backs the renderer's on-screen GPU profiler.
/// </summary>
internal sealed unsafe class GpuTimestampProfiler12 : IDisposable
{
    internal const int QueryCountPerFrame = 10;

    private readonly GpuDevice12 _gpu;
    private readonly ID3D12QueryHeap _queryHeap;
    private readonly ID3D12Resource _readback;
    private readonly PendingFrame[] _pendingFrames = new PendingFrame[GpuCommandRecorder12.FramesInFlight];
    private readonly ulong _timestampFrequency;
    private bool _disposed;
    private int _activeFrameIndex = -1;

    public GpuTimestampProfiler12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        _gpu.DirectQueue.GetTimestampFrequency(out _timestampFrequency).CheckError();
        if (_timestampFrequency == 0)
        {
            throw new InvalidOperationException("D3D12 timestamp frequency is zero.");
        }

        _queryHeap = gpu.Device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription
        {
            Count = QueryCountPerFrame * GpuCommandRecorder12.FramesInFlight,
            Type = QueryHeapType.Timestamp
        });
        _readback = gpu.Device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.ReadbackHeapProperties,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)(sizeof(ulong) * QueryCountPerFrame * GpuCommandRecorder12.FramesInFlight)),
            ResourceStates.CopyDest,
            optimizedClearValue: null);
    }

    public bool IsEnabled => !_disposed;

    /// <summary>Selects the query slot set for the frame about to be recorded.</summary>
    public void BeginFrame(int frameIndex)
    {
        ThrowIfDisposed();
        if ((uint)frameIndex >= GpuCommandRecorder12.FramesInFlight)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        _activeFrameIndex = frameIndex;
    }

    /// <summary>Returns timings for the oldest frame whose fence has completed, if any are ready.</summary>
    public bool TryCollectCompleted(out GpuFrameTimings timings)
    {
        ThrowIfDisposed();
        for (var i = 0; i < _pendingFrames.Length; i++)
        {
            var pending = _pendingFrames[i];
            if (!pending.HasValue || pending.FenceValue == 0 || _gpu.FrameFence.CompletedValue < pending.FenceValue)
            {
                continue;
            }

            timings = ReadFrame(i, pending.FrameNumber);
            _pendingFrames[i] = default;
            return true;
        }

        timings = default;
        return false;
    }

    /// <summary>Records a timestamp query for the given region into the active frame's command list.</summary>
    public void Write(ID3D12GraphicsCommandList commandList, GpuTimestampRegion region)
    {
        ThrowIfDisposed();
        if (_activeFrameIndex < 0)
        {
            return;
        }

        commandList.EndQuery(_queryHeap, QueryType.Timestamp, QueryIndex(_activeFrameIndex, region));
    }

    /// <summary>Copies the active frame's resolved timestamp queries into the readback buffer.</summary>
    public void ResolveActiveFrame(ID3D12GraphicsCommandList commandList)
    {
        ThrowIfDisposed();
        if (_activeFrameIndex < 0)
        {
            return;
        }

        var firstQuery = _activeFrameIndex * QueryCountPerFrame;
        var destinationOffset = (ulong)(firstQuery * sizeof(ulong));
        commandList.ResolveQueryData(
            _queryHeap,
            QueryType.Timestamp,
            (uint)firstQuery,
            QueryCountPerFrame,
            _readback,
            destinationOffset);
    }

    /// <summary>Records the frame number and fence value for the active frame so its timings can be collected once the fence signals.</summary>
    public void MarkActiveFrameSubmitted(long frameNumber, ulong fenceValue)
    {
        ThrowIfDisposed();
        if (_activeFrameIndex < 0)
        {
            return;
        }

        _pendingFrames[_activeFrameIndex] = new PendingFrame(frameNumber, fenceValue);
        _activeFrameIndex = -1;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _readback.Dispose();
        _queryHeap.Dispose();
    }

    private GpuFrameTimings ReadFrame(int frameIndex, long frameNumber)
    {
        var baseQuery = frameIndex * QueryCountPerFrame;
        var readRange = new Vortice.Direct3D12.Range(
            (nuint)(baseQuery * sizeof(ulong)),
            (nuint)((baseQuery + QueryCountPerFrame) * sizeof(ulong)));
        void* data = null;
        _readback.Map(0, readRange, &data).CheckError();
        try
        {
            var values = (ulong*)data + baseQuery;
            return new GpuFrameTimings(
                frameNumber,
                GpuTimestampMath.TicksToMilliseconds(
                    values[(int)GpuTimestampRegion.FrameStart],
                    values[(int)GpuTimestampRegion.FrameEnd],
                    _timestampFrequency),
                GpuTimestampMath.TicksToMilliseconds(
                    values[(int)GpuTimestampRegion.TerrainStart],
                    values[(int)GpuTimestampRegion.TerrainEnd],
                    _timestampFrequency),
                GpuTimestampMath.TicksToMilliseconds(
                    values[(int)GpuTimestampRegion.ReferencesStart],
                    values[(int)GpuTimestampRegion.ReferencesEnd],
                    _timestampFrequency),
                GpuTimestampMath.TicksToMilliseconds(
                    values[(int)GpuTimestampRegion.WaterStart],
                    values[(int)GpuTimestampRegion.WaterEnd],
                    _timestampFrequency),
                GpuTimestampMath.TicksToMilliseconds(
                    values[(int)GpuTimestampRegion.WireframeStart],
                    values[(int)GpuTimestampRegion.WireframeEnd],
                    _timestampFrequency));
        }
        finally
        {
            _readback.Unmap(0, new Vortice.Direct3D12.Range(0, 0));
        }
    }

    internal static uint QueryIndex(int frameIndex, GpuTimestampRegion region) =>
        checked((uint)(frameIndex * QueryCountPerFrame + (int)region));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct PendingFrame(long FrameNumber, ulong FenceValue)
    {
        public bool HasValue => FenceValue != 0;
    }
}
