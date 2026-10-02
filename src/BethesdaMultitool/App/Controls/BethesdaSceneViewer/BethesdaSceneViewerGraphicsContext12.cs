using Slfx77.Multitool.Core.Lifetime;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Vortice.Direct3D12;

namespace BethesdaMultitool;

/// <summary>
///     App-scoped D3D12 stack shared by native Mesh Viewer and NPC Viewer controls. One command
///     recorder is intentional: <see cref="GpuDevice12.FrameFence" /> is device-scoped, while each
///     recorder starts its own fence-value sequence, so independent recorders on one device would
///     violate the monotonic fence contract. A complete frame is serialized through
///     <see cref="BeginFrame" /> instead.
/// </summary>
internal sealed class BethesdaSceneViewerGraphicsContext12 : IDisposable
{
    private const uint DescriptorCapacity = 131_072;
    private const uint PersistentDescriptorCapacity = 16_384;
    private const uint RingBytesPerFrame = 64u * 1024u * 1024u;

    private static readonly object SharedGate = new();
    private static readonly Logger Log = Logger.Instance;
    private static BethesdaSceneViewerGraphicsContext12? _shared;
    private static int _sharedLeaseCount;
    private readonly ID3D12DescriptorHeap[] _descriptorHeaps;

    private readonly object _frameGate = new();
    private readonly Dictionary<BethesdaGame, ReferencePipelineFactory12> _referencePipelines = [];
    private bool _deviceTerminal;
    private bool _disposed;
    private RetiredResourceDisposal? _retiredResources;

    private BethesdaSceneViewerGraphicsContext12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuDescriptorHeapAllocator12 descriptorHeap,
        GpuRootSignature12 rootSignature,
        GpuDeletionQueue12 deletionQueue)
    {
        Gpu = gpu;
        Recorder = recorder;
        RingBuffer = ringBuffer;
        DescriptorHeap = descriptorHeap;
        RootSignature = rootSignature;
        DeletionQueue = deletionQueue;
        _descriptorHeaps = [descriptorHeap.Heap];
    }

    internal GpuDevice12 Gpu { get; }

    internal GpuCommandRecorder12 Recorder { get; }

    internal GpuRingBuffer12 RingBuffer { get; }

    internal GpuDescriptorHeapAllocator12 DescriptorHeap { get; }

    internal GpuRootSignature12 RootSignature { get; }

    internal GpuDeletionQueue12 DeletionQueue { get; }

    internal bool IsSoftwareAdapter => Gpu.IsSoftwareAdapter;

    /// <summary>True only after device termination has completed and live GPU resources may be retired.</summary>
    internal bool IsDeviceTerminal => _deviceTerminal;

    /// <summary>
    ///     Acquires the process-wide native-viewer graphics stack. Creation and every heap mutation
    ///     occur on the calling WinUI thread; the returned lease keeps the stack alive across tab
    ///     unload/reload while panel-bound swap chains remain per-control.
    /// </summary>
    internal static BethesdaSceneViewerGraphicsLease12? TryAcquire(out string? error)
    {
        lock (SharedGate)
        {
            try
            {
                _shared ??= Create();
                _sharedLeaseCount++;
                error = null;
                return new BethesdaSceneViewerGraphicsLease12(_shared);
            }
            catch (Exception ex)
            {
                Log.Error("BethesdaSceneViewer: shared D3D12 initialization failed: {0}", ex);
                error = ex.Message;
                return null;
            }
        }
    }

    /// <summary>
    ///     Begins an exclusive complete frame on the shared recorder and resets all frame-indexed
    ///     allocators. Dispose the returned recording without submitting to abandon safely.
    /// </summary>
    internal BethesdaSceneViewerFrameRecording12 BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_deviceTerminal)
        {
            throw new InvalidOperationException(
                "The shared Bethesda D3D12 device was terminalized after an unfenced submission.");
        }

        Monitor.Enter(_frameGate);
        try
        {
            // Pay the slot fence before mutating any frame-indexed state. This also serializes safely
            // with a second visible native viewer using the same app-scoped recorder.
            Recorder.WaitForFrameSlot();
            Recorder.BeginFrame();
            DeletionQueue.Tick();
            RingBuffer.ResetFrame();
            DescriptorHeap.BeginFrame(Recorder.FrameIndex);
            Gpu.PumpDebugMessages();

            var commandList = Recorder.CommandList;
            commandList.SetDescriptorHeaps(1, _descriptorHeaps);
            commandList.SetGraphicsRootSignature(RootSignature.RootSignature);
            return new BethesdaSceneViewerFrameRecording12(this);
        }
        catch
        {
            try
            {
                Recorder.AbortFrame();
            }
            finally
            {
                Monitor.Exit(_frameGate);
            }

            throw;
        }
    }

    internal void WaitForGpuIdle()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_frameGate)
        {
            if (_deviceTerminal)
            {
                return;
            }

            try
            {
                Recorder.WaitForGpuIdle();
            }
            catch
            {
                // Once an idle fence cannot be established, callers must not release a surface,
                // session, ring, or descriptor graph which may still be referenced by the queue.
                // Explicit device removal supplies the terminal lifetime boundary before rethrow.
                TerminalizeDeviceAfterUnfencedSubmissionCore("idle-wait-failure");
                throw;
            }
        }
    }

    /// <summary>
    ///     Returns the app-scoped reference PSOs for one game. Mesh and NPC scene changes borrow
    ///     this factory instead of rebuilding the same direct/shadow/A2C pipeline family for every
    ///     selection. The complete-frame gate also prevents a new game's driver PSO construction
    ///     from racing another native viewer's command recording.
    /// </summary>
    internal ReferencePipelineFactory12 GetOrCreateReferencePipelines(BethesdaGame game)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_frameGate)
        {
            if (_deviceTerminal)
            {
                throw new InvalidOperationException(
                    "The shared Bethesda D3D12 device was terminalized before pipeline creation.");
            }

            if (_referencePipelines.TryGetValue(game, out var existing))
            {
                Log.Debug(
                    "BethesdaSceneViewer: reused app-scoped reference pipelines for {0}.",
                    game);
                return existing;
            }

            var created = new ReferencePipelineFactory12(Gpu, RootSignature, game);
            _referencePipelines.Add(game, created);
            Log.Info(
                "BethesdaSceneViewer: cached app-scoped reference pipelines for {0}.",
                game);
            return created;
        }
    }

    /// <summary>
    ///     Ends the shared device before a failed post-Execute submission can flow through SetFaulted
    ///     and release its surface/session graph. Idempotent because both viewer controls share this
    ///     context and the second one may observe the same terminal device on its next frame.
    /// </summary>
    internal void TerminalizeDeviceAfterUnfencedSubmission()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_frameGate)
        {
            TerminalizeDeviceAfterUnfencedSubmissionCore("unfenced-frame-submit");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_retiredResources is null)
        {
            if (!_deviceTerminal)
            {
                try
                {
                    lock (_frameGate) { Recorder.WaitForGpuIdle(); }
                }
                catch (Exception exception)
                {
                    Log.Warn("BethesdaSceneViewer: GPU idle wait during shared teardown failed: {0}", exception.Message);
                    lock (_frameGate) { TerminalizeDeviceAfterUnfencedSubmissionCore("shared-context-teardown"); }
                    if (!_deviceTerminal) { throw; }
                }
            }
            // No collection is published until the queue is drained or the device is terminal.
            _retiredResources = new RetiredResourceDisposal(
                (name, exception) => Log.Warn("BethesdaSceneViewer: {0} teardown failed: {1}", name, exception.Message));
            foreach (var (game, pipelines) in _referencePipelines)
            {
                _retiredResources.Add(pipelines, $"{game} reference pipelines", 1);
            }
            _referencePipelines.Clear();
            // Recorder-held retirements may return descriptors or enqueue deletion work.
            // Keep those allocators/queues alive until the recorder has drained every child.
            _retiredResources.Add(() => Recorder.DisposeAfterGpuIdleAttempt(), "command recorder");
            _retiredResources.Add(DeletionQueue, "deletion queue", 1);
            _retiredResources.Add(RootSignature, "root signature", 2);
            _retiredResources.Add(DescriptorHeap, "descriptor heap", 2);
            _retiredResources.Add(RingBuffer, "ring buffer", 2);
            _retiredResources.Add(Gpu, "D3D12 device", 3);
        }
        _retiredResources.Dispose();
    }

    /// <summary>Records actual device removal without releasing borrowed renderer or surface prerequisites.</summary>
    /// <param name="context">Diagnostic origin of the failed queue handshake.</param>
    private void TerminalizeDeviceAfterUnfencedSubmissionCore(string context)
    {
        if (_deviceTerminal) { return; }
        // Releasing COM wrappers does not prove that queued GPU work has stopped.
        if (!Gpu.TryForceDeviceRemoval(context))
        {
            throw new InvalidOperationException("D3D12 retirement is uncertain; the native ownership graph remains retained.");
        }
        _deviceTerminal = true;
    }

    private static BethesdaSceneViewerGraphicsContext12 Create()
    {
        GpuDevice12? gpu = null;
        GpuCommandRecorder12? recorder = null;
        GpuRingBuffer12? ringBuffer = null;
        GpuDescriptorHeapAllocator12? descriptorHeap = null;
        GpuRootSignature12? rootSignature = null;
        GpuDeletionQueue12? deletionQueue = null;

        try
        {
            var debugLayer = EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.D3D12Debug);
            gpu = GpuDevice12.Create(debugLayer, GpuAdapterPolicy.PreferHardwareThenWarp)
                  ?? throw new InvalidOperationException(
                      "No Direct3D 12 device could be created (feature level 12_0 or WARP required).");
            recorder = new GpuCommandRecorder12(gpu);
            ringBuffer = new GpuRingBuffer12(gpu, GpuCommandRecorder12.FramesInFlight, RingBytesPerFrame);
            descriptorHeap = new GpuDescriptorHeapAllocator12(
                    gpu,
                    DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                    DescriptorCapacity,
                    GpuCommandRecorder12.FramesInFlight,
                    PersistentDescriptorCapacity)
                .RegisterWith(ResourceRegistry.Instance, "bethesda-scene-viewer");
            rootSignature = GpuRootSignature12.Create(gpu);
            deletionQueue = new GpuDeletionQueue12(GpuCommandRecorder12.FramesInFlight)
                .RegisterWith(ResourceRegistry.Instance, "bethesda-scene-viewer");

            return new BethesdaSceneViewerGraphicsContext12(
                gpu,
                recorder,
                ringBuffer,
                descriptorHeap,
                rootSignature,
                deletionQueue);
        }
        catch
        {
            deletionQueue?.Dispose();
            rootSignature?.Dispose();
            descriptorHeap?.Dispose();
            ringBuffer?.Dispose();
            recorder?.Dispose();
            gpu?.Dispose();
            throw;
        }
    }

    internal sealed class BethesdaSceneViewerGraphicsLease12(
        BethesdaSceneViewerGraphicsContext12 context) : IDisposable
    {
        private BethesdaSceneViewerGraphicsContext12? _context = context;
        private BethesdaSceneViewerGraphicsContext12? _pendingDisposal;

        internal BethesdaSceneViewerGraphicsContext12 Context =>
            _context ?? throw new ObjectDisposedException(nameof(BethesdaSceneViewerGraphicsLease12));

        /// <summary>Transfers the last counted lease into a retired context exactly once.</summary>
        /// <param name="released">Context whose lease ownership was cleared by its caller under SharedGate.</param>
        /// <returns>The last context to dispose, or null while another lease remains.</returns>
        private static BethesdaSceneViewerGraphicsContext12? ReleaseSharedLease(BethesdaSceneViewerGraphicsContext12? released)
        {
            if (released is null || !ReferenceEquals(_shared, released) || _sharedLeaseCount <= 0) { return null; }
            _sharedLeaseCount--;
            if (_sharedLeaseCount != 0) { return null; }
            _shared = null;
            return released;
        }

        public void Dispose()
        {
            lock (SharedGate)
            {
                var released = _context;
                _context = null;
                _pendingDisposal ??= ReleaseSharedLease(released);
                // The count changes only once. A failed final context release remains owned here
                // and retries independently of any newly acquired device/context.
                _pendingDisposal?.Dispose();
                _pendingDisposal = null;
            }
        }
    }

    internal sealed class BethesdaSceneViewerFrameRecording12 : IDisposable
    {
        private BethesdaSceneViewerGraphicsContext12? _context;

        internal BethesdaSceneViewerFrameRecording12(BethesdaSceneViewerGraphicsContext12 context)
        {
            _context = context;
        }

        internal GpuCommandSubmissionOutcome12 Submit(IDisposable? retainIfUnfenced = null)
        {
            var context = _context ??
                          throw new ObjectDisposedException(nameof(BethesdaSceneViewerFrameRecording12));
            try
            {
                return context.Recorder.EndFrameWithOutcome(retainIfUnfenced);
            }
            finally
            {
                _context = null;
                Monitor.Exit(context._frameGate);
            }
        }

        public void Dispose()
        {
            var context = _context;
            if (context is null) return;

            try
            {
                context.Recorder.AbortFrame();
            }
            finally
            {
                _context = null;
                Monitor.Exit(context._frameGate);
            }
        }
    }
}
