using Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

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

    private readonly ID3D12CommandQueue _copyQueue;
    private readonly ID3D12CommandAllocator[] _allocators = new ID3D12CommandAllocator[RingSize];
    private readonly ID3D12GraphicsCommandList[] _lists = new ID3D12GraphicsCommandList[RingSize];
    private readonly ulong[] _slotFenceValues = new ulong[RingSize];
    private readonly ID3D12Fence _fence;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private readonly Queue<StagingRetirement> _stagingRetire = new();
    private ulong _nextFenceValue = 1;
    private int _ringIndex;
    private bool _disposed;

    public GpuUploadQueue12(GpuDevice12 gpu)
    {
        var queueDesc = new CommandQueueDescription(CommandListType.Copy, CommandQueuePriority.Normal);
        _copyQueue = gpu.Device.CreateCommandQueue<ID3D12CommandQueue>(queueDesc);
        for (var i = 0; i < RingSize; i++)
        {
            _allocators[i] = gpu.Device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Copy);
            // Command lists are born open; close immediately so Submit can Reset before recording.
            _lists[i] = gpu.Device.CreateCommandList<ID3D12GraphicsCommandList>(
                nodeMask: 0, CommandListType.Copy, _allocators[i], initialState: null);
            _lists[i].Close();
        }
        _fence = gpu.Device.CreateFence<ID3D12Fence>(0, FenceFlags.None);
    }

    /// <summary>Highest copy-fence value the GPU has signaled complete. Lock-free; the render
    /// thread polls this to decide which submitted uploads are safe to bind.</summary>
    public ulong LastCompletedValue => _fence.CompletedValue;

    /// <summary>Staging buffers awaiting copy completion before they can be freed.</summary>
    public int PendingStagingCount => _stagingRetire.Count;

    /// <summary>
    ///     Records and submits one copy-only command list on the copy queue, returning the fence
    ///     value that will signal its completion. <paramref name="record" /> must only issue copy
    ///     operations (no resource-state barriers — copy queues can't transition to shader-read
    ///     states; the destination is created in COMMON and the render thread promotes it after
    ///     this fence completes). <paramref name="staging" /> resources are released once the
    ///     returned fence value completes. Uploader-thread only.
    /// </summary>
    public ulong Submit(Action<ID3D12GraphicsCommandList> record, IReadOnlyList<IDisposable> staging)
    {
        ArgumentNullException.ThrowIfNull(record);
        ObjectDisposedException.ThrowIf(_disposed, this);

        RetireCompletedStaging();

        var slot = _ringIndex;
        // Back-pressure: never reset an allocator the GPU may still be reading. If this slot's
        // prior submission is still in flight, wait for it (on the uploader thread, not render).
        WaitForFence(_slotFenceValues[slot]);

        _allocators[slot].Reset();
        var list = _lists[slot];
        list.Reset(_allocators[slot], initialState: null);
        record(list);
        list.Close();

        _copyQueue.ExecuteCommandList(list);
        var value = _nextFenceValue++;
        _copyQueue.Signal(_fence, value).CheckError();
        _slotFenceValues[slot] = value;
        _ringIndex = (slot + 1) % RingSize;

        for (var i = 0; i < staging.Count; i++)
        {
            _stagingRetire.Enqueue(new StagingRetirement(staging[i], value));
        }

        return value;
    }

    /// <summary>Disposes staging buffers whose copy has completed. Uploader-thread only (also
    /// invoked from <see cref="Flush" /> / <see cref="Dispose" /> once the uploader is stopped).</summary>
    public void RetireCompletedStaging()
    {
        var completed = _fence.CompletedValue;
        while (_stagingRetire.TryPeek(out var head) && head.FenceValue <= completed)
        {
            _stagingRetire.Dequeue();
            head.Resource.Dispose();
        }
    }

    /// <summary>
    ///     Signals and waits for all outstanding copy work, then retires every staging buffer.
    ///     Call from the render thread only after the owning uploader thread has stopped (so no
    ///     concurrent <see cref="Submit" /> can race the ring/staging state).
    /// </summary>
    public void Flush()
    {
        if (_disposed) return;
        var value = _nextFenceValue++;
        _copyQueue.Signal(_fence, value).CheckError();
        WaitForFence(value);
        RetireCompletedStaging();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Flush();
        _disposed = true;
        // Any staging left after Flush (none expected) — release defensively.
        while (_stagingRetire.TryDequeue(out var pending))
        {
            pending.Resource.Dispose();
        }
        foreach (var list in _lists) list.Dispose();
        foreach (var allocator in _allocators) allocator.Dispose();
        _fence.Dispose();
        _fenceEvent.Dispose();
        _copyQueue.Dispose();
    }

    private void WaitForFence(ulong value)
        => D3D12FenceWaiter.WaitForFence(_fence, value, _fenceEvent);

    private readonly record struct StagingRetirement(IDisposable Resource, ulong FenceValue);
}
