using System.Collections.Concurrent;
using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core.Orchestration;

/// <summary>
///     A single dedicated background thread that drains a bounded queue of work items, one at a
///     time. The standard primitive for subsystems whose work must have exactly one owning thread —
///     e.g. D3D12 copy-queue recording, where command allocators are not free-threaded: producers
///     only enqueue; the worker does the heavy lifting off their critical path.
///     <para>
///         <b>Back-pressure:</b> <see cref="TryEnqueue" /> is non-blocking. When the queue is full
///         the caller keeps the item pending and retries later, so a producer (e.g. the render
///         thread) never blocks waiting on the worker.
///     </para>
///     <para>
///         A failed work item never kills the worker thread: the exception is logged, counted in
///         <see cref="Failures" />, and surfaced via <see cref="LastError" />.
///     </para>
/// </summary>
internal sealed class DedicatedWorkerThread : ITrackableResource, IDisposable
{
    /// <summary>Default bound on queued-but-not-yet-processed work items.</summary>
    public const int DefaultCapacity = 256;

    private readonly BlockingCollection<Action> _queue;
    private readonly Thread _thread;
    private ResourceRegistration? _registration;
    private long _processed;
    private long _failures;
    private volatile string? _lastError;
    private volatile bool _stopping;

    public DedicatedWorkerThread(string name, int capacity = DefaultCapacity)
    {
        ResourceName = name;
        _queue = new BlockingCollection<Action>(new ConcurrentQueue<Action>(), Math.Max(1, capacity));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = name,
        };
        _thread.Start();
    }

    /// <summary>
    ///     Registers the worker with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the worker for fluent construction.
    /// </summary>
    public DedicatedWorkerThread RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    public string ResourceName { get; }

    public ResourceCategory Category => ResourceCategory.Queue;

    /// <summary>Work items queued but not yet picked up by the worker thread.</summary>
    public int PendingCount => _queue.Count;

    /// <summary>Work items completed (including failed ones) over the worker's lifetime.</summary>
    public long ProcessedCount => Interlocked.Read(ref _processed);

    /// <summary>Work items that threw.</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Message of the most recent work-item exception, when one has occurred.</summary>
    public string? LastError => _lastError;

    public ResourceStats GetStats() => new()
    {
        QueueDepth = _queue.Count,
        Processed = ProcessedCount,
        Failures = Failures,
        LastError = _lastError,
    };

    /// <summary>
    ///     Queues a work item. Returns false (without blocking) when the queue is full or the worker
    ///     is stopping — the caller should leave the work pending and retry later rather than wait.
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
    ///     Stops accepting work and blocks until the worker drains the queue and exits. Bounded by
    ///     the in-flight work item plus whatever is already queued. Idempotent.
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
        _registration?.Dispose();
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
                    // A single failed item must never kill the worker — the producer's work item
                    // reports its own failure; this is a last-resort guard.
                    Interlocked.Increment(ref _failures);
                    _lastError = ex.Message;
                    Logger.Instance.Warn("{0}: work item threw: {1}", ResourceName, ex);
                }
                finally
                {
                    Interlocked.Increment(ref _processed);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Queue disposed mid-drain during shutdown — expected, exit quietly.
        }
    }
}
