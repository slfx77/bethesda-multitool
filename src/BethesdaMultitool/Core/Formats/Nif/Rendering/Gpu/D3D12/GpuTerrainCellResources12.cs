using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Retains one terrain cell's exact arena range before upload and until retired release succeeds.</summary>
/// <remarks>
///     Attach this initially empty owner to the existing residency entry before acquiring geometry. The producer
///     transfers its range before native copies; the caller supplies submission and retirement ordering. This owner
///     does not publish a cache entry, hold textures, submit work or establish GPU completion.
/// </remarks>
internal sealed class GpuTerrainCellResources12 : IDisposable
{
    private readonly RetiredResourceDisposal _cleanup = new();
    private readonly Action<TerrainAllocation12> _freeGeometry;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private TerrainAllocation12 _geometry;
    private bool _hasGeometry;
    private bool _acquisitionAttempted;
    private bool _acquisitionCompleted;
    private bool _retiring;
    private bool _busy;

    /// <summary>Registers range cleanup before any acquisition or native work can occur.</summary>
    /// <param name="freeGeometry">Returns the exact retired range; failed calls must leave its release retryable.</param>
    /// <exception cref="ArgumentNullException">The release callback is null.</exception>
    internal GpuTerrainCellResources12(Action<TerrainAllocation12> freeGeometry)
    {
        ArgumentNullException.ThrowIfNull(freeGeometry);
        _freeGeometry = freeGeometry;
        _cleanup.Add(ReleaseGeometry, "terrain cell geometry range");
    }

    /// <summary>Gets completed geometry views while this owner has not begun retirement.</summary>
    /// <exception cref="InvalidOperationException">Acquisition has not completed, access is on another thread, or a transition is active.</exception>
    /// <exception cref="ObjectDisposedException">Retirement has begun.</exception>
    internal TerrainAllocation12 Geometry
    {
        get
        {
            VerifyAccess();
            ObjectDisposedException.ThrowIf(_retiring, this);
            if (!_acquisitionCompleted) throw new InvalidOperationException("Terrain geometry acquisition is incomplete.");
            return _geometry;
        }
    }

    /// <summary>Gets whether a transferred range still awaits successful release; this diagnostic grants no borrow.</summary>
    internal bool HasGeometry => _hasGeometry;

    /// <summary>Gets whether actual retired cleanup completed, rather than merely being requested or queued.</summary>
    internal bool IsReleased => _retiring && !_cleanup.HasPending;

    /// <summary>Retains one exact range before fallible upload work, then accepts its completed native views.</summary>
    /// <param name="acquire">Synchronous producer receiving a one-use ownership callback. After the callback succeeds,
    /// this owner retains the range even if the producer throws; the producer must not release it independently.</param>
    /// <returns>Completed views identifying the same exact range accepted by the callback.</returns>
    /// <exception cref="ArgumentNullException">The producer is null.</exception>
    /// <exception cref="InvalidOperationException">Acquisition was already attempted, retirement started, access is invalid,
    /// the callback runs late or more than once, or completion does not identify the retained allocation.</exception>
    /// <remarks>Producer exceptions propagate unchanged and end acquisition permanently. Disposal still requires
    /// the caller to prove retirement of any native work the producer may already have recorded.</remarks>
    internal TerrainAllocation12 AcquireGeometry(Func<Action<TerrainAllocation12>, TerrainAllocation12> acquire)
    {
        VerifyAccess();
        if (_retiring || _acquisitionAttempted)
            throw new InvalidOperationException("Terrain geometry acquisition has already ended.");
        ArgumentNullException.ThrowIfNull(acquire);
        _acquisitionAttempted = true;
        var acceptingAllocation = true;
        _busy = true;
        try
        {
            var completed = acquire(geometry =>
            {
                if (Environment.CurrentManagedThreadId != _threadId || !acceptingAllocation || _retiring)
                    throw new InvalidOperationException("Terrain ownership must transfer synchronously on its creating thread.");
                if (_hasGeometry) throw new InvalidOperationException("Terrain geometry was already retained.");
                _geometry = geometry;
                _hasGeometry = true;
            });
            if (!_hasGeometry) throw new InvalidOperationException("Terrain acquisition did not transfer its allocation.");
            if (completed.Allocation != _geometry.Allocation)
                throw new InvalidOperationException("Completed terrain geometry differs from the retained allocation.");
            _geometry = completed;
            _acquisitionCompleted = true;
            return completed;
        }
        finally
        {
            acceptingAllocation = false;
            _busy = false;
        }
    }

    /// <summary>Releases the caller-proven retired range, retaining any failed release for a later attempt.</summary>
    /// <exception cref="AggregateException">Range release failed and remains pending in Shared cleanup.</exception>
    /// <exception cref="InvalidOperationException">Access is on another thread or reenters acquisition or release.</exception>
    public void Dispose()
    {
        VerifyAccess();
        _retiring = true;
        _busy = true;
        try { _cleanup.Dispose(); }
        finally { _busy = false; }
    }

    /// <summary>Returns the captured exact range once, clearing ownership only after the arena accepts its release.</summary>
    private void ReleaseGeometry()
    {
        if (!_hasGeometry) return;
        _freeGeometry(_geometry);
        _hasGeometry = false;
    }

    /// <summary>Rejects cross-thread access and callback reentry before any lifetime state can change.</summary>
    /// <exception cref="InvalidOperationException">The calling thread or current transition cannot access this owner.</exception>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId || _busy)
            throw new InvalidOperationException("Terrain resource lifetime belongs to its creating thread and cannot be reentered.");
    }
}
