using System.Runtime.ExceptionServices;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Runs CPU work on the thread pool while retaining at most one incomplete operation. Enqueuing
///     the next item first drains and commits the previous result, which lets a caller do unrelated
///     producer work between enqueues without allowing an unbounded task or buffer backlog.
/// </summary>
/// <typeparam name="T">The result committed on the caller's synchronization context.</typeparam>
internal sealed class SingleOutstandingOperation<T>
{
    private Task<T>? _pending;

    /// <summary>True while one background operation still needs to be observed and committed.</summary>
    public bool HasPending => _pending is not null;

    /// <summary>
    ///     Drains the previous operation, checks whether the caller still admits new work, then starts
    ///     <paramref name="operation" /> on the thread pool. The operation deliberately receives no
    ///     cancellation token: once ownership of an input buffer is handed off, it must finish and be
    ///     observed before teardown rather than continue as unobserved background I/O.
    /// </summary>
    public async Task EnqueueAsync(
        Func<T> operation,
        Action<T> commit,
        CancellationToken admissionToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(commit);

        await DrainAsync(commit);
        admissionToken.ThrowIfCancellationRequested();
        _pending = Task.Run(operation, CancellationToken.None);
    }

    /// <summary>Awaits and commits the outstanding result, if any, on the caller's context.</summary>
    public async Task DrainAsync(Action<T> commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var pending = _pending;
        if (pending is null)
        {
            return;
        }

        // Clear before awaiting so a fault is still observed exactly once and a recovery/drain path
        // cannot accidentally await or commit the same operation twice.
        _pending = null;
        var result = await pending;
        commit(result);
    }

    /// <summary>
    ///     Observes the outstanding operation after producer failure, reports a secondary drain fault,
    ///     and rethrows the original exception with its stack intact. This prevents background I/O from
    ///     escaping teardown without letting a later save failure hide the primary render/cancel cause.
    /// </summary>
    public async Task DrainAndRethrowAsync(
        Exception primaryException,
        Action<T> commit,
        Action<Exception>? reportSecondaryException = null)
    {
        ArgumentNullException.ThrowIfNull(primaryException);
        ArgumentNullException.ThrowIfNull(commit);

        try
        {
            await DrainAsync(commit);
        }
        catch (Exception secondaryException)
        {
            reportSecondaryException?.Invoke(secondaryException);
        }

        ExceptionDispatchInfo.Capture(primaryException).Throw();
    }
}
