using System.Diagnostics;
using System.Runtime.ExceptionServices;
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

    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[FramesInFlight];
    private readonly List<IGpuCommandSubmissionParticipant12> _currentFrameParticipants = new();
    private readonly List<IDisposable> _currentFrameRetirements = new();
    private readonly AutoResetEvent _fenceEvent = new(false);
    private readonly Queue<FenceRetirement> _fenceRetirements = new();
    private readonly ulong[] _frameFenceValues = new ulong[FramesInFlight];
    private readonly List<IDisposable> _unfencedSubmissionRetirements = new();

    private readonly GpuDevice12 _gpu;
    private bool _disposed;

    // True once this frame slot's wait counters have been reset, so a wait split across
    // WaitForFrameSlot + BeginFrame reports one total rather than only the second half.
    private bool _fenceWaitAccumulating;
    private bool _frameOpen;
    private bool _submissionPoisoned;
    private ulong _nextFenceValue = 1;

    public GpuCommandRecorder12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        for (var i = 0; i < FramesInFlight; i++)
        {
            _allocators[i] = gpu.Device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct);
        }

        // Command lists are born in the Open state; Close immediately so BeginFrame can Reset.
        CommandList = gpu.Device.CreateCommandList<ID3D12GraphicsCommandList>(
            0,
            CommandListType.Direct,
            _allocators[0],
            null);
        CommandList.Close();
    }

    /// <summary>
    ///     The current frame's command list. Begin/end the frame via the recorder; the
    ///     list itself is exposed for renderers to record commands into.
    /// </summary>
    public ID3D12GraphicsCommandList CommandList { get; }

    /// <summary>
    ///     0..<see cref="FramesInFlight" />-1 — frame-keyed accumulators (ring buffer
    ///     offset, descriptor heap slot, etc.) index off this.
    /// </summary>
    public int FrameIndex { get; private set; }

    /// <summary>
    ///     True when the most recent <see cref="BeginFrame" /> blocked on the GPU fence
    ///     (CPU was ahead of the GPU). A4 — gate for raising <see cref="FramesInFlight" /> 2→3: if
    ///     this is regularly true, the CPU is fence-bound and a deeper pipeline would help.
    /// </summary>
    public bool LastFrameWaitedOnFence { get; private set; }

    /// <summary>
    ///     Milliseconds the most recent <see cref="BeginFrame" /> spent blocked on the fence
    ///     (0 when it did not wait). Surfaced so the bump decision is measured, not guessed.
    /// </summary>
    public double LastFrameFenceWaitMilliseconds { get; private set; }

    /// <summary>Fence value signaled by the most recent <see cref="EndFrame" /> submission.</summary>
    public ulong LastSubmittedFenceValue { get; private set; }

    public void Dispose() => DisposeCore(waitForGpuIdle: true);

    /// <summary>
    ///     Releases recorder-owned objects after the owner has already made its one best-effort idle
    ///     wait. This keeps shared-context teardown from issuing a second queue signal after device
    ///     removal while preserving <see cref="Dispose()" /> as the safe standalone API.
    /// </summary>
    internal void DisposeAfterGpuIdleAttempt() => DisposeCore(waitForGpuIdle: false);

    private void DisposeCore(bool waitForGpuIdle)
    {
        if (_disposed) return;
        _disposed = true;

        if (waitForGpuIdle)
        {
            try
            {
                WaitForGpuIdle();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GpuCommandRecorder12: GPU idle wait during teardown failed: {ex}");
                if (!_gpu.TryForceDeviceRemoval("command-recorder-teardown"))
                {
                    // An owner cannot safely release allocator/retirement state after an unfenced
                    // wait failure. The supported Windows runtime exposes RemoveDevice; keep the
                    // same terminal queue/device-release fallback as the shared viewer context.
                    _gpu.Dispose();
                }
            }
        }

        if (_frameOpen)
        {
            try
            {
                AbortFrame();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GpuCommandRecorder12: abandoning the final open frame failed: {ex}");
            }
        }

        RetireAllFenceResources();
        DisposeNoThrow(CommandList, "command list");
        foreach (var allocator in _allocators)
        {
            DisposeNoThrow(allocator, "command allocator");
        }

        DisposeNoThrow(_fenceEvent, "fence event");
    }

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
    ///     Enlists a transaction participant in the currently open command list. A participant is
    ///     notified exactly once when that list is either successfully submitted or abandoned, and
    ///     duplicate enlistment of the same instance in one frame is ignored. This is the commit
    ///     boundary for CPU cache state whose backing data exists only as commands in the open list.
    /// </summary>
    public void EnlistCurrentFrame(IGpuCommandSubmissionParticipant12 participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!_frameOpen)
        {
            throw new InvalidOperationException(
                "A command-submission participant can only enlist while a frame is open.");
        }

        foreach (var enlisted in _currentFrameParticipants)
        {
            if (ReferenceEquals(enlisted, participant))
            {
                return;
            }
        }

        _currentFrameParticipants.Add(participant);
    }

    /// <summary>
    ///     Blocks until the GPU has finished with this frame slot, WITHOUT touching any D3D12 state.
    ///     <para>
    ///         Exists so the host can pay the wait BEFORE it samples input and integrates the camera.
    ///         The wait is the dominant term in a GPU-bound frame (measured at 46 ms of a 70 ms frame),
    ///         and anything sampled ahead of it is already that stale by the time recording starts —
    ///         which reads as the camera lagging the mouse. Waiting first makes the pose as fresh as
    ///         the frame can possibly deliver. Idempotent: <see cref="BeginFrame" /> re-checks the
    ///         fence and finds it already satisfied.
    ///     </para>
    /// </summary>
    public void WaitForFrameSlot()
    {
        ThrowIfSubmissionPoisoned();
        if (_frameOpen)
        {
            return;
        }

        WaitForFrameSlotFence();
    }

    /// <summary>
    ///     Blocks until the GPU is done with this frame slot's resources, then resets the
    ///     allocator and command list ready for fresh recording. Call once at the top of
    ///     every frame before any other D3D12 work.
    /// </summary>
    public void BeginFrame()
    {
        ThrowIfSubmissionPoisoned();
        if (_frameOpen) throw new InvalidOperationException("BeginFrame called twice without EndFrame.");

        WaitForFrameSlotFence();
        RetireCompletedFenceResources();
        _allocators[FrameIndex].Reset();
        CommandList.Reset(_allocators[FrameIndex], null);
        _frameOpen = true;
    }

    /// <summary>
    ///     The fence wait itself. Accumulates into <see cref="LastFrameFenceWaitMilliseconds" /> so the
    ///     reported figure stays whole no matter how the wait is split between
    ///     <see cref="WaitForFrameSlot" /> and <see cref="BeginFrame" />; the pair is reset by whichever
    ///     runs first for this frame slot.
    /// </summary>
    private void WaitForFrameSlotFence()
    {
        if (!_fenceWaitAccumulating)
        {
            LastFrameWaitedOnFence = false;
            LastFrameFenceWaitMilliseconds = 0;
            _fenceWaitAccumulating = true;
        }

        var waitFor = _frameFenceValues[FrameIndex];
        if (waitFor > 0 && _gpu.FrameFence.CompletedValue < waitFor)
        {
            var waitStarted = Stopwatch.GetTimestamp();
            D3D12FenceWaiter.WaitForFence(_gpu.FrameFence, waitFor, _fenceEvent);
            LastFrameWaitedOnFence = true;
            LastFrameFenceWaitMilliseconds += Stopwatch.GetElapsedTime(waitStarted).TotalMilliseconds;
        }
    }

    /// <summary>
    ///     Closes and discards the currently recorded command list without submitting it. This is the
    ///     recovery path for a CPU-side recording failure: because none of the recorded barriers or
    ///     draws reach the queue, GPU resources retain the states established by the preceding
    ///     successfully submitted frame. The frame slot still advances so the next BeginFrame waits
    ///     the other slot's prior fence; this preserves the frame-count deletion queue's safety proof.
    /// </summary>
    /// <remarks>
    ///     Call only before <see cref="EndFrame" /> starts submission. Resources whose disposal was
    ///     tied to the abandoned list are released immediately because the GPU can never reference
    ///     them.
    /// </remarks>
    /// <returns>True when an open frame was abandoned; false when no frame was open.</returns>
    public bool AbortFrame()
    {
        if (!_frameOpen) return false;

        try
        {
            CommandList.Close();
        }
        finally
        {
            try
            {
                foreach (var resource in _currentFrameRetirements)
                {
                    DisposeNoThrow(resource, "aborted-frame retirement");
                }
            }
            finally
            {
                _currentFrameRetirements.Clear();
                NotifyCurrentFrameParticipants(submitted: false);
                FrameIndex = (FrameIndex + 1) % FramesInFlight;
                _frameOpen = false;
                _fenceWaitAccumulating = false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Closes the command list, submits it to the device's direct queue, signals the
    ///     frame fence so <see cref="BeginFrame" /> on this slot N frames hence can detect
    ///     completion, then rotates <see cref="FrameIndex" /> to the next slot.
    /// </summary>
    public void EndFrame()
    {
        EndFrameWithOutcome().ThrowIfFailed();
    }

    /// <summary>
    ///     Ends a frame while preserving whether the command list may already be executing when a
    ///     queue signal fails. The optional lifetime is transferred to the recorder only in that
    ///     unfenced case; on success or a pre-execute failure it remains caller-owned.
    /// </summary>
    internal GpuCommandSubmissionOutcome12 EndFrameWithOutcome(IDisposable? retainIfUnfenced = null)
    {
        if (!_frameOpen) throw new InvalidOperationException("EndFrame called without BeginFrame.");

        try
        {
            CommandList.Close();
        }
        catch (Exception ex)
        {
            return FinalizeFailedSubmission(ex, commandListMayHaveReachedQueue: false, retainIfUnfenced);
        }

        try
        {
            _gpu.DirectQueue.ExecuteCommandList(CommandList);
        }
        catch (Exception ex)
        {
            // ExecuteCommandLists has no HRESULT return. If its managed projection throws while
            // crossing the native boundary, conservatively retain every referenced lifetime.
            return FinalizeFailedSubmission(ex, commandListMayHaveReachedQueue: true, retainIfUnfenced);
        }

        var signalValue = _nextFenceValue++;
        try
        {
            _gpu.DirectQueue.Signal(_gpu.FrameFence, signalValue).CheckError();
        }
        catch (Exception ex)
        {
            // Execute succeeded, so releasing staging/readback resources here can race the GPU even
            // though no usable completion fence was published. Poison future recording and retain
            // those objects until the owner performs its final idle/device teardown.
            return FinalizeFailedSubmission(ex, commandListMayHaveReachedQueue: true, retainIfUnfenced);
        }

        _frameFenceValues[FrameIndex] = signalValue;
        LastSubmittedFenceValue = signalValue;
        for (var i = 0; i < _currentFrameRetirements.Count; i++)
        {
            _fenceRetirements.Enqueue(new FenceRetirement(_currentFrameRetirements[i], signalValue));
        }

        _currentFrameRetirements.Clear();
        NotifyCurrentFrameParticipants(submitted: true);

        FrameIndex = (FrameIndex + 1) % FramesInFlight;
        _frameOpen = false;
        _fenceWaitAccumulating = false;
        return GpuCommandSubmissionOutcome12.Success(signalValue);
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
            DisposeNoThrow(pending.Resource, "fence retirement");
        }

        foreach (var resource in _currentFrameRetirements)
        {
            DisposeNoThrow(resource, "current-frame retirement");
        }

        _currentFrameRetirements.Clear();
        foreach (var resource in _unfencedSubmissionRetirements)
        {
            DisposeNoThrow(resource, "unfenced-submission retirement");
        }

        _unfencedSubmissionRetirements.Clear();
    }

    private GpuCommandSubmissionOutcome12 FinalizeFailedSubmission(
        Exception error,
        bool commandListMayHaveReachedQueue,
        IDisposable? retainIfUnfenced)
    {
        if (commandListMayHaveReachedQueue)
        {
            _unfencedSubmissionRetirements.AddRange(_currentFrameRetirements);
            if (retainIfUnfenced is not null)
            {
                _unfencedSubmissionRetirements.Add(retainIfUnfenced);
            }

            NotifyCurrentFrameParticipants(submitted: true);
        }
        else
        {
            foreach (var resource in _currentFrameRetirements)
            {
                DisposeNoThrow(resource, "abandoned submission retirement");
            }

            NotifyCurrentFrameParticipants(submitted: false);
        }

        _currentFrameRetirements.Clear();
        _submissionPoisoned = true;
        FrameIndex = (FrameIndex + 1) % FramesInFlight;
        _frameOpen = false;
        _fenceWaitAccumulating = false;
        return GpuCommandSubmissionOutcome12.Failure(commandListMayHaveReachedQueue, error);
    }

    private void ThrowIfSubmissionPoisoned()
    {
        if (_submissionPoisoned)
        {
            throw new InvalidOperationException(
                "The D3D12 command recorder cannot begin another frame after an unfenced or failed submission.");
        }
    }

    private static void DisposeNoThrow(IDisposable resource, string resourceKind)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GpuCommandRecorder12: disposing {resourceKind} failed: {ex}");
        }
    }

    /// <summary>
    ///     Submission notification is deliberately exception-isolated. Recorder state must always
    ///     advance after Execute/Signal (or after Abort closes the list), even if a cache participant
    ///     has a bookkeeping bug. Participants are expected to be no-throw; the catch is the final
    ///     containment boundary that keeps one cache from wedging every future BeginFrame.
    /// </summary>
    private void NotifyCurrentFrameParticipants(bool submitted)
    {
        foreach (var participant in _currentFrameParticipants)
        {
            try
            {
                if (submitted)
                {
                    participant.OnCommandListSubmitted();
                }
                else
                {
                    participant.OnCommandListAborted();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"GpuCommandRecorder12: submission participant {participant.GetType().Name} " +
                    $"threw during {(submitted ? "commit" : "rollback")}: {ex}");
            }
        }

        _currentFrameParticipants.Clear();
    }

    private readonly record struct FenceRetirement(IDisposable Resource, ulong FenceValue);
}

/// <summary>
///     Exact CPU-side outcome of ending one command list. A failed outcome can still require GPU
///     lifetime retention when ExecuteCommandLists was reached before the failure.
/// </summary>
internal readonly record struct GpuCommandSubmissionOutcome12(
    bool Succeeded,
    bool CommandListMayHaveReachedQueue,
    ulong FenceValue,
    Exception? Error)
{
    internal static GpuCommandSubmissionOutcome12 Success(ulong fenceValue) =>
        new(true, true, fenceValue, null);

    internal static GpuCommandSubmissionOutcome12 Failure(
        bool commandListMayHaveReachedQueue,
        Exception error) =>
        new(false, commandListMayHaveReachedQueue, 0, error);

    internal void ThrowIfFailed()
    {
        if (Error is not null)
        {
            ExceptionDispatchInfo.Capture(Error).Throw();
        }
    }
}

/// <summary>
///     A no-throw participant in one open command list's CPU-side transaction. Use this when cache
///     publication is valid only if the commands that initialize its GPU backing are submitted.
/// </summary>
internal interface IGpuCommandSubmissionParticipant12
{
    void OnCommandListSubmitted();

    void OnCommandListAborted();
}
