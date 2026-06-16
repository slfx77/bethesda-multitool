#if WINDOWS_GUI
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     3D-5 — draws a single bright outline box around the currently-selected placed object so the
///     user can see what's selected. Modeled on <see cref="CellGridDebugRenderer12" />: line-list
///     topology, the shared <c>cellgrid</c> shaders, one per-frame CB + VB from the ring buffer, no
///     textures. Drawn LAST and <b>depth-disabled</b> so the selection stays visible even when the
///     object is behind terrain/other geometry (GECK/Creation-Kit selection behavior). The box is
///     built from the object's OBND bounds × its world matrix (so it honors placement rotation/scale),
///     with a world-space cube fallback when the object has no OBND.
/// </summary>
internal sealed class SelectionHighlightRenderer12 : IDisposable
{
    private const uint UniformsByteSize = 80; // float4x4 (64) + float4 color (16) — matches cellgrid CB
    private const uint VertexStride = 12;     // sizeof(Vector3)
    private const int BoxVertexCount = 24;    // 12 box edges × 2 verts (line list)

    // 12 edges of a box, indexing the 8 corners by (x|y<<1|z<<2) min/max bits.
    private static readonly int[] BoxEdges =
    [
        0, 1, 1, 3, 3, 2, 2, 0, // bottom face (z=min)
        4, 5, 5, 7, 7, 6, 6, 4, // top face (z=max)
        0, 4, 1, 5, 3, 7, 2, 6  // vertical edges
    ];

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly ID3D12PipelineState _pso;
    private readonly Vector3[] _verts = new Vector3[BoxVertexCount];
    private bool _hasSelection;
    private bool _disposed;

    public SelectionHighlightRenderer12(
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

        // Depth DISABLED — the selection outline must stay visible even when the object is occluded,
        // so the user can always locate the current selection (matches GECK selection brackets).
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false
        };

        var msaa = gpu.SceneSampleCount > 1;
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
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
            DestinationBlendAlpha = D12.Blend.Zero,
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
            RenderTargetFormats = new[] { Format.B8G8R8A8_UNorm },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        _pso = gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    public void ClearSelection() => _hasSelection = false;

    /// <summary>
    ///     Sets the selection box from a local-space AABB (e.g. OBND) and the object's world matrix.
    ///     The 8 corners are transformed by <paramref name="world" /> so the box honors the placement's
    ///     rotation/scale. Pass an identity matrix with world-space min/max for the no-OBND fallback.
    /// </summary>
    public void SetSelection(Vector3 localMin, Vector3 localMax, Matrix4x4 world)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
        {
            var lx = (i & 1) == 0 ? localMin.X : localMax.X;
            var ly = (i & 2) == 0 ? localMin.Y : localMax.Y;
            var lz = (i & 4) == 0 ? localMin.Z : localMax.Z;
            c[i] = Vector3.Transform(new Vector3(lx, ly, lz), world);
        }
        for (var e = 0; e < BoxEdges.Length; e++)
        {
            _verts[e] = c[BoxEdges[e]];
        }
        _hasSelection = true;
    }

    public void Render(Matrix4x4 viewProj)
    {
        if (_disposed || !_hasSelection) return;

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;

        const uint vbByteCount = (uint)BoxVertexCount * VertexStride;
        if (!_ringBuffer.TryAllocate(frameIndex, vbByteCount, out var vbAlloc, alignment: 4)) return;
        unsafe
        {
            fixed (Vector3* src = _verts)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(
                    destination: (void*)vbAlloc.CpuPtr, source: src, byteCount: vbByteCount);
            }
        }

        var cbAlloc = _ringBuffer.Allocate(frameIndex, UniformsByteSize, GpuRingBuffer12.CbAlignment);
        var uniforms = new HighlightUniforms
        {
            ViewProj = viewProj,
            LineColor = new Vector4(1.0f, 0.6f, 0.1f, 0.95f) // bright orange
        };
        unsafe { *(HighlightUniforms*)cbAlloc.CpuPtr = uniforms; }

        cmd.SetPipelineState(_pso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        cmd.IASetVertexBuffers(0, new VertexBufferView
        {
            BufferLocation = vbAlloc.GpuAddress,
            SizeInBytes = vbByteCount,
            StrideInBytes = VertexStride,
        });
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, cbAlloc.GpuAddress);
        cmd.DrawInstanced(BoxVertexCount, 1, 0, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pso.Dispose();
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
    private struct HighlightUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 LineColor;
    }
}
#endif
