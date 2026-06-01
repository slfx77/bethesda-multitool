#if WINDOWS_GUI
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     v3 Phase 2a — renders terrain meshes for the frustum-visible exterior cells. Vertex
///     and index buffers are built and uploaded lazily on first sight, cached LRU
///     (<see cref="CellMeshLruCache{T}" />), and disposed when evicted or when the renderer
///     is torn down.
///     <para>
///         Sibling to <see cref="CellGridDebugRenderer" />; mirrors its constructor + LoadData
///         + Render(viewProj, frustum) shape so <c>WorldView3DControl</c> can compose them
///         in a uniform layer pipeline.
///     </para>
/// </summary>
internal sealed class TerrainRenderer : IDisposable
{
    private const uint UniformsByteSize = 64; // float4x4 viewProj

    /// <summary>
    ///     Cap on new cell mesh builds + uploads per frame. Bounds the worst-case frame cost
    ///     when many cells enter the frustum at once (rapid mouse-look, camera teleport).
    ///     Visible cells consume the budget first; whatever's left funds the 8-neighbor
    ///     idle pre-upload pass so cells about to enter view are already cached.
    /// </summary>
    private const int MaxNewUploadsPerFrame = 16;

    /// <summary>
    ///     Floor on the LRU cache capacity, used before <see cref="LoadData" /> is called.
    ///     <see cref="LoadData" /> upsizes the cache to the worldspace's actual cell count plus
    ///     headroom — eviction must never kick in within a single worldspace, otherwise visible
    ///     cells get evicted by other visible cells' uploads (or by neighbor pre-upload) and
    ///     the cache thrashes, with cells visibly blinking in and out as they rebuild.
    /// </summary>
    private const int MinCacheCapacity = 1024;

    /// <summary>
    ///     Spare slots over the worldspace cell count, so pre-upload of cells just outside
    ///     the current worldspace's grid (and any transient race) never triggers eviction.
    /// </summary>
    private const int CacheHeadroom = 256;

    private readonly GpuDevice _gpu;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11BlendState _blendState;

    private CellMeshLruCache<CachedCellMesh> _meshCache = new(MinCacheCapacity);
    private readonly HashSet<(int gx, int gy)> _knownUnusableCells = new();
    private readonly List<VisibleCell> _visibleScratch = new();

    // Pooled per-cell mesh buffers — reused across every TryBuildAndCache call. Without these,
    // each cell mesh would allocate ~78 KB of vertices + ~12 KB of indices; at 16 builds/frame
    // during motion that's ~85 MB/sec of garbage, which drives a Gen 2 GC every ~1 second
    // and produces a visible hitch on large worldspaces. CreateBuffer copies the bytes into
    // the immutable D3D11 buffer at create-time, so the scratch is safe to reuse next iteration.
    private readonly GpuMeshUploader.GpuVertex[] _vertexScratch = new GpuMeshUploader.GpuVertex[TerrainMeshBuilder.VertexCount];
    private readonly ushort[] _indexScratch = new ushort[TerrainMeshBuilder.IndexCount];

    // Single-element array pinned for UpdateSubresource. Reused so we don't allocate one
    // per Render call.
    private readonly TerrainUniforms[] _uniformsScratch = new TerrainUniforms[1];

    private Dictionary<(int gx, int gy), CellRecord>? _cells;

    private static readonly Comparison<VisibleCell> ByDistanceAscending =
        (a, b) => a.DistSq.CompareTo(b.DistSq);

    private readonly record struct VisibleCell((int gx, int gy) Key, CellRecord Cell, float DistSq);

    public TerrainRenderer(GpuDevice gpu)
    {
        _gpu = gpu;

        var vsBytecode = CompileEmbeddedShader("terrain.vert.hlsl", "main", "vs_5_0");
        var psBytecode = CompileEmbeddedShader("terrain.frag.hlsl", "main", "ps_5_0");
        _vertexShader = gpu.Device.CreateVertexShader(vsBytecode.AsSpan());
        _pixelShader = gpu.Device.CreatePixelShader(psBytecode.AsSpan());

        // Reuse the same input layout the NIF sprite path uses; GpuMeshUploader.GpuVertex is
        // the same 72-byte struct TerrainMeshBuilder emits.
        _inputLayout = gpu.Device.CreateInputLayout(GpuMeshUploader.InputElements, vsBytecode.AsSpan());

        _constantBuffer = gpu.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = UniformsByteSize,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

        _rasterizer = gpu.Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            // CullMode.None for v3 first pass — skips winding-debug rabbit holes. Tighten to
            // CullMode.Back once visual smoke confirms the CCW winding emitted by TerrainMeshBuilder.
            CullMode = CullMode.None,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false
        });

        _depthState = gpu.Device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.Less,
            StencilEnable = false
        });

        var blendDesc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = false, // opaque
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blendState = gpu.Device.CreateBlendState(blendDesc);
    }

    public void Dispose()
    {
        _meshCache.Dispose();
        _blendState.Dispose();
        _depthState.Dispose();
        _rasterizer.Dispose();
        _constantBuffer.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    /// <summary>Total exterior-cell count last loaded (used for the status overlay).</summary>
    public int CellCount => _cells?.Count ?? 0;

    /// <summary>
    ///     Replaces the per-cell lookup. Existing cached GPU buffers are disposed so a fresh
    ///     LoadData (e.g. switching worldspaces) doesn't leak stale meshes or carry over
    ///     "known unusable" decisions from a different data set. The LRU cache is rebuilt
    ///     sized to fit the new worldspace plus headroom, so within-worldspace eviction
    ///     never thrashes visible cells.
    /// </summary>
    public void LoadData(Dictionary<(int gx, int gy), CellRecord> cells)
    {
        _meshCache.Dispose();
        var capacity = Math.Max(MinCacheCapacity, cells.Count + CacheHeadroom);
        _meshCache = new CellMeshLruCache<CachedCellMesh>(capacity);
        _knownUnusableCells.Clear();
        _cells = cells;
    }

    /// <summary>
    ///     Draws every cell that intersects <paramref name="cylinder" /> — a 2D radius test in
    ///     the XY plane that ignores camera orientation entirely. Neither yaw nor pitch
    ///     rotation unloads cells; only camera translation does. Cells outside the actual view
    ///     cone are still submitted to the GPU and clipped in hardware — the tradeoff for
    ///     visual continuity across every rotation.
    ///     <para>
    ///         Visible cells are processed in ascending XY distance to the camera, so when the
    ///         per-frame upload budget is consumed it's spent on the closest cells first —
    ///         terrain fills outwards from under the camera. Any remaining budget funds an
    ///         8-neighbor idle pre-upload pass so cells about to enter the cylinder are already
    ///         cached. Returns the count of cells actually drawn.
    ///     </para>
    /// </summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        if (_cells is null || _cells.Count == 0) return 0;

        var ctx = _gpu.Context;
        UpdateConstantBuffer(ctx, _constantBuffer, new TerrainUniforms { ViewProj = viewProj });

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vertexShader);
        ctx.PSSetShader(_pixelShader);
        ctx.VSSetConstantBuffer(0, _constantBuffer);
        ctx.PSSetConstantBuffer(0, _constantBuffer);
        ctx.RSSetState(_rasterizer);
        ctx.OMSetDepthStencilState(_depthState);
        ctx.OMSetBlendState(_blendState, new Color4(1f, 1f, 1f, 1f));

        // Pass 1a — gather cells inside the wedge, with their XY distance² to the camera as
        // the sort key for the closest-first upload priority.
        _visibleScratch.Clear();
        var camX = cylinder.Position.X;
        var camY = cylinder.Position.Y;
        foreach (var (key, cell) in _cells)
        {
            if (!cylinder.ContainsCell(key.gx, key.gy)) continue;

            var cellCenterX = (key.gx + 0.5f) * WorldGridConstants.CellSize;
            var cellCenterY = (key.gy + 0.5f) * WorldGridConstants.CellSize;
            var dx = camX - cellCenterX;
            var dy = camY - cellCenterY;
            _visibleScratch.Add(new VisibleCell(key, cell, dx * dx + dy * dy));
        }

        // Closest cells first — they get the upload budget so the area under the camera fills
        // before the rim of the view does.
        _visibleScratch.Sort(ByDistanceAscending);

        // Pass 1b — build (if budget allows) + draw, in distance order.
        var uploadBudget = MaxNewUploadsPerFrame;
        var vertexStride = (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        var drawn = 0;
        foreach (var vc in _visibleScratch)
        {
            var entry = GetOrUploadMesh(vc.Key, vc.Cell, ref uploadBudget);
            if (entry is null) continue;

            ctx.IASetVertexBuffer(0, entry.VertexBuffer, vertexStride);
            ctx.IASetIndexBuffer(entry.IndexBuffer, Format.R16_UInt, 0);
            ctx.DrawIndexed((uint)entry.IndexCount, 0, 0);
            drawn++;
        }

        // Pass 2 — idle 8-neighbor pre-upload. Walks visible cells in the same closest-first
        // order, so the cells just past the leading edge of view (most likely to be needed
        // next as the camera moves) are pre-built ahead of those past the trailing edge.
        PreUploadVisibleNeighbors(ref uploadBudget);

        return drawn;
    }

    private void PreUploadVisibleNeighbors(ref int uploadBudget)
    {
        if (uploadBudget <= 0 || _cells is null) return;

        foreach (var vc in _visibleScratch)
        {
            if (uploadBudget <= 0) return;
            var (gx, gy) = vc.Key;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    if (uploadBudget <= 0) return;

                    var nkey = (gx + dx, gy + dy);
                    if (_meshCache.ContainsKey(nkey)) continue;
                    if (_knownUnusableCells.Contains(nkey)) continue;
                    if (!_cells.TryGetValue(nkey, out var ncell)) continue;

                    if (TryBuildAndCache(nkey, ncell) is not null)
                        uploadBudget--;
                }
            }
        }
    }

    private CachedCellMesh? GetOrUploadMesh((int gx, int gy) key, CellRecord cell, ref int uploadBudget)
    {
        if (_meshCache.TryGet(key, out var existing)) return existing;
        if (_knownUnusableCells.Contains(key)) return null;
        // Out of frame budget — skip this cell, try again next frame.
        if (uploadBudget <= 0) return null;

        var built = TryBuildAndCache(key, cell);
        if (built is null) return null;
        uploadBudget--;
        return built;
    }

    private CachedCellMesh? TryBuildAndCache((int gx, int gy) key, CellRecord cell)
    {
        if (!TerrainMeshBuilder.TryBuild(cell, _vertexScratch, _indexScratch))
        {
            // Remember the negative result so the builder isn't re-run every frame for a
            // cell that has no heightmap (interior cells, exterior cells with no LAND record).
            _knownUnusableCells.Add(key);
            return null;
        }

        // CreateBuffer copies the scratch contents into an immutable D3D11 buffer, so the
        // scratch is free to be reused by the next iteration.
        var vb = GpuMeshUploader.CreateVertexBuffer(_gpu.Device, _vertexScratch);
        var ib = GpuMeshUploader.CreateIndexBuffer(_gpu.Device, _indexScratch);
        var entry = new CachedCellMesh
        {
            VertexBuffer = vb,
            IndexBuffer = ib,
            IndexCount = TerrainMeshBuilder.IndexCount
        };
        _meshCache.Insert(key, entry);
        return entry;
    }

    private void UpdateConstantBuffer(ID3D11DeviceContext ctx, ID3D11Buffer buffer, TerrainUniforms uniforms)
    {
        _uniformsScratch[0] = uniforms;
        var gc = GCHandle.Alloc(_uniformsScratch, GCHandleType.Pinned);
        try
        {
            ctx.UpdateSubresource(buffer, 0, null, gc.AddrOfPinnedObject(), 0u, 0u);
        }
        finally
        {
            gc.Free();
        }
    }

    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded shader resource not found: {name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        var result = Compiler.Compile(source, entryPoint, sourceName: name, profile,
            out Blob? bytecode, out Blob? errors);

        if (result.Failure || bytecode is null)
        {
            var errorText = errors?.AsString() ?? "(no error blob)";
            errors?.Dispose();
            bytecode?.Dispose();
            throw new InvalidOperationException($"HLSL compile failed for {name} ({profile}): {errorText}");
        }

        errors?.Dispose();
        try { return bytecode.AsBytes().ToArray(); }
        finally { bytecode.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TerrainUniforms
    {
        public Matrix4x4 ViewProj;
    }

    private sealed class CachedCellMesh : IDisposable
    {
        public required ID3D11Buffer VertexBuffer { get; init; }
        public required ID3D11Buffer IndexBuffer { get; init; }
        public required int IndexCount { get; init; }

        public void Dispose()
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }
    }
}
#endif
