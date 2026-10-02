using Slfx77.Multitool.Core.Lifetime;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A dedicated D3D12 <see cref="CommandListType.Copy" /> queue used to stream resource
///     uploads (texture mip copies) on the DMA engine in parallel with rendering, so the
///     per-frame direct command list — recorded on the WinUI render thread — no longer carries
///     the staging copy + barrier that previously caused streaming hitches.
///     <para>
///         Owns its own copy queue, a small ring of copy allocator+list pairs, a copy fence,
///         and a fence-keyed staging-retirement queue. One instance is owned by a single
///         <see cref="GpuTextureCache12" /> and is driven exclusively by that cache's dedicated
///         uploader thread (see <see cref="Orchestration.DedicatedWorkerThread" />), mirroring the per-cache
///         ownership of <see cref="Orchestration.BoundedResolveQueue{TKey,TResult}" />.
///     </para>
///     <para>
///         <b>Cross-queue handshake (no GPU stall).</b> The destination texture is created in
///         <see cref="ResourceStates.Common" />; the copy queue implicitly promotes it to
///         COPY_DEST for the copy and it decays back to COMMON when the copy list completes. The
///         render thread polls <see cref="LastCompletedValue" /> and only once a submission's
///         fence value has completed does it record the COMMON→PIXEL_SHADER_RESOURCE barrier on
///         the direct queue and bind the texture. No <c>ID3D12CommandQueue.Wait</c> is used, so
///         rendering never blocks on the copy engine.
///     </para>
///     <para>
///         <b>Threading.</b> <see cref="Submit" /> and <see cref="RetireCompletedStaging" /> run
///         only on the owning uploader thread. <see cref="LastCompletedValue" /> is a lock-free
///         fence read safe from any thread. <see cref="Flush" /> / <see cref="Dispose" /> are
///         called from the render thread only after the uploader thread has been stopped, so no
///         lock is required around the ring or the staging queue.
///     </para>
/// </summary>
internal sealed class GpuUploadQueue12 : IDisposable
{
    /// <summary>
    ///     Copy allocator+list slots. Three lets the uploader keep recording the next copy while
    ///     up to two earlier submissions drain on the GPU; it also bounds in-flight staging
    ///     memory (a slot can't be reused until its copy completes).
    /// </summary>
    private const int RingSize = 3;

    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[RingSize];

    private readonly ID3D12CommandQueue _copyQueue;
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private readonly ID3D12GraphicsCommandList[] _lists = new ID3D12GraphicsCommandList[RingSize];
    private readonly ulong[] _slotFenceValues = new ulong[RingSize];
    private readonly Queue<StagingRetirement> _stagingRetire = new();
    private readonly GpuDevice12 _gpu;
    private readonly RetiredResourceDisposal _retiredStaging = new();
    private RetiredResourceDisposal? _retiredResources;
    private bool _disposed;
    private bool _stopping;
    private bool _disposing;
    private bool _submissionFailed;
    private bool _retirementProven;
    private ulong _nextFenceValue = 1;
    private int _ringIndex;

    public GpuUploadQueue12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        var queueDesc = new CommandQueueDescription(CommandListType.Copy, CommandQueuePriority.Normal);
        _copyQueue = gpu.Device.CreateCommandQueue<ID3D12CommandQueue>(queueDesc);
        for (var i = 0; i < RingSize; i++)
        {
            _allocators[i] = gpu.Device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Copy);
            // Command lists are born open; close immediately so Submit can Reset before recording.
            _lists[i] = gpu.Device.CreateCommandList<ID3D12GraphicsCommandList>(
                0, CommandListType.Copy, _allocators[i]);
            _lists[i].Close();
        }

        _fence = gpu.Device.CreateFence<ID3D12Fence>();
    }

    /// <summary>
    ///     Highest copy-fence value the GPU has signaled complete. Lock-free; the render
    ///     thread polls this to decide which submitted uploads are safe to bind.
    /// </summary>
    public ulong LastCompletedValue => _fence.CompletedValue;

    /// <summary>Staging buffers awaiting copy completion before they can be freed.</summary>
    public int PendingStagingCount => _stagingRetire.Count;

    /// <summary>Stops admission and proves copy completion or actual device removal before retryable staged cleanup.</summary>
    /// <remarks>A failed fence or release retains its resource and every later prerequisite for another Dispose call.</remarks>
    public void Dispose()
    {
        if (_disposed) return;
#pragma warning disable S3877 // A reentrant Dispose cannot report success while this dependent still owns live resources.
        if (_disposing) { throw new InvalidOperationException("Copy-queue retirement is already in progress."); }
#pragma warning restore S3877
        _disposing = true;
        _stopping = true;
        try
        {
            if (!_retirementProven) { EstablishRetirement(); }
            if (_retiredResources is null)
            {
                // The proof covers even an Execute that threw or was followed by a failed Signal.
                while (_stagingRetire.TryPeek(out var pending))
                {
                    _retiredStaging.Add(pending.Resource, "copy staging");
                    _stagingRetire.Dequeue();
                }

                var resources = new RetiredResourceDisposal();
                resources.Add(_retiredStaging, "copy staging", 0);
                foreach (var list in _lists) { resources.Add(list, "copy list", 1); }
                foreach (var allocator in _allocators) { resources.Add(allocator, "copy allocator", 2); }
                resources.Add(_fence, "copy fence", 3);
                resources.Add(_fenceEvent, "copy fence event", 3);
                resources.Add(_copyQueue, "copy queue", 3);
                _retiredResources = resources;
            }

            _retiredResources.Dispose();
            _disposed = true;
        }
        finally { _disposing = false; }
    }

    /// <summary>
    ///     Records and submits one copy-only command list on the copy queue, returning the fence
    ///     value that will signal its completion. <paramref name="record" /> must only issue copy
    ///     operations (no resource-state barriers — copy queues can't transition to shader-read
    ///     states; the destination is created in COMMON and the render thread promotes it after
    ///     this fence completes). <paramref name="staging" /> resources are released once the
    ///     returned fence value completes. Uploader-thread only.
    /// </summary>
    /// <param name="record">Records copies synchronously before ownership transfer or execution.</param>
    /// <param name="staging">Resources transferred to this queue immediately before Execute.</param>
    /// <param name="stagingOwnershipTransferred">True even on a later exception when this queue owns all staging until proven retirement.</param>
    /// <returns>The signaled copy-fence value.</returns>
    public ulong Submit(Action<ID3D12GraphicsCommandList> record, IReadOnlyList<IDisposable> staging,
        out bool stagingOwnershipTransferred)
    {
        stagingOwnershipTransferred = false;
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(staging);
        ObjectDisposedException.ThrowIf(_stopping || _disposed, this);
        if (_submissionFailed) { throw new InvalidOperationException("The copy queue requires retirement after a failed submission."); }

        RetireCompletedStaging();

        var slot = _ringIndex;
        // Back-pressure: never reset an allocator the GPU may still be reading. If this slot's
        // prior submission is still in flight, wait for it (on the uploader thread, not render).
        WaitForFence(_slotFenceValues[slot]);

        _allocators[slot].Reset();
        var list = _lists[slot];
        list.Reset(_allocators[slot], null);
        record(list);
        list.Close();

        var value = _nextFenceValue++;
        var ownedStaging = staging.ToArray();
        foreach (var resource in ownedStaging) { ArgumentNullException.ThrowIfNull(resource); }
        _stagingRetire.EnsureCapacity(checked(_stagingRetire.Count + ownedStaging.Length));
        foreach (var resource in ownedStaging) { _stagingRetire.Enqueue(new StagingRetirement(resource, value)); }
        stagingOwnershipTransferred = true;
        _retirementProven = false;
        try
        {
            _copyQueue.ExecuteCommandList(list);
            _copyQueue.Signal(_fence, value).CheckError();
        }
        catch
        {
            // Execute may already have reached the GPU. No later upload may reuse this slot, and
            // the caller must retain its destination until this queue can establish retirement.
            _submissionFailed = true;
            throw;
        }
        _slotFenceValues[slot] = value;
        _ringIndex = (slot + 1) % RingSize;

        return value;
    }

    /// <summary>
    ///     Disposes staging buffers whose copy has completed, retaining failed releases for retry.
    ///     Uploader-thread only, or called by <see cref="Flush" /> after the uploader has stopped.
    /// </summary>
    public void RetireCompletedStaging()
    {
        ObjectDisposedException.ThrowIf(_stopping || _disposed, this);
        var completed = CompletedValueWithRemovalProof();
        while (_stagingRetire.TryPeek(out var head) && head.FenceValue <= completed)
        {
            _retiredStaging.Add(head.Resource, "completed copy staging");
            _stagingRetire.Dequeue();
        }
        _retiredStaging.Dispose();
    }

    /// <summary>
    ///     Proves completion of all outstanding copy work or actual device removal, then retires staging.
    ///     Call from the render thread only after the owning uploader thread has stopped (so no
    ///     concurrent <see cref="Submit" /> can race the ring/staging state).
    /// </summary>
    public void Flush()
    {
        if (_disposed) return;
        ObjectDisposedException.ThrowIf(_stopping, this);
        EstablishRetirement();
        RetireCompletedStaging();
    }

    /// <summary>Establishes all-copy retirement; a failed wait alone never authorizes releasing submitted resources.</summary>
    private void EstablishRetirement()
    {
        try
        {
            var value = _nextFenceValue++;
            _copyQueue.Signal(_fence, value).CheckError();
            WaitForFence(value);
        }
        catch
        {
            if (!_gpu.TryForceDeviceRemoval("texture-copy-retirement")) { throw; }
        }
        _retirementProven = true;
    }

    /// <summary>Reads completion without mistaking a removal sentinel for an unverified ordinary fence value.</summary>
    /// <returns>The completed value, including the removal sentinel only after actual removal is established.</returns>
    private ulong CompletedValueWithRemovalProof()
    {
        var completed = _fence.CompletedValue;
        if (completed == ulong.MaxValue && !_gpu.TryForceDeviceRemoval("texture-copy-completion"))
        {
            throw new InvalidOperationException("Copy-fence completion could not establish device retirement.");
        }
        return completed;
    }

    /// <summary>Waits without pumping UI messages and verifies the requested fence really completed.</summary>
    /// <param name="value">Copy-fence value requiring completion.</param>
    private void WaitForFence(ulong value)
    {
        D3D12FenceWaiter.WaitForFence(_fence, value, _fenceEvent);
        if (CompletedValueWithRemovalProof() < value)
        {
            throw new InvalidOperationException("The copy-fence wait returned before its requested submission completed.");
        }
    }

    private readonly record struct StagingRetirement(IDisposable Resource, ulong FenceValue);
}
