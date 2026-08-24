namespace BethesdaMultitool.Core.Orchestration;

/// <summary>
///     Data-parallel loops that are safe to call from the STA UI thread — the non-pumping
///     counterpart to <see cref="System.Threading.Tasks.Parallel" />.
///     <para>
///         <b>Why this exists.</b> <c>Parallel.For</c> / <c>Parallel.ForEach</c> BLOCK the calling
///         thread until every partition finishes, and they do it through
///         <c>Task.InternalWait</c> → <c>ManualResetEventSlim.Wait</c> → <c>Monitor.Wait</c>. On an
///         STA thread that is a COM pumping wait: the pump can dispatch a pending input message back
///         into XAML while the dispatcher is inside a reentrancy-protected callback, and
///         <c>CXcpDispatcher::CheckReentrancy</c> <b>fail-fasts the process</b> (0xc000027b, stowed
///         exception at <c>Microsoft.UI.Xaml.dll+0x3ace5d</c>) — bypassing every managed exception
///         handler, which is why such a crash leaves zero <c>[ERR]</c> lines behind.
///     </para>
///     <para>
///         This is the same hazard as a raw <see cref="Task.Wait()" />, but it does not LOOK like a
///         wait, which is how a <c>Parallel.For</c> in the per-frame reference cull survived a sweep
///         that searched for <c>.Wait</c>/<c>.Result</c>/<c>GetResult</c>/<c>WaitOne</c> and shipped
///         a recurring crash for over two weeks. Treat <c>Parallel.*</c> as a blocking wait.
///     </para>
///     <para>
///         Semantics match <c>Parallel</c> closely enough to be a drop-in for the render paths:
///         the calling thread runs the FIRST partition itself (as <c>Parallel</c> does, so its core
///         is not idle), the rest go to the thread pool, and the join spins briefly before falling
///         back to <see cref="NonPumpingWait" />'s 1 ms non-alertable sleep — the spin matters
///         because these loops finish in single-digit milliseconds, where a pure 1 ms sleep
///         granularity would dominate. Exceptions from every partition are aggregated and rethrown
///         after all of them complete, so no partition is left running against state the caller is
///         about to reuse.
///     </para>
/// </summary>
internal static class NonPumpingParallel
{
    /// <summary>
    ///     Runs <paramref name="body" /> for each index in
    ///     <c>[fromInclusive, toExclusive)</c>, one partition per index. Callers that already chunk
    ///     their own work (the reference cull partitions cells itself) map one index to one chunk.
    /// </summary>
    public static void For(int fromInclusive, int toExclusive, Action<int> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var count = toExclusive - fromInclusive;
        if (count <= 0)
        {
            return;
        }

        if (count == 1)
        {
            body(fromInclusive);
            return;
        }

        var tasks = new Task[count - 1];
        for (var i = 1; i < count; i++)
        {
            var index = fromInclusive + i;
            tasks[i - 1] = Task.Run(() => body(index));
        }

        List<Exception>? failures = null;
        try
        {
            // The caller's own core does real work instead of spinning on the join below.
            body(fromInclusive);
        }
        catch (Exception ex)
        {
            failures = [ex];
        }

        JoinAndThrow(tasks, failures);
    }

    /// <summary>
    ///     Runs <paramref name="body" /> over <paramref name="items" /> with at most
    ///     <paramref name="maxConcurrency" /> workers, claiming items with an interlocked cursor
    ///     (self-balancing, so one slow item cannot strand a whole static range).
    /// </summary>
    public static void ForEach<T>(IReadOnlyList<T> items, int maxConcurrency, Action<T> body)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(body);
        if (items.Count == 0)
        {
            return;
        }

        var workers = Math.Clamp(maxConcurrency, 1, items.Count);
        if (workers == 1)
        {
            foreach (var item in items)
            {
                body(item);
            }

            return;
        }

        var cursor = -1;
        void Drain()
        {
            while (true)
            {
                var index = Interlocked.Increment(ref cursor);
                if (index >= items.Count)
                {
                    return;
                }

                body(items[index]);
            }
        }

        var tasks = new Task[workers - 1];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Run(Drain);
        }

        List<Exception>? failures = null;
        try
        {
            Drain();
        }
        catch (Exception ex)
        {
            failures = [ex];
        }

        JoinAndThrow(tasks, failures);
    }

    /// <summary>
    ///     Waits for every pool partition WITHOUT pumping, then rethrows anything that failed.
    ///     Always waits — even when the inline partition already threw — because a partition still
    ///     running would keep writing into state the caller is about to read or reuse.
    /// </summary>
    private static void JoinAndThrow(Task[] tasks, List<Exception>? failures)
    {
        // Bounded pure spin first (Thread.SpinWait only — never a wait handle, so never a pump).
        var spinner = new SpinWait();
        while (!AllCompleted(tasks) && !spinner.NextSpinWillYield)
        {
            spinner.SpinOnce();
        }

        NonPumpingWait.WaitAll(tasks);

        foreach (var task in tasks)
        {
            // NonPumpingWait never throws on a faulted task — observe each one explicitly or a
            // partition failure would vanish silently.
            if (task.Exception is { } aggregate)
            {
                (failures ??= []).AddRange(aggregate.InnerExceptions);
            }
        }

        if (failures is { Count: > 0 })
        {
            throw new AggregateException(failures);
        }
    }

    private static bool AllCompleted(Task[] tasks)
    {
        foreach (var task in tasks)
        {
            if (!task.IsCompleted)
            {
                return false;
            }
        }

        return true;
    }
}
