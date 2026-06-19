using System.Collections.Concurrent;
using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core.Orchestration;

/// <summary>
///     A bounded, de-duplicating background resolution queue. Keys are enqueued on the owner thread,
///     resolved on background tasks (never more than <c>maxConcurrent</c> in flight), and their
///     results drained back on the owner thread. The standard primitive for pushing expensive
///     resolution (archive read + decode) off a latency-critical thread while keeping consumption of
///     the results on it.
///     <para>
///         The task scheduler is injectable (defaults to <c>Task.Run</c>) so tests can drive
///         resolution deterministically.
///     </para>
///     <para>
///         <b>Threading:</b> <see cref="Enqueue" />, <see cref="Pump" />,
///         <see cref="TryDequeueCompleted" /> and <see cref="WaitForDrain()" /> are single-thread
///         (owner-thread) calls. Only the background resolution tasks run elsewhere, and they touch
///         only the thread-safe completion queue and the interlocked active counter.
///     </para>
/// </summary>
internal sealed class BoundedResolveQueue<TKey, TResult> : ITrackableResource, IDisposable
    where TKey : notnull
    where TResult : class
{
    private readonly int _maxConcurrent;
    private readonly Func<TKey, TResult?> _resolve;
    private readonly Func<Func<TResult?>, Task<TResult?>> _scheduler;
    private readonly Queue<TKey> _pending = new();
    private readonly HashSet<TKey> _known;
    private readonly ConcurrentQueue<Completion> _completed = new();
    private readonly List<Task> _inFlight = [];
    private readonly Lock _inFlightLock = new();
    private ResourceRegistration? _registration;
    private int _active;
    private long _processed;

    /// <summary>Creates a queue that runs at most <paramref name="maxConcurrent" /> resolutions concurrently.</summary>
    /// <param name="name">Stable name for diagnostics.</param>
    /// <param name="maxConcurrent">Hard ceiling on background tasks running at once.</param>
    /// <param name="resolve">The (potentially expensive) resolution run on a background thread.</param>
    /// <param name="keyComparer">Key comparer (e.g. OrdinalIgnoreCase for path keys).</param>
    /// <param name="scheduler">
    ///     Schedules the resolution work. Defaults to <c>Task.Run</c>; tests pass a deferred
    ///     scheduler so concurrency and ordering are observable deterministically.
    /// </param>
    public BoundedResolveQueue(
        string name,
        int maxConcurrent,
        Func<TKey, TResult?> resolve,
        IEqualityComparer<TKey>? keyComparer = null,
        Func<Func<TResult?>, Task<TResult?>>? scheduler = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        ResourceName = name;
        _maxConcurrent = maxConcurrent;
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _scheduler = scheduler ?? (static work => Task.Run(work));
        _known = keyComparer is null ? [] : new HashSet<TKey>(keyComparer);
    }

    /// <summary>
    ///     Registers the queue with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the queue for fluent construction.
    /// </summary>
    public BoundedResolveQueue<TKey, TResult> RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    public string ResourceName { get; }

    public ResourceCategory Category => ResourceCategory.Queue;

    /// <summary>Background tasks currently running.</summary>
    public int ActiveCount => Volatile.Read(ref _active);

    /// <summary>Keys enqueued but not yet started.</summary>
    public int QueuedCount => _pending.Count;

    public ResourceStats GetStats() => new()
    {
        QueueDepth = _pending.Count,
        InFlight = Volatile.Read(ref _active),
        Processed = Interlocked.Read(ref _processed),
    };

    /// <summary>
    ///     Queues <paramref name="key" /> for background resolution unless it is already queued or in
    ///     flight. Returns true when newly enqueued.
    /// </summary>
    public bool Enqueue(TKey key)
    {
        if (!_known.Add(key))
        {
            return false;
        }

        _pending.Enqueue(key);
        return true;
    }

    /// <summary>Starts background resolution tasks up to the concurrency cap.</summary>
    public void Pump()
    {
        PruneInFlight();
        while (Volatile.Read(ref _active) < _maxConcurrent && _pending.Count > 0)
        {
            var key = _pending.Dequeue();
            Interlocked.Increment(ref _active);
            var task = _scheduler(() => _resolve(key)).ContinueWith(
                t =>
                {
                    TResult? result = null;
                    if (t.IsCompletedSuccessfully)
                    {
                        result = t.Result;
                    }
                    else
                    {
                        // Observe a faulted/cancelled antecedent so it never surfaces as an
                        // UnobservedTaskException; the key simply resolves to "no payload".
                        _ = t.Exception;
                    }

                    _completed.Enqueue(new Completion(key, result));
                    Interlocked.Increment(ref _processed);
                    Interlocked.Decrement(ref _active);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            lock (_inFlightLock)
            {
                _inFlight.Add(task);
            }
        }
    }

    /// <summary>
    ///     Dequeues one completed resolution. The key is dropped from the de-dup set so a later
    ///     re-enqueue (e.g. a deliberate retry) is permitted.
    /// </summary>
    public bool TryDequeueCompleted(out TKey key, out TResult? result)
    {
        if (_completed.TryDequeue(out var completion))
        {
            key = completion.Key;
            result = completion.Result;
            _known.Remove(key);
            return true;
        }

        key = default!;
        result = null;
        return false;
    }

    /// <summary>
    ///     Blocks until every in-flight resolution task has finished. Call before disposing whatever
    ///     the resolution delegate reads from, so a background task can never touch a freed resource.
    /// </summary>
    public void WaitForDrain()
    {
        while (true)
        {
            Task[] pending;
            lock (_inFlightLock)
            {
                pending = _inFlight.Where(static t => !t.IsCompleted).ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            // Non-pumping: drains can run on the UI thread during cache/pipeline teardown, where
            // Task.WaitAll's STA pumping wait can re-enter XAML and fail-fast (see NonPumpingWait).
            // The in-flight continuations never fault (they observe their antecedents' exceptions),
            // so WaitAll's throw-on-fault behavior is not needed here.
            NonPumpingWait.WaitAll(pending);
            PruneInFlight();
        }
    }

    /// <summary>
    ///     Blocks until every in-flight resolution task has finished (or <paramref name="timeout" />
    ///     elapses). Returns false on timeout.
    /// </summary>
    public bool WaitForDrain(TimeSpan timeout)
    {
        Task[] pending;
        lock (_inFlightLock)
        {
            pending = _inFlight.Where(static t => !t.IsCompleted).ToArray();
        }

        return pending.Length == 0 || NonPumpingWait.WaitAll(pending, timeout);
    }

    public void Dispose()
    {
        _registration?.Dispose();
    }

    private void PruneInFlight()
    {
        lock (_inFlightLock)
        {
            _inFlight.RemoveAll(static t => t.IsCompleted);
        }
    }

    private readonly record struct Completion(TKey Key, TResult? Result);
}
