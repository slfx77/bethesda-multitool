namespace BethesdaMultitool.Core.Orchestration;

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

    /// <summary>
    ///     Blocks until every tracked task has finished, including ones added while waiting. Faulted
    ///     tasks surface as an <see cref="AggregateException" />, as with <c>Task.WaitAll</c>.
    /// </summary>
    public void WaitForDrain()
    {
        while (true)
        {
            var pending = SnapshotPending();
            if (pending.Length == 0)
            {
                return;
            }

            // Non-pumping: drains run on the UI thread before teardown, where Task.WaitAll's STA
            // pumping wait can re-enter XAML and fail-fast (see NonPumpingWait). It never throws,
            // so faults are gathered and rethrown explicitly to keep the WaitAll contract.
            NonPumpingWait.WaitAll(pending);
            ThrowIfAnyFailed(pending);
            PruneCompleted();
        }
    }

    /// <summary>
    ///     Blocks until every tracked task has finished or the timeout elapses. Returns false on
    ///     timeout; faulted tasks surface as an <see cref="AggregateException" />.
    /// </summary>
    public bool WaitForDrain(TimeSpan timeout)
    {
        var pending = SnapshotPending();
        if (pending.Length == 0)
        {
            return true;
        }

        if (!NonPumpingWait.WaitAll(pending, timeout))
        {
            return false;
        }

        ThrowIfAnyFailed(pending);
        return true;
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

            NonPumpingWait.WaitAll(tracked);
            foreach (var task in tracked)
            {
                if (task.Exception is { } aggregate) // observes the fault — never an unobserved-task exception
                {
                    foreach (var inner in aggregate.Flatten().InnerExceptions)
                    {
                        Logger.Instance.Warn("{0}: in-flight task failed during drain: {1}", _ownerName, inner.Message);
                    }
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

    /// <summary>Rethrows faults/cancellations of completed tasks, mirroring <c>Task.WaitAll</c>.</summary>
    private static void ThrowIfAnyFailed(Task[] tasks)
    {
        List<Exception>? failures = null;
        foreach (var task in tasks)
        {
            if (task.Exception is { } aggregate)
            {
                failures ??= [];
                failures.AddRange(aggregate.Flatten().InnerExceptions);
            }
            else if (task.IsCanceled)
            {
                failures ??= [];
                failures.Add(new TaskCanceledException(task));
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(failures);
        }
    }
}
