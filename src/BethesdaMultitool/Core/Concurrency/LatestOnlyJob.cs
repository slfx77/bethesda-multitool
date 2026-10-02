// Ported from JimmyPCTool / AweMultitool,
// src/AweMultitool/Core/Concurrency/LatestOnlyJob.cs. Copied essentially verbatim — the ordering
// this class encodes is subtle and was arrived at by fixing real races, so it is deliberately NOT
// re-derived here. Adapted with an optional discard callback for resource-owning preview results.

namespace BethesdaMultitool.Core.Concurrency;

/// <summary>
///     Runs one background job at a time on behalf of a consumer, and guarantees a superseded job's
///     result is never applied.
///     <para>
///         The replacement is published <b>before</b> the previous operation is cancelled. Each
///         invocation owns and disposes its own token source, so no newer invocation can dispose
///         state an older invocation is still inspecting.
///     </para>
///     <para>
///         Use this wherever a selection change kicks off async work whose result writes to shared
///         UI state — asset-tree selection, thumbnail decode, preview load. Without it a slow
///         earlier decode can land after a faster later one and leave the wrong item on screen.
///     </para>
/// </summary>
internal sealed class LatestOnlyJob : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;
    private bool _disposed;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var current = _current;
            _current = null;
            current?.Cancel();
        }
    }

    /// <summary>
    ///     Runs <paramref name="work" /> off the calling thread, then invokes <paramref name="apply" />
    ///     on the captured context — but only if no newer job has started in the meantime.
    ///     When a completed result is rejected, <paramref name="discard" /> releases any resources
    ///     it owns. Applied results transfer ownership to <paramref name="apply" /> instead.
    /// </summary>
    public async Task RunAsync<T>(Func<CancellationToken, T> work, Action<T> apply, Action<T>? discard = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(apply);

        CancellationTokenSource cts;
        CancellationToken token;
        lock (_gate)
        {
            cts = new CancellationTokenSource();
            token = cts.Token;

            // Publish first: an older continuation must see the replacement before its cancellation
            // callback can let that continuation resume.
            var previous = _current;
            _current = cts;
#pragma warning disable S6966 // Cancel synchronously under the gate before an invocation can dispose its token source.
            previous?.Cancel();
            if (_disposed)
            {
                cts.Cancel();
            }
#pragma warning restore S6966
        }

        try
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var result = await Task.Run(() => work(token), token);

            lock (_gate)
            {
                if (!token.IsCancellationRequested && ReferenceEquals(_current, cts))
                {
                    apply(result);
                    return;
                }
            }

            discard?.Invoke(result);
        }
        catch (OperationCanceledException)
        {
            // A superseded selection is not an error; anything else reaches the caller's handler.
        }
        catch (Exception) when (token.IsCancellationRequested || !IsCurrent(cts))
        {
            // Work that does not observe cancellation can still fail after it has been replaced.
            // That failure belongs to the superseded request and must not disturb the current UI.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, cts))
                {
                    _current = null;
                }

                cts.Dispose();
            }
        }
    }

    /// <summary>Cancels whatever is running without starting anything new.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            var current = _current;
            _current = null;
            current?.Cancel();
        }
    }

    private bool IsCurrent(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            return ReferenceEquals(_current, cts);
        }
    }
}
