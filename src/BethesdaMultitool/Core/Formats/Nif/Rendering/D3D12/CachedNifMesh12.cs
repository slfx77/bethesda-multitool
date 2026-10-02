#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>A reference NIF uploaded to GPU geometry/texture caches: its submeshes, bounds, and authored water geometry, drawn each frame and disposed when evicted.</summary>
internal sealed class CachedNifMesh12 : IDisposable
{
    private readonly GeometryAllocation12 _geometry;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly GpuMeshResources12 _resources;
    private ResourceResidencyPin<GpuMeshResources12>? _residencyPin;
    private bool _residencyOwned;
    private bool _retirementQueued;
    private bool _texturesReady;
    private bool _disposed;

    /// <summary>Builds CPU presentation around an acquisition owner; the caller commits that owner after construction succeeds.</summary>
    public CachedNifMesh12(
        CachedSubmesh12[] submeshes,
        GpuMeshResources12 resources,
        GpuDeletionQueue12 deletionQueue,
        float localBoundsRadius,
        Vector3 localBoundsMin,
        Vector3 localBoundsMax,
        IReadOnlyList<NifWaterGeometry> waterPlanesLocal)
    {
        Submeshes = submeshes;
        _geometry = resources.Geometry;
        _resources = resources;
        _deletionQueue = deletionQueue;
        LocalBoundsRadius = localBoundsRadius;
        LocalBoundsMin = localBoundsMin;
        LocalBoundsMax = localBoundsMax;
        WaterPlanesLocal = waterPlanesLocal;
    }

    /// <summary>
    ///     The mesh's submeshes, as a concrete ARRAY rather than an interface.
    ///     <para>
    ///         Deliberate: the render thread walks this list once per cull survivor per frame
    ///         (~24k times in a dense FNV exterior). Through <see cref="IReadOnlyList{T}" /> each
    ///         <c>foreach</c> boxes a fresh enumerator and every step is an interface dispatch; over an
    ///         array the compiler emits a plain indexed loop and allocates nothing.
    ///     </para>
    /// </summary>
    public CachedSubmesh12[] Submeshes { get; }

    /// <summary>Model path this mesh was uploaded for — names the mesh in geometry diagnostics.</summary>
    public string? SourcePath { get; init; }

    /// <summary>True once eviction disposed this mesh: its arena range is queued for reuse and any
    /// batch still holding one of its submeshes must not be drawn.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>The mesh's arena sub-allocation, exposed for draw-time liveness validation.</summary>
    internal GeometryAllocation12 Geometry => _geometry;

    /// <summary>
    ///     Keyframe animation rig (bone tree + tracks + clip window) for animated statics (Morrowind
    ///     banners), carried CPU-side for the per-frame mesh skinner; null for the common static
    ///     mesh. The uploaded vertex buffers hold the REST pose, so a renderer that never ticks the
    ///     skinner still draws the correct static mesh.
    /// </summary>
    public NifMeshAnimation? Animation { get; set; }

    /// <summary>
    ///     Mesh-local positions and indices of any WaterShaderProperty submeshes in this NIF — the
    ///     placeable water surfaces (cave/pool/reflecting-pool water) that were diverted out of the
    ///     drawable submesh set at upload. Empty for the common (non-water) mesh. The reference
    ///     renderer applies each placement world matrix to the authored vertices and feeds the result
    ///     to the water renderer, preserving slopes, rotations, and non-rectangular outlines.
    /// </summary>
    public IReadOnlyList<NifWaterGeometry> WaterPlanesLocal { get; }

    /// <summary>
    ///     Conservative bounding-sphere radius of the whole mesh around the NIF origin (max vertex
    ///     distance from local 0,0,0). The reference cull scales this into world space and uses it
    ///     instead of the OBND estimate so large meshes aren't culled at screen edges.
    /// </summary>
    public float LocalBoundsRadius { get; }

    /// <summary>
    ///     Mesh-local axis-aligned bounding box (min/max over all combined vertices, in NIF-local
    ///     space). Used as the selection-highlight box for refs that carry no OBND (every Oblivion
    ///     ref, and any FO3+ ref whose base record omits OBND): the highlight transforms this by the
    ///     placement world matrix so the box hugs the real geometry instead of the oversized
    ///     no-bounds fallback sphere. <see cref="LocalBoundsMin" /> &gt; <see cref="LocalBoundsMax" />
    ///     for a degenerate (empty) mesh — callers should treat that as "no AABB".
    /// </summary>
    public Vector3 LocalBoundsMin { get; }

    /// <inheritdoc cref="LocalBoundsMin" />
    public Vector3 LocalBoundsMax { get; }

    public bool TexturesReady
    {
        get
        {
            if (_texturesReady)
            {
                return true;
            }

            foreach (var submesh in Submeshes)
            {
                if (!submesh.TexturesReady)
                {
                    return false;
                }
            }

            _texturesReady = true;
            return true;
        }
    }

    /// <summary>Stops drawing and returns the cache pin, or queues standalone resources for retired release.</summary>
    /// <remarks>Shared residency entries remain the sole native owner for world meshes. Standalone
    /// transfer failures remain retryable. This method never establishes physical release.</remarks>
    public void Dispose()
    {
        _disposed = true;
        if (_residencyOwned)
        {
            _residencyPin?.Dispose();
            _residencyPin = null;
            return;
        }
        if (_retirementQueued) return;
        _deletionQueue.EnqueueDispose(_resources);
        _retirementQueued = true;
    }

    /// <summary>Releases the actual range and texture references after the caller proves all GPU users retired.</summary>
    /// <remarks>Failed releases remain owned for retry. Do not mix this path with queued disposal.</remarks>
    internal void ReleaseAfterRetirement()
    {
        if (_residencyOwned)
            throw new InvalidOperationException("The residency entry owns physical mesh release.");
        if (_retirementQueued)
            throw new InvalidOperationException("Queued mesh retirement already owns these resources.");
        _disposed = true;
        _resources.Dispose();
    }

    /// <summary>Gets whether the range was returned and every acquired texture reference was released.</summary>
    internal bool ResourcesReleased => _resources.IsReleased;

    /// <summary>Transfers the cache's initial prepared pin to this presentation without changing native ownership.</summary>
    /// <param name="pin">Exact resource pin owned by the caller until this method succeeds.</param>
    internal void AdoptResidencyPin(ResourceResidencyPin<GpuMeshResources12> pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (_disposed || _residencyOwned || _retirementQueued || !ReferenceEquals(pin.Resource, _resources))
            throw new InvalidOperationException("The mesh cannot adopt this residency pin.");
        _residencyPin = pin;
        _residencyOwned = true;
    }

    /// <summary>Retains the exact mesh for a batch or skinner; standalone previews have no residency entry.</summary>
    /// <returns>An independent pin, or null for a standalone materialization.</returns>
    internal ResourceResidencyPin<GpuMeshResources12>? AcquireResidencyPin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _residencyPin?.Retain();
    }
}
#endif
