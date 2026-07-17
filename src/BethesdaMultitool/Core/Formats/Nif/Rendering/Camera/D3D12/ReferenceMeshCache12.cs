#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Effects;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
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
    // scales with cores but stays GC-guarded: HALF the logical processors, clamped to [2, 12]. This
    // leaves headroom for the render + UI + GC threads (full-core saturation caused the GC pauses the
    // old conservative default of 2 was guarding against). The ceiling rose 8→12 when disk-cache
    // persists moved off the decode workers onto the background persist writer — a decode slot is now
    // pure CPU decode, so high-core machines (24+ threads) get the extra workers a cold Commonwealth
    // load can use. Env override for profiling specific machines.
    private static readonly int DefaultMaxConcurrentDecodeTasks = ParsePositiveIntEnvironment(
        EnvironmentVariables.Viewer.ReferenceDecodeConcurrency,
        defaultValue: Math.Clamp(Environment.ProcessorCount / 2, 2, 12),
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
    // Disk-persist handoff: decode workers enqueue (path, mesh) and free their slot immediately;
    // payload serialization + the atomic file write run on the single background writer below
    // instead of inside the decode task (cold loads were spending decode-worker time on disk I/O).
    // Bounded: when the writer falls behind, TryWrite fails and the decode worker writes
    // synchronously — natural backpressure, never a dropped persist.
    private readonly Channel<(string DecodePath, string? VariantKey, DecodedNifMesh12? Decoded)>? _persistQueue;
    private readonly Task? _persistWriterTask;
    private readonly object _decodedCacheLock = new();
    private int _activeDecodeTasks;
    private FrameByteBudget _frameUploadByteBudget;
    // Accumulated-upload-TIME pacer (milliseconds of actual UploadDecodedMesh work this frame).
    // Replaces the renderer's wall-clock deadline, which was measured from frame start and so
    // expired before the resolve loop ever REACHED far-cell survivors (~20-30ms of iteration at
    // 100k survivors) — far meshes with ready payloads could never start an upload, starving the
    // fill by distance ("assets stream in very slowly downtown", and frozen batch-reuse fills).
    private double _frameUploadMsBudget;
    private double _frameUploadMsConsumed;
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
        IReadOnlyDictionary<string, string>? speedTreeLeafTextures = null,
        IReadOnlyDictionary<string, SpeedTreeDimming>? speedTreeDimming = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");

        _meshArchives = meshArchives;
        // speedTreeHeights: null — the engine renders .spt geometry at its natural world scale; the old
        // TREE-OBND rescale oversized shrubs (param retained on the decoder until its file frees up).
        _decoder = new ReferenceMeshDecoder12(meshArchives, textureResolver, speedTreeHeights: null,
            speedTreeLeafTextures, speedTreeDimming);
        _textureCache = textureCache;
        _deletionQueue = deletionQueue;
        _geometryArena = new GpuGeometryArena12(gpu)
            .RegisterWith(Core.Diagnostics.ResourceRegistry.Instance, "reference");
        _persistentDecodedCache = persistentDecodedCache ?? ReferenceDecodedMeshDiskCache12.CreateFromEnvironment();
        if (_persistentDecodedCache is not null)
        {
            _persistQueue = Channel.CreateBounded<(string, string?, DecodedNifMesh12?)>(
                new BoundedChannelOptions(64) { SingleReader = true });
            _persistWriterTask = Task.Run(ProcessPersistQueueAsync);
        }

        Capacity = capacity;
        DecodedMeshCacheByteBudget = decodedCacheByteBudget;
        _autoSizeMeshCapacity = autoSizeMeshCapacity;
        // No sizeOf: GPU bytes are already tracked by GpuGeometryArena12["reference"] — sizing
        // this cache too would double-count them in the registry.
        _meshLru = new LruCache<string, Node>(
                "ReferenceMeshLru",
                ResourceCategory.GpuResident,
                maxEntries: capacity,
                onEvicted: (_, node) =>
                {
                    node.Mesh?.Dispose();
                    // Any eviction invalidates frozen (reused) reference batches: their instance
                    // lists key on CachedSubmesh12 objects whose GPU buffers just entered the
                    // deletion queue. The renderer compares this against its build snapshot.
                    EvictionGeneration++;
                },
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

    /// <summary>
    ///     Collision-overlay cold path. Promotes an already-queued plain-model decode to the supplied
    ///     nearest-instance priority <em>before</em> <see cref="GetOrUpload" /> starts queued work, then
    ///     gives one decoded mesh a chance to upload within the frame's existing byte/time budgets.
    ///     The caller bounds how many unique paths it offers per frame.
    /// </summary>
    public CollisionMesh? GetOrWarmCollisionMesh(string modelPath, float priority)
    {
        if (TryGetCollisionMesh(modelPath, out var collision)) return collision;
        if (_disposed) return null;

        var normalizedPath = ReferenceMeshDecoder12.NormalizeModelPath(modelPath);
        if (_meshLru.TryPeek(normalizedPath, out var node) && node.DecodeQueued)
        {
            // GetOrUpload normally starts the queue before re-offering an existing node's latest
            // distance. The collision overlay already did a global nearest-first sort, so promote
            // here first or an older far-field priority could win the newly-open worker slot.
            _decodeQueue.Enqueue(normalizedPath, priority);
        }

        var uploadBudget = 1;
        _ = GetOrUpload(normalizedPath, ref uploadBudget, priority);
        return TryGetCollisionMesh(normalizedPath, out collision) ? collision : null;
    }

    public int Capacity { get; }
    public int Count => _meshLru.Count;

    /// <summary>
    ///     Bumped on every resident-mesh eviction (LRU overflow, capacity shrink, dispose cascade).
    ///     Batch reuse keys on it: a frozen batch built under an older generation may reference
    ///     evicted GPU buffers and must rebuild. Render-thread only, like the LRU it mirrors.
    /// </summary>
    public int EvictionGeneration { get; private set; }

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

    /// <summary>
    ///     Time-based streaming pace: per-frame allowances × the last frame's duration relative to
    ///     60fps (see <see cref="Core.Resources.StreamingFrameBudgetScaler" />). Keeps loading
    ///     throughput frame-rate-independent — a viewer starved to a few FPS by GPU contention
    ///     (another game running) otherwise streams at a few percent of its normal rate. Updated by
    ///     <see cref="ResetFrameStats" />; also consumed by the renderer's upload time budget.
    /// </summary>
    public double FrameBudgetScale { get; private set; } = 1.0;

    private long _lastFrameTimestamp;

    private int EffectiveMaxDecodeStartsPerFrame =>
        StreamingThrottled
            ? Core.Resources.StreamingFrameBudgetScaler.ScaleCount(MaxDecodeStartsPerFrame, FrameBudgetScale)
            : int.MaxValue;

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

        // Self-measured frame duration → the time-based streaming pace for this frame. First frame
        // (no prior timestamp) stays at 1.0.
        var now = Stopwatch.GetTimestamp();
        FrameBudgetScale = _lastFrameTimestamp == 0
            ? 1.0
            : Core.Resources.StreamingFrameBudgetScaler.Scale(
                Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds);
        _lastFrameTimestamp = now;

        _frameUploadByteBudget = new FrameByteBudget(StreamingThrottled
            ? Core.Resources.StreamingFrameBudgetScaler.ScaleBytes(MaxUploadBytesPerFrame, FrameBudgetScale)
            : long.MaxValue);
        _frameUploadMsBudget = StreamingThrottled
            ? UploadMillisecondsPerFrame * FrameBudgetScale
            : double.MaxValue;
        _frameUploadMsConsumed = 0;
        _textureCache.ResetFrameStats(FrameBudgetScale);
        PruneCompletedDecodeTasks();
    }

    /// <summary>
    ///     Per-frame budget of ACTUAL mesh-upload milliseconds (scaled by <see cref="FrameBudgetScale" />
    ///     when throttled; see the <c>_frameUploadMsConsumed</c> field notes). Set by the renderer from
    ///     its FALLOUT_VIEWER_REFERENCE_UPLOAD_MS_PER_FRAME knob.
    /// </summary>
    public double UploadMillisecondsPerFrame { get; set; } = 2.0;

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
        // variant (so a shared NIF — e.g. every billboard — renders its correct per-base textures).
        // Archive lookups, decode, and collision key on the plain decodePath via Node.DecodePath;
        // the in-memory LRUs and the on-disk cache both discriminate variants (the disk key folds
        // Node.VariantKey), so a re-skinned mesh warm-loads like any other.
        var cacheKey = alternateTextures is null
            ? decodePath
            : decodePath + "#" + alternateTextures.VariantKey;
        // TryGet bumps the hit to MRU — the old explicit Touch.
        if (_meshLru.TryGet(cacheKey, out var existing))
        {
            var resolved = ResolveExisting(cacheKey, existing, ref uploadBudget);
            // An unresolved node with no in-memory payload, no running decode, and no negative
            // verdict needs (re-)queueing: either its payload was evicted from the decoded LRU
            // after its decode/disk-load completed, or it's still waiting in the queue (the call
            // then just re-offers the current distance for the decrease-key promotion).
            if (resolved is null && !existing.ResolvedNull && !existing.DecodedCacheAvailable &&
                existing.DecodeTask is null)
            {
                QueueDecode(cacheKey, existing, priority);
            }

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
            MaterialSwaps = alternateTextures?.MaterialSwaps,
            GradientMapVOverride = alternateTextures?.GradientMapVOverride,
            ExternalEmittanceColor = alternateTextures?.ExternalEmittanceColor,
            HasVariant = alternateTextures is not null,
            VariantKey = alternateTextures?.VariantKey
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

        DrainDecodeTasksForDispose();

        // Producers are drained; let the persist writer flush the queued disk writes before the stats
        // log. Bounded wait — an abandoned tail write just means those meshes re-decode next session.
        if (_persistQueue is not null)
        {
            _persistQueue.Writer.TryComplete();
            try
            {
                _persistWriterTask?.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException ex)
            {
                Log.Warn("ReferenceMeshCache12: persist writer faulted during dispose: {0}",
                    ex.GetBaseException().Message);
            }
        }

        _persistentDecodedCache?.LogStatistics();

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
            StoreDecodedCache(modelPath, null);
            return null;
        }

        var decoded = task.Result;
        node.DecodeTask = null;
        if (decoded is null)
        {
            node.ResolvedNull = true;
            StoreDecodedCache(modelPath, null);
            return null;
        }

        StoreDecodedCache(modelPath, decoded);
        // The decode above also persisted to disk, so if the decoded-LRU entry is later evicted the
        // node may probe the on-disk cache once more (and hit) instead of re-decoding.
        node.DecodedCacheMissRecorded = false;
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
            // NO disk I/O on this path — it runs every frame for every visible unresolved mesh, and
            // a disk-cache load (probe + read + deserialize) is milliseconds. The DECODE WORKER
            // checks the on-disk cache before decoding (StartDecodeTask), so returning false routes
            // the node through the decode queue and the load happens off-thread at the same
            // nearest-first priority as decodes. The predecessors of this design both collapsed
            // dense areas: per-frame inline probes cost 7-17 s/frame (57k file opens), and the
            // budget-gated inline load that replaced them drip-fed downtown at a handful of meshes
            // per frame ("assets stream in very slowly"). The flag below re-arms whenever a fresh
            // payload lands, so eviction of this node's decoded-LRU entry re-queues it (via the
            // caller's DecodedCacheAvailable gate) instead of wedging.
            node.DecodedCacheAvailable = false;
            if (!node.DecodedCacheMissRecorded)
            {
                FrameCpuDecodedCacheMisses++;
                node.DecodedCacheMissRecorded = true;
            }

            return false;
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
        // Accumulated-upload-time gate (same first-unit rule): paces real upload WORK per frame.
        // Deliberately not a wall-clock deadline — see the _frameUploadMsConsumed field notes.
        if (_frameUploadByteBudget.Count > 0 && _frameUploadMsConsumed >= _frameUploadMsBudget)
        {
            FrameByteBudgetDeferrals++;
            return true;
        }

        uploadBudget--;
        FrameGpuUploads++;
        _frameUploadByteBudget.Record(decoded.ByteSize);
        // Upload + collision keying use the plain archive path (collision geometry is texture-
        // independent, so all variants of a NIF share one collision entry keyed on DecodePath).
        var uploadStarted = Stopwatch.GetTimestamp();
        mesh = UploadDecodedMesh(node.DecodePath, decoded.Mesh);
        _frameUploadMsConsumed += Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds;
        if (mesh is null)
        {
            node.ResolvedNull = true;
            StoreDecodedCache(modelPath, null);
            return true;
        }

        node.Mesh = mesh;
        return true;
    }

    private void QueueDecode(string modelPath, Node node, float priority)
    {
        if (node.DecodeTask is not null || node.ResolvedNull)
        {
            return;
        }

        if (node.DecodeQueued)
        {
            // Re-offer the current distance every frame the mesh stays visible: the queue promotes
            // the key when this priority is nearer (lazy decrease-key), so a mesh first sighted at
            // the far edge does not stay parked behind the whole far-field backlog as the camera
            // approaches it.
            _decodeQueue.Enqueue(modelPath, priority);
            return;
        }

        if (!_decodeQueue.Enqueue(modelPath, priority))
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
        // submesh texture paths; cache the result under the composite cacheKey. Disk persistence
        // keys on the plain path's archive identity + the variant discriminator, so re-skinned
        // variants warm-load like the default mesh.
        var decodePath = node.DecodePath;
        var variantKey = node.VariantKey;
        var overrides = node.Overrides;
        var materialSwaps = node.MaterialSwaps;
        var gradientMapVOverride = node.GradientMapVOverride;
        var externalEmittanceColor = node.ExternalEmittanceColor;
        Interlocked.Increment(ref _activeDecodeTasks);
        var task = Task.Run(() =>
        {
            try
            {
                // Warm path first: the on-disk decoded cache. Loading here (worker thread, decode-
                // queue priority) instead of inline in the render loop's resolve is what lets a
                // dense warm area stream in at full disk speed without costing frame time. The
                // load already stores into the in-memory decoded LRU; returning its mesh matches
                // the decode contract (null = negative entry ⇒ ResolvedNull downstream).
                if (TryLoadPersistentDecodedCache(decodePath, variantKey, cacheKey, out var cached))
                {
                    return cached.IsNegative ? null : cached.Mesh;
                }

                var decoded = _decoder.DecodeMesh(
                    decodePath, overrides, materialSwaps, gradientMapVOverride, externalEmittanceColor);
                StoreDecodedCache(cacheKey, decoded);
                StorePersistentDecodedCache(decodePath, variantKey, decoded);
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

    private bool TryLoadPersistentDecodedCache(
        string decodePath,
        string? variantKey,
        string storeKey,
        out DecodedCacheValue value)
    {
        value = default;
        if (_persistentDecodedCache is null)
        {
            return false;
        }

        // SpeedTree geometry is generated procedurally (cheap, and the algorithm is still being
        // tuned). Never serve it from the on-disk cache, or generator changes / live option tweaks
        // would be masked by a stale entry keyed on the unchanged .spt bytes.
        if (SpeedTreeModelPath.IsSpt(decodePath))
        {
            return false;
        }

        var metadata = _meshArchives.GetLookupMetadata(decodePath);

        if (!_persistentDecodedCache.TryLoad(metadata, variantKey, out var cached))
        {
            return false;
        }

        // Live particle definitions contain the parsed modifier/controller graph and resolved emitter mesh,
        // which the static payload deliberately does not serialize. Reject particle-source (and negative,
        // which could represent a quiet particle-only NIF) warm entries in opt-in mode so those paths source-
        // decode. The mesh-level provenance remains true when a mixed NIF's time-zero bake emitted no cloud.
        // Ordinary positive meshes still get the established high-throughput warm cache.
        var containsParticleSource = cached.Mesh?.ContainsParticleSource == true;
        if (!ParticleLiveSettings.UsePersistentDecodedMeshCache(
                ParticleLiveSettings.Enabled, cached.IsNegative, containsParticleSource))
        {
            return false;
        }

        var decoded = cached.IsNegative || cached.Mesh is null
            ? null
            : ReferenceMeshDecoder12.FromPersistentPayload(cached.Mesh);
        StoreDecodedCache(storeKey, decoded);
        return TryGetDecodedCache(storeKey, out value);
    }

    private void StoreDecodedCache(string modelPath, DecodedNifMesh12? decoded)
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
    }

    private void StorePersistentDecodedCache(string decodePath, string? variantKey, DecodedNifMesh12? decoded)
    {
        if (_persistentDecodedCache is null)
        {
            return;
        }

        // SpeedTree geometry is regenerated each session (see TryLoadPersistentDecodedCache) — never
        // persist it.
        if (SpeedTreeModelPath.IsSpt(decodePath))
        {
            return;
        }

        // Hand off to the background persist writer so the decode worker's slot frees for the next
        // mesh. TryWrite fails when the queue is full (writer behind) or completed (disposing) — the
        // caller then pays for the write itself, which is exactly the pre-queue behavior.
        if (_persistQueue is not null && _persistQueue.Writer.TryWrite((decodePath, variantKey, decoded)))
        {
            return;
        }

        PersistDecodedCacheEntry(decodePath, variantKey, decoded);
    }

    private async Task ProcessPersistQueueAsync()
    {
        await foreach (var (decodePath, variantKey, decoded) in
                       _persistQueue!.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            PersistDecodedCacheEntry(decodePath, variantKey, decoded);
        }
    }

    private void PersistDecodedCacheEntry(string decodePath, string? variantKey, DecodedNifMesh12? decoded)
    {
        try
        {
            var metadata = _meshArchives.GetLookupMetadata(decodePath);

            _persistentDecodedCache!.Store(
                metadata,
                variantKey,
                decoded is null ? null : ReferenceMeshDecoder12.ToPersistentPayload(decoded));
        }
        catch (Exception ex)
        {
            Log.Warn("ReferenceMeshCache12: decoded mesh disk cache write skipped for '{0}': {1}", decodePath, ex.Message);
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

        // A routed physics-lite submesh rotates around an off-origin root-local hinge. Preserve the
        // renderer's conservative cull contract across the whole authored arc: rotation keeps each
        // vertex's distance from the hinge fixed, so |pivot| + |vertex-pivot| is an exact upper bound
        // on its possible distance from the NIF origin. This is scoped to the rare FNV descriptors;
        // ordinary meshes retain the tighter one-pass radius above.
        foreach (var sub in decoded.Submeshes)
        {
            if (sub.PhysicsLiteSway is not { } sway)
            {
                continue;
            }

            var maxPivotDistance = 0f;
            foreach (var vertex in sub.Vertices)
            {
                maxPivotDistance = MathF.Max(
                    maxPivotDistance,
                    Vector3.Distance(vertex.Position, sway.Pivot));
            }

            var swayRadius = sway.Pivot.Length() + maxPivotDistance;
            meshLocalRadiusSq = MathF.Max(meshLocalRadiusSq, swayRadius * swayRadius);
        }

        // Degenerate (no vertices) → collapse the AABB to the origin so consumers see min == max and
        // fall back to the sphere rather than an inverted box.
        if (aabbMin.X > aabbMax.X)
        {
            aabbMin = aabbMax = Vector3.Zero;
        }

        var submeshes = new List<CachedSubmesh12>(decoded.Submeshes.Count);
        var softParticleSources = new HashSet<NifSoftParticleSource>();
        // Authored positions + triangle indices of WaterShaderProperty submeshes (caves/pools/
        // reflecting pools), captured so the dedicated water renderer can transform and draw their
        // real geometry. FNV WATER000 consumes source positions directly; reducing them to an AABB
        // changed rotated, sloped, and circular water surfaces.
        List<NifWaterGeometry>? waterPlanesLocal = null;
        var vertexStride = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        for (var i = 0; i < decoded.Submeshes.Count; i++)
        {
            var sub = decoded.Submeshes[i];
            // Placed water-shader geometry (WaterShaderProperty NIFs — caves/pools/reflecting pools)
            // is NOT drawn as a reference slab. Divert it: retain the authored triangle geometry so
            // WaterRenderer12 can render it with the real Fresnel/ripple/depth-fade water shader, and
            // skip building a drawable submesh — that omission gates off the old flat translucent-tile
            // fallback (the submesh never becomes a reference draw → no double-draw).
            if (string.Equals(sub.DiffuseTexturePath, RenderableSubmesh.WaterSurfaceTexturePath, StringComparison.Ordinal))
            {
                AppendWaterGeometry(sub.Vertices, sub.Indices, ref waterPlanesLocal);
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
                var softParticle = NifSoftParticlePolicy.Resolve(new NifSoftParticleCandidate(
                    modelPath,
                    sub.AlphaBlend,
                    sub.DepthWritingBlend,
                    sub.IsDecal,
                    sub.IsParticleCloud,
                    sub.IsBillboard,
                    sub.HasEffectFalloff,
                    sub.SoftParticleFalloffDepth,
                    sub.SrcBlendMode,
                    sub.DstBlendMode));
                if (softParticle.Enabled)
                {
                    softParticleSources.Add(softParticle.Source);
                }

                GpuTextureCache12.Entry diffuse;
                if (string.IsNullOrEmpty(sub.DiffuseTexturePath))
                {
                    diffuse = _textureCache.WhitePixel;
                }
                else if (string.Equals(sub.DiffuseTexturePath, RenderableSubmesh.WaterSurfaceTexturePath, StringComparison.Ordinal))
                {
                    diffuse = _textureCache.WaterSurface;
                }
                else
                {
                    // Alpha-tested SpeedTree leaf cards request the ALPHA-WEIGHTED-MIPS variant of their
                    // atlas: the shipped DDS mips average foliage color with the atlas background (white
                    // for the Oblivion dogwood/maple composites), washing distant canopies toward the
                    // background color — the engine never hits those mips (distant trees are billboard
                    // imposters). The suffix keys a separate cache entry end-to-end.
                    var diffusePath = sub.IsLeafBillboard && sub.AlphaTest
                        ? sub.DiffuseTexturePath + NifGpuTextureResolver.LeafAtlasMipsSuffix
                        : sub.DiffuseTexturePath!;
                    diffuse = _textureCache.GetOrUpload(diffusePath);
                }
                var normal = !string.IsNullOrEmpty(sub.NormalMapTexturePath)
                    ? _textureCache.GetOrUpload(sub.NormalMapTexturePath!, isNormalMap: true)
                    : _textureCache.FlatNormal;
                var specularMap = !string.IsNullOrEmpty(sub.SpecularMapTexturePath)
                    ? _textureCache.GetOrUpload(sub.SpecularMapTexturePath!)
                    : null;
                var gradientMap = !string.IsNullOrEmpty(sub.GradientMapTexturePath)
                    ? _textureCache.GetOrUpload(sub.GradientMapTexturePath!)
                    : null;
                System.Diagnostics.Debug.Assert(
                    gradientMap is null || string.IsNullOrEmpty(sub.Lighting30GlowMapTexturePath),
                    "FO4 gradient and classic Lighting30 glow maps cannot share TexIndices.w.");
                var lighting30GlowMap = sub.IsLighting30 && gradientMap is null &&
                                        !string.IsNullOrEmpty(sub.Lighting30GlowMapTexturePath)
                    ? _textureCache.GetOrUpload(sub.Lighting30GlowMapTexturePath!)
                    : null;
                // FO4 environment cubemap (nearly always the shared mipblur_defaultoutside1.dds —
                // one texture serving thousands of materials). The env shader term stays off until
                // the entry promotes to a real TextureCube (see CachedSubmesh12.EnvMapState).
                var envMap = !string.IsNullOrEmpty(sub.EnvironmentMapTexturePath) && sub.EnvironmentMapScale > 0f
                    ? _textureCache.GetOrUpload(sub.EnvironmentMapTexturePath!)
                    : null;

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
                    SpecularMap = specularMap,
                    GradientMap = gradientMap,
                    Lighting30GlowMap = lighting30GlowMap,
                    GradientMapV = sub.GradientMapV,
                    EnvMap = envMap,
                    EnvMapScale = sub.EnvironmentMapScale,
                    EnvMapSmoothness = sub.EnvironmentMapSmoothness,
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
                    MaterialAlphaController = sub.MaterialAlphaController,
                    PhysicsLiteSway = sub.PhysicsLiteSway,
                    DoubleSided = sub.DoubleSided,
                    IsEmissive = sub.IsEmissive,
                    IsLighting30 = sub.IsLighting30,
                    Lighting30Emission = new Vector4(
                        sub.Lighting30EmissionColor,
                        sub.Lighting30EmissionMultiplier),
                    LocalBoundsCenter = sub.LocalBoundsCenter,
                    IsBillboard = sub.IsBillboard,
                    BillboardFrontAxis = sub.IsBillboard
                        ? NifBillboardFacing.ResolveFrontAxis(sub.Vertices, sub.Indices)
                        : Vector2.UnitY,
                    IsLeafBillboard = sub.IsLeafBillboard,
                    ParticleCenters = ExtractParticleCenters(sub),
                    LiveParticles = sub.ParticleRuntime is { } runtime
                        ? new LiveParticleOwner12(runtime)
                        : null,
                    ClampTextureU = sub.ClampTextureU,
                    ClampTextureV = sub.ClampTextureV,
                    IsSpeedTreeBranch = sub.IsSpeedTreeBranch,
                    // Fresh decodes always carry an explicit value (RenderableSubmesh defaults to
                    // one). Preserve authored (0,0): zero is a valid CNAM phase rate, not "missing".
                    SpeedTreeWindSpeeds = sub.SpeedTreeWindSpeeds,
                    SpeedTreeLod = sub.SpeedTreeLod,
                    DepthWritingBlend = sub.DepthWritingBlend,
                    IsDecal = sub.IsDecal,
                    // default(Vector3) = a pre-effect-fields payload (or a caller that skipped the
                    // arg); black would tint everything out, so normalize to the no-op white.
                    EffectTint = sub.EffectTint == default ? Vector3.One : sub.EffectTint,
                    EffectFalloffParams = sub.EffectFalloffParams,
                    HasEffectFalloff = sub.HasEffectFalloff,
                    SoftParticle = softParticle,
                    UvScrollVelocity = sub.UvScrollVelocity,
                    Skin = sub.Skin,
                    RestPoseVertices = sub.Skin is not null ? sub.Vertices : null
                });
            }
            catch (Exception ex)
            {
                Log.Warn("ReferenceMeshCache12: submesh upload failed for '{0}': {1}", modelPath, ex.Message);
            }
        }

        if (submeshes.Count == 0 && (waterPlanesLocal is null || waterPlanesLocal.Count == 0))
        {
            // No drawable submeshes and no water geometry — nothing references the arena range, so reclaim it.
            _geometryArena.Free(geometry);
            return null;
        }

        success = true;
        var cached = new CachedNifMesh12(
            submeshes, geometry, _geometryArena, _deletionQueue, _textureCache, MathF.Sqrt(meshLocalRadiusSq),
            aabbMin, aabbMax,
            (IReadOnlyList<NifWaterGeometry>?)waterPlanesLocal ?? Array.Empty<NifWaterGeometry>())
        {
            Animation = decoded.Animation
        };
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
                ["fnvPhysicsLiteSubmeshes"] = submeshes.Count(static sub => sub.PhysicsLiteSway is not null),
                ["softParticleSubmeshes"] = submeshes.Count(static sub => sub.SoftParticle.Enabled),
                ["softParticleSources"] = softParticleSources.Select(static source => source.ToString()).ToArray(),
                ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }

        return cached;
    }

    private static Vector3[]? ExtractParticleCenters(DecodedSubmesh12 sub)
    {
        // The particle extractor emits exactly four vertices + six indices per quad and stores
        // the common center in every vertex tangent for the camera-facing VS reconstruction.
        // Reject malformed payloads here so the draw path can safely fall back to the static IB.
        if (!sub.IsParticleCloud || sub.Vertices.Length == 0 ||
            sub.Vertices.Length % 4 != 0 || sub.Indices.Length != sub.Vertices.Length / 4 * 6)
        {
            return null;
        }

        var centers = new Vector3[sub.Vertices.Length / 4];
        for (var i = 0; i < centers.Length; i++)
        {
            centers[i] = sub.Vertices[i * 4].Tangent;
        }

        return centers;
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
            Math.Clamp(sub.MaterialAlpha, 0f, 8f),
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
    // Emissive shapes ride their material glow tint in the otherwise-unused xyz (the decoder put it
    // there; see ReferenceMeshDecoder12) with w = 0 so the specular sentinel stays off — the PS's
    // emissive branch reads vSpecular.rgb as the glow color.
    private static Vector4 BuildSpecular(DecodedSubmesh12 sub) =>
        sub.SpecularEnabled ? new Vector4(sub.SpecularColor, MathF.Max(sub.Glossiness, 1f))
        : sub.IsEmissive ? new Vector4(sub.SpecularColor, 0f)
        : Vector4.Zero;

    private static uint CheckedByteSize(int elementCount, uint elementSize) =>
        checked((uint)((long)elementCount * elementSize));

    // Retains one water submesh's validated, mesh-local triangle geometry. The per-reference world
    // matrix is applied later (ReferenceRenderer12) once the placement is known. Invalid topology is
    // skipped as a unit rather than guessed at; the ordinary drawable path already validates the same
    // decoded arrays before they reach this point.
    private static void AppendWaterGeometry(
        GpuMeshUploader.GpuVertex[] vertices,
        ushort[] indices,
        ref List<NifWaterGeometry>? geometries)
    {
        if (vertices.Length == 0 || indices.Length < 3) return;
        var positions = new Vector3[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            positions[i] = vertices[i].Position;
        }

        if (NifWaterGeometry.TryCreate(positions, indices, out var geometry) && geometry is not null)
        {
            (geometries ??= new List<NifWaterGeometry>(1)).Add(geometry);
        }
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

        /// <summary>FO4/FO76 MSWP material swaps (normalized original → replacement <c>.bgsm</c> path)
        /// to apply during material resolution at decode time, or null when the placement has none.
        /// Rides the same '#variant' cache key as <see cref="Overrides" /> (via the merged
        /// <see cref="AlternateTextureSet.VariantKey" />), so each swap decodes as its own mesh.</summary>
        public IReadOnlyDictionary<string, string>? MaterialSwaps { get; init; }

        /// <summary>FO4-family MODC gradient-palette row override (0–1) applied at decode to
        /// grayscale-to-palette materials, or null. Rides the same variant cache key.</summary>
        public float? GradientMapVOverride { get; init; }

        /// <summary>Resolved REFR XEMI color, carried in the placement's variant cache key.</summary>
        public Vector3? ExternalEmittanceColor { get; init; }

        /// <summary>True when this node is a MODS re-skin variant.</summary>
        public bool HasVariant { get; init; }

        /// <summary>The re-skin's <see cref="AlternateTextureSet.VariantKey" /> (a content hash of
        /// the override + swap pairs), or null for the default variant. Folded into the on-disk cache
        /// key so each variant persists its own decode — without it, variants had to skip disk
        /// persistence entirely and re-decoded from scratch EVERY session; on FO4 (where base-record
        /// MODS makes swapped placements ubiquitous) that erased most of the warm-load win.</summary>
        public string? VariantKey { get; init; }
    }

    private readonly record struct DecodedCacheValue(
        DecodedNifMesh12? Mesh,
        bool IsNegative,
        long ByteSize);
}
#endif
