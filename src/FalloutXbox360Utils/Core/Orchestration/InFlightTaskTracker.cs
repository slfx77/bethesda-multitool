namespace FalloutXbox360Utils.Core.Orchestration;

/// <summary>
///     Tracks background tasks started by an owner so it can drain them before tearing down whatever
///     they read — the shared shape behind the streaming caches' decode-task lists. Add tasks as
///     they start, <see cref="PruneCompleted" /> opportunistically, and call one of the drain
///     methods before disposing shared state so a background task can never touch a freed resource.
/// </summary>
internal sealed class InFlightTaskTracker
{
    private readonly string _ownerName;
    private readonly List<Task> _tasks = [];
    private readonly Lock _gate = new();

    public InFlightTaskTracker(string ownerName)
    {
        _ownerName = ownerName;
    }

    /// <summary>Tracked tasks that have not yet completed.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _tasks.Count(static t => !t.IsCompleted);
            }
        }
    }

    public void Add(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            _tasks.Add(task);
        }
    }

    public void PruneCompleted()
    {
        lock (_gate)
        {
            _tasks.RemoveAll(static t => t.IsCompleted);
        }
    }

    /// <summary>Blocks until every tracked task has finished, including ones added while waiting.</summary>
    public void WaitForDrain()
    {
        while (true)
        {
            var pending = SnapshotPending();
            if (pending.Length == 0)
            {
                return;
            }

            Task.WaitAll(pending);
            PruneCompleted();
        }
    }

    /// <summary>Blocks until every tracked task has finished or the timeout elapses. Returns false on timeout.</summary>
    public bool WaitForDrain(TimeSpan timeout)
    {
        var pending = SnapshotPending();
        return pending.Length == 0 || Task.WaitAll(pending, timeout);
    }

    /// <summary>
    ///     Drains like <see cref="WaitForDrain()" /> but logs each faulted task instead of letting
    ///     the fault propagate — the dispose-path variant, where a failed background task must not
    ///     turn teardown into a crash. Waits over every tracked task (not just incomplete ones), so
    ///     already-faulted tasks are observed and logged too instead of surfacing later as
    ///     unobserved task exceptions.
    /// </summary>
    public void WaitForDrainLogged()
    {
        while (true)
        {
            Task[] tracked;
            lock (_gate)
            {
                tracked = [.. _tasks];
            }

            if (tracked.Length == 0)
            {
                return;
            }

            try
            {
                Task.WaitAll(tracked);
            }
            catch (AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    Logger.Instance.Warn("{0}: in-flight task failed during drain: {1}", _ownerName, inner.Message);
                }
            }

            lock (_gate)
            {
                _tasks.RemoveAll(static t => t.IsCompleted);
                if (_tasks.Count == 0)
                {
                    return;
                }
            }
        }
    }

    private Task[] SnapshotPending()
    {
        lock (_gate)
        {
            return _tasks.Where(static t => !t.IsCompleted).ToArray();
        }
    }
}
