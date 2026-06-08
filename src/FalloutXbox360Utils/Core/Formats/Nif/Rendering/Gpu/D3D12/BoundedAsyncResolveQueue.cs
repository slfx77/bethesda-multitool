using System.Collections.Concurrent;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A bounded, de-duplicating background resolution queue. Keys are enqueued on the render
///     thread, resolved on background tasks (never more than <c>maxConcurrent</c> in flight), and
///     their results drained back on the render thread. Used to push expensive texture payload
///     resolution (BSA read + DDX→DDS conversion + BCn decode) off the render thread while keeping
///     every D3D12 resource creation on it.
///     <para>
///         The task scheduler is injectable (defaults to <c>Task.Run</c>) so tests can drive
///         resolution deterministically — the same testability stance as
///         <see cref="FrameUploadTimeBudget" />'s deterministic constructor.
///     </para>
///     <para>
///         <b>Threading:</b> <see cref="Enqueue" />, <see cref="Pump" />,
///         <see cref="TryDequeueCompleted" /> and <see cref="WaitForDrain" /> are single-thread
///         (render-thread) calls. Only the background resolution tasks run elsewhere, and they touch
///         only the thread-safe completion queue and the interlocked active counter.
///     </para>
/// </summary>
internal sealed class BoundedAsyncResolveQueue<TResult>
    where TResult : class
{
    private readonly int _maxConcurrent;
    private readonly Func<string, TResult?> _resolve;
    private readonly Func<Func<TResult?>, Task<TResult?>> _scheduler;
    private readonly Queue<string> _pending = new();
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Completion> _completed = new();
    private readonly List<Task> _inFlight = new();
    private readonly object _inFlightLock = new();
    private int _active;

    /// <param name="maxConcurrent">Hard ceiling on background tasks running at once.</param>
    /// <param name="resolve">The (potentially expensive) resolution run on a background thread.</param>
    /// <param name="scheduler">
    ///     Schedules the resolution work. Defaults to <c>Task.Run</c>; tests pass a deferred
    ///     scheduler so concurrency and ordering are observable deterministically.
    /// </param>
    public BoundedAsyncResolveQueue(
        int maxConcurrent,
        Func<string, TResult?> resolve,
        Func<Func<TResult?>, Task<TResult?>>? scheduler = null)
    {
        if (maxConcurrent <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent), "Must be > 0.");
        _maxConcurrent = maxConcurrent;
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _scheduler = scheduler ?? (static work => Task.Run(work));
    }

    /// <summary>Background tasks currently running.</summary>
    public int ActiveCount => Volatile.Read(ref _active);

    /// <summary>Keys enqueued but not yet started.</summary>
    public int QueuedCount => _pending.Count;

    /// <summary>
    ///     Queues <paramref name="key" /> for background resolution unless it is already queued or in
    ///     flight. Returns true when newly enqueued.
    /// </summary>
    public bool Enqueue(string key)
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
    public bool TryDequeueCompleted(out string key, out TResult? result)
    {
        if (_completed.TryDequeue(out var completion))
        {
            key = completion.Key;
            result = completion.Result;
            _known.Remove(key);
            return true;
        }

        key = string.Empty;
        result = null;
        return false;
    }

    /// <summary>
    ///     Blocks until every in-flight resolution task has finished (or <paramref name="timeout" />
    ///     elapses). Call before disposing whatever the resolution delegate reads from, so a
    ///     background task can never touch a freed resource. Returns false on timeout.
    /// </summary>
    public bool WaitForDrain(TimeSpan timeout)
    {
        Task[] pending;
        lock (_inFlightLock)
        {
            pending = _inFlight.Where(static t => !t.IsCompleted).ToArray();
        }

        return pending.Length == 0 || Task.WaitAll(pending, timeout);
    }

    private void PruneInFlight()
    {
        lock (_inFlightLock)
        {
            _inFlight.RemoveAll(static t => t.IsCompleted);
        }
    }

    private readonly record struct Completion(string Key, TResult? Result);
}
