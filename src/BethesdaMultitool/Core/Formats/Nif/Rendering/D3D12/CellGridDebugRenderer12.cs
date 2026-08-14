#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     v3 Pass 4 Step 2b — D3D12 port of <c>CellGridDebugRenderer</c>. Renders
///     the yellow cell-grid wireframe overlay (D1 toggle).
///     <para>
///         Simplest of the four renderer ports: line list topology, single PSO, single
///         per-frame CB via the ring buffer, no textures, no instancing. Serves as the
///         end-to-end validator of the PSO + root signature + ring buffer + command list
///         pipeline before the more complex renderers (terrain, references, water) port.
///     </para>
///     <para>
///         Both the constant buffer AND the vertex data are written into the shared
///         <see cref="GpuRingBuffer12" /> each frame — no per-renderer vertex buffer
///         resource is required since the wireframe geometry is rebuilt every frame
///         from the visible cell set. The ring buffer's per-frame partition prevents
///         CPU/GPU contention on the same memory.
///     </para>
/// </summary>
internal sealed class CellGridDebugRenderer12
    : Abstractions.ICellGridRenderer
{
    private const uint UniformsByteSize = 80; // float4x4 (64) + float4 color (16) — matches the shader cbuffer
    private const uint VertexStride = 12;     // sizeof(Vector3)

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly ID3D12PipelineState _pso;

    // CommandSignature for one DrawInstanced indirect entry
    // (D3D12_DRAW_ARGUMENTS = 16 bytes: VertexCountPerInstance, InstanceCount,
    // StartVertexLocation, StartInstanceLocation). Validates that Vortice 3.8.2 exposes
    // CreateCommandSignature + ExecuteIndirect end-to-end before building out the
    // CommandSignature factory + indirect-args plumbing for the reference renderer.
    private readonly ID3D12CommandSignature _drawIndirectSignature;

    private readonly List<(int gx, int gy)> _cells = [];
    private readonly List<(int gx, int gy)> _visibleKeyScratch = [];
    private readonly List<global::BethesdaMultitool.WorldSpatialCell> _visibleCellScratch = [];
    private Vector3[] _vertexScratch = [];
    private int _cellCount;
    private global::BethesdaMultitool.WorldSpatialIndex? _spatialIndex;
    // Active worldspace's cell-edge size (4096 Fallout-family, 8192 Morrowind), taken from the spatial
    // index on load so the wireframe sits on the real cell boundaries (and the wall squares stay square).
    private float _cellSize = WorldGridConstants.CellSize;
    private bool _disposed;

    // World vertical extent spanned by the line cage. Horizontal rings every CellSize visually divide
    // its vertical posts into cell-sized squares; there are no filled wall faces. _zMin/_zMax are
    // snapped to CellSize multiples in SetWorldZExtent so the horizontal lines sit at consistent world
    // heights across the whole grid. Defaults to z=0..CellSize (2 levels → 24 verts/cell) until the
    // control supplies a finite extent for this load.
    private float _zMin;
    private float _zMax = WorldGridConstants.CellSize;
    private int _horizontalLevels = 2;     // number of horizontal square rings (z planes)
    private int _verticesPerCell = 24;     // _horizontalLevels * 8 (rings) + 8 (4 vertical corner posts)
    // Bound the per-cell vertex count for very tall worldspaces so the per-frame ring-buffer allocation
    // stays sane. Walls taller than this keep cell-sized rings up to the cap; the corner posts still span
    // the full extent.
    private const int MaxHorizontalLevels = 16;

    public CellGridDebugRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature)
    {
        _recorder = recorder;
        _ringBuffer = ringBuffer;

        var vsBytecode = CompileEmbeddedShader("cellgrid.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("cellgrid.frag.hlsl", "main", "ps_5_1");

        var inputElements = new[]
        {
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 0, 0)
        };

        // 3D-1/3D-2: the grid is now a full-height 3D line cage, so it must be OCCLUDED by terrain
        // and objects to read as part of the 3D scene (depth-disabled, it just painted over the
        // terrain — "rendered over it, not in the same space"). Depth-TEST against the scene depth
        // (terrain + opaque refs already wrote it) but never WRITE (read-only, like the navmesh
        // overlay) so it can't corrupt later layers' occlusion. Reversed-Z: clear = 0 (far),
        // near = 1, so the visible-if-closer test is GreaterEqual.
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.GreaterEqual,
            StencilEnable = false
        };

        var msaa = gpu.SceneSampleCount > 1;
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None,    // lines have no winding
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            // Under MSAA, multisample coverage antialiases the grid lines (robust to the SDR->HDR
            // display curve); fall back to fixed-function line AA only when MSAA is unavailable.
            MultisampleEnable = msaa,
            AntialiasedLineEnable = !msaa,
        };

        var blend = new D12.BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = D12.Blend.SourceAlpha,
            DestinationBlend = D12.Blend.InverseSourceAlpha,
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            // Preserve destination coverage for transparent PNG/export targets. One/Zero replaced the
            // whole target alpha with this overlay's 0.6 even though RGB already used source-over.
            DestinationBlendAlpha = D12.Blend.InverseSourceAlpha,
            BlendOperationAlpha = D12.BlendOperation.Add,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Line,
            RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        _pso = gpu.Device.CreateGraphicsPipelineState(psoDesc);

        var indirectArgs = new IndirectArgumentDescription[1];
        indirectArgs[0].Type = IndirectArgumentType.Draw;
        var sigDesc = new CommandSignatureDescription
        {
            ByteStride = 16,                  // sizeof(D3D12_DRAW_ARGUMENTS) = 4 × uint
            IndirectArguments = indirectArgs,
        };
        // rootSignature: null because the signature contains no root-arg changes — only the
        // Draw arg itself. That allows reuse across any PSO bound at execute time.
        _drawIndirectSignature = gpu.Device.CreateCommandSignature<ID3D12CommandSignature>(sigDesc, rootSignature: null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _drawIndirectSignature.Dispose();
        _pso.Dispose();
    }

    public int CellCount => _cellCount;
    public global::BethesdaMultitool.WorldRenderStats LastStats { get; } = new();
    public bool DetailedProfilingEnabled { get; set; }

    public void LoadData(IEnumerable<CellRecord> exteriorCells)
        => LoadData(exteriorCells, spatialIndex: null);

    public void LoadData(
        IEnumerable<CellRecord> exteriorCells,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex)
    {
        _cellCount = 0;
        _spatialIndex = spatialIndex;
        _cellSize = spatialIndex?.CellSize ?? WorldGridConstants.CellSize;
        ResetWorldZExtent();
        _cells.Clear();
        _vertexScratch = [];

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

    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        LastStats.Reset();
        if (_cellCount == 0) return 0;

        var started = StartTiming();
        var segmentStarted = started;
        var visibleCells = GatherVisibleCells(cylinder);
        LastStats.VisibleCandidates = visibleCells;
        LastStats.VisibleGatherMilliseconds = ElapsedMilliseconds(segmentStarted);
        if (visibleCells == 0)
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }

        segmentStarted = StartTiming();
        EnsureVertexCapacity(visibleCells);
        LastStats.ResourceResizeMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartTiming();
        WriteVisibleVertices(cylinder, visibleCells);
        LastStats.InstanceBuildMilliseconds = ElapsedMilliseconds(segmentStarted);

        // Both CB and VB land in the per-frame ring partition. CB needs 256-byte alignment
        // (D3D12 CBV spec); VB only needs 4-byte (matches Vector3's natural alignment).
        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;

        segmentStarted = StartTiming();
        var vbByteCount = (uint)(visibleCells * _verticesPerCell * VertexStride);
        if (!_ringBuffer.TryAllocate(frameIndex, vbByteCount, out var vbAlloc, alignment: 4))
        {
            // Grid too large for the per-frame ring partition this frame (very tall worldspace + huge
            // render distance). It's a non-essential debug overlay — skip it this frame instead of
            // throwing, and let later frames retry as the visible set changes.
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        WriteVertexBytes(vbAlloc.CpuPtr, vbByteCount);

        // Same soft-fail as the vertex block above — non-essential overlay, retry next frame.
        if (!_ringBuffer.TryAllocate(frameIndex, UniformsByteSize, out var cbAlloc, GpuRingBuffer12.CbAlignment))
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        var uniforms = new CellGridUniforms
        {
            ViewProj = viewProj,
            LineColor = new Vector4(1.0f, 0.95f, 0.4f, 0.6f) // light yellow, semi-transparent
        };
        unsafe { *(CellGridUniforms*)cbAlloc.CpuPtr = uniforms; }

        // Write a D3D12_DRAW_ARGUMENTS into the ring buffer, dispatch via
        // ExecuteIndirect instead of the direct DrawInstanced. Same exact draw shape,
        // so the result should be pixel-identical to the direct-draw path.
        // Args alignment: D3D12 requires 4-byte alignment for indirect arg buffers; the
        // struct's natural alignment of 4 suffices.
        if (!_ringBuffer.TryAllocate(frameIndex, 16, out var argAlloc, alignment: 4))
        {
            LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
            return 0;
        }
        unsafe
        {
            *(DrawArguments*)argAlloc.CpuPtr = new DrawArguments(
                vertexCountPerInstance: (uint)(visibleCells * _verticesPerCell),
                instanceCount: 1,
                startVertexLocation: 0,
                startInstanceLocation: 0);
        }
        LastStats.GpuUploadMilliseconds = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartTiming();
        cmd.SetPipelineState(_pso);
        cmd.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.LineList);
        cmd.IASetVertexBuffers(0, new VertexBufferView
        {
            BufferLocation = vbAlloc.GpuAddress,
            SizeInBytes = vbByteCount,
            StrideInBytes = VertexStride,
        });
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, cbAlloc.GpuAddress);
        cmd.ExecuteIndirect(
            commandSignature: _drawIndirectSignature,
            maxCommandCount: 1,
            argumentBuffer: _ringBuffer.GetFrameBuffer(frameIndex),
            argumentBufferOffset: argAlloc.ByteOffset,
            countBuffer: null,
            countBufferOffset: 0);
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
        var x0 = gx * _cellSize;
        var y0 = gy * _cellSize;
        var x1 = x0 + _cellSize;
        var y1 = y0 + _cellSize;
        var idx = cellIndex * _verticesPerCell;

        // Horizontal square rings at each z level (z = _zMin + k*cellSize). Together with the corner
        // posts these form a full 3D wireframe grid rather than just top/bottom edges.
        for (var k = 0; k < _horizontalLevels; k++)
        {
            var z = _zMin + k * _cellSize;
            _vertexScratch[idx++] = new Vector3(x0, y0, z);
            _vertexScratch[idx++] = new Vector3(x1, y0, z);
            _vertexScratch[idx++] = new Vector3(x1, y0, z);
            _vertexScratch[idx++] = new Vector3(x1, y1, z);
            _vertexScratch[idx++] = new Vector3(x1, y1, z);
            _vertexScratch[idx++] = new Vector3(x0, y1, z);
            _vertexScratch[idx++] = new Vector3(x0, y1, z);
            _vertexScratch[idx++] = new Vector3(x0, y0, z);
        }

        // Four vertical corner posts spanning the full extent (_zMin → _zMax).
        _vertexScratch[idx++] = new Vector3(x0, y0, _zMin);
        _vertexScratch[idx++] = new Vector3(x0, y0, _zMax);
        _vertexScratch[idx++] = new Vector3(x1, y0, _zMin);
        _vertexScratch[idx++] = new Vector3(x1, y0, _zMax);
        _vertexScratch[idx++] = new Vector3(x1, y1, _zMin);
        _vertexScratch[idx++] = new Vector3(x1, y1, _zMax);
        _vertexScratch[idx++] = new Vector3(x0, y1, _zMin);
        _vertexScratch[idx] = new Vector3(x0, y1, _zMax);
    }

    /// <summary>
    ///     Sets the world vertical extent (Z) the line cage spans, snapping to CellSize multiples and
    ///     computing the number of horizontal ring levels (one every CellSize). Called from the control
    ///     on load with the loaded worldspace's floor/ceiling so the columns reach the bottom and top of
    ///     the world and form cell-sized squares up the walls instead of floating one cell tall at z=0.
    /// </summary>
    public void SetWorldZExtent(float zMin, float zMax)
    {
        if (!float.IsFinite(zMin) || !float.IsFinite(zMax) || zMax <= zMin) return;

        var cell = _cellSize;
        _zMin = MathF.Floor(zMin / cell) * cell;
        _zMax = MathF.Ceiling(zMax / cell) * cell;

        var levels = (int)MathF.Round((_zMax - _zMin) / cell) + 1; // inclusive of both ends
        _horizontalLevels = Math.Clamp(levels, 2, MaxHorizontalLevels);
        _verticesPerCell = _horizontalLevels * 8 + 8;
    }

    /// <summary>
    ///     Clears world-specific Z state on every load. If the new cell set has no finite placed-object
    ///     census, the control deliberately does not call <see cref="SetWorldZExtent" /> and this
    ///     load-local default must win instead of retaining the prior worldspace's height range.
    /// </summary>
    private void ResetWorldZExtent()
    {
        _zMin = 0f;
        _zMax = _cellSize;
        _horizontalLevels = 2;
        _verticesPerCell = 24;
    }

    private void EnsureVertexCapacity(int requestedCells)
    {
        var requiredVertices = requestedCells * _verticesPerCell;
        if (requiredVertices <= _vertexScratch.Length) return;
        // No GPU resource to recreate (vertices land in the ring buffer at render time).
        _vertexScratch = new Vector3[Math.Max(1, requiredVertices)];
    }

    private unsafe void WriteVertexBytes(IntPtr destination, uint byteCount)
    {
        fixed (Vector3* src = _vertexScratch)
        {
            System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(
                destination: (void*)destination,
                source: src,
                byteCount: byteCount);
        }
    }

    private long StartTiming() => DetailedProfilingEnabled ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>
    ///     Forwards to the one shared compiler — see <see cref="GpuShaderCompiler12" />.
    ///     This was one of a dozen copy-pasted private compilers that had drifted apart on
    ///     shader flags and manifest lookup; the flag decision is now made once, unconditionally.
    /// </summary>
    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile);

    [StructLayout(LayoutKind.Sequential)]
    private struct CellGridUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 LineColor;
    }
}
#endif
