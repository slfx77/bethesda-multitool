using System.Diagnostics;
using System.Globalization;
using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core.Orchestration;

/// <summary>Progress of a named parallel run. <see cref="Total" /> is -1 when the source size is unknown.</summary>
internal readonly record struct WorkProgress(int Completed, int Total);

/// <summary>
///     Thin, named wrappers over <see cref="Parallel" /> — the standard way to run parallel batch
///     work. Each run resolves its degree of parallelism through a <see cref="ConcurrencyPolicy" />,
///     logs start/finish with elapsed time at debug level, registers a transient row in the
///     <see cref="ResourceRegistry" /> for the duration (so long runs are visible in the diagnostics
///     panel), and reports per-item progress through the caller's <see cref="IProgress{T}" />.
///     Exceptions propagate unchanged, exactly as with the raw <see cref="Parallel" /> APIs.
///     <para>
///         The fan-out idiom for heterogeneous concurrent phases (e.g. the minidump scan
///         coordinator) is <see cref="RunNamedAsync" /> per task + <c>Task.WhenAll</c> barrier +
///         sequential post-processing — deliberately a documented idiom, not a class.
///     </para>
/// </summary>
internal static class ParallelWork
{
    /// <summary>
    ///     Runs <paramref name="body" /> over <paramref name="source" /> in parallel at the policy's degree, with named
    ///     timing logs and a transient diagnostics row.
    /// </summary>
    public static void ForEach<T>(
        string name,
        IEnumerable<T> source,
        ConcurrencyPolicy policy,
        Action<T> body,
        IProgress<WorkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var degree = policy.Resolve();
        using var run = new RunScope(name, degree, TryCount(source));
        Parallel.ForEach(
            source,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
            item =>
            {
                run.OnItemStarted();
                try
                {
                    body(item);
                }
                finally
                {
                    run.OnItemFinished(progress);
                }
            });
    }

    /// <summary>
    ///     Async counterpart to <see cref="ForEach" />: awaits <paramref name="body" /> over <paramref name="source" />
    ///     in parallel at the policy's degree.
    /// </summary>
    public static async Task ForEachAsync<T>(
        string name,
        IEnumerable<T> source,
        ConcurrencyPolicy policy,
        Func<T, CancellationToken, ValueTask> body,
        IProgress<WorkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var degree = policy.Resolve();
        using var run = new RunScope(name, degree, TryCount(source));
        await Parallel.ForEachAsync(
            source,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
            async (item, ct) =>
            {
                run.OnItemStarted();
                try
                {
                    await body(item, ct).ConfigureAwait(false);
                }
                finally
                {
                    run.OnItemFinished(progress);
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    ///     Runs <paramref name="body" /> over the integer range [<paramref name="fromInclusive" />,
    ///     <paramref name="toExclusive" />) in parallel at the policy's degree.
    /// </summary>
    public static void For(
        string name,
        int fromInclusive,
        int toExclusive,
        ConcurrencyPolicy policy,
        Action<int> body,
        CancellationToken cancellationToken = default)
    {
        var degree = policy.Resolve();
        using var run = new RunScope(name, degree, Math.Max(0, toExclusive - fromInclusive));
        Parallel.For(
            fromInclusive,
            toExclusive,
            new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = cancellationToken },
            index =>
            {
                run.OnItemStarted();
                try
                {
                    body(index);
                }
                finally
                {
                    run.OnItemFinished(null);
                }
            });
    }

    /// <summary>
    ///     Named <c>Task.Run</c> with debug timing — the building block of the fan-out + barrier +
    ///     sequential-post idiom. Exceptions propagate through the returned task unchanged.
    /// </summary>
    public static Task RunNamedAsync(string name, Action work, CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () =>
            {
                var log = Logger.Instance;
                log.Debug("{0}: started.", name);
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    work();
                }
                finally
                {
                    log.Debug("{0}: finished in {1:N0} ms.", name, stopwatch.Elapsed.TotalMilliseconds);
                }
            },
            cancellationToken);
    }

    private static int TryCount<T>(IEnumerable<T> source)
    {
        return source.TryGetNonEnumeratedCount(out var count) ? count : -1;
    }

    /// <summary>
    ///     Per-run scope: debug logs, elapsed time, and a transient registry row reporting in-flight
    ///     and processed counts while the run is active.
    /// </summary>
    private sealed class RunScope : ITrackableResource, IDisposable
    {
        private readonly ResourceRegistration _registration;
        private readonly Stopwatch _stopwatch;
        private readonly int _total;
        private int _completed;
        private int _inFlight;

        internal RunScope(string name, int degree, int total)
        {
            ResourceName = $"ParallelWork:{name}";
            _total = total;
            _stopwatch = Stopwatch.StartNew();
            Logger.Instance.Debug("{0}: started (dop {1}, {2} items).", ResourceName, degree,
                total < 0 ? "?" : total.ToString(CultureInfo.InvariantCulture));
            _registration = ResourceRegistry.Instance.Register(this);
        }

        public void Dispose()
        {
            _registration.Dispose();
            Logger.Instance.Debug(
                "{0}: finished {1:N0} items in {2:N0} ms.",
                ResourceName, Volatile.Read(ref _completed), _stopwatch.Elapsed.TotalMilliseconds);
        }

        public string ResourceName { get; }

        public ResourceCategory Category => ResourceCategory.Queue;

        public ResourceStats GetStats()
        {
            return new ResourceStats
            {
                EntryCount = _total < 0 ? 0 : _total,
                InFlight = Volatile.Read(ref _inFlight),
                Processed = Volatile.Read(ref _completed)
            };
        }

        internal void OnItemStarted()
        {
            Interlocked.Increment(ref _inFlight);
        }

        internal void OnItemFinished(IProgress<WorkProgress>? progress)
        {
            Interlocked.Decrement(ref _inFlight);
            var completed = Interlocked.Increment(ref _completed);
            progress?.Report(new WorkProgress(completed, _total));
        }
    }
}
