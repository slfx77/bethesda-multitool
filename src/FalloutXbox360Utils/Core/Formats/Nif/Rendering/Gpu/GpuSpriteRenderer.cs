using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Dds;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     GPU-accelerated renderer that produces the same <see cref="SpriteResult" /> as
///     <see cref="NifSpriteRenderer" /> but using Direct3D 11 via Vortice for hardware
///     rasterization. Headless — uses an offscreen color + depth render target, copies
///     to a staging texture for CPU readback. No window required.
/// </summary>
internal sealed class GpuSpriteRenderer : IDisposable
{
    private const int ConstantBufferSize = 224; // sizeof(GpuUniforms), must be 16-byte aligned

    private readonly Dictionary<BlendPipelineKey, PipelineState> _blendPipelines = [];
    private readonly GpuDevice _gpu;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11SamplerState _linearSampler;
    private readonly PipelineState _opaqueDoubleSidedPipeline;
    private readonly PipelineState _opaquePipeline;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly GpuTextureCache _textureCache;
    private readonly ID3D11VertexShader _vertexShader;

    private ID3D11Texture2D? _stagingTexture;
    private uint _stagingHeight;
    private uint _stagingWidth;

    public GpuSpriteRenderer(GpuDevice gpu)
    {
        _gpu = gpu;
        _textureCache = new GpuTextureCache(gpu.Device);

        // Compile HLSL shaders at runtime from embedded resources.
        var vsBytecode = CompileEmbeddedShader("skin.vert.hlsl", "main", "vs_5_0");
        var psBytecode = CompileEmbeddedShader("skin.frag.hlsl", "main", "ps_5_0");

        _vertexShader = gpu.Device.CreateVertexShader(vsBytecode.AsSpan());
        _pixelShader = gpu.Device.CreatePixelShader(psBytecode.AsSpan());
        _inputLayout = gpu.Device.CreateInputLayout(GpuMeshUploader.InputElements, vsBytecode.AsSpan());

        _linearSampler = gpu.Device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            MaxAnisotropy = 1,
            // ComparisonFunc defaults to Never — leave unset for forward compat with property renames.
            MinLOD = 0,
            MaxLOD = float.MaxValue
        });

        _opaquePipeline = CreatePipelineState(gpu.Device, blendAttachment: null,
            depthWriteEnabled: true, doubleSided: false);
        _opaqueDoubleSidedPipeline = CreatePipelineState(gpu.Device, blendAttachment: null,
            depthWriteEnabled: true, doubleSided: true);
    }

    public void Dispose()
    {
        _stagingTexture?.Dispose();
        _textureCache.Dispose();
        _opaquePipeline.Dispose();
        _opaqueDoubleSidedPipeline.Dispose();
        foreach (var pipeline in _blendPipelines.Values)
            pipeline.Dispose();
        _blendPipelines.Clear();
        _linearSampler.Dispose();
        _inputLayout.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    /// <summary>
    ///     Renders a model to a sprite. Convenience wrapper around
    ///     <see cref="SubmitRender" /> + <see cref="CompleteRender" />.
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
    ///     Records GPU commands on the immediate context and queues a copy-to-staging-texture.
    ///     The GPU executes asynchronously — the caller can do CPU work before
    ///     <see cref="CompleteRender" /> blocks on staging Map. Returns null if the model
    ///     has no geometry.
    /// </summary>
    public PendingRender? SubmitRender(NifRenderableModel model,
        NifTextureResolver? textureResolver,
        float pixelsPerUnit, int minSize, int maxSize,
        float azimuthDeg, float elevationDeg,
        int? fixedSize = null)
    {
        if (!model.HasGeometry)
            return null;

        var (projMinX, projMinY, projWidth, projHeight, viewMatrix) =
            ComputeViewBounds(model, azimuthDeg, elevationDeg);

        if (projWidth < 0.001f && projHeight < 0.001f)
            return null;

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
        const float margin = 1f;
        var orthoLeft = projMinX - margin;
        var orthoRight = projMinX + projWidth + margin;
        var orthoTop = projMinY - margin; // min view Y (top of image)
        var orthoBottom = projMinY + projHeight + margin; // max view Y (bottom of image)
        const float orthoNear = -10000f;
        const float orthoFar = 10000f;

        var projMatrix = Matrix4x4.CreateOrthographicOffCenter(
            orthoLeft, orthoRight, orthoBottom, orthoTop, orthoNear, orthoFar);

        var viewProj = viewMatrix * projMatrix;

        // Allocate offscreen color + depth at supersampled resolution for SSAA.
        var device = _gpu.Device;
        var context = _gpu.Context;
        var ssWidth = (uint)(width * RenderLightingConstants.SsaaFactor);
        var ssHeight = (uint)(height * RenderLightingConstants.SsaaFactor);

        var colorTex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = ssWidth,
            Height = ssHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
        var colorRtv = device.CreateRenderTargetView(colorTex);

        var depthTex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = ssWidth,
            Height = ssHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float_S8X24_UInt,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
        var depthDsv = device.CreateDepthStencilView(depthTex);

        // Classify + sort draw items the same way the CPU path does.
        var renderItems = new List<RenderItem>();
        foreach (var sub in model.Submeshes)
        {
            if (sub.TriangleCount == 0 || sub.VertexCount == 0)
                continue;

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

        // Collect per-submesh GPU resources for deferred disposal in CompleteRender.
        var disposables = new List<IDisposable> { colorRtv, depthDsv };

        // Bind the offscreen render target + viewport (immediate-context state).
        context.OMSetRenderTargets(colorRtv, depthDsv);
        context.ClearRenderTargetView(colorRtv, new Color4(0f, 0f, 0f, 0f));
        context.ClearDepthStencilView(depthDsv, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 1f, 0);
        context.RSSetViewport(0, 0, ssWidth, ssHeight, 0, 1);
        context.IASetInputLayout(_inputLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);

        foreach (var item in ordered)
        {
            var sub = item.Submesh;
            var alphaState = item.AlphaState;
            var hasDiffuse = item.HasDiffuseTexture;

            // Build flags bitfield (must match the bit positions in skin.frag.hlsl).
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

            // Per-submesh constant buffer — separate buffers avoid overwrite races inside one frame.
            var cbDesc = new BufferDescription
            {
                ByteWidth = ConstantBufferSize,
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            var uniformsArray = new[] { uniforms };
            var uniformsGc = GCHandle.Alloc(uniformsArray, GCHandleType.Pinned);
            ID3D11Buffer constantBuffer;
            try
            {
                constantBuffer = device.CreateBuffer(cbDesc,
                    new SubresourceData(uniformsGc.AddrOfPinnedObject(), ConstantBufferSize));
            }
            finally
            {
                uniformsGc.Free();
            }

            // Mesh upload
            var vertices = GpuMeshUploader.BuildVertices(sub);
            var vb = GpuMeshUploader.CreateVertexBuffer(device, vertices);
            var ib = GpuMeshUploader.CreateIndexBuffer(device, sub.Triangles);

            // Texture binds
            var diffuseSrv = _textureCache.WhitePixel;
            var normalSrv = _textureCache.FlatNormal;
            if (hasDiffuse && textureResolver != null && sub.DiffuseTexturePath != null)
                diffuseSrv = _textureCache.GetOrUpload(sub.DiffuseTexturePath, textureResolver);
            if (textureResolver != null && sub.NormalMapTexturePath != null)
                normalSrv = _textureCache.GetOrUpload(sub.NormalMapTexturePath, textureResolver);

            // Pipeline selection
            PipelineState pipeline;
            if (alphaState.RenderMode == NifAlphaRenderMode.Blend)
                pipeline = GetBlendPipeline(alphaState.SrcBlendMode, alphaState.DstBlendMode, sub.IsDoubleSided);
            else
                pipeline = sub.IsDoubleSided ? _opaqueDoubleSidedPipeline : _opaquePipeline;

            context.RSSetState(pipeline.RasterizerState);
            context.OMSetDepthStencilState(pipeline.DepthStencilState);
            context.OMSetBlendState(pipeline.BlendState, new Color4(1f, 1f, 1f, 1f));

            context.VSSetConstantBuffer(0, constantBuffer);
            context.PSSetConstantBuffer(0, constantBuffer);
            context.PSSetShaderResource(0, diffuseSrv);
            context.PSSetSampler(0, _linearSampler);
            context.PSSetShaderResource(1, normalSrv);
            context.PSSetSampler(1, _linearSampler);

            context.IASetVertexBuffer(0, vb, (uint)Marshal.SizeOf<GpuMeshUploader.GpuVertex>());
            context.IASetIndexBuffer(ib, Format.R16_UInt, 0);

            context.DrawIndexed((uint)sub.Triangles.Length, 0, 0);

            disposables.Add(vb);
            disposables.Add(ib);
            disposables.Add(constantBuffer);
        }

        // Append readback copy. Reuse staging texture across frames when dimensions match.
        if (_stagingTexture == null || _stagingWidth != ssWidth || _stagingHeight != ssHeight)
        {
            _stagingTexture?.Dispose();
            _stagingTexture = device.CreateTexture2D(new Texture2DDescription
            {
                Width = ssWidth,
                Height = ssHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            });
            _stagingWidth = ssWidth;
            _stagingHeight = ssHeight;
        }

        context.CopyResource(_stagingTexture!, colorTex);

        // GPU is now executing asynchronously — caller can do CPU work before CompleteRender().
        return new PendingRender
        {
            Width = width,
            Height = height,
            SsWidth = (int)ssWidth,
            SsHeight = (int)ssHeight,
            BoundsWidth = projWidth,
            BoundsHeight = projHeight,
            HasTexture = ordered.Any(item => item.HasDiffuseTexture),
            Disposables = disposables,
            ColorTexture = colorTex,
            DepthTexture = depthTex
        };
    }

    /// <summary>
    ///     Maps the staging texture (implicit GPU sync) and reads back the pixels.
    ///     Disposes per-frame GPU resources.
    /// </summary>
    public SpriteResult CompleteRender(PendingRender pending)
    {
        var context = _gpu.Context;
        var ssPixels = ReadBackStagingPixels(context, (uint)pending.SsWidth, (uint)pending.SsHeight);
        var pixels = NifSpriteRenderer.Downsample(ssPixels, pending.SsWidth, pending.SsHeight,
            RenderLightingConstants.SsaaFactor);

        foreach (var d in pending.Disposables)
            d.Dispose();
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
    ///     Evicts a specific texture from the GPU cache.
    ///     Call after rendering each NPC to free per-NPC morphed face textures.
    /// </summary>
    public void EvictTexture(string key)
    {
        _textureCache.EvictTexture(key);
    }

    private byte[] ReadBackStagingPixels(ID3D11DeviceContext context, uint width, uint height)
    {
        // Map() on a STAGING texture with MapMode.Read blocks until prior GPU work targeting
        // this resource (the CopyResource we queued) completes — no explicit Flush needed.
        var mapped = context.Map(_stagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[width * height * 4];
            var rowSize = (int)(width * 4);
            for (uint y = 0; y < height; y++)
            {
                var srcOffset = (int)(y * mapped.RowPitch);
                var dstOffset = (int)(y * rowSize);
                Marshal.Copy(mapped.DataPointer + srcOffset, pixels, dstOffset, rowSize);
            }

            return pixels;
        }
        finally
        {
            context.Unmap(_stagingTexture!, 0);
        }
    }

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

        // Build view matrix with basis vectors as COLUMNS in System.Numerics row-major bytes;
        // both GLSL and HLSL re-interpret cbuffer bytes as column-major, so basis-as-columns
        // is what `mul(uView, vec)` (HLSL) and `uView * vec` (GLSL) both expect.
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

    private static Blend ResolveBlendFactor(byte mode)
    {
        // NIF alpha property blend modes follow OpenGL enumeration order.
        return mode switch
        {
            0 => Blend.One,
            1 => Blend.Zero,
            2 => Blend.SourceColor,
            3 => Blend.InverseSourceColor,
            4 => Blend.DestinationColor,
            5 => Blend.InverseDestinationColor,
            6 => Blend.SourceAlpha,
            7 => Blend.InverseSourceAlpha,
            8 => Blend.DestinationAlpha,
            9 => Blend.InverseDestinationAlpha,
            10 => Blend.One, // GL_SRC_ALPHA_SATURATE — no D3D equivalent; approximate as One
            _ => Blend.SourceAlpha
        };
    }

    private static PipelineState CreatePipelineState(
        ID3D11Device device,
        RenderTargetBlendDescription? blendAttachment,
        bool depthWriteEnabled,
        bool doubleSided)
    {
        var rasterizer = device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = doubleSided ? CullMode.None : CullMode.Back,
            FrontCounterClockwise = true,
            DepthClipEnable = true,
            ScissorEnable = false,
            MultisampleEnable = false,
            AntialiasedLineEnable = false
        });

        var depthStencil = device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = true,
            DepthWriteMask = depthWriteEnabled ? DepthWriteMask.All : DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.LessEqual,
            StencilEnable = false
        });

        var rtBlend = blendAttachment ?? new RenderTargetBlendDescription
        {
            BlendEnable = false,
            SourceBlend = Blend.One,
            DestinationBlend = Blend.Zero,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.Zero,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };

        var blendDesc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false
        };
        blendDesc.RenderTarget[0] = rtBlend;
        var blend = device.CreateBlendState(blendDesc);

        return new PipelineState(rasterizer, depthStencil, blend);
    }

    private PipelineState GetBlendPipeline(byte srcBlendMode, byte dstBlendMode, bool doubleSided)
    {
        var key = new BlendPipelineKey(srcBlendMode, dstBlendMode, doubleSided);
        if (_blendPipelines.TryGetValue(key, out var existing))
            return existing;

        var rtBlend = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = ResolveBlendFactor(srcBlendMode),
            DestinationBlend = ResolveBlendFactor(dstBlendMode),
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Max,
            RenderTargetWriteMask = ColorWriteEnable.All
        };

        var pipeline = CreatePipelineState(_gpu.Device, rtBlend, depthWriteEnabled: false, doubleSided);
        _blendPipelines[key] = pipeline;
        return pipeline;
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

        var compileResult = Compiler.Compile(
            source,
            entryPoint,
            sourceName: name,
            profile,
            out Blob? bytecode,
            out Blob? errors);

        if (compileResult.Failure || bytecode is null)
        {
            var errorText = errors?.AsString() ?? "(no error blob)";
            errors?.Dispose();
            bytecode?.Dispose();
            throw new InvalidOperationException(
                $"HLSL compile failed for {name} ({profile}): {errorText}");
        }

        errors?.Dispose();
        try
        {
            return bytecode.AsBytes().ToArray();
        }
        finally
        {
            bytecode.Dispose();
        }
    }

    /// <summary>
    ///     Holds intermediate state between <see cref="SubmitRender" /> and <see cref="CompleteRender" />.
    ///     GPU commands have been recorded but the staging Map has not blocked yet.
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
        public required ID3D11Texture2D ColorTexture { get; init; }
        public required ID3D11Texture2D DepthTexture { get; init; }
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

    private readonly struct PipelineState : IDisposable
    {
        public PipelineState(
            ID3D11RasterizerState rasterizerState,
            ID3D11DepthStencilState depthStencilState,
            ID3D11BlendState blendState)
        {
            RasterizerState = rasterizerState;
            DepthStencilState = depthStencilState;
            BlendState = blendState;
        }

        public ID3D11RasterizerState RasterizerState { get; }
        public ID3D11DepthStencilState DepthStencilState { get; }
        public ID3D11BlendState BlendState { get; }

        public void Dispose()
        {
            BlendState.Dispose();
            DepthStencilState.Dispose();
            RasterizerState.Dispose();
        }
    }

    /// <summary>
    ///     GPU uniform buffer layout — must match HLSL cbuffer Uniforms exactly.
    ///     Each Vector4 / Matrix4x4 is naturally 16-byte aligned.
    /// </summary>
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
