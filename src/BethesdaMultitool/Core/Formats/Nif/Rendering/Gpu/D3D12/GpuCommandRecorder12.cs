using System.Diagnostics;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     v3 Pass 4 — D3D12 per-frame command list + allocator pool + fence-based CPU↔GPU sync.
///     The D3D12 analog of D3D11's immediate context — but the command-list reset cadence is
///     CPU-controlled, so we maintain N <see cref="ID3D12CommandAllocator" />s (one per
///     frame-in-flight slot) plus a single reusable command list.
///     <para>
///         Frame loop:
///     </para>
///     <code>
///         recorder.BeginFrame();         // WaitForFence + Reset(allocator) + Reset(commandList)
///         // ... record draw calls via recorder.CommandList ...
///         recorder.EndFrame();           // Close + ExecuteCommandList + Signal(fence) + advance frame
///     </code>
///     <para>
///         <see cref="FrameIndex" /> rotates 0..<see cref="FramesInFlight" />-1 each frame.
///         Resources keyed on frame index (ring buffer, descriptor heap allocator) MUST use
///         that index so the GPU isn't reading slot N's data while the CPU writes slot N+1.
///     </para>
/// </summary>
internal sealed class GpuCommandRecorder12 : IDisposable
{
    /// <summary>
    ///     Two frames in flight matches the swap-chain back-buffer count. Three would allow
    ///     more CPU/GPU overlap but doubles upload-heap memory; revisit if Step 1c shows the
    ///     CPU regularly waiting on fences.
    /// </summary>
    public const int FramesInFlight = 2;

    private readonly GpuDevice12 _gpu;
    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[FramesInFlight];
    private readonly ID3D12GraphicsCommandList _commandList;
    private readonly ulong[] _frameFenceValues = new ulong[FramesInFlight];
    private readonly List<IDisposable> _currentFrameRetirements = new();
    private readonly Queue<FenceRetirement> _fenceRetirements = new();
    private readonly AutoResetEvent _fenceEvent = new(false);
    private ulong _nextFenceValue = 1;
    private int _frameIndex;
    private bool _frameOpen;
    private bool _disposed;

    public GpuCommandRecorder12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        for (var i = 0; i < FramesInFlight; i++)
        {
            _allocators[i] = gpu.Device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct);
        }
        // Command lists are born in the Open state; Close immediately so BeginFrame can Reset.
        _commandList = gpu.Device.CreateCommandList<ID3D12GraphicsCommandList>(
            nodeMask: 0,
            CommandListType.Direct,
            _allocators[0],
            initialState: null);
        _commandList.Close();
    }

    /// <summary>The current frame's command list. Begin/end the frame via the recorder; the
    /// list itself is exposed for renderers to record commands into.</summary>
    public ID3D12GraphicsCommandList CommandList => _commandList;

    /// <summary>0..<see cref="FramesInFlight" />-1 — frame-keyed accumulators (ring buffer
    /// offset, descriptor heap slot, etc.) index off this.</summary>
    public int FrameIndex => _frameIndex;

    /// <summary>True when the most recent <see cref="BeginFrame" /> blocked on the GPU fence
    /// (CPU was ahead of the GPU). A4 — gate for raising <see cref="FramesInFlight" /> 2→3: if
    /// this is regularly true, the CPU is fence-bound and a deeper pipeline would help.</summary>
    public bool LastFrameWaitedOnFence { get; private set; }

    /// <summary>Milliseconds the most recent <see cref="BeginFrame" /> spent blocked on the fence
    /// (0 when it did not wait). Surfaced so the bump decision is measured, not guessed.</summary>
    public double LastFrameFenceWaitMilliseconds { get; private set; }

    /// <summary>Fence value signaled by the most recent <see cref="EndFrame" /> submission.</summary>
    public ulong LastSubmittedFenceValue { get; private set; }

    /// <summary>
    ///     Defers disposal of a short-lived resource used by the currently open command list
    ///     until the fence value signaled for that submission has completed. Upload staging
    ///     resources should use this instead of a frame-count hold so they are released as
    ///     soon as the GPU is actually done with the copy.
    /// </summary>
    public void EnqueueDisposeAfterCurrentFrame(IDisposable resource)
    {
        if (_disposed)
        {
            resource.Dispose();
            return;
        }

        if (!_frameOpen)
        {
            resource.Dispose();
            return;
        }

        _currentFrameRetirements.Add(resource);
    }

    /// <summary>
    ///     Blocks until the GPU is done with this frame slot's resources, then resets the
    ///     allocator and command list ready for fresh recording. Call once at the top of
    ///     every frame before any other D3D12 work.
    /// </summary>
    public void BeginFrame()
    {
        if (_frameOpen) throw new InvalidOperationException("BeginFrame called twice without EndFrame.");

        var waitFor = _frameFenceValues[_frameIndex];
        LastFrameWaitedOnFence = false;
        LastFrameFenceWaitMilliseconds = 0;
        if (waitFor > 0 && _gpu.FrameFence.CompletedValue < waitFor)
        {
            var waitStarted = Stopwatch.GetTimestamp();
            D3D12FenceWaiter.WaitForFence(_gpu.FrameFence, waitFor, _fenceEvent);
            LastFrameWaitedOnFence = true;
            LastFrameFenceWaitMilliseconds = Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
        }

        RetireCompletedFenceResources();
        _allocators[_frameIndex].Reset();
        _commandList.Reset(_allocators[_frameIndex], initialState: null);
        _frameOpen = true;
    }

    /// <summary>
    ///     Closes the command list, submits it to the device's direct queue, signals the
    ///     frame fence so <see cref="BeginFrame" /> on this slot N frames hence can detect
    ///     completion, then rotates <see cref="FrameIndex" /> to the next slot.
    /// </summary>
    public void EndFrame()
    {
        if (!_frameOpen) throw new InvalidOperationException("EndFrame called without BeginFrame.");

        _commandList.Close();
        _gpu.DirectQueue.ExecuteCommandList(_commandList);

        var signalValue = _nextFenceValue++;
        _gpu.DirectQueue.Signal(_gpu.FrameFence, signalValue).CheckError();
        _frameFenceValues[_frameIndex] = signalValue;
        LastSubmittedFenceValue = signalValue;
        for (var i = 0; i < _currentFrameRetirements.Count; i++)
        {
            _fenceRetirements.Enqueue(new FenceRetirement(_currentFrameRetirements[i], signalValue));
        }
        _currentFrameRetirements.Clear();

        _frameIndex = (_frameIndex + 1) % FramesInFlight;
        _frameOpen = false;
    }

    /// <summary>
    ///     Blocks until all queued GPU work completes. Required before destroying resources
    ///     the GPU may still reference (swap-chain resize, app shutdown, backend switch).
    /// </summary>
    public void WaitForGpuIdle()
    {
        var signalValue = _nextFenceValue++;
        _gpu.DirectQueue.Signal(_gpu.FrameFence, signalValue).CheckError();
        D3D12FenceWaiter.WaitForFence(_gpu.FrameFence, signalValue, _fenceEvent);
        Array.Clear(_frameFenceValues);
        RetireAllFenceResources();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WaitForGpuIdle();
        _commandList.Dispose();
        foreach (var a in _allocators) a.Dispose();
        _fenceEvent.Dispose();
    }

    private void RetireCompletedFenceResources()
    {
        var completed = _gpu.FrameFence.CompletedValue;
        while (_fenceRetirements.TryPeek(out var head) && head.FenceValue <= completed)
        {
            _fenceRetirements.Dequeue();
            head.Resource.Dispose();
        }
    }

    private void RetireAllFenceResources()
    {
        while (_fenceRetirements.TryDequeue(out var pending))
        {
            pending.Resource.Dispose();
        }

        foreach (var resource in _currentFrameRetirements)
        {
            resource.Dispose();
        }
        _currentFrameRetirements.Clear();
    }

    private readonly record struct FenceRetirement(IDisposable Resource, ulong FenceValue);
}
