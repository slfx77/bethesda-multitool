#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     v3 textured-sky billboards — the sun (disc + glare) and moon, drawn as camera-facing textured
///     quads at their sky directions. Mirrors the engine, which draws each celestial object as a
///     4-vertex billboard quad with a NiBillboard controller (decompiled Sun::Initialize /
///     Moon::Initialize). FO3/FNV stage the alpha-blended sun base between stars and clouds, then draw
///     the additive glare after clouds; other games retain the established unified billboard route.
///     Every billboard remains before terrain, depth test/write OFF (DSV stays bound), so depth-written
///     geometry overwrites it and mountains correctly hide the sun.
///     <para>
///         Two PSOs differ only in blend: source-alpha/inverse-source-alpha for the FO3/FNV sun base
///         and moons, and source-alpha/one for glare (plus the legacy unified sun route). Each draw is
///         a 4-vertex triangle-strip quad expanded from <c>SV_VertexID</c> (no vertex buffer), with its
///         own b0 CB carrying the world direction, camera basis, texture index, tint and fade. Samples
///         the shared bindless <c>textures[]</c> table (slot 4, space1) used by terrain/references.
///     </para>
/// </summary>
internal sealed class SkyBillboardRenderer12 : IDisposable
{
    /// <summary>Sentinel bindless index meaning "texture not available" — the object is skipped.</summary>
    public const uint NoTexture = uint.MaxValue;

    // Sky-sphere radius the billboards sit on, in CLASSIC world units (~70/metre). Depth is off, so
    // this only sets the projected screen position/size — kept well inside the camera far plane.
    // Public so the per-game celestial profiles can preserve each authored quad-to-path ratio at this
    // replacement radius.
    public const float ClassicRadius = 30000f;

    /// <summary>
    ///     Live sky-sphere radius (world units). Defaults to <see cref="ClassicRadius" />; the host
    ///     rescales it by the game's human-scale factor per frame — in Starfield (metre units) the
    ///     unscaled 30,000 sits far outside the cell-scaled far plane, clipping the sun and moons
    ///     entirely. Callers computing billboard half-sizes must use THIS value so quad-to-path
    ///     ratios stay consistent with where the renderer actually places the quads.
    /// </summary>
    public float Radius { get; set; } = ClassicRadius;

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
    private readonly ShaderPipelineResources _pipelineResources;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly ID3D12PipelineState _psoAdditive;
    private readonly ID3D12PipelineState _psoAlpha;
    private bool _disposed;

    /// <summary>Creates the two celestial billboard pipelines on the world rendering thread.</summary>
    /// <param name="gpu">Borrowed device retained through renderer disposal.</param>
    /// <param name="recorder">Borrowed command recorder whose submitted work the caller retires before disposal.</param>
    /// <param name="ringBuffer">Borrowed allocator for per-draw constants.</param>
    /// <param name="rootSignature">World root retained by the renderer's shared pipeline family.</param>
    /// <param name="cbvSrvUavHeap">Borrowed world descriptor heap.</param>
    /// <remarks>Construction, native rendering and disposal use the same managed thread.</remarks>
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

        _pipelineResources = rootSignature.CreatePipelineResources(2);
        try
        {
            var (additive, alpha) = SkyPipelineFactory12.CreateBillboardPipelines(gpu, _pipelineResources);
            _psoAdditive = additive;
            _psoAlpha = alpha;
        }
        catch
        {
            _pipelineResources.Dispose();
            throw;
        }
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
        Vector3 moon2Dir, float moon2Fade, Vector3 moon2Color, uint moon2Tex, float moon2HalfSize,
        SkyBillboardPass12 pass = SkyBillboardPass12.All)
    {
        if (_disposed) return;

        // Preserve the established unified route for games without a recovered stage oracle: glare
        // first, then additive disc. FO3/FNV never enter this branch; their host submits SunBase and
        // SunGlareAndMoons around the cloud pass below.
        if (pass == SkyBillboardPass12.All &&
            MathF.Max(sunFade, sunGlareFade) > 0.001f && sunDir.Z > -0.05f)
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

        // Fallout 3/New Vegas submit the opaque-looking base texture through the ordinary
        // source-alpha pass BEFORE clouds. It is not additive: making the base additive turns its
        // already-bright carrier texture into the oversized white bloom seen in the parity gate.
        if (pass == SkyBillboardPass12.SunBase &&
            sunFade > 0.001f && sunDir.Z > -0.05f && sunDiscTex != NoTexture)
        {
            Draw(_psoAlpha, viewProj, camPos, camRight, camUp, sunDir,
                sunDiscHalfSize, sunFade, sunDiscTex, sunColor, 1f, glowExp: 1f);
        }

        // The separate glare pass is source-alpha additive and follows clouds in the FO3/FNV sky order.
        if (pass == SkyBillboardPass12.SunGlareAndMoons &&
            sunGlareFade > 0.001f && sunDir.Z > -0.05f && sunGlareTex != NoTexture)
        {
            Draw(_psoAdditive, viewProj, camPos, camRight, camUp, sunDir,
                sunGlareHalfSize, sunGlareFade, sunGlareTex, sunGlareColor, 1f, glowExp: 1f);
        }

        // Moon(s) (night): alpha-blended lit discs. The primary moon plus an optional second moon
        // (Secunda for the two-moon TES games), each sized by the caller's per-game SkyMoonProfile.
        // MoonGlowExponent suppresses the texture's over-opaque glow halo (keeps the disc).
        var renderMoons = pass is SkyBillboardPass12.All or SkyBillboardPass12.SunGlareAndMoons;
        if (renderMoons && moonFade > 0.001f && moonDir.Z > -0.05f && moonTex != NoTexture)
        {
            Draw(_psoAlpha, viewProj, camPos, camRight, camUp, moonDir,
                moonHalfSize, moonFade, moonTex, moonColor, 1f, glowExp: MoonGlowExponent);
        }

        if (renderMoons && moon2Fade > 0.001f && moon2Dir.Z > -0.05f && moon2Tex != NoTexture)
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
        GpuRootSignature12.SetGraphicsBindlessTables(cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);
        cmd.DrawInstanced(4, 1, 0, 0); // triangle-strip quad
    }

    /// <summary>Stops drawing and releases the caller-retired billboard family, retaining failed releases for retry.</summary>
    /// <remarks>The caller must prove GPU completion or device removal before disposal.</remarks>
    /// <exception cref="InvalidOperationException">Disposal is attempted outside the creating thread.</exception>
    /// <exception cref="AggregateException">A native release failed; retain the renderer and retry disposal.</exception>
    public void Dispose()
    {
        VerifyAccess();
        _disposed = true;
        _pipelineResources.Dispose();
    }

    /// <summary>Rejects disposal on another thread before changing the renderer's drawing state.</summary>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Sky billboard rendering belongs to its creating thread.");
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
}
#endif
