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
///     v3 Phase 3 — renders placed-object (REFR) NIF meshes for the visible exterior cells.
///     <para>
///         Per visible cell, walks <see cref="WorldRenderCache.GetPlacementList" /> (a baked
///         filtered + transform-pre-composed list) and draws each <see cref="RenderableReference" />
///         after a per-REFR cylinder cull. Mesh + texture data is lazy-loaded into a
///         <see cref="ReferenceMeshCache" /> on first sight; subsequent frames hit warm cache.
///     </para>
///     <para>
///         Mirrors <see cref="TerrainRenderer" />'s render-loop shape: shared state set once
///         per frame, SRV binding cache to skip redundant PSSetShaderResource calls between
///         draws that share textures, rasterizer + depth-stencil state switching only when
///         <c>AlphaTest</c> / <c>DoubleSided</c> flips.
///     </para>
/// </summary>
internal sealed class ReferenceRenderer : IDisposable
{
    private const uint PerFrameByteSize = 64;     // float4x4 viewProj
    private const uint PerDrawByteSize  = 80;     // float4x4 world + float4 flags (16-aligned)

    private readonly GpuDevice _gpu;
    private readonly ReferenceMeshCache _meshCache;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11Buffer _perFrameCb;
    private readonly ID3D11Buffer _perDrawCb;
    private readonly ID3D11RasterizerState _rasterizerCullBack;
    private readonly ID3D11RasterizerState _rasterizerCullNone;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11SamplerState _diffuseSampler;

    private readonly Matrix4x4[] _perFrameScratch = new Matrix4x4[1];
    private readonly PerDrawConstants[] _perDrawScratch = new PerDrawConstants[1];

    private Dictionary<(int gx, int gy), CellRecord>? _cells;
    private WorldRenderCache? _renderCache;
    private ID3D11ShaderResourceView? _lastBoundSrv;
    private bool _lastCullNone;

    public int ReferencesDrawnLastFrame { get; private set; }

    public ReferenceRenderer(GpuDevice gpu, ReferenceMeshCache meshCache)
    {
        _gpu = gpu;
        _meshCache = meshCache;

        var vsBytecode = CompileEmbeddedShader("reference.vert.hlsl", "main", "vs_5_0");
        var psBytecode = CompileEmbeddedShader("reference.frag.hlsl", "main", "ps_5_0");
        _vertexShader = gpu.Device.CreateVertexShader(vsBytecode.AsSpan());
        _pixelShader = gpu.Device.CreatePixelShader(psBytecode.AsSpan());
        _inputLayout = gpu.Device.CreateInputLayout(GpuMeshUploader.InputElements, vsBytecode.AsSpan());

        _perFrameCb = CreateCb(gpu.Device, PerFrameByteSize);
        _perDrawCb = CreateCb(gpu.Device, PerDrawByteSize);

        _rasterizerCullBack = gpu.Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            // NIFs are authored CCW from outside; CullMode.Back drops the inside-facing tris.
            CullMode = CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false
        });
        _rasterizerCullNone = gpu.Device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false
        });

        // Opaque + alpha-tested meshes both write depth (alpha-tested foliage still occludes
        // what's behind it). Less-equal so coplanar surfaces from adjacent REFRs don't z-fight.
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
            BlendEnable = false, // alpha-test is via shader discard; no blend
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
    }

    public void Dispose()
    {
        _diffuseSampler.Dispose();
        _blendState.Dispose();
        _depthState.Dispose();
        _rasterizerCullNone.Dispose();
        _rasterizerCullBack.Dispose();
        _perDrawCb.Dispose();
        _perFrameCb.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    /// <summary>
    ///     Stores the per-cell lookup + render cache. Per-cell placement lists materialise
    ///     lazily inside <see cref="Render" /> on first visit — no upfront walk.
    /// </summary>
    public void LoadData(WorldRenderCache renderCache, Dictionary<(int gx, int gy), CellRecord> cells)
    {
        _renderCache = renderCache;
        _cells = cells;
        _lastBoundSrv = null;
    }

    /// <summary>
    ///     Draws every <see cref="RenderableReference" /> in the visible cells whose world
    ///     bounds intersect the camera cylinder. Returns the count of REFRs that issued at
    ///     least one draw — for the HUD.
    /// </summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        ReferencesDrawnLastFrame = 0;
        if (_cells is null || _cells.Count == 0 || _renderCache is null) return 0;

        var ctx = _gpu.Context;

        _perFrameScratch[0] = viewProj;
        UpdateConstantBuffer(ctx, _perFrameCb, _perFrameScratch);

        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vertexShader);
        ctx.PSSetShader(_pixelShader);
        ctx.VSSetConstantBuffer(0, _perFrameCb);
        ctx.PSSetConstantBuffer(0, _perFrameCb);
        ctx.VSSetConstantBuffer(1, _perDrawCb);
        ctx.PSSetConstantBuffer(1, _perDrawCb);
        ctx.PSSetSampler(0, _diffuseSampler);
        ctx.OMSetDepthStencilState(_depthState);
        ctx.OMSetBlendState(_blendState, new Color4(1f, 1f, 1f, 1f));

        // Default to back-face culling; per-draw flips to CullNone for double-sided REFRs.
        ctx.RSSetState(_rasterizerCullBack);
        _lastCullNone = false;
        _lastBoundSrv = null;

        var vertexStride = (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>();
        var drawn = 0;

        var cylinderRadius = cylinder.Radius;
        var cylinderX = cylinder.Position.X;
        var cylinderY = cylinder.Position.Y;

        foreach (var (key, cell) in _cells)
        {
            // Cell-level cull first (cheap, drops most cells in WastelandNV-scale loads).
            if (!cylinder.ContainsCell(key.gx, key.gy)) continue;

            var placements = _renderCache.GetPlacementList(cell);
            if (placements.Count == 0) continue;

            foreach (var r in placements)
            {
                // Per-REFR cylinder cull: sphere center XY vs cylinder XY, with combined radii.
                // Z is ignored (matches terrain's translation-only cylinder cull).
                var dx = r.BoundsCenter.X - cylinderX;
                var dy = r.BoundsCenter.Y - cylinderY;
                var maxDist = cylinderRadius + r.BoundsRadius;
                if (dx * dx + dy * dy > maxDist * maxDist) continue;

                var mesh = _meshCache.GetOrUpload(r.ModelPath);
                if (mesh is null) continue;

                var anySubmeshDrawn = false;
                foreach (var sub in mesh.Submeshes)
                {
                    SetCullModeIfChanged(ctx, sub.DoubleSided);
                    BindSrvIfChanged(ctx, sub.DiffuseSrv);

                    _perDrawScratch[0] = new PerDrawConstants
                    {
                        World = r.WorldMatrix,
                        AlphaTestThreshold = sub.AlphaTest ? sub.AlphaTestThreshold : 0f,
                        DoubleSided = sub.DoubleSided ? 1f : 0f,
                        Pad0 = 0f,
                        Pad1 = 0f
                    };
                    UpdateConstantBuffer(ctx, _perDrawCb, _perDrawScratch);

                    ctx.IASetVertexBuffer(0, sub.VertexBuffer, vertexStride);
                    ctx.IASetIndexBuffer(sub.IndexBuffer, Vortice.DXGI.Format.R16_UInt, 0);
                    ctx.DrawIndexed((uint)sub.IndexCount, 0, 0);
                    anySubmeshDrawn = true;
                }
                if (anySubmeshDrawn) drawn++;
            }
        }

        ReferencesDrawnLastFrame = drawn;
        return drawn;
    }

    private void BindSrvIfChanged(ID3D11DeviceContext ctx, ID3D11ShaderResourceView srv)
    {
        if (ReferenceEquals(_lastBoundSrv, srv)) return;
        _lastBoundSrv = srv;
        ctx.PSSetShaderResource(0, srv);
    }

    private void SetCullModeIfChanged(ID3D11DeviceContext ctx, bool doubleSided)
    {
        if (_lastCullNone == doubleSided) return;
        _lastCullNone = doubleSided;
        ctx.RSSetState(doubleSided ? _rasterizerCullNone : _rasterizerCullBack);
    }

    private static ID3D11Buffer CreateCb(ID3D11Device device, uint byteWidth) =>
        device.CreateBuffer(new BufferDescription
        {
            ByteWidth = byteWidth,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });

    private static void UpdateConstantBuffer<T>(ID3D11DeviceContext ctx, ID3D11Buffer buffer, T[] scratch)
        where T : unmanaged
    {
        var gc = GCHandle.Alloc(scratch, GCHandleType.Pinned);
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
    private struct PerDrawConstants
    {
        public Matrix4x4 World;
        public float AlphaTestThreshold;
        public float DoubleSided;
        public float Pad0;
        public float Pad1;
    }
}
