#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Threading.Channels;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Effects;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Procedural;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Orchestration;
using BethesdaMultitool.Core.Resources;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     D3D12 placed-reference mesh cache. CPU decode is requested asynchronously; all D3D12
///     resource creation stays on the render thread while the command list is open.
/// </summary>
internal sealed class ReferenceMeshCache12 : IDisposable, IGpuCommandSubmissionParticipant12
{
    private static readonly Logger Log = Logger.Instance;

    // Cold NIF conversion allocates heavily and competes with the UI/render thread, so the default
    // scales with cores but stays GC-guarded: HALF the logical processors, clamped to [2, 12]. This
    // leaves headroom for the render + UI + GC threads (full-core saturation caused the GC pauses the
    // old conservative default of 2 was guarding against). The ceiling rose 8→12 when disk-cache
    // persists moved off the decode workers onto the background persist writer — a decode slot is now
    // pure CPU decode, so high-core machines (24+ threads) get the extra workers a cold Commonwealth
    // load can use. Env override for profiling specific machines.
    // Sized as this workload's SHARE of the machine rather than from ProcessorCount in isolation:
    // three pools each doing the latter summed to 25 workers on a 20-core box with no reserve for
    // the UI or render threads. See CpuBudget.
    private static readonly int DefaultMaxConcurrentDecodeTasks =
        Core.Orchestration.ConcurrencyPolicy
            .Fixed(Core.Orchestration.CpuBudget.Interactive()
                .Claim(Core.Orchestration.CpuWorkload.ReferenceMeshDecode))
            .WithEnvironmentOverride(EnvironmentVariables.Viewer.ReferenceDecodeConcurrency, 1, 16)
            .Resolve();

    private static readonly int DefaultMaxDecodeStartsPerFrame = EnvironmentVariables.GetClampedInt(
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
    // 1.0 (engine-faithful).
    private const float ReferenceBumpStrength = 1.0f;

    // Size-aware ceiling on render-thread mesh-upload work per frame. The integer upload budget
    // (passed by the renderer) caps *how many* meshes upload; this caps the *bytes*, checked before
    // each upload so a frame that has already spent its budget defers an expensive mesh to the next
    // frame instead of overshooting on it. Sized so a typical 48-mesh frame passes but a burst of
    // unusually large meshes is spread across frames.
    private static readonly long DefaultMaxUploadBytesPerFrame = EnvironmentVariables.GetClampedLong(
        EnvironmentVariables.Viewer.ReferenceUploadBytesPerFrame,
        defaultValue: 4L * 1024L * 1024L,
        min: 1L * 1024L * 1024L,
        max: 64L * 1024L * 1024L);

    private readonly MeshArchiveSet _meshArchives;
    private readonly ReferenceMeshDecoder12 _decoder;
    private readonly GpuTextureCache12 _textureCache;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuGeometryArena12 _geometryArena;
    // DEFAULT-heap meshes are usable by the command list that records their copy, but cannot become
    // durable cache state until that list is submitted. One enlistment covers every upload in the
    // open frame; commit/rollback is delivered synchronously by GpuCommandRecorder12.
    private readonly List<PendingMeshPublication> _pendingMeshPublications = new();
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
    // Placed-reference collision geometry (positions + indices only), keyed by normalized model path.
    // Built inside UploadDecodedMesh and read by walk, selection, and overlay queries on the render
    // thread, so it shares the LruCache single-threaded contract with _meshLru. Positions-only is a
    // fraction of the GPU footprint, so a large byte budget keeps far more meshes warm than residency.
    private readonly LruCache<string, CollisionCacheEntry> _collisionLru;
    // Geometry stays exclusively in _collisionLru. This registry retains only a bounded
    // plain-path -> resident variant owner and its recovery state, so an independently evicted
    // collision entry can be rebuilt from the exact decoded variant without another GPU upload.
    private readonly CollisionRecoveryRegistry _collisionRecovery = new();
    private readonly ReferenceDecodedMeshDiskCache12? _persistentDecodedCache;
    private readonly string? _starfieldMaterialDatabaseCacheIdentity;
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

    /// <summary>Weight of the newest observation in <see cref="_uploadMillisecondsPerByte" />.</summary>
    private const double UploadThroughputAlpha = 0.25;

    /// <summary>
    ///     Observed render-thread upload cost per byte, used to predict whether the NEXT upload fits
    ///     in what remains of the frame's allowance. Seeded at roughly 1 ms per MiB — deliberately
    ///     pessimistic, so early frames pace conservatively and the EMA relaxes it once real
    ///     throughput is known.
    /// </summary>
    private double _uploadMillisecondsPerByte = 1.0 / (1024.0 * 1024.0);
    // When true, LoadData resizes _meshLru to the worldspace's working set (default). False when the
    // FALLOUT_VIEWER_REFERENCE_MESH_CAPACITY env knob pins a fixed cap for eviction-cascade stress gates.
    private readonly bool _autoSizeMeshCapacity;
    // Retry debt is scoped to the desired set visited by the current batch build. A global count
    // wedges capture after the camera moves away from a failed resident node: nothing revisits that
    // off-demand node to clear its flag. Each fresh traversal advances this token and resets the
    // count in O(1); incremental continuation slices keep the same token until publication.
    private ulong _materializationRetryDemandGeneration = 1;
    private int _pendingMaterializationRetries;
    private bool _disposed;

    public ReferenceMeshCache12(
        GpuDevice12 gpu,
        MeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        GpuTextureCache12 textureCache,
        GpuDeletionQueue12 deletionQueue,
        GpuCommandRecorder12 recorder,
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
        _starfieldMaterialDatabaseCacheIdentity = textureResolver.StarfieldMaterialDatabaseCacheIdentity;
        // speedTreeHeights: null — the engine renders .spt geometry at its natural world scale; the old
        // TREE-OBND rescale oversized shrubs (param retained on the decoder until its file frees up).
        _decoder = new ReferenceMeshDecoder12(meshArchives, textureResolver, speedTreeHeights: null,
            speedTreeLeafTextures, speedTreeDimming);
        _textureCache = textureCache;
        _deletionQueue = deletionQueue;
        _recorder = recorder;
        var requestedGeometryBacking =
            EnvironmentVariables.Get(EnvironmentVariables.Viewer.ReferenceGeometryHeap);
        var geometryBackingMode = GpuGeometryArenaBackingModePolicy.Parse(requestedGeometryBacking);
        string? geometryBackingFallback = null;
        if (geometryBackingMode == GpuGeometryArenaBackingMode.DefaultHeap &&
            GeometryArenaDiagnostics.ValidateLevel >= 2)
        {
            geometryBackingFallback = "geometry-validation-level-2";
            Log.Warn(
                "ReferenceMeshCache12: DEFAULT reference geometry disabled because geometry " +
                "validation level {0} requires CPU-readable byte hashing; using UPLOAD heap.",
                GeometryArenaDiagnostics.ValidateLevel);
            geometryBackingMode = GpuGeometryArenaBackingMode.UploadHeap;
        }

        Log.Info("ReferenceMeshCache12: reference geometry backing = {0}.", geometryBackingMode);
        RendererProfilerTrace.Event("reference-geometry-backing", new Dictionary<string, object?>
        {
            ["requested"] = requestedGeometryBacking,
            ["effective"] = geometryBackingMode.ToString(),
            ["fallback"] = geometryBackingFallback,
            ["geometryValidationLevel"] = GeometryArenaDiagnostics.ValidateLevel
        });
        _geometryArena = new GpuGeometryArena12(gpu, backingMode: geometryBackingMode)
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
        // Sized in real arena bytes, but categorised GpuAttributed: the bytes are physically the
        // geometry arena's (already reported under GpuGeometryArena12["reference"]), and
        // GpuAttributed is excluded from category totals so nothing double-counts. Reporting 0 —
        // or the old 1-byte-per-entry default — would leave the largest reference pool
        // ungovernable, because a byte budget cannot be enforced on a size nobody knows.
        //
        // Segment follows the selected arena backing. UPLOAD geometry is NonLocal system RAM;
        // DEFAULT geometry is Local device memory. The LRU is attribution-only either way, so the
        // arena remains the single physical-byte owner in registry totals.
        //
        // sizeOf runs at Set time, when the node's Mesh is still null and the size is 0; the real
        // size is published via _meshLru.UpdateSize once the async decode completes and the mesh is
        // assigned in place.
        _meshLru = new LruCache<string, Node>(
                "ReferenceMeshLru",
                ResourceCategory.GpuAttributed,
                maxEntries: capacity,
                sizeOf: static (_, node) => node.Mesh?.Geometry.Allocation.AlignedSize ?? 0,
                onEvicted: OnMeshNodeEvicted,
                comparer: StringComparer.OrdinalIgnoreCase)
            .WithSegment(geometryBackingMode == GpuGeometryArenaBackingMode.DefaultHeap
                ? Core.Diagnostics.GpuMemorySegment.Local
                : Core.Diagnostics.GpuMemorySegment.NonLocal)
            .RegisterWith(ResourceRegistry.Instance, "reference-meshes");
        _decodedLru = new LruCache<string, DecodedCacheValue>(
                "ReferenceDecodedMeshCache",
                ResourceCategory.CpuCache,
                maxBytes: decodedCacheByteBudget,
                sizeOf: static (_, value) => value.ByteSize,
                comparer: StringComparer.OrdinalIgnoreCase)
            .RegisterWith(ResourceRegistry.Instance, "reference-decoded");
        _collisionLru = new LruCache<string, CollisionCacheEntry>(
                "ReferenceCollisionMeshCache",
                ResourceCategory.CpuCache,
                maxBytes: CollisionMeshCacheByteBudget,
                sizeOf: static (_, value) => value.ByteSize,
                comparer: StringComparer.OrdinalIgnoreCase)
            .RegisterWith(ResourceRegistry.Instance, "reference-collision");
    }

    private void OnMeshNodeEvicted(string cacheKey, Node node)
    {
        _collisionRecovery.RemoveOwner(node.DecodePath, cacheKey);
        ClearMaterializationRetry(node);
        if (node.PendingPublication is { } pending)
        {
            // The open command list may already contain draws through this pending mesh. Keep it
            // alive until that list reaches its submit/abort boundary; the participant then disposes
            // it instead of committing it into the node that has left the LRU.
            pending.Evicted = true;
            node.PendingPublication = null;
        }
        node.Mesh?.Dispose();
        // Any eviction invalidates frozen (reused) reference batches: their instance lists key on
        // CachedSubmesh12 objects whose GPU buffers just entered the deletion queue. The renderer
        // compares this against its build snapshot.
        EvictionGeneration++;
        if (GeometryArenaDiagnostics.AuditEnabled && node.Mesh is not null)
        {
            RendererProfilerTrace.Event("geometry-arena", new Dictionary<string, object?>
            {
                ["op"] = "evict",
                ["tag"] = cacheKey,
                ["evictionGeneration"] = EvictionGeneration,
            });
        }
    }

    /// <summary>
    ///     Returns the category-resolved mesh-local collision geometry for <paramref name="modelPath" />.
    ///     A collision-LRU miss also consults lightweight resident-node recovery state: authoritative
    ///     no-collision remains resolved, while terminal decode failure retains OBND fallback without
    ///     requesting another warmup. Render-thread only (same contract as <c>_collisionLru</c>).
    /// </summary>
    public CollisionMeshResolution ResolveCollisionMesh(
        string modelPath,
        PlacedObjectCategory category = PlacedObjectCategory.Unknown)
    {
        if (_disposed) return CollisionMeshResolution.Unresolved;

        var normalizedPath = ReferenceMeshDecoder12.NormalizeModelPath(modelPath);
        if (_collisionLru.TryGet(normalizedPath, out var entry))
        {
            var result = entry.Resolve(normalizedPath, category);
            return CollisionMeshResolution.From(result);
        }

        return _collisionRecovery.ResolveCacheMiss(normalizedPath);
    }

    /// <summary>
    ///     Collision cold/recovery path. A never-seen plain model follows the collision-only resolver;
    ///     an independently evicted collision entry instead republishes from its exact resident
    ///     variant's decoded LRU/disk/source payload without uploading its GPU mesh again. The caller
    ///     bounds how many unique paths it offers per frame.
    /// </summary>
    public CollisionMeshResolution GetOrWarmCollisionMesh(
        string modelPath,
        PlacedObjectCategory category,
        float priority)
    {
        var resolution = ResolveCollisionMesh(modelPath, category);
        if (resolution.IsResolved || !resolution.ShouldOfferWarmup || _disposed) return resolution;

        var normalizedPath = ReferenceMeshDecoder12.NormalizeModelPath(modelPath);
        if (TryGetCollisionOwnerNode(normalizedPath, out var ownerKey, out var ownerNode))
        {
            // Drain before resident/render-null early returns so a completed Task cannot pin its
            // DecodedNifMesh12 outside the byte-bounded decoded LRU. A collision-recovery task also
            // republishes here without touching the already-resident GPU mesh.
            DrainCompletedDecodeTask(ownerKey, ownerNode);
            resolution = ResolveCollisionMesh(normalizedPath, category);
            if (resolution.IsResolved || !resolution.ShouldOfferWarmup) return resolution;

            if (TryRepublishCollisionFromDecodedCache(ownerKey, ownerNode))
            {
                return ResolveCollisionMesh(normalizedPath, category);
            }

            QueueCollisionRecovery(ownerKey, ownerNode, priority);
            StartQueuedDecodes();
            return ResolveCollisionMesh(normalizedPath, category);
        }

        if (_meshLru.TryPeek(normalizedPath, out var node) && node.DecodeQueued)
        {
            // The ordinary resolver normally starts the queue before re-offering an existing node's latest
            // distance. The collision overlay already did a global nearest-first sort, so promote
            // here first or an older far-field priority could win the newly-open worker slot.
            _decodeQueue.Enqueue(normalizedPath, priority);
        }

        // Walk/collision sampling runs from the controller update BEFORE BeginFrame opens the direct
        // command list. Resolve/decode and publish CPU collision here, but never let this path record
        // a DEFAULT-heap geometry copy. The ordinary reference render will consume the same positive
        // decoded-cache entry later with its active command list and realize the GPU mesh then.
        var uploadBudget = 0;
        _ = GetOrResolve(
            null,
            true,
            normalizedPath,
            ref uploadBudget,
            priority);
        return ResolveCollisionMesh(normalizedPath, category);
    }

    private bool TryGetCollisionOwnerNode(
        string normalizedPath,
        out string ownerKey,
        out Node ownerNode)
    {
        if (_collisionRecovery.TryGetOwner(normalizedPath, out ownerKey))
        {
            if (_meshLru.TryPeek(ownerKey, out ownerNode))
            {
                return true;
            }

            // Defensive stale-owner cleanup. The ordinary mesh-eviction callback removes this
            // synchronously, but keeping lookup fail-closed protects future removal paths.
            _collisionRecovery.RemoveOwner(normalizedPath, ownerKey);
        }

        // Pre-registry/default nodes can still exist in a warm cache. Adopt only the exact plain-key
        // node; never guess which alternate-material variant should own collision.
        ownerKey = normalizedPath;
        if (_meshLru.TryPeek(ownerKey, out ownerNode))
        {
            _collisionRecovery.TrackUnknownOwner(normalizedPath, ownerKey);
            return true;
        }

        ownerNode = null!;
        return false;
    }

    private bool TryRepublishCollisionFromDecodedCache(string ownerKey, Node ownerNode)
    {
        if (!TryGetDecodedCache(ownerKey, out var decoded))
        {
            ownerNode.DecodedCacheAvailable = false;
            return false;
        }

        ownerNode.DecodedCacheAvailable = true;
        ownerNode.DecodedCacheMissRecorded = false;
        ownerNode.CollisionRecoveryRequested = false;
        if (decoded.IsNegative || decoded.Mesh is null)
        {
            MarkCollisionTerminalUnavailable(ownerKey, ownerNode);
            return true;
        }

        StoreCollisionMesh(ownerKey, ownerNode, decoded.Mesh);
        return true;
    }

    private void QueueCollisionRecovery(string ownerKey, Node ownerNode, float priority)
    {
        ownerNode.CollisionRecoveryRequested = true;
        if (ownerNode.DecodeTask is not null)
        {
            return;
        }

        if (ownerNode.DecodeQueued)
        {
            _decodeQueue.Enqueue(ownerKey, priority);
            return;
        }

        if (!_decodeQueue.Enqueue(ownerKey, priority))
        {
            return;
        }

        ownerNode.DecodeQueued = true;
        FrameQueuedDecodes++;
    }

    public int Capacity { get; }
    public int Count => _meshLru.Count;

    /// <summary>
    ///     Bumped on every resident-mesh eviction (LRU overflow, capacity shrink, dispose cascade).
    ///     Batch reuse keys on it: a frozen batch built under an older generation may reference
    ///     evicted GPU buffers and must rebuild. Render-thread only, like the LRU it mirrors.
    /// </summary>
    public int EvictionGeneration { get; private set; }

    /// <summary>Actual arena free/copy-retirement generation at the last empty-block sweep.</summary>
    private ulong _lastArenaSweepReclamationGeneration;

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

        if (GeometryArenaDiagnostics.AuditEnabled && capacity < _meshLru.Count)
        {
            RendererProfilerTrace.Event("geometry-arena", new Dictionary<string, object?>
            {
                ["op"] = "set-capacity",
                ["newCapacity"] = capacity,
                ["residentCount"] = _meshLru.Count,
            });
        }

        _meshLru.SetMaxEntries(capacity);
    }

    /// <summary>The geometry arena, exposed for draw-time liveness/byte-hash validation only.</summary>
    internal GpuGeometryArena12 GeometryArenaForDiagnostics => _geometryArena;

    public int MaxDecodeStartsPerFrame { get; init; } = DefaultMaxDecodeStartsPerFrame;
    public int MaxConcurrentDecodeTasks { get; init; } = DefaultMaxConcurrentDecodeTasks;
    public long DecodedMeshCacheByteBudget { get; }
    public long MaxUploadBytesPerFrame { get; init; } = DefaultMaxUploadBytesPerFrame;

    // Concurrency ceiling used when streaming is unthrottled (the on-demand top-down overlay path,
    // which has no framerate target). Wider than the conservative live-loop default so a single
    // overlay pass can saturate the CPU with decodes instead of trickling 2 at a time.
    private static readonly int UnthrottledMaxConcurrentDecodeTasks =
        Core.Orchestration.CpuBudget.Bulk().Claim(Core.Orchestration.CpuWorkload.ReferenceMeshDecode);

    /// <summary>
    ///     When <c>false</c>, the per-frame streaming budget (decode starts + concurrency + upload
    ///     bytes) is lifted so a caller can load everything visible across a few back-to-back renders.
    ///     The live 60fps loop leaves this <c>true</c> to keep motion smooth; the top-down overlay
    ///     sets it <c>false</c> for the duration of its render. Set via <see cref="ReferenceRenderer12.StreamingThrottled"/>.
    /// </summary>
    public bool StreamingThrottled
    {
        get => _streamingThrottled;
        set
        {
            _streamingThrottled = value;
            // Forward to the texture cache's DISPATCH step: the on-demand overlay renders lift the
            // mesh budgets but were still promoting at most 16×scale textures per pass, making the
            // 2D map's rendered-meshes convergence texture-dispatch-bound (dozens of full
            // re-cull + re-render + readback passes to drain a cold worldspace's texture backlog).
            _textureCache.StreamingThrottled = value;
        }
    }

    private bool _streamingThrottled = true;

    /// <summary>
    ///     Time-based streaming pace: per-frame allowances × the last frame's duration relative to
    ///     60fps (see <see cref="Core.Resources.StreamingFrameBudgetScaler" />). Keeps loading
    ///     throughput frame-rate-independent — a viewer starved to a few FPS by GPU contention
    ///     (another game running) otherwise streams at a few percent of its normal rate. Updated by
    ///     <see cref="ResetFrameStats" />; also consumed by the renderer's upload time budget.
    /// </summary>
    public double FrameBudgetScale { get; private set; } = 1.0;

    private long _lastFrameTimestamp;

    /// <summary>EMA of the observed frame duration that drives the streaming budget scale. Smoothed
    /// because the raw previous frame is partly a RESULT of this budget — see StreamingFrameBudgetScaler.</summary>
    private double _smoothedFrameSeconds;

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

    /// <summary>
    ///     Meshes still waiting to be decoded — the queue's true depth, not a per-frame delta.
    ///     <see cref="FrameQueuedDecodes" /> counts only keys queued for the FIRST time this frame
    ///     (a re-offer of an already-queued key returns early without incrementing it), so on a
    ///     steady-state backlog it reads zero while thousands remain outstanding. Anything deciding
    ///     "is streaming finished?" must consult this instead, or it will conclude the world is
    ///     complete while the queue is still full.
    /// </summary>
    public int PendingDecodeCount => _decodeQueue.Count;
    public int FrameCpuDecodedCacheHits { get; private set; }
    public int FrameCpuDecodedCacheMisses { get; private set; }
    public int FrameCpuDecodedNegativeHits { get; private set; }
    public int FrameGpuUploads { get; private set; }
    /// <summary>GPU materialization attempts that failed transiently in this frame.</summary>
    public int FrameMaterializationFailures { get; private set; }
    /// <summary>
    ///     Resident decoded nodes that still need a GPU materialization retry. Unlike the per-frame
    ///     failure counter this persists across batch reuse, so capture quiescence cannot mistake a
    ///     quiet gap between retries for a settled scene.
    /// </summary>
    public int PendingMaterializationRetryCount => _pendingMaterializationRetries;
    public double FrameGpuUploadMilliseconds => _frameUploadMsConsumed;
    public int FrameByteBudgetDeferrals { get; private set; }
    public int FrameCompressedTextureUploads => _textureCache.FrameCompressedUploads;
    public int FrameRgbaTextureUploads => _textureCache.FrameRgbaFallbackUploads;
    public int FrameQueuedTextureResolves => _textureCache.FrameQueuedResolves;
    public int FrameActiveTextureResolves => _textureCache.FrameActiveResolves;
    public int PendingTextureResolves => _textureCache.PendingResolveCount;
    public int PendingTextureUploads => _textureCache.PendingUploadCount;
    public int TotalMissingModelPaths => _decoder.TotalMissingModelPaths;
    public int TotalSkinnedModelPaths => _decoder.TotalSkinnedModelPaths;
    public int TotalParseFailedModelPaths => _decoder.TotalParseFailedModelPaths;
    public int TotalMissingGeometryBlobs => _decoder.TotalMissingGeometryBlobs;
    public int TotalDecodeFailedGeometryBlobs => _decoder.TotalDecodeFailedGeometryBlobs;

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
        FrameMaterializationFailures = 0;
        FrameByteBudgetDeferrals = 0;

        // Self-measured frame duration → the time-based streaming pace for this frame. Driven from a
        // SMOOTHED frame time: the raw previous frame is the output of a loop this budget feeds, so
        // using it directly let one hitch license a burst that sustained the hitch. First frame (no
        // prior timestamp) stays at 1.0.
        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTimestamp == 0)
        {
            FrameBudgetScale = 1.0;
        }
        else
        {
            _smoothedFrameSeconds = Core.Resources.StreamingFrameBudgetScaler.SmoothFrameSeconds(
                _smoothedFrameSeconds,
                Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds);
            FrameBudgetScale = Core.Resources.StreamingFrameBudgetScaler.Scale(_smoothedFrameSeconds);
        }

        _lastFrameTimestamp = now;

        _frameUploadByteBudget = new FrameByteBudget(StreamingThrottled
            ? Core.Resources.StreamingFrameBudgetScaler.ScaleBytes(MaxUploadBytesPerFrame, FrameBudgetScale)
            : long.MaxValue);
        _frameUploadMsBudget = StreamingThrottled
            ? Core.Resources.StreamingFrameBudgetScaler.TimeBudgetMilliseconds(
                UploadMillisecondsPerFrame, FrameBudgetScale, _smoothedFrameSeconds * 1000.0)
            : double.MaxValue;
        _frameUploadMsConsumed = 0;
        _textureCache.ResetFrameStats(FrameBudgetScale);
        PruneCompletedDecodeTasks();
        ReleaseDrainedArenaBlocks();
    }

    /// <summary>
    ///     Hands back any arena block that has fully drained. The arena's committed bytes were
    ///     otherwise a monotonic high-water mark for the session — on Fallout 76 the arena is a large
    ///     part of the ~8 GB of unmanaged memory that keeps a capture from settling, and it is UPLOAD
    ///     heap, i.e. system RAM.
    ///     <para>
    ///         Gated on the arena's actual reclamation generation rather than the eviction-request
    ///         generation. Mesh frees mature later on the deletion queue, and a rejected upload can
    ///         free without an eviction at all; only the arena knows when either event really made a
    ///         block newly releasable. With no actual free/copy retirement, steady state does no scan.
    ///     </para>
    /// </summary>
    private void ReleaseDrainedArenaBlocks()
    {
        var reclamationGeneration = _geometryArena.ReclamationGeneration;
        if (reclamationGeneration == _lastArenaSweepReclamationGeneration)
        {
            return;
        }

        _lastArenaSweepReclamationGeneration = reclamationGeneration;
        var released = _geometryArena.ReleaseEmptyBlocks();
        if (released > 0 && GeometryArenaDiagnostics.AuditEnabled)
        {
            RendererProfilerTrace.Event("geometry-arena", new Dictionary<string, object?>
            {
                ["op"] = "release-empty-blocks",
                ["releasedBytes"] = released,
                ["evictionGeneration"] = EvictionGeneration,
                ["reclamationGeneration"] = reclamationGeneration
            });
        }
    }

    /// <summary>
    ///     Per-frame budget of ACTUAL mesh-upload milliseconds (scaled by <see cref="FrameBudgetScale" />
    ///     when throttled; see the <c>_frameUploadMsConsumed</c> field notes). Set by the renderer from
    ///     its FALLOUT_VIEWER_REFERENCE_UPLOAD_MS_PER_FRAME knob.
    /// </summary>
    /// <summary>
    ///     Base per-frame render-thread upload allowance, before time-based scaling.
    ///     <para>
    ///         Raised from 2.0 when the budget gate became PREDICTIVE. Previously a frame spent the
    ///         whole allowance and then started one more upload on top, so the effective spend was
    ///         roughly double the nominal figure — the budget said 2 ms and the frame took ~12 ms of
    ///         resolve on the frames that hit it. Removing that overshoot without adjusting the base
    ///         therefore cut real streaming throughput ~3x (measured: 2.94 -> 0.90 uploads/frame),
    ///         which just trades a pacing spike for a longer stretch of half-loaded scene. The base is
    ///         set to the old EFFECTIVE spend so throughput is preserved and only the variance is
    ///         removed.
    ///     </para>
    /// </summary>
    public double UploadMillisecondsPerFrame { get; set; } = 4.0;

    public CachedNifMesh12? GetOrUpload(
        ID3D12GraphicsCommandList commandList,
        string modelPath,
        ref int uploadBudget,
        float priority = 0f,
        AlternateTextureSet? alternateTextures = null)
    {
        ArgumentNullException.ThrowIfNull(commandList);
        return GetOrResolve(
            commandList,
            false,
            modelPath,
            ref uploadBudget,
            priority,
            alternateTextures);
    }

    /// <summary>
    ///     Resolves an already-generated FO4 BNDS tube through the same decoded LRU, upload budget,
    ///     transactional DEFAULT-heap publication, texture cache, and GPU mesh LRU as archive NIFs.
    ///     The generated payload is retained by the placement cache, so decoded-LRU eviction can
    ///     re-arm this entry without an archive lookup or an unbounded task result.
    /// </summary>
    public CachedNifMesh12? GetOrUploadGenerated(
        ID3D12GraphicsCommandList commandList,
        BendableSplineRenderMesh generated,
        ref int uploadBudget)
    {
        ArgumentNullException.ThrowIfNull(commandList);
        ArgumentNullException.ThrowIfNull(generated);
        if (_disposed) return null;

        PruneCompletedDecodeTasks();
        StartQueuedDecodes();

        var cacheKey = generated.CacheKey;
        if (_meshLru.TryGet(cacheKey, out var existing))
        {
            // DecodedCacheAvailable is only a hint and remains true until a failed LRU probe clears
            // it. Probe the bounded cache directly; when it has evicted this small wrapper, rebuild
            // it around the placement-owned vertex/index arrays without copying those arrays.
            if (existing.Mesh is null && existing.PendingPublication is null &&
                !TryGetDecodedCache(cacheKey, out _))
            {
                StoreDecodedCache(cacheKey, WrapGeneratedSpline(generated));
                existing.DecodedCacheAvailable = true;
                existing.DecodedCacheMissRecorded = false;
            }

            return ResolveExisting(
                cacheKey, existing, commandList, collisionOnly: false, ref uploadBudget);
        }

        FrameCacheMisses++;
        var node = new Node(
            Mesh: null,
            DecodeTask: null,
            ResolvedNull: false)
        {
            DecodePath = cacheKey,
            DecodedCacheAvailable = true
        };
        StoreDecodedCache(cacheKey, WrapGeneratedSpline(generated));
        _meshLru.Set(cacheKey, node);
        return ResolveExisting(
            cacheKey, node, commandList, collisionOnly: false, ref uploadBudget);
    }

    private static DecodedNifMesh12 WrapGeneratedSpline(BendableSplineRenderMesh generated)
    {
        var submesh = new DecodedSubmesh12(
            generated.Vertices,
            generated.Indices,
            DiffuseTexturePath: generated.DiffuseTexturePath,
            NormalMapTexturePath: generated.NormalMapTexturePath,
            HasBump: !string.IsNullOrEmpty(generated.NormalMapTexturePath),
            AlphaRenderMode: NifAlphaRenderMode.Opaque,
            AlphaBlend: false,
            AlphaTest: false,
            AlphaTestThreshold: 0.5f,
            AlphaTestFunction: 4,
            SrcBlendMode: 6,
            DstBlendMode: 7,
            MaterialAlpha: 1f,
            DoubleSided: false,
            IsEmissive: false,
            LocalBoundsCenter: generated.LocalBoundsCenter,
            LocalBoundsRadius: generated.LocalBoundsRadius,
            IsBillboard: false);

        // Retail also builds two capsule collision spans. Until that narrow collision shape is
        // reconstructed, AbsentOrUnsupported deliberately admits the existing solid visual-soup
        // fallback instead of falsely claiming the procedural object is non-collidable.
        return new DecodedNifMesh12([submesh]);
    }

    /// <summary>
    ///     Shared node/decode resolution. A null <paramref name="commandList" /> is admitted only for
    ///     collision warmup: positive decoded geometry may populate the CPU collision cache, but it
    ///     must remain GPU-unrealized until the render path returns with an open direct command list.
    /// </summary>
    private CachedNifMesh12? GetOrResolve(
        ID3D12GraphicsCommandList? commandList,
        bool collisionOnly,
        string modelPath,
        ref int uploadBudget,
        float priority = 0f,
        AlternateTextureSet? alternateTextures = null)
    {
        if (!collisionOnly && commandList is null)
        {
            throw new ArgumentNullException(nameof(commandList));
        }

        if (_disposed) return null;

        PruneCompletedDecodeTasks();
        StartQueuedDecodes();

        var decodePath = ReferenceMeshDecoder12.NormalizeModelPath(modelPath);
        // A placement whose base carries MODS alternate textures gets its own cache entry per texture
        // variant (so a shared NIF — e.g. every billboard — renders its correct per-base textures).
        // Archive lookup uses the plain decodePath via Node.DecodePath. GPU/decoded/disk cache keys
        // discriminate variants (the disk key folds Node.VariantKey). Collision is still plain-path
        // keyed; that is only safe for authored Havok, because material swaps can change whether a
        // visual submesh is admitted. The active collision backlog tracks the required variant fix.
        var cacheKey = alternateTextures is null
            ? decodePath
            : decodePath + "#" + alternateTextures.VariantKey;
        // TryGet bumps the hit to MRU — the old explicit Touch.
        if (_meshLru.TryGet(cacheKey, out var existing))
        {
            var resolved = ResolveExisting(
                cacheKey, existing, commandList, collisionOnly, ref uploadBudget);
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
        var mesh = ResolveExisting(
            cacheKey, node, commandList, collisionOnly, ref uploadBudget);
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

        // Ordinarily EndFrame/AbortFrame has already resolved this list. Keep teardown robust when
        // a host disposes after abandoning its render loop without delivering that boundary.
        RollbackPendingPublicationsNoThrow(invalidateBatches: false, preserveRetries: false);

        DrainDecodeTasksForDispose();

        // Producers are drained; let the persist writer flush the queued disk writes before the stats
        // log. Bounded wait — an abandoned tail write just means those meshes re-decode next session.
        //
        // MUST be a NON-PUMPING wait (same reason as DrainDecodeTasksForDispose above): Dispose runs
        // on the STA UI thread during a worldspace switch, and a managed Task.Wait there becomes a COM
        // pumping wait that can dispatch input back into XAML mid-callback — CXcpDispatcher::
        // CheckReentrancy then fail-fasts the process with a stowed 0xc000027b / E_UNEXPECTED. That
        // was the crash: three WER reports (2026-08-07, and two on 08-11) share fault bucket
        // StackHash12_f74 at Microsoft.UI.Xaml.dll+0x3ace5d, and both 08-11 per-process logs end with
        // a COMPLETED worldspace switch and no following "building cell grid" line — i.e. the process
        // died in the NEXT switch's teardown, here. NonPumpingWait never throws on a faulted task, so
        // the fault is observed explicitly below.
        if (_persistQueue is not null)
        {
            _persistQueue.Writer.TryComplete();
            if (_persistWriterTask is { } persistWriterTask)
            {
                if (!Core.Orchestration.NonPumpingWait.Wait(persistWriterTask, TimeSpan.FromSeconds(30)))
                {
                    Log.Warn("ReferenceMeshCache12: persist writer did not flush within 30s during " +
                             "dispose; abandoning the tail write (those meshes re-decode next session).");
                }
                else if (persistWriterTask.Exception is { } persistWriterFault)
                {
                    Log.Warn("ReferenceMeshCache12: persist writer faulted during dispose: {0}",
                        persistWriterFault.GetBaseException().Message);
                }
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

    private CachedNifMesh12? ResolveExisting(
        string modelPath,
        Node node,
        ID3D12GraphicsCommandList? commandList,
        bool collisionOnly,
        ref int uploadBudget)
    {
        // Consume completed work before either resident or render-null early return. Otherwise a
        // successful Task retains its DecodedNifMesh12 indefinitely even after _decodedLru evicts the
        // bounded copy, and collision-only recovery accidentally depends on that unbounded retention.
        DrainCompletedDecodeTask(modelPath, node);
        if (node.Mesh is not null)
        {
            ClearMaterializationRetry(node);
            return node.Mesh;
        }
        if (node.PendingPublication is { } pending)
        {
            // Same-frame repeated placements must share the materialized mesh whose copy is already
            // recorded. It is safe for this open list, but remains absent from durable node.Mesh until
            // the recorder reports successful submission.
            ClearMaterializationRetry(node);
            return pending.Mesh;
        }
        if (node.ResolvedNull)
        {
            // Terminal render-null nodes never carry materialization work. Keep this defensive clear
            // at the early return so a future terminal path cannot strand current-demand retry debt.
            ClearMaterializationRetry(node);
            return null;
        }
        if (!collisionOnly && node.MaterializationRetryDemandGeneration != 0)
        {
            // A fresh demand generation resets the aggregate count in O(1), leaving old node stamps
            // as membership hints. Re-adopt a still-visible failed node before decoded-cache, byte,
            // time, or upload-count admission can defer it; otherwise a deferred retry could make a
            // completed traversal look quiescent and then freeze under stable batch reuse.
            MarkMaterializationRetry(node, countFrameFailure: false);
        }
        if (TryResolveFromDecodedCache(
                modelPath, node, commandList, collisionOnly, ref uploadBudget, out var cachedMesh))
        {
            return cachedMesh;
        }

        return null;
    }

    private void DrainCompletedDecodeTask(string cacheKey, Node node)
    {
        if (node.DecodeTask is not { IsCompleted: true } task)
        {
            return;
        }

        node.DecodeTask = null;
        node.DecodeQueued = false;
        if (task.IsFaulted || task.IsCanceled)
        {
            Log.Warn(
                "ReferenceMeshCache12: decode failed for '{0}': {1}",
                cacheKey,
                task.Exception?.GetBaseException().Message ??
                (task.IsCanceled ? "(cancelled)" : "(unknown)"));
            if (node.Mesh is null)
            {
                node.ResolvedNull = true;
            }
            ClearMaterializationRetry(node);
            node.CollisionRecoveryRequested = false;
            StoreDecodedCache(cacheKey, null);
            MarkCollisionTerminalUnavailable(cacheKey, node);
            return;
        }

        // This method runs on the STA UI thread via the render tick, where a blocking read would be
        // a COM pumping wait (see UiThreadPumpingWaitGuardTests) — but `task` was pattern-matched
        // non-pumping: `{ IsCompleted: true }` at the top of this method, with an early return
        // otherwise, so reading Result here can neither block nor pump.
        var decoded = task.Result;
        if (decoded is null)
        {
            if (node.Mesh is null)
            {
                node.ResolvedNull = true;
            }
            ClearMaterializationRetry(node);
            node.CollisionRecoveryRequested = false;
            StoreDecodedCache(cacheKey, null);
            MarkCollisionTerminalUnavailable(cacheKey, node);
            return;
        }

        StoreDecodedCache(cacheKey, decoded);
        // The decode above also persisted to disk, so if the decoded-LRU entry is later evicted the
        // node may probe the on-disk cache once more (and hit) instead of re-decoding.
        node.DecodedCacheAvailable = true;
        node.DecodedCacheMissRecorded = false;
        if (node.CollisionRecoveryRequested)
        {
            node.CollisionRecoveryRequested = false;
            StoreCollisionMesh(cacheKey, node, decoded);
        }
    }

    private void MarkCollisionTerminalUnavailable(string cacheKey, Node node)
    {
        _collisionRecovery.MarkTerminalUnavailable(
            node.DecodePath,
            cacheKey,
            _collisionLru.ContainsKey(node.DecodePath));
    }

    private bool TryResolveFromDecodedCache(
        string modelPath,
        Node node,
        ID3D12GraphicsCommandList? commandList,
        bool collisionOnly,
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
            ClearMaterializationRetry(node);
            node.CollisionRecoveryRequested = false;
            MarkCollisionTerminalUnavailable(modelPath, node);
            return true;
        }

        node.DecodedCacheAvailable = true;
        if (collisionOnly)
        {
            // This is deliberately BEFORE every GPU upload gate/counter. Collision warmup can run
            // before BeginFrame, and a positive decoded payload is enough to publish its CPU soup.
            // Do not mark the node render-null: its GPU mesh remains pending for the render path.
            StoreCollisionMesh(modelPath, node, decoded.Mesh);
            return true;
        }

        if (commandList is null)
        {
            throw new InvalidOperationException(
                "GPU mesh realization requires an open D3D12 command list.");
        }

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
        // PROJECTED, not just consumed: testing only what has already been spent let a frame use its
        // whole budget and then start one more upload on top, overshooting by that mesh's full cost.
        // Measured, that turned a ~3.6 ms allowance into ~8.5 ms of extra resolve time on the frames
        // that hit it — a burst landing on exactly the frames where new content appears, which is
        // what a heartbeat while loading feels like. The estimate self-calibrates from observed
        // throughput, so it needs no hardware assumptions.
        if (_frameUploadByteBudget.Count > 0 &&
            _frameUploadMsConsumed + EstimateUploadMilliseconds(decoded.ByteSize) > _frameUploadMsBudget)
        {
            FrameByteBudgetDeferrals++;
            return true;
        }

        uploadBudget--;
        FrameGpuUploads++;
        _frameUploadByteBudget.Record(decoded.ByteSize);
        // Upload uses the plain archive path and currently overwrites one plain-path collision entry.
        // Authored Havok is variant-independent; synthesized visual soup is not necessarily so because
        // decoded material swaps can change AlphaBlend/IsEmissive/water admission. Until collision
        // identity carries the variant, the last uploaded variant wins (tracked in the active backlog).
        var uploadStarted = Stopwatch.GetTimestamp();
        // Collision is a placed-reference cache concern, while GPU materialization is shared with
        // the standalone native viewer. Keep the collision side effect here so the shared helper
        // remains archive/node agnostic.
        StoreCollisionMesh(modelPath, node, decoded.Mesh);
        var materialization = UploadDecodedMesh(
            commandList,
            node.DecodePath,
            decoded.Mesh,
            _geometryArena,
            _deletionQueue,
            _textureCache);
        var uploadMs = Stopwatch.GetElapsedTime(uploadStarted).TotalMilliseconds;
        _frameUploadMsConsumed += uploadMs;
        RecordUploadThroughput(decoded.ByteSize, uploadMs);
        if (materialization.Status == MeshMaterializationStatus.RetryableFailure)
        {
            MarkMaterializationRetry(node);
            return true;
        }

        ClearMaterializationRetry(node);
        if (materialization.Status == MeshMaterializationStatus.RenderEmpty)
        {
            // Collision was installed before shared GPU materialization reported an authored
            // render-empty payload. Keep that positive decoded payload in the CPU/disk caches;
            // replacing it with a total
            // negative would discard the provenance after collision-LRU eviction or next launch.
            node.ResolvedNull = true;
            return true;
        }

        mesh = materialization.Mesh
               ?? throw new InvalidOperationException("Successful mesh materialization returned no mesh.");
        if (_geometryArena.BackingMode == GpuGeometryArenaBackingMode.DefaultHeap)
        {
            if (!TryStagePendingPublication(modelPath, node, mesh))
            {
                mesh = null;
                MarkMaterializationRetry(node);
                return true;
            }

            // The copy and every draw through this return value belong to the same open list. The
            // node remains transactionally pending until EndFrame submits that list.
            return true;
        }

        node.Mesh = mesh;
        // Publish the node's real arena footprint now that it has one. The LRU sized this node at 0
        // when Set inserted it (nodes are populated in place after an async decode, so sizeOf could
        // only ever observe an empty node), and UpdateSize is deliberately non-evicting — it must
        // not run the dispose cascade on the mesh being published right here.
        // NOTE: `modelPath` is this method's parameter name, but every caller passes the LRU cache
        // key (path + optional "#variant"), which is what UpdateSize needs.
        _meshLru.UpdateSize(modelPath, mesh.Geometry.Allocation.AlignedSize);
        return true;
    }

    private bool TryStagePendingPublication(string cacheKey, Node node, CachedNifMesh12 mesh)
    {
        if (node.PendingPublication is not null)
        {
            Log.Warn(
                "ReferenceMeshCache12: duplicate pending DEFAULT publication for '{0}'; retrying next frame.",
                cacheKey);
            SafeDisposePendingMesh(mesh, cacheKey, "duplicate-pending");
            return false;
        }

        var pending = new PendingMeshPublication(cacheKey, node, mesh);
        node.PendingPublication = pending;
        _pendingMeshPublications.Add(pending);
        try
        {
            _recorder.EnlistCurrentFrame(this);
            return true;
        }
        catch (Exception ex)
        {
            pending.Completed = true;
            node.PendingPublication = null;
            _pendingMeshPublications.Remove(pending);
            SafeDisposePendingMesh(mesh, cacheKey, "enlist-failed");
            Log.Warn(
                "ReferenceMeshCache12: DEFAULT publication enlistment failed for '{0}': {1}",
                cacheKey, ex.Message);
            return false;
        }
    }

    void IGpuCommandSubmissionParticipant12.OnCommandListSubmitted() =>
        CommitPendingPublicationsNoThrow();

    void IGpuCommandSubmissionParticipant12.OnCommandListAborted() =>
        RollbackPendingPublicationsNoThrow(invalidateBatches: true, preserveRetries: true);

    /// <summary>
    ///     Commits DEFAULT-heap cache state only after Execute+Signal succeeded. Stale entries whose
    ///     LRU node was evicted during the open frame are disposed instead; they may still have been
    ///     used by that submitted list, so CachedNifMesh12 routes the geometry free through the same
    ///     frames-in-flight deletion queue.
    /// </summary>
    private void CommitPendingPublicationsNoThrow()
    {
        var invalidateBatches = false;
        try
        {
            foreach (var pending in _pendingMeshPublications)
            {
                if (pending.Completed)
                {
                    continue;
                }

                pending.Completed = true;
                try
                {
                    var nodeOwnsPending = ReferenceEquals(pending.Node.PendingPublication, pending);
                    if (nodeOwnsPending)
                    {
                        pending.Node.PendingPublication = null;
                    }

                    if (!_disposed && !pending.Evicted && nodeOwnsPending &&
                        _meshLru.TryPeek(pending.CacheKey, out var current) &&
                        ReferenceEquals(current, pending.Node))
                    {
                        pending.Node.Mesh = pending.Mesh;
                        _meshLru.UpdateSize(
                            pending.CacheKey, pending.Mesh.Geometry.Allocation.AlignedSize);
                        continue;
                    }

                    SafeDisposePendingMesh(pending.Mesh, pending.CacheKey, "stale-on-submit");
                }
                catch (Exception ex)
                {
                    // Publication bookkeeping must never escape into EndFrame. Re-arm a resident
                    // decoded node so the next batch build retries rather than retaining a half-
                    // published mesh whose LRU size was not established.
                    if (ReferenceEquals(pending.Node.PendingPublication, pending))
                    {
                        pending.Node.PendingPublication = null;
                    }

                    if (ReferenceEquals(pending.Node.Mesh, pending.Mesh))
                    {
                        pending.Node.Mesh = null;
                    }

                    try
                    {
                        if (!_disposed && _meshLru.TryPeek(pending.CacheKey, out var current) &&
                            ReferenceEquals(current, pending.Node))
                        {
                            MarkMaterializationRetry(pending.Node, countFrameFailure: false);
                        }
                    }
                    catch (Exception retryEx)
                    {
                        Log.Warn(
                            "ReferenceMeshCache12: failed to re-arm DEFAULT publication retry for '{0}': {1}",
                            pending.CacheKey, retryEx.Message);
                    }

                    invalidateBatches = true;
                    SafeDisposePendingMesh(pending.Mesh, pending.CacheKey, "commit-failed");
                    Log.Warn(
                        "ReferenceMeshCache12: DEFAULT publication commit failed for '{0}': {1}",
                        pending.CacheKey, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            invalidateBatches = true;
            Log.Warn("ReferenceMeshCache12: DEFAULT publication commit sweep failed: {0}", ex.Message);
        }
        finally
        {
            _pendingMeshPublications.Clear();
            if (invalidateBatches)
            {
                EvictionGeneration++;
            }
        }
    }

    private void RollbackPendingPublicationsNoThrow(bool invalidateBatches, bool preserveRetries)
    {
        var rolledBack = false;
        try
        {
            foreach (var pending in _pendingMeshPublications)
            {
                if (pending.Completed)
                {
                    continue;
                }

                pending.Completed = true;
                rolledBack = true;
                try
                {
                    var nodeOwnsPending = ReferenceEquals(pending.Node.PendingPublication, pending);
                    if (nodeOwnsPending)
                    {
                        pending.Node.PendingPublication = null;
                    }

                    if (preserveRetries && !_disposed && !pending.Evicted && nodeOwnsPending &&
                        _meshLru.TryPeek(pending.CacheKey, out var current) &&
                        ReferenceEquals(current, pending.Node))
                    {
                        MarkMaterializationRetry(pending.Node, countFrameFailure: false);
                    }
                }
                catch (Exception ex)
                {
                    if (ReferenceEquals(pending.Node.PendingPublication, pending))
                    {
                        pending.Node.PendingPublication = null;
                    }

                    Log.Warn(
                        "ReferenceMeshCache12: DEFAULT publication rollback failed for '{0}': {1}",
                        pending.CacheKey, ex.Message);
                }
                finally
                {
                    // Every uncommitted mesh is disposed independently. A bookkeeping failure for
                    // one LRU node must not leak this mesh or prevent later pending entries from
                    // reaching their own rollback.
                    SafeDisposePendingMesh(pending.Mesh, pending.CacheKey, "command-list-aborted");
                }
            }
        }
        catch (Exception ex)
        {
            rolledBack = true;
            Log.Warn("ReferenceMeshCache12: DEFAULT publication rollback sweep failed: {0}", ex.Message);
        }
        finally
        {
            _pendingMeshPublications.Clear();
            if (invalidateBatches && rolledBack)
            {
                // A staged batch can already contain the pending mesh. Make every renderer snapshot
                // observe the rollback on its next frame instead of drawing the freed allocation.
                EvictionGeneration++;
            }
        }
    }

    private static void SafeDisposePendingMesh(
        CachedNifMesh12 mesh, string cacheKey, string reason)
    {
        try
        {
            mesh.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn(
                "ReferenceMeshCache12: pending mesh disposal failed for '{0}' ({1}): {2}",
                cacheKey, reason, ex.Message);
        }
    }

    /// <summary>
    ///     Starts accounting retry debt for a fresh desired-set traversal. Call exactly once for a
    ///     newly-created batch-build state, never for a valid incremental continuation. Nodes that
    ///     fell out of camera demand retain only a stale token and no longer veto capture.
    /// </summary>
    public void BeginMaterializationRetryDemandGeneration()
    {
        _pendingMaterializationRetries = 0;
        if (_materializationRetryDemandGeneration != ulong.MaxValue)
        {
            _materializationRetryDemandGeneration++;
            return;
        }

        // Practically unreachable, but make token wrap exact instead of allowing an ancient node
        // tagged generation 1 to alias the new traversal. This scan runs at most once per 2^64
        // desired-set generations and remains off every normal frame path.
        foreach (var key in _meshLru.SnapshotKeys())
        {
            if (_meshLru.TryPeek(key, out var node))
            {
                node.MaterializationRetryDemandGeneration = 0;
            }
        }

        _materializationRetryDemandGeneration = 1;
    }

    private void MarkMaterializationRetry(Node node, bool countFrameFailure = true)
    {
        if (countFrameFailure)
        {
            FrameMaterializationFailures++;
        }

        if (node.MaterializationRetryDemandGeneration ==
            _materializationRetryDemandGeneration)
        {
            return;
        }

        node.MaterializationRetryDemandGeneration = _materializationRetryDemandGeneration;
        _pendingMaterializationRetries++;
    }

    private void ClearMaterializationRetry(Node node)
    {
        var belongsToCurrentDemand = node.MaterializationRetryDemandGeneration ==
                                     _materializationRetryDemandGeneration;
        node.MaterializationRetryDemandGeneration = 0;
        if (belongsToCurrentDemand && _pendingMaterializationRetries > 0)
        {
            _pendingMaterializationRetries--;
        }
    }

    /// <summary>
    ///     Predicted render-thread cost of uploading <paramref name="byteSize" />, from the observed
    ///     throughput of previous uploads. Seeded pessimistically so the very first uploads of a
    ///     session are paced conservatively rather than let through unmeasured.
    /// </summary>
    private double EstimateUploadMilliseconds(long byteSize) =>
        byteSize <= 0 ? 0 : byteSize * _uploadMillisecondsPerByte;

    /// <summary>
    ///     Folds one observed upload into the throughput estimate. An EMA rather than a running mean:
    ///     the cost per byte shifts with residency pressure and heap state, and the estimate needs to
    ///     track that rather than average over the whole session.
    /// </summary>
    private void RecordUploadThroughput(long byteSize, double milliseconds)
    {
        if (byteSize <= 0 || !double.IsFinite(milliseconds) || milliseconds <= 0)
        {
            return;
        }

        var observed = milliseconds / byteSize;
        _uploadMillisecondsPerByte += (observed - _uploadMillisecondsPerByte) * UploadThroughputAlpha;
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
                node.DecodeTask is not null ||
                (!node.CollisionRecoveryRequested && (node.ResolvedNull || node.Mesh is not null)) ||
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

        if (!_persistentDecodedCache.TryLoad(
                metadata,
                variantKey,
                _starfieldMaterialDatabaseCacheIdentity,
                out var cached))
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
            // Set replaces an existing entry and evicts LRU entries over the byte budget. An entry
            // larger than the whole budget SURVIVES its own insert (alone, over budget) — this
            // prevents a decode→store→self-evict→re-decode livelock for any mesh whose estimate
            // exceeds a (diagnostically) tiny budget.
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
                _starfieldMaterialDatabaseCacheIdentity,
                decoded is null ? null : ReferenceMeshDecoder12.ToPersistentPayload(decoded));
        }
        catch (Exception ex)
        {
            Log.Warn("ReferenceMeshCache12: decoded mesh disk cache write skipped for '{0}': {1}", decodePath, ex.Message);
        }
    }

    /// <summary>
    ///     Materializes an already-decoded Bethesda mesh through the exact placed-reference GPU
    ///     packing/material route. The native asset viewer calls this with its own short-lived arena
    ///     and scene texture cache so Starfield slots, alpha state, environment maps, and authored
    ///     water diversion cannot drift into a second implementation.
    /// </summary>
    internal static MeshMaterializationResult UploadDecodedMesh(
        ID3D12GraphicsCommandList? commandList,
        string modelPath,
        DecodedNifMesh12 decoded,
        GpuGeometryArena12 geometryArena,
        GpuDeletionQueue12 deletionQueue,
        GpuTextureCache12 textureCache)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(geometryArena);
        ArgumentNullException.ThrowIfNull(deletionQueue);
        ArgumentNullException.ThrowIfNull(textureCache);
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
        if (totalVertexCount == 0 || totalIndexCount == 0)
        {
            return MeshMaterializationResult.RenderEmpty;
        }

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
            // Pack both streams into one shared arena range. UPLOAD backing memcpy's directly;
            // DEFAULT backing stages and records its copy/barrier on this frame's active command
            // list. Either mode publishes identical VBV/IBV addresses into the cached submeshes.
            var vertexBytes =
                System.Runtime.InteropServices.MemoryMarshal.AsBytes<GpuMeshUploader.GpuVertex>(vertices);
            var indexBytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes<ushort>(indices);
            geometry = geometryArena.BackingMode == GpuGeometryArenaBackingMode.UploadHeap
                ? geometryArena.Upload(vertexBytes, indexBytes, debugTag: modelPath)
                : geometryArena.Upload(
                    commandList ?? throw new InvalidOperationException(
                        "DEFAULT-heap decoded mesh materialization requires an open command list."),
                    deletionQueue,
                    vertexBytes,
                    indexBytes,
                    debugTag: modelPath);
        }
        catch (Exception ex)
        {
            Log.Warn("ReferenceMeshCache12: geometry arena upload failed for '{0}': {1}", modelPath, ex.Message);
            return MeshMaterializationResult.RetryableFailure;
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

        // A rigid node-animated submesh moves through baked model-space deltas. Bound the whole
        // clip conservatively: |delta·v| <= maxScale·|v| + |maxTranslation| for a sphere around
        // the NIF origin, using the largest per-sample scale/translation the track visits.
        foreach (var sub in decoded.Submeshes)
        {
            if (sub.RigidNodeAnimation is not { } rigidTrack || rigidTrack.SampleCount == 0)
            {
                continue;
            }

            var restReach = 0f;
            foreach (var vertex in sub.Vertices)
            {
                restReach = MathF.Max(restReach, vertex.Position.Length());
            }

            var maxScale = 1f;
            var maxTranslation = 0f;
            for (var s = 0; s < rigidTrack.SampleCount; s++)
            {
                var scale = rigidTrack.Scales[s];
                maxScale = MathF.Max(maxScale, MathF.Max(scale.X, MathF.Max(scale.Y, scale.Z)));
                maxTranslation = MathF.Max(maxTranslation, rigidTrack.Translations[s].Length());
            }

            var animatedReach = (restReach * maxScale) + maxTranslation;
            meshLocalRadiusSq = MathF.Max(meshLocalRadiusSq, animatedReach * animatedReach);
        }

        // Degenerate (no vertices) → collapse the AABB to the origin so consumers see min == max and
        // fall back to the sphere rather than an inverted box.
        if (aabbMin.X > aabbMax.X)
        {
            aabbMin = aabbMax = Vector3.Zero;
        }

        var submeshes = new List<CachedSubmesh12>(decoded.Submeshes.Count);
        var materializationFailed = false;
        var softParticleSources = new HashSet<NifSoftParticleSource>();
        // Authored positions + triangle indices of submeshes classified as placed water (normally
        // WaterShaderProperty caves/pools; also the bounded TES3 Vivec legacy signature), captured so
        // the dedicated renderer can transform and draw their real geometry. FNV WATER000 consumes
        // source positions directly; reducing them to an AABB changed rotated/sloped/circular surfaces.
        List<NifWaterGeometry>? waterPlanesLocal = null;
        var vertexStride = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        for (var i = 0; i < decoded.Submeshes.Count; i++)
        {
            var sub = decoded.Submeshes[i];
            var submeshCountBefore = submeshes.Count;
            List<GpuTextureCache12.Entry>? acquiredTextures = null;

            GpuTextureCache12.Entry Acquire(GpuTextureCache12.Entry entry)
            {
                // Pinned fallbacks/synthetics are harmless to pass to Release (it ignores a null
                // CacheKey), which keeps rollback uniform across every texture route.
                (acquiredTextures ??= new List<GpuTextureCache12.Entry>(8)).Add(entry);
                return entry;
            }

            // Placed water geometry is NOT drawn as a reference slab. Divert it: retain the authored
            // triangles for WaterRenderer12 and skip the drawable submesh, preventing a double draw.
            // For the TES3 legacy signature this deliberately substitutes the shared Morrowind water
            // material; source texture/UV/alpha state is not represented by NifWaterGeometry today.
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

            // Starfield materials that exist but resolve NO albedo in any form: normal-only detail
            // decals (bolts/trims/bevels), glow-only strips, occlusion boxes, BlankNoRender
            // placeholders. The engine draws them through channels this renderer does not implement
            // yet — or never draws them at all — and the white fallback painted every Akila City
            // building with bright white overlay shapes (13.9k placed instances measured). Skip until
            // normal-mapped decal rendering exists. Materials MISSING from the database keep the
            // white fallback: that is broken content and should stay loudly visible.
            if (sub.DiffuseTexturePath is { } matPath &&
                matPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase) &&
                textureCache.IsStarfieldNoDrawMaterial(matPath))
            {
                continue;
            }
            try
            {
                // GRASS2000 reuses the direct-draw uSoftParticle slot as WindData. Enforce the
                // material-route union here so a TallGrass mesh can never retain an authored or
                // heuristic soft-particle classification and feed conflicting constants later.
                var softParticle = sub.IsTallGrass
                    ? NifSoftParticleSettings.Disabled
                    : NifSoftParticlePolicy.Resolve(new NifSoftParticleCandidate(
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
                    // Untextured shape with an authored legacy NiMaterialProperty: bind its diffuse
                    // as a pinned 1×1 solid instead of the white fallback. This approximates
                    // Gamebryo's fixed-function output (matAmbient × sceneAmbient + matDiffuse ×
                    // diffuseLight + matEmissive) — exact when Ambient == Diffuse (Bethesda's
                    // authoring norm) and exact for the authored-black case this fixes
                    // (SewerExitGateExterior01's 'black' Plane02 rendered lit near-white).
                    // Material-less shapes keep the legacy WhitePixel path byte-identical.
                    if (sub.MaterialDiffuse is { } materialDiffuse)
                    {
                        var baseColor =
                            NifMaterialDiffusePolicy.ResolveUntexturedBaseColor(materialDiffuse);
                        diffuse = Acquire(textureCache.GetOrCreateSynthetic(
                            NifMaterialDiffusePolicy.SyntheticTextureKey(baseColor), 1, 1,
                            NifMaterialDiffusePolicy.ToRgbaPixel(baseColor)));
                    }
                    else
                    {
                        diffuse = textureCache.WhitePixel;
                    }
                }
                else if (string.Equals(sub.DiffuseTexturePath, RenderableSubmesh.WaterSurfaceTexturePath, StringComparison.Ordinal))
                {
                    diffuse = textureCache.WaterSurface;
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
                    diffuse = Acquire(textureCache.GetOrUpload(diffusePath));
                }
                // Starfield stores both albedo and normal behind one .mat name; decoded meshes carry
                // that name only in the diffuse lane. Derive the role-qualified normal request here
                // (after the persistent decoded-mesh cache) so warm payloads gain slot 1 without a
                // DecoderVersion bump/cold-cache rebuild. This does not admit skipped no-albedo
                // shapes or add a draw — it only changes the normal bound to an existing submesh.
                var starfieldMaterialPath =
                    sub.DiffuseTexturePath is { } materialPath &&
                    MaterialTexturePathResolver.IsStarfieldMaterialPath(materialPath)
                        ? materialPath
                        : null;
                var normalBinding = StarfieldMaterialNormalPolicy.Resolve(
                    sub.DiffuseTexturePath,
                    sub.NormalMapTexturePath,
                    sub.HasBump,
                    sub.Vertices);
                var normal = !string.IsNullOrEmpty(normalBinding.TexturePath)
                    ? Acquire(textureCache.GetOrUpload(normalBinding.TexturePath!, isNormalMap: true))
                    : textureCache.FlatNormal;
                var starfieldOpacity =
                    starfieldMaterialPath is not null &&
                    sub.StarfieldMaterialAlpha.IsLayer0OpacityCutout
                        ? Acquire(textureCache.GetOrUpload(
                            MaterialTexturePathResolver.BuildStarfieldOpacityMapRequest(
                                starfieldMaterialPath)))
                        : null;
                var specularMap = !string.IsNullOrEmpty(sub.SpecularMapTexturePath)
                    ? Acquire(textureCache.GetOrUpload(sub.SpecularMapTexturePath!))
                    : null;
                var gradientMap = !string.IsNullOrEmpty(sub.GradientMapTexturePath)
                    ? Acquire(textureCache.GetOrUpload(sub.GradientMapTexturePath!))
                    : null;
                System.Diagnostics.Debug.Assert(
                    gradientMap is null || string.IsNullOrEmpty(sub.Lighting30GlowMapTexturePath),
                    "FO4 gradient and classic Lighting30 glow maps cannot share TexIndices.w.");
                var lighting30GlowMap = sub.IsLighting30 && gradientMap is null &&
                                        !string.IsNullOrEmpty(sub.Lighting30GlowMapTexturePath)
                    ? Acquire(textureCache.GetOrUpload(sub.Lighting30GlowMapTexturePath!))
                    : null;
                var hasBgsmEmission = sub.BgsmEmissionColor.X > 0f ||
                                      sub.BgsmEmissionColor.Y > 0f ||
                                      sub.BgsmEmissionColor.Z > 0f;
                var bgsmGlowMap = hasBgsmEmission &&
                                  !string.IsNullOrEmpty(sub.BgsmGlowMapTexturePath)
                    ? Acquire(textureCache.GetOrUpload(sub.BgsmGlowMapTexturePath!))
                    : null;
                // FO4 environment cubemap (nearly always the shared mipblur_defaultoutside1.dds —
                // one texture serving thousands of materials). The env shader term stays off until
                // the entry promotes to a real TextureCube (see CachedSubmesh12.EnvMapState).
                var envMap = !string.IsNullOrEmpty(sub.EnvironmentMapTexturePath) && sub.EnvironmentMapScale > 0f
                    ? Acquire(textureCache.GetOrUpload(sub.EnvironmentMapTexturePath!))
                    : null;
                // FO3/FNV classic PP-lighting environment pass. Slot 5 is its own red-channel mask,
                // not FO4's _s texture; keep both cache entries and packed-state routes separate.
                var classicEnvMap = !string.IsNullOrEmpty(sub.ClassicEnvironmentMapTexturePath) &&
                                    sub.ClassicEnvironmentMapScale > 0f
                    ? Acquire(textureCache.GetOrUpload(sub.ClassicEnvironmentMapTexturePath!))
                    : null;
                var classicEnvMask = classicEnvMap is not null &&
                                     !string.IsNullOrEmpty(sub.ClassicEnvironmentMaskTexturePath)
                    ? Acquire(textureCache.GetOrUpload(sub.ClassicEnvironmentMaskTexturePath!))
                    : null;
                var classicParallaxHeightMap =
                    !string.IsNullOrEmpty(sub.ClassicParallaxHeightMapTexturePath)
                        ? Acquire(textureCache.GetOrUpload(sub.ClassicParallaxHeightMapTexturePath!))
                        : null;
                System.Diagnostics.Debug.Assert(
                    envMap is null || classicEnvMap is null,
                    "FO4 and classic FO3/FNV environment-map routes are mutually exclusive.");
                System.Diagnostics.Debug.Assert(
                    specularMap is null || classicEnvMask is null,
                    "FO4 specular and classic environment masks cannot share TexIndices.z.");
                System.Diagnostics.Debug.Assert(
                    (specularMap is null ? 0 : 1) +
                    (classicEnvMask is null ? 0 : 1) +
                    (classicParallaxHeightMap is null ? 0 : 1) +
                    (starfieldOpacity is null ? 0 : 1) <= 1,
                    "Specular, classic environment-mask, classic height, and Starfield opacity maps cannot share TexIndices.z.");

                var vertexByteOffset = CheckedByteSize(vertexStarts[i], vertexStride);
                var vertexByteSize = CheckedByteSize(sub.Vertices.Length, vertexStride);
                var indexByteOffset = CheckedByteSize(indexStarts[i], sizeof(ushort));
                var indexByteSize = CheckedByteSize(sub.Indices.Length, sizeof(ushort));
                if (GeometryArenaDiagnostics.Enabled &&
                    MeshPackingValidator12.ValidateSubmeshWindow(
                        i, sub.Vertices.Length, sub.Indices,
                        vertexByteOffset, vertexByteSize, indexByteOffset, indexByteSize,
                        vertexStride, geometry.VertexBytes, geometry.IndexBytes) is { } packingViolation)
                {
                    Log.Error("ReferenceMeshCache12: PACKING VIOLATION '{0}': {1}", modelPath, packingViolation);
                    RendererProfilerTrace.Event("geometry-violation", new Dictionary<string, object?>
                    {
                        ["kind"] = "packing",
                        ["path"] = modelPath,
                        ["detail"] = packingViolation,
                    });
                }

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
                    StarfieldMaterialPath = starfieldMaterialPath,
                    StarfieldMaterialColor = sub.StarfieldMaterialColor,
                    StarfieldMaterialAlpha = sub.StarfieldMaterialAlpha,
                    StarfieldOpacity = starfieldOpacity,
                    HasDerivedStarfieldNormal = normalBinding.IsDerived,
                    SpecularMap = specularMap,
                    GradientMap = gradientMap,
                    Lighting30GlowMap = lighting30GlowMap,
                    BgsmGlowMap = bgsmGlowMap,
                    BgsmEmissionColor = hasBgsmEmission ? sub.BgsmEmissionColor : Vector3.Zero,
                    GradientMapV = sub.GradientMapV,
                    EnvMap = envMap,
                    EnvMapScale = sub.EnvironmentMapScale,
                    EnvMapSmoothness = sub.EnvironmentMapSmoothness,
                    ClassicEnvMap = classicEnvMap,
                    ClassicEnvMask = classicEnvMask,
                    ClassicParallaxHeightMap = classicParallaxHeightMap,
                    ClassicEnvMapScale = sub.ClassicEnvironmentMapScale,
                    ClassicEnvMapUsesWindowReflection =
                        sub.ClassicEnvironmentMapUsesWindowReflection,
                    // Sphere maps ride the ordinary 2D upload above (GetOrUpload never
                    // cube-promotes a 2D DDS); the flag routes EnvMapState + the shader.
                    ClassicEnvMapIsSphereMap = sub.ClassicEnvironmentMapIsSphereMap,
                    AlphaState = BuildAlphaState(sub),
                    RenderState = BuildRenderState(sub, normalBinding.HasBump),
                    Specular = BuildSpecular(sub),
                    HasBump = normalBinding.HasBump,
                    AlphaRenderMode = sub.AlphaRenderMode,
                    AlphaBlend = sub.AlphaBlend,
                    AlphaTest = sub.AlphaTest,
                    AlphaTestThreshold = sub.StarfieldMaterialAlpha.IsLayer0OpacityCutout
                        ? sub.StarfieldMaterialAlpha.AlphaTestThreshold
                        : sub.AlphaTestThreshold,
                    AlphaTestFunction = sub.AlphaTestFunction,
                    SrcBlendMode = sub.SrcBlendMode,
                    DstBlendMode = sub.DstBlendMode,
                    MaterialAlpha = sub.MaterialAlpha,
                    MaterialAlphaController = sub.MaterialAlphaController,
                    PhysicsLiteSway = sub.PhysicsLiteSway,
                    RigidNodeAnimation = sub.RigidNodeAnimation,
                    DoubleSided = sub.DoubleSided,
                    IsEmissive = sub.IsEmissive,
                    IsTallGrass = sub.IsTallGrass,
                    IsLighting30 = sub.IsLighting30,
                    ClassicBasicShaderMode = sub.ClassicBasicShaderMode,
                    Lighting30Emission = new Vector4(
                        sub.Lighting30EmissionColor,
                        sub.Lighting30EmissionMultiplier),
                    SourceBlockIndex = sub.SourceBlockIndex,
                    MaterializationSourceIndex = i,
                    LocalBoundsCenter = sub.LocalBoundsCenter,
                    LocalBoundsRadius = sub.LocalBoundsRadius,
                    IsBillboard = sub.IsBillboard,
                    BillboardMode = sub.BillboardMode,
                    BillboardFrontNormal = sub.IsBillboard
                        ? NifBillboardFacing.ResolveFrontNormal(sub.Vertices, sub.Indices)
                        : Vector3.UnitY,
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
                    EngineZWriteOff = sub.EngineZWriteOff,
                    DepthTestOff = sub.DepthTestOff,
                    IsDecal = sub.IsDecal,
                    // default(Vector3) = a pre-effect-fields payload (or a caller that skipped the
                    // arg); black would tint everything out, so normalize to the no-op white.
                    EffectTint = sub.EffectTintSpecified
                        ? sub.EffectTint
                        : sub.EffectTint == default ? Vector3.One : sub.EffectTint,
                    EffectFalloffParams = sub.EffectFalloffParams,
                    HasEffectFalloff = sub.HasEffectFalloff,
                    SoftParticle = softParticle,
                    UvScrollVelocity = sub.UvScrollVelocity,
                    Skin = sub.Skin,
                    RestPoseVertices = sub.Skin is not null ? sub.Vertices : null
                });
                if (GeometryArenaDiagnostics.ValidateLevel >= 2)
                {
                    // Content stamps of the exact packed windows this submesh's views cover, so the
                    // draw-time validator can prove whether the GPU-visible bytes were overwritten.
                    var added = submeshes[^1];
                    added.DebugVertexHash = GeometryArenaDiagnostics.Fnv1a64(
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                            vertices.AsSpan(vertexStarts[i], sub.Vertices.Length)));
                    added.DebugIndexHash = GeometryArenaDiagnostics.Fnv1a64(
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                            indices.AsSpan(indexStarts[i], sub.Indices.Length)));
                }
            }
            catch (Exception ex)
            {
                materializationFailed = true;
                // If the exception landed after Add (for example in validation), remove the partial
                // owner before releasing its acquisitions. A retry must rebuild the whole mesh; a
                // silently partial resident mesh is not a successful materialization.
                if (submeshes.Count > submeshCountBefore)
                {
                    submeshes.RemoveRange(submeshCountBefore, submeshes.Count - submeshCountBefore);
                }

                if (acquiredTextures is not null)
                {
                    foreach (var acquired in acquiredTextures)
                    {
                        textureCache.Release(acquired);
                    }
                }

                Log.Warn("ReferenceMeshCache12: submesh upload failed for '{0}': {1}", modelPath, ex.Message);
            }
        }

        if (materializationFailed)
        {
            ReleaseSubmeshTextures(submeshes, textureCache);
            geometryArena.Free(geometry);
            return MeshMaterializationResult.RetryableFailure;
        }

        if (submeshes.Count == 0 && (waterPlanesLocal is null || waterPlanesLocal.Count == 0))
        {
            // No drawable submeshes and no water geometry — nothing references the arena range, so reclaim it.
            geometryArena.Free(geometry);
            return MeshMaterializationResult.RenderEmpty;
        }

        success = true;
        var cached = new CachedNifMesh12(
            // Materialized once at upload; the render thread then walks it allocation-free every frame.
            submeshes.ToArray(),
            geometry, geometryArena, deletionQueue, textureCache, MathF.Sqrt(meshLocalRadiusSq),
            aabbMin, aabbMax,
            (IReadOnlyList<NifWaterGeometry>?)waterPlanesLocal ?? Array.Empty<NifWaterGeometry>())
        {
            Animation = decoded.Animation,
            SourcePath = modelPath
        };
        foreach (var uploaded in submeshes)
        {
            uploaded.OwnerMesh = cached;
        }
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

        return MeshMaterializationResult.Success(cached);
    }

    /// <summary>
    ///     Rolls back texture acquisitions made by submeshes from a materialization attempt that
    ///     cannot be published as a complete mesh. Pinned fallback/synthetic entries are accepted;
    ///     <see cref="GpuTextureCache12.Release" /> deliberately ignores them.
    /// </summary>
    private static void ReleaseSubmeshTextures(
        IEnumerable<CachedSubmesh12> submeshes,
        GpuTextureCache12 textureCache)
    {
        foreach (var submesh in submeshes)
        {
            textureCache.Release(submesh.Diffuse);
            textureCache.Release(submesh.Normal);
            textureCache.Release(submesh.SpecularMap);
            textureCache.Release(submesh.GradientMap);
            textureCache.Release(submesh.Lighting30GlowMap);
            textureCache.Release(submesh.BgsmGlowMap);
            textureCache.Release(submesh.EnvMap);
            textureCache.Release(submesh.ClassicEnvMap);
            textureCache.Release(submesh.ClassicEnvMask);
            textureCache.Release(submesh.ClassicParallaxHeightMap);
            textureCache.Release(submesh.StarfieldOpacity);
        }
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
    ///     Builds the source-separated collision entry and stores it under the normalized model path.
    ///     Render-thread only; called both before a normal GPU upload and by collision-only recovery.
    /// </summary>
    private void StoreCollisionMesh(string cacheKey, Node node, DecodedNifMesh12 decoded)
    {
        var normalizedPath = node.DecodePath;
        var entry = BuildCollisionCacheEntry(normalizedPath, decoded);

        // Resolved-null results live in this same byte-bounded LRU. They are authoritative negative
        // cache entries, not an unbounded side set, and prevent permanent-null models from stealing
        // the walk/overlay cold-warmup budget every frame.
        _collisionLru.Set(normalizedPath, entry);
        // Preserve the exact variant that produced the plain-path entry. If collision geometry is
        // evicted while this mesh node remains resident, recovery reuses this key's bounded decoded
        // LRU/disk/source payload and republishes without a second GPU upload.
        _collisionRecovery.Publish(normalizedPath, cacheKey, entry);
    }

    /// <summary>
    ///     Builds authored Havok and visual-fallback sources without guessing a placement category.
    ///     The fallback merges solid submeshes while skipping alpha-blended (glass/effect/force-field),
    ///     emissive (glow cards), and geometry classified onto the placed-water route. Mesh-local space matches
    ///     <c>RenderableReference.WorldMatrix</c> (both use the <c>treatRootsAsIdentity</c> decode).
    /// </summary>
    private static CollisionCacheEntry BuildCollisionCacheEntry(
        string normalizedModelPath,
        DecodedNifMesh12 decoded)
    {
        return CollisionCacheEntry.Create(
            normalizedModelPath,
            decoded.CollisionProvenance,
            decoded.CollisionPositions,
            decoded.CollisionTriangles,
            BuildVisualFallback);

        CollisionMesh? BuildVisualFallback()
        {
            var positions = new List<Vector3>();
            var triangles = new List<int>();
            foreach (var sub in decoded.Submeshes)
            {
                if (sub.AlphaBlend || sub.IsEmissive) continue;
                if (string.Equals(
                        sub.DiffuseTexturePath,
                        RenderableSubmesh.WaterSurfaceTexturePath,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (sub.Vertices.Length == 0 || sub.Indices.Length == 0) continue;

                var baseIndex = positions.Count;
                foreach (var v in sub.Vertices) positions.Add(v.Position);
                foreach (var idx in sub.Indices) triangles.Add(baseIndex + idx);
            }

            return triangles.Count < 3
                ? null
                : new CollisionMesh(positions.ToArray(), triangles.ToArray());
        }
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
            alphaTestEnabled
                ? sub.StarfieldMaterialAlpha.IsLayer0OpacityCutout
                    ? sub.StarfieldMaterialAlpha.AlphaTestThreshold
                    : sub.AlphaTestThreshold
                : 0f,
            alphaTestEnabled ? sub.AlphaTestFunction : -1f,
            Math.Clamp(sub.MaterialAlpha, 0f, 8f),
            sub.AlphaRenderMode == NifAlphaRenderMode.Blend ? 1f : 0f);
    }

    private static Vector4 BuildRenderState(DecodedSubmesh12 sub, bool hasBump) =>
        new(
            sub.DoubleSided ? 1f : 0f,
            hasBump ? 1f : 0f,
            ReferenceBumpStrength,
            sub.IsEmissive ? 1f : 0f);

    // GPU specular term (1A): xyz = specular tint, w = Phong exponent (glossiness). w == 0 is the
    // shader's "no specular" sentinel, so a disabled material uploads Vector4.Zero. Glossiness is floored
    // at 1 to avoid pow(_, 0) flattening the highlight across the whole surface.
    // Emissive shapes ride their material glow tint in the otherwise-unused xyz (the decoder put it
    // there; see ReferenceMeshDecoder12) with w = 0 so the specular sentinel stays off — the PS's
    // emissive branch reads vSpecular.rgb as the glow color.
    private static Vector4 BuildSpecular(DecodedSubmesh12 sub)
    {
        if (sub.SpecularEnabled)
        {
            return new Vector4(sub.SpecularColor, MathF.Max(sub.Glossiness, 1f));
        }

        return sub.IsEmissive ? new Vector4(sub.SpecularColor, 0f) : Vector4.Zero;
    }

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

    internal enum MeshMaterializationStatus
    {
        Success,
        RenderEmpty,
        RetryableFailure
    }

    internal readonly record struct MeshMaterializationResult(
        MeshMaterializationStatus Status,
        CachedNifMesh12? Mesh)
    {
        public static MeshMaterializationResult Success(CachedNifMesh12 mesh) =>
            new(MeshMaterializationStatus.Success, mesh);

        public static MeshMaterializationResult RenderEmpty { get; } =
            new(MeshMaterializationStatus.RenderEmpty, null);

        public static MeshMaterializationResult RetryableFailure { get; } =
            new(MeshMaterializationStatus.RetryableFailure, null);
    }

    private sealed class PendingMeshPublication(string cacheKey, Node node, CachedNifMesh12 mesh)
    {
        public string CacheKey { get; } = cacheKey;
        public Node Node { get; } = node;
        public CachedNifMesh12 Mesh { get; } = mesh;
        public bool Evicted { get; set; }
        public bool Completed { get; set; }
    }

    private sealed class Node(
        CachedNifMesh12? Mesh,
        Task<DecodedNifMesh12?>? DecodeTask,
        bool ResolvedNull)
    {
        public CachedNifMesh12? Mesh { get; set; } = Mesh;
        public Task<DecodedNifMesh12?>? DecodeTask { get; set; } = DecodeTask;
        public bool ResolvedNull { get; set; } = ResolvedNull;
        /// <summary>
        ///     A collision-LRU miss requested an exact-variant disk/source refresh. This may coexist
        ///     with a resident GPU mesh or render-null node; completion republishes collision only.
        /// </summary>
        public bool CollisionRecoveryRequested { get; set; }
        public bool DecodeQueued { get; set; }
        public bool DecodedCacheAvailable { get; set; }
        public bool DecodedCacheMissRecorded { get; set; }
        public ulong MaterializationRetryDemandGeneration { get; set; }
        public PendingMeshPublication? PendingPublication { get; set; }

        /// <summary>Plain normalized archive path (no '#variant' suffix), used for archive decode and
        /// on-disk metadata. Collision also keys on this today, but visual-fallback material admission
        /// can be variant-dependent; see the active collision backlog.</summary>
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
