using System.Collections.Concurrent;
using System.Diagnostics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Orchestration;
using BethesdaMultitool.Core.Resources;
using Slfx77.Multitool.Core.Lifetime;
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

    /// <summary>How many unresolvable textures get named individually before rate-limiting.</summary>
    private const int MaxNamedResolveFailures = 8;

    // Scales with cores (GC-guarded: half the logical processors, clamped to [2, 12]) — texture
    // resolve (BSA read + DDX→DDS transcode) is the documented streaming-hitch cost and is
    // embarrassingly parallel + off the render thread, so more workers directly multiply throughput.
    // Half-cores (not full) leaves headroom for mesh decode + render + GC; the 8→12 ceiling raise
    // matches the decode-worker raise (high-core machines were idling half their cores cold-loading
    // FO4's texture-heavy Commonwealth). Env override for profiling.
    private static readonly int DefaultMaxConcurrentTextureResolves =
        ConcurrencyPolicy
            .Fixed(CpuBudget.Interactive()
                .Claim(CpuWorkload.TextureResolve))
            .WithEnvironmentOverride(EnvironmentVariables.Viewer.TextureResolveConcurrency, 1, 12)
            .Resolve();

    // Release each decoded texture payload's CPU mip bytes from the resolver cache once it's on the
    // GPU (default on) — they're dead weight afterward and otherwise accumulate to multi-GB managed
    // heap → GC stalls. FALLOUT_VIEWER_RETAIN_TEXTURE_PAYLOADS=1 keeps the old retain-forever
    // behavior (for A/B measurement / fallback).
    private static readonly bool ReleaseTexturePayloadsAfterUpload =
        !EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.RetainTexturePayloads);

    private static readonly Logger Log = Logger.Instance;

    // Alias accounting is opt-in with the JSONL profiler. Keep it off the normal hot path entirely:
    // when tracing is disabled, nodes carry no legacy-key set and GetOrUpload performs no extra
    // path normalization or allocations.
    private readonly bool _aliasTraceEnabled;
    private readonly Dictionary<string, TextureUploadNode> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<CompletedUpload> _completedUploads = new();
    private readonly ConcurrentQueue<IDisposable> _failedUploadResources = new();

    // Async copy-queue upload machinery (owned per-cache, mirroring _resolveQueue ownership).
    private readonly GpuUploadQueue12 _copyUploadQueue;
    private readonly GpuDeletionQueue12? _deletionQueue;

    private readonly GpuDevice12 _gpu;
    private readonly GpuDescriptorHeapAllocator12 _heap;

    // Resolution runs on background threads, so both of these are touched off the render thread.
    private readonly List<string> _namedResolveFailures = new(MaxNamedResolveFailures);
    private readonly List<ID3D12Resource> _ownedTextures = new();
    private readonly HashSet<GpuTextureRetirement12> _pendingEntryRetirements = new();
    private readonly Queue<TextureUploadNode> _pendingDispatch = new();
    private readonly List<CompletedUpload> _pendingPromote = new();
    private readonly GpuCommandRecorder12 _recorder;
    private readonly Lock _resolveFailureGate = new();
    private readonly BoundedResolveQueue<string, GpuTexturePayload> _resolveQueue;

    private readonly NifGpuTextureResolver? _resolver;

    // Frame-independent fallback/placeholder texture creation (1×1 solids + cold-miss placeholders).
    // Deliberately kept OUT of the async copy-queue upload pipeline — it only needs the device + heap.
    private readonly GpuSolidTextureFactory12 _solidTextureFactory;
    private readonly Dictionary<string, Entry> _syntheticEntries = new(StringComparer.OrdinalIgnoreCase);
    private readonly DedicatedWorkerThread _uploadDispatcher;
    private bool _disposed;
    private bool _disposing;
    private RetiredResourceDisposal? _retirementPrerequisites;
    private RetiredResourceDisposal? _retiredResources;
    private long _evictions;

    private Entry? _flatNormal;

    // Time-based streaming pace for this frame (StreamingFrameBudgetScaler; set by ResetFrameStats).
    private double _frameBudgetScale = 1.0;
    private long _hits;

    private long _misses;

    // Fallback singletons + synthesized frames: created once, never released before Dispose, and
    // outside the refcounted add/release pair that maintains _residentBytes. Counted separately so
    // the resident total includes them without touching the release-path symmetry.
    private long _pinnedBytes;

    // Diagnostics counters — plain fields written only by the render thread (tracking-only
    // conformance; this cache is refcount-pinned and never trimmed). Snapshot readers tolerate
    // slightly stale values per the ITrackableResource threading contract.
    private ResourceRegistration? _registration;
    private long _residentBytes;
    private int _resolveFailures;
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

    /// <summary>Drains texture users and proves copy retirement before releasing resource and descriptor ownership.</summary>
    /// <remarks>Failed drains or releases remain owned for retry; callers must retain the resolver, device and heap until this succeeds.</remarks>
    public void Dispose()
    {
        if (_disposed && _retiredResources is not null && !_retiredResources.HasPending) { return; }
#pragma warning disable S3877 // A reentrant Dispose cannot report success while this dependent still owns live resources.
        if (_disposing) { throw new InvalidOperationException("Texture-cache retirement is already in progress."); }
#pragma warning restore S3877
        _disposing = true;
        try
        {
            if (_retirementPrerequisites is null)
            {
                var prerequisites = new RetiredResourceDisposal(ReportRetirementFailure);
                prerequisites.Add(EmitTraceSummary, "texture trace summary", 0);
                prerequisites.Add(_uploadDispatcher.Stop, "texture uploader drain", 0);
                prerequisites.Add(_resolveQueue.WaitForDrain, "texture resolver drain", 0);
                prerequisites.Add(_copyUploadQueue, "texture copy retirement", 1);
                prerequisites.Add(() =>
                {
                    _registration?.Dispose();
                    _registration = null;
                }, "texture cache registration", 2);
                prerequisites.Add(_uploadDispatcher, "texture uploader", 3);
                prerequisites.Add(_resolveQueue, "texture resolver queue", 3);
                _retirementPrerequisites = prerequisites;
                _disposed = true;
            }

            // A stop request or failed copy wait is not completion. Keep every texture collection
            // and borrowed resolver/device prerequisite intact until all of these stages succeed.
            _retirementPrerequisites.Dispose();
            if (_retiredResources is null) { CollectRetiredResources(); }
            _retiredResources!.Dispose();
        }
        finally { _disposing = false; }
    }

    /// <summary>Transfers the frozen post-drain texture graph into retryable actions without losing partially collected ownership.</summary>
    private void CollectRetiredResources()
    {
        var resources = new RetiredResourceDisposal(ReportRetirementFailure);
        var textures = new HashSet<IDisposable>(ReferenceEqualityComparer.Instance);
        foreach (var completed in _completedUploads)
        {
            if (completed.Texture is not null) { textures.Add(completed.Texture); }
        }
        foreach (var pending in _pendingPromote)
        {
            if (pending.Texture is not null) { textures.Add(pending.Texture); }
        }
        foreach (var texture in _ownedTextures) { textures.Add(texture); }
        foreach (var resource in _failedUploadResources) { textures.Add(resource); }
        var slots = new HashSet<uint>();
        foreach (var node in _cache.Values)
        {
            slots.Add(node.Entry.BindlessIndex);
        }
        if (_whitePixel is Entry wp)
        {
            textures.Add(wp.Texture);
            slots.Add(wp.BindlessIndex);
        }
        if (_flatNormal is Entry fn)
        {
            textures.Add(fn.Texture);
            slots.Add(fn.BindlessIndex);
        }
        if (_waterSurface is Entry ws)
        {
            textures.Add(ws.Texture);
            slots.Add(ws.BindlessIndex);
        }
        foreach (var synthetic in _syntheticEntries.Values)
        {
            textures.Add(synthetic.Texture);
            slots.Add(synthetic.BindlessIndex);
        }
        foreach (var texture in textures)
        {
            resources.Add(() =>
            {
                if (texture is ID3D12Resource native) { DisposeResource(native); }
                else { texture.Dispose(); }
            }, "retired texture", 0);
        }
        foreach (var retirement in _pendingEntryRetirements)
        {
            resources.Add(() => TransferRetiredEntry(retirement), "evicted texture ownership transfer", 0);
        }
        foreach (var slot in slots)
        {
            resources.Add(() => RetirePersistentSlot(slot), "texture descriptor", 1);
        }

        // All captured identities remain in the original collections if allocation above fails.
        // Publishing the complete owner precedes clearing those aliases or invoking any release.
        _retiredResources = resources;
        _completedUploads.Clear();
        _failedUploadResources.Clear();
        _pendingPromote.Clear();
        _syntheticEntries.Clear();
        _ownedTextures.Clear();
        _pendingDispatch.Clear();
        _cache.Clear();
        _whitePixel = null;
        _flatNormal = null;
        _waterSurface = null;
        PendingUploadCount = 0;
    }

    /// <summary>Reports a retained cleanup failure without claiming the failed owner was released.</summary>
    /// <param name="name">Failed ownership operation.</param>
    /// <param name="exception">Observed release or drain failure.</param>
    private static void ReportRetirementFailure(string name, Exception exception) =>
        Log.Warn("GpuTextureCache12: {0} remains pending: {1}", name, exception.Message);

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
    ///     Creates a pinned fallback singleton AND counts it: pinned entries never pass through the
    ///     refcounted add/release pair, so before this they were invisible to the resident total.
    /// </summary>
    private Entry CreatePinnedSolid(byte r, byte g, byte b, byte a)
    {
        var entry = _solidTextureFactory.CreateSolid(r, g, b, a);
        _pinnedBytes += entry.ByteSize;
        return entry;
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
    ///     generates at runtime (retail ships no water00-31.dds). Callers choose whether the upload
    ///     carries a CPU box-filtered mip chain; the default preserves the existing policy for
    ///     synthesized assets that are not runtime render-target equivalents. Entries are pinned
    ///     like the fallback singletons: never refcounted or evicted, released at cache disposal.
    /// </summary>
    public Entry GetOrCreateSynthetic(
        string key,
        int width,
        int height,
        byte[] rgba,
        bool generateMips = true)
    {
        if (_syntheticEntries.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var entry = _solidTextureFactory.CreateFromRgba(width, height, rgba, generateMips);
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
    public Entry GetOrUpload(string path, bool isNormalMap = false, DecodedTexture? generatedTexture = null)
    {
        var cacheKey = NormalizeCacheKey(path, isNormalMap);
        if (cacheKey.Length == 0)
        {
            return isNormalMap ? FlatNormal : WhitePixel;
        }

        if (generatedTexture is null && _resolver is not null) cacheKey = _resolver.GetCacheKey(cacheKey);

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
        if (_resolver is null && generatedTexture is null)
        {
            return fallback; // pinned fallback — not reference-counted.
        }

        // Cold miss: hand back a fallback placeholder immediately and resolve the real DDS/DDX payload
        // on a background thread. The streamed GPU upload later overwrites this Entry's persistent
        // descriptor slot in place, so callers that cached the Entry upgrade from placeholder →
        // textured transparently (BindlessIndex is stable).
        var generatedPayload = generatedTexture is null ? null : GpuTexturePayload.FromRgba(generatedTexture);
        var entry = _solidTextureFactory.CreatePlaceholder(fallback, cacheKey);
        entry.RefCount = 1; // acquire (first reference).
        node = new TextureUploadNode(
            cacheKey,
            entry,
            _aliasTraceEnabled ? new LegacyAliasTrace(path) : null);
        _cache[cacheKey] = node;
        if (generatedPayload is not null)
        {
            // The normal refcounted upload/retirement path owns this immutable scene payload.
            // It does not depend on the mesh LRU or a browser's temporary FaceGen cache surviving.
            node.Payload = generatedPayload;
            EnqueueForDispatch(node, true);
            return entry;
        }
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

        if (entry.Retirement is { } pending)
        {
            TransferRetiredEntry(pending);
            return; // This acquisition reached zero already; retry only its retained transfer.
        }

        if (entry.RefCount <= 0)
        {
            return; // already released (defensive against double-release).
        }

        if (entry.RefCount > 1)
        {
            entry.RefCount--;
            return; // still referenced by another resident mesh.
        }

        // Last reference dropped → evict.
        if (!_cache.TryGetValue(key, out var node) || !ReferenceEquals(node.Entry, entry))
        {
            entry.RefCount = 0;
            return; // node already gone or replaced; nothing to reclaim.
        }

        // Construct and retain the complete bundle before detaching the entry. A failed queue
        // admission or synchronous release can then retry this exact entry even after a new entry
        // with the same key is acquired. A placeholder borrows its pinned fallback's texture.
        var ownsTexture = entry.IsResident && _ownedTextures.Contains(entry.Texture);
        var slot = entry.BindlessIndex;
        var retirement = new GpuTextureRetirement12(
            ownsTexture ? entry.Texture : null,
            () => _heap.FreePersistent(slot),
            _deletionQueue,
            ownsTexture ? entry.ByteSize : 0);
        _pendingEntryRetirements.Add(retirement);
        entry.Retirement = retirement;
        entry.RefCount = 0;
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
        if (ownsTexture)
        {
            _ownedTextures.Remove(entry.Texture);
        }

        _evictions++;

        // One queue admission transfers both children. Shared retains independent release
        // progress, and does not return the slot if releasing its texture fails.
        TransferRetiredEntry(retirement);
    }

    /// <summary>Removes a cache charge only after the complete retired entry leaves cache ownership.</summary>
    /// <param name="retirement">Exact evicted entry token; retries cannot affect a replacement entry.</param>
    private void TransferRetiredEntry(GpuTextureRetirement12 retirement)
    {
        retirement.Transfer();
        if (_pendingEntryRetirements.Remove(retirement))
        {
            _residentBytes -= retirement.ResidentBytes;
        }
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
        while (_completedUploads.TryPeek(out var completed))
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

                _completedUploads.TryDequeue(out _);
                continue;
            }

            // Growing the render-thread list can fail. Keep the cross-thread owner until the
            // next owner has accepted the same resource; this is the queue's only consumer.
            _pendingPromote.Add(completed);
            _completedUploads.TryDequeue(out _);
        }

        if (_pendingPromote.Count == 0)
        {
            return;
        }

        var completedFence = _copyUploadQueue.LastCompletedValue;
        var cmd = _recorder.CommandList;
        var keep = 0;
        var read = 0;
        try
        {
            for (; read < _pendingPromote.Count; read++)
            {
                var c = _pendingPromote[read];
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

                // Admission must precede publication: a growing ownership list cannot orphan a
                // texture whose entry has already become resident.
                _ownedTextures.EnsureCapacity(checked(_ownedTextures.Count + 1));
                // Water-noise compute and ordinary pixel sampling share these resident resources.
                cmd.ResourceBarrierTransition(
                    c.Texture!,
                    ResourceStates.Common,
                    ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
                _gpu.Device.CreateShaderResourceView(c.Texture, c.SrvDesc, node.Entry.PersistentSrv);
                // Cache charges use allocation footprint; frame pacing uses compressed payload size.
                node.Entry.ReplaceTexture(c.Texture!, c.SrvDesc, c.Format, c.NormalDecodeMode, c.AllocationBytes);
                _residentBytes += c.AllocationBytes;
                node.Dispatched = false;
                // The resolver relinquished its payload after upload; remove the last node reference.
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
        }
        finally
        {
            // Remove successful transfers even if a later item fails. Preserve that item and the
            // unvisited tail; retry must never release an earlier orphan for a second time.
            for (; read < _pendingPromote.Count; read++)
            {
                _pendingPromote[keep++] = _pendingPromote[read];
            }
            _pendingPromote.RemoveRange(keep, _pendingPromote.Count - keep);
        }
    }

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
        if (count == 25 || count == 100 || count == 1_000 || count % 10_000 == 0)
        {
            string firstFailures;
            lock (_resolveFailureGate)
            {
                firstFailures = string.Join(", ", _namedResolveFailures);
            }

            Log.Warn(
                "GpuTextureCache12[{0}]: {1:N0} textures unresolved so far. First few: {2}.",
                _traceCacheTag, count, firstFailures);
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
        if (payload is null && !_resolver.IsUnauthoredStarfieldNormalMap(BethesdaMultitool.Core.Assets.AssetCacheIdentity.PathOf(cacheKey)))
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
                ["assetDerivation"] = payload?.Derivation,
                ["assetReadReceipts"] = BethesdaMultitool.Core.Assets.AssetSelectionJson.Serialize(payload?.AssetReadReceipts ?? []),
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
    ///     completion so the node settles onto its fallback. Possibly submitted destinations remain
    ///     owned until cache retirement proves the copy queue safe. Never touches render-thread state.
    /// </summary>
    /// <param name="cacheKey">Exact cache identity whose upload completion is published.</param>
    /// <param name="payload">Decoded mip payload borrowed for this synchronous upload operation.</param>
    private void RunUploadOnUploaderThread(string cacheKey, GpuTexturePayload payload)
    {
        var started = RendererProfilerTrace.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        ID3D12Resource? texture = null;
        ID3D12Resource? staging = null;
        var stagingOwnershipTransferred = false;
        var completionPublished = false;
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
                new IDisposable[] { stagingResource }, out stagingOwnershipTransferred);
            staging = null; // The copy queue owns staging even if a later publication step fails.

            var srvDesc = payload.IsCubemap
                ? GpuTextureFormatHelpers12.MakeCubeSrvDesc(mipCount, dxgiFormat)
                : GpuTextureFormatHelpers12.MakeSrvDesc(mipCount, dxgiFormat);
            _completedUploads.Enqueue(new CompletedUpload(
                cacheKey, texture, srvDesc, payload.Format, payload.NormalDecodeMode,
                payload.ByteSize, allocationBytes, payload.IsCompressed, copyFenceValue));
            completionPublished = true;
            texture = null; // The completion owns the destination before fallible resolver/trace calls.
            // The decoded CPU payload is now in the GPU staging buffer — release its mip bytes from
            // the resolver cache so they don't accumulate in managed memory (the dominant heap /
            // GC-stall source under heavy streaming). The path is never re-requested (the Entry is
            // cached), so this costs nothing.
            if (ReleaseTexturePayloadsAfterUpload)
            {
                _resolver?.Release(cacheKey);
            }

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
            // Execute/Signal can fail after the list reached the queue. The queue already owns its
            // staging in that case; this cache retains the destination until copy retirement. Keep
            // pre-submit failures here too so a failed child release cannot escape its owner.
            if (!stagingOwnershipTransferred && staging is not null) { _failedUploadResources.Enqueue(staging); }
            if (texture is not null) { _failedUploadResources.Enqueue(texture); }
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

            if (!completionPublished) { _completedUploads.Enqueue(CompletedUpload.Failure(cacheKey)); }
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

        /// <summary>Retains the last-reference transfer so a failed release retries without consuming another acquisition.</summary>
        internal GpuTextureRetirement12? Retirement { get; set; }

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
