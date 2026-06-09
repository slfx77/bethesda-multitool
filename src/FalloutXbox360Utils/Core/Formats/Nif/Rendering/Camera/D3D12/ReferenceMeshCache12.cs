#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
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
        "FALLOUT_VIEWER_REFERENCE_DECODE_CONCURRENCY",
        defaultValue: Math.Clamp(Environment.ProcessorCount / 2, 2, 8),
        min: 1,
        max: 16);

    private static readonly int DefaultMaxDecodeStartsPerFrame = ParsePositiveIntEnvironment(
        "FALLOUT_VIEWER_REFERENCE_DECODE_STARTS_PER_FRAME",
        defaultValue: DefaultMaxConcurrentDecodeTasks,
        min: 1,
        max: DefaultMaxConcurrentDecodeTasks);
    private const long DefaultDecodedMeshCacheByteBudget = 256L * 1024L * 1024L;
    private const float ReferenceBumpStrength = 0.35f;

    // Size-aware ceiling on render-thread mesh-upload work per frame. The integer upload budget
    // (passed by the renderer) caps *how many* meshes upload; this caps the *bytes*, checked before
    // each upload so a frame that has already spent its budget defers an expensive mesh to the next
    // frame instead of overshooting on it. Sized so a typical 48-mesh frame passes but a burst of
    // unusually large meshes is spread across frames.
    private static readonly long DefaultMaxUploadBytesPerFrame = ParsePositiveLongEnvironment(
        "FALLOUT_VIEWER_REFERENCE_UPLOAD_BYTES_PER_FRAME",
        defaultValue: 4L * 1024L * 1024L,
        min: 1L * 1024L * 1024L,
        max: 64L * 1024L * 1024L);

    private readonly GpuDevice12 _gpu;
    private readonly CLI.NpcMeshArchiveSet _meshArchives;
    private readonly NifTextureResolver _textureResolver;
    private readonly GpuTextureCache12 _textureCache;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly GpuGeometryArena12 _geometryArena;
    private readonly Dictionary<string, Node> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    // Nearest-first decode queue: priority = squared distance from the view point at request time
    // (smaller = nearer = dequeued first). The queue persists across frames, so when a dense area
    // enters view the foreground meshes decode before the far edge instead of in arrival (FIFO)
    // order. _queuedDecodePaths dedups (PriorityQueue has no Contains); a path keeps its first
    // enqueued priority — approximate but cheap, no priority updates.
    private readonly PriorityQueue<string, float> _decodeQueue = new();
    private readonly HashSet<string> _queuedDecodePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task<DecodedNifMesh12?>> _decodeTasks = new();
    private readonly Dictionary<string, DecodedCacheNode> _decodedCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _decodedCacheOrder = new();
    private readonly ReferenceDecodedMeshDiskCache12? _persistentDecodedCache;
    private readonly object _decodedCacheLock = new();
    private readonly object _decodeTaskListLock = new();
    private long _decodedCacheBytes;
    private int _activeDecodeTasks;
    private int _totalMissingModelPaths;
    private int _totalSkinnedModelPaths;
    private FrameUploadByteBudget _frameUploadByteBudget;
    private bool _disposed;

    public ReferenceMeshCache12(
        GpuDevice12 gpu,
        CLI.NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        GpuTextureCache12 textureCache,
        GpuDeletionQueue12 deletionQueue,
        int capacity,
        ReferenceDecodedMeshDiskCache12? persistentDecodedCache = null)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");

        _gpu = gpu;
        _meshArchives = meshArchives;
        _textureResolver = textureResolver;
        _textureCache = textureCache;
        _deletionQueue = deletionQueue;
        _geometryArena = new GpuGeometryArena12(_gpu);
        _persistentDecodedCache = persistentDecodedCache ?? ReferenceDecodedMeshDiskCache12.CreateFromEnvironment();
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => _entries.Count;
    public int MaxDecodeStartsPerFrame { get; init; } = DefaultMaxDecodeStartsPerFrame;
    public int MaxConcurrentDecodeTasks { get; init; } = DefaultMaxConcurrentDecodeTasks;
    public long DecodedMeshCacheByteBudget { get; init; } = DefaultDecodedMeshCacheByteBudget;
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
        _frameUploadByteBudget = new FrameUploadByteBudget(StreamingThrottled ? MaxUploadBytesPerFrame : long.MaxValue);
        _textureCache.ResetFrameStats();
        PruneCompletedDecodeTasks();
    }

    public CachedNifMesh12? GetOrUpload(string modelPath, ref int uploadBudget, float priority = 0f)
    {
        if (_disposed) return null;

        PruneCompletedDecodeTasks();
        StartQueuedDecodes();

        var cacheKey = NormalizeModelPath(modelPath);
        if (_entries.TryGetValue(cacheKey, out var existing))
        {
            Touch(existing);
            var resolved = ResolveExisting(cacheKey, existing, ref uploadBudget);
            StartQueuedDecodes();
            return resolved;
        }

        FrameCacheMisses++;
        var node = new Node(
            Mesh: null,
            DecodeTask: null,
            OrderNode: new LinkedListNode<string>(cacheKey),
            ResolvedNull: false);
        Insert(cacheKey, node);
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

        Task[] pending;
        lock (_decodeTaskListLock)
        {
            pending = _decodeTasks
                .Where(static t => !t.IsCompleted)
                .Cast<Task>()
                .ToArray();
        }
        if (pending.Length > 0 && !Task.WaitAll(pending, TimeSpan.FromSeconds(5)))
        {
            Log.Warn("ReferenceMeshCache12: {0} decode task(s) still running during dispose.", pending.Length);
        }

        foreach (var node in _entries.Values)
        {
            node.Mesh?.Dispose();
        }
        _entries.Clear();
        _order.Clear();
        _decodeQueue.Clear();
        _queuedDecodePaths.Clear();
        lock (_decodedCacheLock)
        {
            _decodedCache.Clear();
            _decodedCacheOrder.Clear();
            _decodedCacheBytes = 0;
        }

        // Frees the arena's UPLOAD blocks. The host calls WaitForGpuIdle before disposing this cache,
        // so no draw still references them; any arena frees the meshes above enqueued on the deletion
        // queue become no-ops (GpuGeometryArena12.Free is disposed-guarded).
        _geometryArena.Dispose();
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

    private void Insert(string modelPath, Node node)
    {
        _order.AddFirst(node.OrderNode);
        _entries[modelPath] = node;

        while (_entries.Count > Capacity)
        {
            var tail = _order.Last;
            if (tail is null) break;
            _order.RemoveLast();
            if (_entries.Remove(tail.Value, out var evicted))
            {
                evicted.Mesh?.Dispose();
            }
        }
    }

    private void Touch(Node node)
    {
        _order.Remove(node.OrderNode);
        _order.AddFirst(node.OrderNode);
    }

    private void QueueDecode(string modelPath, Node node, float priority)
    {
        if (node.DecodeTask is not null ||
            node.DecodeQueued ||
            node.ResolvedNull ||
            _queuedDecodePaths.Contains(modelPath))
        {
            return;
        }

        _decodeQueue.Enqueue(modelPath, priority);
        _queuedDecodePaths.Add(modelPath);
        node.DecodeQueued = true;
        FrameQueuedDecodes++;
    }

    private void StartQueuedDecodes()
    {
        while (FrameDecodeStarts < EffectiveMaxDecodeStartsPerFrame &&
               Volatile.Read(ref _activeDecodeTasks) < EffectiveMaxConcurrentDecodeTasks &&
               _decodeQueue.Count > 0)
        {
            var modelPath = _decodeQueue.Dequeue();
            _queuedDecodePaths.Remove(modelPath);
            if (!_entries.TryGetValue(modelPath, out var node) ||
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

        lock (_decodeTaskListLock)
        {
            _decodeTasks.Add(task);
        }

        return task;
    }

    private void PruneCompletedDecodeTasks()
    {
        lock (_decodeTaskListLock)
        {
            for (var i = _decodeTasks.Count - 1; i >= 0; i--)
            {
                if (_decodeTasks[i].IsCompleted)
                {
                    _decodeTasks.RemoveAt(i);
                }
            }
        }
    }

    private bool TryGetDecodedCache(string modelPath, out DecodedCacheValue value)
    {
        lock (_decodedCacheLock)
        {
            if (!_decodedCache.TryGetValue(modelPath, out var node))
            {
                value = default;
                return false;
            }

            _decodedCacheOrder.Remove(node.OrderNode);
            _decodedCacheOrder.AddFirst(node.OrderNode);
            value = node.Value;
            return true;
        }
    }

    private bool TryLoadPersistentDecodedCache(string modelPath, out DecodedCacheValue value)
    {
        value = default;
        if (_persistentDecodedCache is null)
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
            if (_decodedCache.Remove(modelPath, out var existing))
            {
                _decodedCacheBytes -= existing.Value.ByteSize;
                _decodedCacheOrder.Remove(existing.OrderNode);
            }

            var orderNode = new LinkedListNode<string>(modelPath);
            _decodedCacheOrder.AddFirst(orderNode);
            _decodedCache[modelPath] = new DecodedCacheNode(value, orderNode);
            _decodedCacheBytes += byteSize;

            while (_decodedCacheBytes > DecodedMeshCacheByteBudget)
            {
                var tail = _decodedCacheOrder.Last;
                if (tail is null)
                {
                    break;
                }

                _decodedCacheOrder.RemoveLast();
                if (_decodedCache.Remove(tail.Value, out var evicted))
                {
                    _decodedCacheBytes -= evicted.Value.ByteSize;
                }
            }
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
                sub.LocalBoundsCenter));
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
                sub.LocalBoundsCenter));
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
            var nif = NifParser.Parse(nifData);
            if (nif is null)
            {
                result = "parse-failed";
                return null;
            }

            if (nif.IsBigEndian)
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

            var model = NifGeometryExtractor.Extract(
                nifData, nif,
                textureResolver: _textureResolver,
                bindPoseOnly: false,
                skipSkinning: true);

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
                    ComputeLocalBoundsCenter(sub.Positions)));
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

        var submeshes = new List<CachedSubmesh12>(decoded.Submeshes.Count);
        var vertexStride = (uint)System.Runtime.InteropServices.Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        for (var i = 0; i < decoded.Submeshes.Count; i++)
        {
            var sub = decoded.Submeshes[i];
            try
            {
                var diffuse = string.IsNullOrEmpty(sub.DiffuseTexturePath)
                    ? _textureCache.WhitePixel
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
                    LocalBoundsCenter = sub.LocalBoundsCenter
                });
            }
            catch (Exception ex)
            {
                Log.Warn("ReferenceMeshCache12: submesh upload failed for '{0}': {1}", modelPath, ex.Message);
            }
        }

        if (submeshes.Count == 0)
        {
            // No CachedNifMesh12 is created and no draw referenced the range, so reclaim it now.
            _geometryArena.Free(geometry);
            return null;
        }

        success = true;
        var cached = new CachedNifMesh12(submeshes, geometry, _geometryArena, _deletionQueue, _textureCache);
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
        return normalized.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : "meshes\\" + normalized.TrimStart('\\');
    }

    private static int ParsePositiveIntEnvironment(string name, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!int.TryParse(raw, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static long ParsePositiveLongEnvironment(string name, long defaultValue, long min, long max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (!long.TryParse(raw, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private sealed class Node(
        CachedNifMesh12? Mesh,
        Task<DecodedNifMesh12?>? DecodeTask,
        LinkedListNode<string> OrderNode,
        bool ResolvedNull)
    {
        public CachedNifMesh12? Mesh { get; set; } = Mesh;
        public Task<DecodedNifMesh12?>? DecodeTask { get; set; } = DecodeTask;
        public LinkedListNode<string> OrderNode { get; } = OrderNode;
        public bool ResolvedNull { get; set; } = ResolvedNull;
        public bool DecodeQueued { get; set; }
        public bool DecodedCacheAvailable { get; set; }
        public bool DecodedCacheMissRecorded { get; set; }
    }

    private sealed record DecodedCacheNode(
        DecodedCacheValue Value,
        LinkedListNode<string> OrderNode);

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
        Vector3 LocalBoundsCenter);
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
        GpuTextureCache12 textureCache)
    {
        Submeshes = submeshes;
        _geometry = geometry;
        _arena = arena;
        _deletionQueue = deletionQueue;
        _textureCache = textureCache;
    }

    public IReadOnlyList<CachedSubmesh12> Submeshes { get; }

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
}
#endif
