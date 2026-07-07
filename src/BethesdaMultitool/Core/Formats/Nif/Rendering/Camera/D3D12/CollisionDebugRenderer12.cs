#if WINDOWS_GUI
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     Debug overlay that draws each visible placed reference's walk-mode <see cref="CollisionMesh" />
///     as a wireframe cage (the Havok <c>bhk*</c> collision when present, else the visual-mesh fallback),
///     so the user can visually compare collision against the rendered meshes. Off by default; toggled
///     from the Visibility menu.
///     <para>
///         Cloned from <c>CellGridDebugRenderer12</c>: reuses the cellgrid line shaders + PSO, builds a
///         single per-frame dynamic line-vertex buffer in the shared ring buffer (no per-renderer GPU
///         resource), and draws one <c>DrawInstanced</c> line list. Each collision triangle contributes
///         three edges (6 line vertices). Depth is DISABLED so the cage stays visible through the meshes
///         it overlays. Only refs whose collision mesh is already cached (warm) are drawn — exactly the
///         set walk mode samples.
///     </para>
/// </summary>
internal sealed class CollisionDebugRenderer12 : IDisposable
{
    private const uint UniformsByteSize = 80; // float4x4 (64) + float4 color (16)
    private const uint VertexStride = 12;     // sizeof(Vector3)

    // Cap on line vertices accumulated per frame (each 12 B → ~6 MB ring allocation at the cap). A dense
    // area's collision cages fit comfortably; beyond this we stop adding (debug overlay — move closer to
    // see the rest). Keeps the per-frame ring-buffer allocation bounded.
    private const int MaxLineVertices = 500_000;

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly ID3D12PipelineState _pso;

    private readonly List<global::BethesdaMultitool.WorldSpatialCell> _visibleCellScratch = [];
    private Vector3[] _lineScratch = new Vector3[4096];
    private Vector3[] _worldPosScratch = [];
    private int _lineVertexCount;
    private bool _disposed;

    /// <summary>Spatial index for the loaded worldspace; set via <see cref="LoadData" />.</summary>
    public global::BethesdaMultitool.WorldSpatialIndex? SpatialIndex { get; private set; }

    /// <summary>Resolves a model path to its cached walk-mode collision mesh (null when not warm).</summary>
    public Func<string, CollisionMesh?>? CollisionResolver { get; set; }

    /// <summary>Mirror the viewer's "show initially-disabled" toggle so the overlay matches the scene.</summary>
    public bool ShowDisabled { get; set; }

    public CollisionDebugRenderer12(
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

        // Depth DISABLED: the collision cage is a comparison overlay — it must stay visible through the
        // meshes it sits inside (you're checking the cage matches/spans the visible geometry), like the
        // selection highlight. It never writes depth, so later layers are unaffected.
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pso.Dispose();
    }

    /// <summary>Binds the loaded worldspace's spatial index (placed-ref source for the overlay).</summary>
    public void LoadData(global::BethesdaMultitool.WorldSpatialIndex? spatialIndex)
        => SpatialIndex = spatialIndex;

    /// <summary>
    ///     Draws the collision wireframe for every warm-collision reference within the visibility
    ///     cylinder. Returns the number of references drawn (0 when nothing is warm / in view).
    /// </summary>
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
    {
        if (_disposed || SpatialIndex is null || CollisionResolver is null) return 0;

        _lineVertexCount = 0;
        SpatialIndex.QueryCellsInRadius(cylinder.Position.X, -cylinder.Position.Y, cylinder.Radius, _visibleCellScratch);

        // Draw NEAREST cells first so the per-frame line-vertex budget (MaxLineVertices) is spent on the
        // collision closest to the camera — that's what you compare against the rendered meshes up close.
        // QueryCellsInRadius returns cells in index order, so without this the budget could be exhausted by
        // distant cells, leaving near collision undrawn ("only rendered far from the camera").
        var camCanvas = new Vector2(cylinder.Position.X, -cylinder.Position.Y);
        _visibleCellScratch.Sort((a, b) =>
            Vector2.DistanceSquared(a.CenterCanvas, camCanvas)
                .CompareTo(Vector2.DistanceSquared(b.CenterCanvas, camCanvas)));

        var refsDrawn = 0;
        foreach (var spatialCell in _visibleCellScratch)
        {
            foreach (var p in spatialCell.Cell.PlacedObjects)
            {
                if (_lineVertexCount >= MaxLineVertices) break;
                if (string.IsNullOrEmpty(p.ModelPath)) continue;
                if (!ShowDisabled && p.IsInitiallyDisabled) continue;
                if (p.RecordType is "ACHR" or "ACRE") continue; // skinned actors carry no static collision
                if (RenderableReference.IsMarkerModelPath(p.ModelPath) ||
                    RenderableReference.IsImposterModelPath(p.ModelPath) ||
                    RenderableReference.IsLodDuplicateBaseEditorId(p.BaseEditorId)) continue;

                var mesh = CollisionResolver(p.ModelPath!);
                if (mesh is null) continue;

                var world = PlacedReferenceTransform.ComposeWorldMatrix(p.X, p.Y, p.Z, p.RotX, p.RotY, p.RotZ, p.Scale);
                AppendCollisionWireframe(mesh, world);
                refsDrawn++;
            }

            if (_lineVertexCount >= MaxLineVertices) break;
        }

        if (_lineVertexCount < 2) return 0;

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;

        var vbByteCount = (uint)(_lineVertexCount * VertexStride);
        if (!_ringBuffer.TryAllocate(frameIndex, vbByteCount, out var vbAlloc, alignment: 4))
        {
            // Too large for the per-frame ring partition this frame — skip (non-essential overlay).
            return 0;
        }

        WriteVertexBytes(vbAlloc.CpuPtr, vbByteCount);

        var cbAlloc = _ringBuffer.Allocate(frameIndex, UniformsByteSize, GpuRingBuffer12.CbAlignment);
        var uniforms = new CollisionUniforms
        {
            ViewProj = viewProj,
            LineColor = new Vector4(0.2f, 1.0f, 0.35f, 0.85f) // bright green, distinct from grid/navmesh
        };
        unsafe { *(CollisionUniforms*)cbAlloc.CpuPtr = uniforms; }

        cmd.SetPipelineState(_pso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        cmd.IASetVertexBuffers(0, new VertexBufferView
        {
            BufferLocation = vbAlloc.GpuAddress,
            SizeInBytes = vbByteCount,
            StrideInBytes = VertexStride,
        });
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, cbAlloc.GpuAddress);
        cmd.DrawInstanced((uint)_lineVertexCount, 1, 0, 0);

        return refsDrawn;
    }

    private void AppendCollisionWireframe(CollisionMesh mesh, Matrix4x4 world)
    {
        var positions = mesh.Positions;
        var triangles = mesh.Triangles;
        if (positions.Length == 0 || triangles.Length < 3) return;

        // Transform local positions to world once, then emit triangle edges by index.
        if (_worldPosScratch.Length < positions.Length) _worldPosScratch = new Vector3[positions.Length];
        for (var i = 0; i < positions.Length; i++) _worldPosScratch[i] = Vector3.Transform(positions[i], world);

        for (var t = 0; t + 2 < triangles.Length; t += 3)
        {
            if (_lineVertexCount + 6 > MaxLineVertices) return;
            int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
            if ((uint)a >= (uint)positions.Length || (uint)b >= (uint)positions.Length || (uint)c >= (uint)positions.Length)
                continue;

            EnsureLineCapacity(_lineVertexCount + 6);
            Vector3 pa = _worldPosScratch[a], pb = _worldPosScratch[b], pc = _worldPosScratch[c];
            _lineScratch[_lineVertexCount++] = pa;
            _lineScratch[_lineVertexCount++] = pb;
            _lineScratch[_lineVertexCount++] = pb;
            _lineScratch[_lineVertexCount++] = pc;
            _lineScratch[_lineVertexCount++] = pc;
            _lineScratch[_lineVertexCount++] = pa;
        }
    }

    private void EnsureLineCapacity(int required)
    {
        if (required <= _lineScratch.Length) return;
        var next = _lineScratch.Length;
        while (next < required) next *= 2;
        Array.Resize(ref _lineScratch, next);
    }

    private unsafe void WriteVertexBytes(IntPtr destination, uint byteCount)
    {
        fixed (Vector3* src = _lineScratch)
        {
            System.Runtime.CompilerServices.Unsafe.CopyBlockUnaligned(
                destination: (void*)destination,
                source: src,
                byteCount: byteCount);
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
    private struct CollisionUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 LineColor;
    }
}
#endif
