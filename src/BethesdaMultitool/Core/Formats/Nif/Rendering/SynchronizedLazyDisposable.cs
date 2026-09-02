namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Owns a disposable resource that is created on first use. Creation, use, and disposal share one
///     gate so a long-running caller cannot race disposal and a resource created concurrently with
///     disposal cannot be stranded.
/// </summary>
internal sealed class SynchronizedLazyDisposable<T> : IDisposable where T : class, IDisposable
{
    private readonly Func<T> _factory;
    private readonly object _gate = new();
    private bool _disposed;
    private bool _isValueCreated;
    private T? _value;

    internal SynchronizedLazyDisposable(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    internal TResult Use<TResult>(Func<T, TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SynchronizedLazyDisposable<T>));
            }

            if (!_isValueCreated)
            {
                _value = _factory();
                _isValueCreated = true;
            }

            return action(_value!);
        }
    }

    public void Dispose()
    {
        T? value;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            value = _isValueCreated ? _value : null;
            _value = null;
        }

        value?.Dispose();
    }
}
