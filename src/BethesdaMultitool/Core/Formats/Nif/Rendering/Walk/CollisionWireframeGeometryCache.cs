using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     One placed collision mesh participating in the collision-debug wireframe. The source mesh is
///     immutable by convention, but the cache hashes its arrays on every resolve so an in-place change
///     still invalidates the persistent geometry buffer.
/// </summary>
internal readonly record struct CollisionWireframeInstance(CollisionMesh Mesh, Matrix4x4 World);

/// <summary>
///     Result of resolving the collision wireframe for one frame. <see cref="Rebuilt" /> is false when
///     the ordered mesh content and placement transforms match the preceding frame; in that case the
///     same persistent <see cref="Buffer" /> is returned and the uploader is not called.
/// </summary>
internal readonly record struct CollisionWireframeCacheResult<TBuffer>(
    TBuffer? Buffer,
    int LineVertexCount,
    int ReferencesDrawn,
    bool Rebuilt,
    bool Uploaded)
    where TBuffer : class;

/// <summary>
///     Synchronous uploader used by <see cref="CollisionWireframeGeometryCache{TBuffer}" />. The
///     implementation must consume/copy the first <paramref name="count" /> array entries before
///     returning; the cache reuses its CPU scratch array on later rebuilds.
/// </summary>
internal delegate TBuffer CollisionWireframeUploader<out TBuffer>(Vector3[] vertices, int count)
    where TBuffer : class;

/// <summary>
///     Content-addressed CPU geometry + persistent-buffer cache for the collision debug overlay.
///     Every resolve computes a compact key over the ordered visible collision meshes, their complete
///     position/index content, and placement matrices. Transforming vertices, expanding triangle edges,
///     and uploading a replacement buffer happen only when that key changes.
///     <para>
///         The cache owns the current buffer logically but never disposes it directly. Replacement,
///         explicit invalidation, and disposal call the supplied retirement action so a D3D12 caller can
///         defer resource release until in-flight frames have crossed their fence.
///     </para>
/// </summary>
internal sealed class CollisionWireframeGeometryCache<TBuffer> : IDisposable
    where TBuffer : class
{
    public const int DefaultMaxLineVertices = 500_000;

    // A line-list triangle always contributes six vertices. Rounding down prevents a permanently
    // unreachable two-vertex tail when the configured cap is not divisible by six.
    private readonly int _effectiveMaxLineVertices;

    private readonly Dictionary<CollisionMesh, MeshFingerprint> _meshFingerprintScratch =
        new(ReferenceEqualityComparer.Instance);

    private readonly Action<TBuffer> _retire;
    private readonly CollisionWireframeUploader<TBuffer> _uploader;
    private TBuffer? _buffer;
    private bool _disposed;
    private bool _hasKey;
    private WireframeContentKey _key;

    private Vector3[] _lineScratch = new Vector3[4096];
    private int _lineVertexCount;
    private int _referencesDrawn;
    private Vector3[] _worldPositionScratch = [];

    public CollisionWireframeGeometryCache(
        CollisionWireframeUploader<TBuffer> uploader,
        Action<TBuffer> retire,
        int maxLineVertices = DefaultMaxLineVertices)
    {
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentNullException.ThrowIfNull(retire);
        if (maxLineVertices < 6)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLineVertices), "Must fit at least one triangle.");
        }

        _uploader = uploader;
        _retire = retire;
        _effectiveMaxLineVertices = maxLineVertices - maxLineVertices % 6;
    }

    /// <summary>Number of content misses that rebuilt the CPU line list.</summary>
    public int BuildCount { get; private set; }

    /// <summary>Number of non-empty replacement buffers created by the uploader.</summary>
    public int UploadCount { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        Invalidate();
        _disposed = true;
    }

    public CollisionWireframeCacheResult<TBuffer> Resolve(IReadOnlyList<CollisionWireframeInstance> instances)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(instances);

        var plan = CreatePlan(instances);
        if (_hasKey && plan.Key == _key)
        {
            return new CollisionWireframeCacheResult<TBuffer>(
                _buffer, _lineVertexCount, _referencesDrawn, false, false);
        }

        BuildGeometry(instances, plan);

        TBuffer? replacement = null;
        if (plan.LineVertexCount > 0)
        {
            replacement = _uploader(_lineScratch, plan.LineVertexCount);
        }

        var previous = _buffer;
        _buffer = replacement;
        _lineVertexCount = plan.LineVertexCount;
        _referencesDrawn = plan.SelectedInstanceCount;
        _key = plan.Key;
        _hasKey = true;
        BuildCount++;
        if (replacement is not null) UploadCount++;
        if (previous is not null) _retire(previous);

        return new CollisionWireframeCacheResult<TBuffer>(
            _buffer, _lineVertexCount, _referencesDrawn, true, replacement is not null);
    }

    /// <summary>
    ///     Drops the content key and retires the current persistent buffer. The next resolve rebuilds
    ///     even if supplied with byte-for-byte identical geometry.
    /// </summary>
    public void Invalidate()
    {
        if (_buffer is { } buffer)
        {
            _retire(buffer);
            _buffer = null;
        }

        _hasKey = false;
        _lineVertexCount = 0;
        _referencesDrawn = 0;
    }

    private BuildPlan CreatePlan(IReadOnlyList<CollisionWireframeInstance> instances)
    {
        _meshFingerprintScratch.Clear();
        var content = new ContentHasher();
        content.Add((uint)_effectiveMaxLineVertices);

        var selectedInstanceCount = 0;
        var lineVertexCount = 0;
        for (var i = 0; i < instances.Count && lineVertexCount < _effectiveMaxLineVertices; i++)
        {
            var instance = instances[i];
            if (!_meshFingerprintScratch.TryGetValue(instance.Mesh, out var meshFingerprint))
            {
                meshFingerprint = Fingerprint(instance.Mesh);
                _meshFingerprintScratch.Add(instance.Mesh, meshFingerprint);
            }

            // Instance boundary + complete mesh content + exact placement bits make ordering and
            // duplicate placements significant while hashing a shared mesh's arrays only once/frame.
            content.Add(0xC0111510u);
            content.Add(meshFingerprint.HashA);
            content.Add(meshFingerprint.HashB);
            content.Add((uint)meshFingerprint.PositionCount);
            content.Add((uint)meshFingerprint.IndexCount);
            AddMatrix(ref content, instance.World);

            selectedInstanceCount++;
            var remaining = _effectiveMaxLineVertices - lineVertexCount;
            var available = Math.Min((long)meshFingerprint.ValidTriangleCount * 6, remaining);
            lineVertexCount += (int)(available - available % 6);
        }

        content.Add(0xC011E0FFu);
        content.Add((uint)selectedInstanceCount);
        content.Add((uint)lineVertexCount);
        return new BuildPlan(
            new WireframeContentKey(content.HashA, content.HashB, selectedInstanceCount, lineVertexCount),
            selectedInstanceCount,
            lineVertexCount);
    }

    private void BuildGeometry(IReadOnlyList<CollisionWireframeInstance> instances, BuildPlan plan)
    {
        EnsureLineCapacity(plan.LineVertexCount);
        var lineVertexCount = 0;

        for (var instanceIndex = 0;
             instanceIndex < plan.SelectedInstanceCount && lineVertexCount < plan.LineVertexCount;
             instanceIndex++)
        {
            var instance = instances[instanceIndex];
            var positions = instance.Mesh.Positions;
            var triangleIndices = instance.Mesh.Triangles;
            if (positions.Length == 0 || triangleIndices.Length < 3) continue;

            if (_worldPositionScratch.Length < positions.Length)
            {
                _worldPositionScratch = new Vector3[positions.Length];
            }

            for (var i = 0; i < positions.Length; i++)
            {
                _worldPositionScratch[i] = Vector3.Transform(positions[i], instance.World);
            }

            for (var triangle = 0;
                 triangle + 2 < triangleIndices.Length && lineVertexCount + 6 <= plan.LineVertexCount;
                 triangle += 3)
            {
                var a = triangleIndices[triangle];
                var b = triangleIndices[triangle + 1];
                var c = triangleIndices[triangle + 2];
                if ((uint)a >= (uint)positions.Length || (uint)b >= (uint)positions.Length ||
                    (uint)c >= (uint)positions.Length)
                {
                    continue;
                }

                var pa = _worldPositionScratch[a];
                var pb = _worldPositionScratch[b];
                var pc = _worldPositionScratch[c];
                _lineScratch[lineVertexCount++] = pa;
                _lineScratch[lineVertexCount++] = pb;
                _lineScratch[lineVertexCount++] = pb;
                _lineScratch[lineVertexCount++] = pc;
                _lineScratch[lineVertexCount++] = pc;
                _lineScratch[lineVertexCount++] = pa;
            }
        }

        // CreatePlan and BuildGeometry inspect the same immutable-by-convention arrays on one thread.
        // If a caller mutates them concurrently, fail closed instead of uploading an unkeyed buffer.
        if (lineVertexCount != plan.LineVertexCount)
        {
            throw new InvalidOperationException("Collision geometry changed while its wireframe was being built.");
        }
    }

    private static MeshFingerprint Fingerprint(CollisionMesh mesh)
    {
        var content = new ContentHasher();
        var positions = mesh.Positions;
        var triangles = mesh.Triangles;
        content.Add((uint)positions.Length);
        foreach (var position in positions)
        {
            content.Add(BitConverter.SingleToUInt32Bits(position.X));
            content.Add(BitConverter.SingleToUInt32Bits(position.Y));
            content.Add(BitConverter.SingleToUInt32Bits(position.Z));
        }

        content.Add((uint)triangles.Length);
        var validTriangleCount = 0;
        for (var i = 0; i < triangles.Length; i++)
        {
            content.Add(unchecked((uint)triangles[i]));
            if (i % 3 != 2) continue;
            var a = triangles[i - 2];
            var b = triangles[i - 1];
            var c = triangles[i];
            if ((uint)a < (uint)positions.Length && (uint)b < (uint)positions.Length &&
                (uint)c < (uint)positions.Length)
            {
                validTriangleCount++;
            }
        }

        return new MeshFingerprint(
            content.HashA, content.HashB, positions.Length, triangles.Length, validTriangleCount);
    }

    private static void AddMatrix(ref ContentHasher content, Matrix4x4 matrix)
    {
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M11));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M12));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M13));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M14));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M21));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M22));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M23));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M24));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M31));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M32));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M33));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M34));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M41));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M42));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M43));
        content.Add(BitConverter.SingleToUInt32Bits(matrix.M44));
    }

    private void EnsureLineCapacity(int required)
    {
        if (required <= _lineScratch.Length) return;
        var next = _lineScratch.Length;
        while (next < required) next = Math.Min(_effectiveMaxLineVertices, next * 2);
        Array.Resize(ref _lineScratch, next);
    }

    private readonly record struct BuildPlan(
        WireframeContentKey Key,
        int SelectedInstanceCount,
        int LineVertexCount);

    private readonly record struct WireframeContentKey(
        ulong HashA,
        ulong HashB,
        int InstanceCount,
        int LineVertexCount);

    private readonly record struct MeshFingerprint(
        ulong HashA,
        ulong HashB,
        int PositionCount,
        int IndexCount,
        int ValidTriangleCount);

    private struct ContentHasher
    {
        private const ulong FnvPrime = 1_099_511_628_211UL;

        public ContentHasher()
        {
            HashA = 14_695_981_039_346_656_037UL;
            HashB = 0x9E3779B97F4A7C15UL;
        }

        public ulong HashA { get; private set; }

        public ulong HashB { get; private set; }

        public void Add(ulong value)
        {
            Add((uint)value);
            Add((uint)(value >> 32));
        }

        public void Add(uint value)
        {
            unchecked
            {
                HashA ^= value;
                HashA *= FnvPrime;

                HashB ^= value + 0x9E3779B97F4A7C15UL;
                HashB = BitOperations.RotateLeft(HashB, 27);
                HashB = HashB * 5 + 0x52DCE729UL;
            }
        }
    }
}
