#if WINDOWS_GUI
using System.Numerics;
using System.Reflection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     v3 textured-sky billboards — the sun (disc + glare) and moon, drawn as camera-facing textured
///     quads at their sky directions. Mirrors the engine, which draws each celestial object as a
///     4-vertex billboard quad with a NiBillboard controller (decompiled Sun::Initialize /
///     Moon::Initialize). Drawn right AFTER the <see cref="SkyGeometryRenderer12" /> sky dome and BEFORE
///     terrain, depth test/write OFF (DSV stays bound) — so depth-written geometry overwrites it and
///     mountains correctly hide the sun.
///     <para>
///         Two PSOs differing only in blend: the sun disc/glare are ADDITIVE (they glow over the sky);
///         the moon is ALPHA (a lit disc over the night sky). Each draw is a 4-vertex triangle-strip
///         quad expanded from <c>SV_VertexID</c> (no vertex buffer), with its own b0 CB carrying the
///         world direction, camera basis, texture index, tint and fade. Samples the shared bindless
///         <c>textures[]</c> table (slot 4, space1) the terrain/reference shaders use.
///     </para>
/// </summary>
internal sealed class SkyBillboardRenderer12 : IDisposable
{
    /// <summary>Sentinel bindless index meaning "texture not available" — the object is skipped.</summary>
    public const uint NoTexture = uint.MaxValue;

    // Sky-sphere radius the billboards sit on (world units). Depth is off, so this only sets the
    // projected screen position/size — kept well inside the camera far plane. Public so the per-game
    // celestial profiles can preserve each authored quad-to-path ratio at this replacement radius.
    public const float Radius = 30000f;

    // Moon glow-halo suppression exponent (see sky_billboard.frag: alpha = pow(texel.a, exp)). The moon
    // textures (masser/secunda) bake a bright DISC at alpha~1 plus a soft GLOW HALO at low alpha; drawn
    // straight (exp=1) the halo reads as an over-opaque smear around the moon (the "moon glow/halo too
    // opaque" report). exp>1 leaves the disc (alpha~1) intact and drives the halo toward transparent.
    // 1.0 = disc+halo drawn as authored; higher = more halo suppression. GUI taste tuning is owed —
    // overridable via FALLOUT_VIEWER_MOON_HALO for that pass.
    private const float DefaultMoonGlowExponent = 2.2f;
    private static readonly float MoonGlowExponent = ResolveMoonGlowExponent();

    private static float ResolveMoonGlowExponent()
    {
        var raw = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_MOON_HALO");
        return float.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 1f
            ? v
            : DefaultMoonGlowExponent;
    }
    // Moon half-extents are NOT a shared constant any more: each engine draws a different number of moons
    // at different apparent sizes, so the per-game SkyMoonProfile supplies moonHalfSize/moon2HalfSize per
    // draw (FNV/Skyrim decompile-grounded, the rest hand-tuned). See SkyMoonProfile.

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly ID3D12PipelineState _psoAdditive;
    private readonly ID3D12PipelineState _psoAlpha;
    private bool _disposed;

    public SkyBillboardRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDescriptorHeapAllocator12 cbvSrvUavHeap)
    {
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _cbvSrvUavHeap = cbvSrvUavHeap;

        var vs = CompileEmbeddedShader("sky_billboard.vert.hlsl", "main", "vs_5_1");
        var ps = CompileEmbeddedShader("sky_billboard.frag.hlsl", "main", "ps_5_1");

        // Depth OFF (DSV stays bound — terrain follows in the same pass and overwrites these).
        var depth = new D12.DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = D12.DepthWriteMask.Zero,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false,
        };

        var rasterizer = new D12.RasterizerDescription
        {
            FillMode = D12.FillMode.Solid,
            CullMode = D12.CullMode.None,
            FrontCounterClockwise = true,
            DepthClipEnable = false,
            MultisampleEnable = gpu.SceneSampleCount > 1,
        };

        _psoAdditive = CreatePso(gpu, rootSignature, vs, ps, depth, rasterizer, additive: true);
        _psoAlpha = CreatePso(gpu, rootSignature, vs, ps, depth, rasterizer, additive: false);
    }

    private static ID3D12PipelineState CreatePso(
        GpuDevice12 gpu, GpuRootSignature12 rootSignature, byte[] vs, byte[] ps,
        D12.DepthStencilDescription depth, D12.RasterizerDescription rasterizer, bool additive)
    {
        var blend = new D12.BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        blend.RenderTarget[0] = new D12.RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = D12.Blend.SourceAlpha,
            DestinationBlend = additive ? D12.Blend.One : D12.Blend.InverseSourceAlpha,
            BlendOperation = D12.BlendOperation.Add,
            SourceBlendAlpha = D12.Blend.One,
            DestinationBlendAlpha = D12.Blend.InverseSourceAlpha,
            BlendOperationAlpha = D12.BlendOperation.Add,
            RenderTargetWriteMask = D12.ColorWriteEnable.All,
        };

        var psoDesc = new GraphicsPipelineStateDescription
        {
            RootSignature = rootSignature.RootSignature,
            VertexShader = vs,
            PixelShader = ps,
            BlendState = blend,
            RasterizerState = rasterizer,
            DepthStencilState = depth,
            InputLayout = new InputLayoutDescription(Array.Empty<InputElementDescription>()),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
            RenderTargetFormats = new[] { GpuSceneFormats.SceneColor },
            DepthStencilFormat = Format.D32_Float,
            SampleDescription = new SampleDescription((uint)gpu.SceneSampleCount, 0),
            SampleMask = uint.MaxValue,
        };
        return gpu.Device.CreateGraphicsPipelineState(psoDesc);
    }

    /// <summary>
    ///     Draws the sun (disc + glare, day) and moon (night) for the given camera. Each object is
    ///     skipped when below the horizon, fully faded, or its texture isn't available. Must be called
    ///     AFTER the skybox gradient and BEFORE terrain, with the descriptor heap already bound.
    /// </summary>
    /// <param name="camRight">Camera world-space right (from the inverse view matrix).</param>
    /// <param name="camUp">Camera world-space up.</param>
    /// <param name="sunDir">Unit world direction toward the sun (+Z up).</param>
    /// <param name="sunColor">Sun tint (warms at sunrise/sunset).</param>
    /// <param name="sunDiscHalfSize">Caller-resolved authored base-quad half-extent.</param>
    /// <param name="sunGlareHalfSize">Caller-resolved authored maximum glare-quad half-extent.</param>
    public void Render(
        Matrix4x4 viewProj, Vector3 camPos, Vector3 camRight, Vector3 camUp,
        Vector3 sunDir, float sunFade, Vector3 sunColor,
        float sunGlareFade, Vector3 sunGlareColor,
        float sunDiscHalfSize, float sunGlareHalfSize, uint sunDiscTex, uint sunGlareTex,
        Vector3 moonDir, float moonFade, Vector3 moonColor, uint moonTex, float moonHalfSize,
        Vector3 moon2Dir, float moon2Fade, Vector3 moon2Color, uint moon2Tex, float moon2HalfSize)
    {
        if (_disposed) return;

        // Sun (day): glare halo first (so the disc reads on top), then the disc. Both additive.
        // glowExp = 1 → the disc/glare are drawn exactly as authored (no halo suppression).
        if (MathF.Max(sunFade, sunGlareFade) > 0.001f && sunDir.Z > -0.05f)
        {
            if (sunGlareFade > 0.001f && sunGlareTex != NoTexture)
            {
                Draw(_psoAdditive, viewProj, camPos, camRight, camUp, sunDir,
                    sunGlareHalfSize, sunGlareFade, sunGlareTex, sunGlareColor, 1f, glowExp: 1f);
            }

            if (sunFade > 0.001f && sunDiscTex != NoTexture)
            {
                Draw(_psoAdditive, viewProj, camPos, camRight, camUp, sunDir,
                    sunDiscHalfSize, sunFade, sunDiscTex, sunColor, 1f, glowExp: 1f);
            }
        }

        // Moon(s) (night): alpha-blended lit discs. The primary moon plus an optional second moon
        // (Secunda for the two-moon TES games), each sized by the caller's per-game SkyMoonProfile.
        // MoonGlowExponent suppresses the texture's over-opaque glow halo (keeps the disc).
        if (moonFade > 0.001f && moonDir.Z > -0.05f && moonTex != NoTexture)
        {
            Draw(_psoAlpha, viewProj, camPos, camRight, camUp, moonDir,
                moonHalfSize, moonFade, moonTex, moonColor, 1f, glowExp: MoonGlowExponent);
        }

        if (moon2Fade > 0.001f && moon2Dir.Z > -0.05f && moon2Tex != NoTexture)
        {
            Draw(_psoAlpha, viewProj, camPos, camRight, camUp, moon2Dir,
                moon2HalfSize, moon2Fade, moon2Tex, moon2Color, 1f, glowExp: MoonGlowExponent);
        }
    }

    private void Draw(
        ID3D12PipelineState pso, Matrix4x4 viewProj, Vector3 camPos, Vector3 camRight, Vector3 camUp,
        Vector3 dir, float halfSize, float fade, uint texIndex, Vector3 tint, float baseAlpha,
        float glowExp)
    {
        var c = new BillboardConstants
        {
            ViewProj = viewProj,
            CenterDirRadius = new Vector4(dir, Radius),
            RightHalfSize = new Vector4(camRight, halfSize),
            UpFade = new Vector4(camUp, fade),
            CamPos = new Vector4(camPos, glowExp),
            Tint = new Vector4(tint, baseAlpha),
            TexIndex = texIndex,
        };

        var frameIndex = _recorder.FrameIndex;
        var cmd = _recorder.CommandList;
        // Soft-fail on ring exhaustion — drop this billboard for the frame rather than throwing.
        if (!_ringBuffer.TryAllocate(frameIndex, BillboardConstants.ByteSize, out var alloc, GpuRingBuffer12.CbAlignment))
        {
            return;
        }
        unsafe { *(BillboardConstants*)alloc.CpuPtr = c; }

        cmd.SetPipelineState(pso);
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, alloc.GpuAddress);
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);
        cmd.DrawInstanced(4, 1, 0, 0); // triangle-strip quad
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _psoAdditive.Dispose();
        _psoAlpha.Dispose();
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BillboardConstants
    {
        public Matrix4x4 ViewProj;       // 64
        public Vector4 CenterDirRadius;  // xyz dir, w radius
        public Vector4 RightHalfSize;    // xyz camera right, w half-size
        public Vector4 UpFade;           // xyz camera up, w fade
        public Vector4 CamPos;           // xyz camera pos, w unused
        public Vector4 Tint;             // rgb tint, a base alpha
        public uint TexIndex;            // bindless texture index (real uint — matches HLSL uint4.x)
        public uint Pad0;
        public uint Pad1;
        public uint Pad2;

        public const uint ByteSize = 64 + (6 * 16); // 160
    }

    /// <summary>
    ///     Forwards to the one shared compiler — see <see cref="GpuShaderCompiler12" />.
    ///     This was one of a dozen copy-pasted private compilers that had drifted apart on
    ///     shader flags and manifest lookup; the flag decision is now made once, unconditionally.
    /// </summary>
    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile) =>
        GpuShaderCompiler12.Compile(name, entryPoint, profile);
}
#endif
