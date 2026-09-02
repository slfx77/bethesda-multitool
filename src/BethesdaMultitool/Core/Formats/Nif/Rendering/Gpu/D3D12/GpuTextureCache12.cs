using System.Collections.Concurrent;
using System.Diagnostics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Orchestration;
using BethesdaMultitool.Core.Resources;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     D3D12 texture cache for terrain and reference rendering. Texture misses resolve to
///     stable bindless entries immediately, then the real DEFAULT-heap upload streams on a
///     dedicated copy queue off the render thread.
///     <para>
///         Pipeline: a cold <see cref="GetOrUpload" /> returns a fallback placeholder and queues
///         a background DDS/DDX resolve (<see cref="BoundedResolveQueue{TKey,TResult}" />). When
///         the payload is ready it is handed to a dedicated uploader thread
///         (<see cref="DedicatedWorkerThread" />) that creates the GPU texture, fills staging, and
///         records the mip copies on a copy queue (<see cref="GpuUploadQueue12" />) — none of that
///         touches the per-frame direct command list. Once the copy fence completes, the render
///         thread transitions the texture to a shader-read state and points the placeholder's
///         persistent SRV slot at it, so callers upgrade placeholder → textured transparently
///         (the bindless index is stable and draw records never rebuild).
///     </para>
///     <para>
///         This removes the staging memcpy + <c>CopyTextureRegion</c> + barrier (previously the
///         dominant streaming-hitch source — a single multi-MB texture could spike a frame) from
///         the render thread entirely.
///     </para>
/// </summary>
internal sealed unsafe class GpuTextureCache12 : ITrackableResource, IDisposable
{
    // Per-frame caps now bound how fast resolved textures are HANDED to the uploader (copy-queue /
    // staging-VRAM pressure), not render-thread time — the upload itself is async. Kept generous so
    // the placeholder→textured window stays short.
    private const int DefaultMaxUploadsPerFrame = 16;
    private const long DefaultMaxUploadBytesPerFrame = 48L * 1024L * 1024L;

    private const int UnthrottledMaxUploadsPerDispatch = 1024;

    private const long UnthrottledMaxUploadBytesPerDispatch = 1024L * 1024L * 1024L;

    // Scales with cores (GC-guarded: half the logical processors, clamped to [2, 12]) — texture
    // resolve (BSA read + DDX→DDS transcode) is the documented streaming-hitch cost and is
    // embarrassingly parallel + off the render thread, so more workers directly multiply throughput.
    // Half-cores (not full) leaves headroom for mesh decode + render + GC; the 8→12 ceiling raise
    // matches the decode-worker raise (high-core machines were idling half their cores cold-loading
    // FO4's texture-heavy Commonwealth). Env override for profiling.
    private static readonly int DefaultMaxConcurrentTextureResolves =
        BethesdaMultitool.Core.Orchestration.ConcurrencyPolicy
            .Fixed(BethesdaMultitool.Core.Orchestration.CpuBudget.Interactive()
                .Claim(BethesdaMultitool.Core.Orchestration.CpuWorkload.TextureResolve))
            .WithEnvironmentOverride(EnvironmentVariables.Viewer.TextureResolveConcurrency, 1, 12)
            .Resolve();

    // Release each decoded texture payload's CPU mip bytes from the resolver cache once it's on the
    // GPU (default on) — they're dead weight afterward and otherwise accumulate to multi-GB managed
    // heap → GC stalls. FALLOUT_VIEWER_RETAIN_TEXTURE_PAYLOADS=1 keeps the old retain-forever
    // behavior (for A/B measurement / fallback).
    private static readonly bool ReleaseTexturePayloadsAfterUpload =
        !EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.RetainTexturePayloads);

    // Alias accounting is opt-in with the JSONL profiler. Keep it off the normal hot path entirely:
    // when tracing is disabled, nodes carry no legacy-key set and GetOrUpload performs no extra
    // path normalization or allocations.
    private readonly bool _aliasTraceEnabled;
    private readonly Dictionary<string, TextureUploadNode> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Logger Log = Logger.Instance;

    /// <summary>How many unresolvable textures get named individually before rate-limiting.</summary>
    private const int MaxNamedResolveFailures = 8;

    // Resolution runs on background threads, so both of these are touched off the render thread.
    private readonly List<string> _namedResolveFailures = new(MaxNamedResolveFailures);
    private readonly Lock _resolveFailureGate = new();
    private int _resolveFailures;

    private readonly ConcurrentQueue<CompletedUpload> _completedUploads = new();

    // Async copy-queue upload machinery (owned per-cache, mirroring _resolveQueue ownership).
    private readonly GpuUploadQueue12 _copyUploadQueue;
    private readonly GpuDeletionQueue12? _deletionQueue;

    private readonly GpuDevice12 _gpu;
    private readonly GpuDescriptorHeapAllocator12 _heap;
    private readonly List<ID3D12Resource> _ownedTextures = new();
    private readonly Queue<TextureUploadNode> _pendingDispatch = new();
    private readonly List<CompletedUpload> _pendingPromote = new();
    private readonly GpuCommandRecorder12 _recorder;
    private readonly BoundedResolveQueue<string, GpuTexturePayload> _resolveQueue;

    private readonly NifGpuTextureResolver? _resolver;

    // Frame-independent fallback/placeholder texture creation (1×1 solids + cold-miss placeholders).
    // Deliberately kept OUT of the async copy-queue upload pipeline — it only needs the device + heap.
    private readonly GpuSolidTextureFactory12 _solidTextureFactory;
    private readonly Dictionary<string, Entry> _syntheticEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly DedicatedWorkerThread _uploadDispatcher;
    private bool _disposed;
    private long _evictions;

    private Entry? _flatNormal;

    // Time-based streaming pace for this frame (StreamingFrameBudgetScaler; set by ResetFrameStats).
    private double _frameBudgetScale = 1.0;
    private long _hits;
    private long _misses;

    // Diagnostics counters — plain fields written only by the render thread (tracking-only
    // conformance; this cache is refcount-pinned and never trimmed). Snapshot readers tolerate
    // slightly stale values per the ITrackableResource threading contract.
    private ResourceRegistration? _registration;
    // Fallback singletons + synthesized frames: created once, never released before Dispose, and
    // outside the refcounted add/release pair that maintains _residentBytes. Counted separately so
    // the resident total includes them without touching the release-path symmetry.
    private long _pinnedBytes;
    private long _residentBytes;
    private string _traceCacheTag = "unregistered";
    private bool _traceSummaryEmitted;
    private Entry? _waterSurface;
    private Entry? _whitePixel;

    public GpuTextureCache12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuDescriptorHeapAllocator12 heap,
        NifGpuTextureResolver? resolver,
        GpuDeletionQueue12? deletionQueue = null)
    {
        _gpu = gpu;
        _recorder = recorder;
        _heap = heap;
        _resolver = resolver;
        _deletionQueue = deletionQueue;
        _solidTextureFactory = new GpuSolidTextureFactory12(gpu, heap);
        _aliasTraceEnabled = RendererProfilerTrace.IsEnabled;
        // Resolve DDS/DDX payloads off the render thread. Keep the default conservative because
        // DDX conversion and DDS parsing allocate enough to cause visible UI/render pauses when
        // too many complete in the same window.
        _resolveQueue = new BoundedResolveQueue<string, GpuTexturePayload>(
            "TextureResolveQueue",
            DefaultMaxConcurrentTextureResolves,
            ResolvePayload,
            StringComparer.OrdinalIgnoreCase);
        _copyUploadQueue = new GpuUploadQueue12(gpu);
        _uploadDispatcher = new DedicatedWorkerThread("GpuTextureUploader");
    }

    /// <summary>1x1 opaque white texture used as the fallback diffuse.</summary>
    public Entry WhitePixel => _whitePixel ??= CreatePinnedSolid(255, 255, 255, 255);

    /// <summary>1x1 flat normal map used as the fallback normal.</summary>
    public Entry FlatNormal => _flatNormal ??= CreatePinnedSolid(128, 128, 255, 255);

    /// <summary>
    ///     1×1 translucent water-blue tile used as the diffuse for placed water-shader geometry
    ///     (WaterShaderProperty NIFs carry no diffuse — the engine renders them via its water system,
    ///     which we don't reproduce on placed geometry). Renders the surface as blue water instead of
    ///     the opaque-white fallback. RGB ≈ (0.15, 0.32, 0.42); combined with forced alpha-blend it
    ///     reads as a translucent water plane.
    /// </summary>
    public Entry WaterSurface => _waterSurface ??= CreatePinnedSolid(38, 82, 107, 255);

    /// <summary>
    ///     Creates a pinned fallback singleton AND counts it: pinned entries never pass through the
    ///     refcounted add/release pair, so before this they were invisible to the resident total.
    /// </summary>
    private Entry CreatePinnedSolid(byte r, byte g, byte b, byte a)
    {
        var entry = _solidTextureFactory.CreateSolid(r, g, b, a);
        _pinnedBytes += entry.ByteSize;
        return entry;
    }

    public int MaxUploadsPerFrame { get; init; } = DefaultMaxUploadsPerFrame;

    /// <summary>
    ///     Mirrors <c>ReferenceMeshCache12.StreamingThrottled</c> for the DISPATCH step. The live
    ///     60fps loop keeps the default per-frame admission caps (they bound copy-queue/staging-VRAM
    ///     pressure). On-demand overlay renders (2D map top-down, 3D export) set this false so a
    ///     single pass hands the WHOLE resolved backlog to the uploader instead of 16×scale items:
    ///     measured on the 2D map, the dispatch cap — not resolve or the async copy itself — was the
    ///     convergence bottleneck (≤64 textures promoted per whole-worldspace re-render, so a
    ///     cold-start needed dozens of full re-cull + re-render + readback passes). The burst stays
    ///     BOUNDED (<see cref="UnthrottledMaxUploadsPerDispatch" />/<see cref="UnthrottledMaxUploadBytesPerDispatch" />)
    ///     because every admitted item allocates fence-retired staging memory.
    /// </summary>
    public bool StreamingThrottled { get; set; } = true;

    public long MaxUploadBytesPerFrame { get; init; } = DefaultMaxUploadBytesPerFrame;

    public int FrameCompressedUploads { get; private set; }

    public int FrameRgbaFallbackUploads { get; private set; }

    public int FrameQueuedUploads { get; private set; }

    public long FrameUploadBytes { get; private set; }

    /// <summary>Texture paths newly queued for background payload resolution this frame.</summary>
    public int FrameQueuedResolves { get; private set; }

    /// <summary>Background payload-resolution tasks running right now.</summary>
    public int FrameActiveResolves => _resolveQueue.ActiveCount;

    public int PendingUploadCount { get; private set; }

    public int PendingResolveCount => _resolveQueue.QueuedCount;

    /// <summary>Upload work items handed to the uploader thread but not yet processed.</summary>
    public int PendingUploadDispatch => _uploadDispatcher.PendingCount;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Snapshot while every queue/stat source is still readable and before any cache entries are
        // cleared. The trace writer outlives renderer disposal in the profiler host.
        EmitTraceSummary();
        // Unregister first so the retired-stats record captures the cache as it was at teardown.
        _registration?.Dispose();
        _registration = null;
        // Stop the uploader thread first so no upload completion / copy submission races teardown.
        _uploadDispatcher.Dispose();
        // Let in-flight background resolutions finish before the host disposes the resolver they read
        // from (BSA archives). Mirrors ReferenceMeshCache12's decode-task drain on dispose; the host
        // disposes this cache before the resolver, so this keeps that ordering safe.
        _resolveQueue.WaitForDrain();
        // Flush + dispose the copy queue: drains outstanding copies and retires their staging. After
        // this every uploaded DEFAULT texture is GPU-idle and safe to release.
        _copyUploadQueue.Dispose();

        // Release textures that finished uploading but were never promoted (cache torn down first).
        while (_completedUploads.TryDequeue(out var leftover))
        {
            if (leftover.Texture is not null) DisposeResource(leftover.Texture);
        }

        foreach (var pending in _pendingPromote)
        {
            if (pending.Texture is not null) DisposeResource(pending.Texture);
        }

        _pendingPromote.Clear();

        foreach (var t in _ownedTextures) DisposeResource(t);
        // Every cache entry owns a distinct persistent bindless slot even while its texture is only
        // a shared fallback placeholder. Teardown used to release the textures but abandon all of
        // those descriptor allocations (including the pinned/synthetic entries), so repeatedly
        // opening standalone viewers steadily exhausted the host heap. Retire each still-owned slot
        // through the same frame-safe path as Release(); a set makes the teardown defensive against
        // future aliases without risking a double return to the allocator's free-list.
        var retiredPersistentSlots = new HashSet<uint>();
        foreach (var node in _cache.Values)
        {
            RetirePersistentSlot(node.Entry.BindlessIndex, retiredPersistentSlots);
        }

        if (_whitePixel is Entry wp)
        {
            DisposeResource(wp.Texture);
            RetirePersistentSlot(wp.BindlessIndex, retiredPersistentSlots);
        }
        if (_flatNormal is Entry fn)
        {
            DisposeResource(fn.Texture);
            RetirePersistentSlot(fn.BindlessIndex, retiredPersistentSlots);
        }
        if (_waterSurface is Entry ws)
        {
            DisposeResource(ws.Texture);
            RetirePersistentSlot(ws.BindlessIndex, retiredPersistentSlots);
        }
        foreach (var synthetic in _syntheticEntries.Values)
        {
            DisposeResource(synthetic.Texture);
            RetirePersistentSlot(synthetic.BindlessIndex, retiredPersistentSlots);
        }
        _syntheticEntries.Clear();
        _ownedTextures.Clear();
        _pendingDispatch.Clear();
        _cache.Clear();
        PendingUploadCount = 0;
        // _heap is owned by the host (WorldView3DControl); do not dispose here.
    }

    public string ResourceName => nameof(GpuTextureCache12);

    public ResourceCategory Category => ResourceCategory.GpuResident;

    /// <summary>
    ///     Textures are DEFAULT-heap, so these bytes are genuinely device-local VRAM
    ///     (<see cref="GpuMemorySegment.Local" />) — unlike the geometry arena's UPLOAD blocks.
    ///     <para>
    ///         <see cref="_residentBytes" /> carries the D3D12 ALLOCATION footprint of streamed
    ///         textures (stamped at upload from <c>GetResourceAllocationInfo</c>, so placement
    ///         rounding is included), and <see cref="_pinnedBytes" /> the never-released fallback
    ///         singletons + synthesized frames. Their sum is what a VRAM budget can honestly be
    ///         compared against.
    ///     </para>
    /// </summary>
    public ResourceStats GetStats()
    {
        return new ResourceStats
        {
            EstimatedBytes = Volatile.Read(ref _residentBytes) + Volatile.Read(ref _pinnedBytes),
            EntryCount = _cache.Count,
            Hits = Volatile.Read(ref _hits),
            Misses = Volatile.Read(ref _misses),
            Evictions = Volatile.Read(ref _evictions),
            QueueDepth = _uploadDispatcher.PendingCount,
            InFlight = _resolveQueue.ActiveCount,
            Segment = GpuMemorySegment.Local
        };
    }

    /// <summary>
    ///     Registers the cache with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the cache for fluent construction.
    /// </summary>
    public GpuTextureCache12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _traceCacheTag = string.IsNullOrWhiteSpace(instanceTag) ? "default" : instanceTag;
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    /// <summary>
    ///     Returns (creating + uploading on first use) a pinned synthesized RGBA8 texture keyed by
    ///     <paramref name="key" /> — e.g. the 32 Oblivion water-surface animation frames the engine
    ///     generates at runtime (retail ships no water00-31.dds); those upload with a full box-filter
    ///     mip chain so minification filters instead of shimmering. Entries are pinned like the
    ///     fallback singletons: never refcounted or evicted, released at cache disposal.
    /// </summary>
    public Entry GetOrCreateSynthetic(string key, int width, int height, byte[] rgba)
    {
        if (_syntheticEntries.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var entry = _solidTextureFactory.CreateFromRgba(width, height, rgba, true);
        _syntheticEntries[key] = entry;
        _pinnedBytes += entry.ByteSize;
        return entry;
    }

    /// <summary>
    ///     See <see cref="NifGpuTextureResolver.IsStarfieldNoDrawMaterial" /> — true when the shape
    ///     referencing <paramref name="materialPath" /> should be SKIPPED (no albedo in any form)
    ///     rather than drawn with <see cref="WhitePixel" />. False when this cache has no resolver.
    /// </summary>
    public bool IsStarfieldNoDrawMaterial(string materialPath)
    {
        return _resolver?.IsStarfieldNoDrawMaterial(materialPath) == true;
    }

    public void ResetFrameStats(double frameBudgetScale = 1.0)
    {
        FrameCompressedUploads = 0;
        FrameRgbaFallbackUploads = 0;
        FrameQueuedUploads = 0;
        FrameUploadBytes = 0;
        FrameQueuedResolves = 0;
        // Time-based pace (see StreamingFrameBudgetScaler): slow frames earn proportionally larger
        // dispatch allowances so texture streaming throughput doesn't collapse with the frame rate.
        _frameBudgetScale = frameBudgetScale;
        PromoteCompletedUploads();
        DrainResolvedPayloads();
        DispatchQueuedUploads();
        _resolveQueue.Pump();
    }

    /// <summary>
    ///     Returns a stable cached entry for <paramref name="path" />. On a cold miss the
    ///     entry initially points at a fallback texture; the streamed upload later overwrites
    ///     the same persistent descriptor slot so existing terrain/reference caches update
    ///     without rebuilding their draw records.
    /// </summary>
    public Entry GetOrUpload(string path, bool isNormalMap = false)
    {
        var cacheKey = NormalizeCacheKey(path, isNormalMap);
        if (cacheKey.Length == 0)
        {
            return isNormalMap ? FlatNormal : WhitePixel;
        }

        if (_cache.TryGetValue(cacheKey, out var node))
        {
            node.AliasTrace?.Observe(path);
            _hits++;
            // Resolved (payload present) but not yet resident → make sure it is queued for dispatch.
            // Still resolving (payload null) → just return the placeholder; the background resolution
            // queues the dispatch itself once it completes.
            if (node.Payload is not null)
            {
                EnqueueForDispatch(node, false);
            }

            node.Entry.RefCount++; // acquire — balanced by Release() when the owning mesh is evicted.
            return node.Entry;
        }

        _misses++;
        var fallback = isNormalMap ? FlatNormal : WhitePixel;
        if (_resolver is null)
        {
            return fallback; // pinned fallback — not reference-counted.
        }

        // Cold miss: hand back a fallback placeholder immediately and resolve the real DDS/DDX payload
        // on a background thread. The streamed GPU upload later overwrites this Entry's persistent
        // descriptor slot in place, so callers that cached the Entry upgrade from placeholder →
        // textured transparently (BindlessIndex is stable).
        var entry = _solidTextureFactory.CreatePlaceholder(fallback, cacheKey);
        entry.RefCount = 1; // acquire (first reference).
        node = new TextureUploadNode(
            cacheKey,
            entry,
            _aliasTraceEnabled ? new LegacyAliasTrace(path) : null);
        _cache[cacheKey] = node;
        if (_resolveQueue.Enqueue(cacheKey))
        {
            FrameQueuedResolves++;
        }

        _resolveQueue.Pump();
        return entry;
    }

    /// <summary>
    ///     Canonicalizes the GPU-residency key exactly like the backing resolver. Retail assets mix
    ///     archive-relative paths (for example <c>SetDressing\Foo_d.dds</c>) with explicit
    ///     <c>textures\</c>-rooted paths for the same DDS. Keeping the raw spelling here created two
    ///     descriptors, uploads, and resident textures even though the resolver normalized both to
    ///     one archive entry.
    /// </summary>
    internal static string NormalizeCacheKey(string path, bool isNormalMap = false)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        // Starfield's .mat path names several CDB slots. A raw path alone cannot safely key both
        // diffuse and normal: whichever role resolves first would alias the other. Qualify only the
        // normal request; ordinary DDS paths and all existing cache keys remain unchanged.
        var normalizedPath = NifGpuTextureResolver.NormalizeKey(path);
        return isNormalMap && MaterialTexturePathResolver.IsStarfieldMaterialPath(normalizedPath)
            ? MaterialTexturePathResolver.BuildStarfieldNormalMapRequest(normalizedPath)
            : normalizedPath;
    }

    /// <summary>
    ///     Reconstructs the exact key used by the GPU cache before canonicalization. The old
    ///     dictionary compared these strings with <see cref="StringComparer.OrdinalIgnoreCase" />,
    ///     so slash, case, and surrounding-whitespace variants were already one entry and must not
    ///     be reported as avoided aliases.
    /// </summary>
    internal static string NormalizeLegacyCacheKeyForTrace(string path)
    {
        return path.Replace('/', '\\').Trim();
    }

    /// <summary>
    ///     Drops one reference acquired via <see cref="GetOrUpload" /> (render thread only). When the
    ///     last reference is released the entry is evicted: removed from the cache, its GPU texture
    ///     disposed, and its bindless slot returned for reuse — both deferred by frames-in-flight via
    ///     the deletion queue so the GPU isn't still sampling them. Pinned fallback singletons (null
    ///     <see cref="Entry.CacheKey" />) are ignored. This is what bounds the cache: without it the
    ///     persistent descriptor heap exhausts after sustained streaming.
    /// </summary>
    public void Release(Entry? entry)
    {
        if (entry?.CacheKey is not { } key)
        {
            return; // null entry or pinned fallback.
        }

        if (entry.RefCount <= 0)
        {
            return; // already released (defensive against double-release).
        }

        if (--entry.RefCount > 0)
        {
            return; // still referenced by another resident mesh.
        }

        // Last reference dropped → evict.
        if (!_cache.TryGetValue(key, out var node) || !ReferenceEquals(node.Entry, entry))
        {
            return; // node already gone or replaced; nothing to reclaim.
        }

        _cache.Remove(key);

        // If a resolve/upload is still in flight for this node, the downstream paths already cope with
        // the cache miss (DrainResolvedPayloads skips; PromoteCompletedUploads orphan-disposes the
        // uploaded texture). Keep the pending-upload counter consistent for those in-flight nodes,
        // since the orphan path won't decrement it.
        if (node.Payload is not null && !entry.IsResident && !node.Failed)
        {
            PendingUploadCount--;
        }

        // Dispose this entry's OWN uploaded texture. Non-resident placeholders (and failed nodes)
        // still point at the shared fallback singleton, which must never be freed.
        if (entry.IsResident && _ownedTextures.Remove(entry.Texture))
        {
            _residentBytes -= entry.ByteSize;
            DisposeResource(entry.Texture);
        }

        _evictions++;

        // Return the bindless slot — deferred by frames-in-flight so a reused slot can't alias a
        // texture the GPU is still sampling via an in-flight frame's draw records.
        RetirePersistentSlot(entry.BindlessIndex);
    }

    /// <summary>
    ///     Returns one cache-owned persistent descriptor after any in-flight draw that may still
    ///     index it has drained. <paramref name="retiredSlots" /> is used by bulk teardown to make
    ///     ownership release idempotent across defensive aliases; ordinary eviction owns one slot
    ///     and therefore does not need a set.
    /// </summary>
    private void RetirePersistentSlot(uint slot, HashSet<uint>? retiredSlots = null)
    {
        if (retiredSlots is not null && !retiredSlots.Add(slot))
        {
            return;
        }

        if (_deletionQueue is not null)
        {
            _deletionQueue.EnqueueDispose(new PersistentSlotReturn(_heap, slot));
        }
        else
        {
            _heap.FreePersistent(slot);
        }
    }

    /// <summary>
    ///     Render-thread step: enqueue a resolved-but-not-resident node onto the dispatch queue so
    ///     <see cref="DispatchQueuedUploads" /> can hand it to the uploader thread. Idempotent — a
    ///     node already queued, dispatched, resident or failed is left alone.
    ///     <paramref
    ///         name="newlyPending" />
    ///     increments the pending counter exactly once, at first resolution.
    /// </summary>
    private void EnqueueForDispatch(TextureUploadNode node, bool newlyPending)
    {
        if (node.Payload is null || node.Entry.IsResident || node.Failed || node.InDispatchQueue || node.Dispatched)
        {
            return;
        }

        if (newlyPending)
        {
            PendingUploadCount++;
        }

        node.InDispatchQueue = true;
        _pendingDispatch.Enqueue(node);
    }

    /// <summary>
    ///     Render-thread step: hand resolved nodes to the uploader thread, up to the per-frame
    ///     count + byte caps (which bound copy-queue / staging-VRAM pressure, not frame time).
    /// </summary>
    private void DispatchQueuedUploads()
    {
        var uploadLimit = StreamingThrottled
            ? Math.Max(1,
                StreamingFrameBudgetScaler.ScaleCount(MaxUploadsPerFrame, _frameBudgetScale))
            : UnthrottledMaxUploadsPerDispatch;
        // Count stays in the while condition (count exhaustion leaves the next node at the queue
        // FRONT, exactly as before); CanStart gates bytes with the first-item-always rule.
        var budget = new FrameBudget(
            uploadLimit,
            StreamingThrottled
                ? StreamingFrameBudgetScaler.ScaleBytes(MaxUploadBytesPerFrame, _frameBudgetScale)
                : UnthrottledMaxUploadBytesPerDispatch);

        while (_pendingDispatch.Count > 0 && budget.ItemsUsed < uploadLimit)
        {
            var node = _pendingDispatch.Dequeue();
            node.InDispatchQueue = false;

            var payload = node.Payload;
            if (payload is null || node.Entry.IsResident || node.Failed)
            {
                continue;
            }

            var byteSize = Math.Max(1L, payload.ByteSize);
            if (!budget.CanStart(byteSize))
            {
                // Over the per-frame admission budget → requeue and stop; retry next frame.
                node.InDispatchQueue = true;
                _pendingDispatch.Enqueue(node);
                break;
            }

            var cacheKey = node.Path;
            node.Dispatched = true;
            if (_uploadDispatcher.TryEnqueue(() => RunUploadOnUploaderThread(cacheKey, payload)))
            {
                budget.Record(byteSize);
                FrameQueuedUploads++;
            }
            else
            {
                // Uploader queue full → requeue for a later frame (no render-thread blocking).
                node.Dispatched = false;
                node.InDispatchQueue = true;
                _pendingDispatch.Enqueue(node);
                break;
            }
        }
    }

    /// <summary>
    ///     Render-thread step: convert background resolution results into dispatch-queue entries.
    ///     Invalid or missing payloads leave the node permanently on its fallback placeholder.
    /// </summary>
    private void DrainResolvedPayloads()
    {
        while (_resolveQueue.TryDequeueCompleted(out var cacheKey, out var payload))
        {
            if (!_cache.TryGetValue(cacheKey, out var node))
            {
                continue;
            }

            if (payload is null || payload.Width <= 0 || payload.Height <= 0 || payload.MipCount <= 0)
            {
                node.Failed = true;
                node.Entry.MarkUnavailable();
                continue;
            }

            node.Payload = payload;
            EnqueueForDispatch(node, true);
        }
    }

    /// <summary>
    ///     Render-thread step: complete uploads whose copy fence has passed. The copy left the
    ///     texture in COMMON (decayed from COPY_DEST); transition it to a shader-read state on the
    ///     direct queue, write the real SRV into the placeholder's persistent slot, and mark it
    ///     resident. Recorded into the open frame command list before any draws sample it.
    /// </summary>
    private void PromoteCompletedUploads()
    {
        // Move newly-finished uploads off the cross-thread queue; settle failures immediately.
        while (_completedUploads.TryDequeue(out var completed))
        {
            if (completed.Texture is null)
            {
                if (_cache.TryGetValue(completed.CacheKey, out var failedNode) &&
                    !failedNode.Entry.IsResident && !failedNode.Failed)
                {
                    failedNode.Failed = true;
                    failedNode.Entry.MarkUnavailable();
                    failedNode.Dispatched = false;
                    failedNode.Payload = null; // free the decoded bytes; this node won't upload again.
                    PendingUploadCount--;
                }

                continue;
            }

            _pendingPromote.Add(completed);
        }

        if (_pendingPromote.Count == 0)
        {
            return;
        }

        var completedFence = _copyUploadQueue.LastCompletedValue;
        var cmd = _recorder.CommandList;
        var keep = 0;
        for (var i = 0; i < _pendingPromote.Count; i++)
        {
            var c = _pendingPromote[i];
            if (c.CopyFenceValue > completedFence)
            {
                _pendingPromote[keep++] = c; // copy not finished yet — re-check next frame.
                continue;
            }

            if (!_cache.TryGetValue(c.CacheKey, out var node) || node.Entry.IsResident || node.Failed)
            {
                // Node evicted/cleared or already satisfied → the uploaded texture is orphaned.
                DisposeResource(c.Texture!);
                continue;
            }

            // Bindless material textures are normally sampled by pixel shaders, but the recovered
            // FO3/FNV water-noise construction also consumes its NNAM source in a compute shader.
            // Keep resident texture resources legal for both shader classes; descriptors and all
            // existing pixel-only consumers are unchanged.
            cmd.ResourceBarrierTransition(
                c.Texture!,
                ResourceStates.Common,
                ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
            _gpu.Device.CreateShaderResourceView(c.Texture, c.SrvDesc, node.Entry.PersistentSrv);
            // The entry carries the ALLOCATION footprint, so the symmetric release at Release()
            // (_residentBytes -= entry.ByteSize) automatically balances what is added here.
            // FrameUploadBytes below deliberately stays on the payload byte count — it feeds the
            // per-frame streaming pacer, and inflating it by the footprint padding would silently
            // throttle streaming in every game.
            node.Entry.ReplaceTexture(c.Texture!, c.SrvDesc, c.Format, c.NormalDecodeMode, c.AllocationBytes);
            _residentBytes += c.AllocationBytes;
            node.Dispatched = false;
            // Drop the decoded payload's mip bytes now that the texture is resident on the GPU.
            // The node is the second strong ref (alongside the resolver cache, released on the
            // uploader thread); clearing it here is what actually frees the bytes — otherwise every
            // resident texture's CPU bytes are retained for the session (multi-GB heap / GC stalls).
            node.Payload = null;
            _ownedTextures.Add(c.Texture!);
            PendingUploadCount--;

            if (c.IsCompressed)
            {
                FrameCompressedUploads++;
            }
            else
            {
                FrameRgbaFallbackUploads++;
            }

            FrameUploadBytes += c.ByteSize;
        }

        _pendingPromote.RemoveRange(keep, _pendingPromote.Count - keep);
    }

    /// <summary>
    ///     Background-thread payload resolution (BSA read + DDX→DDS + BCn decode). Runs through the
    ///     resolver's own thread-safe cache; never touches render-thread state.
    /// </summary>
    /// <summary>
    ///     Textures this cache looked for and could not find in any source, for the session.
    ///     Non-zero is always a real asset problem: a wrong path root, a missing archive, or a
    ///     load-order gap.
    /// </summary>
    internal int ResolveFailureCount => Volatile.Read(ref _resolveFailures);

    /// <summary>
    ///     Records a texture that resolved to nothing, and says so in the log.
    ///     <para>
    ///         This existed only as a profiler trace field before, so a missing texture was SILENT
    ///         unless JSONL tracing happened to be on. Fallout 76's entire landscape resolved to
    ///         nothing for weeks behind that silence — every LTEX material was looked up under the
    ///         wrong root, and the only symptom was terrain that looked plausibly default.
    ///     </para>
    ///     <para>
    ///         Named individually for the first few, then rate-limited to widening milestones, so a
    ///         systematically broken worldspace announces itself immediately without a flood drowning
    ///         the log (a bad root fails EVERY texture, not one).
    ///     </para>
    /// </summary>
    private void NoteResolveFailure(string cacheKey)
    {
        var count = Interlocked.Increment(ref _resolveFailures);

        var name = false;
        lock (_resolveFailureGate)
        {
            if (_namedResolveFailures.Count < MaxNamedResolveFailures)
            {
                _namedResolveFailures.Add(cacheKey);
                name = true;
            }
        }

        if (name)
        {
            Log.Warn("GpuTextureCache12[{0}]: texture not found in any source — '{1}'.",
                _traceCacheTag, cacheKey);
            return;
        }

        // 25, 100, 1000, 10000, … — enough to show a systemic failure escalating, few enough that a
        // worldspace missing thousands of textures adds a handful of lines rather than thousands.
        if (count == 25 || count == 100 || count == 1_000 || (count % 10_000) == 0)
        {
            Log.Warn(
                "GpuTextureCache12[{0}]: {1:N0} textures unresolved so far. First few: {2}.",
                _traceCacheTag, count, string.Join(", ", _namedResolveFailures));
        }
    }

    private GpuTexturePayload? ResolvePayload(string cacheKey)
    {
        if (_resolver is null)
        {
            return null;
        }

        var started = RendererProfilerTrace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        var payload = _resolver.GetTexture(cacheKey);
        if (payload is null && !_resolver.IsUnauthoredStarfieldNormalMap(cacheKey))
        {
            NoteResolveFailure(cacheKey);
        }

        if (started != 0)
        {
            RendererProfilerTrace.Event("resource-event", new Dictionary<string, object?>
            {
                ["resource"] = "texture",
                ["phase"] = "resolve",
                ["cacheTag"] = _traceCacheTag,
                ["path"] = cacheKey,
                ["found"] = payload is not null,
                ["compressed"] = payload?.IsCompressed,
                ["bytes"] = payload?.ByteSize ?? 0,
                ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }

        return payload;
    }

    /// <summary>
    ///     Uploader-thread step: create the DEFAULT texture (in COMMON), fill staging, and submit
    ///     the mip copies on the copy queue. Publishes a <see cref="CompletedUpload" /> (with the
    ///     copy fence value) for the render thread to promote; on failure publishes a null-texture
    ///     completion so the node settles onto its fallback. Never touches render-thread state.
    /// </summary>
    private void RunUploadOnUploaderThread(string cacheKey, GpuTexturePayload payload)
    {
        var started = RendererProfilerTrace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        ID3D12Resource? texture = null;
        ID3D12Resource? staging = null;
        try
        {
            var width = (uint)payload.Width;
            var height = (uint)payload.Height;
            var mipCount = (ushort)payload.MipCount;
            var arraySize = (ushort)Math.Max(payload.ArraySize, 1);
            // Total subresources = mips × array slices; payload.MipLevels is already laid out in
            // D3D12 subresource order (arraySlice-major — face 0's chain, then face 1's, …).
            var subresourceCount = mipCount * arraySize;
            if (width == 0 || height == 0 || mipCount == 0 ||
                subresourceCount != payload.MipLevels.Count)
            {
                throw new InvalidOperationException("Degenerate texture payload.");
            }

            var dxgiFormat = GpuTextureFormatHelpers12.ToDxgiFormat(payload.Format);
            var desc = ResourceDescription.Texture2D(
                dxgiFormat, width, height,
                arraySize, mipCount);
            // What the resource ACTUALLY charges (64 KiB placement rounding + internal layout), as
            // opposed to the decoded payload's byte count — the two diverge by the padding, and
            // _residentBytes exists to be compared against a real VRAM budget.
            var allocationBytes = (long)_gpu.Device.GetResourceAllocationInfo(0, desc).SizeInBytes;

            // COMMON initial state: a copy queue implicitly promotes COMMON→COPY_DEST for the copy
            // and the resource decays back to COMMON on copy-list completion. The render thread then
            // transitions COMMON→PIXEL_SHADER_RESOURCE after the copy fence passes (copy queues can't
            // transition to shader-read states themselves).
            texture = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties,
                HeapFlags.None,
                desc,
                ResourceStates.Common);

            var footprints = new PlacedSubresourceFootPrint[subresourceCount];
            var numRows = new uint[subresourceCount];
            var rowSize = new ulong[subresourceCount];
            _gpu.Device.GetCopyableFootprints(
                desc, 0, (uint)subresourceCount, 0,
                footprints, numRows, rowSize, out var totalBytes);

            staging = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer(totalBytes),
                ResourceStates.GenericRead);

            void* cpuPtr = null;
            staging.Map(0, &cpuPtr).CheckError();
            try
            {
                for (var mip = 0; mip < subresourceCount; mip++)
                {
                    var level = payload.MipLevels[mip];
                    if (level.Bytes.Length == 0 || level.Width == 0 || level.Height == 0)
                    {
                        continue;
                    }

                    var srcRowPitch = GpuTextureFormatHelpers12.GetSourceRowPitch(payload, level);
                    var sourceRows = GpuTextureFormatHelpers12.GetSourceRowCount(payload, level);
                    var dstRowPitch = footprints[mip].Footprint.RowPitch;
                    var dstBase = (byte*)cpuPtr + (long)footprints[mip].Offset;
                    fixed (byte* src = level.Bytes)
                    {
                        for (uint row = 0; row < sourceRows; row++)
                        {
                            var copyBytes = Math.Min(srcRowPitch, dstRowPitch);
                            Buffer.MemoryCopy(
                                src + row * srcRowPitch,
                                dstBase + row * dstRowPitch,
                                dstRowPitch,
                                copyBytes);
                        }
                    }
                }
            }
            finally
            {
                staging.Unmap(0);
            }

            var textureResource = texture;
            var stagingResource = staging;
            // record() runs synchronously inside Submit on this (uploader) thread; the staging
            // buffer is retired once the returned copy fence completes.
            var copyFenceValue = _copyUploadQueue.Submit(
                list =>
                {
                    for (uint sub = 0; sub < subresourceCount; sub++)
                    {
                        var srcLoc = new TextureCopyLocation(stagingResource, footprints[sub]);
                        var dstLoc = new TextureCopyLocation(textureResource, sub);
                        list.CopyTextureRegion(dstLoc, 0, 0, 0, srcLoc);
                    }
                },
                new IDisposable[] { stagingResource });

            var srvDesc = payload.IsCubemap
                ? GpuTextureFormatHelpers12.MakeCubeSrvDesc(mipCount, dxgiFormat)
                : GpuTextureFormatHelpers12.MakeSrvDesc(mipCount, dxgiFormat);
            _completedUploads.Enqueue(new CompletedUpload(
                cacheKey, texture, srvDesc, payload.Format, payload.NormalDecodeMode,
                payload.ByteSize, allocationBytes, payload.IsCompressed, copyFenceValue));
            // The decoded CPU payload is now in the GPU staging buffer — release its mip bytes from
            // the resolver cache so they don't accumulate in managed memory (the dominant heap /
            // GC-stall source under heavy streaming). The path is never re-requested (the Entry is
            // cached), so this costs nothing.
            if (ReleaseTexturePayloadsAfterUpload)
            {
                _resolver?.Release(cacheKey);
            }

            // Ownership transferred: texture → render thread (via the completion); staging → copy
            // queue retirement. Clear locals so the catch/finally don't double-free them.
            texture = null;
            staging = null;

            if (started != 0)
            {
                RendererProfilerTrace.Event("resource-event", new Dictionary<string, object?>
                {
                    ["resource"] = "texture",
                    ["phase"] = "gpu-upload",
                    ["cacheTag"] = _traceCacheTag,
                    ["path"] = cacheKey,
                    ["format"] = payload.Format.ToString(),
                    ["width"] = payload.Width,
                    ["height"] = payload.Height,
                    ["mipCount"] = payload.MipCount,
                    ["compressed"] = payload.IsCompressed,
                    ["bytes"] = payload.ByteSize,
                    ["elapsedMs"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn(
                "GpuTextureCache12: texture upload failed for '{0}': {1}",
                cacheKey,
                ex.Message);
            // A copy-queue submission can be the first call to observe a GPU device removal —
            // attribute it here (no-op if the device is fine). Needs FALLOUT_VIEWER_DRED=1 for detail.
            try
            {
                _gpu.LogDeviceRemovedDiagnostics("texture-upload");
            }
            catch (Exception dredEx)
            {
                Logger.Instance.Warn("GpuTextureCache12: DRED dump threw: {0}", dredEx.Message);
            }

            staging?.Dispose();
            texture?.Dispose();
            _completedUploads.Enqueue(CompletedUpload.Failure(cacheKey));
        }
    }

    private void DisposeResource(ID3D12Resource resource)
    {
        if (_deletionQueue is not null)
        {
            _deletionQueue.EnqueueDispose(resource);
        }
        else
        {
            resource.Dispose();
        }
    }

    /// <summary>
    ///     Emits the opt-in profiler snapshot once. Owners whose dependents release every cache entry
    ///     during their own teardown call this immediately before that release cascade; Dispose is the
    ///     fallback for owners that leave entries resident until cache teardown.
    /// </summary>
    internal void EmitTraceSummary()
    {
        if (_traceSummaryEmitted)
        {
            return;
        }

        _traceSummaryEmitted = true;

        if (!_aliasTraceEnabled || !RendererProfilerTrace.IsEnabled)
        {
            return;
        }

        try
        {
            var aliases = BuildAliasTraceSummary(_cache.Values.Select(static node =>
                new AliasTraceEntry(
                    node.Path,
                    node.Entry.IsResident,
                    node.Entry.ByteSize,
                    node.AliasTrace)));
            var stats = GetStats();
            RendererProfilerTrace.Event(
                "resource-event",
                BuildCacheSummaryTraceFields(
                    _traceCacheTag,
                    stats,
                    PendingResolveCount,
                    PendingUploadCount,
                    PendingUploadDispatch,
                    aliases));
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn(
                "GpuTextureCache12[{0}]: profiler cache-summary emission failed: {1}",
                _traceCacheTag,
                ex.Message);
        }
    }

    internal static Dictionary<string, object?> BuildCacheSummaryTraceFields(
        string cacheTag,
        ResourceStats stats,
        int pendingResolves,
        int pendingUploads,
        int pendingUploadDispatch,
        AliasTraceSummary aliases)
    {
        var aliasGroups = aliases.Groups
            .Select(static group => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["canonicalKey"] = group.CanonicalKey,
                ["legacyKeys"] = group.LegacyKeys,
                ["residentPayloadBytes"] = group.ResidentPayloadBytes,
                ["estimatedAvoidedPayloadBytes"] = group.EstimatedAvoidedPayloadBytes
            })
            .ToArray();

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["resource"] = "texture",
            ["phase"] = "cache-summary",
            ["cacheTag"] = cacheTag,
            ["cacheEntries"] = stats.EntryCount,
            ["residentEntries"] = aliases.ResidentEntries,
            ["nonResidentEntries"] = aliases.NonResidentEntries,
            ["residentPayloadBytes"] = stats.EstimatedBytes,
            ["hits"] = stats.Hits,
            ["misses"] = stats.Misses,
            ["evictions"] = stats.Evictions,
            ["queueDepth"] = stats.QueueDepth,
            ["inFlight"] = stats.InFlight,
            ["pendingResolves"] = pendingResolves,
            ["pendingUploads"] = pendingUploads,
            ["pendingUploadDispatch"] = pendingUploadDispatch,
            ["residentAliasGroups"] = aliases.Groups.Count,
            ["residentLegacyExtraKeys"] = aliases.LegacyExtraKeys,
            ["estimatedResidentAliasPayloadBytesAvoided"] = aliases.EstimatedAliasResidentBytesAvoided,
            ["residentAliasDetails"] = aliasGroups
        };
    }

    /// <summary>
    ///     Builds the teardown-only alias snapshot. Only entries resident at the snapshot contribute
    ///     groups or bytes; evicted nodes are absent from the input and pending/failed placeholders
    ///     are explicitly excluded. The avoided value is an estimate because <see cref="Release" />
    ///     receives an Entry, not the original legacy spelling, so a live node cannot attribute
    ///     partial reference releases back to individual pre-canonicalization keys.
    /// </summary>
    internal static AliasTraceSummary BuildAliasTraceSummary(IEnumerable<AliasTraceEntry> entries)
    {
        var residentEntries = 0;
        var nonResidentEntries = 0;
        var legacyExtraKeys = 0;
        long estimatedAliasResidentBytesAvoided = 0;
        var groups = new List<AliasTraceGroup>();

        foreach (var entry in entries.OrderBy(static entry => entry.CanonicalKey, StringComparer.Ordinal))
        {
            if (!entry.IsResident)
            {
                nonResidentEntries++;
                continue;
            }

            residentEntries++;
            var legacyKeyCount = entry.AliasTrace?.LegacyKeyCount ?? 0;
            if (legacyKeyCount <= 1)
            {
                continue;
            }

            var extraKeys = legacyKeyCount - 1;
            var estimatedAvoidedBytes = entry.ResidentPayloadBytes * extraKeys;
            legacyExtraKeys += extraKeys;
            estimatedAliasResidentBytesAvoided += estimatedAvoidedBytes;
            groups.Add(new AliasTraceGroup(
                entry.CanonicalKey,
                entry.AliasTrace!.GetSortedLegacyKeys(),
                entry.ResidentPayloadBytes,
                estimatedAvoidedBytes));
        }

        return new AliasTraceSummary(
            residentEntries,
            nonResidentEntries,
            legacyExtraKeys,
            estimatedAliasResidentBytesAvoided,
            groups);
    }


    /// <summary>
    ///     Deletion-queue payload that returns a persistent bindless slot to the allocator's
    ///     free-list once the GPU has drained the frame that evicted it.
    /// </summary>
    private sealed class PersistentSlotReturn(GpuDescriptorHeapAllocator12 heap, uint slot) : IDisposable
    {
        public void Dispose()
        {
            heap.FreePersistent(slot);
        }
    }

    private sealed class TextureUploadNode
    {
        internal TextureUploadNode(string path, Entry entry, LegacyAliasTrace? aliasTrace)
        {
            Path = path;
            Entry = entry;
            AliasTrace = aliasTrace;
        }

        internal string Path { get; }

        internal Entry Entry { get; }

        /// <summary>
        ///     Distinct keys that the pre-canonicalization cache would have seen during this live
        ///     node's lifetime. Null outside opt-in profiler traces; naturally discarded on eviction.
        /// </summary>
        internal LegacyAliasTrace? AliasTrace { get; }

        /// <summary>
        ///     Null while the background resolution stage is still running (or after a permanent
        ///     failure, paired with <see cref="Failed" />); set once a payload is ready to upload.
        /// </summary>
        internal GpuTexturePayload? Payload { get; set; }

        /// <summary>Sitting in the render-thread dispatch queue awaiting handoff to the uploader.</summary>
        internal bool InDispatchQueue { get; set; }

        /// <summary>Handed to the uploader thread and awaiting copy completion + promotion.</summary>
        internal bool Dispatched { get; set; }

        internal bool Failed { get; set; }
    }

    /// <summary>
    ///     Per-live-node set of pre-canonicalization keys. The comparer deliberately matches the old
    ///     cache dictionary, preventing slash/case/whitespace-only variants from inflating aliases.
    ///     Like the cache dictionary and reference counts, this tracker is render-thread-owned.
    /// </summary>
    internal sealed class LegacyAliasTrace
    {
        private readonly HashSet<string> _legacyKeys = new(StringComparer.OrdinalIgnoreCase);

        internal LegacyAliasTrace(string path)
        {
            Observe(path);
        }

        internal int LegacyKeyCount => _legacyKeys.Count;

        internal bool Observe(string path)
        {
            return _legacyKeys.Add(NormalizeLegacyCacheKeyForTrace(path));
        }

        internal string[] GetSortedLegacyKeys()
        {
            return _legacyKeys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    internal readonly record struct AliasTraceEntry(
        string CanonicalKey,
        bool IsResident,
        long ResidentPayloadBytes,
        LegacyAliasTrace? AliasTrace);

    internal readonly record struct AliasTraceGroup(
        string CanonicalKey,
        string[] LegacyKeys,
        long ResidentPayloadBytes,
        long EstimatedAvoidedPayloadBytes);

    internal readonly record struct AliasTraceSummary(
        int ResidentEntries,
        int NonResidentEntries,
        int LegacyExtraKeys,
        long EstimatedAliasResidentBytesAvoided,
        IReadOnlyList<AliasTraceGroup> Groups);

    /// <summary>
    ///     An uploaded texture published by the uploader thread for the render thread to promote
    ///     once <see cref="CopyFenceValue" /> completes. A null <see cref="Texture" /> signals a
    ///     failed upload (the target node settles onto its fallback).
    /// </summary>
    private readonly record struct CompletedUpload(
        string CacheKey,
        ID3D12Resource? Texture,
        ShaderResourceViewDescription SrvDesc,
        GpuTexturePayloadFormat Format,
        GpuNormalDecodeMode NormalDecodeMode,
        long ByteSize,
        long AllocationBytes,
        bool IsCompressed,
        ulong CopyFenceValue)
    {
        internal static CompletedUpload Failure(string cacheKey)
        {
            return new CompletedUpload(cacheKey, null, default, default, default, 0, 0, false, 0);
        }
    }

    /// <summary>
    ///     One cached texture descriptor. Cold texture misses receive a non-resident entry
    ///     backed by a fallback SRV; upload completion overwrites the same descriptor slot and
    ///     mutates the metadata read by reference draw constants.
    /// </summary>
    public sealed class Entry
    {
        /// <summary>
        ///     Live references held by cached meshes (render-thread only). Acquired in
        ///     <see cref="GpuTextureCache12.GetOrUpload" />, dropped in
        ///     <see cref="GpuTextureCache12.Release" />; at zero the entry is evicted so its bindless
        ///     slot and GPU texture are reclaimed. Pinned fallbacks (null <see cref="CacheKey" />)
        ///     ignore this.
        /// </summary>
        internal int RefCount;

        internal Entry(
            ID3D12Resource texture,
            ShaderResourceViewDescription srvDesc,
            CpuDescriptorHandle persistentSrv,
            uint bindlessIndex,
            GpuTexturePayloadFormat format,
            GpuNormalDecodeMode normalDecodeMode,
            bool isResident,
            string? cacheKey)
        {
            Texture = texture;
            SrvDesc = srvDesc;
            PersistentSrv = persistentSrv;
            BindlessIndex = bindlessIndex;
            Format = format;
            NormalDecodeMode = normalDecodeMode;
            IsResident = isResident;
            IsReady = isResident;
            CacheKey = cacheKey;
        }

        /// <summary>
        ///     The <c>_cache</c> key this entry is stored under, or null for the pinned shared
        ///     fallback singletons (white pixel / flat normal), which are never reference-counted
        ///     or evicted. Used by <see cref="GpuTextureCache12.Release" /> to find + remove the node.
        /// </summary>
        internal string? CacheKey { get; }

        public ID3D12Resource Texture { get; private set; }

        public ShaderResourceViewDescription SrvDesc { get; private set; }

        public CpuDescriptorHandle PersistentSrv { get; }

        public uint BindlessIndex { get; }

        public GpuTexturePayloadFormat Format { get; private set; }

        public GpuNormalDecodeMode NormalDecodeMode { get; private set; }

        public bool IsResident { get; private set; }

        /// <summary>
        ///     True once the resident texture is a TextureCube SRV (six-face environment map).
        ///     Cold placeholders are 2D, so consumers gating a cube-sampling shader term on this
        ///     never index a mismatched descriptor dimension.
        /// </summary>
        public bool IsCubemap => SrvDesc.ViewDimension == ShaderResourceViewDimension.TextureCube;

        /// <summary>GPU bytes of the resident texture (0 while a placeholder). Feeds diagnostics.</summary>
        internal long ByteSize { get; private set; }

        /// <summary>
        ///     True once this entry no longer represents an in-flight placeholder. Successful
        ///     uploads set both <see cref="IsResident" /> and <see cref="IsReady" />; failed or
        ///     missing textures leave the fallback SRV in place but still become ready so callers
        ///     do not hide the owning geometry forever.
        /// </summary>
        public bool IsReady { get; private set; }

        internal void ReplaceTexture(
            ID3D12Resource texture,
            ShaderResourceViewDescription srvDesc,
            GpuTexturePayloadFormat format,
            GpuNormalDecodeMode normalDecodeMode,
            long byteSize)
        {
            Texture = texture;
            SrvDesc = srvDesc;
            Format = format;
            NormalDecodeMode = normalDecodeMode;
            ByteSize = byteSize;
            IsResident = true;
            IsReady = true;
        }

        internal void MarkUnavailable()
        {
            IsReady = true;
        }

        /// <summary>
        ///     Stamps the allocation footprint on a PINNED entry (fallback singletons, synthesized
        ///     frames), whose texture is created outside the streaming upload path and therefore
        ///     never passes through <see cref="ReplaceTexture" />. Placeholders that alias a
        ///     fallback's texture must NOT be stamped — the bytes belong to the fallback and are
        ///     counted once.
        /// </summary>
        internal void SetPinnedFootprint(long byteSize)
        {
            ByteSize = byteSize;
        }
    }
}
