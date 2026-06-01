#if WINDOWS_GUI
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
    private const uint UniformsByteSize = 96; // float4x4 (64) + float4 (16) + float4 (16)

    private static readonly Vector4 DefaultWaterColor = new(0.118f, 0.216f, 0.471f, 0.65f);

    private readonly GpuDevice _gpu;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constantBuffer;
    private readonly ID3D11RasterizerState _rasterizer;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11BlendState _blendState;

    private Dictionary<(int gx, int gy), CellRecord>? _cells;
    private float? _worldspaceDefaultWaterHeight;

    // Single-element array reused for the per-cell UpdateSubresource. Per-cell allocation
    // would otherwise tally to ~200 visible water cells × 96 bytes × 60 Hz ≈ 1.1 MB/sec of
    // garbage and contribute to the Gen 2 GC pressure that shows up as a periodic hitch on
    // large worldspaces.
    private readonly WaterUniforms[] _uniformsScratch = new WaterUniforms[1];

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
    }

    public void Dispose()
    {
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
    {
        _cells = cells;
        _worldspaceDefaultWaterHeight = worldspaceDefaultWaterHeight;
    }

    /// <summary>
    ///     Draws one quad per cell within <paramref name="cylinder" /> that has a valid
    ///     (non-sentinel) water height. Cylinder culling matches the terrain renderer —
    ///     neither rotation direction changes which water quads are emitted; only translation
    ///     does. Returns the count of drawn quads for the host HUD.
    /// </summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        if (_cells is null || _cells.Count == 0) return 0;

        var ctx = _gpu.Context;

        ctx.IASetInputLayout(null); // no vertex buffer; VS reads SV_VertexID directly
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vertexShader);
        ctx.PSSetShader(_pixelShader);
        ctx.VSSetConstantBuffer(0, _constantBuffer);
        ctx.PSSetConstantBuffer(0, _constantBuffer);
        ctx.RSSetState(_rasterizer);
        ctx.OMSetDepthStencilState(_depthState);
        ctx.OMSetBlendState(_blendState, new Color4(1f, 1f, 1f, 1f));

        var drawn = 0;
        foreach (var (key, cell) in _cells)
        {
            var waterHeight = ResolveWaterHeight(cell);
            if (waterHeight is not float z) continue;

            if (!cylinder.ContainsCell(key.gx, key.gy)) continue;

            var uniforms = new WaterUniforms
            {
                ViewProj = viewProj,
                CellOriginAndWater = new Vector4(
                    key.gx * WorldGridConstants.CellSize,
                    key.gy * WorldGridConstants.CellSize,
                    z,
                    WorldGridConstants.CellSize),
                Color = DefaultWaterColor
            };
            UpdateConstantBuffer(ctx, _constantBuffer, uniforms);

            ctx.Draw(6, 0);
            drawn++;
        }
        return drawn;
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
    private struct WaterUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 CellOriginAndWater;
        public Vector4 Color;
    }
}
#endif
