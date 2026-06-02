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
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     v3 Phase 1 debug visualization. Renders each exterior cell as a yellow line-list
///     square at Z=0 (the natural ground reference). Used to verify camera + transform +
///     frustum culling work end-to-end before Phases 2–3 add terrain + REFR meshes.
///     <para>
///         All 4 corner vertices per cell are uploaded once at <see cref="LoadData" /> in
///         line-list pair order (8 vertices per cell). Per frame, the renderer issues one
///         draw for the whole grid and lets the GPU clip off-screen lines; this keeps the
///         debug layer cheap even on very dense worldspaces.
///     </para>
/// </summary>
internal sealed class CellGridDebugRenderer : IDisposable
{
    private const uint UniformsByteSize = 80; // float4x4 (64) + float4 color (16)

    private readonly GpuDevice _gpu;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11BlendState _blendState;

    private ID3D11Buffer? _vertexBuffer;
    private readonly List<(int gx, int gy)> _cells = [];
    private readonly List<(int gx, int gy)> _visibleKeyScratch = [];
    private readonly List<global::FalloutXbox360Utils.WorldSpatialCell> _visibleCellScratch = [];
    private Vector3[] _vertexScratch = [];
    private GCHandle _vertexScratchHandle;
    private int _vertexCellCapacity;
    private int _cellCount;
    private global::FalloutXbox360Utils.WorldSpatialIndex? _spatialIndex;

    // Single-element array reused for UpdateSubresource each frame to avoid a per-frame
    // allocation. Small win on its own but part of a broader pass that keeps the render
    // loop allocation-free at steady state.
    private readonly CellGridUniforms[] _uniformsScratch = new CellGridUniforms[1];
    private GCHandle _uniformsScratchHandle;

    public CellGridDebugRenderer(GpuDevice gpu)
    {
        _gpu = gpu;

        var vsBytecode = CompileEmbeddedShader("cellgrid.vert.hlsl", "main", "vs_5_0");
        var psBytecode = CompileEmbeddedShader("cellgrid.frag.hlsl", "main", "ps_5_0");
        _vertexShader = gpu.Device.CreateVertexShader(vsBytecode.AsSpan());
        _pixelShader = gpu.Device.CreatePixelShader(psBytecode.AsSpan());

        var inputElements = new[]
        {
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 0, 0)
        };
        _inputLayout = gpu.Device.CreateInputLayout(inputElements, vsBytecode.AsSpan());

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
            CullMode = CullMode.None, // lines have no winding
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = true
        });

        // Phase 2a: the wireframe is now drawn AFTER terrain as a debug overlay. With the
        // swapchain surface now owning a DSV, an enabled depth test would hide the wireframe
        // wherever terrain rises above Z=0. Disable depth so the wireframe stays on top — same
        // visual behavior as Phase 1 (which got it for free since no DSV was bound).
        _depthState = gpu.Device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
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

    public void Dispose()
    {
        _vertexBuffer?.Dispose();
        if (_vertexScratchHandle.IsAllocated) _vertexScratchHandle.Free();
        if (_uniformsScratchHandle.IsAllocated) _uniformsScratchHandle.Free();
        _blendState.Dispose();
        _depthState.Dispose();
        _rasterizer.Dispose();
        _constantBuffer.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    /// <summary>Total exterior-cell count last loaded (used for the status overlay).</summary>
    public int CellCount => _cellCount;

    public global::FalloutXbox360Utils.WorldRenderStats LastStats { get; } = new();

    /// <summary>
    ///     Rebuilds the cell-grid vertex buffer from the supplied exterior cells. Call once per
    ///     LoadData on the host control; the buffer is reused across frames.
    /// </summary>
    public void LoadData(IEnumerable<CellRecord> exteriorCells)
        => LoadData(exteriorCells, spatialIndex: null);

    public void LoadData(
        IEnumerable<CellRecord> exteriorCells,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex)
    {
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        if (_vertexScratchHandle.IsAllocated) _vertexScratchHandle.Free();
        _vertexScratch = [];
        _vertexCellCapacity = 0;
        _cellCount = 0;
        _spatialIndex = spatialIndex;
        _cells.Clear();

        var seen = new HashSet<(int gx, int gy)>();
        foreach (var cell in exteriorCells)
        {
            if (cell.GridX is not int gx || cell.GridY is not int gy) continue;
            var key = (gx, gy);
            if (!seen.Add(key)) continue; // dedupe; later DLC overrides keep first
            _cells.Add(key);
        }

        _cellCount = _cells.Count;
        if (_cellCount == 0) return;

        EnsureVertexCapacity(Math.Min(_cellCount, 1024));
    }

    /// <summary>
    ///     Issues one line-list draw for cells inside <paramref name="cylinder" />. Earlier
    ///     versions submitted the whole grid and relied on GPU clipping, but that made the
    ///     debug layer dominate large worldspaces even when terrain was distance-limited.
    /// </summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        LastStats.Reset();
        if (_cellCount == 0) return 0;

        var started = Stopwatch.GetTimestamp();
        var segmentStarted = started;
        var visibleCells = GatherVisibleCells(cylinder);
        LastStats.VisibleCandidates = visibleCells;
        LastStats.VisibleGatherMilliseconds = ElapsedMilliseconds(segmentStarted);
        if (visibleCells == 0)
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }

        segmentStarted = Stopwatch.GetTimestamp();
        EnsureVertexCapacity(visibleCells);
        LastStats.ResourceResizeMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = Stopwatch.GetTimestamp();
        WriteVisibleVertices(cylinder, visibleCells);
        LastStats.InstanceBuildMilliseconds = ElapsedMilliseconds(segmentStarted);

        var ctx = _gpu.Context;

        segmentStarted = Stopwatch.GetTimestamp();
        ctx.UpdateSubresource(
            _vertexBuffer!,
            0,
            null,
            _vertexScratchHandle.AddrOfPinnedObject(),
            0u,
            0u);
        LastStats.GpuUploadMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = Stopwatch.GetTimestamp();
        // Upload uniforms (viewProj + line color) — same matrix-byte semantics as skin shaders.
        var uniforms = new CellGridUniforms
        {
            ViewProj = viewProj,
            LineColor = new Vector4(1.0f, 0.95f, 0.4f, 0.6f) // light yellow, semi-transparent
        };
        UpdateConstantBuffer(ctx, _constantBuffer, uniforms);

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, (uint)Marshal.SizeOf<Vector3>());
        ctx.VSSetShader(_vertexShader);
        ctx.PSSetShader(_pixelShader);
        ctx.VSSetConstantBuffer(0, _constantBuffer);
        ctx.PSSetConstantBuffer(0, _constantBuffer);
        ctx.RSSetState(_rasterizer);
        ctx.OMSetDepthStencilState(_depthState);
        ctx.OMSetBlendState(_blendState, new Color4(1f, 1f, 1f, 1f));

        ctx.Draw((uint)(visibleCells * 8), 0);
        LastStats.DrawCallMilliseconds = ElapsedMilliseconds(segmentStarted);
        LastStats.WireframeDraws = visibleCells;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return visibleCells;
    }

    private int GatherVisibleCells(VisibilityCylinder cylinder)
    {
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryCellsInRadius(
                cylinder.Position.X,
                -cylinder.Position.Y,
                cylinder.Radius,
                _visibleCellScratch);
            return _visibleCellScratch.Count;
        }

        _visibleKeyScratch.Clear();
        foreach (var key in _cells)
        {
            if (cylinder.ContainsCell(key.gx, key.gy))
            {
                _visibleKeyScratch.Add(key);
            }
        }
        return _visibleKeyScratch.Count;
    }

    private void WriteVisibleVertices(VisibilityCylinder cylinder, int visibleCells)
    {
        if (_spatialIndex is not null)
        {
            for (var i = 0; i < visibleCells; i++)
            {
                WriteCellVertices(i, _visibleCellScratch[i].Key);
            }
        }
        else
        {
            _ = cylinder;
            for (var i = 0; i < visibleCells; i++)
            {
                WriteCellVertices(i, _visibleKeyScratch[i]);
            }
        }
    }

    private void WriteCellVertices(int cellIndex, (int gx, int gy) key)
    {
        var (gx, gy) = key;
        var x0 = gx * WorldGridConstants.CellSize;
        var y0 = gy * WorldGridConstants.CellSize;
        var x1 = x0 + WorldGridConstants.CellSize;
        var y1 = y0 + WorldGridConstants.CellSize;
        const float z = 0f;
        var baseIdx = cellIndex * 8;
        _vertexScratch[baseIdx + 0] = new Vector3(x0, y0, z);
        _vertexScratch[baseIdx + 1] = new Vector3(x1, y0, z);
        _vertexScratch[baseIdx + 2] = new Vector3(x1, y0, z);
        _vertexScratch[baseIdx + 3] = new Vector3(x1, y1, z);
        _vertexScratch[baseIdx + 4] = new Vector3(x1, y1, z);
        _vertexScratch[baseIdx + 5] = new Vector3(x0, y1, z);
        _vertexScratch[baseIdx + 6] = new Vector3(x0, y1, z);
        _vertexScratch[baseIdx + 7] = new Vector3(x0, y0, z);
    }

    private void EnsureVertexCapacity(int requestedCells)
    {
        if (requestedCells <= _vertexCellCapacity && _vertexBuffer is not null)
        {
            return;
        }

        if (_vertexScratchHandle.IsAllocated) _vertexScratchHandle.Free();
        _vertexBuffer?.Dispose();

        var capacity = Math.Max(1, requestedCells);
        _vertexCellCapacity = capacity;
        _vertexScratch = new Vector3[capacity * 8];
        _vertexScratchHandle = GCHandle.Alloc(_vertexScratch, GCHandleType.Pinned);

        var byteWidth = (uint)(_vertexScratch.Length * Marshal.SizeOf<Vector3>());
        _vertexBuffer = _gpu.Device.CreateBuffer(new BufferDescription
        {
            ByteWidth = byteWidth,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.VertexBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
            StructureByteStride = 0
        });
    }

    private static double ElapsedMilliseconds(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private void UpdateConstantBuffer(ID3D11DeviceContext ctx, ID3D11Buffer buffer, CellGridUniforms uniforms)
    {
        _uniformsScratch[0] = uniforms;
        ctx.UpdateSubresource(buffer, 0, null, _uniformsScratchHandle.AddrOfPinnedObject(), 0u, 0u);
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
    private struct CellGridUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 LineColor;
    }
}
#endif
