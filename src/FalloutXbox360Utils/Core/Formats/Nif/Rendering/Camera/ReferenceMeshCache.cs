using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Vortice.Direct3D11;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     v3 Phase 3 — caches uploaded GPU meshes for NIF model paths referenced by REFRs.
///     Bounded LRU; entries are loaded lazily on first sight from the bound
///     <see cref="CLI.NpcMeshArchiveSet" />, parsed via the existing
///     <see cref="NifGeometryExtractor" />, and uploaded to D3D11 via
///     <see cref="GpuMeshUploader" />.
///     <para>
///         Negative-result caching: when a path resolves to a missing/empty NIF, a skinned NIF
///         (deferred to v4), or one that yields no submeshes, the cache stores a <c>null</c>
///         entry so the loader isn't re-invoked every frame for every visible instance.
///     </para>
///     <para>
///         Texture ownership: each submesh's <see cref="CachedSubmesh.DiffuseSrv" /> is owned
///         by the shared <see cref="GpuTextureCache" /> passed in the ctor — disposing this
///         cache does NOT dispose those SRVs (other meshes might still reference them).
///     </para>
/// </summary>
internal sealed class ReferenceMeshCache : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly ID3D11Device _device;
    private readonly CLI.NpcMeshArchiveSet _meshArchives;
    private readonly NifTextureResolver _textureResolver;
    private readonly GpuTextureCache _textureCache;
    private readonly Dictionary<string, Node> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    private bool _disposed;

    public ReferenceMeshCache(
        ID3D11Device device,
        CLI.NpcMeshArchiveSet meshArchives,
        NifTextureResolver textureResolver,
        GpuTextureCache textureCache,
        int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");
        _device = device;
        _meshArchives = meshArchives;
        _textureResolver = textureResolver;
        _textureCache = textureCache;
        Capacity = capacity;
    }

    public int Capacity { get; }
    public int Count => _entries.Count;
    public int FrameCacheMisses { get; private set; }

    public void ResetFrameStats() => FrameCacheMisses = 0;

    /// <summary>
    ///     Looks up (or loads) the cached GPU mesh for <paramref name="modelPath" />. Returns
    ///     <c>null</c> when the NIF is missing, empty, skinned (v4), or yielded no usable
    ///     submeshes — these negative results are cached so we don't retry every frame.
    /// </summary>
    public CachedNifMesh? GetOrUpload(string modelPath)
    {
        if (_entries.TryGetValue(modelPath, out var node))
        {
            // LRU bump
            _order.Remove(node.OrderNode);
            _order.AddFirst(node.OrderNode);
            return node.Mesh;
        }

        FrameCacheMisses++;
        var mesh = BuildMesh(modelPath);
        Insert(modelPath, mesh);
        return mesh;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var node in _entries.Values)
        {
            node.Mesh?.Dispose();
        }
        _entries.Clear();
        _order.Clear();
    }

    private void Insert(string modelPath, CachedNifMesh? mesh)
    {
        var orderNode = new LinkedListNode<string>(modelPath);
        _order.AddFirst(orderNode);
        _entries[modelPath] = new Node(mesh, orderNode);

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

    private CachedNifMesh? BuildMesh(string modelPath)
    {
        // Bethesda model paths in records are stored "architecture\foo.nif"; the BSA index is
        // case-insensitive, accepts forward or backslashes (see NpcMeshArchiveSet.ResolveHit),
        // and expects a relative path without a "meshes\" prefix on the original record. The
        // base-record parsers already store the path verbatim from MODL, which works for the
        // sprite pipeline; reuse that representation directly.
        var raw = CLI.NpcMeshHelpers.LoadNifRawFromBsa(modelPath, _meshArchives);
        if (raw is null) return null;

        // bindPoseOnly=true: render at the NIF's authored bind pose, skip node-hierarchy
        // animation transforms. skipSkinning=true: don't even attempt skin-weight evaluation —
        // skinned NIFs are filtered out below, so the skinning pass would be wasted work.
        var model = NifGeometryExtractor.Extract(
            raw.Value.Data, raw.Value.Info,
            textureResolver: _textureResolver,
            bindPoseOnly: true,
            skipSkinning: true);

        if (model is null) return null;
        if (model.WasSkinned)
        {
            // Skinned actors / weapons / clothing → v4. Negative-cache so we don't retry.
            return null;
        }
        if (!model.HasGeometry) return null;

        var submeshes = new List<CachedSubmesh>(model.Submeshes.Count);
        foreach (var sub in model.Submeshes)
        {
            if (sub.Positions.Length == 0 || sub.Triangles.Length == 0) continue;

            ID3D11Buffer? vb = null;
            ID3D11Buffer? ib = null;
            try
            {
                var vertices = GpuMeshUploader.BuildVertices(sub);
                vb = GpuMeshUploader.CreateVertexBuffer(_device, vertices);
                ib = GpuMeshUploader.CreateIndexBuffer(_device, sub.Triangles);

                // Texture: hit the shared GpuTextureCache so the same DDS isn't re-uploaded
                // when two REFRs share a diffuse. WhitePixel fallback when DiffuseTexturePath
                // is null / not in the BSA — keeps the geometry visible.
                var diffuseSrv = string.IsNullOrEmpty(sub.DiffuseTexturePath)
                    ? _textureCache.WhitePixel
                    : _textureCache.GetOrUpload(sub.DiffuseTexturePath!, _textureResolver);

                submeshes.Add(new CachedSubmesh
                {
                    VertexBuffer = vb,
                    IndexBuffer = ib,
                    IndexCount = sub.Triangles.Length,
                    DiffuseSrv = diffuseSrv,
                    AlphaTest = sub.HasAlphaTest,
                    AlphaTestThreshold = sub.AlphaTestThreshold / 255f,
                    DoubleSided = sub.IsDoubleSided
                });
                vb = null; // ownership transferred to CachedSubmesh
                ib = null;
            }
            catch (Exception ex)
            {
                Log.Warn("ReferenceMeshCache: submesh upload failed for '{0}': {1}", modelPath, ex.Message);
                vb?.Dispose();
                ib?.Dispose();
            }
        }

        if (submeshes.Count == 0) return null;
        return new CachedNifMesh(submeshes);
    }

    private readonly record struct Node(CachedNifMesh? Mesh, LinkedListNode<string> OrderNode);
}

/// <summary>
///     Per-model cached GPU representation: a list of <see cref="CachedSubmesh" />es. The
///     model is whatever <see cref="NifGeometryExtractor" /> produced after filtering empty +
///     skinned shapes. Disposing the mesh disposes each submesh's owned buffers (VB/IB) but
///     leaves the texture SRVs alone — those are owned by the shared
///     <see cref="GpuTextureCache" />.
/// </summary>
internal sealed class CachedNifMesh : IDisposable
{
    public CachedNifMesh(IReadOnlyList<CachedSubmesh> submeshes)
    {
        Submeshes = submeshes;
    }

    public IReadOnlyList<CachedSubmesh> Submeshes { get; }

    public void Dispose()
    {
        foreach (var sub in Submeshes) sub.Dispose();
    }
}

internal sealed class CachedSubmesh : IDisposable
{
    public required ID3D11Buffer VertexBuffer { get; init; }
    public required ID3D11Buffer IndexBuffer { get; init; }
    public required int IndexCount { get; init; }
    /// <summary>Owned by the shared <see cref="GpuTextureCache" /> — do NOT dispose here.</summary>
    public required ID3D11ShaderResourceView DiffuseSrv { get; init; }
    public required bool AlphaTest { get; init; }
    public required float AlphaTestThreshold { get; init; }
    public required bool DoubleSided { get; init; }

    public void Dispose()
    {
        VertexBuffer.Dispose();
        IndexBuffer.Dispose();
    }
}
