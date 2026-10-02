using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Adapts Shared's native frame recorder to Bethesda's frame loop, callback diagnostics,
///     capture ownership, and device-removal policy. Native allocation, submission, fence values,
///     and retained resource ownership belong to <see cref="NativeFrameRecorder" />.
/// </summary>
internal sealed class GpuCommandRecorder12 : IDisposable
{
    /// <summary>Two frame slots match the swap chain and its frame-keyed upload and descriptor storage.</summary>
    public const int FramesInFlight = NativeFrameRecorder.FramesInFlight;

    private readonly NativeFrameRecorder _native;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly GpuDevice12 _gpu;
    private bool _disposed;
    private bool _lifetimeCallbackActive;

    /// <summary>Acquires an initialized Shared recorder whose failed initialization remains device-owned.</summary>
    /// <param name="gpu">Borrowed device, queue, and fence retained by the enclosing graphics context.</param>
    public GpuCommandRecorder12(GpuDevice12 gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        _gpu = gpu;
        _native = gpu.CreateFrameRecorder();
    }

    /// <summary>Gets the borrowed command list used by renderers between frame begin and submission.</summary>
    public ID3D12GraphicsCommandList CommandList { get { VerifyAccess(); return _native.CommandList; } }

    /// <summary>Gets the current fence-protected slot for frame-keyed upload and descriptor allocations.</summary>
    public int FrameIndex => _native.FrameIndex;

    /// <summary>Gets whether the current list can accept commands and retirement ownership.</summary>
    internal bool IsRecording { get { VerifyAccess(); return _native.IsFrameOpen && !_disposed; } }

    /// <summary>Gets the non-repeating identity of the most recently opened recording.</summary>
    internal ulong RecordingGeneration => _native.RecordingGeneration;

    /// <summary>Gets whether the current frame-slot wait blocked on GPU completion.</summary>
    public bool LastFrameWaitedOnFence => _native.LastFrameWaitedOnFence;

    /// <summary>Gets total fence-wait milliseconds across the separate wait and frame-begin calls.</summary>
    public double LastFrameFenceWaitMilliseconds => _native.LastFrameFenceWaitMilliseconds;

    /// <summary>Gets the most recent submitted frame's signal, published before participant notification.</summary>
    public ulong LastSubmittedFenceValue => _native.LastSubmittedFenceValue;

    /// <summary>Proves GPU retirement, then releases retained children before native recording resources.</summary>
    /// <exception cref="AggregateException">One or more releases remain owned for an explicit retry.</exception>
    public void Dispose() => DisposeCore(true);

    /// <summary>Releases owned resources after the enclosing owner proved queue retirement or device removal.</summary>
    internal void DisposeAfterGpuIdleAttempt() => DisposeCore(false);

    /// <summary>Preserves Bethesda's terminal-device recovery while Shared retains failed child releases.</summary>
    /// <param name="waitForGpuIdle">False only when the enclosing graphics owner already proved retirement.</param>
    private void DisposeCore(bool waitForGpuIdle)
    {
        VerifyAccess();
        if (!_disposed && waitForGpuIdle)
        {
            try { WaitForGpuIdle(); }
            catch
            {
                if (!_gpu.TryForceDeviceRemoval("command-recorder-teardown")) { throw; }
            }
        }
        _disposed = true;
        // Preserve Bethesda's diagnostic-only participant policy before terminal Shared cleanup.
        // A native Close failure leaves the stopped owner retained for the next disposal attempt.
        if (_native.IsFrameOpen) { AbortFrame(); }
        _lifetimeCallbackActive = true;
        try { _native.DisposeAfterRetirement(); }
        finally { _lifetimeCallbackActive = false; }
    }

    /// <summary>
    ///     Transfers a resource to the open frame's exact submission lifetime. Without an open
    ///     recording, releases it synchronously as required by existing Bethesda callers.
    /// </summary>
    /// <param name="resource">Owned input whose transfer occurs only after registration succeeds.</param>
    /// <exception cref="InvalidOperationException">The caller thread, callback phase, or capacity rejects registration.</exception>
    public void EnqueueDisposeAfterCurrentFrame(IDisposable resource)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(resource);
        if (_disposed || !_native.IsFrameOpen)
        {
            _lifetimeCallbackActive = true;
            try { resource.Dispose(); }
            finally { _lifetimeCallbackActive = false; }
            return;
        }
        _native.RetireAfterCurrentFrame(resource, "current-frame GPU resource");
    }

    /// <summary>Enlists one participant identity in the active frame's submitted, uncertain, or abandoned outcome.</summary>
    /// <param name="participant">Borrowed identity notified once per recording; no resource ownership transfers.</param>
    /// <exception cref="InvalidOperationException">No frame is active, access is invalid, or participant capacity is exhausted.</exception>
    public void EnlistCurrentFrame(ISubmissionParticipant participant)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _native.EnlistCurrentFrame(participant);
    }

    /// <summary>Waits for this frame slot before camera sampling without resetting native recording state.</summary>
    public void WaitForFrameSlot()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _native.WaitForFrameSlot();
    }

    /// <summary>Waits, retires completed ownership, and resets the current allocator and command list.</summary>
    public void BeginFrame()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_native.IsFrameOpen) { throw new InvalidOperationException("BeginFrame called twice without EndFrame."); }
        if (RecordingGeneration == ulong.MaxValue)
            throw new InvalidOperationException("Command recording identities are exhausted.");
        _native.WaitForFrameSlot();
        _lifetimeCallbackActive = true;
        try
        {
            _native.ReleaseCompletedResources();
            _native.BeginFrame();
        }
        finally { _lifetimeCallbackActive = false; }
    }

    /// <summary>
    ///     Abandons unsubmitted commands and rotates the frame slot. Notification and resource-release
    ///     failures remain diagnostic and retryable; a native close failure is returned to the caller.
    /// </summary>
    /// <returns>True when an open frame was abandoned; false when no frame was open.</returns>
    public bool AbortFrame()
    {
        VerifyAccess();
        _lifetimeCallbackActive = true;
        try
        {
            var result = _native.AbortFrame();
            WriteLifetimeDiagnostic("submission participant notification", result.NotificationFailure);
            WriteLifetimeDiagnostic("abandoned recording retirement", result.CleanupFailure);
            if (result.RecordingFailure is not null)
                ExceptionDispatchInfo.Capture(result.RecordingFailure).Throw();
            return result.Aborted;
        }
        finally { _lifetimeCallbackActive = false; }
    }

    /// <summary>Submits the frame and reports the original native submission error, if any.</summary>
    public void EndFrame() => EndFrameWithOutcome().ThrowIfFailed();

    /// <summary>Preserves exact native submission facts while containing independent callback failures.</summary>
    /// <param name="retainIfUnfenced">Borrowed capture or staging owner transferred only when execution becomes uncertain.</param>
    /// <returns>The existing Bethesda outcome, including the unchanged native failure and queue-execution classification.</returns>
    internal GpuCommandSubmissionOutcome12 EndFrameWithOutcome(IDisposable? retainIfUnfenced = null)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _lifetimeCallbackActive = true;
        try
        {
            var result = _native.EndFrame(retainIfUnfenced);
            WriteLifetimeDiagnostic("submission participant notification", result.NotificationFailure);
            WriteLifetimeDiagnostic("abandoned recording retirement", result.CleanupFailure);
            return new GpuCommandSubmissionOutcome12(
                result.Succeeded, result.CommandListMayHaveReachedQueue, result.FenceValue, result.SubmissionFailure);
        }
        finally { _lifetimeCallbackActive = false; }
    }

    /// <summary>Proves queued GPU work complete without resolving an active, unsubmitted recording.</summary>
    public void WaitForGpuIdle()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _native.WaitForGpuIdle();
        _lifetimeCallbackActive = true;
        try
        {
            try { _native.ReleaseCompletedResources(); }
            catch (Exception error)
            {
                // A child release cannot invalidate the completion proof established above.
                WriteLifetimeDiagnostic("proven-idle retirement remains pending", error);
            }
        }
        finally { _lifetimeCallbackActive = false; }
    }

    /// <summary>Contains diagnostic sink failures so they cannot reverse native submission or retirement facts.</summary>
    /// <param name="operation">Ownership operation whose remaining error is diagnostic.</param>
    /// <param name="error">Optional failure retained or independently reported by Shared.</param>
    private static void WriteLifetimeDiagnostic(string operation, Exception? error)
    {
        if (error is null) { return; }
        try { Debug.WriteLine($"GpuCommandRecorder12: {operation}: {error}"); }
#pragma warning disable RCS1075 // Diagnostic sinks cannot change established submission or retirement state.
        catch (Exception) { /* Preserve the already-established native outcome. */ }
#pragma warning restore RCS1075
    }

    /// <summary>Rejects foreign-thread mutation and recorder reentry from application lifetime callbacks.</summary>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("D3D12 command recording belongs to its creating thread.");
        if (_lifetimeCallbackActive)
            throw new InvalidOperationException("D3D12 lifetime callbacks cannot reenter the native recorder.");
    }
}
