using Vortice.Direct3D12;
using Range = Vortice.Direct3D12.Range;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Named GPU timestamp query slots marking the start/end of each render pass within a frame.
///     <para>
///         Slots 0-9 are the original set and their VALUES are load-bearing: they are the historical
///         wire format of the readback buffer. Append new regions; never renumber these.
///     </para>
///     <para>
///         Everything from <see cref="SkyStart" /> onward was added to close the profiler's blind spot:
///         a measured FNV exterior frame spent 42.98 ms of a 65.84 ms GPU frame outside every
///         instrumented region, and the aggregate reported no residual, so the cost was invisible.
///         The sun-shadow pass is the bulk of it, hence the per-cascade slots.
///     </para>
/// </summary>
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
    FrameEnd = 9,
    SkyStart = 10,
    SkyEnd = 11,
    BlendedStart = 12,
    BlendedEnd = 13,
    ShadowStart = 14,
    ShadowEnd = 15,

    // Per-cascade: Start -> Refs brackets the reference replay, Refs -> End the terrain gather.
    // Kept contiguous in groups of three so the render loop can index them arithmetically.
    ShadowCascade0Start = 16,
    ShadowCascade0Refs = 17,
    ShadowCascade0End = 18,
    ShadowCascade1Start = 19,
    ShadowCascade1Refs = 20,
    ShadowCascade1End = 21,
    ShadowCascade2Start = 22,
    ShadowCascade2Refs = 23,
    ShadowCascade2End = 24,
    ShadowCascade3Start = 25,
    ShadowCascade3Refs = 26,
    ShadowCascade3End = 27,

    ResolveStart = 28,
    ResolveEnd = 29
}

/// <summary>Reference-replay and terrain-gather GPU time for one shadow cascade.</summary>
internal readonly record struct GpuShadowCascadeTimings(
    double ReferencesMilliseconds,
    double TerrainMilliseconds)
{
    public double TotalMilliseconds => ReferencesMilliseconds + TerrainMilliseconds;
}

/// <summary>Resolved per-pass GPU timings (in milliseconds) for a completed frame.</summary>
internal readonly record struct GpuFrameTimings(
    long FrameNumber,
    double FrameMilliseconds,
    double TerrainMilliseconds,
    double ReferencesMilliseconds,
    double WaterMilliseconds,
    double WireframeMilliseconds,
    double SkyMilliseconds,
    double BlendedMilliseconds,
    double ShadowMilliseconds,
    double ResolveMilliseconds,
    GpuShadowCascadeTimings Cascade0,
    GpuShadowCascadeTimings Cascade1,
    GpuShadowCascadeTimings Cascade2,
    GpuShadowCascadeTimings Cascade3)
{
    /// <summary>
    ///     Sum of every instrumented top-level pass. Cascade slots are NOT added — they are a
    ///     breakdown of <see cref="ShadowMilliseconds" />, not additional work.
    /// </summary>
    public double AccountedMilliseconds =>
        TerrainMilliseconds + ReferencesMilliseconds + WaterMilliseconds + WireframeMilliseconds +
        SkyMilliseconds + BlendedMilliseconds + ShadowMilliseconds + ResolveMilliseconds;

    /// <summary>
    ///     GPU time inside the frame that no instrumented region covers. The whole point of the
    ///     expanded region set: if this is not ~0, something expensive is still unmeasured.
    /// </summary>
    public double OtherMilliseconds => Math.Max(0, FrameMilliseconds - AccountedMilliseconds);

    public GpuShadowCascadeTimings Cascade(int index)
    {
        return index switch
        {
            0 => Cascade0,
            1 => Cascade1,
            2 => Cascade2,
            _ => Cascade3
        };
    }
}

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
    internal const int QueryCountPerFrame = 30;

    /// <summary>Per-cascade slot triples, indexed by cascade. Lets the render loop stay a loop.</summary>
    internal static readonly GpuTimestampRegion[] ShadowCascadeStart =
    [
        GpuTimestampRegion.ShadowCascade0Start, GpuTimestampRegion.ShadowCascade1Start,
        GpuTimestampRegion.ShadowCascade2Start, GpuTimestampRegion.ShadowCascade3Start
    ];

    internal static readonly GpuTimestampRegion[] ShadowCascadeRefs =
    [
        GpuTimestampRegion.ShadowCascade0Refs, GpuTimestampRegion.ShadowCascade1Refs,
        GpuTimestampRegion.ShadowCascade2Refs, GpuTimestampRegion.ShadowCascade3Refs
    ];

    internal static readonly GpuTimestampRegion[] ShadowCascadeEnd =
    [
        GpuTimestampRegion.ShadowCascade0End, GpuTimestampRegion.ShadowCascade1End,
        GpuTimestampRegion.ShadowCascade2End, GpuTimestampRegion.ShadowCascade3End
    ];

    private readonly GpuDevice12 _gpu;
    private readonly PendingFrame[] _pendingFrames = new PendingFrame[GpuCommandRecorder12.FramesInFlight];
    private readonly ID3D12QueryHeap _queryHeap;
    private readonly ID3D12Resource _readback;
    private readonly ulong _timestampFrequency;
    private int _activeFrameIndex = -1;
    private bool _disposed;

    // Which slots this frame actually wrote. REQUIRED for correctness, not diagnostics: the shadow pass
    // is conditional, and the per-cascade slots are conditional WITHIN it (a frame may render 0, 2 or 4
    // cascades). ResolveQueryData copies the whole slot range regardless, so an unwritten slot still
    // holds whatever the same frame-index slot held two frames ago — reading it yields a plausible but
    // entirely fictional duration. Every region pair is gated on both its bits being set.
    private uint _writtenMask;

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
            ResourceDescription.Buffer(sizeof(ulong) * QueryCountPerFrame * GpuCommandRecorder12.FramesInFlight),
            ResourceStates.CopyDest);
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

    /// <summary>Selects the query slot set for the frame about to be recorded.</summary>
    public void BeginFrame(int frameIndex)
    {
        ThrowIfDisposed();
        if ((uint)frameIndex >= GpuCommandRecorder12.FramesInFlight)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        _activeFrameIndex = frameIndex;
        _writtenMask = 0;
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

            timings = ReadFrame(i, pending.FrameNumber, pending.WrittenMask);
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
        _writtenMask |= 1u << (int)region;
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

    /// <summary>
    ///     Records the frame number and fence value for the active frame so its timings can be collected once the fence
    ///     signals.
    /// </summary>
    public void MarkActiveFrameSubmitted(long frameNumber, ulong fenceValue)
    {
        ThrowIfDisposed();
        if (_activeFrameIndex < 0)
        {
            return;
        }

        _pendingFrames[_activeFrameIndex] = new PendingFrame(frameNumber, fenceValue, _writtenMask);
        _activeFrameIndex = -1;
        _writtenMask = 0;
    }

    private GpuFrameTimings ReadFrame(int frameIndex, long frameNumber, uint writtenMask)
    {
        var baseQuery = frameIndex * QueryCountPerFrame;
        var readRange = new Range(
            (nuint)(baseQuery * sizeof(ulong)),
            (nuint)((baseQuery + QueryCountPerFrame) * sizeof(ulong)));
        void* data = null;
        _readback.Map(0, readRange, &data).CheckError();
        try
        {
            var values = (ulong*)data + baseQuery;

            // A pair only yields a duration when BOTH its slots were written this frame; otherwise the
            // readback holds a stale tick from an earlier frame in the same slot set.
            double Span(GpuTimestampRegion start, GpuTimestampRegion end)
            {
                var bits = (1u << (int)start) | (1u << (int)end);
                return (writtenMask & bits) != bits
                    ? 0
                    : GpuTimestampMath.TicksToMilliseconds(values[(int)start], values[(int)end], _timestampFrequency);
            }

            GpuShadowCascadeTimings Cascade(int i)
            {
                return new GpuShadowCascadeTimings(
                    Span(ShadowCascadeStart[i], ShadowCascadeRefs[i]),
                    Span(ShadowCascadeRefs[i], ShadowCascadeEnd[i]));
            }

            return new GpuFrameTimings(
                frameNumber,
                Span(GpuTimestampRegion.FrameStart, GpuTimestampRegion.FrameEnd),
                Span(GpuTimestampRegion.TerrainStart, GpuTimestampRegion.TerrainEnd),
                Span(GpuTimestampRegion.ReferencesStart, GpuTimestampRegion.ReferencesEnd),
                Span(GpuTimestampRegion.WaterStart, GpuTimestampRegion.WaterEnd),
                Span(GpuTimestampRegion.WireframeStart, GpuTimestampRegion.WireframeEnd),
                Span(GpuTimestampRegion.SkyStart, GpuTimestampRegion.SkyEnd),
                Span(GpuTimestampRegion.BlendedStart, GpuTimestampRegion.BlendedEnd),
                Span(GpuTimestampRegion.ShadowStart, GpuTimestampRegion.ShadowEnd),
                Span(GpuTimestampRegion.ResolveStart, GpuTimestampRegion.ResolveEnd),
                Cascade(0),
                Cascade(1),
                Cascade(2),
                Cascade(3));
        }
        finally
        {
            _readback.Unmap(0, new Range(0, 0));
        }
    }

    internal static uint QueryIndex(int frameIndex, GpuTimestampRegion region)
    {
        return checked((uint)(frameIndex * QueryCountPerFrame + (int)region));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private readonly record struct PendingFrame(long FrameNumber, ulong FenceValue, uint WrittenMask)
    {
        public bool HasValue => FenceValue != 0;
    }
}
