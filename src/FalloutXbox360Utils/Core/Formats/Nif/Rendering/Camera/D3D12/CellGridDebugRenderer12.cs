#if WINDOWS_GUI
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using D12 = Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     v3 Pass 4 Step 2b — D3D12 port of <see cref="CellGridDebugRenderer" />. Renders
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
    private const uint UniformsByteSize = 80; // float4x4 (64) + float4 color (16) — matches D3D11 layout
    private const uint VertexStride = 12;     // sizeof(Vector3)

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuRootSignature12 _rootSignature;
    private readonly ID3D12PipelineState _pso;

    // Spike A — Pass 4 Step 4 probe. CommandSignature for one DrawInstanced indirect entry
    // (D3D12_DRAW_ARGUMENTS = 16 bytes: VertexCountPerInstance, InstanceCount,
    // StartVertexLocation, StartInstanceLocation). Validates that Vortice 3.8.2 exposes
    // CreateCommandSignature + ExecuteIndirect end-to-end before 4b/4c invest in the
    // CommandSignature factory + indirect-args plumbing for the reference renderer.
    private readonly ID3D12CommandSignature _drawIndirectSignature;

    private readonly List<(int gx, int gy)> _cells = [];
    private readonly List<(int gx, int gy)> _visibleKeyScratch = [];
    private readonly List<global::FalloutXbox360Utils.WorldSpatialCell> _visibleCellScratch = [];
    private Vector3[] _vertexScratch = [];
    private int _cellCount;
    private global::FalloutXbox360Utils.WorldSpatialIndex? _spatialIndex;
    private bool _disposed;

    public CellGridDebugRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature)
    {
        _gpu = gpu;
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _rootSignature = rootSignature;

        var vsBytecode = CompileEmbeddedShader("cellgrid.vert.hlsl", "main", "vs_5_1");
        var psBytecode = CompileEmbeddedShader("cellgrid.frag.hlsl", "main", "ps_5_1");

        var inputElements = new[]
        {
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 0, 0)
        };

        // Phase 2a wireframe behavior: drawn AFTER terrain as a debug overlay; depth
        // disabled so the grid stays on top even where terrain rises above Z=0.
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false
        };

        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None,    // lines have no winding
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            MultisampleEnable = false,
            AntialiasedLineEnable = true,
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
            DestinationBlendAlpha = D12.Blend.Zero,
            BlendOperationAlpha = D12.BlendOperation.Add,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature.RootSignature,
            VertexShader = vsBytecode,
            PixelShader = psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Line,
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
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
    public global::FalloutXbox360Utils.WorldRenderStats LastStats { get; } = new();
    public bool DetailedProfilingEnabled { get; set; }

    public void LoadData(IEnumerable<CellRecord> exteriorCells)
        => LoadData(exteriorCells, spatialIndex: null);

    public void LoadData(
        IEnumerable<CellRecord> exteriorCells,
        global::FalloutXbox360Utils.WorldSpatialIndex? spatialIndex)
    {
        _cellCount = 0;
        _spatialIndex = spatialIndex;
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
        var vbByteCount = (uint)(visibleCells * 8 * VertexStride);
        var vbAlloc = _ringBuffer.Allocate(frameIndex, vbByteCount, alignment: 4);
        WriteVertexBytes(vbAlloc.CpuPtr, vbByteCount);

        var cbAlloc = _ringBuffer.Allocate(frameIndex, UniformsByteSize, GpuRingBuffer12.CbAlignment);
        var uniforms = new CellGridUniforms
        {
            ViewProj = viewProj,
            LineColor = new Vector4(1.0f, 0.95f, 0.4f, 0.6f) // light yellow, semi-transparent
        };
        unsafe { *(CellGridUniforms*)cbAlloc.CpuPtr = uniforms; }

        // Spike A — write a D3D12_DRAW_ARGUMENTS into the ring buffer, dispatch via
        // ExecuteIndirect instead of the direct DrawInstanced. Same exact draw shape;
        // visual A/B with the prior frame should be pixel-identical.
        // Args alignment: D3D12 requires 4-byte alignment for indirect arg buffers; the
        // struct's natural alignment of 4 suffices.
        var argAlloc = _ringBuffer.Allocate(frameIndex, 16, alignment: 4);
        unsafe
        {
            *(DrawArguments*)argAlloc.CpuPtr = new DrawArguments(
                vertexCountPerInstance: (uint)(visibleCells * 8),
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
        var requiredVertices = requestedCells * 8;
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
