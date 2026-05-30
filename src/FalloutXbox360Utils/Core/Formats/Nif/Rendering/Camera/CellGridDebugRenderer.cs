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
///     v3 Phase 1 debug visualization. Renders each exterior cell as a yellow line-list
///     square at Z=0 (the natural ground reference). Used to verify camera + transform +
///     frustum culling work end-to-end before Phases 2–3 add terrain + REFR meshes.
///     <para>
///         All 4 corner vertices per cell are uploaded once at <see cref="LoadData" /> in
///         line-list pair order (8 vertices per cell). Per frame, the renderer iterates
///         frustum-visible cells and issues one <c>Draw(8, startVertex)</c> per cell. For
///         the densest worldspace (~30k cells) this is ~1k draws/frame typical, well within
///         budget for D3D11.
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
    private int _cellCount;
    private Dictionary<(int gx, int gy), int>? _cellStartVertex;

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

        _depthState = gpu.Device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.LessEqual,
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
    }

    public void Dispose()
    {
        _vertexBuffer?.Dispose();
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

    /// <summary>
    ///     Rebuilds the cell-grid vertex buffer from the supplied exterior cells. Call once per
    ///     LoadData on the host control; the buffer is reused across frames.
    /// </summary>
    public void LoadData(IEnumerable<CellRecord> exteriorCells)
    {
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _cellStartVertex = new Dictionary<(int, int), int>();
        _cellCount = 0;

        var cells = new List<(int gx, int gy)>();
        foreach (var cell in exteriorCells)
        {
            if (cell.GridX is not int gx || cell.GridY is not int gy) continue;
            var key = (gx, gy);
            if (_cellStartVertex.ContainsKey(key)) continue; // dedupe; later DLC overrides keep first
            _cellStartVertex[key] = cells.Count * 8;
            cells.Add(key);
        }

        _cellCount = cells.Count;
        if (_cellCount == 0) return;

        var vertices = new Vector3[_cellCount * 8];
        for (var i = 0; i < cells.Count; i++)
        {
            var (gx, gy) = cells[i];
            var x0 = gx * WorldGridConstants.CellSize;
            var y0 = gy * WorldGridConstants.CellSize;
            var x1 = x0 + WorldGridConstants.CellSize;
            var y1 = y0 + WorldGridConstants.CellSize;
            const float z = 0f;
            var baseIdx = i * 8;
            // 4 edges, line-list pairs: (south, east, north, west) at Z=0.
            vertices[baseIdx + 0] = new Vector3(x0, y0, z); vertices[baseIdx + 1] = new Vector3(x1, y0, z);
            vertices[baseIdx + 2] = new Vector3(x1, y0, z); vertices[baseIdx + 3] = new Vector3(x1, y1, z);
            vertices[baseIdx + 4] = new Vector3(x1, y1, z); vertices[baseIdx + 5] = new Vector3(x0, y1, z);
            vertices[baseIdx + 6] = new Vector3(x0, y1, z); vertices[baseIdx + 7] = new Vector3(x0, y0, z);
        }

        _vertexBuffer = CreateImmutableBuffer(_gpu.Device, vertices, BindFlags.VertexBuffer);
    }

    /// <summary>
    ///     Issues draw calls for every cell whose AABB intersects the frustum. Returns the
    ///     count of visible cells so the host control can surface it in the status overlay.
    /// </summary>
    public int Render(Matrix4x4 viewProj, Frustum frustum)
    {
        if (_vertexBuffer is null || _cellStartVertex is null) return 0;

        var ctx = _gpu.Context;

        // Upload uniforms (viewProj + line color) — same matrix-byte semantics as skin shaders.
        var uniforms = new CellGridUniforms
        {
            ViewProj = viewProj,
            LineColor = new Vector4(1.0f, 0.95f, 0.4f, 0.6f) // light yellow, semi-transparent
        };
        UpdateConstantBuffer(ctx, _constantBuffer, uniforms);

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        ctx.IASetVertexBuffer(0, _vertexBuffer, (uint)Marshal.SizeOf<Vector3>());
        ctx.VSSetShader(_vertexShader);
        ctx.PSSetShader(_pixelShader);
        ctx.VSSetConstantBuffer(0, _constantBuffer);
        ctx.PSSetConstantBuffer(0, _constantBuffer);
        ctx.RSSetState(_rasterizer);
        ctx.OMSetDepthStencilState(_depthState);
        ctx.OMSetBlendState(_blendState, new Color4(1f, 1f, 1f, 1f));

        var visible = 0;
        foreach (var (key, startVertex) in _cellStartVertex)
        {
            var (min, max) = WorldGridConstants.GetCellWorldBounds(key.gx, key.gy);
            if (!frustum.IntersectsAabb(min, max)) continue;

            ctx.Draw(8, (uint)startVertex);
            visible++;
        }
        return visible;
    }

    private static void UpdateConstantBuffer(ID3D11DeviceContext ctx, ID3D11Buffer buffer, CellGridUniforms uniforms)
    {
        var arr = new[] { uniforms };
        var gc = GCHandle.Alloc(arr, GCHandleType.Pinned);
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

    private static ID3D11Buffer CreateImmutableBuffer<T>(ID3D11Device device, T[] data, BindFlags bindFlags)
        where T : unmanaged
    {
        var byteWidth = (uint)(data.Length * Marshal.SizeOf<T>());
        var gc = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var desc = new BufferDescription
            {
                ByteWidth = byteWidth,
                Usage = ResourceUsage.Immutable,
                BindFlags = bindFlags,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
                StructureByteStride = 0
            };
            return device.CreateBuffer(desc, new SubresourceData(gc.AddrOfPinnedObject(), byteWidth));
        }
        finally { gc.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CellGridUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 LineColor;
    }
}
#endif
