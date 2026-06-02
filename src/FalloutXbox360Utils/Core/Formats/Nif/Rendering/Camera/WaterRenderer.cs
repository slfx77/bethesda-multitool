#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     v3 Phase 2a — draws a flat alpha-blended water quad per visible cell whose water
///     height resolves to a non-sentinel value. No vertex buffer is bound; the 6 quad
///     corners are expanded from <c>SV_VertexID</c> in <c>water.vert.hlsl</c>.
///     <para>
///         Water height per cell is the first non-null, non-sentinel value of: the cell's
///         own <see cref="CellRecord.WaterHeight" /> → the worldspace's <c>DefaultWaterHeight</c>
///         passed to <see cref="LoadData" />. Cells where both resolve to <c>null</c> or
///         the no-water sentinel are silently skipped.
///     </para>
/// </summary>
internal sealed class WaterRenderer : IDisposable
{
    private const uint UniformsByteSize = 64; // float4x4

    private static readonly Vector4 DefaultWaterColor = new(0.118f, 0.216f, 0.471f, 0.65f);

    private readonly GpuDevice _gpu;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11BlendState _blendState;

    private readonly List<global::FalloutXbox360Utils.WorldWaterCell> _waterCells = new();
    private readonly List<global::FalloutXbox360Utils.WorldWaterCell> _visibleWaterScratch = new();
    private float? _worldspaceDefaultWaterHeight;
    private global::FalloutXbox360Utils.WorldSpatialIndex? _spatialIndex;
    private ID3D11Buffer? _instanceBuffer;
    private ID3D11ShaderResourceView? _instanceSrv;
    private WaterInstance[] _instanceScratch = [];
    private GCHandle _instanceScratchHandle;

    // Single-element array reused for the per-cell UpdateSubresource. Per-cell allocation
    // would otherwise tally to ~200 visible water cells × 96 bytes × 60 Hz ≈ 1.1 MB/sec of
    // garbage and contribute to the Gen 2 GC pressure that shows up as a periodic hitch on
    // large worldspaces.
    private readonly WaterUniforms[] _uniformsScratch = new WaterUniforms[1];
    private GCHandle _uniformsScratchHandle;

    public WaterRenderer(GpuDevice gpu)
    {
        _gpu = gpu;

        var vsBytecode = CompileEmbeddedShader("water.vert.hlsl", "main", "vs_5_0");
        var psBytecode = CompileEmbeddedShader("water.frag.hlsl", "main", "ps_5_0");
        _vertexShader = gpu.Device.CreateVertexShader(vsBytecode.AsSpan());
        _pixelShader = gpu.Device.CreatePixelShader(psBytecode.AsSpan());

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
            CullMode = CullMode.None, // flat plane, both faces visible (looking up through water from underneath is valid)
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false
        });

        // Read depth so terrain occludes water that's below the ground; don't WRITE depth so
        // the layer order (terrain → water → wireframe) stays sane and water doesn't punch
        // the wireframe out of the depth buffer when it draws on top.
        _depthState = gpu.Device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Less,
            StencilEnable = false
        });

        var blendDesc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        blendDesc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.Zero,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blendState = gpu.Device.CreateBlendState(blendDesc);

        _uniformsScratchHandle = GCHandle.Alloc(_uniformsScratch, GCHandleType.Pinned);
    }

    public global::FalloutXbox360Utils.WorldRenderStats LastStats { get; } = new();

    public void Dispose()
    {
        if (_instanceScratchHandle.IsAllocated) _instanceScratchHandle.Free();
        _instanceSrv?.Dispose();
        _instanceBuffer?.Dispose();
        if (_uniformsScratchHandle.IsAllocated) _uniformsScratchHandle.Free();
        _blendState.Dispose();
        _depthState.Dispose();
        _rasterizer.Dispose();
        _constantBuffer.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight)
        => LoadData(cells, worldspaceDefaultWaterHeight, spatialIndex: null);

    public void LoadData(
        Dictionary<(int gx, int gy), CellRecord> cells,
        float? worldspaceDefaultWaterHeight,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex)
    {
        _worldspaceDefaultWaterHeight = worldspaceDefaultWaterHeight;
        _spatialIndex = spatialIndex;
        _waterCells.Clear();

        if (spatialIndex is not null)
        {
            _waterCells.AddRange(spatialIndex.WaterCells);
        }
        else
        {
            foreach (var (key, cell) in cells)
            {
                if (ResolveWaterHeight(cell) is float z)
                    _waterCells.Add(new global::FalloutXbox360Utils.WorldWaterCell(key, cell, z));
            }
        }

        EnsureInstanceCapacity(_waterCells.Count);
    }

    /// <summary>
    ///     Draws one quad per cell within <paramref name="cylinder" /> that has a valid
    ///     (non-sentinel) water height. Cylinder culling matches the terrain renderer —
    ///     neither rotation direction changes which water quads are emitted; only translation
    ///     does. Returns the count of drawn quads for the host HUD.
    /// </summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        LastStats.Reset();
        if (_waterCells.Count == 0) return 0;

        var started = Stopwatch.GetTimestamp();
        var segmentStarted = started;
        var ctx = _gpu.Context;

        ctx.IASetInputLayout(null); // no vertex buffer; VS reads SV_VertexID directly
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vertexShader);
        ctx.PSSetShader(_pixelShader);
        ctx.VSSetConstantBuffer(0, _constantBuffer);
        ctx.PSSetConstantBuffer(0, _constantBuffer);
        if (_instanceSrv is not null)
        {
            ctx.VSSetShaderResource(0, _instanceSrv);
        }
        ctx.RSSetState(_rasterizer);
        ctx.OMSetDepthStencilState(_depthState);
        ctx.OMSetBlendState(_blendState, new Color4(1f, 1f, 1f, 1f));
        LastStats.StateSetupMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = Stopwatch.GetTimestamp();
        var visible = GatherVisibleWater(cylinder);
        LastStats.VisibleCandidates = visible;
        LastStats.VisibleGatherMilliseconds = ElapsedMilliseconds(segmentStarted);
        if (visible == 0)
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }

        segmentStarted = Stopwatch.GetTimestamp();
        EnsureInstanceCapacity(visible);
        LastStats.ResourceResizeMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = Stopwatch.GetTimestamp();
        for (var i = 0; i < visible; i++)
        {
            var water = _visibleWaterScratch[i];
            var key = water.Key;
            _instanceScratch[i] = new WaterInstance
            {
                CellOriginAndWater = new Vector4(
                    key.gx * WorldGridConstants.CellSize,
                    key.gy * WorldGridConstants.CellSize,
                    water.Height,
                    WorldGridConstants.CellSize),
                Color = DefaultWaterColor
            };
        }
        LastStats.InstanceBuildMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = Stopwatch.GetTimestamp();
        UpdateConstantBuffer(ctx, _constantBuffer, new WaterUniforms { ViewProj = viewProj });
        UpdateInstanceBuffer(ctx, visible);
        LastStats.GpuUploadMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = Stopwatch.GetTimestamp();
        ctx.DrawInstanced(6, (uint)visible, 0, 0);
        LastStats.DrawCallMilliseconds = ElapsedMilliseconds(segmentStarted);
        LastStats.WaterDraws = visible;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return visible;
    }

    private int GatherVisibleWater(VisibilityCylinder cylinder)
    {
        _visibleWaterScratch.Clear();
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryWaterCellsInRadius(
                cylinder.Position.X,
                -cylinder.Position.Y,
                cylinder.Radius,
                _visibleWaterScratch);
            return _visibleWaterScratch.Count;
        }

        foreach (var water in _waterCells)
        {
            var key = water.Key;
            if (cylinder.ContainsCell(key.gx, key.gy))
            {
                _visibleWaterScratch.Add(water);
            }
        }

        return _visibleWaterScratch.Count;
    }

    private float? ResolveWaterHeight(CellRecord cell)
    {
        // Cell XCLW wins when present and not the "no water" sentinel. Otherwise fall back to
        // the worldspace DNAM default (same fallback chain the 2D heightmap renderer uses).
        if (cell.WaterHeight is float cellHeight && !WorldHeightNormalizer.IsNoWaterSentinel(cellHeight))
            return cellHeight;

        if (_worldspaceDefaultWaterHeight is float worldHeight && !WorldHeightNormalizer.IsNoWaterSentinel(worldHeight))
            return worldHeight;

        return null;
    }

    private void UpdateConstantBuffer(ID3D11DeviceContext ctx, ID3D11Buffer buffer, WaterUniforms uniforms)
    {
        _uniformsScratch[0] = uniforms;
        ctx.UpdateSubresource(buffer, 0, null, _uniformsScratchHandle.AddrOfPinnedObject(), 0u, 0u);
    }

    private void UpdateInstanceBuffer(ID3D11DeviceContext ctx, int instanceCount)
    {
        if (_instanceBuffer is null || instanceCount == 0)
        {
            return;
        }

        ctx.UpdateSubresource(_instanceBuffer, 0, null, _instanceScratchHandle.AddrOfPinnedObject(), 0u, 0u);
    }

    private void EnsureInstanceCapacity(int requested)
    {
        if (requested <= _instanceScratch.Length && _instanceBuffer is not null && _instanceSrv is not null)
        {
            return;
        }

        if (_instanceScratchHandle.IsAllocated) _instanceScratchHandle.Free();
        _instanceSrv?.Dispose();
        _instanceBuffer?.Dispose();
        _instanceSrv = null;
        _instanceBuffer = null;

        var capacity = Math.Max(1, requested);
        _instanceScratch = new WaterInstance[capacity];
        _instanceScratchHandle = GCHandle.Alloc(_instanceScratch, GCHandleType.Pinned);
        var byteWidth = (uint)(capacity * Marshal.SizeOf<WaterInstance>());
        _instanceBuffer = _gpu.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = byteWidth,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = (uint)Marshal.SizeOf<WaterInstance>()
        });
        _instanceSrv = _gpu.Device.CreateShaderResourceView(_instanceBuffer);
    }

    private static double ElapsedMilliseconds(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds;

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
    private struct WaterUniforms
    {
        public Matrix4x4 ViewProj;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaterInstance
    {
        public Vector4 CellOriginAndWater;
        public Vector4 Color;
    }
}
#endif
