using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core.Orchestration;

/// <summary>
///     Tracks background tasks scoped to a session (a tab, a loaded file), replacing bare
///     fire-and-forget (<c>_ = SomethingAsync()</c>) calls — the pattern behind past
///     torn-collection heap corruption, where a populate task kept running against session state
///     that a new-file load had already torn down.
///     <para>
///         Work is invoked <b>synchronously on the calling thread</b>: an async UI method keeps its
///         DispatcherQueue affinity through its own awaits exactly as it does today — the runner
///         adds no thread hops. What it adds:
///     </para>
///     <list type="bullet">
///         <item>a session <see cref="Token" />, cancelled by <see cref="CancelAll" /> (work checks
///             it after awaits and before touching session state),</item>
///         <item>single-flight de-duplication per key — concurrent triggers of the same populate
///             share one task instead of racing,</item>
///         <item>exception capture: failures are logged via <see cref="Logger" /> and counted, never
///             thrown back through an event handler or lost,</item>
///         <item><see cref="CancelAllAndDrainAsync" /> — THE guard to await before disposing or
///             reopening session state, guaranteeing quiescence.</item>
///     </list>
///     <para>
///         <b>Drain must be awaited, never blocked on:</b> work continuations typically resume on
///         the calling (UI) thread, so synchronously waiting there would deadlock.
///     </para>
/// </summary>
internal sealed class SessionTaskRunner : ITrackableResource, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private ResourceRegistration? _registration;
    private CancellationTokenSource _cts = new();
    private bool _disposed;
    private long _started;
    private long _failures;
    private volatile string? _lastError;

    public SessionTaskRunner(string ownerName)
    {
        ResourceName = ownerName;
    }

    /// <summary>
    ///     Registers the runner with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the runner for fluent construction.
    /// </summary>
    public SessionTaskRunner RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    public string ResourceName { get; }

    public ResourceCategory Category => ResourceCategory.Queue;

    /// <summary>
    ///     The current session token. Replaced (with the old one cancelled) by
    ///     <see cref="CancelAll" />; work received it at start time, so cancelled work observes the
    ///     old token while new work gets the fresh one.
    /// </summary>
    public CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                return _cts.Token;
            }
        }
    }

    public ResourceStats GetStats() => new()
    {
        InFlight = RunningCount,
        Processed = Interlocked.Read(ref _started),
        Failures = Interlocked.Read(ref _failures),
        LastError = _lastError,
    };

    /// <summary>
    ///     Runs <paramref name="work" /> unless work with the same key is already running, in which
    ///     case the in-flight task is returned instead (single-flight). The returned task completes
    ///     when the work finishes and never faults: exceptions are logged and counted;
    ///     <see cref="OperationCanceledException" /> is swallowed as normal session teardown.
    /// </summary>
    public Task RunExclusiveAsync(string key, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        TaskCompletionSource completion;
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (_running.TryGetValue(key, out var existing))
            {
                return existing;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running[key] = completion.Task;
            token = _cts.Token;
        }

        Interlocked.Increment(ref _started);
        // The synchronous head of `work` runs right here on the calling thread (UI affinity is
        // preserved); the discarded wrapper task never faults — all failure paths are handled inside.
        _ = ExecuteAsync(key, work, completion, token);
        return completion.Task;
    }

    /// <summary>Fire-and-forget form of <see cref="RunExclusiveAsync" /> for event handlers.</summary>
    public void Post(string key, Func<CancellationToken, Task> work) => _ = RunExclusiveAsync(key, work);

    public bool IsRunning(string key)
    {
        lock (_gate)
        {
            return _running.TryGetValue(key, out var task) && !task.IsCompleted;
        }
    }

    /// <summary>
    ///     Cancels all tracked work and installs a fresh token. The runner stays usable — this is
    ///     the new-file-load path, where the old session's work must stop but the tab lives on.
    /// </summary>
    public void CancelAll()
    {
        CancellationTokenSource toCancel;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            toCancel = _cts;
            _cts = new CancellationTokenSource();
        }

        // Cancel outside the lock: registered callbacks run synchronously on Cancel and must not
        // observe (or deadlock against) the runner's internal state mid-update.
        toCancel.Cancel();
    }

    /// <summary>
    ///     Cancels all tracked work, then awaits quiescence of every tracked task — THE guard to
    ///     await before disposing or reopening session state, so no background task can touch
    ///     torn-down collections. Await it; never block on it from the UI thread.
    /// </summary>
    public async Task CancelAllAndDrainAsync()
    {
        CancelAll();
        while (true)
        {
            Task[] pending;
            lock (_gate)
            {
                pending = _running.Values.Where(static t => !t.IsCompleted).ToArray();
            }

            if (pending.Length == 0)
            {
                return;
            }

            // Wrapper tasks never fault (see ExecuteAsync), so WhenAll cannot throw.
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Cancels outstanding work and rejects new work. Does not block on in-flight tasks — await
    ///     <see cref="CancelAllAndDrainAsync" /> first when teardown ordering matters.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource toCancel;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            toCancel = _cts;
        }

        toCancel.Cancel();
        _registration?.Dispose();
    }

    private int RunningCount
    {
        get
        {
            lock (_gate)
            {
                return _running.Count(static pair => !pair.Value.IsCompleted);
            }
        }
    }

    private async Task ExecuteAsync(
        string key,
        Func<CancellationToken, Task> work,
        TaskCompletionSource completion,
        CancellationToken token)
    {
        try
        {
            await work(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Session cancelled — expected during teardown or a new-file load.
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failures);
            _lastError = ex.Message;
            Logger.Instance.Error("{0}: background task '{1}' failed: {2}", ResourceName, key, ex);
        }
        finally
        {
            lock (_gate)
            {
                if (_running.TryGetValue(key, out var current) && ReferenceEquals(current, completion.Task))
                {
                    _running.Remove(key);
                }
            }

            completion.TrySetResult();
        }
    }
}
