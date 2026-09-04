namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

/// <summary>
///     Serializes access to the mutable caches owned by one <see cref="NpcBrowserService" />.
///     A lease must remain on the thread that acquired it; browser composition operations are
///     synchronous even when their callers dispatch them to a worker thread.
/// </summary>
internal sealed class NpcBrowserOperationGate
{
    private readonly object _sync = new();
    private bool _disposed;

    public IDisposable Enter()
    {
        Monitor.Enter(_sync);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new Lease(_sync);
        }
        catch
        {
            Monitor.Exit(_sync);
            throw;
        }
    }

    public void DisposeResources(Action disposeResources)
    {
        ArgumentNullException.ThrowIfNull(disposeResources);

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        disposeResources();
    }

    private sealed class Lease(object sync) : IDisposable
    {
        private object? _sync = sync;

        public void Dispose()
        {
            var sync = Interlocked.Exchange(ref _sync, null);
            if (sync is not null)
            {
                Monitor.Exit(sync);
            }
        }
    }
}
