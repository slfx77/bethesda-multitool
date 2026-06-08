using System.Collections.Concurrent;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A single dedicated background thread that drains a bounded queue of upload work items,
///     one at a time. Owned by a <see cref="GpuTextureCache12" /> so that all of its
///     <see cref="GpuUploadQueue12" /> recording happens on exactly one thread — D3D12 command
///     allocators are not free-threaded, so the copy ring must have a single owner. The render
///     thread only enqueues; the uploader thread does the resource creation + staging memcpy +
///     copy submission off the frame critical path.
///     <para>
///         This type is intentionally GPU-agnostic — it runs opaque <see cref="Action" /> work
///         items — so its threading/back-pressure behavior is unit-testable without a D3D12
///         device. The D3D12-specific work lives in the closures the cache enqueues.
///     </para>
///     <para>
///         <b>Back-pressure:</b> <see cref="TryEnqueue" /> is non-blocking. When the queue is
///         full the caller keeps the item pending and retries on a later frame, so the render
///         thread never blocks waiting on the uploader.
///     </para>
/// </summary>
internal sealed class GpuUploadDispatcher12 : IDisposable
{
    /// <summary>Default bound on queued-but-not-yet-processed upload work items.</summary>
    public const int DefaultCapacity = 256;

    private readonly BlockingCollection<Action> _queue;
    private readonly Thread _thread;
    private volatile bool _stopping;

    public GpuUploadDispatcher12(int capacity = DefaultCapacity, string threadName = "GpuTextureUploader")
    {
        _queue = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), Math.Max(1, capacity));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = threadName
        };
        _thread.Start();
    }

    /// <summary>Work items queued but not yet picked up by the uploader thread.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>
    ///     Queues an upload work item for the uploader thread. Returns false (without blocking)
    ///     when the queue is full or the dispatcher is stopping — the caller should leave the
    ///     work pending and retry later rather than wait.
    /// </summary>
    public bool TryEnqueue(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (_stopping)
        {
            return false;
        }

        try
        {
            return _queue.TryAdd(work);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced with this call (queue is shutting down) — treat as "not queued".
            return false;
        }
    }

    /// <summary>
    ///     Stops accepting work and blocks until the uploader thread drains the queue and exits.
    ///     Bounded by the in-flight work item plus whatever is already queued. Idempotent.
    /// </summary>
    public void Stop()
    {
        if (_stopping)
        {
            return;
        }

        _stopping = true;
        _queue.CompleteAdding();
        if (_thread.IsAlive)
        {
            _thread.Join();
        }
    }

    public void Dispose()
    {
        Stop();
        _queue.Dispose();
    }

    private void Run()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    // A single failed upload must never kill the uploader thread — the cache's
                    // work item reports its own failure; this is a last-resort guard.
                    Logger.Instance.Warn("GpuUploadDispatcher12: upload work item threw: {0}", ex);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Queue disposed mid-drain during shutdown — expected, exit quietly.
        }
    }
}
