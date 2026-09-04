// Ported verbatim (namespace and doc-comment style aside) from JimmyPCTool / AweMultitool
//   (https://github.com/slfx77/JimmyPCTool, MIT License) — src/AweMultitool/Core/Concurrency/LatestOnlyJob.cs.
//   License texts are collected centrally in THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.Concurrency;

/// <summary>
///     Runs one background job at a time on behalf of a consumer, and guarantees a superseded job's
///     result is never applied.
///     <para>
///         The replacement is published <b>before</b> the previous operation is cancelled. Each
///         invocation owns and disposes its own token source, so no newer invocation may dispose
///         state an older invocation is still inspecting.
///     </para>
///     <para>
///         This is the shape every "selection changed, go load the thing" handler needs: without it
///         a slow earlier load can land after a fast later one and leave the pane showing the wrong
///         item. Kept in <c>Core/</c> because it has no WinUI dependency and <c>App/**</c> is
///         excluded from the <c>net10.0</c> target framework, so only here can it be tested.
///     </para>
/// </summary>
internal sealed class LatestOnlyJob : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;
    private bool _disposed;

    /// <summary>
    ///     Runs <paramref name="work" /> off the calling thread, then invokes
    ///     <paramref name="apply" /> on the captured context — but only if no newer job has started
    ///     in the meantime.
    /// </summary>
    public async Task RunAsync<T>(Func<CancellationToken, T> work, Action<T> apply)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(apply);

        CancellationTokenSource cts;
        CancellationToken token;

        // S6966 (await CancelAsync) does not apply inside a lock: awaiting there is illegal, and the
        // publish-then-cancel ordering below is only correct while both happen under one gate.
#pragma warning disable S6966
        lock (_gate)
        {
            cts = new CancellationTokenSource();
            token = cts.Token;

            // Publish first: an older continuation must see the replacement before its cancellation
            // callback can let that continuation resume.
            var previous = _current;
            _current = cts;
            previous?.Cancel();
            if (_disposed)
            {
                cts.Cancel();
            }
        }
#pragma warning restore S6966

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
                }
            }
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

    private bool IsCurrent(CancellationTokenSource cts)
    {
        lock (_gate)
        {
            return ReferenceEquals(_current, cts);
        }
    }
}
