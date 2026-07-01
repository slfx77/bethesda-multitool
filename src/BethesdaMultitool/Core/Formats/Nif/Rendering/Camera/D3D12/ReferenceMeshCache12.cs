#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Orchestration;
using BethesdaMultitool.Core.Resources;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     D3D12 placed-reference mesh cache. CPU decode is requested asynchronously; all D3D12
///     resource creation stays on the render thread while the command list is open.
/// </summary>
internal sealed class ReferenceMeshCache12 : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    // Cold NIF conversion allocates heavily and competes with the UI/render thread, so the default
    // scales with cores but stays GC-guarded: HALF the logical processors, clamped to [2, 8]. This
    // leaves headroom for the render + UI + GC threads (full-core saturation caused the GC pauses the
    // old conservative default of 2 was guarding against). Env override for profiling specific machines.
    private static readonly int DefaultMaxConcurrentDecodeTasks = ParsePositiveIntEnvironment(
        EnvironmentVariables.Viewer.ReferenceDecodeConcurrency,
        defaultValue: Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
        min: 1,
        max: 16);

    private static readonly int DefaultMaxDecodeStartsPerFrame = ParsePositiveIntEnvironment(
        EnvironmentVariables.Viewer.ReferenceDecodeStartsPerFrame,
        defaultValue: DefaultMaxConcurrentDecodeTasks,
        min: 1,
        max: DefaultMaxConcurrentDecodeTasks);
    private const long DefaultDecodedMeshCacheByteBudget = 256L * 1024L * 1024L;
    // Positions-only collision data is ~12 B/vertex (vs the 72 B GPU vertex), so a generous budget
    // holds the walkable footprint of a worldspace many times over. Render-thread LRU.
    private const long CollisionMeshCacheByteBudget = 128L * 1024L * 1024L;
    // Tangent-space normal-map perturbation scale (reference.frag.hlsl: mapN.xy *= vRenderState.z).
    // The FNV SLS pixel shader applies the sampled tangent-space normal at full strength, so this is
    // 1.0 (engine-faithful). It was previously 0.35, which flattened the perturbation to ~a third and
    // made bump mapping read as "not applied" under the viewer's lighting.
    private const float ReferenceBumpStrength = 1.0f;

    // Size-aware ceiling on render-thread mesh-upload work per frame. The integer upload budget
    // (passed by the renderer) caps *how many* meshes upload; this caps the *bytes*, checked before
    // each upload so a frame that has already spent its budget defers an expensive mesh to the next
    // frame instead of overshooting on it. Sized so a typical 48-mesh frame passes but a burst of
    // unusually large meshes is spread across frames.
    private static readonly long DefaultMaxUploadBytesPerFrame = ParsePositiveLongEnvironment(
        EnvironmentVariables.Viewer.ReferenceUploadBytesPerFrame,
        defaultValue: 4L * 1024L * 1024L,
        min: 1L * 1024L * 1024L,
        max: 64L * 1024L * 1024L);

    private readonly MeshArchiveSet _meshArchives;
    private readonly ReferenceMeshDecoder12 _decoder;
    private readonly GpuTextureCache12 _textureCache;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly GpuGeometryArena12 _geometryArena;
    // Resident-mesh LRU. Render-thread only — LruCache's lock-free single-threaded contract.
    // Eviction runs the GPU residency cascade via onEvicted → CachedNifMesh12.Dispose (deferred
    // arena free + per-submesh texture refcount release). Node values are mutated IN PLACE after
    // insertion (decode completes → node.Mesh assigned); they are never re-Set, which would fire
    // onEvicted on the live node.
    private readonly LruCache<string, Node> _meshLru;
    // Nearest-first decode queue: priority = squared distance from the view point at request time
    // (smaller = nearer = dequeued first). The queue persists across frames, so when a dense area
    // enters view the foreground meshes decode before the far edge instead of in arrival (FIFO)
    // order. De-dups internally; a path keeps its first enqueued priority — approximate but cheap,
    // no priority updates. Equal priorities now dequeue in strict FIFO order (stable tiebreak).
    private readonly PrioritizedKeyQueue<string> _decodeQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly InFlightTaskTracker _decodeTasks = new(nameof(ReferenceMeshCache12));
    // Byte-budgeted decoded CPU mesh cache. LruCache is single-threaded by design; every access
    // is serialized by _decodedCacheLock (decode pool threads store, render thread reads).
    // Eviction is a plain drop (no onEvicted) — decoded payloads are GC-reclaimed.
    private readonly LruCache<string, DecodedCacheValue> _decodedLru;
    // Walk-mode collision geometry (positions + indices only), keyed by normalized model path. Built
    // on the render thread inside UploadDecodedMesh and read on the render thread by the control's
    // ground-snap, so it shares the LruCache single-threaded contract with _meshLru. Positions-only is
    // a fraction of the GPU footprint, so a large byte budget keeps far more meshes warm than residency.
    private readonly LruCache<string, CollisionMesh> _collisionLru;
    private readonly ReferenceDecodedMeshDiskCache12? _persistentDecodedCache;
    private readonly object _decodedCacheLock = new();
    private int _activeDecodeTasks;
    private FrameByteBudget _frameUploadByteBudget;
    // When true, LoadData resizes _meshLru to the worldspace's working set (default). False when the
    // FALLOUT_VIEWER_REFERENCE_MESH_CAPACITY env knob pins a fixed cap for eviction-cascade stress gates.
    private readonly bool _autoSizeMeshCapacity;
    private bool _disposed;

    public ReferenceMeshCache12(
        GpuDevice12 gpu,
        MeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        GpuTextureCache12 textureCache,
        GpuDeletionQueue12 deletionQueue,
        int capacity,
        long decodedCacheByteBudget = DefaultDecodedMeshCacheByteBudget,
        ReferenceDecodedMeshDiskCache12? persistentDecodedCache = null,
        bool autoSizeMeshCapacity = true,
        IReadOnlyDictionary<string, float>? speedTreeHeights = null,
        IReadOnlyDictionary<string, string>? speedTreeLeafTextures = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");

        _meshArchives = meshArchives;
        _decoder = new ReferenceMeshDecoder12(meshArchives, textureResolver, speedTreeHeights, speedTreeLeafTextures);
        _textureCache = textureCache;
        _deletionQueue = deletionQueue;
        _geometryArena = new GpuGeometryArena12(gpu)
            .RegisterWith(Core.Diagnostics.ResourceRegistry.Instance, "reference");
        _persistentDecodedCache = persistentDecodedCache ?? ReferenceDecodedMeshDiskCache12.CreateFromEnvironment();
        Capacity = capacity;
        DecodedMeshCacheByteBudget = decodedCacheByteBudget;
        _autoSizeMeshCapacity = autoSizeMeshCapacity;
        // No sizeOf: GPU bytes are already tracked by GpuGeometryArena12["reference"] — sizing
        // this cache too would double-count them in the registry.
        _meshLru = new LruCache<string, Node>(
                "ReferenceMeshLru",
                ResourceCategory.GpuResident,
                maxEntries: capacity,
                onEvicted: static (_, node) => node.Mesh?.Dispose(),
                comparer: StringComparer.OrdinalIgnoreCase)
            .RegisterWith(ResourceRegistry.Instance, "reference-meshes");
        _decodedLru = new LruCache<string, DecodedCacheValue>(
                "ReferenceDecodedMeshCache",
                ResourceCategory.CpuCache,
                maxBytes: decodedCacheByteBudget,
                sizeOf: static (_, value) => value.ByteSize,
                comparer: StringComparer.OrdinalIgnoreCase)
            .RegisterWith(ResourceRegistry.Instance, "reference-decoded");
        _collisionLru = new LruCache<string, CollisionMesh>(
                "ReferenceCollisionMeshCache",
                ResourceCategory.CpuCache,
                maxBytes: CollisionMeshCacheByteBudget,
                sizeOf: static (_, value) => value.ByteSize,
                comparer: StringComparer.OrdinalIgnoreCase)
            .RegisterWith(ResourceRegistry.Instance, "reference-collision");
    }

    /// <summary>
    ///     Walk-mode ground-snap accessor: returns the cached mesh-local collision geometry for
    ///     <paramref name="modelPath" /> when it has been decoded+uploaded at least once and not since
    ///     evicted. Render-thread only (shares the <c>_collisionLru</c> single-threaded contract). A
    ///     cold miss is expected for never-streamed or recently-evicted meshes; the caller falls back
    ///     to the OBND box for that frame.
    /// </summary>
    public bool TryGetCollisionMesh(string modelPath, out CollisionMesh? collision)
    {
        collision = null;
        if (_disposed) return false;
        if (_collisionLru.TryGet(ReferenceMeshDecoder12.NormalizeModelPath(modelPath), out var mesh))
        {
            collision = mesh;
            return true;
        }

        return false;
    }

    public int Capacity { get; }
    public int Count => _meshLru.Count;

    /// <summary>
    ///     Resizes the resident-mesh LRU to <paramref name="capacity" /> (raising holds more; lowering
    ///     evicts the excess through the dispose cascade). No-op when auto-sizing is off (the capacity
    ///     env knob pinned a fixed cap), disposed, or <paramref name="capacity" /> is non-positive.
    ///     Render-thread only — same contract as every other <c>_meshLru</c> access.
    /// </summary>
    public void SetMeshCapacity(int capacity)
    {
        if (_disposed || !_autoSizeMeshCapacity || capacity <= 0)
        {
            return;
        }

        _meshLru.SetMaxEntries(capacity);
    }

    public int MaxDecodeStartsPerFrame { get; init; } = DefaultMaxDecodeStartsPerFrame;
    public int MaxConcurrentDecodeTasks { get; init; } = DefaultMaxConcurrentDecodeTasks;
    public long DecodedMeshCacheByteBudget { get; }
    public long MaxUploadBytesPerFrame { get; init; } = DefaultMaxUploadBytesPerFrame;

    // Concurrency ceiling used when streaming is unthrottled (the on-demand top-down overlay path,
    // which has no framerate target). Wider than the conservative live-loop default so a single
    // overlay pass can saturate the CPU with decodes instead of trickling 2 at a time.
    private static readonly int UnthrottledMaxConcurrentDecodeTasks = Math.Clamp(Environment.ProcessorCount, 4, 16);

    /// <summary>
    ///     When <c>false</c>, the per-frame streaming budget (decode starts + concurrency + upload
    ///     bytes) is lifted so a caller can load everything visible across a few back-to-back renders.
    ///     The live 60fps loop leaves this <c>true</c> to keep motion smooth; the top-down overlay
    ///     sets it <c>false</c> for the duration of its render. Set via <see cref="ReferenceRenderer12.StreamingThrottled"/>.
    /// </summary>
    public bool StreamingThrottled { get; set; } = true;

    private int EffectiveMaxDecodeStartsPerFrame =>
        StreamingThrottled ? MaxDecodeStartsPerFrame : int.MaxValue;

    private int EffectiveMaxConcurrentDecodeTasks =>
        StreamingThrottled ? MaxConcurrentDecodeTasks : Math.Max(MaxConcurrentDecodeTasks, UnthrottledMaxConcurrentDecodeTasks);
    public int FrameCacheMisses { get; private set; }
    public int FrameDecodeRequests { get; private set; }
    public int FrameQueuedDecodes { get; private set; }
    public int FrameDecodeStarts { get; private set; }
    public int FrameActiveDecodes => Volatile.Read(ref _activeDecodeTasks);
    public int FrameCpuDecodedCacheHits { get; private set; }
    public int FrameCpuDecodedCacheMisses { get; private set; }
    public int FrameCpuDecodedNegativeHits { get; private set; }
    public int FrameGpuUploads { get; private set; }
    public int FrameByteBudgetDeferrals { get; private set; }
    public int FrameCompressedTextureUploads => _textureCache.FrameCompressedUploads;
    public int FrameRgbaTextureUploads => _textureCache.FrameRgbaFallbackUploads;
    public int FrameQueuedTextureResolves => _textureCache.FrameQueuedResolves;
    public int FrameActiveTextureResolves => _textureCache.FrameActiveResolves;
    public int PendingTextureResolves => _textureCache.PendingResolveCount;
    public int PendingTextureUploads => _textureCache.PendingUploadCount;
    public int TotalMissingModelPaths => _decoder.TotalMissingModelPaths;
    public int TotalSkinnedModelPaths => _decoder.TotalSkinnedModelPaths;

    public void ResetFrameStats()
    {
        FrameCacheMisses = 0;
        FrameDecodeRequests = 0;
        FrameQueuedDecodes = 0;
        FrameDecodeStarts = 0;
        FrameCpuDecodedCacheHits = 0;
        FrameCpuDecodedCacheMisses = 0;
        FrameCpuDecodedNegativeHits = 0;
        FrameGpuUploads = 0;
        FrameByteBudgetDeferrals = 0;
        _frameUploadByteBudget = new FrameByteBudget(StreamingThrottled ? MaxUploadBytesPerFrame : long.MaxValue);
        _textureCache.ResetFrameStats();
        PruneCompletedDecodeTasks();
    }

    public CachedNifMesh12? GetOrUpload(
        string modelPath,
        ref int uploadBudget,
        float priority = 0f,
        AlternateTextureSet? alternateTextures = null)
    {
        if (_disposed) return null;

        PruneCompletedDecodeTasks();
        StartQueuedDecodes();

        var decodePath = ReferenceMeshDecoder12.NormalizeModelPath(modelPath);
        // A placement whose base carries MODS alternate textures gets its own cache entry per texture
        // variant (so a shared NIF — e.g. every billboard — renders its correct per-base textures). The
        // '#variant' suffix is an in-memory-cache discriminator ONLY: archive lookups, on-disk cache
        // metadata, decode, and collision all key on the plain decodePath via Node.DecodePath. Disk
        // persistence is skipped for variants (the on-disk cache keys on the NIF's archive identity, so
        // two variants of one NIF would otherwise collide there — see Node.HasVariant).
        var cacheKey = alternateTextures is null
            ? decodePath
            : decodePath + "#" + alternateTextures.VariantKey;
        // TryGet bumps the hit to MRU — the old explicit Touch.
        if (_meshLru.TryGet(cacheKey, out var existing))
        {
            var resolved = ResolveExisting(cacheKey, existing, ref uploadBudget);
            StartQueuedDecodes();
            return resolved;
        }

        FrameCacheMisses++;
        var node = new Node(
            Mesh: null,
            DecodeTask: null,
            ResolvedNull: false)
        {
            DecodePath = decodePath,
            Overrides = alternateTextures?.Overrides,
            HasVariant = alternateTextures is not null
        };
        // Set evicts LRU entries over capacity, each through onEvicted (the dispose cascade);
        // the just-inserted node always survives its own Set.
        _meshLru.Set(cacheKey, node);
        var mesh = ResolveExisting(cacheKey, node, ref uploadBudget);
        if (mesh is null && !node.ResolvedNull && !node.DecodedCacheAvailable)
        {
            QueueDecode(cacheKey, node, priority);
        }
        StartQueuedDecodes();
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _persistentDecodedCache?.LogStatistics();

        DrainDecodeTasksForDispose();

        // Unregisters, then evicts every node LRU-tail-first through onEvicted (Mesh?.Dispose()).
        // Cross-mesh disposal order carries no correctness weight: texture refcount releases are
        // commutative and the deletion queue is order-insensitive; the host has already called
        // WaitForGpuIdle.
        _meshLru.Dispose();
        _decodeQueue.Clear();
        lock (_decodedCacheLock)
        {
            // Unregisters from the resource registry, then drops every entry (no onEvicted).
            _decodedLru.Dispose();
        }

        // Render-thread cache (no onEvicted); the host has already idled the GPU + stopped the loop.
        _collisionLru.Dispose();

        // Frees the arena's UPLOAD blocks. The host calls WaitForGpuIdle before disposing this cache,
        // so no draw still references them; any arena frees the meshes above enqueued on the deletion
        // queue become no-ops (GpuGeometryArena12.Free is disposed-guarded).
        _geometryArena.Dispose();
    }

    private void DrainDecodeTasksForDispose()
    {
        var pending = _decodeTasks.PendingCount;
        if (pending > 0)
        {
            Log.Info("ReferenceMeshCache12: waiting for {0} decode task(s) during dispose.", pending);
        }

        // Non-pumping drain (dispose runs on the UI thread); faulted tasks are observed + logged.
        _decodeTasks.WaitForDrainLogged();
    }

    private CachedNifMesh12? ResolveExisting(string modelPath, Node node, ref int uploadBudget)
    {
        if (node.Mesh is not null)
        {
            return node.Mesh;
        }
        if (node.ResolvedNull)
        {
            return null;
        }
        if (TryResolveFromDecodedCache(modelPath, node, ref uploadBudget, out var cachedMesh))
        {
            return cachedMesh;
        }

        if (node.DecodeTask is not { IsCompleted: true } task)
        {
            return null;
        }
        if (task.IsFaulted)
        {
            Log.Warn(
                "ReferenceMeshCache12: decode failed for '{0}': {1}",
                modelPath,
                task.Exception?.GetBaseException().Message ?? "(unknown)");
            node.ResolvedNull = true;
            node.DecodeTask = null;
            StoreDecodedCache(modelPath, null, persist: false);
            return null;
        }

        var decoded = task.Result;
        node.DecodeTask = null;
        if (decoded is null)
        {
            node.ResolvedNull = true;
            StoreDecodedCache(modelPath, null, persist: false);
            return null;
        }

        StoreDecodedCache(modelPath, decoded, persist: false);
        if (TryResolveFromDecodedCache(modelPath, node, ref uploadBudget, out cachedMesh))
        {
            return cachedMesh;
        }

        return null;
    }

    private bool TryResolveFromDecodedCache(
        string modelPath,
        Node node,
        ref int uploadBudget,
        out CachedNifMesh12? mesh)
    {
        mesh = null;
        if (!TryGetDecodedCache(modelPath, out var decoded))
        {
            // Variants never consult the on-disk cache (it keys on the NIF's archive identity, shared
            // across variants). Non-variant load uses the plain DecodePath (== modelPath here).
            if ((node.HasVariant || !TryLoadPersistentDecodedCache(node.DecodePath, out decoded)) &&
                !node.DecodedCacheMissRecorded)
            {
                FrameCpuDecodedCacheMisses++;
                node.DecodedCacheMissRecorded = true;
            }
            if (decoded.Mesh is null && !decoded.IsNegative)
            {
                return false;
            }
        }

        FrameCpuDecodedCacheHits++;
        if (decoded.IsNegative || decoded.Mesh is null)
        {
            FrameCpuDecodedNegativeHits++;
            node.ResolvedNull = true;
            return true;
        }

        node.DecodedCacheAvailable = true;
        if (uploadBudget <= 0)
        {
            return true;
        }
        // Size-aware gate: defer an upload the frame can't afford to a later frame (DecodedCacheAvailable
        // stays set, so it retries) instead of overshooting the frame on one big mesh. Never blocks the
        // first upload of a frame, so a mesh larger than the whole budget still makes progress.
        if (!_frameUploadByteBudget.CanUpload(decoded.ByteSize))
        {
            FrameByteBudgetDeferrals++;
            return true;
        }

        uploadBudget--;
        FrameGpuUploads++;
        _frameUploadByteBudget.Record(decoded.ByteSize);
        // Upload + collision keying use the plain archive path (collision geometry is texture-
        // independent, so all variants of a NIF share one collision entry keyed on DecodePath).
        mesh = UploadDecodedMesh(node.DecodePath, decoded.Mesh);
        if (mesh is null)
        {
            node.ResolvedNull = true;
            StoreDecodedCache(modelPath, null, persist: false);
            return true;
        }

        node.Mesh = mesh;
        return true;
    }

    private void QueueDecode(string modelPath, Node node, float priority)
    {
        if (node.DecodeTask is not null ||
            node.DecodeQueued ||
            node.ResolvedNull ||
            !_decodeQueue.Enqueue(modelPath, priority))
        {
            return;
        }

        node.DecodeQueued = true;
        FrameQueuedDecodes++;
    }

    private void StartQueuedDecodes()
    {
        while (FrameDecodeStarts < EffectiveMaxDecodeStartsPerFrame &&
               Volatile.Read(ref _activeDecodeTasks) < EffectiveMaxConcurrentDecodeTasks &&
               _decodeQueue.TryDequeue(out var modelPath))
        {
            // TryPeek, not TryGet: validating a de-queued decode key must not MRU-bump the node
            // (nobody asked for it this frame) or perturb the registry hit/miss counters.
            if (!_meshLru.TryPeek(modelPath, out var node) ||
                node.ResolvedNull ||
                node.Mesh is not null ||
                node.DecodeTask is not null ||
                TryGetDecodedCache(modelPath, out _))
            {
                if (node is not null)
                {
                    node.DecodeQueued = false;
                }
                continue;
            }

            node.DecodeQueued = false;
            node.DecodeTask = StartDecodeTask(modelPath, node);
            FrameDecodeRequests++;
            FrameDecodeStarts++;
        }
    }

    private Task<DecodedNifMesh12?> StartDecodeTask(string cacheKey, Node node)
    {
        // Decode from the plain archive path with the node's MODS overrides (if any) baked into the
        // submesh texture paths; cache the result under the composite cacheKey. Variants aren't
        // persisted to disk (their overridden textures would collide with the base under the NIF's
        // archive identity) — the in-memory decoded LRU still caches them for the session.
        var decodePath = node.DecodePath;
        var overrides = node.Overrides;
        var persist = !node.HasVariant;
        Interlocked.Increment(ref _activeDecodeTasks);
        var task = Task.Run(() =>
        {
            try
            {
                var decoded = _decoder.DecodeMesh(decodePath, overrides);
                StoreDecodedCache(cacheKey, decoded, persist);
                return decoded;
            }
            finally
            {
                Interlocked.Decrement(ref _activeDecodeTasks);
            }
        });

        _decodeTasks.Add(task);

        return task;
    }

    private void PruneCompletedDecodeTasks()
    {
        _decodeTasks.PruneCompleted();
    }

    private bool TryGetDecodedCache(string modelPath, out DecodedCacheValue value)
    {
        lock (_decodedCacheLock)
        {
            // TryGet bumps the hit to MRU — same recency semantics as the hand-rolled cache.
            return _decodedLru.TryGet(modelPath, out value);
        }
    }

    private bool TryLoadPersistentDecodedCache(string modelPath, out DecodedCacheValue value)
    {
        value = default;
        if (_persistentDecodedCache is null)
        {
            return false;
        }

        // SpeedTree geometry is generated procedurally (cheap, and the algorithm is still being
        // tuned). Never serve it from the on-disk cache, or generator changes / live option tweaks
        // would be masked by a stale entry keyed on the unchanged .spt bytes.
        if (SpeedTreeModelPath.IsSpt(modelPath))
        {
            return false;
        }

        var metadata = _meshArchives.GetLookupMetadata(modelPath);

        if (!_persistentDecodedCache.TryLoad(metadata, out var cached))
        {
            return false;
        }

        var decoded = cached.IsNegative || cached.Mesh is null
            ? null
            : ReferenceMeshDecoder12.FromPersistentPayload(cached.Mesh);
        StoreDecodedCache(modelPath, decoded, persist: false);
        return TryGetDecodedCache(modelPath, out value);
    }

    private void StoreDecodedCache(string modelPath, DecodedNifMesh12? decoded, bool persist = true)
    {
        var byteSize = decoded is null
            ? 1L
            : Math.Max(1L, ReferenceMeshDecoder12.EstimateDecodedMeshBytes(decoded));
        var value = new DecodedCacheValue(decoded, decoded is null, byteSize);

        lock (_decodedCacheLock)
        {
            // Set replaces an existing entry and evicts LRU entries over the byte budget. One
            // deliberate delta vs the old hand-rolled loop: an entry larger than the whole
            // budget now SURVIVES its own insert (alone, over budget) instead of self-evicting —
            // the old behavior was a decode→store→self-evict→re-decode livelock for any mesh
            // whose estimate exceeded a (diagnostically) tiny budget.
            _decodedLru.Set(modelPath, value);
        }

        if (persist)
        {
            StorePersistentDecodedCache(modelPath, decoded);
        }
    }

    private void StorePersistentDecodedCache(string modelPath, DecodedNifMesh12? decoded)
    {
        if (_persistentDecodedCache is null)
        {
            return;
        }

        // SpeedTree geometry is regenerated each session (see TryLoadPersistentDecodedCache) — never
        // persist it.
        if (SpeedTreeModelPath.IsSpt(modelPath))
        {
            return;
        }

        try
        {
            var metadata = _meshArchives.GetLookupMetadata(modelPath);

            _persistentDecodedCache.Store(
                metadata,
                decoded is null ? null : ReferenceMeshDecoder12.ToPersistentPayload(decoded));
        }
        catch (Exception ex)
        {
            Log.Warn("ReferenceMeshCache12: decoded mesh disk cache write skipped for '{0}': {1}", modelPath, ex.Message);
        }
    }

    private CachedNifMesh12? UploadDecodedMesh(string modelPath, DecodedNifMesh12 decoded)
    {
        var started = RendererProfilerTrace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        var success = false;
        var totalVertexCount = 0;
        var totalIndexCount = 0;
        foreach (var sub in decoded.Submeshes)
        {
            checked
            {
                totalVertexCount += sub.Vertices.Length;
                totalIndexCount += sub.Indices.Length;
            }
        }
        if (totalVertexCount == 0 || totalIndexCount == 0) return null;

        // Capture walk-mode collision geometry from the solid submeshes before they are packed for the
        // GPU. Free here (positions+indices already in hand) and independent of GPU upload success.
        StoreCollisionMesh(modelPath, decoded);

        var vertices = new GpuMeshUploader.GpuVertex[totalVertexCount];
        var indices = new ushort[totalIndexCount];
        var vertexStarts = new int[decoded.Submeshes.Count];
        var indexStarts = new int[decoded.Submeshes.Count];

        var vertexCursor = 0;
        var indexCursor = 0;
        for (var i = 0; i < decoded.Submeshes.Count; i++)
        {
            var sub = decoded.Submeshes[i];
            vertexStarts[i] = vertexCursor;
            indexStarts[i] = indexCursor;
            sub.Vertices.AsSpan().CopyTo(vertices.AsSpan(vertexCursor));
            sub.Indices.AsSpan().CopyTo(indices.AsSpan(indexCursor));
            vertexCursor += sub.Vertices.Length;
            indexCursor += sub.Indices.Length;
        }

        GeometryAllocation12 geometry;
        try
        {
            // memcpy the packed vertex/index bytes into a shared UPLOAD-heap arena sub-range — no
            // per-mesh committed resource, staging buffer, copy, or barrier (the churn the profiler
            // flagged). VBV/IBV bind directly against the arena block's GPU virtual address.
            geometry = _geometryArena.Upload(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes<GpuMeshUploader.GpuVertex>(vertices),
                System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>(indices));
        }
        catch (Exception ex)
        {
            Log.Warn("ReferenceMeshCache12: geometry arena upload failed for '{0}': {1}", modelPath, ex.Message);
            return null;
        }

        // Conservative whole-mesh bounding radius around the NIF origin (max vertex distance from
        // local 0,0,0) AND the mesh-local AABB (min/max XYZ). One pass over the combined vertex array;
        // the radius sizes the reference cull's sphere from real geometry rather than the OBND estimate,
        // and the AABB gives the selection highlight a tight box for refs with no OBND (every Oblivion
        // ref — see WorldView3DControl.UpdateHighlightFromSelection / OBLIV-1).
        var meshLocalRadiusSq = 0f;
        var aabbMin = new Vector3(float.PositiveInfinity);
        var aabbMax = new Vector3(float.NegativeInfinity);
        foreach (var v in vertices)
        {
            var p = v.Position;
            var lenSq = p.LengthSquared();
            if (lenSq > meshLocalRadiusSq) meshLocalRadiusSq = lenSq;
            aabbMin = Vector3.Min(aabbMin, p);
            aabbMax = Vector3.Max(aabbMax, p);
        }

        // Degenerate (no vertices) → collapse the AABB to the origin so consumers see min == max and
        // fall back to the sphere rather than an inverted box.
        if (aabbMin.X > aabbMax.X)
        {
            aabbMin = aabbMax = Vector3.Zero;
        }

        var submeshes = new List<CachedSubmesh12>(decoded.Submeshes.Count);
        // Local AABBs of any WaterShaderProperty submeshes (caves/pools/reflecting pools), captured
        // so the dedicated water renderer can place them as real water planes — see the divert below.
        List<(Vector3 Min, Vector3 Max)>? waterPlanesLocal = null;
        var vertexStride = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        for (var i = 0; i < decoded.Submeshes.Count; i++)
        {
            var sub = decoded.Submeshes[i];
            // Placed water-shader geometry (WaterShaderProperty NIFs — caves/pools/reflecting pools)
            // is NOT drawn as a reference slab. Divert it: capture the plane's local AABB so the
            // dedicated WaterRenderer12 can render it with the real Fresnel/ripple/depth-fade water
            // shader, and skip building a drawable submesh — that omission is what gates off the old
            // flat translucent-tile fallback (the submesh never becomes a reference draw → no double-draw).
            if (string.Equals(sub.DiffuseTexturePath, RenderableSubmesh.WaterSurfaceTexturePath, StringComparison.Ordinal))
            {
                AppendWaterPlaneLocalBounds(sub.Vertices, ref waterPlanesLocal);
                continue;
            }

            // Refraction geometry (Oblivion's Refract.dds marker — Oblivion-gate portal rings, magic
            // refraction) is rendered by the engine via framebuffer distortion; the static viewer can't
            // reproduce it. Refract.dds is a ~58-byte placeholder, so drawing the submesh as a reference
            // slab shows opaque-white blobs (OblivionArchGate01's three "untextured circles"). Skip it —
            // keyed on the engine's refraction-marker texture, not a shape-name heuristic.
            if (sub.DiffuseTexturePath is { } refractPath &&
                refractPath.EndsWith("refract.dds", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                GpuTextureCache12.Entry diffuse;
                if (string.IsNullOrEmpty(sub.DiffuseTexturePath))
                {
                    diffuse = _textureCache.WhitePixel;
                }
                else
                {
                    diffuse = string.Equals(sub.DiffuseTexturePath, RenderableSubmesh.WaterSurfaceTexturePath, StringComparison.Ordinal)
                        ? _textureCache.WaterSurface
                        : _textureCache.GetOrUpload(sub.DiffuseTexturePath!);
                }
                var normal = !string.IsNullOrEmpty(sub.NormalMapTexturePath)
                    ? _textureCache.GetOrUpload(sub.NormalMapTexturePath!, isNormalMap: true)
                    : _textureCache.FlatNormal;

                var vertexByteOffset = CheckedByteSize(vertexStarts[i], vertexStride);
                var vertexByteSize = CheckedByteSize(sub.Vertices.Length, vertexStride);
                var indexByteOffset = CheckedByteSize(indexStarts[i], sizeof(ushort));
                var indexByteSize = CheckedByteSize(sub.Indices.Length, sizeof(ushort));
                submeshes.Add(new CachedSubmesh12
                {
                    VertexBufferView = new VertexBufferView
                    {
                        BufferLocation = geometry.VertexBufferLocation + vertexByteOffset,
                        SizeInBytes = vertexByteSize,
                        StrideInBytes = vertexStride
                    },
                    IndexBufferView = new IndexBufferView
                    {
                        BufferLocation = geometry.IndexBufferLocation + indexByteOffset,
                        SizeInBytes = indexByteSize,
                        Format = Vortice.DXGI.Format.R16_UInt
                    },
                    IndexCount = sub.Indices.Length,
                    Diffuse = diffuse,
                    Normal = normal,
                    AlphaState = BuildAlphaState(sub),
                    RenderState = BuildRenderState(sub),
                    Specular = BuildSpecular(sub),
                    HasBump = sub.HasBump,
                    AlphaRenderMode = sub.AlphaRenderMode,
                    AlphaBlend = sub.AlphaBlend,
                    AlphaTest = sub.AlphaTest,
                    AlphaTestThreshold = sub.AlphaTestThreshold,
                    AlphaTestFunction = sub.AlphaTestFunction,
                    SrcBlendMode = sub.SrcBlendMode,
                    DstBlendMode = sub.DstBlendMode,
                    MaterialAlpha = sub.MaterialAlpha,
                    DoubleSided = sub.DoubleSided,
                    IsEmissive = sub.IsEmissive,
                    LocalBoundsCenter = sub.LocalBoundsCenter,
                    IsBillboard = sub.IsBillboard,
                    IsLeafBillboard = sub.IsLeafBillboard,
                    DepthWritingBlend = sub.DepthWritingBlend
                });
            }
            catch (Exception ex)
            {
                Log.Warn("ReferenceMeshCache12: submesh upload failed for '{0}': {1}", modelPath, ex.Message);
            }
        }

        if (submeshes.Count == 0 && (waterPlanesLocal is null || waterPlanesLocal.Count == 0))
        {
            // No drawable submeshes and no water planes — nothing references the arena range, so reclaim it.
            _geometryArena.Free(geometry);
            return null;
        }

        success = true;
        var cached = new CachedNifMesh12(
            submeshes, geometry, _geometryArena, _deletionQueue, _textureCache, MathF.Sqrt(meshLocalRadiusSq),
            aabbMin, aabbMax,
            (IReadOnlyList<(Vector3 Min, Vector3 Max)>?)waterPlanesLocal ?? Array.Empty<(Vector3 Min, Vector3 Max)>());
        if (started != 0)
        {
            RendererProfilerTrace.Event("resource-event", new Dictionary<string, object?>
            {
                ["resource"] = "reference-mesh",
                ["phase"] = "gpu-upload",
                ["path"] = modelPath,
                ["success"] = success,
                ["vertices"] = totalVertexCount,
                ["indices"] = totalIndexCount,
                ["submeshes"] = submeshes.Count,
                ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }

        return cached;
    }

    /// <summary>
    ///     Builds walk-mode collision geometry from the solid submeshes and stores it under the
    ///     normalized model path. Render-thread only (called from <see cref="UploadDecodedMesh" />).
    /// </summary>
    private void StoreCollisionMesh(string modelPath, DecodedNifMesh12 decoded)
    {
        var collision = BuildCollisionMesh(decoded);
        if (collision is null) return;
        // Set evicts over-budget tail entries (plain drop — collision payloads are GC-reclaimed).
        _collisionLru.Set(ReferenceMeshDecoder12.NormalizeModelPath(modelPath), collision);
    }

    /// <summary>
    ///     Merges the decoded mesh's <b>solid</b> submeshes into one mesh-local triangle soup. Skips
    ///     alpha-blended (glass/effect/force-field), emissive (glow cards), and water-shader submeshes
    ///     so the camera snaps onto real surfaces only — never a translucent FX plane. Mesh-local space
    ///     matches <c>RenderableReference.WorldMatrix</c> (both come from the <c>treatRootsAsIdentity</c>
    ///     decode). Returns <c>null</c> when nothing solid remains.
    /// </summary>
    private static CollisionMesh? BuildCollisionMesh(DecodedNifMesh12 decoded)
    {
        // Prefer the NIF's Havok (bhk*) collision mesh when present — it's the gapless physics surface,
        // so walk mode rides plank bridges / catwalks instead of falling through the visual-mesh gaps.
        if (decoded.CollisionTriangles is { Length: >= 3 } havokTris &&
            decoded.CollisionPositions is { Length: > 0 } havokPos)
        {
            return new CollisionMesh(havokPos, havokTris);
        }

        var positions = new List<Vector3>();
        var triangles = new List<int>();
        foreach (var sub in decoded.Submeshes)
        {
            if (sub.AlphaBlend || sub.IsEmissive) continue;
            if (string.Equals(sub.DiffuseTexturePath, RenderableSubmesh.WaterSurfaceTexturePath, StringComparison.Ordinal))
                continue;
            if (sub.Vertices.Length == 0 || sub.Indices.Length == 0) continue;

            var baseIndex = positions.Count;
            foreach (var v in sub.Vertices) positions.Add(v.Position);
            foreach (var idx in sub.Indices) triangles.Add(baseIndex + idx);
        }

        if (triangles.Count < 3) return null;
        return new CollisionMesh(positions.ToArray(), triangles.ToArray());
    }

    private static Vector4 BuildAlphaState(DecodedSubmesh12 sub)
    {
        // The alpha test discards sub-threshold texels in the pixel shader. We normally skip it for plain
        // Blend (blend itself fades transparency, no discard needed). But a DepthWritingBlend shape ALSO
        // writes depth, so it MUST discard its transparent texels — otherwise the whole card writes depth
        // and occludes whatever's behind it (e.g. NVSeaPlant02 punching holes in the water and showing only
        // a faint full-card haze). With the test on, the card background discards (no depth, no haze) and
        // only the surviving fronds blend + write depth — exactly the engine's blend+test+ZBuffer_Write.
        var alphaTestEnabled = (sub.AlphaRenderMode != NifAlphaRenderMode.Blend || sub.DepthWritingBlend)
                               && sub.AlphaTest;
        return new Vector4(
            alphaTestEnabled ? sub.AlphaTestThreshold : 0f,
            alphaTestEnabled ? sub.AlphaTestFunction : -1f,
            Math.Clamp(sub.MaterialAlpha, 0f, 1f),
            sub.AlphaRenderMode == NifAlphaRenderMode.Blend ? 1f : 0f);
    }

    private static Vector4 BuildRenderState(DecodedSubmesh12 sub) =>
        new(
            sub.DoubleSided ? 1f : 0f,
            sub.HasBump ? 1f : 0f,
            ReferenceBumpStrength,
            sub.IsEmissive ? 1f : 0f);

    // GPU specular term (1A): xyz = specular tint, w = Phong exponent (glossiness). w == 0 is the
    // shader's "no specular" sentinel, so a disabled material uploads Vector4.Zero. Glossiness is floored
    // at 1 to avoid pow(_, 0) flattening the highlight across the whole surface.
    private static Vector4 BuildSpecular(DecodedSubmesh12 sub) =>
        sub.SpecularEnabled
            ? new Vector4(sub.SpecularColor, MathF.Max(sub.Glossiness, 1f))
            : Vector4.Zero;

    private static uint CheckedByteSize(int elementCount, uint elementSize) =>
        checked((uint)((long)elementCount * elementSize));

    // Accumulates one water plane's mesh-local AABB (min/max XYZ over its vertices). The water
    // renderer expands a flat quad over the world-space footprint, so only the extent matters; the
    // per-reference world matrix is applied later (ReferenceRenderer12) once the placement is known.
    private static void AppendWaterPlaneLocalBounds(
        GpuMeshUploader.GpuVertex[] vertices,
        ref List<(Vector3 Min, Vector3 Max)>? planes)
    {
        if (vertices.Length == 0) return;
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var v in vertices)
        {
            min = Vector3.Min(min, v.Position);
            max = Vector3.Max(max, v.Position);
        }

        (planes ??= new List<(Vector3 Min, Vector3 Max)>(1)).Add((min, max));
    }

    private static int ParsePositiveIntEnvironment(string name, int defaultValue, int min, int max)
    {
        var raw = EnvironmentVariables.Get(name);
        if (!int.TryParse(raw, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static long ParsePositiveLongEnvironment(string name, long defaultValue, long min, long max)
    {
        var raw = EnvironmentVariables.Get(name);
        if (!long.TryParse(raw, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private sealed class Node(
        CachedNifMesh12? Mesh,
        Task<DecodedNifMesh12?>? DecodeTask,
        bool ResolvedNull)
    {
        public CachedNifMesh12? Mesh { get; set; } = Mesh;
        public Task<DecodedNifMesh12?>? DecodeTask { get; set; } = DecodeTask;
        public bool ResolvedNull { get; set; } = ResolvedNull;
        public bool DecodeQueued { get; set; }
        public bool DecodedCacheAvailable { get; set; }
        public bool DecodedCacheMissRecorded { get; set; }

        /// <summary>Plain normalized archive path (no '#variant' suffix) — used for decode, on-disk
        /// cache metadata, and collision keying, all of which are texture-variant-independent.</summary>
        public string DecodePath { get; init; } = "";

        /// <summary>MODS alternate-texture overrides (shape name → texture paths) to bake into the
        /// decoded submeshes, or null when this is the mesh's default (non-re-skinned) variant.</summary>
        public IReadOnlyDictionary<string, ShapeTextureOverride>? Overrides { get; init; }

        /// <summary>True when this node is a MODS re-skin variant. Disk persistence is skipped for
        /// variants (the on-disk cache keys on the NIF's archive identity, which is shared across
        /// variants and would collide); the in-memory LRUs still de-dupe by the '#variant' cache key.</summary>
        public bool HasVariant { get; init; }
    }

    private readonly record struct DecodedCacheValue(
        DecodedNifMesh12? Mesh,
        bool IsNegative,
        long ByteSize);
}
#endif
