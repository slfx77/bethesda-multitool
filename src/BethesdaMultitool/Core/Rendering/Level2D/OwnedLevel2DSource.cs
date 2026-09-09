namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>A preview that owns the resource its source uses for deferred reads.</summary>
internal sealed class OwnedLevel2DSource : ILevel2DSource, IDisposable
{
    private readonly ILevel2DSource _source;
    private IDisposable? _resource;

    private OwnedLevel2DSource(ILevel2DSource source, IDisposable resource)
    {
        _source = source;
        _resource = resource;
    }

    public IReadOnlyList<Level2DLayer> Layers => _source.Layers;

    public string DisplayName => _source.DisplayName;

    /// <summary>
    ///     Takes ownership of <paramref name="resource" />, releasing it if opening fails or returns
    ///     null. A successful preview keeps it alive until the preview is replaced or discarded.
    /// </summary>
    public static ILevel2DSource? Create(IDisposable resource, Func<ILevel2DSource?> open)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(open);
        var transferred = false;
        try
        {
            var source = open();
            if (source is null)
            {
                return null;
            }

            var owned = new OwnedLevel2DSource(source, resource);
            transferred = true;
            return owned;
        }
        finally
        {
            if (!transferred)
            {
                resource.Dispose();
            }
        }
    }

    public Level2DRender? Render(Level2DLayer layer)
    {
        ObjectDisposedException.ThrowIf(_resource is null, this);
        return _source.Render(layer);
    }

    public void Dispose()
    {
        var resource = Interlocked.Exchange(ref _resource, null);
        if (resource is null)
        {
            return;
        }

        try
        {
            (_source as IDisposable)?.Dispose();
        }
        finally
        {
            resource.Dispose();
        }
    }
}
