#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using FalloutXbox360Utils.Core.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.SpeedTree;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using FalloutXbox360Utils.Core.Orchestration;
using FalloutXbox360Utils.Core.Resources;
using Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12;

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
    private const float ReferenceBumpStrength = 0.35f;

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

    private readonly CLI.NpcMeshArchiveSet _meshArchives;
    private readonly NifTextureResolver _textureResolver;
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
    private int _totalMissingModelPaths;
    private int _totalSkinnedModelPaths;
    private FrameByteBudget _frameUploadByteBudget;
    // When true, LoadData resizes _meshLru to the worldspace's working set (default). False when the
    // FALLOUT_VIEWER_REFERENCE_MESH_CAPACITY env knob pins a fixed cap for eviction-cascade stress gates.
    private readonly bool _autoSizeMeshCapacity;

    // archive-path (trees\<name>.spt) → recorded tree height (TREE OBND Z-extent), so the procedural
    // SpeedTree generator can size each tree from the ESM data rather than a magic constant.
    private readonly IReadOnlyDictionary<string, float>? _speedTreeHeights;
    private bool _disposed;

    public ReferenceMeshCache12(
        GpuDevice12 gpu,
        CLI.NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        GpuTextureCache12 textureCache,
        GpuDeletionQueue12 deletionQueue,
        int capacity,
        long decodedCacheByteBudget = DefaultDecodedMeshCacheByteBudget,
        ReferenceDecodedMeshDiskCache12? persistentDecodedCache = null,
        bool autoSizeMeshCapacity = true,
        IReadOnlyDictionary<string, float>? speedTreeHeights = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");

        _speedTreeHeights = speedTreeHeights;
        _meshArchives = meshArchives;
        _textureResolver = textureResolver;
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
        if (_collisionLru.TryGet(NormalizeModelPath(modelPath), out var mesh))
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
    public int TotalMissingModelPaths => Volatile.Read(ref _totalMissingModelPaths);
    public int TotalSkinnedModelPaths => Volatile.Read(ref _totalSkinnedModelPaths);

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

    public CachedNifMesh12? GetOrUpload(string modelPath, ref int uploadBudget, float priority = 0f)
    {
        if (_disposed) return null;

        PruneCompletedDecodeTasks();
        StartQueuedDecodes();

        var cacheKey = NormalizeModelPath(modelPath);
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
            ResolvedNull: false);
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
            if (!TryLoadPersistentDecodedCache(modelPath, out decoded) &&
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
        mesh = UploadDecodedMesh(modelPath, decoded.Mesh);
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
            node.DecodeTask = StartDecodeTask(modelPath);
            FrameDecodeRequests++;
            FrameDecodeStarts++;
        }
    }

    private Task<DecodedNifMesh12?> StartDecodeTask(string modelPath)
    {
        Interlocked.Increment(ref _activeDecodeTasks);
        var task = Task.Run(() =>
        {
            try
            {
                var decoded = DecodeMesh(modelPath);
                StoreDecodedCache(modelPath, decoded);
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
            : FromPersistentPayload(cached.Mesh);
        StoreDecodedCache(modelPath, decoded, persist: false);
        return TryGetDecodedCache(modelPath, out value);
    }

    private void StoreDecodedCache(string modelPath, DecodedNifMesh12? decoded, bool persist = true)
    {
        var byteSize = decoded is null
            ? 1L
            : Math.Max(1L, EstimateDecodedMeshBytes(decoded));
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
                decoded is null ? null : ToPersistentPayload(decoded));
        }
        catch (Exception ex)
        {
            Log.Warn("ReferenceMeshCache12: decoded mesh disk cache write skipped for '{0}': {1}", modelPath, ex.Message);
        }
    }

    private static ReferenceDecodedMeshPayload12 ToPersistentPayload(DecodedNifMesh12 decoded)
    {
        var submeshes = new List<ReferenceDecodedSubmeshPayload12>(decoded.Submeshes.Count);
        foreach (var sub in decoded.Submeshes)
        {
            submeshes.Add(new ReferenceDecodedSubmeshPayload12(
                sub.Vertices,
                sub.Indices,
                sub.DiffuseTexturePath,
                sub.NormalMapTexturePath,
                sub.HasBump,
                sub.AlphaRenderMode,
                sub.AlphaBlend,
                sub.AlphaTest,
                sub.AlphaTestThreshold,
                sub.AlphaTestFunction,
                sub.SrcBlendMode,
                sub.DstBlendMode,
                sub.MaterialAlpha,
                sub.DoubleSided,
                sub.IsEmissive,
                sub.LocalBoundsCenter,
                sub.IsBillboard));
        }

        return new ReferenceDecodedMeshPayload12(submeshes);
    }

    private static DecodedNifMesh12 FromPersistentPayload(ReferenceDecodedMeshPayload12 payload)
    {
        var submeshes = new List<DecodedSubmesh12>(payload.Submeshes.Count);
        foreach (var sub in payload.Submeshes)
        {
            submeshes.Add(new DecodedSubmesh12(
                sub.Vertices,
                sub.Indices,
                sub.DiffuseTexturePath,
                sub.NormalMapTexturePath,
                sub.HasBump,
                sub.AlphaRenderMode,
                sub.AlphaBlend,
                sub.AlphaTest,
                sub.AlphaTestThreshold,
                sub.AlphaTestFunction,
                sub.SrcBlendMode,
                sub.DstBlendMode,
                sub.MaterialAlpha,
                sub.DoubleSided,
                sub.IsEmissive,
                sub.LocalBoundsCenter,
                sub.IsBillboard));
        }

        return new DecodedNifMesh12(submeshes);
    }

    private static long EstimateDecodedMeshBytes(DecodedNifMesh12 decoded)
    {
        var vertexSize = System.Runtime.InteropServices.Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        long total = 0;
        foreach (var submesh in decoded.Submeshes)
        {
            total += (long)submesh.Vertices.Length * vertexSize;
            total += (long)submesh.Indices.Length * sizeof(ushort);
            total += 256;
        }

        return total;
    }

    private DecodedNifMesh12? DecodeMesh(string modelPath)
    {
        var started = RendererProfilerTrace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        var lookupPath = NormalizeModelPath(modelPath);
        var result = "missing";
        var nifBytes = 0;
        var submeshCount = 0;

        try
        {
            // No lock needed: NpcMeshArchiveSet.TryExtractFile resolves under its own cache lock and
            // BsaExtractor.ExtractFile is memory-mapped/lock-free, so concurrent decode tasks extract
            // in parallel. This is what actually parallelizes mesh streaming (removing the old coarse
            // archive lock that serialized every decode despite the wider task pool).
            if (!_meshArchives.TryExtractFile(lookupPath, out var nifData, out _) || nifData.Length == 0)
            {
                Interlocked.Increment(ref _totalMissingModelPaths);
                return null;
            }

            nifBytes = nifData.Length;

            NifRenderableModel? model;
            if (lookupPath.EndsWith(".spt", StringComparison.OrdinalIgnoreCase))
            {
                // SpeedTree tree: the .spt is a procedural recipe, not a NIF. Parse it and generate
                // geometry (branch tubes + scattered leaf cards) via SptGeometryBuilder. Size is
                // data-driven from the TREE record's OBND height; the seed is the .spt's own embedded
                // value (token 2005 == TREE SNAM), falling back to a stable per-path hash.
                var spt = SptFile.TryParse(nifData);
                if (spt is null)
                {
                    result = "spt-parse-failed";
                    return null;
                }

                float? targetHeight = null;
                if (_speedTreeHeights is not null && _speedTreeHeights.TryGetValue(lookupPath, out var h))
                {
                    targetHeight = h;
                }

                var seed = spt.General.Token2005 != 0 ? spt.General.Token2005 : StableSeed(lookupPath);
                var sptOptions = SptGeometryOptions.FromEnvironment() with { TargetHeight = targetHeight };
                model = SptGeometryBuilder.Build(spt, seed, sptOptions);
            }
            else
            {
                // Cheap endianness probe avoids fully parsing the source NIF just to decide whether it
                // needs big-endian conversion. A determinate probe (true/false) is byte-for-byte the same
                // verdict a full parse would give; null means the header wasn't cheaply readable, so fall
                // back to a full parse. This drops the BE mesh path from three parses (source +
                // convert-internal + converted output) to two.
                var probe = NifParser.TryProbeEndianness(nifData);
                NifInfo? sourceInfo = null;
                bool isBigEndian;
                if (probe.HasValue)
                {
                    isBigEndian = probe.Value;
                }
                else
                {
                    sourceInfo = NifParser.Parse(nifData);
                    if (sourceInfo is null)
                    {
                        result = "parse-failed";
                        return null;
                    }

                    isBigEndian = sourceInfo.IsBigEndian;
                }

                NifInfo? nif;
                if (isBigEndian)
                {
                    var converted = NifConverter.Convert(nifData);
                    if (!converted.Success || converted.OutputData is null)
                    {
                        result = "convert-failed";
                        return null;
                    }

                    nifData = converted.OutputData;
                    nif = NifParser.Parse(nifData);
                    if (nif is null)
                    {
                        result = "converted-parse-failed";
                        return null;
                    }
                }
                else
                {
                    // LE source: reuse the fallback parse if we already made one, else parse now.
                    nif = sourceInfo ?? NifParser.Parse(nifData);
                    if (nif is null)
                    {
                        result = "parse-failed";
                        return null;
                    }
                }

                model = NifGeometryExtractor.Extract(
                    nifData, nif,
                    textureResolver: _textureResolver,
                    bindPoseOnly: false,
                    skipSkinning: true,
                    // Placed references get their scene-root world transform from the REFR placement
                    // (RenderableReference.ComposeWorldMatrix). Discard the root node's OWN authored
                    // transform so a non-identity root rotation (e.g. McMarranWalls wallReg at 90°,
                    // monorail curves at 15°) is not injected twice — which rendered those meshes
                    // rotated by the root angle (perpendicular for the 90° walls).
                    treatRootsAsIdentity: true,
                    // Flag geometry under a NiBillboardNode (e.g. NVashpile smoke glow) so the renderer
                    // re-aims it at the camera per frame instead of using the baked-in orientation.
                    collectBillboards: true);
            }

            if (model is null)
            {
                result = "extract-failed";
                return null;
            }
            if (model.WasSkinned)
            {
                result = "skinned";
                Interlocked.Increment(ref _totalSkinnedModelPaths);
                return null;
            }
            if (!model.HasGeometry)
            {
                result = "empty";
                return null;
            }

            var submeshes = new List<DecodedSubmesh12>(model.Submeshes.Count);
            foreach (var sub in model.Submeshes)
            {
                if (sub.Positions.Length == 0 || sub.Triangles.Length == 0) continue;

                var alphaState = NifAlphaClassifier.Classify(sub, diffuseTexture: null);
                var alphaRenderMode = alphaState.RenderMode == NifAlphaRenderMode.AlphaToCoverage
                    ? NifAlphaRenderMode.Blend
                    : alphaState.RenderMode;
                var hasBump = sub.Tangents != null &&
                              sub.Bitangents != null &&
                              !string.IsNullOrEmpty(sub.NormalMapTexturePath);

                submeshes.Add(new DecodedSubmesh12(
                    GpuMeshUploader.BuildVertices(sub),
                    sub.Triangles,
                    sub.DiffuseTexturePath,
                    hasBump ? sub.NormalMapTexturePath : null,
                    hasBump,
                    alphaRenderMode,
                    alphaState.HasAlphaBlend,
                    alphaState.HasAlphaTest,
                    alphaState.AlphaTestThreshold / 255f,
                    alphaState.AlphaTestFunction,
                    alphaState.SrcBlendMode,
                    alphaState.DstBlendMode,
                    alphaState.MaterialAlpha,
                    sub.IsDoubleSided,
                    sub.IsEmissive,
                    ComputeLocalBoundsCenter(sub.Positions),
                    sub.IsBillboard));
            }

            if (submeshes.Count == 0)
            {
                result = "empty";
                return null;
            }

            submeshCount = submeshes.Count;
            result = "success";
            return new DecodedNifMesh12(submeshes);
        }
        finally
        {
            if (started != 0)
            {
                RendererProfilerTrace.Event("resource-event", new Dictionary<string, object?>
                {
                    ["resource"] = "reference-mesh",
                    ["phase"] = "decode",
                    ["path"] = lookupPath,
                    ["result"] = result,
                    ["bytes"] = nifBytes,
                    ["submeshes"] = submeshCount,
                    ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                });
            }
        }
    }

    /// <summary>
    ///     Stable FNV-1a hash of the model path, used to seed the deterministic SpeedTree generator so
    ///     the same tree type produces identical geometry across decode/cache cycles and process runs.
    /// </summary>
    private static uint StableSeed(string path)
    {
        var hash = 2166136261u;
        foreach (var ch in path)
        {
            hash = (hash ^ char.ToLowerInvariant(ch)) * 16777619u;
        }

        return hash;
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
        // local 0,0,0). One pass over the combined vertex array; used by the reference cull to size
        // each placement's sphere from real geometry rather than the OBND estimate.
        var meshLocalRadiusSq = 0f;
        foreach (var v in vertices)
        {
            var lenSq = v.Position.LengthSquared();
            if (lenSq > meshLocalRadiusSq) meshLocalRadiusSq = lenSq;
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
            try
            {
                var diffuse = string.IsNullOrEmpty(sub.DiffuseTexturePath)
                    ? _textureCache.WhitePixel
                    : string.Equals(sub.DiffuseTexturePath, RenderableSubmesh.WaterSurfaceTexturePath, StringComparison.Ordinal)
                        ? _textureCache.WaterSurface
                        : _textureCache.GetOrUpload(sub.DiffuseTexturePath!);
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
                    IsBillboard = sub.IsBillboard
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
        _collisionLru.Set(NormalizeModelPath(modelPath), collision);
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
        var alphaTestEnabled = sub.AlphaRenderMode != NifAlphaRenderMode.Blend && sub.AlphaTest;
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

    private static Vector3 ComputeLocalBoundsCenter(float[] positions)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return Vector3.Multiply(min + max, 0.5f);
    }

    private static string NormalizeModelPath(string modelPath)
    {
        var normalized = modelPath.Replace('/', '\\').Trim();

        // SpeedTree trees ship at the BSA root under "trees\" (e.g. "trees\wastelandshrub01.spt"),
        // NOT under "meshes\" like NIFs — and the TREE MODL is typically a bare name with a leading
        // backslash ("\WastelandShrub01.spt"). Normalize to "trees\<name>.spt" so the archive hit
        // matches; prepending "meshes\" (correct for NIFs) would miss every .spt and the tree would
        // silently render nothing.
        if (SpeedTreeModelPath.IsSpt(normalized))
        {
            return SpeedTreeModelPath.ToArchivePath(normalized);
        }

        return normalized.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : "meshes\\" + normalized.TrimStart('\\');
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
    }

    private readonly record struct DecodedCacheValue(
        DecodedNifMesh12? Mesh,
        bool IsNegative,
        long ByteSize);

    private sealed record DecodedNifMesh12(IReadOnlyList<DecodedSubmesh12> Submeshes);

    private sealed record DecodedSubmesh12(
        GpuMeshUploader.GpuVertex[] Vertices,
        ushort[] Indices,
        string? DiffuseTexturePath,
        string? NormalMapTexturePath,
        bool HasBump,
        NifAlphaRenderMode AlphaRenderMode,
        bool AlphaBlend,
        bool AlphaTest,
        float AlphaTestThreshold,
        byte AlphaTestFunction,
        byte SrcBlendMode,
        byte DstBlendMode,
        float MaterialAlpha,
        bool DoubleSided,
        bool IsEmissive,
        Vector3 LocalBoundsCenter,
        bool IsBillboard);
}

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
        IReadOnlyList<(Vector3 Min, Vector3 Max)> waterPlanesLocal)
    {
        Submeshes = submeshes;
        _geometry = geometry;
        _arena = arena;
        _deletionQueue = deletionQueue;
        _textureCache = textureCache;
        LocalBoundsRadius = localBoundsRadius;
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
        }
    }
}

internal sealed class CachedSubmesh12
{
    private Vector4 _textureState;
    private bool _textureStateCached;

    public required VertexBufferView VertexBufferView { get; init; }
    public required IndexBufferView IndexBufferView { get; init; }
    public required int IndexCount { get; init; }
    public required GpuTextureCache12.Entry Diffuse { get; init; }
    public required GpuTextureCache12.Entry Normal { get; init; }
    public required Vector4 AlphaState { get; init; }
    public required Vector4 RenderState { get; init; }
    public Vector4 TextureState
    {
        get
        {
            if (_textureStateCached)
            {
                return _textureState;
            }

            var state = new Vector4(
                Normal.NormalDecodeMode == GpuNormalDecodeMode.Bc5ReconstructZ ? 1f : 0f,
                0f,
                0f,
                0f);
            if (TexturesReady)
            {
                _textureState = state;
                _textureStateCached = true;
            }

            return state;
        }
    }
    public bool TexturesReady => Diffuse.IsReady && Normal.IsReady;
    public required bool HasBump { get; init; }
    public required NifAlphaRenderMode AlphaRenderMode { get; init; }
    public required bool AlphaBlend { get; init; }
    public required bool AlphaTest { get; init; }
    public required float AlphaTestThreshold { get; init; }
    public required byte AlphaTestFunction { get; init; }
    public required byte SrcBlendMode { get; init; }
    public required byte DstBlendMode { get; init; }
    public required float MaterialAlpha { get; init; }
    public required bool DoubleSided { get; init; }
    public required bool IsEmissive { get; init; }
    public required Vector3 LocalBoundsCenter { get; init; }

    /// <summary>
    ///     True if this submesh sat under a <c>NiBillboardNode</c> in the source NIF. The renderer
    ///     routes it to the per-draw blended path and replaces the placement world matrix with a
    ///     cylindrical camera-facing matrix so the quad re-aims at the camera every frame.
    /// </summary>
    public required bool IsBillboard { get; init; }
}
#endif
