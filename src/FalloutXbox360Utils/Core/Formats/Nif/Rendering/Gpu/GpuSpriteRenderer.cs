using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Dds;
using Veldrid;
using Veldrid.SPIRV;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     GPU-accelerated renderer that produces the same <see cref="SpriteResult" /> as
///     <see cref="NifSpriteRenderer" /> but using Veldrid for hardware rasterization.
///     Supports headless rendering (no window required) on Vulkan, D3D11, and OpenGL.
/// </summary>
internal sealed class GpuSpriteRenderer : IDisposable
{
    private readonly Dictionary<BlendPipelineKey, Pipeline> _blendPipelines = [];

    private readonly GpuDevice _gpu;
    private readonly Sampler _linearSampler;
    private readonly Pipeline _opaqueDoubleSidedPipeline;
    private readonly Pipeline _opaquePipeline;
    private readonly Shader[] _shaders;
    private readonly GpuTextureCache _textureCache;
    private readonly ResourceLayout _textureLayout;
    private readonly ResourceLayout _uniformLayout;
    private uint _stagingHeight;

    // Persistent staging texture for pixel readback — avoids per-frame create/destroy overhead.
    // Resized lazily when the render target dimensions change.
    private Texture? _stagingTexture;
    private uint _stagingWidth;

    public GpuSpriteRenderer(GpuDevice gpu)
    {
        _gpu = gpu;
        _textureCache = new GpuTextureCache(gpu.Device);

        var factory = gpu.Factory;

        // Load and compile shaders from embedded resources
        var vertSource = LoadEmbeddedShader("skin.vert.glsl");
        var fragSource = LoadEmbeddedShader("skin.frag.glsl");

        var vertDesc = new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(vertSource), "main");
        var fragDesc = new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(fragSource), "main");
        _shaders = factory.CreateFromSpirv(vertDesc, fragDesc);

        // Resource layouts
        _uniformLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("Uniforms", ResourceKind.UniformBuffer,
                ShaderStages.Vertex | ShaderStages.Fragment)));

        _textureLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("tDiffuse", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("sDiffuse", ResourceKind.Sampler, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("tNormalMap", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("sNormalMap", ResourceKind.Sampler, ShaderStages.Fragment)));

        // Linear sampler with wrapping
        _linearSampler = factory.CreateSampler(new SamplerDescription(
            SamplerAddressMode.Wrap, SamplerAddressMode.Wrap, SamplerAddressMode.Wrap,
            SamplerFilter.MinLinear_MagLinear_MipLinear,
            null, 0, 0, 0, 0, SamplerBorderColor.TransparentBlack));

        _opaquePipeline = CreatePipeline(factory, null, true, false);
        _opaqueDoubleSidedPipeline = CreatePipeline(factory, null, true, true);
    }

    public void Dispose()
    {
        _stagingTexture?.Dispose();
        _textureCache.Dispose();
        _opaquePipeline.Dispose();
        _opaqueDoubleSidedPipeline.Dispose();
        foreach (var pipeline in _blendPipelines.Values)
        {
            pipeline.Dispose();
        }

        _uniformLayout.Dispose();
        _textureLayout.Dispose();
        _linearSampler.Dispose();
        foreach (var shader in _shaders)
            shader.Dispose();
    }

    private Pipeline CreatePipeline(
        ResourceFactory factory,
        BlendAttachmentDescription? blendAttachment,
        bool depthWriteEnabled,
        bool doubleSided)
    {
        var blendState = blendAttachment.HasValue
            ? new BlendStateDescription(RgbaFloat.White, blendAttachment.Value)
            : new BlendStateDescription(
                RgbaFloat.White,
                new BlendAttachmentDescription(
                    false,
                    BlendFactor.One,
                    BlendFactor.Zero,
                    BlendFunction.Add,
                    BlendFactor.One,
                    BlendFactor.Zero,
                    BlendFunction.Add));

        return factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(
            blendState,
            new DepthStencilStateDescription(true, depthWriteEnabled, ComparisonKind.LessEqual),
            new RasterizerStateDescription(
                doubleSided ? FaceCullMode.None : FaceCullMode.Back,
                PolygonFillMode.Solid,
                FrontFace.CounterClockwise,
                true,
                false),
            PrimitiveTopology.TriangleList,
            new ShaderSetDescription([GpuMeshUploader.VertexLayout], _shaders),
            [_uniformLayout, _textureLayout],
            new OutputDescription(
                new OutputAttachmentDescription(PixelFormat.D32_Float_S8_UInt),
                new OutputAttachmentDescription(PixelFormat.R8_G8_B8_A8_UNorm))));
    }

    private Pipeline GetBlendPipeline(byte srcBlendMode, byte dstBlendMode, bool doubleSided)
    {
        var key = new BlendPipelineKey(srcBlendMode, dstBlendMode, doubleSided);
        if (_blendPipelines.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var blendAttachment = new BlendAttachmentDescription(
            true,
            ResolveBlendFactor(srcBlendMode),
            ResolveBlendFactor(dstBlendMode),
            BlendFunction.Add,
            BlendFactor.One,
            BlendFactor.One,
            BlendFunction.Maximum);

        var pipeline = CreatePipeline(_gpu.Factory, blendAttachment, false, doubleSided);
        _blendPipelines[key] = pipeline;
        return pipeline;
    }

    /// <summary>
    ///     Renders a model to a sprite, matching the API of <see cref="NifSpriteRenderer.Render" />.
    ///     Convenience wrapper around <see cref="SubmitRender" /> + <see cref="CompleteRender" />.
    /// </summary>
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

    /// <summary>
    ///     Builds GPU command list and submits it. Returns a <see cref="PendingRender" />
    ///     handle to retrieve results via <see cref="CompleteRender" />. Returns null if
    ///     the model has no geometry. After this call returns, the GPU executes asynchronously —
    ///     the caller can do CPU work (e.g., build next NPC model) before calling CompleteRender.
    /// </summary>
    public PendingRender? SubmitRender(NifRenderableModel model,
        NifTextureResolver? textureResolver,
        float pixelsPerUnit, int minSize, int maxSize,
        float azimuthDeg, float elevationDeg,
        int? fixedSize = null)
    {
        if (!model.HasGeometry)
            return null;

        // Apply view rotation to get projected bounds (same logic as NifSpriteRenderer)
        var (projMinX, projMinY, projWidth, projHeight, viewMatrix) =
            ComputeViewBounds(model, azimuthDeg, elevationDeg);

        if (projWidth < 0.001f && projHeight < 0.001f)
            return null;

        // Compute canvas size (same logic as NifSpriteRenderer.RenderCore)
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

        // Build orthographic projection that maps the model's projected bounds to the viewport.
        // In view space, Y increases downward (the "screen_down" basis vector from the CPU
        // renderer). In the GPU framebuffer, row 0 = top of image, so we flip Y by passing
        // maxViewY as "bottom" and minViewY as "top" in the ortho projection.
        var margin = 1f; // 1 unit margin
        var orthoLeft = projMinX - margin;
        var orthoRight = projMinX + projWidth + margin;
        var orthoTop = projMinY - margin; // min view Y (top of image)
        var orthoBottom = projMinY + projHeight + margin; // max view Y (bottom of image)
        var orthoNear = -10000f;
        var orthoFar = 10000f;

        var projMatrix = Matrix4x4.CreateOrthographicOffCenter(
            orthoLeft, orthoRight, orthoBottom, orthoTop, orthoNear, orthoFar);

        var viewProj = viewMatrix * projMatrix;

        // Create offscreen framebuffer at supersampled resolution for SSAA
        var device = _gpu.Device;
        var factory = device.ResourceFactory;
        var ssWidth = width * RenderLightingConstants.SsaaFactor;
        var ssHeight = height * RenderLightingConstants.SsaaFactor;

        var colorTex = factory.CreateTexture(new TextureDescription(
            (uint)ssWidth, (uint)ssHeight, 1, 1, 1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.RenderTarget | TextureUsage.Sampled,
            TextureType.Texture2D));

        var depthTex = factory.CreateTexture(new TextureDescription(
            (uint)ssWidth, (uint)ssHeight, 1, 1, 1,
            PixelFormat.D32_Float_S8_UInt,
            TextureUsage.DepthStencil,
            TextureType.Texture2D));

        var framebuffer = factory.CreateFramebuffer(new FramebufferDescription(depthTex, colorTex));

        var renderItems = new List<RenderItem>();
        foreach (var sub in model.Submeshes)
        {
            if (sub.TriangleCount == 0 || sub.VertexCount == 0)
            {
                continue;
            }

            DecodedTexture? diffuseTexture = null;
            if (textureResolver != null && sub.DiffuseTexturePath != null)
            {
                diffuseTexture = textureResolver.GetTexture(sub.DiffuseTexturePath);
            }

            // Skip untextured submeshes using the same criteria as the CPU path.
            if (textureResolver != null && diffuseTexture == null &&
                sub.DiffuseTexturePath == null &&
                !(sub.IsEmissive && sub.DiffuseTexturePath != null) &&
                !(sub.UseVertexColors && sub.VertexColors != null))
            {
                continue;
            }

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

        // Collect per-submesh GPU resources for deferred disposal after single submit.
        // Each submesh gets its own uniform buffer to avoid overwrite conflicts when
        // all draw calls are batched in a single command list.
        var disposables = new List<IDisposable>();

        var cl = factory.CreateCommandList();
        cl.Begin();
        cl.SetFramebuffer(framebuffer);
        cl.ClearColorTarget(0, RgbaFloat.Clear);
        cl.ClearDepthStencil(1f);
        cl.SetViewport(0, new Viewport(0, 0, ssWidth, ssHeight, 0, 1));

        foreach (var item in ordered)
        {
            var sub = item.Submesh;
            var alphaState = item.AlphaState;
            var hasDiffuse = item.HasDiffuseTexture;

            // Build flags bitfield
            uint flags = 0;
            if (hasDiffuse) flags |= 1; // HAS_TEXTURE
            if (sub.Normals != null) flags |= 2; // HAS_NORMALS
            if (sub.Tangents != null && sub.Bitangents != null && sub.NormalMapTexturePath != null)
                flags |= 4; // HAS_BUMP
            if (NifVertexColorPolicy.HasVertexColorData(sub))
                flags |= 8; // HAS_VCOL
            if (sub.IsEmissive) flags |= 16; // IS_EMISSIVE
            if (sub.IsDoubleSided) flags |= 32; // IS_DOUBLE_SIDED
            if (alphaState.HasAlphaBlend) flags |= 64; // HAS_ALPHA_BLEND
            if (alphaState.HasAlphaTest) flags |= 128; // HAS_ALPHA_TEST
            if (sub.IsEyeEnvmap) flags |= 256; // IS_EYE_ENVMAP
            if (sub.TintColor.HasValue) flags |= 512; // HAS_TINT
            if (sub.IsFaceGen) flags |= 1024; // IS_FACEGEN

            // Per-submesh uniform buffer (each submesh needs different material/flags)
            var subUniformBuffer = factory.CreateBuffer(new BufferDescription(
                (uint)Marshal.SizeOf<GpuUniforms>(), BufferUsage.UniformBuffer));
            var uniforms = new GpuUniforms
            {
                ViewProj = viewProj,
                View = viewMatrix,
                LightDir = new Vector4(RenderLightingConstants.LightDir, 0),
                HalfVec = new Vector4(RenderLightingConstants.HalfVec, RenderLightingConstants.HdotNegL),
                Ambient = new Vector4(RenderLightingConstants.SkyAmbient, RenderLightingConstants.GroundAmbient,
                    RenderLightingConstants.LightIntensity,
                    NifSpriteRenderer.BumpStrength),
                Material = new Vector4(alphaState.MaterialAlpha, sub.EnvMapScale,
                    alphaState.AlphaTestThreshold / 255f, alphaState.AlphaTestFunction),
                TintColor = new Vector4(
                    sub.TintColor?.R ?? 1f, sub.TintColor?.G ?? 1f, sub.TintColor?.B ?? 1f, 0),
                Flags = new Vector4(flags,
                    sub.SubsurfaceColor.R, sub.SubsurfaceColor.G, sub.SubsurfaceColor.B)
            };
            device.UpdateBuffer(subUniformBuffer, 0, uniforms);

            var subUniformSet = factory.CreateResourceSet(new ResourceSetDescription(
                _uniformLayout, subUniformBuffer));

            // Upload mesh data
            var vertices = GpuMeshUploader.BuildVertices(sub);
            var vb = GpuMeshUploader.CreateVertexBuffer(device, vertices);
            var ib = GpuMeshUploader.CreateIndexBuffer(device, sub.Triangles);

            // Bind textures
            var diffuseTex = _textureCache.WhitePixel;
            var normalMapTex = _textureCache.FlatNormal;

            if (hasDiffuse && textureResolver != null && sub.DiffuseTexturePath != null)
                diffuseTex = _textureCache.GetOrUpload(sub.DiffuseTexturePath, textureResolver);
            if (textureResolver != null && sub.NormalMapTexturePath != null)
                normalMapTex = _textureCache.GetOrUpload(sub.NormalMapTexturePath, textureResolver);

            var texSet = factory.CreateResourceSet(new ResourceSetDescription(
                _textureLayout, diffuseTex, _linearSampler, normalMapTex, _linearSampler));

            // Select pipeline variant
            var pipeline = alphaState.RenderMode == NifAlphaRenderMode.Blend
                ? GetBlendPipeline(alphaState.SrcBlendMode, alphaState.DstBlendMode, sub.IsDoubleSided)
                : sub.IsDoubleSided
                    ? _opaqueDoubleSidedPipeline
                    : _opaquePipeline;

            cl.SetPipeline(pipeline);
            cl.SetGraphicsResourceSet(0, subUniformSet);
            cl.SetGraphicsResourceSet(1, texSet);
            cl.SetVertexBuffer(0, vb);
            cl.SetIndexBuffer(ib, IndexFormat.UInt16);
            cl.DrawIndexed((uint)sub.Triangles.Length);

            disposables.Add(vb);
            disposables.Add(ib);
            disposables.Add(texSet);
            disposables.Add(subUniformBuffer);
            disposables.Add(subUniformSet);
        }

        // Append readback copy to the SAME command list — single GPU submission + single WaitForIdle.
        // Reuse staging texture if dimensions match, otherwise recreate.
        if (_stagingTexture == null || _stagingWidth != (uint)ssWidth || _stagingHeight != (uint)ssHeight)
        {
            _stagingTexture?.Dispose();
            _stagingTexture = factory.CreateTexture(new TextureDescription(
                (uint)ssWidth, (uint)ssHeight, 1, 1, 1,
                PixelFormat.R8_G8_B8_A8_UNorm,
                TextureUsage.Staging,
                TextureType.Texture2D));
            _stagingWidth = (uint)ssWidth;
            _stagingHeight = (uint)ssHeight;
        }

        cl.CopyTexture(colorTex, _stagingTexture);
        cl.End();
        device.SubmitCommands(cl);

        // GPU is now executing asynchronously — caller can do CPU work before CompleteRender().
        return new PendingRender
        {
            Width = width,
            Height = height,
            SsWidth = ssWidth,
            SsHeight = ssHeight,
            BoundsWidth = projWidth,
            BoundsHeight = projHeight,
            HasTexture = ordered.Any(item => item.HasDiffuseTexture),
            Disposables = disposables,
            CommandList = cl,
            Framebuffer = framebuffer,
            ColorTexture = colorTex,
            DepthTexture = depthTex
        };
    }

    /// <summary>
    ///     Waits for a previously submitted GPU render to complete, reads back the pixels,
    ///     and returns the final sprite result. Disposes per-frame GPU resources.
    /// </summary>
    public SpriteResult CompleteRender(PendingRender pending)
    {
        var device = _gpu.Device;
        device.WaitForIdle();

        // Read pixels from staging texture
        var ssPixels = ReadBackStagingPixels(device, (uint)pending.SsWidth, (uint)pending.SsHeight);
        var pixels = NifSpriteRenderer.Downsample(ssPixels, pending.SsWidth, pending.SsHeight,
            RenderLightingConstants.SsaaFactor);

        // Cleanup: dispose per-submesh resources, then per-frame resources
        foreach (var d in pending.Disposables)
            d.Dispose();
        pending.CommandList.Dispose();
        pending.Framebuffer.Dispose();
        pending.ColorTexture.Dispose();
        pending.DepthTexture.Dispose();

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

    /// <summary>
    ///     Convenience overload for top-down rendering (no view rotation).
    /// </summary>
    public SpriteResult? Render(NifRenderableModel model,
        NifTextureResolver? textureResolver = null,
        float pixelsPerUnit = 1.0f, int minSize = 32, int maxSize = 1024,
        int? fixedSize = null)
    {
        if (!model.HasGeometry) return null;

        return Render(model, textureResolver, pixelsPerUnit, minSize, maxSize,
            0f, 90f, fixedSize);
    }

    /// <summary>
    ///     Computes the view-space bounds and view matrix for a given camera angle.
    ///     Same rotation math as NifSpriteRenderer.ApplyViewRotation.
    /// </summary>
    private static (float MinX, float MinY, float Width, float Height, Matrix4x4 ViewMatrix)
        ComputeViewBounds(NifRenderableModel model, float azimuthDeg, float elevationDeg)
    {
        var alpha = azimuthDeg * MathF.PI / 180f;
        var theta = elevationDeg * MathF.PI / 180f;

        var ca = MathF.Cos(alpha);
        var sa = MathF.Sin(alpha);
        var ct = MathF.Cos(theta);
        var st = MathF.Sin(theta);

        // View basis vectors (same as NifSpriteRenderer)
        var right = new Vector3(-sa, ca, 0);
        var up = new Vector3(st * ca, st * sa, -ct);
        var forward = new Vector3(ca * ct, sa * ct, st);

        // Build view matrix with basis vectors as COLUMNS (not rows).
        // The CPU renderer uses RotatePoint which computes M * pos (matrix × column vector),
        // where M has basis vectors as rows. System.Numerics' Vector3.Transform(pos, M)
        // computes pos * M (row vector × matrix). To get the same result, we need the
        // transpose: basis vectors as columns. This also ensures the GLSL shader
        // (which reinterprets row-major bytes as column-major) sees the correct rotation.
        var viewMatrix = new Matrix4x4(
            right.X, up.X, forward.X, 0,
            right.Y, up.Y, forward.Y, 0,
            right.Z, up.Z, forward.Z, 0,
            0, 0, 0, 1);

        // Project all vertices to find bounds
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

    private static BlendFactor ResolveBlendFactor(byte mode)
    {
        return mode switch
        {
            0 => BlendFactor.One,
            1 => BlendFactor.Zero,
            2 => BlendFactor.SourceAlpha,
            3 => BlendFactor.InverseSourceAlpha,
            6 => BlendFactor.SourceAlpha,
            7 => BlendFactor.InverseSourceAlpha,
            _ => BlendFactor.SourceAlpha
        };
    }

    /// <summary>
    ///     Reads pixels from the persistent staging texture (already populated via CopyTexture
    ///     in the main command list). No additional GPU submission needed.
    /// </summary>
    private byte[] ReadBackStagingPixels(GraphicsDevice device, uint width, uint height)
    {
        var map = device.Map(_stagingTexture!, MapMode.Read);
        var pixels = new byte[width * height * 4];

        // Copy row-by-row to handle potential row pitch differences
        var rowSize = width * 4;
        for (uint y = 0; y < height; y++)
        {
            var srcOffset = (int)(y * map.RowPitch);
            var dstOffset = (int)(y * rowSize);
            Marshal.Copy(map.Data + srcOffset, pixels, dstOffset, (int)rowSize);
        }

        device.Unmap(_stagingTexture!);
        return pixels;
    }

    private static string LoadEmbeddedShader(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new FileNotFoundException($"Embedded shader resource not found: {name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    ///     Evicts a specific texture from the GPU cache.
    ///     Call after rendering each NPC to free per-NPC morphed face textures.
    /// </summary>
    public void EvictTexture(string key)
    {
        _textureCache.EvictTexture(key);
    }

    /// <summary>
    ///     Holds intermediate state between <see cref="SubmitRender" /> and <see cref="CompleteRender" />.
    ///     GPU commands have been submitted but not yet waited on.
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
        public required List<IDisposable> Disposables { get; init; }
        public required CommandList CommandList { get; init; }
        public required Framebuffer Framebuffer { get; init; }
        public required Texture ColorTexture { get; init; }
        public required Texture DepthTexture { get; init; }
    }

    private readonly record struct BlendPipelineKey(
        byte SrcBlendMode,
        byte DstBlendMode,
        bool DoubleSided);

    private sealed record RenderItem(
        RenderableSubmesh Submesh,
        NifAlphaRenderState AlphaState,
        float AverageZ,
        bool HasDiffuseTexture);

    /// <summary>
    ///     GPU uniform buffer layout — must match shader Uniforms block exactly.
    ///     Uses vec4 packing for std140 alignment.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuUniforms
    {
        public Matrix4x4 ViewProj; // 64 bytes
        public Matrix4x4 View; // 64 bytes (3x3 view rotation for normals)
        public Vector4 LightDir; // 16 bytes (xyz = dir, w = 0)
        public Vector4 HalfVec; // 16 bytes (xyz = half, w = HdotNegL)
        public Vector4 Ambient; // 16 bytes (x=sky, y=ground, z=lightInt, w=bumpStr)
        public Vector4 Material; // 16 bytes (x=alpha, y=envmap, z=alphaThresh, w=alphaFunc)
        public Vector4 TintColor; // 16 bytes (rgb=tint, a=0)

        public Vector4 Flags; // 16 bytes (x=flags bitfield as float, yzw=0)
        // Total: 224 bytes
    }
}
