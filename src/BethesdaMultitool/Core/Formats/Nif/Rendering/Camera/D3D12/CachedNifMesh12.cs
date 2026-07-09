#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>A reference NIF uploaded to GPU geometry/texture caches: its submeshes, bounds, and water planes, drawn each frame and disposed when evicted.</summary>
internal sealed class CachedNifMesh12 : IDisposable
{
    private readonly GeometryAllocation12 _geometry;
    private readonly GpuGeometryArena12 _arena;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly GpuTextureCache12 _textureCache;
    private bool _texturesReady;
    private bool _disposed;

    public CachedNifMesh12(
        IReadOnlyList<CachedSubmesh12> submeshes,
        GeometryAllocation12 geometry,
        GpuGeometryArena12 arena,
        GpuDeletionQueue12 deletionQueue,
        GpuTextureCache12 textureCache,
        float localBoundsRadius,
        Vector3 localBoundsMin,
        Vector3 localBoundsMax,
        IReadOnlyList<(Vector3 Min, Vector3 Max)> waterPlanesLocal)
    {
        Submeshes = submeshes;
        _geometry = geometry;
        _arena = arena;
        _deletionQueue = deletionQueue;
        _textureCache = textureCache;
        LocalBoundsRadius = localBoundsRadius;
        LocalBoundsMin = localBoundsMin;
        LocalBoundsMax = localBoundsMax;
        WaterPlanesLocal = waterPlanesLocal;
    }

    public IReadOnlyList<CachedSubmesh12> Submeshes { get; }

    /// <summary>
    ///     Mesh-local AABBs (min/max) of any WaterShaderProperty submeshes in this NIF — the
    ///     placeable water planes (cave/pool/reflecting-pool water) that were diverted out of the
    ///     drawable submesh set at upload. Empty for the common (non-water) mesh. The reference
    ///     renderer transforms each by the placement world matrix and feeds the result to the water
    ///     renderer so placed water gets the real Fresnel/ripple/depth-fade shader instead of a slab.
    /// </summary>
    public IReadOnlyList<(Vector3 Min, Vector3 Max)> WaterPlanesLocal { get; }

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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Defer the arena free by FramesInFlight frames so in-flight draws referencing this mesh's
        // sub-range drain before the range is handed back out — the same GPU-safety guarantee the
        // deletion queue gave the per-mesh committed buffers this replaced.
        _deletionQueue.EnqueueDispose(_arena.DeferredFreeHandle(_geometry));

        // Release this mesh's references on its submesh textures so the texture cache can evict and
        // reclaim their bindless slots once no resident mesh needs them. Without this the texture
        // cache grows unbounded and the persistent descriptor heap exhausts during sustained
        // streaming (the ~5-minute walk-mode crash). Each GetOrUpload at build time is balanced by
        // one Release here; shared textures stay alive until their last owning mesh is evicted.
        foreach (var submesh in Submeshes)
        {
            _textureCache.Release(submesh.Diffuse);
            _textureCache.Release(submesh.Normal);
            // The optional material maps hold GetOrUpload references too — skipping them leaked
            // their entries (and bindless slots) on every mesh eviction since the v21/v22 fields.
            if (submesh.SpecularMap is { } specularMap)
            {
                _textureCache.Release(specularMap);
            }

            if (submesh.GradientMap is { } gradientMap)
            {
                _textureCache.Release(gradientMap);
            }

            if (submesh.EnvMap is { } envMap)
            {
                _textureCache.Release(envMap);
            }
        }
    }
}
#endif
