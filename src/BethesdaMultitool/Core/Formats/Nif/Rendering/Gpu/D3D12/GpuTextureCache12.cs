using System.Collections.Concurrent;
using System.Threading;
using Vortice.Direct3D12;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Orchestration;

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
    // Scales with cores (GC-guarded: half the logical processors, clamped to [2, 12]) — texture
    // resolve (BSA read + DDX→DDS transcode) is the documented streaming-hitch cost and is
    // embarrassingly parallel + off the render thread, so more workers directly multiply throughput.
    // Half-cores (not full) leaves headroom for mesh decode + render + GC; the 8→12 ceiling raise
    // matches the decode-worker raise (high-core machines were idling half their cores cold-loading
    // FO4's texture-heavy Commonwealth). Env override for profiling.
    private static readonly int DefaultMaxConcurrentTextureResolves = ParsePositiveIntEnvironment(
        EnvironmentVariables.Viewer.TextureResolveConcurrency,
        defaultValue: Math.Clamp(Environment.ProcessorCount / 2, 2, 12),
        min: 1,
        max: 12);

    // Release each decoded texture payload's CPU mip bytes from the resolver cache once it's on the
    // GPU (default on) — they're dead weight afterward and otherwise accumulate to multi-GB managed
    // heap → GC stalls. FALLOUT_VIEWER_RETAIN_TEXTURE_PAYLOADS=1 keeps the old retain-forever
    // behavior (for A/B measurement / fallback).
    private static readonly bool ReleaseTexturePayloadsAfterUpload =
        !EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.RetainTexturePayloads);

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly NifGpuTextureResolver? _resolver;
    // Time-based streaming pace for this frame (StreamingFrameBudgetScaler; set by ResetFrameStats).
    private double _frameBudgetScale = 1.0;
    private readonly GpuDeletionQueue12? _deletionQueue;
    private readonly GpuDescriptorHeapAllocator12 _heap;
    // Frame-independent fallback/placeholder texture creation (1×1 solids + cold-miss placeholders).
    // Deliberately kept OUT of the async copy-queue upload pipeline — it only needs the device + heap.
    private readonly GpuSolidTextureFactory12 _solidTextureFactory;
    private readonly Dictionary<string, TextureUploadNode> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<TextureUploadNode> _pendingDispatch = new();
    private readonly BoundedResolveQueue<string, GpuTexturePayload> _resolveQueue;
    private readonly List<ID3D12Resource> _ownedTextures = new();
    // Async copy-queue upload machinery (owned per-cache, mirroring _resolveQueue ownership).
    private readonly GpuUploadQueue12 _copyUploadQueue;
    private readonly DedicatedWorkerThread _uploadDispatcher;
    private readonly ConcurrentQueue<CompletedUpload> _completedUploads = new();
    private readonly List<CompletedUpload> _pendingPromote = new();
    private Entry? _whitePixel;
    private Entry? _flatNormal;
    private Entry? _waterSurface;
    private int _pendingUploadCount;
    private bool _disposed;

    // Diagnostics counters — plain fields written only by the render thread (tracking-only
    // conformance; this cache is refcount-pinned and never trimmed). Snapshot readers tolerate
    // slightly stale values per the ITrackableResource threading contract.
    private ResourceRegistration? _registration;
    private long _residentBytes;
    private long _hits;
    private long _misses;
    private long _evictions;

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
        // Resolve DDS/DDX payloads off the render thread. Keep the default conservative because
        // DDX conversion and DDS parsing allocate enough to cause visible UI/render pauses when
        // too many complete in the same window.
        _resolveQueue = new BoundedResolveQueue<string, GpuTexturePayload>(
            "TextureResolveQueue",
            maxConcurrent: DefaultMaxConcurrentTextureResolves,
            resolve: ResolvePayload,
            keyComparer: StringComparer.OrdinalIgnoreCase);
        _copyUploadQueue = new GpuUploadQueue12(gpu);
        _uploadDispatcher = new DedicatedWorkerThread("GpuTextureUploader");
    }

    public string ResourceName => nameof(GpuTextureCache12);

    public ResourceCategory Category => ResourceCategory.GpuResident;

    public ResourceStats GetStats() => new()
    {
        EstimatedBytes = Volatile.Read(ref _residentBytes),
        EntryCount = _cache.Count,
        Hits = Volatile.Read(ref _hits),
        Misses = Volatile.Read(ref _misses),
        Evictions = Volatile.Read(ref _evictions),
        QueueDepth = _uploadDispatcher.PendingCount,
        InFlight = _resolveQueue.ActiveCount,
    };

    /// <summary>
    ///     Registers the cache with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the cache for fluent construction.
    /// </summary>
    public GpuTextureCache12 RegisterWith(ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    /// <summary>1x1 opaque white texture used as the fallback diffuse.</summary>
    public Entry WhitePixel => _whitePixel ??= _solidTextureFactory.CreateSolid(255, 255, 255, 255);

    /// <summary>1x1 flat normal map used as the fallback normal.</summary>
    public Entry FlatNormal => _flatNormal ??= _solidTextureFactory.CreateSolid(128, 128, 255, 255);

    /// <summary>
    ///     1×1 translucent water-blue tile used as the diffuse for placed water-shader geometry
    ///     (WaterShaderProperty NIFs carry no diffuse — the engine renders them via its water system,
    ///     which we don't reproduce on placed geometry). Renders the surface as blue water instead of
    ///     the opaque-white fallback. RGB ≈ (0.15, 0.32, 0.42); combined with forced alpha-blend it
    ///     reads as a translucent water plane.
    /// </summary>
    public Entry WaterSurface => _waterSurface ??= _solidTextureFactory.CreateSolid(38, 82, 107, 255);

    public int MaxUploadsPerFrame { get; init; } = DefaultMaxUploadsPerFrame;

    public long MaxUploadBytesPerFrame { get; init; } = DefaultMaxUploadBytesPerFrame;

    public int FrameCompressedUploads { get; private set; }

    public int FrameRgbaFallbackUploads { get; private set; }

    public int FrameQueuedUploads { get; private set; }

    public long FrameUploadBytes { get; private set; }

    /// <summary>Texture paths newly queued for background payload resolution this frame.</summary>
    public int FrameQueuedResolves { get; private set; }

    /// <summary>Background payload-resolution tasks running right now.</summary>
    public int FrameActiveResolves => _resolveQueue.ActiveCount;

    public int PendingUploadCount => _pendingUploadCount;

    public int PendingResolveCount => _resolveQueue.QueuedCount;

    /// <summary>Upload work items handed to the uploader thread but not yet processed.</summary>
    public int PendingUploadDispatch => _uploadDispatcher.PendingCount;

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
        var cacheKey = path.Replace('/', '\\').Trim();
        if (cacheKey.Length == 0)
        {
            return isNormalMap ? FlatNormal : WhitePixel;
        }

        if (_cache.TryGetValue(cacheKey, out var node))
        {
            _hits++;
            // Resolved (payload present) but not yet resident → make sure it is queued for dispatch.
            // Still resolving (payload null) → just return the placeholder; the background resolution
            // queues the dispatch itself once it completes.
            if (node.Payload is not null)
            {
                EnqueueForDispatch(node, newlyPending: false);
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
        node = new TextureUploadNode(cacheKey, entry);
        _cache[cacheKey] = node;
        if (_resolveQueue.Enqueue(cacheKey))
        {
            FrameQueuedResolves++;
        }
        _resolveQueue.Pump();
        return entry;
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
            _pendingUploadCount--;
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
        if (_deletionQueue is not null)
        {
            _deletionQueue.EnqueueDispose(new PersistentSlotReturn(_heap, entry.BindlessIndex));
        }
        else
        {
            _heap.FreePersistent(entry.BindlessIndex);
        }
    }

    /// <summary>Deletion-queue payload that returns a persistent bindless slot to the allocator's
    /// free-list once the GPU has drained the frame that evicted it.</summary>
    private sealed class PersistentSlotReturn(GpuDescriptorHeapAllocator12 heap, uint slot) : IDisposable
    {
        public void Dispose() => heap.FreePersistent(slot);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
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
        if (_whitePixel is Entry wp) DisposeResource(wp.Texture);
        if (_flatNormal is Entry fn) DisposeResource(fn.Texture);
        if (_waterSurface is Entry ws) DisposeResource(ws.Texture);
        _ownedTextures.Clear();
        _pendingDispatch.Clear();
        _cache.Clear();
        _pendingUploadCount = 0;
        // _heap is owned by the host (WorldView3DControl); do not dispose here.
    }

    /// <summary>
    ///     Render-thread step: enqueue a resolved-but-not-resident node onto the dispatch queue so
    ///     <see cref="DispatchQueuedUploads" /> can hand it to the uploader thread. Idempotent — a
    ///     node already queued, dispatched, resident or failed is left alone. <paramref
    ///     name="newlyPending" /> increments the pending counter exactly once, at first resolution.
    /// </summary>
    private void EnqueueForDispatch(TextureUploadNode node, bool newlyPending)
    {
        if (node.Payload is null || node.Entry.IsResident || node.Failed || node.InDispatchQueue || node.Dispatched)
        {
            return;
        }

        if (newlyPending)
        {
            _pendingUploadCount++;
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
        var uploadLimit = Math.Max(1,
            Core.Resources.StreamingFrameBudgetScaler.ScaleCount(MaxUploadsPerFrame, _frameBudgetScale));
        // Count stays in the while condition (count exhaustion leaves the next node at the queue
        // FRONT, exactly as before); CanStart gates bytes with the first-item-always rule.
        var budget = new FrameBudget(
            uploadLimit,
            Core.Resources.StreamingFrameBudgetScaler.ScaleBytes(MaxUploadBytesPerFrame, _frameBudgetScale));

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
            EnqueueForDispatch(node, newlyPending: true);
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
                    _pendingUploadCount--;
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
            node.Entry.ReplaceTexture(c.Texture!, c.SrvDesc, c.Format, c.NormalDecodeMode, c.ByteSize);
            _residentBytes += c.ByteSize;
            node.Dispatched = false;
            // Drop the decoded payload's mip bytes now that the texture is resident on the GPU.
            // The node is the second strong ref (alongside the resolver cache, released on the
            // uploader thread); clearing it here is what actually frees the bytes — otherwise every
            // resident texture's CPU bytes are retained for the session (multi-GB heap / GC stalls).
            node.Payload = null;
            _ownedTextures.Add(c.Texture!);
            _pendingUploadCount--;

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
    private GpuTexturePayload? ResolvePayload(string cacheKey)
    {
        if (_resolver is null)
        {
            return null;
        }

        var started = RendererProfilerTrace.IsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var payload = _resolver.GetTexture(cacheKey);
        if (started != 0)
        {
            RendererProfilerTrace.Event("resource-event", new Dictionary<string, object?>
            {
                ["resource"] = "texture",
                ["phase"] = "resolve",
                ["path"] = cacheKey,
                ["found"] = payload is not null,
                ["compressed"] = payload?.IsCompressed,
                ["bytes"] = payload?.ByteSize ?? 0,
                ["elapsedMs"] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
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
        var started = RendererProfilerTrace.IsEnabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
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
                arraySize: arraySize, mipLevels: mipCount,
                sampleCount: 1, sampleQuality: 0,
                ResourceFlags.None);

            // COMMON initial state: a copy queue implicitly promotes COMMON→COPY_DEST for the copy
            // and the resource decays back to COMMON on copy-list completion. The render thread then
            // transitions COMMON→PIXEL_SHADER_RESOURCE after the copy fence passes (copy queues can't
            // transition to shader-read states themselves).
            texture = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties,
                HeapFlags.None,
                desc,
                ResourceStates.Common,
                optimizedClearValue: null);

            var footprints = new PlacedSubresourceFootPrint[subresourceCount];
            var numRows = new uint[subresourceCount];
            var rowSize = new ulong[subresourceCount];
            _gpu.Device.GetCopyableFootprints(
                desc, firstSubresource: 0, numSubresources: (uint)subresourceCount, baseOffset: 0,
                footprints, numRows, rowSize, out var totalBytes);

            staging = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer(totalBytes),
                ResourceStates.GenericRead,
                optimizedClearValue: null);

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
                staging.Unmap(0, null);
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
                payload.ByteSize, payload.IsCompressed, copyFenceValue));
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
                    ["path"] = cacheKey,
                    ["format"] = payload.Format.ToString(),
                    ["width"] = payload.Width,
                    ["height"] = payload.Height,
                    ["mipCount"] = payload.MipCount,
                    ["compressed"] = payload.IsCompressed,
                    ["bytes"] = payload.ByteSize,
                    ["elapsedMs"] = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
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
            try { _gpu.LogDeviceRemovedDiagnostics("texture-upload"); }
            catch (Exception dredEx) { Logger.Instance.Warn("GpuTextureCache12: DRED dump threw: {0}", dredEx.Message); }
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

    private static int ParsePositiveIntEnvironment(string name, int defaultValue, int min, int max)
    {
        var raw = EnvironmentVariables.Get(name);
        if (!int.TryParse(raw, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private sealed class TextureUploadNode
    {
        internal TextureUploadNode(string path, Entry entry)
        {
            Path = path;
            Entry = entry;
        }

        internal string Path { get; }

        internal Entry Entry { get; }

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
        bool IsCompressed,
        ulong CopyFenceValue)
    {
        internal static CompletedUpload Failure(string cacheKey) =>
            new(cacheKey, null, default, default, default, 0, false, 0);
    }

    /// <summary>
    ///     One cached texture descriptor. Cold texture misses receive a non-resident entry
    ///     backed by a fallback SRV; upload completion overwrites the same descriptor slot and
    ///     mutates the metadata read by reference draw constants.
    /// </summary>
    public sealed class Entry
    {
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

        /// <summary>
        ///     Live references held by cached meshes (render-thread only). Acquired in
        ///     <see cref="GpuTextureCache12.GetOrUpload" />, dropped in
        ///     <see cref="GpuTextureCache12.Release" />; at zero the entry is evicted so its bindless
        ///     slot and GPU texture are reclaimed. Pinned fallbacks (null <see cref="CacheKey" />)
        ///     ignore this.
        /// </summary>
        internal int RefCount;

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
    }
}
