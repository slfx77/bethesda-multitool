using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using ShaderResourceViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Native headless renderer with shared pipeline and submission ownership,
///     producing the same <see cref="SpriteResult" /> contract as the CPU path. Used by the
///     CLI <c>render npc</c> / <c>export npc</c> commands and the GPU smoke tests.
///     <para>
///         Preserves the SubmitRender/CompleteRender async split: SubmitRender records a
///         one-shot command list (MSAA render + Resolve + CopyTextureRegion to a READBACK
///         buffer) and signals a fence; CompleteRender waits on the fence, maps the readback
///         buffer, and copies pixels out. An explicit fence wait synchronizes the readback.
///     </para>
/// </summary>
internal sealed unsafe class GpuSpriteRenderer12 : IDisposable
{
    private const int ConstantBufferSize = 240; // sizeof(GpuUniforms) — must be 16-byte aligned
    private const int MsaaSampleCount = 4;
    private const uint SrvTableSize = 3; // t0 diffuse + t1 normal + t2 CE2 opacity
    private const uint SamplerTableSize = 3; // skin.frag.hlsl binds one sampler per texture
    private const uint SamplerModeCount = 4; // wrap/wrap, clampU, clampV, clampUV
    private const uint SamplerHeapSize = SamplerTableSize * SamplerModeCount;
    private readonly ReadOnlyMemory<byte> _classicSkinPsBytecode;
    private readonly AutoResetEvent _fenceEvent;
    private readonly ShaderResourceViewDescription _flatNormalSrvDesc;
    private readonly ID3D12Resource _flatNormalTexture;

    private readonly GpuDevice12 _gpu;
    private readonly InputElementDescription[] _inputElements;
    private readonly ReadOnlyMemory<byte> _psBytecode;
    private readonly bool[] _pipelineReady = new bool[GpuSpritePipelineKey.Capacity];
    private readonly ShaderPipelineResources _pipelineResources;
    private readonly RetiredResourceDisposal _ownedResources = new();
    private readonly List<SubmissionResourceRetirement> _submissions = [];
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _retirementProven;
    private readonly ID3D12Fence _renderFence;
    private readonly ID3D12RootSignature _rootSignature;
    private readonly ID3D12DescriptorHeap _samplerHeap;
    private readonly ReadOnlyMemory<byte> _vsBytecode;
    private readonly ShaderResourceViewDescription _whitePixelSrvDesc;

    // Built-in fallback textures (1×1 RGBA), uploaded once at construction. White is the
    // diffuse fallback; flat normal (128,128,255) is the normal-map fallback. Per-render
    // texture uploads (NIF-resolved diffuse/normal) live on their own per-render command
    // list and are disposed in CompleteRender — no shared texture cache across renders,
    // which keeps SubmitRender self-contained and side-effect-free.
    private readonly ID3D12Resource _whitePixelTexture;
    private bool _disposed;
    private ulong _nextFenceValue = 1;

    /// <summary>Creates the sprite pipeline owner and fallback textures on the rendering thread.</summary>
    /// <param name="gpu">Borrowed device retained until renderer disposal completes.</param>
    /// <remarks>Creation, submission, completion and disposal must use this same managed thread.</remarks>
    public GpuSpriteRenderer12(GpuDevice12 gpu)
    {
        _gpu = gpu;
        try
        {
            _ownedResources.Add(ReleaseSubmissionsAfterRetirement, "sprite submissions");
            _ownedResources.Add(() => _pipelineResources?.Dispose(), "sprite pipeline family", 2);
            _ownedResources.Add(() => _fenceEvent?.Dispose(), "sprite completion event", 3);
            _pipelineResources = new ShaderPipelineResources(gpu.Device, GpuSpritePipelineKey.Capacity);
            _fenceEvent = new AutoResetEvent(false);
            _vsBytecode = CompileEmbeddedShader("skin.vert.hlsl", "main", "vs_5_1");
            _psBytecode = CompileEmbeddedShader("skin.frag.hlsl", "main", "ps_5_1");
            _classicSkinPsBytecode = CompileEmbeddedShader(
                "skin.frag.hlsl", "main", "ps_5_1", new ShaderMacro("CLASSIC_SKIN2000", "1"));
            _inputElements = GpuMeshBufferFactory12.InputElements;
            _pipelineResources.Initialize(CreateRootSignatureDescription());
            _rootSignature = _pipelineResources.RootSignature;
            _samplerHeap = CreateSamplerHeap(gpu.Device);
            _renderFence = TrackResource(gpu.Device.CreateFence<ID3D12Fence>(), _ownedResources, 3);
            _whitePixelTexture = CreateSolidPixelOneShot(255, 255, 255, 255);
            _whitePixelSrvDesc = MakeSrv(Format.R8G8B8A8_UNorm, 1);
            _flatNormalTexture = CreateSolidPixelOneShot(128, 128, 255, 255);
            _flatNormalSrvDesc = MakeSrv(Format.R8G8B8A8_UNorm, 1);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Drains submitted work and releases dependencies in stages, retaining failures for retry.</summary>
    /// <exception cref="InvalidOperationException">Called outside the creating thread.</exception>
    /// <exception cref="AggregateException">Cleanup failed; retain this owner and retry disposal.</exception>
    public void Dispose()
    {
        VerifyAccess();
        _disposed = true;
        if (!_retirementProven)
        {
            try
            {
                if (_renderFence is not null)
                {
                    var drain = _nextFenceValue++;
                    _gpu.DirectQueue.Signal(_renderFence, drain).CheckError();
                    D3D12FenceWaiter.WaitForFence(_renderFence, drain, _fenceEvent);
                    if (_renderFence.CompletedValue == ulong.MaxValue &&
                        !_gpu.TryForceDeviceRemoval("sprite-renderer-teardown"))
#pragma warning disable S3877 // Dispose must retain resources when neither completion nor terminal device retirement is proven.
                        throw new InvalidOperationException("Sprite completion requires a fence or terminal device proof.");
#pragma warning restore S3877
                }
            }
            catch
            {
                if (!_gpu.TryForceDeviceRemoval("sprite-renderer-teardown")) throw;
            }
            _retirementProven = true;
        }
        _ownedResources.Dispose();
        Array.Clear(_pipelineReady);
    }

    /// <summary>Rejects access outside the renderer's creating thread before any native state changes.</summary>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Sprite rendering belongs to its creating thread.");
    }

    /// <summary>Retains newly allocated native resources before subsequent preparation can fail.</summary>
    /// <typeparam name="T">Owned native or managed disposable type.</typeparam>
    /// <param name="resource">New resource to transfer.</param>
    /// <param name="owner">Existing retryable owner retained by the renderer.</param>
    /// <param name="stage">Dependency stage within that owner.</param>
    /// <returns>The borrowed handle used for preparation and recording.</returns>
    private static T TrackResource<T>(T resource, RetiredResourceDisposal owner, int stage = 1)
        where T : IDisposable
    {
        owner.Add(resource, "sprite resource", stage);
        return resource;
    }

    /// <summary>Releases every retained submission only after the renderer has proven queue or device retirement.</summary>
    private void ReleaseSubmissionsAfterRetirement()
    {
        List<Exception>? failures = null;
        for (var index = _submissions.Count - 1; index >= 0; index--)
        {
            try
            {
                _submissions[index].ReleaseAllAfterRetirement();
                _submissions.RemoveAt(index);
            }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is not null) throw new AggregateException("Sprite submission cleanup failed.", failures);
    }

    /// <summary>Retains an empty release bundle before recording allocates resources or can reach the native queue.</summary>
    /// <param name="resources">Owner populated by subsequent preparation.</param>
    /// <returns>The shared submission classifier retained until successful cleanup.</returns>
    private SubmissionResourceRetirement BeginSubmission(RetiredResourceDisposal resources)
    {
        var submission = new SubmissionResourceRetirement(1);
        _submissions.Add(submission);
        submission.BeginRecording();
        submission.Retain(resources, "sprite recording");
        return submission;
    }

    /// <summary>Classifies the native execute result before associating its successful fence signal.</summary>
    /// <param name="commandList">Closed command list with resources already retained.</param>
    /// <param name="submission">Current recording ownership.</param>
    /// <returns>The actual signaled fence value.</returns>
    private ulong Submit(ID3D12GraphicsCommandList commandList, SubmissionResourceRetirement submission)
    {
        submission.MarkSubmissionPossible();
        try { _gpu.DirectQueue.ExecuteCommandList(commandList); }
        catch
        {
            submission.ReportSubmission(SubmissionOutcome.SubmissionUncertain);
            throw;
        }
        submission.ReportSubmission(SubmissionOutcome.Submitted);
        var fence = _nextFenceValue++;
        _gpu.DirectQueue.Signal(_renderFence, fence).CheckError();
        submission.AssociateUnfenced(fence);
        return fence;
    }

    public SpriteResult? Render(NifRenderableModel model,
        NifTextureResolver? textureResolver,
        float pixelsPerUnit, int minSize, int maxSize,
        float azimuthDeg, float elevationDeg,
        int? fixedSize = null)
    {
        var pending = SubmitRender(model, textureResolver, pixelsPerUnit, minSize, maxSize,
            azimuthDeg, elevationDeg, fixedSize);
        return pending == null ? null : CompleteRender(pending);
    }

    public SpriteResult? Render(NifRenderableModel model,
        NifTextureResolver? textureResolver = null,
        float pixelsPerUnit = 1.0f, int minSize = 32, int maxSize = 1024,
        int? fixedSize = null)
    {
        if (!model.HasGeometry) return null;
        return Render(model, textureResolver, pixelsPerUnit, minSize, maxSize, 0f, 90f, fixedSize);
    }

    /// <summary>Records a sprite while retaining every allocation through actual queue completion or abandonment.</summary>
    /// <param name="model">Normalized geometry and material inputs.</param>
    /// <param name="textureResolver">Optional source texture resolver.</param>
    /// <param name="pixelsPerUnit">Orthographic sizing scale when fixed size is absent.</param>
    /// <param name="minSize">Minimum automatically sized extent.</param>
    /// <param name="maxSize">Maximum automatically sized extent.</param>
    /// <param name="azimuthDeg">Horizontal camera angle.</param>
    /// <param name="elevationDeg">Vertical camera angle.</param>
    /// <param name="fixedSize">Optional fixed maximum image extent.</param>
    /// <returns>A retained submitted sprite, or null when geometry cannot produce a view.</returns>
    public PendingRender? SubmitRender(NifRenderableModel model,
        NifTextureResolver? textureResolver,
        float pixelsPerUnit, int minSize, int maxSize,
        float azimuthDeg, float elevationDeg,
        int? fixedSize = null)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gpu.PumpDebugMessages();
        var resources = new RetiredResourceDisposal();
        var submission = BeginSubmission(resources);
        try
        {
            var pending = SubmitRenderCore(model, textureResolver, pixelsPerUnit, minSize, maxSize,
                azimuthDeg, elevationDeg, fixedSize, resources, submission);
            if (pending is null)
            {
                submission.AbandonRecording();
                submission.ReleaseCompleted(0);
                _submissions.Remove(submission);
            }
            return pending;
        }
        catch
        {
            if (submission.HasRecording)
            {
                submission.AbandonRecording();
                submission.ReleaseCompleted(0);
                _submissions.Remove(submission);
            }
            _gpu.PumpDebugMessages();
            throw;
        }
    }

    /// <summary>Builds the unchanged sprite draw sequence under an already retained submission owner.</summary>
    /// <param name="model">Geometry and per-draw material state.</param>
    /// <param name="textureResolver">Optional resolver for source images.</param>
    /// <param name="pixelsPerUnit">Automatic image scale.</param>
    /// <param name="minSize">Minimum automatic extent.</param>
    /// <param name="maxSize">Maximum automatic extent.</param>
    /// <param name="azimuthDeg">Horizontal camera angle.</param>
    /// <param name="elevationDeg">Vertical camera angle.</param>
    /// <param name="fixedSize">Optional fixed extent.</param>
    /// <param name="pendingResources">Retryable allocation owner retained before this method is called.</param>
    /// <param name="submission">Shared classifier for recording, queue acceptance and successful signal.</param>
    /// <returns>Readback metadata owned by this renderer, or null for absent geometry.</returns>
    private PendingRender? SubmitRenderCore(NifRenderableModel model,
        NifTextureResolver? textureResolver,
        float pixelsPerUnit, int minSize, int maxSize,
        float azimuthDeg, float elevationDeg,
        int? fixedSize, RetiredResourceDisposal pendingResources, SubmissionResourceRetirement submission)
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

        // Per-render command allocator + command list (one-shot).
        var allocator = device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct);
        pendingResources.Add(allocator, "sprite command allocator", 2);
        var cmd = device.CreateCommandList<ID3D12GraphicsCommandList>(
            0, CommandListType.Direct, allocator);
        pendingResources.Add(cmd, "sprite command list");

        // MSAA color RT.
        var colorTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, ssWidth, ssHeight,
                1, 1, MsaaSampleCount, 0,
                ResourceFlags.AllowRenderTarget),
            ResourceStates.RenderTarget,
            new ClearValue(Format.R8G8B8A8_UNorm, new Color4(0f, 0f, 0f, 0f)));
        pendingResources.Add(colorTex, "sprite render resource", 1);

        // Non-MSAA resolve target.
        var resolveTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, ssWidth, ssHeight,
                1, 1),
            ResourceStates.ResolveDest);
        pendingResources.Add(resolveTex, "sprite render resource", 1);

        // MSAA depth.
        var depthTex = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None,
            ResourceDescription.Texture2D(Format.D32_Float_S8X24_UInt, ssWidth, ssHeight,
                1, 1, MsaaSampleCount, 0,
                ResourceFlags.AllowDepthStencil),
            ResourceStates.DepthWrite,
            new ClearValue(Format.D32_Float_S8X24_UInt, new DepthStencilValue(1.0f)));
        pendingResources.Add(depthTex, "sprite render resource", 1);

        // Small per-render RTV + DSV heaps (CPU-only).
        var rtvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.RenderTargetView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.None
        });
        pendingResources.Add(rtvHeap, "sprite render resource", 1);
        var dsvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.DepthStencilView,
            DescriptorCount = 1,
            Flags = DescriptorHeapFlags.None
        });
        pendingResources.Add(dsvHeap, "sprite render resource", 1);
        var rtvHandle = rtvHeap.GetCPUDescriptorHandleForHeapStart();
        var dsvHandle = dsvHeap.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(colorTex, null, rtvHandle);
        device.CreateDepthStencilView(depthTex, null, dsvHandle);

        // Shader-visible CBV/SRV/UAV heap sized to fit the worst case (3 descriptors per
        // submesh: diffuse + normal + CE2 opacity). Use the actual draw count when known after
        // classification.
        // ---- Classify + sort submeshes ----------------------------------------------------
        var renderItems = new List<RenderItem>();
        foreach (var sub in model.Submeshes)
        {
            if (sub.TriangleCount == 0 || sub.VertexCount == 0) continue;

            DecodedTexture? diffuseTexture = null;
            if (textureResolver != null && sub.DiffuseTexturePath != null)
                diffuseTexture = textureResolver.GetTexture(sub.DiffuseTexturePath);

            DecodedTexture? normalTexture = null;
            if (textureResolver != null && sub.NormalMapTexturePath != null)
                normalTexture = textureResolver.GetTexture(sub.NormalMapTexturePath);

            if (textureResolver != null && diffuseTexture == null &&
                normalTexture == null &&
                sub.DiffuseTexturePath == null &&
                !(sub.IsEmissive && sub.DiffuseTexturePath != null) &&
                !(sub.UseVertexColors && sub.VertexColors != null))
                continue;

            if (textureResolver != null && diffuseTexture == null &&
                sub.DiffuseTexturePath == null &&
                sub.IsEmissive && sub.HasAlphaBlend &&
                !NifVertexColorPolicy.HasVertexColorData(sub))
                continue;

            string? starfieldOpacityPath = null;
            if (textureResolver != null &&
                sub.StarfieldMaterialAlpha.IsLayer0OpacityCutout &&
                sub.DiffuseTexturePath is { } starfieldMaterialPath &&
                MaterialTexturePathResolver.IsStarfieldMaterialPath(starfieldMaterialPath))
            {
                var request = MaterialTexturePathResolver.BuildStarfieldOpacityMapRequest(starfieldMaterialPath);
                if (textureResolver.GetTexture(request) is not null)
                {
                    starfieldOpacityPath = request;
                }
            }

            renderItems.Add(new RenderItem(
                sub,
                NifAlphaClassifier.Classify(sub, diffuseTexture),
                ComputeAverageZ(sub, viewMatrix),
                diffuseTexture != null,
                normalTexture != null,
                starfieldOpacityPath));
        }

        var ordered = renderItems
            .OrderBy(item => item.Submesh.RenderOrder)
            .ThenBy(item => item.AlphaState.RenderMode == NifAlphaRenderMode.Blend ? 1 : 0)
            .ThenBy(item => item.AverageZ)
            .ToList();

        var srvHeapCapacity = (uint)Math.Max(1, ordered.Count * (int)SrvTableSize);
        var srvHeap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            DescriptorCount = srvHeapCapacity,
            Flags = DescriptorHeapFlags.ShaderVisible
        });
        pendingResources.Add(srvHeap, "sprite render resource", 1);
        var srvIncrement = device.GetDescriptorHandleIncrementSize(
            DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        var samplerIncrement = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);

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
            if (sub.Tangents != null && sub.Bitangents != null && item.HasNormalTexture) flags |= 4;
            if (NifVertexColorPolicy.HasVertexColorData(sub)) flags |= 8;
            if (sub.IsEmissive) flags |= 16;
            if (sub.IsDoubleSided) flags |= 32;
            if (alphaState.HasAlphaBlend) flags |= 64;
            if (alphaState.HasAlphaTest) flags |= 128;
            if (sub.IsEyeEnvmap) flags |= 256;
            if (sub.TintColor.HasValue) flags |= 512;
            if (sub.IsFaceGen) flags |= 1024;
            if (alphaState.RenderMode == NifAlphaRenderMode.AlphaToCoverage) flags |= 2048;
            if (sub.StarfieldMaterialColor.IsVertexLerp) flags |= 4096;
            if (item.StarfieldOpacityPath is not null) flags |= 8192;

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
                EffectTint = new Vector4(sub.EffectTint.R, sub.EffectTint.G, sub.EffectTint.B, 1f)
            };

            // Per-submesh CB lives in a fresh UPLOAD-heap buffer. Persistent-mapped + filled +
            // bound as a root CBV. Disposed in CompleteRender.
            var cb = device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties, HeapFlags.None,
                ResourceDescription.Buffer(ConstantBufferSize),
                ResourceStates.GenericRead);
            pendingResources.Add(cb, "sprite render resource", 1);
            void* cbCpu = null;
            cb.Map(0, &cbCpu).CheckError();
            *(GpuUniforms*)cbCpu = uniforms;
            cb.Unmap(0);

            // Mesh upload.
            var vertices = GpuMeshUploader.BuildVertices(sub);
            var vb = GpuMeshBufferFactory12.CreateUploadBuffer(_gpu, vertices);
            pendingResources.Add(vb, "sprite render resource", 1);
            var ib = GpuMeshBufferFactory12.CreateUploadBuffer(_gpu, sub.Triangles);
            pendingResources.Add(ib, "sprite render resource", 1);

            // Texture binds — default to the white-pixel + flat-normal builtins; if the
            // submesh references a NIF path, upload the decoded mip chain on this command
            // list. Uploaded textures + their staging buffers get appended to
            // pendingResources so CompleteRender disposes them after the GPU is done.
            var diffuseTex = _whitePixelTexture;
            var diffuseSrv = _whitePixelSrvDesc;
            var normalTex = _flatNormalTexture;
            var normalSrv = _flatNormalSrvDesc;
            var opacityTex = _whitePixelTexture;
            var opacitySrv = _whitePixelSrvDesc;
            if (hasDiffuse && textureResolver != null && sub.DiffuseTexturePath != null)
            {
                var upload = UploadTextureOn(cmd, textureResolver, sub.DiffuseTexturePath, pendingResources);
                if (upload is { } u)
                {
                    diffuseTex = u.Texture;
                    diffuseSrv = u.SrvDesc;
                }
            }

            if (textureResolver != null && item.HasNormalTexture && sub.NormalMapTexturePath != null)
            {
                var upload = UploadTextureOn(cmd, textureResolver, sub.NormalMapTexturePath, pendingResources);
                if (upload is { } u)
                {
                    normalTex = u.Texture;
                    normalSrv = u.SrvDesc;
                }
            }

            if (textureResolver != null && item.StarfieldOpacityPath is { } opacityPath)
            {
                var upload = UploadTextureOn(cmd, textureResolver, opacityPath, pendingResources);
                if (upload is { } u)
                {
                    opacityTex = u.Texture;
                    opacitySrv = u.SrvDesc;
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
            device.CreateShaderResourceView(opacityTex, opacitySrv,
                new CpuDescriptorHandle(cpuStart, 2, srvIncrement));
            srvBumpOffset += SrvTableSize;

            // PSO selection by alpha mode + double-sided + blend mode.
            var psoKey = GpuSpritePipelineKey.Create(
                alphaState.RenderMode,
                alphaState.SrcBlendMode,
                alphaState.DstBlendMode,
                sub.IsDoubleSided,
                sub.IsFaceGen);
            var pso = GetOrCreatePso(psoKey);
            cmd.SetPipelineState(pso);

            cmd.SetGraphicsRootConstantBufferView(0, cb.GPUVirtualAddress);
            cmd.SetGraphicsRootDescriptorTable(1, gpuStart);
            var samplerMode = (sub.ClampTextureU ? 1 : 0) | (sub.ClampTextureV ? 2 : 0);
            cmd.SetGraphicsRootDescriptorTable(2, new GpuDescriptorHandle(
                _samplerHeap.GetGPUDescriptorHandleForHeapStart(),
                samplerMode * (int)SamplerTableSize,
                samplerIncrement));

            cmd.IASetVertexBuffers(0, new VertexBufferView
            {
                BufferLocation = vb.GPUVirtualAddress,
                SizeInBytes = (uint)vertices.Length * (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>(),
                StrideInBytes = (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>()
            });
            cmd.IASetIndexBuffer(new IndexBufferView
            {
                BufferLocation = ib.GPUVirtualAddress,
                SizeInBytes = (uint)sub.Triangles.Length * sizeof(ushort),
                Format = Format.R16_UInt
            });
            cmd.DrawIndexedInstanced((uint)sub.Triangles.Length, 1, 0, 0, 0);
        }

        // ---- Resolve MSAA → resolveTex; copy resolveTex → readback buffer -----------------
        cmd.ResourceBarrierTransition(colorTex, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
        cmd.ResolveSubresource(resolveTex, 0, colorTex, 0, Format.R8G8B8A8_UNorm);
        cmd.ResourceBarrierTransition(resolveTex, ResourceStates.ResolveDest, ResourceStates.CopySource);

        // Allocate readback buffer sized to fit the resolved texture.
        var resolveDesc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, ssWidth, ssHeight,
            1, 1);
        var footprints = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSize = new ulong[1];
        device.GetCopyableFootprints(resolveDesc, 0, 1, 0, footprints, numRows, rowSize, out var readbackBytes);

        var readback = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.ReadbackHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(readbackBytes),
            ResourceStates.CopyDest);
        pendingResources.Add(readback, "sprite render resource", 1);

        cmd.CopyTextureRegion(
            new TextureCopyLocation(readback, footprints[0]), 0, 0, 0,
            new TextureCopyLocation(resolveTex));

        cmd.Close();
        var fenceValue = Submit(cmd, submission);

        return new PendingRender
        {
            Width = width,
            Height = height,
            SsWidth = (int)ssWidth,
            SsHeight = (int)ssHeight,
            BoundsWidth = projWidth,
            BoundsHeight = projHeight,
            HasTexture = ordered.Any(item =>
                item.HasDiffuseTexture || item.HasNormalTexture || item.StarfieldOpacityPath is not null),
            FenceValue = fenceValue,
            ReadbackBuffer = readback,
            ReadbackRowPitch = footprints[0].Footprint.RowPitch,
            Resources = submission
        };
    }

    /// <summary>Waits for one owned submission, reads its sprite, and releases its completed resources even if readback fails.</summary>
    /// <param name="pending">Exact pending result from this renderer, completed at most once.</param>
    /// <returns>The rendered and downsampled sprite.</returns>
    public SpriteResult CompleteRender(PendingRender pending)
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_submissions.Contains(pending.Resources))
            throw new InvalidOperationException("The sprite submission is foreign or already completed.");
        D3D12FenceWaiter.WaitForFence(_renderFence, pending.FenceValue, _fenceEvent);
        if (_renderFence.CompletedValue == ulong.MaxValue)
            throw new InvalidOperationException("The sprite device was removed before readback.");
        try
        {
            var ssPixels = ReadBackPixels(pending.ReadbackBuffer,
                (uint)pending.SsWidth, (uint)pending.SsHeight, pending.ReadbackRowPitch);
            var pixels = NifSpriteRenderer.Downsample(ssPixels, pending.SsWidth, pending.SsHeight,
                RenderLightingConstants.SsaaFactor);
            return new SpriteResult
            {
                Pixels = pixels,
                Width = pending.Width,
                Height = pending.Height,
                BoundsWidth = pending.BoundsWidth,
                BoundsHeight = pending.BoundsHeight,
                HasTexture = pending.HasTexture
            };
        }
        finally
        {
            pending.Resources.ReleaseCompleted(pending.FenceValue);
            _submissions.Remove(pending.Resources);
        }
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
    public void EvictTexture(string key)
    {
        _ = key;
    }
#pragma warning restore CA1822, S2325

    /// <summary>
    ///     Decodes <paramref name="path" /> via <paramref name="resolver" /> and uploads it
    ///     to a DEFAULT-heap committed texture on <paramref name="cmd" />. Records the
    ///     CopyTextureRegion + state transition; the texture is ready to sample as soon as
    ///     the same command list reaches the draw call. Returns null on resolver miss so the
    ///     caller can fall back to <see cref="_whitePixelTexture" /> / <see cref="_flatNormalTexture" />.
    /// </summary>
    /// <param name="cmd">Open caller-owned command list.</param>
    /// <param name="resolver">Source image decoder and cache.</param>
    /// <param name="path">Stable texture identity understood by the resolver.</param>
    /// <param name="resources">Already retained submission bundle that owns allocations before mapping or recording.</param>
    /// <returns>Borrowed native texture, its view and staging handle, or null when no pixels are available.</returns>
    private (ID3D12Resource Texture, ShaderResourceViewDescription SrvDesc, ID3D12Resource Staging)?
        UploadTextureOn(
            ID3D12GraphicsCommandList cmd,
            NifTextureResolver resolver,
            string path, RetiredResourceDisposal resources)
    {
        var decoded = resolver.GetTexture(path);
        if (decoded is null) return null;
        var width = (uint)decoded.Width;
        var height = (uint)decoded.Height;
        var mipCount = (ushort)decoded.MipCount;
        if (width == 0 || height == 0 || mipCount == 0) return null;

        var device = _gpu.Device;
        var desc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, width, height,
            1, mipCount);
        var texture = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
            ResourceStates.CopyDest);
        resources.Add(texture, "sprite texture", 1);

        var footprints = new PlacedSubresourceFootPrint[mipCount];
        var numRows = new uint[mipCount];
        var rowSize = new ulong[mipCount];
        device.GetCopyableFootprints(desc, 0, mipCount, 0, footprints, numRows, rowSize, out var totalBytes);

        var staging = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes),
            ResourceStates.GenericRead);
        resources.Add(staging, "sprite texture staging", 1);

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
                        Buffer.MemoryCopy(
                            src + row * srcRowPitch, dstBase + row * dstRowPitch, dstRowPitch, copyBytes);
                    }
                }
            }
        }
        finally
        {
            staging.Unmap(0);
        }

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
    /// <param name="r">Red byte.</param>
    /// <param name="g">Green byte.</param>
    /// <param name="b">Blue byte.</param>
    /// <param name="a">Alpha byte.</param>
    /// <returns>The persistent texture, retained before staging work can fail.</returns>
    private ID3D12Resource CreateSolidPixelOneShot(byte r, byte g, byte b, byte a)
    {
        var resources = new RetiredResourceDisposal();
        var submission = BeginSubmission(resources);
        var device = _gpu.Device;
        var desc = ResourceDescription.Texture2D(Format.R8G8B8A8_UNorm, 1, 1,
            1, 1);
        var texture = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties, HeapFlags.None, desc,
            ResourceStates.CopyDest);
        _ownedResources.Add(texture, "sprite fallback texture", 1);

        var footprints = new PlacedSubresourceFootPrint[1];
        var numRows = new uint[1];
        var rowSize = new ulong[1];
        device.GetCopyableFootprints(desc, 0, 1, 0, footprints, numRows, rowSize, out var totalBytes);
        var staging = device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.UploadHeapProperties, HeapFlags.None,
            ResourceDescription.Buffer(totalBytes),
            ResourceStates.GenericRead);
        resources.Add(staging, "sprite fallback staging", 1);

        void* cpuPtr = null;
        staging.Map(0, &cpuPtr).CheckError();
        try
        {
            var p = (byte*)cpuPtr + (long)footprints[0].Offset;
            p[0] = r;
            p[1] = g;
            p[2] = b;
            p[3] = a;
        }
        finally
        {
            staging.Unmap(0);
        }

        var allocator = device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct);
        resources.Add(allocator, "sprite fallback allocator", 2);
        var cmd = device.CreateCommandList<ID3D12GraphicsCommandList>(
            0, CommandListType.Direct, allocator);
        resources.Add(cmd, "sprite fallback command list");
        cmd.CopyTextureRegion(
            new TextureCopyLocation(texture), 0, 0, 0,
            new TextureCopyLocation(staging, footprints[0]));
        cmd.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        cmd.Close();
        var fenceValue = Submit(cmd, submission);
        D3D12FenceWaiter.WaitForFence(_renderFence, fenceValue, _fenceEvent);
        if (_renderFence.CompletedValue == ulong.MaxValue)
            throw new InvalidOperationException("The sprite device was removed during fallback upload.");
        submission.ReleaseCompleted(fenceValue);
        _submissions.Remove(submission);
        return texture;
    }

    private static ShaderResourceViewDescription MakeSrv(Format format, ushort mipCount)
    {
        return new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MipLevels = mipCount, MostDetailedMip = 0 }
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
            buffer.Unmap(0);
        }
    }

    // ---- PSO + RootSig + sampler setup --------------------------------------------------

    /// <summary>Reuses a borrowed shared pipeline by native state without allocating a duplicate handle cache.</summary>
    /// <param name="key">Canonical native blend, cull and shader state.</param>
    /// <returns>The borrowed native pipeline for this exact state.</returns>
    private ID3D12PipelineState GetOrCreatePso(GpuSpritePipelineKey key)
    {
        if (_pipelineReady[key.Slot]) return _pipelineResources.GetPipeline(key.Slot);
        var pso = CreatePso(key);
        _pipelineReady[key.Slot] = true;
        return pso;
    }

    /// <summary>Passes the sprite shader and fixed-function declarations unchanged to Shared's owner.</summary>
    /// <param name="key">Native state and its unoccupied bounded slot.</param>
    /// <returns>A borrowed pipeline retained by the shared family.</returns>
    private ID3D12PipelineState CreatePso(GpuSpritePipelineKey key)
    {
        var rasterizer = new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = key.DoubleSided ? CullMode.None : CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            MultisampleEnable = true,
            AntialiasedLineEnable = false
        };

        var depthWriteEnabled = !key.Blended;
        var depth = new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = depthWriteEnabled ? DepthWriteMask.All : DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.LessEqual,
            StencilEnable = false
        };

        var blend = new BlendDescription
        {
            // A2C is done manually in the pixel shader via SV_Coverage (IS_A2C flag):
            // the fixed-function path derives coverage from the WRITTEN alpha, which
            // would force covered samples to carry texture alpha and double-attenuate
            // after resolve. The shader instead writes opaque alpha + a coverage mask.
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false
        };
        if (key.Blended)
        {
            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = true,
                SourceBlend = MapBlend(key.SourceBlend),
                DestinationBlend = MapBlend(key.DestinationBlend),
                BlendOperation = BlendOperation.Add,
                SourceBlendAlpha = Blend.One,
                DestinationBlendAlpha = Blend.One,
                BlendOperationAlpha = BlendOperation.Max,
                RenderTargetWriteMask = ColorWriteEnable.All
            };
        }
        else
        {
            blend.RenderTarget[0] = new RenderTargetBlendDescription
            {
                BlendEnable = false,
                RenderTargetWriteMask = ColorWriteEnable.All
            };
        }

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = _rootSignature,
            VertexShader = _vsBytecode,
            PixelShader = key.ClassicSkin ? _classicSkinPsBytecode : _psBytecode,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(_inputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { Format.R8G8B8A8_UNorm },
            DepthStencilFormat = Format.D32_Float_S8X24_UInt,
            SampleDescription = new SampleDescription(MsaaSampleCount, 0),
            SampleMask = uint.MaxValue
        };
        return _pipelineResources.CreateGraphics(key.Slot, psoDesc);
    }

    private static Blend MapBlend(byte nifBlendMode)
    {
        // Keep the standalone sprite path on the same OpenGL-order NIF mapping as the world
        // renderer — a shifted ordering of modes 4..9 turns the common 6/7 pair into
        // DestinationAlpha/InverseDestinationAlpha instead of SourceAlpha/InverseSourceAlpha,
        // which on a transparent sprite target turns the first blended draw black, and over an
        // opaque draw replaces rather than blends.
        return NifD3D12BlendMapper
            .ResolveBlendFactor(nifBlendMode);
    }

    /// <summary>Describes the existing per-draw CBV, texture table and sampler table without creating native ownership.</summary>
    /// <returns>The sprite root layout and its input-assembler declaration.</returns>
    private static VersionedRootSignatureDescription CreateRootSignatureDescription()
    {
        // Slot 0: root CBV b0 (per-submesh uniforms). VS + PS.
        var cbv = new RootParameter1(
            RootParameterType.ConstantBufferView,
            new RootDescriptor1(0, 0),
            ShaderVisibility.All);

        // Slot 1: SRV table t0..t2 (diffuse + normal + CE2 opacity). PS.
        var srvRange = new DescriptorRange1(
            DescriptorRangeType.ShaderResourceView,
            SrvTableSize,
            0);
        var srvTable = new RootParameter1(new RootDescriptorTable1(srvRange), ShaderVisibility.Pixel);

        // Slot 2: sampler table s0..s2. PS.
        var samplerRange = new DescriptorRange1(
            DescriptorRangeType.Sampler,
            SamplerTableSize,
            0);
        var samplerTable = new RootParameter1(new RootDescriptorTable1(samplerRange), ShaderVisibility.Pixel);

        var desc = new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            new[] { cbv, srvTable, samplerTable });
        return new VersionedRootSignatureDescription(desc);
    }

    /// <summary>Retains the heap before filling its twelve authored-address-mode sampler descriptors.</summary>
    /// <param name="device">Borrowed native device.</param>
    /// <returns>The heap retained until all sprite work retires.</returns>
    private ID3D12DescriptorHeap CreateSamplerHeap(ID3D12Device device)
    {
        var heap = device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = DescriptorHeapType.Sampler,
            DescriptorCount = SamplerHeapSize,
            Flags = DescriptorHeapFlags.ShaderVisible
        });
        _ownedResources.Add(heap, "sprite sampler heap", 1);
        var samplerSize = device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);
        var heapStart = heap.GetCPUDescriptorHandleForHeapStart();
        for (var mode = 0; mode < (int)SamplerModeCount; mode++)
        {
            var sampler = new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = (mode & 1) != 0 ? TextureAddressMode.Clamp : TextureAddressMode.Wrap,
                AddressV = (mode & 2) != 0 ? TextureAddressMode.Clamp : TextureAddressMode.Wrap,
                AddressW = TextureAddressMode.Wrap,
                MaxAnisotropy = 1,
                ComparisonFunction = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue
            };
            // skin.frag.hlsl binds diffuse s0, normal s1, and CE2 opacity s2. Give all three the
            // independently authored material address mode and select one table per draw.
            for (var samplerSlot = 0; samplerSlot < (int)SamplerTableSize; samplerSlot++)
            {
                var descriptorIndex = mode * (int)SamplerTableSize + samplerSlot;
                device.CreateSampler(ref sampler,
                    new CpuDescriptorHandle(heapStart, descriptorIndex, samplerSize));
            }
        }

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

    /// <summary>Gets an embedded shader permutation through the application cache and Shared compiler.</summary>
    /// <param name="name">Embedded shader file name.</param>
    /// <param name="entryPoint">HLSL entry point.</param>
    /// <param name="profile">Native compiler target profile.</param>
    /// <param name="macros">Definitions selecting the shader permutation.</param>
    /// <returns>Read-only cached DXBC passed directly to native pipeline creation without a payload copy.</returns>
    private static ReadOnlyMemory<byte> CompileEmbeddedShader(
        string name,
        string entryPoint,
        string profile,
        params ShaderMacro[] macros)
    {
        return GpuShaderCompiler12.Compile(name, entryPoint, profile, macros);
    }

    // ---- Per-render structures ----------------------------------------------------------

    /// <summary>
    ///     State for an in-flight async sprite render: target dimensions, model bounds, and texture flags, completed
    ///     later by <c>CompleteRender</c>.
    /// </summary>
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
        public required SubmissionResourceRetirement Resources { get; init; }
    }

    private sealed record RenderItem(
        RenderableSubmesh Submesh,
        NifAlphaRenderState AlphaState,
        float AverageZ,
        bool HasDiffuseTexture,
        bool HasNormalTexture,
        string? StarfieldOpacityPath);

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuUniforms
    {
        public Matrix4x4 ViewProj; // 64
        public Matrix4x4 View; // 64
        public Vector4 LightDir; // 16
        public Vector4 HalfVec; // 16
        public Vector4 Ambient; // 16
        public Vector4 Material; // 16
        public Vector4 TintColor; // 16
        public Vector4 Flags; // 16

        public Vector4 EffectTint; // 16 — BSEffect/BGEM base RGB × HDR scale
        // Total: 240 bytes
    }
}
