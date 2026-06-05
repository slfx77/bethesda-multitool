#if WINDOWS_GUI
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

    // Decode runs entirely on background Task.Run threads (BSA read → parse → endian convert →
    // geometry extract) and never touches the render thread, so we scale it to the machine instead
    // of the old hard-coded 2. Leave one core for the render/UI thread; clamp to [4,16] so small
    // machines still fill the pool and large ones don't oversubscribe. Downstream work stays
    // bounded by the 256 MB decoded-mesh cache budget and the per-frame GPU upload budget, so a
    // wider decode pool only shortens the "one type at a time" pop-in — it does not spike a frame.
    private static readonly int DefaultMaxConcurrentDecodeTasks = Math.Clamp(Environment.ProcessorCount - 1, 4, 16);

    // Start as many decodes per frame as the pool can hold so it fills in one burst rather than
    // ramping two-per-frame; the `_activeDecodeTasks < MaxConcurrentDecodeTasks` gate in
    // StartQueuedDecodes is the real throttle.
    private static readonly int DefaultMaxDecodeStartsPerFrame = DefaultMaxConcurrentDecodeTasks;
    private const long DefaultDecodedMeshCacheByteBudget = 256L * 1024L * 1024L;

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly CLI.NpcMeshArchiveSet _meshArchives;
    private readonly NifTextureResolver _textureResolver;
    private readonly GpuTextureCache12 _textureCache;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly Dictionary<string, Node> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    private readonly Queue<string> _decodeQueue = new();
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
    private bool _disposed;

    public ReferenceMeshCache12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
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
        _recorder = recorder;
        _meshArchives = meshArchives;
        _textureResolver = textureResolver;
        _textureCache = textureCache;
        _deletionQueue = deletionQueue;
        _persistentDecodedCache = persistentDecodedCache ?? ReferenceDecodedMeshDiskCache12.CreateFromEnvironment();
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => _entries.Count;
    public int MaxDecodeStartsPerFrame { get; init; } = DefaultMaxDecodeStartsPerFrame;
    public int MaxConcurrentDecodeTasks { get; init; } = DefaultMaxConcurrentDecodeTasks;
    public long DecodedMeshCacheByteBudget { get; init; } = DefaultDecodedMeshCacheByteBudget;
    public int FrameCacheMisses { get; private set; }
    public int FrameDecodeRequests { get; private set; }
    public int FrameQueuedDecodes { get; private set; }
    public int FrameDecodeStarts { get; private set; }
    public int FrameActiveDecodes => Volatile.Read(ref _activeDecodeTasks);
    public int FrameCpuDecodedCacheHits { get; private set; }
    public int FrameCpuDecodedCacheMisses { get; private set; }
    public int FrameCpuDecodedNegativeHits { get; private set; }
    public int FrameGpuUploads { get; private set; }
    public int FrameCompressedTextureUploads => _textureCache.FrameCompressedUploads;
    public int FrameRgbaTextureUploads => _textureCache.FrameRgbaFallbackUploads;
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
        _textureCache.ResetFrameStats();
        PruneCompletedDecodeTasks();
    }

    public CachedNifMesh12? GetOrUpload(string modelPath, ref int uploadBudget)
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
            QueueDecode(cacheKey, node);
        }
        StartQueuedDecodes();
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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

        uploadBudget--;
        FrameGpuUploads++;
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

    private void QueueDecode(string modelPath, Node node)
    {
        if (node.DecodeTask is not null ||
            node.DecodeQueued ||
            node.ResolvedNull ||
            _queuedDecodePaths.Contains(modelPath))
        {
            return;
        }

        _decodeQueue.Enqueue(modelPath);
        _queuedDecodePaths.Add(modelPath);
        node.DecodeQueued = true;
        FrameQueuedDecodes++;
    }

    private void StartQueuedDecodes()
    {
        while (FrameDecodeStarts < MaxDecodeStartsPerFrame &&
               Volatile.Read(ref _activeDecodeTasks) < MaxConcurrentDecodeTasks &&
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
        var lookupPath = NormalizeModelPath(modelPath);

        // No lock needed: NpcMeshArchiveSet.TryExtractFile resolves under its own cache lock and
        // BsaExtractor.ExtractFile is memory-mapped/lock-free, so concurrent decode tasks extract
        // in parallel. This is what actually parallelizes mesh streaming (removing the old coarse
        // archive lock that serialized every decode despite the wider task pool).
        if (!_meshArchives.TryExtractFile(lookupPath, out var nifData, out _) || nifData.Length == 0)
        {
            Interlocked.Increment(ref _totalMissingModelPaths);
            return null;
        }

        var nif = NifParser.Parse(nifData);
        if (nif is null) return null;

        if (nif.IsBigEndian)
        {
            var converted = NifConverter.Convert(nifData);
            if (!converted.Success || converted.OutputData is null) return null;
            nifData = converted.OutputData;
            nif = NifParser.Parse(nifData);
            if (nif is null) return null;
        }

        var model = NifGeometryExtractor.Extract(
            nifData, nif,
            textureResolver: _textureResolver,
            bindPoseOnly: false,
            skipSkinning: true);

        if (model is null) return null;
        if (model.WasSkinned)
        {
            Interlocked.Increment(ref _totalSkinnedModelPaths);
            return null;
        }
        if (!model.HasGeometry) return null;

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

        return submeshes.Count == 0 ? null : new DecodedNifMesh12(submeshes);
    }

    private CachedNifMesh12? UploadDecodedMesh(string modelPath, DecodedNifMesh12 decoded)
    {
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

        ID3D12Resource? vertexBuffer = null;
        ID3D12Resource? indexBuffer = null;
        try
        {
            vertexBuffer = GpuMeshBufferFactory12.CreateDefaultBuffer(
                _gpu,
                _recorder.CommandList,
                _deletionQueue,
                vertices,
                ResourceStates.VertexAndConstantBuffer);
            indexBuffer = GpuMeshBufferFactory12.CreateDefaultBuffer(
                _gpu,
                _recorder.CommandList,
                _deletionQueue,
                indices,
                ResourceStates.IndexBuffer);
        }
        catch (Exception ex)
        {
            Log.Warn("ReferenceMeshCache12: packed mesh buffer upload failed for '{0}': {1}", modelPath, ex.Message);
            if (vertexBuffer is not null) _deletionQueue.EnqueueDispose(vertexBuffer);
            if (indexBuffer is not null) _deletionQueue.EnqueueDispose(indexBuffer);
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
                    VertexBufferView = GpuMeshBufferFactory12.VertexBufferViewOf(
                        vertexBuffer!,
                        vertexByteOffset,
                        vertexByteSize,
                        vertexStride),
                    IndexBufferView = GpuMeshBufferFactory12.IndexBufferViewOf(
                        indexBuffer!,
                        indexByteOffset,
                        indexByteSize),
                    IndexCount = sub.Indices.Length,
                    Diffuse = diffuse,
                    Normal = normal,
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
            _deletionQueue.EnqueueDispose(vertexBuffer);
            _deletionQueue.EnqueueDispose(indexBuffer);
            return null;
        }

        return new CachedNifMesh12(submeshes, vertexBuffer, indexBuffer, _deletionQueue);
    }

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
    private readonly ID3D12Resource _vertexBuffer;
    private readonly ID3D12Resource _indexBuffer;
    private readonly GpuDeletionQueue12 _deletionQueue;

    public CachedNifMesh12(
        IReadOnlyList<CachedSubmesh12> submeshes,
        ID3D12Resource vertexBuffer,
        ID3D12Resource indexBuffer,
        GpuDeletionQueue12 deletionQueue)
    {
        Submeshes = submeshes;
        _vertexBuffer = vertexBuffer;
        _indexBuffer = indexBuffer;
        _deletionQueue = deletionQueue;
    }

    public IReadOnlyList<CachedSubmesh12> Submeshes { get; }

    public void Dispose()
    {
        _deletionQueue.EnqueueDispose(_vertexBuffer);
        _deletionQueue.EnqueueDispose(_indexBuffer);
    }
}

internal sealed class CachedSubmesh12
{
    public required VertexBufferView VertexBufferView { get; init; }
    public required IndexBufferView IndexBufferView { get; init; }
    public required int IndexCount { get; init; }
    public required GpuTextureCache12.Entry Diffuse { get; init; }
    public required GpuTextureCache12.Entry Normal { get; init; }
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
