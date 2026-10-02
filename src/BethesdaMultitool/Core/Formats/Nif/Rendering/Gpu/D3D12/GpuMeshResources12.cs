using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Retains a materialized mesh's arena range and each acquired texture reference until retired cleanup succeeds.</summary>
/// <remarks>The deletion queue retains rollback before acquisition starts. Successful materialization transfers
/// this owner to the mesh. Dispose requires independent proof that every GPU use has retired.</remarks>
internal sealed class GpuMeshResources12 : IDisposable
{
    private readonly RetiredResourceDisposal _cleanup = new();
    private readonly Action<GeometryAllocation12> _freeGeometry;
    private readonly Action<GpuTextureCache12.Entry?> _releaseTexture;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private GeometryAllocation12 _geometry;
    private bool _hasGeometry;
    private bool _committed;
    private bool _retiring;
    private bool _busy;

    /// <summary>Registers geometry cleanup before any native acquisition can occur.</summary>
    private GpuMeshResources12(Action<GeometryAllocation12> freeGeometry, Action<GpuTextureCache12.Entry?> releaseTexture)
    {
        _freeGeometry = freeGeometry;
        _releaseTexture = releaseTexture;
        _cleanup.Add(ReleaseGeometry, "mesh geometry range");
    }

    /// <summary>Gets the exact acquired range, without transferring its ownership.</summary>
    internal GeometryAllocation12 Geometry => _geometry;

    /// <summary>Gets whether actual retired cleanup has completed, rather than merely being queued.</summary>
    internal bool IsReleased => _retiring && !_cleanup.HasPending;

    /// <summary>Creates an empty resource ledger for attachment to a residency entry before acquisition.</summary>
    /// <param name="freeGeometry">Returns the exact range only after all uses have retired.</param>
    /// <param name="releaseTexture">Returns one independently acquired texture reference.</param>
    /// <returns>An empty owner; the caller must attach it and register rollback before acquiring anything.</returns>
    internal static GpuMeshResources12 CreateForResidency(
        Action<GeometryAllocation12> freeGeometry, Action<GpuTextureCache12.Entry?> releaseTexture)
    {
        ArgumentNullException.ThrowIfNull(freeGeometry);
        ArgumentNullException.ThrowIfNull(releaseTexture);
        return new GpuMeshResources12(freeGeometry, releaseTexture);
    }

    /// <summary>Retains rollback in the established frame-retirement owner before allocating geometry or textures.</summary>
    /// <param name="queue">Queue whose ticks are synchronized with the application's GPU fences.</param>
    /// <param name="freeGeometry">Returns an arena range after retirement; must support retry on failure.</param>
    /// <param name="releaseTexture">Returns one acquired reference; equal entries remain distinct acquisitions.</param>
    /// <returns>An acquisition owner that must be committed only after its mesh is ready to return.</returns>
    internal static GpuMeshResources12 Begin(GpuDeletionQueue12 queue,
        Action<GeometryAllocation12> freeGeometry, Action<GpuTextureCache12.Entry?> releaseTexture)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(freeGeometry);
        ArgumentNullException.ThrowIfNull(releaseTexture);
        var resources = new GpuMeshResources12(freeGeometry, releaseTexture);
        queue.EnqueueDispose(new GpuMeshMaterializationRollback12(resources));
        resources.VerifyAcquisition();
        return resources;
    }

    /// <summary>Acquires one geometry range into its already-registered cleanup slot.</summary>
    /// <param name="acquire">Uploads and returns the range, retaining responsibility for failures inside the upload itself.</param>
    internal GeometryAllocation12 AcquireGeometry(Func<GeometryAllocation12> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        return AcquireGeometry(retain =>
        {
            var geometry = acquire();
            retain(geometry);
            return geometry;
        });
    }

    /// <summary>Captures the allocated range before fallible backing and copy work, then records its completed views.</summary>
    /// <param name="acquire">Receives a callback accepting ownership of the range before native use. After callback
    /// success, this ledger owns the range even if acquisition later throws; the producer must not free it.</param>
    /// <returns>The completed geometry views; failed acquisition remains retained for actual retirement.</returns>
    internal GeometryAllocation12 AcquireGeometry(Func<Action<GeometryAllocation12>, GeometryAllocation12> acquire)
    {
        VerifyAcquisition();
        ArgumentNullException.ThrowIfNull(acquire);
        if (_hasGeometry) throw new InvalidOperationException("Mesh geometry was already acquired.");
        var acceptingAllocation = true;
        _busy = true;
        try
        {
            var completed = acquire(geometry =>
            {
                if (!acceptingAllocation || _retiring || _committed || Environment.CurrentManagedThreadId != _threadId)
                    throw new InvalidOperationException("Geometry ownership must transfer during its creating-thread acquisition.");
                if (_hasGeometry) throw new InvalidOperationException("Mesh geometry was already retained.");
                _geometry = geometry;
                _hasGeometry = true;
            });
            if (!_hasGeometry) throw new InvalidOperationException("Geometry acquisition did not transfer its allocation.");
            if (completed.Allocation != _geometry.Allocation)
                throw new InvalidOperationException("Completed geometry differs from the retained allocation.");
            _geometry = completed;
            return _geometry;
        }
        finally { acceptingAllocation = false; _busy = false; }
    }

    /// <summary>Registers an independent cleanup action before acquiring a texture reference.</summary>
    /// <param name="acquire">Returns one reference, which may alias another acquired reference.</param>
    /// <returns>The acquired entry for the material binding.</returns>
    internal GpuTextureCache12.Entry AcquireTexture(Func<GpuTextureCache12.Entry> acquire)
    {
        VerifyAcquisition();
        ArgumentNullException.ThrowIfNull(acquire);
        GpuTextureCache12.Entry? entry = null;
        _cleanup.Add(() =>
        {
            if (entry is not null) _releaseTexture(entry);
        }, "mesh texture reference");
        _busy = true;
        try { return entry = acquire(); }
        finally { _busy = false; }
    }

    /// <summary>Transfers the completed acquisition owner to its mesh and disarms queued rollback.</summary>
    internal void Commit()
    {
        VerifyAcquisition();
        if (!_hasGeometry) throw new InvalidOperationException("A materialized mesh requires geometry.");
        _committed = true;
    }

    /// <summary>Releases a failed materialization only after its pre-registered retirement delay has elapsed.</summary>
    internal void RollbackAfterRetirement()
    {
        VerifyAccess();
        if (!_committed) Dispose();
    }

    /// <summary>Releases actual geometry and references, retaining failed actions for a later retry.</summary>
    /// <exception cref="AggregateException">One or more independent release actions remain pending.</exception>
    /// <exception cref="InvalidOperationException">The owner is accessed from another thread or during an acquisition/release callback.</exception>
    public void Dispose()
    {
        VerifyAccess();
        _retiring = true;
        _busy = true;
        try { _cleanup.Dispose(); }
        finally { _busy = false; }
    }

    /// <summary>Returns the captured arena range once; unsuccessful release remains owned by Shared cleanup.</summary>
    private void ReleaseGeometry()
    {
        if (!_hasGeometry) return;
        _freeGeometry(_geometry);
        _hasGeometry = false;
    }

    /// <summary>Rejects acquisition after either publication or retirement without changing ownership.</summary>
    private void VerifyAcquisition()
    {
        VerifyAccess();
        if (_committed || _retiring) throw new InvalidOperationException("Mesh resource acquisition has ended.");
    }

    /// <summary>Preserves render-thread ownership and rejects callbacks that reenter a lifetime transition.</summary>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId || _busy)
            throw new InvalidOperationException("Mesh resource lifetime belongs to its creating thread and cannot be reentered.");
    }
}
