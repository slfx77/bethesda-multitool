using Vortice.Direct3D12;
using Range = Vortice.Direct3D12.Range;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Pipeline activity recorded by the primary reference pass for one submitted frame.
///     The frame number is the CPU/profile identity of the command list that produced the
///     counters, not the later frame on which its readback happened.
/// </summary>
internal readonly record struct GpuReferencePipelineStatisticsSample(
    long FrameNumber,
    ulong IAPrimitives,
    ulong VSInvocations,
    ulong PSInvocations);

/// <summary>
///     Explicitly opt-in D3D12 pipeline-statistics query around the primary reference pass.
///     One query/readback slot follows each command-recorder frame slot, and CPU access is
///     permitted only after the submission fence completes. No method waits for the GPU.
/// </summary>
internal sealed unsafe class GpuReferencePipelineStatistics12 : IDisposable
{
    internal const int QueryCountPerFrame = 1;
    internal static int ResultSizeInBytes => sizeof(QueryDataPipelineStatistics);

    private readonly GpuDevice12 _gpu;
    private readonly PendingFrame[] _pendingFrames = new PendingFrame[GpuCommandRecorder12.FramesInFlight];
    private readonly ID3D12QueryHeap _queryHeap;
    private readonly ID3D12Resource _readback;
    private int _activeFrameIndex = -1;
    private bool _queryOpen;
    private bool _queryCompleted;
    private bool _queryResolved;
    private bool _disposed;

    public GpuReferencePipelineStatistics12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        var queryHeap = gpu.Device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription
        {
            Count = QueryCountPerFrame * GpuCommandRecorder12.FramesInFlight,
            Type = QueryHeapType.PipelineStatistics
        });
        try
        {
            _readback = gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.ReadbackHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer(
                    (ulong)(ResultSizeInBytes * QueryCountPerFrame * GpuCommandRecorder12.FramesInFlight)),
                ResourceStates.CopyDest);
        }
        catch
        {
            // Construction never publishes this object to the control, so its outer failure path
            // cannot call Dispose. Retire the first allocation here if the second one fails.
            queryHeap.Dispose();
            throw;
        }

        _queryHeap = queryHeap;
    }

    public bool IsEnabled => !_disposed;

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

    /// <summary>Selects and clears the query state for the frame about to be recorded.</summary>
    public void BeginFrame(int frameIndex)
    {
        ThrowIfDisposed();
        if ((uint)frameIndex >= GpuCommandRecorder12.FramesInFlight)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        _activeFrameIndex = frameIndex;
        _queryOpen = false;
        _queryCompleted = false;
        _queryResolved = false;
    }

    /// <summary>Starts the primary-reference pipeline-statistics interval.</summary>
    public void BeginReferencePass(ID3D12GraphicsCommandList commandList)
    {
        ThrowIfDisposed();
        if (_activeFrameIndex < 0)
        {
            return;
        }

        if (_queryOpen || _queryCompleted)
        {
            throw new InvalidOperationException("Reference pipeline-statistics query was begun twice.");
        }

        commandList.BeginQuery(
            _queryHeap,
            QueryType.PipelineStatistics,
            QueryIndex(_activeFrameIndex));
        _queryOpen = true;
    }

    /// <summary>Ends the primary-reference pipeline-statistics interval.</summary>
    public void EndReferencePass(ID3D12GraphicsCommandList commandList)
    {
        ThrowIfDisposed();
        if (_activeFrameIndex < 0)
        {
            return;
        }

        if (!_queryOpen)
        {
            throw new InvalidOperationException("Reference pipeline-statistics query ended without a begin.");
        }

        commandList.EndQuery(
            _queryHeap,
            QueryType.PipelineStatistics,
            QueryIndex(_activeFrameIndex));
        _queryOpen = false;
        _queryCompleted = true;
    }

    /// <summary>
    ///     Resolves the completed active query into its frame-slot readback range. An incomplete
    ///     query is deliberately skipped: D3D12 defines resolving incomplete/uninitialized queries
    ///     as undefined behavior, so absence is safer and makes a strict capture fail closed.
    /// </summary>
    public void ResolveActiveFrame(ID3D12GraphicsCommandList commandList)
    {
        ThrowIfDisposed();
        if (_activeFrameIndex < 0 || _queryOpen || !_queryCompleted)
        {
            return;
        }

        var queryIndex = QueryIndex(_activeFrameIndex);
        commandList.ResolveQueryData(
            _queryHeap,
            QueryType.PipelineStatistics,
            queryIndex,
            QueryCountPerFrame,
            _readback,
            ReadbackOffset(_activeFrameIndex));
        _queryResolved = true;
    }

    /// <summary>
    ///     Associates the resolved slot with the fence signaled for its submission. Frames whose
    ///     query did not complete and resolve are intentionally omitted rather than publishing zeros.
    /// </summary>
    public void MarkActiveFrameSubmitted(long frameNumber, ulong fenceValue)
    {
        ThrowIfDisposed();
        if (_activeFrameIndex >= 0 && _queryResolved && fenceValue != 0)
        {
            _pendingFrames[_activeFrameIndex] = new PendingFrame(frameNumber, fenceValue);
        }

        _activeFrameIndex = -1;
        _queryOpen = false;
        _queryCompleted = false;
        _queryResolved = false;
    }

    /// <summary>
    ///     Reads the oldest submitted query whose fence is complete. This method only polls the
    ///     completed fence value; it never asks the CPU to wait for the GPU.
    /// </summary>
    public bool TryCollectCompleted(out GpuReferencePipelineStatisticsSample sample)
    {
        ThrowIfDisposed();
        var completedFence = _gpu.FrameFence.CompletedValue;
        var readyIndex = -1;
        var readyFrameNumber = long.MaxValue;
        for (var i = 0; i < _pendingFrames.Length; i++)
        {
            var pending = _pendingFrames[i];
            if (!pending.HasValue || pending.FenceValue > completedFence ||
                pending.FrameNumber >= readyFrameNumber)
            {
                continue;
            }

            readyIndex = i;
            readyFrameNumber = pending.FrameNumber;
        }

        if (readyIndex < 0)
        {
            sample = default;
            return false;
        }

        sample = ReadFrame(readyIndex, readyFrameNumber);
        _pendingFrames[readyIndex] = default;
        return true;
    }

    internal static uint QueryIndex(int frameIndex)
    {
        return checked((uint)(frameIndex * QueryCountPerFrame));
    }

    internal static ulong ReadbackOffset(int frameIndex)
    {
        return checked((ulong)(frameIndex * QueryCountPerFrame * ResultSizeInBytes));
    }

    private GpuReferencePipelineStatisticsSample ReadFrame(int frameIndex, long frameNumber)
    {
        var offset = ReadbackOffset(frameIndex);
        var readRange = new Range(
            checked((nuint)offset),
            checked((nuint)(offset + (ulong)ResultSizeInBytes)));
        void* data = null;
        _readback.Map(0, readRange, &data).CheckError();
        try
        {
            var statistics = *(QueryDataPipelineStatistics*)((byte*)data + offset);
            return new GpuReferencePipelineStatisticsSample(
                frameNumber,
                statistics.IAPrimitives,
                statistics.VSInvocations,
                statistics.PSInvocations);
        }
        finally
        {
            _readback.Unmap(0, new Range(0, 0));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct PendingFrame(long FrameNumber, ulong FenceValue)
    {
        public bool HasValue => FenceValue != 0;
    }
}
