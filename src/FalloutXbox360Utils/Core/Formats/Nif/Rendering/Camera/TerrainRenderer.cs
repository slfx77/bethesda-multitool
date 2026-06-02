#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     v3 Phase 2b — renders textured terrain meshes for the visible exterior cells.
///     <para>
///         Each cell is drawn as 4 quadrant sub-draws against the shared cell vertex buffer.
///         Per quadrant: up to 4 LTEX-resolved diffuse textures and 3 opacity textures bound
///         to PS slots t0..t6, with per-quadrant constants written to b1 each draw. Layers
///         beyond 4 are truncated at <see cref="SelectQuadrant" /> and silently dropped.
///     </para>
///     <para>
///         Mesh vertex + index buffers are built lazily on first sight (capped by
///         <see cref="MaxNewUploadsPerFrame" /> per frame), cached LRU
///         (<see cref="CellMeshLruCache{T}" />), and disposed on eviction. The renderer does
///         NOT own the supplied <see cref="TerrainTextureResolver" /> or
///         <see cref="TerrainOpacityTextureCache" />; the host control disposes those.
///     </para>
/// </summary>
internal sealed class TerrainRenderer : IDisposable
{
    private const uint PerFrameByteSize = 64;     // float4x4 viewProj
    private const uint PerQuadrantByteSize = 16;  // float4 (origin.xy, layerCount, uvScale)
    private const uint PerModeByteSize = 16;      // float4 (debugMode.x, pad)

    /// <summary>Default world-units → texture-repeats scale: 1/256 ≈ 16 tile repeats per 4096-unit cell.</summary>
    public const float DefaultDiffuseUvScale = 1f / 256f;

    /// <summary>
    ///     Cap on new cell mesh builds + uploads per frame. Bounds the worst-case frame cost
    ///     when many cells enter the cylinder at once (rapid mouse-look, camera teleport).
    ///     Visible cells consume the budget first; whatever's left funds the 8-neighbor
    ///     idle pre-upload pass so cells about to enter view are already cached.
    /// </summary>
    private const int MaxNewUploadsPerFrame = 16;

    /// <summary>Floor on the LRU cache capacity, used before <see cref="LoadData" /> is called.</summary>
    private const int MinCacheCapacity = 1024;

    /// <summary>Spare slots over the worldspace cell count to absorb idle pre-upload.</summary>
    private const int CacheHeadroom = 256;

    private readonly GpuDevice _gpu;
    private readonly TerrainTextureResolver _textureResolver;
    private readonly TerrainOpacityTextureCache _opacityCache;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _perFrameCb;
    private readonly ID3D11Buffer _perQuadrantCb;
    private readonly ID3D11Buffer _perModeCb;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11SamplerState _diffuseSampler;
    private readonly ID3D11SamplerState _opacitySampler;
    private readonly ID3D11Buffer _sharedIndexBuffer;

    private CellMeshLruCache<CachedCellMesh> _meshCache = new(MinCacheCapacity);
    private readonly HashSet<(int gx, int gy)> _knownUnusableCells = new();
    private readonly List<VisibleCell> _visibleScratch = new();
    private readonly List<global::FalloutXbox360Utils.WorldSpatialCell> _candidateScratch = new();

    // Tracks the last SRV bound to each PS slot — skips redundant PSSetShaderResource calls
    // when adjacent quadrants share textures (typical case in WastelandNV, where one cell
    // often uses 1-2 LTEXes across all 4 quadrants). Drops the per-quadrant Set call count
    // from 7 to ~0-3 on warm runs. Reset at the top of each frame.
    private readonly ID3D11ShaderResourceView?[] _lastBoundSrvs = new ID3D11ShaderResourceView?[7];

    private readonly GpuMeshUploader.GpuVertex[] _vertexScratch = new GpuMeshUploader.GpuVertex[TerrainMeshBuilder.VertexCount];

    private readonly Matrix4x4[] _perFrameScratch = new Matrix4x4[1];
    private readonly Vector4[] _perQuadrantScratch = new Vector4[1];
    private readonly Vector4[] _perModeScratch = new Vector4[1];
    private GCHandle _perFrameScratchHandle;
    private GCHandle _perQuadrantScratchHandle;
    private GCHandle _perModeScratchHandle;

    // Per-quadrant SelectQuadrant scratch — avoids 4 List<> allocations per visible cell per frame.
    private readonly LandTextureLayer?[] _alphaScratch = new LandTextureLayer?[3];

    private Dictionary<(int gx, int gy), CellRecord>? _cells;
    private global::FalloutXbox360Utils.WorldSpatialIndex? _spatialIndex;
    private global::FalloutXbox360Utils.WorldRenderCache? _renderCache;
    private bool _vclrOnlyMode;

    private static readonly Comparison<VisibleCell> ByDistanceAscending =
        (a, b) => a.DistSq.CompareTo(b.DistSq);

    private static readonly Vector2[] QuadrantUvOrigins =
    {
        new(0.0f, 0.0f), // 0 = SW
        new(0.5f, 0.0f), // 1 = SE
        new(0.0f, 0.5f), // 2 = NW
        new(0.5f, 0.5f)  // 3 = NE
    };

    private readonly record struct VisibleCell((int gx, int gy) Key, CellRecord Cell, float DistSq);

    public TerrainRenderer(
        GpuDevice gpu,
        TerrainTextureResolver textureResolver,
        TerrainOpacityTextureCache opacityCache)
    {
        _gpu = gpu;
        _textureResolver = textureResolver;
        _opacityCache = opacityCache;

        var vsBytecode = CompileEmbeddedShader("terrain_textured.vert.hlsl", "main", "vs_5_0");
        var psBytecode = CompileEmbeddedShader("terrain_textured.frag.hlsl", "main", "ps_5_0");
        _vertexShader = gpu.Device.CreateVertexShader(vsBytecode.AsSpan());
        _pixelShader = gpu.Device.CreatePixelShader(psBytecode.AsSpan());

        _inputLayout = gpu.Device.CreateInputLayout(GpuMeshUploader.InputElements, vsBytecode.AsSpan());

        _perFrameCb = CreateCb(gpu.Device, PerFrameByteSize);
        _perQuadrantCb = CreateCb(gpu.Device, PerQuadrantByteSize);
        _perModeCb = CreateCb(gpu.Device, PerModeByteSize);

        _rasterizer = gpu.Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
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
            BlendEnable = false,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blendState = gpu.Device.CreateBlendState(blendDesc);

        _diffuseSampler = gpu.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.Anisotropic,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 4,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
            ComparisonFunc = ComparisonFunction.Never
        });

        _opacitySampler = gpu.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
            ComparisonFunc = ComparisonFunction.Never
        });

        _sharedIndexBuffer = GpuMeshUploader.CreateIndexBuffer(
            gpu.Device,
            TerrainMeshBuilder.BuildSharedIndexBufferData());

        _perFrameScratchHandle = GCHandle.Alloc(_perFrameScratch, GCHandleType.Pinned);
        _perQuadrantScratchHandle = GCHandle.Alloc(_perQuadrantScratch, GCHandleType.Pinned);
        _perModeScratchHandle = GCHandle.Alloc(_perModeScratch, GCHandleType.Pinned);
    }

    public void Dispose()
    {
        _meshCache.Dispose();
        if (_perModeScratchHandle.IsAllocated) _perModeScratchHandle.Free();
        if (_perQuadrantScratchHandle.IsAllocated) _perQuadrantScratchHandle.Free();
        if (_perFrameScratchHandle.IsAllocated) _perFrameScratchHandle.Free();
        _sharedIndexBuffer.Dispose();
        _opacitySampler.Dispose();
        _diffuseSampler.Dispose();
        _blendState.Dispose();
        _depthState.Dispose();
        _rasterizer.Dispose();
        _perModeCb.Dispose();
        _perQuadrantCb.Dispose();
        _perFrameCb.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    /// <summary>Total exterior-cell count last loaded (used for the status overlay).</summary>
    public int CellCount => _spatialIndex?.CellCount ?? _cells?.Count ?? 0;

    public global::FalloutXbox360Utils.WorldRenderStats LastStats { get; } = new();

    /// <summary>Toggles VCLR-only debug mode (Phase 2a look). Applied on next render.</summary>
    public void SetVclrOnlyMode(bool on) => _vclrOnlyMode = on;

    /// <summary>
    ///     Replaces the per-cell lookup and rebuilds the LRU mesh cache to fit the new
    ///     worldspace. Textures are resolved lazily on first sight of each quadrant — the
    ///     prior eager-warm-up path was removed because it stalled the UI thread for several
    ///     seconds on first LoadData of a large worldspace (decoding 30+ DDS files
    ///     synchronously before the first frame could render).
    /// </summary>
    public void LoadData(Dictionary<(int gx, int gy), CellRecord> cells)
        => LoadData(cells, spatialIndex: null, renderCache: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex,
        global::FalloutXbox360Utils.WorldRenderCache? renderCache)
    {
        _meshCache.Dispose();
        var capacity = Math.Max(MinCacheCapacity, cells.Count + CacheHeadroom);
        _meshCache = new CellMeshLruCache<CachedCellMesh>(capacity);
        _knownUnusableCells.Clear();
        _cells = cells;
        _spatialIndex = spatialIndex;
        _renderCache = renderCache;
    }

    /// <summary>
    ///     Draws every cell intersecting <paramref name="cylinder" />. Per cell: 4 quadrant
    ///     sub-draws with per-quadrant texture/opacity binding and constant-buffer update.
    ///     Visible cells are processed closest-first so the upload budget funds the area
    ///     under the camera before the rim. Returns the count of cells that issued at least
    ///     one quadrant draw.
    /// </summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        if ((_spatialIndex is null || _spatialIndex.CellCount == 0) &&
            (_cells is null || _cells.Count == 0))
        {
            LastStats.Reset();
            return 0;
        }

        var started = Stopwatch.GetTimestamp();
        var ctx = _gpu.Context;
        LastStats.Reset();
        _textureResolver.ResetFrameStats();
        _opacityCache.ResetFrameStats();

        var segmentStarted = Stopwatch.GetTimestamp();

        // Per-frame constants
        _perFrameScratch[0] = viewProj;
        UpdateConstantBuffer(ctx, _perFrameCb, _perFrameScratchHandle.AddrOfPinnedObject());

        // Per-mode constants (debug toggle)
        _perModeScratch[0] = new Vector4(_vclrOnlyMode ? 1f : 0f, 0f, 0f, 0f);
        UpdateConstantBuffer(ctx, _perModeCb, _perModeScratchHandle.AddrOfPinnedObject());

        // Bind shared state once outside the per-cell loop.
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vertexShader);
        ctx.PSSetShader(_pixelShader);
        ctx.VSSetConstantBuffer(0, _perFrameCb);
        ctx.PSSetConstantBuffer(0, _perFrameCb);
        ctx.VSSetConstantBuffer(1, _perQuadrantCb);
        ctx.PSSetConstantBuffer(1, _perQuadrantCb);
        ctx.PSSetConstantBuffer(2, _perModeCb);
        ctx.PSSetSampler(0, _diffuseSampler);
        ctx.PSSetSampler(1, _opacitySampler);
        ctx.RSSetState(_rasterizer);
        ctx.OMSetDepthStencilState(_depthState);
        ctx.OMSetBlendState(_blendState, new Color4(1f, 1f, 1f, 1f));
        ctx.IASetIndexBuffer(_sharedIndexBuffer, Format.R16_UInt, 0);

        LastStats.StateSetupMilliseconds = ElapsedMilliseconds(segmentStarted);
        segmentStarted = Stopwatch.GetTimestamp();

        // Pass 1a — gather visible cells with their XY distance² to camera as sort key.
        _visibleScratch.Clear();
        var camX = cylinder.Position.X;
        var camY = cylinder.Position.Y;
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryCellsInRadius(camX, -camY, cylinder.Radius, _candidateScratch);
            foreach (var candidate in _candidateScratch)
            {
                var center = candidate.CenterCanvas;
                var dx = camX - center.X;
                var dy = -camY - center.Y;
                _visibleScratch.Add(new VisibleCell(candidate.Key, candidate.Cell, dx * dx + dy * dy));
            }
        }
        else
        {
            foreach (var (key, cell) in _cells!)
            {
                if (!cylinder.ContainsCell(key.gx, key.gy)) continue;
                var cellCenterX = (key.gx + 0.5f) * WorldGridConstants.CellSize;
                var cellCenterY = (key.gy + 0.5f) * WorldGridConstants.CellSize;
                var dx = camX - cellCenterX;
                var dy = camY - cellCenterY;
                _visibleScratch.Add(new VisibleCell(key, cell, dx * dx + dy * dy));
            }
        }

        LastStats.VisibleCandidates = _visibleScratch.Count;
        LastStats.VisibleGatherMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = Stopwatch.GetTimestamp();
        _visibleScratch.Sort(ByDistanceAscending);
        LastStats.VisibleSortMilliseconds = ElapsedMilliseconds(segmentStarted);

        // Reset SRV binding cache each frame — D3D11 doesn't persist binds across frames
        // in any useful way (and we wouldn't trust caller-bound state anyway).
        for (var i = 0; i < _lastBoundSrvs.Length; i++) _lastBoundSrvs[i] = null;

        // Pass 1b — build (if budget allows) + draw, in distance order.
        segmentStarted = Stopwatch.GetTimestamp();
        var uploadBudget = MaxNewUploadsPerFrame;
        var vertexStride = (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        var drawn = 0;
        foreach (var vc in _visibleScratch)
        {
            var entry = GetOrUploadMesh(vc.Key, vc.Cell, ref uploadBudget);
            if (entry is null) continue;

            ctx.IASetVertexBuffer(0, entry.VertexBuffer, vertexStride);

            var quadrantStarted = Stopwatch.GetTimestamp();
            var anyQuadrantDrawn = DrawCellQuadrants(ctx, vc.Key, vc.Cell);
            LastStats.QuadrantDrawMilliseconds += ElapsedMilliseconds(quadrantStarted);
            if (anyQuadrantDrawn) drawn++;
        }
        LastStats.DrawLoopMilliseconds = ElapsedMilliseconds(segmentStarted);

        // Pass 2 — idle 8-neighbor pre-upload, closest-first.
        segmentStarted = Stopwatch.GetTimestamp();
        PreUploadVisibleNeighbors(ref uploadBudget);
        LastStats.NeighborPreUploadMilliseconds = ElapsedMilliseconds(segmentStarted);

        LastStats.TerrainDraws = drawn;
        LastStats.TextureCacheMisses = _textureResolver.FrameCacheMisses;
        LastStats.OpacityCacheMisses = _opacityCache.FrameCacheMisses;
        LastStats.CpuFrameMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return drawn;
    }

    /// <summary>
    ///     Issues up to 4 DrawIndexed calls (one per quadrant). A quadrant with neither BTXT
    ///     nor ATXT data still draws — bound to the white-fallback diffuse so the geometry is
    ///     visible (matches the engine's "default landscape texture" behaviour without us
    ///     having to plumb WRLD INAM here). Returns true if any quadrant was drawn.
    /// </summary>
    private bool DrawCellQuadrants(ID3D11DeviceContext ctx, (int gx, int gy) key, CellRecord cell)
    {
        var anyDrawn = false;
        for (byte q = 0; q < 4; q++)
        {
            var (baseLayer, alphaCount) = SelectQuadrant(cell, q, _alphaScratch);

            // Base SRV: BTXT if present, else first ATXT (promoted), else WhiteFallback.
            // Always emit the draw — a quadrant with no LTEX data still has VCLR-tinted
            // heightmap geometry that's better than a void.
            var baseSrv = baseLayer is not null
                ? _textureResolver.Resolve(baseLayer.TextureFormId)
                : _textureResolver.WhiteFallback;
            BindSrvIfChanged(ctx, 0, baseSrv);

            for (var i = 0; i < 3; i++)
            {
                var alpha = i < alphaCount ? _alphaScratch[i] : null;
                BindSrvIfChanged(ctx, (uint)(1 + i), alpha is null
                    ? _textureResolver.WhiteFallback
                    : _textureResolver.Resolve(alpha.TextureFormId));
            }
            for (var i = 0; i < 3; i++)
            {
                var alpha = i < alphaCount ? _alphaScratch[i] : null;
                var srv = alpha is null
                    ? _opacityCache.ZeroSrv
                    : _opacityCache.GetOrUpload(
                        new OpacityCacheKey(key.gx, key.gy, q, alpha.Layer),
                        alpha);
                BindSrvIfChanged(ctx, (uint)(4 + i), srv);
            }

            // Per-quadrant CB
            var origin = QuadrantUvOrigins[q];
            _perQuadrantScratch[0] = new Vector4(origin.X, origin.Y, 1f + alphaCount, DefaultDiffuseUvScale);
            UpdateConstantBuffer(ctx, _perQuadrantCb, _perQuadrantScratchHandle.AddrOfPinnedObject());

            ctx.DrawIndexed(
                (uint)TerrainMeshBuilder.IndicesPerQuadrant,
                (uint)(q * TerrainMeshBuilder.IndicesPerQuadrant),
                0);
            LastStats.TerrainQuadrantDraws++;
            anyDrawn = true;
        }
        return anyDrawn;
    }

    /// <summary>
    ///     Skips the PSSetShaderResource call if <paramref name="slot" /> is already bound to
    ///     <paramref name="srv" />. With ~400 quadrant draws per frame each binding 7 SRVs,
    ///     deduplication is the biggest single perf lever in the per-cell loop — adjacent
    ///     quadrants in the same cell almost always share textures.
    /// </summary>
    private void BindSrvIfChanged(ID3D11DeviceContext ctx, uint slot, ID3D11ShaderResourceView srv)
    {
        if (ReferenceEquals(_lastBoundSrvs[slot], srv)) return;
        _lastBoundSrvs[slot] = srv;
        ctx.PSSetShaderResource(slot, srv);
    }

    /// <summary>
    ///     Selects this cell's BTXT (base) + up to 3 ATXT (alpha) layers for the given
    ///     <paramref name="quadrant" />. ATXTs beyond 3 are silently dropped — the shader
    ///     supports at most 4 total layers per quadrant. If the quadrant has no BTXT but at
    ///     least one ATXT, the first ATXT is promoted to base (callers see one fewer alpha).
    ///     Writes alpha layers into <paramref name="alphaScratch" /> (must be length ≥ 3) and
    ///     returns the count.
    /// </summary>
    private static (LandTextureLayer? BaseLayer, int AlphaCount) SelectQuadrant(
        CellRecord cell, byte quadrant, LandTextureLayer?[] alphaScratch)
    {
        alphaScratch[0] = alphaScratch[1] = alphaScratch[2] = null;
        var layers = cell.LandVisualData?.TextureLayers;
        if (layers is null || layers.Count == 0) return (null, 0);

        LandTextureLayer? baseLayer = null;
        var alphaCount = 0;
        LandTextureLayer? firstAtxt = null;
        foreach (var l in layers)
        {
            if (l.Quadrant != quadrant) continue;
            if (l.Kind == LandTextureLayerKind.Base)
            {
                baseLayer = l;
            }
            else if (alphaCount < 3)
            {
                firstAtxt ??= l;
                alphaScratch[alphaCount++] = l;
            }
        }

        // No BTXT but ATXTs exist → promote the first ATXT to base. The promoted layer is
        // already in alphaScratch[0], so shift the array down and decrement alphaCount.
        if (baseLayer is null && firstAtxt is not null)
        {
            baseLayer = firstAtxt;
            alphaScratch[0] = alphaScratch[1];
            alphaScratch[1] = alphaScratch[2];
            alphaScratch[2] = null;
            alphaCount--;
        }

        return (baseLayer, alphaCount);
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
                    {
                        LastStats.NewPreUploads++;
                        uploadBudget--;
                    }
                }
            }
        }
    }

    private CachedCellMesh? GetOrUploadMesh((int gx, int gy) key, CellRecord cell, ref int uploadBudget)
    {
        if (_meshCache.TryGet(key, out var existing)) return existing;
        if (_knownUnusableCells.Contains(key)) return null;
        if (uploadBudget <= 0) return null;

        var built = TryBuildAndCache(key, cell);
        if (built is null) return null;
        uploadBudget--;
        LastStats.NewUploads++;
        return built;
    }

    private CachedCellMesh? TryBuildAndCache((int gx, int gy) key, CellRecord cell)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (!TerrainMeshBuilder.TryBuildVertices(cell, _vertexScratch, _renderCache))
            {
                _knownUnusableCells.Add(key);
                return null;
            }

            var vb = GpuMeshUploader.CreateVertexBuffer(_gpu.Device, _vertexScratch);
            var entry = new CachedCellMesh
            {
                VertexBuffer = vb
            };
            _meshCache.Insert(key, entry);
            return entry;
        }
        finally
        {
            LastStats.MeshBuildUploadMilliseconds += ElapsedMilliseconds(started);
        }
    }

    private static double ElapsedMilliseconds(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static ID3D11Buffer CreateCb(ID3D11Device device, uint byteWidth) =>
        device.CreateBuffer(new BufferDescription
        {
            ByteWidth = byteWidth,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

    private static void UpdateConstantBuffer(ID3D11DeviceContext ctx, ID3D11Buffer buffer, IntPtr source)
    {
        ctx.UpdateSubresource(buffer, 0, null, source, 0u, 0u);
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

    private sealed class CachedCellMesh : IDisposable
    {
        public required ID3D11Buffer VertexBuffer { get; init; }

        public void Dispose()
        {
            VertexBuffer.Dispose();
        }
    }
}
#endif
