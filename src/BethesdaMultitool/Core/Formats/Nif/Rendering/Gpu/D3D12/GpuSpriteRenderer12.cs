using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     v3 Pass 4 Step 3 — D3D12 port of the old <c>GpuSpriteRenderer</c>. Headless renderer
///     producing the same <see cref="SpriteResult" /> as the D3D11 + CPU paths. Used by the
///     CLI <c>render npc</c> / <c>export npc</c> commands and the GPU smoke tests.
///     <para>
///         Mirrors the D3D11 SubmitRender/CompleteRender async split: SubmitRender records a
///         one-shot command list (MSAA render + Resolve + CopyTextureRegion to a READBACK
///         buffer) and signals a fence; CompleteRender waits on the fence, maps the readback
///         buffer, and copies pixels out. The fence wait replaces D3D11's implicit Map sync.
///     </para>
/// </summary>
internal sealed unsafe class GpuSpriteRenderer12 : IDisposable
{
    private const int ConstantBufferSize = 224; // sizeof(GpuUniforms) — must be 16-byte aligned
    private const int MsaaSampleCount = 4;
    private const uint SrvTableSize = 2;     // t0 diffuse + t1 normal
    private const uint SamplerTableSize = 2; // s0 + s1 (both linear-wrap; skin.frag.hlsl binds both)

    private readonly GpuDevice12 _gpu;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12DescriptorHeap _samplerHeap;
    private readonly byte[] _vsBytecode;
    private readonly byte[] _psBytecode;
    private readonly InputElementDescription[] _inputElements;
    private readonly Dictionary<PsoKey, ID3D12PipelineState> _psoCache = new();
    private readonly ID3D12Fence _renderFence;
    private readonly AutoResetEvent _fenceEvent = new(false);
    private ulong _nextFenceValue = 1;

    // Built-in fallback textures (1×1 RGBA), uploaded once at construction. White is the
    // diffuse fallback; flat normal (128,128,255) is the normal-map fallback. Per-render
    // texture uploads (NIF-resolved diffuse/normal) live on their own per-render command
    // list and are disposed in CompleteRender — no shared texture cache across renders,
    // which keeps SubmitRender self-contained and side-effect-free.
    private readonly ID3D12Resource _whitePixelTexture;
    private readonly ShaderResourceViewDescription _whitePixelSrvDesc;
    private readonly ID3D12Resource _flatNormalTexture;
    private readonly ShaderResourceViewDescription _flatNormalSrvDesc;
    private bool _disposed;

    public GpuSpriteRenderer12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        _vsBytecode = CompileEmbeddedShader("skin.vert.hlsl", "main", "vs_5_1");
        _psBytecode = CompileEmbeddedShader("skin.frag.hlsl", "main", "ps_5_1");
        _inputElements = GpuMeshBufferFactory12.InputElements;

        _rootSignature = CreateRootSignature(gpu.Device);
        _samplerHeap = CreateSamplerHeap(gpu.Device);
        _renderFence = gpu.Device.CreateFence<ID3D12Fence>(0, FenceFlags.None);

        // Upload white + flat pixels on a one-shot init command list, fence-waited so the
        // textures are guaranteed in PIXEL_SHADER_RESOURCE state before any SubmitRender.
        _whitePixelTexture = CreateSolidPixelOneShot(255, 255, 255, 255);
        _whitePixelSrvDesc = MakeSrv(Format.R8G8B8A8_UNorm, 1);
        _flatNormalTexture = CreateSolidPixelOneShot(128, 128, 255, 255);
        _flatNormalSrvDesc = MakeSrv(Format.R8G8B8A8_UNorm, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain any in-flight GPU work referencing our resources before tear-down.
        var drain = _nextFenceValue++;
        _gpu.DirectQueue.Signal(_renderFence, drain).CheckError();
        D3D12FenceWaiter.WaitForFence(_renderFence, drain, _fenceEvent);

        _renderFence.Dispose();
        _fenceEvent.Dispose();
        foreach (var pso in _psoCache.Values) pso.Dispose();
        _psoCache.Clear();
        _samplerHeap.Dispose();
        _rootSignature.Dispose();
        _whitePixelTexture.Dispose();
        _flatNormalTexture.Dispose();
    }

    public SpriteResult? Render(NifRenderableModel model,
        global::BethesdaMultitool.Core.Formats.Nif.Rendering.NifTextureResolver? textureResolver,
        float pixelsPerUnit, int minSize, int maxSize,
        float azimuthDeg, float elevationDeg,
        int? fixedSize = null)
    {
        var pending = SubmitRender(model, textureResolver, pixelsPerUnit, minSize, maxSize,
            azimuthDeg, elevationDeg, fixedSize);
        return pending == null ? null : CompleteRender(pending);
    }

    public SpriteResult? Render(NifRenderableModel model,
        global::BethesdaMultitool.Core.Formats.Nif.Rendering.NifTextureResolver? textureResolver = null,
        float pixelsPerUnit = 1.0f, int minSize = 32, int maxSize = 1024,
        int? fixedSize = null)
    {
        if (!model.HasGeometry) return null;
        return Render(model, textureResolver, pixelsPerUnit, minSize, maxSize, 0f, 90f, fixedSize);
    }

    public PendingRender? SubmitRender(NifRenderableModel model,
        global::BethesdaMultitool.Core.Formats.Nif.Rendering.NifTextureResolver? textureResolver,
        float pixelsPerUnit, int minSize, int maxSize,
        float azimuthDeg, float elevationDeg,
        int? fixedSize = null)
    {
        // Drain any pending validation messages from the previous frame so they're attributed
        // to the correct render. No-op if the debug layer wasn't enabled.
        _gpu.PumpDebugMessages();
        try { return SubmitRenderCore(model, textureResolver, pixelsPerUnit, minSize, maxSize,
            azimuthDeg, elevationDeg, fixedSize); }
        catch
        {
            _gpu.PumpDebugMessages();
            throw;
        }
    }

    private PendingRender? SubmitRenderCore(NifRenderableModel model,
        global::BethesdaMultitool.Core.Formats.Nif.Rendering.NifTextureResolver? textureResolver,
        float pixelsPerUnit, int minSize, int maxSize,
        float azimuthDeg, float elevationDeg,
        int? fixedSize)
    {
        if (!model.HasGeometry) return null;

        var (projMinX, projMinY, projWidth, projHeight, viewMatrix) =
            ComputeViewBounds(model, azimuthDeg, elevationDeg);
        if (projWidth < 0.001f && projHeight < 0.001f) return null;

        int width, height;
        if (fixedSize.HasValue)
        {
            var aspect = projWidth / Math.Max(projHeight, 0.001f);
            if (aspect >= 1f)
            {
                width = fixedSize.Value;
                height = Math.Clamp((int)(fixedSize.Value / aspect), 1, fixedSize.Value);
            }
            else
            {
                height = fixedSize.Value;
                width = Math.Clamp((int)(fixedSize.Value * aspect), 1, fixedSize.Value);
            }
        }
        else
        {
            var rawWidth = (int)MathF.Ceiling(projWidth * pixelsPerUnit) + 2;
            var rawHeight = (int)MathF.Ceiling(projHeight * pixelsPerUnit) + 2;
            var scale = 1.0f;
            var maxDim = Math.Max(rawWidth, rawHeight);
            if (maxDim > maxSize) scale = (float)maxSize / maxDim;
            else if (maxDim < minSize) scale = (float)minSize / maxDim;
            width = Math.Clamp((int)(rawWidth * scale), 1, maxSize);
            height = Math.Clamp((int)(rawHeight * scale), 1, maxSize);
        }

        const float margin = 1f;
        var orthoLeft = projMinX - margin;
        var orthoRight = projMinX + projWidth + margin;
        var orthoTop = projMinY - margin;
        var orthoBottom = projMinY + projHeight + margin;
        const float orthoNear = -10000f;
        const float orthoFar = 10000f;
        var projMatrix = Matrix4x4.CreateOrthographicOffCenter(
            orthoLeft, orthoRight, orthoBottom, orthoTop, orthoNear, orthoFar);
        var viewProj = viewMatrix * projMatrix;

        var ssWidth = (uint)(width * RenderLightingConstants.SsaaFactor);
        var ssHeight = (uint)(height * RenderLightingConstants.SsaaFactor);

        // ---- Allocate per-render GPU resources --------------------------------------------
        var device = _gpu.Device;
        var pendingResources = new List<IDisposable>();

        // Per-render command allocator + command list (one-shot).
        var allocator = device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct);
        pendingResources.Add(allocator);
        var cmd = device.CreateCommandList<ID3D12GraphicsCommandList>(
            nodeMask: 0, CommandListType.Direct, allocator, initialState: null);

        // MSAA color RT.
        var colorTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, ssWidth, ssHeight,
                arraySize: 1, mipLevels: 1, sampleCount: MsaaSampleCount, sampleQuality: 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget,
            new ClearValue(Format.R8G8B8A8_UNorm, new Color4(0f, 0f, 0f, 0f)));
        pendingResources.Add(colorTex);

        // Non-MSAA resolve target.
        var resolveTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, ssWidth, ssHeight,
                arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0,
                ResourceFlags.None),
            ResourceStates.ResolveDest,
            optimizedClearValue: null);
        pendingResources.Add(resolveTex);

        // MSAA depth.
        var depthTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(Format.D32_Float_S8X24_UInt, ssWidth, ssHeight,
                arraySize: 1, mipLevels: 1, sampleCount: MsaaSampleCount, sampleQuality: 0,
                ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite,
            new ClearValue(Format.D32_Float_S8X24_UInt, new DepthStencilValue(1.0f, 0)));
        pendingResources.Add(depthTex);

        // Small per-render RTV + DSV heaps (CPU-only).
        var rtvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.None,
        });
        pendingResources.Add(rtvHeap);
        var dsvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.DepthStencilView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.None,
        });
        pendingResources.Add(dsvHeap);
        var rtvHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        var dsvHandle = dsvHeap.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(colorTex, null, rtvHandle);
        device.CreateDepthStencilView(depthTex, null, dsvHandle);

        // Shader-visible CBV/SRV/UAV heap sized to fit the worst case (2 descriptors per
        // submesh: diffuse + normal). Use the actual draw count when known after classification.
        // ---- Classify + sort submeshes ----------------------------------------------------
        var renderItems = new List<RenderItem>();
        foreach (var sub in model.Submeshes)
        {
            if (sub.TriangleCount == 0 || sub.VertexCount == 0) continue;

            DecodedTexture? diffuseTexture = null;
            if (textureResolver != null && sub.DiffuseTexturePath != null)
                diffuseTexture = textureResolver.GetTexture(sub.DiffuseTexturePath);

            if (textureResolver != null && diffuseTexture == null &&
                sub.DiffuseTexturePath == null &&
                !(sub.IsEmissive && sub.DiffuseTexturePath != null) &&
                !(sub.UseVertexColors && sub.VertexColors != null))
                continue;

            if (textureResolver != null && diffuseTexture == null &&
                sub.DiffuseTexturePath == null &&
                sub.IsEmissive && sub.HasAlphaBlend &&
                !NifVertexColorPolicy.HasVertexColorData(sub))
                continue;

            renderItems.Add(new RenderItem(
                sub,
                NifAlphaClassifier.Classify(sub, diffuseTexture),
                ComputeAverageZ(sub, viewMatrix),
                diffuseTexture != null));
        }

        var ordered = renderItems
            .OrderBy(item => item.Submesh.RenderOrder)
            .ThenBy(item => item.AlphaState.RenderMode == NifAlphaRenderMode.Blend ? 1 : 0)
            .ThenBy(item => item.AverageZ)
            .ToList();

        var srvHeapCapacity = (uint)Math.Max(1, ordered.Count * 2);
        var srvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            DescriptorCount = srvHeapCapacity,
            Flags = DescriptorHeapFlags.ShaderVisible,
        });
        pendingResources.Add(srvHeap);
        var srvIncrement = device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);

        // ---- Begin command recording -----------------------------------------------------
        cmd.OMSetRenderTargets(rtvHandle, dsvHandle);
        cmd.ClearRenderTargetView(rtvHandle, new Color4(0f, 0f, 0f, 0f));
        cmd.ClearDepthStencilView(dsvHandle, ClearFlags.Depth | ClearFlags.Stencil, 1f, 0);
        cmd.RSSetViewport(new Viewport(0, 0, ssWidth, ssHeight, 0f, 1f));
        cmd.RSSetScissorRect((int)ssWidth, (int)ssHeight);
        cmd.SetGraphicsRootSignature(_rootSignature);
        cmd.SetDescriptorHeaps(2, new[] { srvHeap, _samplerHeap });
        cmd.SetGraphicsRootDescriptorTable(2, _samplerHeap.GetGPUDescriptorHandleForHeapStart());
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        var srvBumpOffset = 0u;

        foreach (var item in ordered)
        {
            var sub = item.Submesh;
            var alphaState = item.AlphaState;
            var hasDiffuse = item.HasDiffuseTexture;

            uint flags = 0;
            if (hasDiffuse) flags |= 1;
            if (sub.Normals != null) flags |= 2;
            if (sub.Tangents != null && sub.Bitangents != null && sub.NormalMapTexturePath != null) flags |= 4;
            if (NifVertexColorPolicy.HasVertexColorData(sub)) flags |= 8;
            if (sub.IsEmissive) flags |= 16;
            if (sub.IsDoubleSided) flags |= 32;
            if (alphaState.HasAlphaBlend) flags |= 64;
            if (alphaState.HasAlphaTest) flags |= 128;
            if (sub.IsEyeEnvmap) flags |= 256;
            if (sub.TintColor.HasValue) flags |= 512;
            if (sub.IsFaceGen) flags |= 1024;
            if (alphaState.RenderMode == NifAlphaRenderMode.AlphaToCoverage) flags |= 2048;

            var uniforms = new GpuUniforms
            {
                ViewProj = viewProj,
                View = viewMatrix,
                LightDir = new Vector4(RenderLightingConstants.LightDir, 0),
                HalfVec = new Vector4(RenderLightingConstants.HalfVec, RenderLightingConstants.HdotNegL),
                Ambient = new Vector4(RenderLightingConstants.SkyAmbient, RenderLightingConstants.GroundAmbient,
                    RenderLightingConstants.LightIntensity, NifSpriteRenderer.BumpStrength),
                Material = new Vector4(alphaState.MaterialAlpha, sub.EnvMapScale,
                    alphaState.AlphaTestThreshold / 255f, alphaState.AlphaTestFunction),
                TintColor = new Vector4(
                    sub.TintColor?.R ?? 1f, sub.TintColor?.G ?? 1f, sub.TintColor?.B ?? 1f, 0),
                Flags = new Vector4(flags, sub.SubsurfaceColor.R, sub.SubsurfaceColor.G, sub.SubsurfaceColor.B),
            };

            // Per-submesh CB lives in a fresh UPLOAD-heap buffer. Persistent-mapped + filled +
            // bound as a root CBV. Disposed in CompleteRender.
            var cb = device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer(ConstantBufferSize),
                ResourceStates.GenericRead, optimizedClearValue: null);
            pendingResources.Add(cb);
            void* cbCpu = null;
            cb.Map(0, &cbCpu).CheckError();
            *(GpuUniforms*)cbCpu = uniforms;
            cb.Unmap(0, null);

            // Mesh upload.
            var vertices = GpuMeshUploader.BuildVertices(sub);
            var vb = GpuMeshBufferFactory12.CreateUploadBuffer<GpuMeshUploader.GpuVertex>(_gpu, vertices);
            pendingResources.Add(vb);
            var ib = GpuMeshBufferFactory12.CreateUploadBuffer<ushort>(_gpu, sub.Triangles);
            pendingResources.Add(ib);

            // Texture binds — default to the white-pixel + flat-normal builtins; if the
            // submesh references a NIF path, upload the decoded mip chain on this command
            // list. Uploaded textures + their staging buffers get appended to
            // pendingResources so CompleteRender disposes them after the GPU is done.
            var diffuseTex = _whitePixelTexture;
            var diffuseSrv = _whitePixelSrvDesc;
            var normalTex = _flatNormalTexture;
            var normalSrv = _flatNormalSrvDesc;
            if (hasDiffuse && textureResolver != null && sub.DiffuseTexturePath != null)
            {
                var upload = UploadTextureOn(cmd, textureResolver, sub.DiffuseTexturePath);
                if (upload is { } u)
                {
                    diffuseTex = u.Texture; diffuseSrv = u.SrvDesc;
                    pendingResources.Add(u.Texture); pendingResources.Add(u.Staging);
                }
            }
            if (textureResolver != null && sub.NormalMapTexturePath != null)
            {
                var upload = UploadTextureOn(cmd, textureResolver, sub.NormalMapTexturePath);
                if (upload is { } u)
                {
                    normalTex = u.Texture; normalSrv = u.SrvDesc;
                    pendingResources.Add(u.Texture); pendingResources.Add(u.Staging);
                }
            }

            var cpuStart = new CpuDescriptorHandle(srvHeap.GetCPUDescriptorHandleForHeapStart(),
                (int)srvBumpOffset, srvIncrement);
            var gpuStart = new GpuDescriptorHandle(srvHeap.GetGPUDescriptorHandleForHeapStart(),
                (int)srvBumpOffset, srvIncrement);
            device.CreateShaderResourceView(diffuseTex, diffuseSrv,
                new CpuDescriptorHandle(cpuStart, 0, srvIncrement));
            device.CreateShaderResourceView(normalTex, normalSrv,
                new CpuDescriptorHandle(cpuStart, 1, srvIncrement));
            srvBumpOffset += 2;

            // PSO selection by alpha mode + double-sided + blend mode.
            var psoKey = new PsoKey(
                alphaState.RenderMode,
                alphaState.SrcBlendMode,
                alphaState.DstBlendMode,
                sub.IsDoubleSided);
            var pso = GetOrCreatePso(psoKey);
            cmd.SetPipelineState(pso);

            cmd.SetGraphicsRootConstantBufferView(0, cb.GPUVirtualAddress);
            cmd.SetGraphicsRootDescriptorTable(1, gpuStart);

            cmd.IASetVertexBuffers(0, new VertexBufferView
            {
                BufferLocation = vb.GPUVirtualAddress,
                SizeInBytes = (uint)vertices.Length * (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>(),
                StrideInBytes = (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>(),
            });
            cmd.IASetIndexBuffer(new IndexBufferView
            {
                BufferLocation = ib.GPUVirtualAddress,
                SizeInBytes = (uint)sub.Triangles.Length * sizeof(ushort),
                Format = Format.R16_UInt,
            });
            cmd.DrawIndexedInstanced((uint)sub.Triangles.Length, 1, 0, 0, 0);
        }

        // ---- Resolve MSAA → resolveTex; copy resolveTex → readback buffer -----------------
        cmd.ResourceBarrierTransition(colorTex, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
        cmd.ResolveSubresource(resolveTex, 0, colorTex, 0, Format.R8G8B8A8_UNorm);
        cmd.ResourceBarrierTransition(resolveTex, ResourceStates.ResolveDest, ResourceStates.CopySource);

        // Allocate readback buffer sized to fit the resolved texture.
        var resolveDesc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, ssWidth, ssHeight,
            arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0, ResourceFlags.None);
        var footprints = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSize = new ulong[1];
        device.GetCopyableFootprints(resolveDesc, 0, 1, 0, footprints, numRows, rowSize, out var readbackBytes);

        var readback = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(readbackBytes),
            ResourceStates.CopyDest, optimizedClearValue: null);
        pendingResources.Add(readback);

        cmd.CopyTextureRegion(
            new TextureCopyLocation(readback, footprints[0]), 0, 0, 0,
            new TextureCopyLocation(resolveTex, 0));

        cmd.Close();
        _gpu.DirectQueue.ExecuteCommandList(cmd);
        var fenceValue = _nextFenceValue++;
        _gpu.DirectQueue.Signal(_renderFence, fenceValue).CheckError();
        pendingResources.Add(cmd);

        return new PendingRender
        {
            Width = width,
            Height = height,
            SsWidth = (int)ssWidth,
            SsHeight = (int)ssHeight,
            BoundsWidth = projWidth,
            BoundsHeight = projHeight,
            HasTexture = ordered.Any(item => item.HasDiffuseTexture),
            FenceValue = fenceValue,
            ReadbackBuffer = readback,
            ReadbackRowPitch = footprints[0].Footprint.RowPitch,
            Disposables = pendingResources,
        };
    }

    public SpriteResult CompleteRender(PendingRender pending)
    {
        // Wait for GPU completion of this render's submission.
        D3D12FenceWaiter.WaitForFence(_renderFence, pending.FenceValue, _fenceEvent);

        var ssPixels = ReadBackPixels(pending.ReadbackBuffer,
            (uint)pending.SsWidth, (uint)pending.SsHeight, pending.ReadbackRowPitch);
        var pixels = NifSpriteRenderer.Downsample(ssPixels, pending.SsWidth, pending.SsHeight,
            RenderLightingConstants.SsaaFactor);

        foreach (var d in pending.Disposables) d.Dispose();

        return new SpriteResult
        {
            Pixels = pixels,
            Width = pending.Width,
            Height = pending.Height,
            BoundsWidth = pending.BoundsWidth,
            BoundsHeight = pending.BoundsHeight,
            HasTexture = pending.HasTexture,
        };
    }

    /// <summary>
    ///     No-op in the D3D12 port — sprite renders don't share a persistent texture cache
    ///     across SubmitRender calls (all per-render textures dispose in CompleteRender).
    ///     Kept for API parity with the old <c>GpuSpriteRenderer.EvictTexture</c> so the
    ///     CLI NPC pipeline can call it unconditionally.
    /// </summary>
    // Intentionally an instance method (not static) for API parity with the old GpuSpriteRenderer —
    // the NPC pipeline evicts unconditionally on the renderer instance regardless of backend.
#pragma warning disable CA1822, S2325
    public void EvictTexture(string key) { _ = key; }
#pragma warning restore CA1822, S2325

    /// <summary>
    ///     Decodes <paramref name="path" /> via <paramref name="resolver" /> and uploads it
    ///     to a DEFAULT-heap committed texture on <paramref name="cmd" />. Records the
    ///     CopyTextureRegion + state transition; the texture is ready to sample as soon as
    ///     the same command list reaches the draw call. Returns null on resolver miss so the
    ///     caller can fall back to <see cref="_whitePixelTexture" /> / <see cref="_flatNormalTexture" />.
    /// </summary>
    private (ID3D12Resource Texture, ShaderResourceViewDescription SrvDesc, ID3D12Resource Staging)?
        UploadTextureOn(
            ID3D12GraphicsCommandList cmd,
            global::BethesdaMultitool.Core.Formats.Nif.Rendering.NifTextureResolver resolver,
            string path)
    {
        var decoded = resolver.GetTexture(path);
        if (decoded is null) return null;
        var width = (uint)decoded.Width;
        var height = (uint)decoded.Height;
        var mipCount = (ushort)decoded.MipCount;
        if (width == 0 || height == 0 || mipCount == 0) return null;

        var device = _gpu.Device;
        var desc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, width, height,
            arraySize: 1, mipLevels: mipCount, sampleCount: 1, sampleQuality: 0, ResourceFlags.None);
        var texture = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
            ResourceStates.CopyDest, optimizedClearValue: null);

        var footprints = new PlacedSubresourceFootPrint[mipCount];
        var numRows = new uint[mipCount];
        var rowSize = new ulong[mipCount];
        device.GetCopyableFootprints(desc, 0, mipCount, 0, footprints, numRows, rowSize, out var totalBytes);

        var staging = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes),
            ResourceStates.GenericRead, optimizedClearValue: null);

        void* cpuPtr = null;
        staging.Map(0, &cpuPtr).CheckError();
        try
        {
            for (var mip = 0; mip < mipCount; mip++)
            {
                var level = decoded.GetMipLevel(mip);
                if (level.Pixels.Length == 0 || level.Width == 0 || level.Height == 0) continue;
                var srcRowPitch = (uint)level.Width * 4u;
                var dstRowPitch = footprints[mip].Footprint.RowPitch;
                var dstBase = (byte*)cpuPtr + (long)footprints[mip].Offset;
                fixed (byte* src = level.Pixels)
                {
                    for (uint row = 0; row < (uint)level.Height; row++)
                    {
                        var copyBytes = Math.Min(srcRowPitch, dstRowPitch);
                        System.Buffer.MemoryCopy(
                            src + row * srcRowPitch, dstBase + row * dstRowPitch, dstRowPitch, copyBytes);
                    }
                }
            }
        }
        finally { staging.Unmap(0, null); }

        for (uint mip = 0; mip < mipCount; mip++)
        {
            cmd.CopyTextureRegion(
                new TextureCopyLocation(texture, mip), 0, 0, 0,
                new TextureCopyLocation(staging, footprints[mip]));
        }
        cmd.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

        return (texture, MakeSrv(Format.R8G8B8A8_UNorm, mipCount), staging);
    }

    /// <summary>
    ///     Creates a 1×1 RGBA texture filled with <paramref name="r" />, <paramref name="g" />,
    ///     <paramref name="b" />, <paramref name="a" /> on a one-shot init command list. Used
    ///     by the ctor for the white-pixel + flat-normal builtins.
    /// </summary>
    private ID3D12Resource CreateSolidPixelOneShot(byte r, byte g, byte b, byte a)
    {
        var device = _gpu.Device;
        var desc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, 1, 1,
            arraySize: 1, mipLevels: 1, sampleCount: 1, sampleQuality: 0, ResourceFlags.None);
        var texture = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
            ResourceStates.CopyDest, optimizedClearValue: null);

        var footprints = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSize = new ulong[1];
        device.GetCopyableFootprints(desc, 0, 1, 0, footprints, numRows, rowSize, out var totalBytes);
        var staging = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes),
            ResourceStates.GenericRead, optimizedClearValue: null);

        void* cpuPtr = null;
        staging.Map(0, &cpuPtr).CheckError();
        try
        {
            var p = (byte*)cpuPtr + (long)footprints[0].Offset;
            p[0] = r; p[1] = g; p[2] = b; p[3] = a;
        }
        finally { staging.Unmap(0, null); }

        var allocator = device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct);
        var cmd = device.CreateCommandList<ID3D12GraphicsCommandList>(
            nodeMask: 0, CommandListType.Direct, allocator, initialState: null);
        cmd.CopyTextureRegion(
            new TextureCopyLocation(texture, 0), 0, 0, 0,
            new TextureCopyLocation(staging, footprints[0]));
        cmd.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        cmd.Close();
        _gpu.DirectQueue.ExecuteCommandList(cmd);
        var fenceValue = _nextFenceValue++;
        _gpu.DirectQueue.Signal(_renderFence, fenceValue).CheckError();
        D3D12FenceWaiter.WaitForFence(_renderFence, fenceValue, _fenceEvent);
        cmd.Dispose();
        allocator.Dispose();
        staging.Dispose();
        return texture;
    }

    private static ShaderResourceViewDescription MakeSrv(Format format, ushort mipCount)
    {
        return new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = mipCount, MostDetailedMip = 0 },
        };
    }

    private static byte[] ReadBackPixels(ID3D12Resource buffer, uint width, uint height, uint rowPitch)
    {
        void* cpuPtr = null;
        buffer.Map(0, &cpuPtr).CheckError();
        try
        {
            var pixels = new byte[width * height * 4];
            var rowSize = (int)(width * 4);
            for (uint y = 0; y < height; y++)
            {
                var srcOffset = (int)(y * rowPitch);
                var dstOffset = (int)(y * rowSize);
                Marshal.Copy((IntPtr)cpuPtr + srcOffset, pixels, dstOffset, rowSize);
            }
            return pixels;
        }
        finally
        {
            buffer.Unmap(0, null);
        }
    }

    // ---- Per-render structures ----------------------------------------------------------

    /// <summary>State for an in-flight async sprite render: target dimensions, model bounds, and texture flags, completed later by <c>CompleteRender</c>.</summary>
    internal sealed class PendingRender
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int SsWidth { get; init; }
        public required int SsHeight { get; init; }
        public required float BoundsWidth { get; init; }
        public required float BoundsHeight { get; init; }
        public required bool HasTexture { get; init; }
        public required ulong FenceValue { get; init; }
        public required ID3D12Resource ReadbackBuffer { get; init; }
        public required uint ReadbackRowPitch { get; init; }
        public required List<IDisposable> Disposables { get; init; }
    }

    private sealed record RenderItem(
        RenderableSubmesh Submesh,
        NifAlphaRenderState AlphaState,
        float AverageZ,
        bool HasDiffuseTexture);

    private readonly record struct PsoKey(
        NifAlphaRenderMode Mode,
        byte SrcBlend,
        byte DstBlend,
        bool DoubleSided);

    // ---- PSO + RootSig + sampler setup --------------------------------------------------

    private ID3D12PipelineState GetOrCreatePso(PsoKey key)
    {
        if (_psoCache.TryGetValue(key, out var existing)) return existing;
        var pso = CreatePso(key);
        _psoCache[key] = pso;
        return pso;
    }

    private ID3D12PipelineState CreatePso(PsoKey key)
    {
        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = key.DoubleSided ? D12.CullMode.None : D12.CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            MultisampleEnable = true,
            AntialiasedLineEnable = false,
        };

        var depthWriteEnabled = key.Mode != NifAlphaRenderMode.Blend;
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = depthWriteEnabled ? D12.DepthWriteMask.All : D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.LessEqual,
            StencilEnable = false,
        };

        var blend = new D12.BlendDescription
        {
            // A2C is done manually in the pixel shader via SV_Coverage (IS_A2C flag):
            // the fixed-function path derives coverage from the WRITTEN alpha, which
            // would force covered samples to carry texture alpha and double-attenuate
            // after resolve. The shader instead writes opaque alpha + a coverage mask.
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
        };
        if (key.Mode == NifAlphaRenderMode.Blend)
        {
            blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = MapBlend(key.SrcBlend),
                DestinationBlend = MapBlend(key.DstBlend),
                BlendOperation = D12.BlendOperation.Add,
                SourceBlendAlpha = D12.Blend.One,
                DestinationBlendAlpha = D12.Blend.One,
                BlendOperationAlpha = D12.BlendOperation.Max,
                RenderTargetWriteMask = D12.ColorWriteEnable.All,
            };
        }
        else
        {
            blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
            {
                BlendEnable = false,
                RenderTargetWriteMask = D12.ColorWriteEnable.All,
            };
        }

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = _vsBytecode,
            PixelShader = _psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(_inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Format.R8G8B8A8_UNorm },
            DepthStencilFormat = Format.D32_Float_S8X24_UInt,
            SampleDescription = new SampleDescription(MsaaSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return _gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    private static D12.Blend MapBlend(byte nifBlendMode)
    {
        // NIF blend factors. The D3D11 path uses NifD3D11BlendMapper; mirror its policy here
        // for the common cases. Unmapped values default to One/Zero (effectively no blend).
        return nifBlendMode switch
        {
            0 => D12.Blend.One,
            1 => D12.Blend.Zero,
            2 => D12.Blend.SourceColor,
            3 => D12.Blend.InverseSourceColor,
            4 => D12.Blend.SourceAlpha,
            5 => D12.Blend.InverseSourceAlpha,
            6 => D12.Blend.DestinationAlpha,
            7 => D12.Blend.InverseDestinationAlpha,
            8 => D12.Blend.DestinationColor,
            9 => D12.Blend.InverseDestinationColor,
            10 => D12.Blend.SourceAlphaSaturate,
            _ => D12.Blend.One,
        };
    }

    private static ID3D12RootSignature CreateRootSignature(ID3D12Device device)
    {
        // Slot 0: root CBV b0 (per-submesh uniforms). VS + PS.
        var cbv = new RootParameter1(
            RootParameterType.ConstantBufferView,
            new RootDescriptor1(shaderRegister: 0, registerSpace: 0),
            ShaderVisibility.All);

        // Slot 1: SRV table t0..t1 (diffuse + normal). PS.
        var srvRange = new DescriptorRange1(
            DescriptorRangeType.ShaderResourceView,
            numDescriptors: SrvTableSize,
            baseShaderRegister: 0, registerSpace: 0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);

        // Slot 2: sampler table s0. PS.
        var samplerRange = new DescriptorRange1(
            DescriptorRangeType.Sampler,
            numDescriptors: SamplerTableSize,
            baseShaderRegister: 0, registerSpace: 0);
        var samplerTable = new RootParameter1(new RootDescriptorTable1(samplerRange), ShaderVisibility.Pixel);

        var desc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            new[] { cbv, srvTable, samplerTable });
        return device.CreateRootSignature(desc);
    }

    private static ID3D12DescriptorHeap CreateSamplerHeap(ID3D12Device device)
    {
        var heap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.Sampler,
            DescriptorCount = SamplerTableSize,
            Flags = DescriptorHeapFlags.ShaderVisible,
        });
        var sampler = new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 1,
            ComparisonFunction = ComparisonFunction.Never,
            MinLOD = 0,
            MaxLOD = float.MaxValue,
        };
        // skin.frag.hlsl binds both s0 and s1 — the D3D11 path points both at the same
        // _linearSampler state. Replicate by writing the same sampler descriptor to slots 0+1.
        var samplerSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);
        var heapStart = heap.GetCPUDescriptorHandleForHeapStart();
        device.CreateSampler(ref sampler, new CpuDescriptorHandle(heapStart, 0, samplerSize));
        device.CreateSampler(ref sampler, new CpuDescriptorHandle(heapStart, 1, samplerSize));
        return heap;
    }

    // ---- View bounds (verbatim from D3D11) ----------------------------------------------

    private static (float MinX, float MinY, float Width, float Height, Matrix4x4 ViewMatrix)
        ComputeViewBounds(NifRenderableModel model, float azimuthDeg, float elevationDeg)
    {
        var alpha = azimuthDeg * MathF.PI / 180f;
        var theta = elevationDeg * MathF.PI / 180f;
        var ca = MathF.Cos(alpha);
        var sa = MathF.Sin(alpha);
        var ct = MathF.Cos(theta);
        var st = MathF.Sin(theta);
        var right = new Vector3(-sa, ca, 0);
        var up = new Vector3(st * ca, st * sa, -ct);
        var forward = new Vector3(ca * ct, sa * ct, st);
        var viewMatrix = new Matrix4x4(
            right.X, up.X, forward.X, 0,
            right.Y, up.Y, forward.Y, 0,
            right.Z, up.Z, forward.Z, 0,
            0, 0, 0, 1);

        var minX = float.MaxValue;
        var minY = float.MaxValue;
        var maxX = float.MinValue;
        var maxY = float.MinValue;
        foreach (var sub in model.Submeshes)
        {
            for (var i = 0; i < sub.Positions.Length; i += 3)
            {
                var pos = new Vector3(sub.Positions[i], sub.Positions[i + 1], sub.Positions[i + 2]);
                var viewPos = Vector3.Transform(pos, viewMatrix);
                if (viewPos.X < minX) minX = viewPos.X;
                if (viewPos.Y < minY) minY = viewPos.Y;
                if (viewPos.X > maxX) maxX = viewPos.X;
                if (viewPos.Y > maxY) maxY = viewPos.Y;
            }
        }
        return (minX, minY, maxX - minX, maxY - minY, viewMatrix);
    }

    private static float ComputeAverageZ(RenderableSubmesh sub, Matrix4x4 viewMatrix)
    {
        if (sub.Positions.Length < 3) return 0f;
        var sum = 0f;
        var count = sub.Positions.Length / 3;
        for (var i = 0; i < sub.Positions.Length; i += 3)
        {
            var pos = new Vector3(sub.Positions[i], sub.Positions[i + 1], sub.Positions[i + 2]);
            sum += Vector3.Transform(pos, viewMatrix).Z;
        }
        return sum / count;
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
        var compileResult = Compiler.Compile(source, entryPoint, sourceName: name, profile,
            out Blob? bytecode, out Blob? errors);
        if (compileResult.Failure || bytecode is null)
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
    private struct GpuUniforms
    {
        public Matrix4x4 ViewProj; // 64
        public Matrix4x4 View;     // 64
        public Vector4 LightDir;   // 16
        public Vector4 HalfVec;    // 16
        public Vector4 Ambient;    // 16
        public Vector4 Material;   // 16
        public Vector4 TintColor;  // 16
        public Vector4 Flags;      // 16
        // Total: 224 bytes
    }
}

