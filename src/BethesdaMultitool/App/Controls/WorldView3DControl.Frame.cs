using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Microsoft.UI.Xaml.Media;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    // SpeedTree leaf-wind animation clock + strength. Engine model (Sky::UpdateWind): S = the active
    // weather's wind-speed byte / 255, forced 0 in interiors — so by default wind FOLLOWS the same
    // weather selection that drives clouds/lighting. _windStrength is a MANUAL OVERRIDE: null = auto
    // (weather-driven); set via the lighting panel's "Override wind" row or the FALLOUT_VIEWER_SPT_WIND
    // env var.
    private float _windClockSeconds;
    private float? _windStrength;
    private bool _windEnvChecked;
    private static readonly Vector2 WindDirection = Vector2.Normalize(new Vector2(0.82f, 0.57f));

    // Translation-tolerant cull cache kill-switch (FALLOUT_VIEWER_TOLERANT_CULL=0 → exact compare).
    private static readonly bool TolerantCullEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.TolerantCull) != "0";

    // Sun shadow map (directional shadows: reference batches cast onto terrain + references).
    // Lazily created on the first shadow-enabled frame; disposed with the D3D12 backend
    // (DisposeD3D12Backend). Kill-switch FALLOUT_VIEWER_SHADOWS=0; UI toggle in the lighting flyout.
    private Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12? _shadowMap;
    private static readonly bool ShadowsEnvEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.Shadows) != "0";

    // The directional-light direction resolved by the LAST BindAtmosphereConstants call — the
    // frame-end shadow pass fits the light frustum around it (same frame, same resolve).
    private Vector3 _lastResolvedSunDirection = new(0.5f, 0.5f, 1f);
    private Vector4 _lastBoundShadowParams; // diagnostics: what the last b3 upload carried

    // Shadow coverage half-extent from the lighting flyout's "Shadow distance" slider, in CELLS
    // (null = Unlimited → the full render distance). Smaller = sharper shadows (texel density is
    // 2·radius/resolution). FALLOUT_VIEWER_SHADOW_RADIUS (world units) overrides it headless.
    private float? _shadowDistanceCells = 4f;
    private static readonly float? ShadowRadiusEnvOverride = ParseShadowRadiusEnvOverride();

    // Monotonic tick folded into the shadow key while the captured casters include WIND-ANIMATED
    // leaf cards: the map then re-renders every frame so canopy shadows sway with the trees
    // instead of freezing into a slideshow. _shadowAnimatedActive gates the transition log.
    private int _shadowAnimationTick;
    private bool _shadowAnimatedActive;

    private static float? ParseShadowRadiusEnvOverride()
    {
        var raw = EnvironmentVariables.Get(EnvironmentVariables.Viewer.ShadowRadius);
        return float.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0f
            ? v
            : null;
    }

    // Coverage half-extent for the sun shadow map: the slider (or its headless env override),
    // clamped to the render distance, plus the key's center-snap margin so geometry stays covered
    // while the camera walks between key re-snaps.
    private float ResolveShadowRadius()
    {
        var sliderWorld = _shadowDistanceCells is float cells ? cells * _cellSize : float.MaxValue;
        return MathF.Min(_renderDistance, ShadowRadiusEnvOverride ?? sliderWorld) + SunShadowMath.CenterSnap;
    }

    // Render-loop failure tolerance: skip + retry on transient frame failures, detach only when
    // they persist (see the OnRendering catch block).
    private const int MaxConsecutiveRenderFailures = 3;
    private int _consecutiveRenderFailures;

    // Grid pitch for the snapped camera-relative render origin (see sceneRenderOrigin). One FNV
    // cell; fixed across games — it's a precision/stability trade, not a world-structure quantity.
    private const float RenderOriginGridSize = 4096f;

    private static Vector3 SnapToRenderOriginGrid(Vector3 position) => new(
        RenderOriginGridSize * MathF.Floor(position.X / RenderOriginGridSize),
        RenderOriginGridSize * MathF.Floor(position.Y / RenderOriginGridSize),
        RenderOriginGridSize * MathF.Floor(position.Z / RenderOriginGridSize));

    // Env override for the wind strength; null (the default) = follow the active weather's wind byte.
    private static float? ParseWindStrength()
    {
        var raw = EnvironmentVariables.Get(EnvironmentVariables.Viewer.SpeedTreeWind);
        return !string.IsNullOrWhiteSpace(raw) &&
               float.TryParse(raw, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0f, 5f)
            : null;
    }

    private void AttachRenderLoop()
    {
        if (_renderLoopAttached) return;
        CompositionTarget.Rendering += OnRendering;
        _renderLoopAttached = true;
    }

    private void DetachRenderLoop()
    {
        if (!_renderLoopAttached) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderLoopAttached = false;
    }

    private void OnRendering(object? sender, object e)
    {
        if (Visibility == Microsoft.UI.Xaml.Visibility.Collapsed) return;
        if (_surface12 is null || _gpu12 is null || _commandRecorder12 is null) return;

        // Skip the whole frame until a scene is loaded. The render loop attaches at device init (see
        // Lifecycle.AttachRenderLoop), BEFORE LoadData runs — so without this gate the control renders an
        // empty scene at vsync for the entire duration of a worldspace load happening on the background
        // thread, contending for the GPU and triggering GC pauses that stall both threads. That stretches
        // the load many-fold: a 5.6M-record FO76 parse measured ~8x slower with the loop running than the
        // same parse headless (world-data 2.5 min vs 17.6 s). Rendering resumes the instant LoadData sets _data.
        if (_data is null) return;

        var now = DateTime.UtcNow;
        var deltaSeconds = (float)(now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;
        // Clamp pathological deltas (long pause, debugger break) to keep camera step bounded.
        if (deltaSeconds > 0.1f) deltaSeconds = 0.1f;
        _windClockSeconds += deltaSeconds; // drives the SpeedTree leaf-wind sway phase

        var controllerStarted = StartProfileTimestamp();
        _controller.Update(deltaSeconds);
        _lastControllerUpdateMilliseconds = ElapsedMilliseconds(controllerStarted);

        try
        {
            RenderFrameD3D12();
            _consecutiveRenderFailures = 0;
        }
        catch (Exception ex)
        {
            // Tolerate TRANSIENT failures (e.g. a one-off resource spike) by skipping the frame and
            // retrying: a single exception used to detach the loop permanently, freezing the viewer
            // on what was often a recoverable condition (a whole-map frame exhausting the upload
            // ring). Persistent failures (device removed, real bugs) still detach after a few
            // frames instead of spamming the log at vsync.
            _consecutiveRenderFailures++;
            Log.Warn("WorldView3DControl: render frame failed ({0}/{1}):\n{2}",
                _consecutiveRenderFailures, MaxConsecutiveRenderFailures, ex);
            // Drain any pending D3D12 validation messages while we still can — if this ends in a
            // detach the next-frame pump won't get a chance. No-op unless FALLOUT_VIEWER_D3D12_DEBUG=1.
            try { _gpu12?.PumpDebugMessages(); }
            catch (Exception pumpEx) { Log.Warn("PumpDebugMessages on render-frame failure threw: {0}", pumpEx.Message); }
            // If the GPU device was removed (TDR / page fault — the usual "crash with no cause"),
            // attribute it. No-op when the device is fine. Needs FALLOUT_VIEWER_DRED=1 for breadcrumbs.
            try { _gpu12?.LogDeviceRemovedDiagnostics("render-frame"); }
            catch (Exception dredEx) { Log.Warn("LogDeviceRemovedDiagnostics on render-frame failure threw: {0}", dredEx.Message); }
            if (_consecutiveRenderFailures >= MaxConsecutiveRenderFailures)
            {
                Log.Warn("WorldView3DControl: {0} consecutive render failures — detaching loop.",
                    _consecutiveRenderFailures);
                DetachRenderLoop();
            }
        }
    }

    /// <summary>
    ///     D3D12 live-render path. The shared backend binds the swap chain, root signature,
    ///     descriptor heaps, and per-frame allocators once, then each renderer records its
    ///     own terrain/reference/wireframe draws into the same command list.
    /// </summary>
    // Builds the shared atmosphere from the current game hour and uploads it to the b3 root CBV,
    // bound for the whole scene. A once-per-frame upload off the per-frame ring (always the
    // first allocation after ResetFrame, so it can't overflow). Lighting, sky, and fog are each
    // on by default and gated by their own _show* toggles.
    // enableFog is forced off for the orthographic top-down overlay capture: distance-from-camera fog
    // is meaningless there (it would tint the 2D map by the overhead camera's height). The live
    // perspective path leaves it true so the weather fog shows.
    // enableLighting is likewise forced off for the top-down overlay: the 2D map has no lighting/
    // time-of-day controls, so directional sun shading would bake an uncontrollable look into the
    // composite. Off ⇒ the shader's flat legacy shade. The live perspective path leaves it true so
    // the lighting toggle (the 8 key) still drives the 3D view.
    private void BindAtmosphereConstants(
        Vortice.Direct3D12.ID3D12GraphicsCommandList cmd, int frameIndex, bool enableFog = true,
        bool enableLighting = true, bool cameraRelative = false, Vector3? shadingCameraPosOverride = null,
        float? gameHourOverride = null, Vector3? cameraOriginOverride = null, bool enableShadows = true)
    {
        // The top-down overlay drives lighting from the 2D map's own time-of-day, passed via
        // gameHourOverride; the live perspective path uses the 3D control's _gameHour. enableLighting is
        // the per-call gate (still AND-ed with the scene's _showLighting toggle for the live path; the
        // overlay passes its own lighting-enabled flag as enableLighting with _showLighting left true).
        var gameHour = gameHourOverride ?? _gameHour;
        var lightingOn = enableLighting && _showLighting;
        // Night directional: pass the primary moon's sky direction so Resolve can hand the scene
        // light from the sun to the MOON once the sun is fully down (modern weathers only — the
        // handover is gated on the weather carrying DALC). Same orbit the moon billboard draws on,
        // so night N·L shading matches the visible moon position.
        System.Numerics.Vector3? moonlight = MoonProfile.HasMoon
            ? BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.MoonSky.ComputeMoonDirection(
                MoonProfile.PrimaryOrbit, gameHour, _gameDay)
            : null;
        var resolved = AtmosphereState.Resolve(
            gameHour, _selectedWeather, _currentClimateTiming, lightingEnabled: lightingOn,
            moonlightDirection: moonlight);
        // Stash the frame's directional-light direction for the frame-end shadow pass (the map is
        // fitted around this direction; see RecordSunShadowPass).
        _lastResolvedSunDirection = resolved.SunWorldDirection;
        // Camera-relative: the scene VS subtract CameraOrigin from each world vertex, so the shader's
        // "camera position" (used by fog distance + specular view dir) must sit in that SAME shifted
        // space: camera − origin. The live frame passes the grid-SNAPPED scene origin via
        // cameraOriginOverride (so the shading camera is the in-cell offset, not zero); with no
        // override the origin is the exact camera position and the shading camera collapses to zero.
        // Absolute mode (top-down capture / flag off) keeps the camera position in CameraPosFogPower
        // and a zero origin.
        // shadingCameraPosOverride: the ortho projection modes pass their (far-off) eye position so the
        // specular view vector reads as parallel; ortho is always absolute (camera-relative off there).
        var cameraOrigin = cameraOriginOverride ?? (cameraRelative ? _camera.Position : Vector3.Zero);
        var shadingCameraPos = shadingCameraPosOverride
            ?? (cameraRelative ? _camera.Position - cameraOrigin : _camera.Position);
        // Per-game ambient fill scale (uAmbientColor.w): FNV's 0.3 is too dark for the ambient-heavier
        // TES4-era engines, so Oblivion etc. raise it (see GameProfile.AmbientLightScale).
        var ambientScale = BethesdaMultitool.Core.Games.GameProfiles
            .For(_data?.Game ?? BethesdaMultitool.Core.Games.BethesdaGame.Unknown).AmbientLightScale;
        // Sun-shadow sampling constants: the rendered map's light matrix (with this frame's render
        // origin folded in) + packed params. Disabled (zero) until the map has content, when the
        // caller opts out (ortho export / top-down), or when the toggle / env kill-switch is off —
        // the shader's ShadowFactor then returns 1.0 and the scene is pixel-identical to before.
        var shadowMatrix = Matrix4x4.Identity;
        var shadowParams = Vector4.Zero;
        if (enableShadows && lightingOn && _showShadows && ShadowsEnvEnabled &&
            _shadowMap is { HasContent: true } shadowMap)
        {
            (shadowMatrix, shadowParams) = shadowMap.GetSampleConstants(cameraOrigin);
        }
        _lastBoundShadowParams = shadowParams;
        var constants = AtmosphereConstants.From(
            resolved, gameHour, shadingCameraPos, lightingEnabled: lightingOn ? 1f : 0f,
            skyEnabled: _showSky ? 1f : 0f, fogEnabled: enableFog && _showFog ? 1f : 0f, time: 0f,
            cameraOrigin: cameraOrigin, ambientScale: ambientScale,
            shadowMatrix: shadowMatrix, shadowParams: shadowParams);
        var alloc = _ringBuffer12!.Allocate(frameIndex, AtmosphereConstants.ByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(AtmosphereConstants*)alloc.CpuPtr = constants; }
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, alloc.GpuAddress);
    }

    // CPU mirror of the b3 `cbuffer Atmosphere` the lighting/sky/water shaders declare. 9 float4
    // = 144 bytes (CB-aligned by the ring buffer). When a flag is 0 the shader falls back to its
    // pre-atmosphere behavior, so a disabled toggle keeps that aspect of the scene looking flat.
    // (Shaders that don't apply fog — e.g. skybox — declare only the leading float4s they use, which
    // is layout-safe since later fields are appended at the end.)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct AtmosphereConstants
    {
        public Vector4 SunDirIntensity;    // xyz = sun world dir (toward sun), w = intensity
        public Vector4 SunColorLighting;   // rgb = sun color, w = lightingEnabled (0/1)
        public Vector4 AmbientColor;       // rgb = ambient, w = spare
        public Vector4 SkyTopSkyEnabled;   // rgb = sky-top color, w = skyEnabled (0/1)
        public Vector4 SkyHorizon;         // rgb = sky-horizon color, w = spare
        public Vector4 FogColorFogEnabled; // rgb = fog color, w = fogEnabled (0/1)
        public Vector4 Params;             // x = gameHour, y = fogNear, z = fogFar, w = time
        public Vector4 CameraPosFogPower;  // xyz = camera world pos (0 in camera-relative mode), w = fog power
        // Camera-relative render origin the scene VS subtract from each world vertex before
        // projection (0 when camera-relative is off). The PS shaders that read this CB
        // declare only the prefix they use, so fields appended after their prefix are layout-safe.
        public Vector4 CameraOrigin;       // xyz = render origin (= camera world pos), w unused
        // Sun shadow map (appended after CameraOrigin — same append-safe contract). The matrix maps
        // origin-relative world positions (the PS's vWorldPos space) into shadow clip; ShadowParams
        // packs (enabled, texel UV size, normalized depth bias, bindless SRV slot). Enabled == 0
        // short-circuits the shader's ShadowFactor to 1.0, keeping the scene pixel-identical.
        public Matrix4x4 ShadowMatrix;
        public Vector4 ShadowParams;

        public const uint ByteSize = 14 * 16;

        public static AtmosphereConstants From(
            AtmosphereState.Resolved a,
            float gameHour,
            Vector3 cameraPos,
            float lightingEnabled,
            float skyEnabled,
            float fogEnabled,
            float time,
            Vector3 cameraOrigin,
            float ambientScale = 0.3f,
            Matrix4x4 shadowMatrix = default,
            Vector4 shadowParams = default) => new()
            {
                SunDirIntensity = new Vector4(a.SunWorldDirection, a.SunIntensity),
                SunColorLighting = new Vector4(a.SunColor, lightingEnabled),
                // w carries the per-game ambient ("fill") scale read by the object/terrain shaders.
                AmbientColor = new Vector4(a.AmbientColor, ambientScale),
                SkyTopSkyEnabled = new Vector4(a.SkyTopColor, skyEnabled),
                SkyHorizon = new Vector4(a.SkyHorizonColor, 0f),
                FogColorFogEnabled = new Vector4(a.FogColor, fogEnabled),
                Params = new Vector4(gameHour, a.FogNear, a.FogFar, time),
                CameraPosFogPower = new Vector4(cameraPos, a.FogPower),
                CameraOrigin = new Vector4(cameraOrigin, 0f),
                ShadowMatrix = shadowMatrix,
                ShadowParams = shadowParams,
            };
    }

    /// <summary>
    ///     Re-renders the sun shadow map when its cache key changed: fits the light's ortho frustum
    ///     around the camera (SunShadowMath), binds the shadow DSV, replays the frame's captured
    ///     reference draws depth-only, and draws the resident terrain cells (terrain casts too —
    ///     hillsides shade valleys). Must be recorded AFTER the reference pass (the replay uses
    ///     this frame's ring-buffer CB addresses) and after every screen-target draw (it rebinds the
    ///     OM targets + viewport without restoring them). The map rendered here is sampled from the
    ///     NEXT frame on via the atmosphere CB. While the captured casters include wind-animated
    ///     leaf cards, the key ticks every frame so canopy shadows sway instead of freezing.
    /// </summary>
    private void RecordSunShadowPass(
        Vortice.Direct3D12.ID3D12GraphicsCommandList cmd, Vector3 renderOrigin, Vector3 sceneCenter)
    {
        var terrainCasts = _showTerrain && _terrain is not null;
        if (_shadowMap is null || _references is null || (!_references.HasShadowDraws && !terrainCasts))
        {
            return; // nothing to cast (or a degenerate frame) — keep the previous map + key
        }

        var animated = _references.ShadowDrawsIncludeAnimatedLeaves;
        var tick = animated ? unchecked(++_shadowAnimationTick) : 0;
        var radius = ResolveShadowRadius();
        var contentVersion = HashCode.Combine(
            _references.BatchContentVersion, _terrain?.ContentVersion ?? 0, tick);
        var key = SunShadowMath.BuildKey(_lastResolvedSunDirection, sceneCenter, radius, contentVersion);
        if (!_shadowMap.NeedsRender(key))
        {
            return;
        }

        var frustum = SunShadowMath.BuildLightFrustum(
            _lastResolvedSunDirection, sceneCenter, renderOrigin, radius, _shadowMap.Resolution);
        // One line per DISCRETE map render keeps logs quiet; the wind-driven continuous mode would
        // log at frame rate, so it logs once per activation instead.
        if (!animated)
        {
            _shadowAnimatedActive = false;
            Log.Info("[Shadow] rendering map: draws={0} radius={1:0} dir=({2:0.00},{3:0.00},{4:0.00}) origin=({5:0},{6:0},{7:0})",
                _references.ShadowDrawCount, radius,
                _lastResolvedSunDirection.X, _lastResolvedSunDirection.Y, _lastResolvedSunDirection.Z,
                renderOrigin.X, renderOrigin.Y, renderOrigin.Z);
        }
        else if (!_shadowAnimatedActive)
        {
            _shadowAnimatedActive = true;
            Log.Info("[Shadow] continuous re-render engaged (wind-animated foliage in view): draws={0} radius={1:0}",
                _references.ShadowDrawCount, radius);
        }

        _shadowMap.BeginRender(cmd);
        var drewReferences = _references.RenderShadowDepth(frustum);
        var drewTerrain = false;
        if (terrainCasts)
        {
            // Same cylinder footprint the main pass streamed, clamped to the shadow coverage —
            // draws only RESIDENT cells (the designed back-to-back RenderInternal double-call).
            var terrainRadius = MathF.Min(radius, _renderDistance);
            drewTerrain = _terrain!.RenderShadowDepth(
                frustum.ViewProj, new VisibilityCylinder(sceneCenter, terrainRadius)) > 0;
        }

        if (drewReferences || drewTerrain)
        {
            _shadowMap.EndRender(cmd, key, frustum, renderOrigin);
        }
        else
        {
            // Nothing drew (ring exhaustion / nothing resident): the map was cleared but holds no
            // scene — transition it back WITHOUT publishing the key, so sampling stays on the old
            // constants (a cleared map reads fully lit either way) and the next frame retries.
            _shadowMap.EndRenderEmpty(cmd);
        }
    }

    private void RenderFrameD3D12()
    {
        var frameNumber = ++_profileFrameIndex;
        var frameStarted = StartProfileTimestamp();
        var recorder = _commandRecorder12!;
        var surface = _surface12!;

        var segmentStarted = StartProfileTimestamp();
        recorder.BeginFrame();
        EmitCompletedGpuFrames();
        _gpuTimestampProfiler12?.BeginFrame(recorder.FrameIndex);
        var cmd = recorder.CommandList;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.FrameStart);
        // Deletion queue advances frame counter — releases resources whose hold has elapsed.
        // Must run AFTER BeginFrame's fence wait so the prior GPU work is known complete.
        _deletionQueue12!.Tick();
        _ringBuffer12!.ResetFrame();
        _cbvSrvUavHeap12!.BeginFrame(recorder.FrameIndex);
        // Drain D3D12 debug layer messages from the prior frame (no-op unless
        // FALLOUT_VIEWER_D3D12_DEBUG=1 enabled the layer).
        _gpu12!.PumpDebugMessages();
        var beginFrameMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        var (backBuffer, backRtv) = surface.AcquireBackBufferRtv();
        // Scene renders into the MSAA color target (resolved into the back buffer before Present)
        // when scene-wide MSAA is active; otherwise straight into the back buffer.
        var sceneRtv = surface.IsMsaa ? surface.MsaaColorRtv : backRtv;
        var sceneDsv = surface.DepthStencilView;
        var acquireMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        // The MSAA color target stays in RENDER_TARGET across frames; only the non-MSAA back buffer
        // needs PRESENT → RENDER_TARGET here (the MSAA back buffer is handled by ResolveTo at frame end).
        if (!surface.IsMsaa)
        {
            cmd.ResourceBarrierTransition(backBuffer,
                Vortice.Direct3D12.ResourceStates.Present,
                Vortice.Direct3D12.ResourceStates.RenderTarget);
        }

        cmd.ClearRenderTargetView(sceneRtv,
            new Vortice.Mathematics.Color4(0x1B / 255f, 0x24 / 255f, 0x36 / 255f, 1f));
        // Reversed-Z: clear depth to 0 (the far value). Pairs with CameraState.ReverseZ + every
        // scene PSO's GreaterEqual depth test.
        cmd.ClearDepthStencilView(sceneDsv,
            Vortice.Direct3D12.ClearFlags.Depth, 0f, 0);
        cmd.OMSetRenderTargets(sceneRtv, sceneDsv);
        cmd.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, surface.Width, surface.Height, 0f, 1f));
        cmd.RSSetScissorRect((int)surface.Width, (int)surface.Height);

        // Bind the shared shader-visible descriptor heap + root signature once per frame.
        // SetDescriptorHeaps MUST precede SetGraphicsRootSignature: the root signature carries
        // CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED (bindless), and D3D12 requires the CBV/SRV/UAV heap
        // to be bound before a directly-indexed root signature is set (else the debug layer
        // errors every frame and bindless lookups read from an unbound heap).
        // Each renderer sets only its own PSO + per-slot bindings inside Render().
        cmd.SetDescriptorHeaps(1, new[] { _cbvSrvUavHeap12.Heap });
        cmd.SetGraphicsRootSignature(_rootSignature12!.RootSignature);
        var clearSetupMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        // Camera + cylinder match the D3D11 path so visible-cell sets are identical.
        var aspect = surface.Width / (float)surface.Height;
        var projectionActive = ProjectionActive;
        Matrix4x4 viewProjAbsolute, viewProjScene;
        // Translation-free view-projection for the sky dome (camera at the origin). The dome is centered
        // on the camera and rendered with domeCenter = 0, so the large camera world coordinates never
        // enter the vertex math — killing the float-cancellation jitter the absolute viewProj caused as
        // the camera moved far from the worldspace origin. Sky is direction-based (effectively at
        // infinity), so dropping the translation does not change its alignment with the scene.
        Matrix4x4 viewProjSky;
        VisibilityCylinder cylinder;
        Vector3 orthoEye = default;
        bool cameraRelative;
        // Camera-relative render origin for the WHOLE scene (terrain/references/water + the b3 CB).
        // Snapped to a coarse grid rather than the exact camera position: within a grid cell the
        // origin — and with it every CPU-folded reference world matrix — is bit-stable, which lets
        // the batch/instance build be reused across frames instead of re-folding ~60k matrices per
        // frame. Precision holds: scene coordinates stay within ~2 grid cells of the origin, where
        // fp32 ULP ≈ 2.4e-4 world units (the z-fight case this path fixed sat at ~52k units).
        Vector3 sceneRenderOrigin = default;
        if (projectionActive)
        {
            // Orthographic / isometric / trimetric: the shared OrthoViewProjBuilder owns the matrices.
            // Ortho renders in ABSOLUTE world space (no camera-relative origin subtraction) — its focus
            // points stay within worldspace coords and there's no perspective divide to amplify float
            // cancellation. Scene == absolute, so terrain/reference/water draw with the same matrix.
            viewProjAbsolute = BuildProjectionViewProj(aspect, out cylinder, out orthoEye);
            viewProjScene = viewProjAbsolute;
            viewProjSky = viewProjAbsolute; // sky is suppressed in ortho modes; unused.
            cameraRelative = false;
        }
        else
        {
            // Decouple the far plane from the streaming radius: the cylinder (= _renderDistance) controls
            // what LOADS; the far plane only needs to be large enough never to clip the loaded set from
            // any altitude/tilt (this was the horizon-cutoff bug — far plane == render distance). The
            // farthest loaded point is within _renderDistance horizontally and |Z| vertically of the
            // camera, so cover that plus a couple cells of slack. Reversed-Z keeps precision at range.
            _camera.FarPlane = _renderDistance * 2f + MathF.Abs(_camera.Position.Z)
                               + 2f * _cellSize;
            var proj = _camera.GetProjectionMatrix(aspect);
            // Camera-relative rendering (default on; FALLOUT_VIEWER_CAMERA_RELATIVE=0 disables). The
            // ABSOLUTE viewProj drives sky + debug overlays (their geometry stays in world space and they
            // read no CameraOrigin); the SCENE viewProj is translation-free, and terrain/reference/water VS
            // subtract CameraOrigin from each world vertex before projection. Both map to the SAME clip
            // position (lookAt's translation just moves into the subtraction), so the layers stay aligned —
            // only the scene's float precision improves, killing the worldspace-edge wobble. When disabled
            // the scene viewProj == the absolute one and CameraOrigin == 0, i.e. bit-identical to before.
            cameraRelative = !string.Equals(
                EnvironmentVariables.Get(EnvironmentVariables.Viewer.CameraRelative), "0", StringComparison.Ordinal);
            viewProjAbsolute = _camera.GetViewMatrix() * proj;
            sceneRenderOrigin = cameraRelative ? SnapToRenderOriginGrid(_camera.Position) : Vector3.Zero;
            viewProjScene = cameraRelative
                ? _camera.GetViewMatrixRelativeTo(sceneRenderOrigin) * proj
                : viewProjAbsolute;
            // Sky ALWAYS uses the translation-free view (independent of the camera-relative scene toggle):
            // the dome is camera-centered, so its only correct frame is one with the camera at the origin.
            viewProjSky = _camera.GetViewMatrixCameraRelative() * proj;
            cylinder = new VisibilityCylinder(_camera.Position, _renderDistance);
        }
        var cameraMs = ElapsedMilliseconds(segmentStarted);

        // Per-card SpeedTree leaf billboards: hand the reference renderer the camera world right/up (from
        // the inverse view matrix, same source as the sky billboards) so the leaf-billboard VS re-faces
        // each leaf card to the camera this frame. In an ortho mode the perspective camera isn't aimed
        // where the view looks, so derive the basis from the ortho camera instead (top-down → flat).
        if (projectionActive)
        {
            var (leafRight, leafUp) = ProjectionCameraBasis();
            _references?.SetLeafBillboardBasis(leafRight, leafUp);
        }
        else if (Matrix4x4.Invert(_camera.GetViewMatrix(), out var invViewForLeaves))
        {
            _references?.SetLeafBillboardBasis(
                Vector3.Normalize(new Vector3(invViewForLeaves.M11, invViewForLeaves.M12, invViewForLeaves.M13)),
                Vector3.Normalize(new Vector3(invViewForLeaves.M21, invViewForLeaves.M22, invViewForLeaves.M23)));
        }

        // SpeedTree leaf wind (model recovered from STLEAF/STB shaders + BSTreeManager::UpdateWindMatrices).
        // Engine-faithful default: S = the ACTIVE WEATHER's wind-speed byte / 255 (the same record that
        // drives clouds/lighting; Sky::UpdateWind), forced 0 in interiors and when no weather resolves.
        // The lighting panel's "Override wind" row / FALLOUT_VIEWER_SPT_WIND set a manual override.
        if (!_windEnvChecked)
        {
            _windEnvChecked = true;
            var envWind = ParseWindStrength();
            if (envWind is not null)
            {
                _windStrength = Math.Clamp(envWind.Value, 0f, 1f);
                LightingPanel.SeedWindOverride(_windStrength.Value); // reflect in the Weather section UI
            }
        }
        var weatherWind = _selectedInterior is not null
            ? 0f
            : ((_selectedWeather ?? _climateDefaultWeather)?.Data?.WindSpeed ?? 0) / 255f;
        var effectiveWind = _windStrength ?? weatherWind;
        // In auto mode, keep the (disabled) wind slider showing the weather-driven value. We're on the
        // UI thread (CompositionTarget.Rendering); the panel never raises events for display updates.
        if (_windStrength is null)
        {
            LightingPanel.SetWindSpeedDisplay(weatherWind);
        }
        _references?.SetWindProfile(Core.Formats.SpeedTree.SpeedTreeWindProfile.For(
            _data?.Game ?? BethesdaMultitool.Core.Games.BethesdaGame.Unknown));
        _references?.SetWind(WindDirection, effectiveWind, _windClockSeconds);

        // Sun shadows: arm the reference renderer's shadow-draw capture BEFORE its render pass so
        // this frame's instanced draws are recorded for the frame-end shadow replay. The CB bound
        // below samples the PREVIOUS render of the map (one frame of latency, hidden by the cache);
        // the map itself re-renders at the end of this frame only when its key changed.
        var shadowsActive = ShadowsEnvEnabled && _showShadows && _showLighting && !projectionActive &&
                            _selectedInterior is null && _references is not null && _showReferences;
        if (shadowsActive)
        {
            _shadowMap ??= new Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12(
                _gpu12!, _cbvSrvUavHeap12!);
            _references!.ArmShadowCapture();
        }

        // Resolve + upload the shared atmosphere CB (b3) once per frame, bound for the whole scene.
        // Terrain/reference/water read it for directional + ambient lighting; the sky/fog flags drive
        // those shader paths and are each on by default. Bound once — the renderers only set their
        // own PSO + slots, never the root signature, so this CBV survives every scene pass.
        // Ortho modes: force fog OFF (distance-from-a-1,000,000-unit-eye fog would max out everywhere)
        // and feed the ortho eye as the shading camera position so the specular view vector is parallel.
        BindAtmosphereConstants(
            cmd, recorder.FrameIndex, enableFog: !projectionActive, cameraRelative: cameraRelative,
            shadingCameraPosOverride: projectionActive ? orthoEye : null,
            cameraOriginOverride: cameraRelative ? sceneRenderOrigin : null);

        // Sky FIRST — gradient + clouds + stars into the cleared color target (depth OFF, so terrain
        // overwrites it via the normal depth pass; OFF ⇒ the flat dark-blue clear shows), then the
        // sun/moon billboards over it. Reads the b3 atmosphere CB bound just above. Sky renders
        // CAMERA-RELATIVE: the translation-free viewProjSky with the dome centered at the origin
        // (domeCenter 0), so far-from-origin camera coords don't jitter the dome. Suppressed in the ortho
        // projection modes: the dome is centered on the perspective camera, not where the ortho view looks.
        if (_showSky && !projectionActive)
        {
            RenderSky(viewProjSky, Vector3.Zero);
        }

        // Layer order matches D3D11: terrain → references → water → wireframe. Water is
        // alpha-blended depth-read, so it must come after terrain + references (which write
        // the depth that water samples). Wireframe last so it stays on top (depth-disabled).
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.TerrainStart);
        var visibleTerrain = _showTerrain ? _terrain?.Render(viewProjScene, cylinder) ?? 0 : 0;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.TerrainEnd);
        var terrainMs = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ReferencesStart);
        // Opaque references draw now (write depth) but blended/transparent submeshes are deferred
        // until AFTER the water pass via RenderBlendedDeferred(), so water never paints over them.
        // Pass the ABSOLUTE viewProj for the cull frustum: the GPU CB gets the camera-relative
        // viewProjScene (the VS subtracts CameraOrigin), but the CPU frustum culls world-space
        // reference coordinates, so its apex must sit at the camera, not the world origin. Without
        // this, camera-relative rendering culls everything except a narrow cone from the origin
        // (objects only visible facing one direction). When camera-relative is off the two matrices
        // are equal, so this is a no-op then.
        // renderOrigin MUST equal the CameraOrigin bound in the atmosphere CB above (the snapped
        // sceneRenderOrigin): the reference VS no longer subtracts uCameraOrigin, so the renderer
        // folds this origin into each world matrix on the CPU instead. That yields the same
        // camera-relative clip position as terrain/water (which still subtract uCameraOrigin
        // in-shader) but with far better float32 precision — killing coplanar Z-fighting on distant
        // architecture. Snapping keeps the folded matrices bit-stable within a grid cell (batch
        // reuse) at no precision cost (offsets stay within ~2 cells of the origin).
        var referenceRenderOrigin = sceneRenderOrigin;
        // Perspective path passes the camera pose so the cull cache is translation-TOLERANT: the
        // far plane above is recomputed from |camera.Z| every frame, so the old byte-exact viewProj
        // compare NEVER matched while walking and re-culled ~100k candidates per frame. Ortho /
        // projection modes keep the exact compare (their viewProj is genuinely stable when idle).
        // FALLOUT_VIEWER_TOLERANT_CULL=0 reverts to the exact compare (perf A/B + escape hatch).
        Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceRenderer12.CullCameraPose? cullPose =
            projectionActive || !TolerantCullEnabled
                ? null
                : new Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceRenderer12.CullCameraPose(
                    _camera.Forward, _camera.FovYRadians, aspect);
        var visibleReferences = _showReferences
            ? _references?.Render(
                  viewProjScene, cylinder, deferBlended: true, cullViewProj: viewProjAbsolute,
                  renderOrigin: referenceRenderOrigin, cullCameraPose: cullPose) ?? 0
            : 0;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ReferencesEnd);
        var referencesMs = ElapsedMilliseconds(segmentStarted);
        // Hand the water renderer the placed-NIF water planes the reference pass accumulated (cave/
        // pool water embedded in REFR meshes) so they draw with the real Fresnel/ripple/depth-fade
        // water shader instead of the flat slab. Cheap reference assignment; the list grows as those
        // references' meshes stream in.
        if (_water is not null && _references is not null)
        {
            _water.SetNifWaterPlanes(_references.NifWaterPlanes);
        }
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WaterStart);
        // Feed water the real scene depth (terrain + references already wrote it) so it computes the
        // WATER000-style water-column depth fade. The depth-sample PSO binds no DSV, so for this pass
        // only: unbind the depth target and transition the R32_TYPELESS depth DEPTH_WRITE →
        // PIXEL_SHADER_RESOURCE, then restore both for the depth-reading navmesh/wireframe overlays.
        // Without a depth SRV, water keeps the hardware-depth-test PSO + view-angle proxy (DSV stays).
        var depthRes = surface.DepthResource;
        // Ortho modes skip the depth-SRV soft-fade: it linearizes with the perspective camera near/far,
        // which don't describe the ortho depth range. Fall back to the hardware depth-test water PSO
        // (same path the top-down overlay uses) so water stays height-correct in ortho.
        var waterUsesDepth = !projectionActive && _showWater && _water is not null
                             && _depthSrv is not null && depthRes is not null;
        _water?.SetSceneDepth(
            waterUsesDepth ? _depthSrv!.Value.BindlessIndex : NoDepthSrv,
            _camera.NearPlane,
            _camera.FarPlane);
        if (waterUsesDepth)
        {
            cmd.OMSetRenderTargets(sceneRtv); // drop the DSV; keep the scene color RTV
            cmd.ResourceBarrierTransition(depthRes!,
                Vortice.Direct3D12.ResourceStates.DepthWrite,
                Vortice.Direct3D12.ResourceStates.PixelShaderResource);
        }
        // Water projects CAMERA-RELATIVE like terrain/references (same viewProjScene + render origin, VS
        // subtracts) — the absolute path lost float32 precision far from the origin and the water edge
        // shimmered/ended at view-angle-dependent Z as the camera rotated. Ripple UVs + the PS view vector
        // stay world-anchored: vWorldPos is still the absolute position (see water.vert.hlsl).
        var visibleWater = _showWater
            ? _water?.Render(viewProjScene, cylinder, referenceRenderOrigin) ?? 0
            : 0;
        if (waterUsesDepth)
        {
            cmd.ResourceBarrierTransition(depthRes!,
                Vortice.Direct3D12.ResourceStates.PixelShaderResource,
                Vortice.Direct3D12.ResourceStates.DepthWrite);
            cmd.OMSetRenderTargets(sceneRtv, sceneDsv);
        }
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WaterEnd);
        var waterMs = ElapsedMilliseconds(segmentStarted);
        // Blended (transparent) reference submeshes draw AFTER water so water never paints over
        // them. Water wrote no depth, so they depth-test against the opaque scene depth (terrain +
        // opaque refs); the DSV was restored above, so this stays depth-correct.
        if (_showReferences) _references?.RenderBlendedDeferred();
        // Navmesh overlay — translucent, drawn after water (depth-read) and before the
        // depth-disabled wireframe so the grid still stays on top.
        var visibleNavMesh = _showNavMesh ? _navMesh?.Render(viewProjAbsolute, cylinder) ?? 0 : 0;
        // Collision-cage debug overlay — depth-disabled green wireframe of each ref's walk-mode collision
        // mesh, for comparing Havok collision against the rendered meshes. Off by default.
        if (_showCollision) _collisionDebug?.Render(viewProjAbsolute, cylinder);
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WireframeStart);
        var visibleWireframe = _showWireframe ? _cellGrid?.Render(viewProjAbsolute, cylinder) ?? 0 : 0;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WireframeEnd);
        var wireframeMs = ElapsedMilliseconds(segmentStarted);

        // Selection outline, drawn last + depth-disabled so it stays visible on top of everything
        // (no-op when nothing is selected).
        _selectionHighlight?.Render(viewProjAbsolute);

        // Sun-shadow depth pass — recorded LAST (nothing after it draws to the screen targets, so
        // the shadow DSV/viewport need no restore). Replays this frame's captured reference batches
        // from the light's view; skipped entirely while the cache key (sun dir, snapped camera,
        // content version) is unchanged, so a settled scene records zero shadow work.
        if (shadowsActive)
        {
            RecordSunShadowPass(cmd, referenceRenderOrigin, _camera.Position);
        }

        segmentStarted = StartProfileTimestamp();
        var totalCells = _terrain?.CellCount ?? _cellGrid?.CellCount ?? 0;
        // In interior mode the single loaded cell has no LAND, so the terrain and (off-by-default)
        // wireframe passes both report 0 drawn — even though the camera sits inside that exact cell.
        // Count the loaded cell(s) as visible so the HUD reads "1 / 1" instead of a misleading "0 / 1".
        var visible = _selectedInterior is not null
            ? totalCells
            : Math.Max(visibleTerrain, visibleWireframe);
        UpdateHud(visible, totalCells, visibleWater, visibleReferences, visibleNavMesh);
        var hudMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        if (surface.IsMsaa)
        {
            // Resolve the multisampled scene color into the back buffer; leaves the back buffer in
            // PRESENT (and the MSAA color back in RENDER_TARGET for the next frame).
            surface.ResolveTo(cmd, backBuffer);
        }
        else
        {
            // RENDER_TARGET → PRESENT so DXGI can flip.
            cmd.ResourceBarrierTransition(backBuffer,
                Vortice.Direct3D12.ResourceStates.RenderTarget,
                Vortice.Direct3D12.ResourceStates.Present);
        }

        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.FrameEnd);
        _gpuTimestampProfiler12?.ResolveActiveFrame(cmd);
        recorder.EndFrame();
        _gpuTimestampProfiler12?.MarkActiveFrameSubmitted(frameNumber, recorder.LastSubmittedFenceValue);
        var endFrameMs = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartProfileTimestamp();
        surface.Present();
        var presentMs = ElapsedMilliseconds(segmentStarted);

        var totalFrameMs = ElapsedMilliseconds(frameStarted);
        var gcGen0Collections = GC.CollectionCount(0);
        var gcGen1Collections = GC.CollectionCount(1);
        var gcGen2Collections = GC.CollectionCount(2);
        var gcGen0Delta = gcGen0Collections - _lastGcGen0Collections;
        var gcGen1Delta = gcGen1Collections - _lastGcGen1Collections;
        var gcGen2Delta = gcGen2Collections - _lastGcGen2Collections;
        _lastGcGen0Collections = gcGen0Collections;
        _lastGcGen1Collections = gcGen1Collections;
        _lastGcGen2Collections = gcGen2Collections;

        var terrainStats = _showTerrain ? _terrain?.LastStats.Snapshot() : null;
        var waterStats = _showWater ? _water?.LastStats.Snapshot() : null;
        var wireframeStats = _showWireframe ? _cellGrid?.LastStats.Snapshot() : null;
        var referenceStats = _showReferences ? _references?.LastStats.Snapshot() : null;
        var sample = new FrameProfileSample(
            FrameNumber: frameNumber,
            TotalMilliseconds: totalFrameMs,
            ControllerMilliseconds: _lastControllerUpdateMilliseconds,
            BeginFrameMilliseconds: beginFrameMs,
            FenceWaitMilliseconds: recorder.LastFrameFenceWaitMilliseconds,
            AcquireMilliseconds: acquireMs,
            ClearSetupMilliseconds: clearSetupMs,
            CameraMilliseconds: cameraMs,
            TerrainMilliseconds: terrainMs,
            ReferencesMilliseconds: referencesMs,
            WaterMilliseconds: waterMs,
            WireframeMilliseconds: wireframeMs,
            EndFrameMilliseconds: endFrameMs,
            PresentMilliseconds: presentMs,
            HudMilliseconds: hudMs,
            VisibleTerrain: visibleTerrain,
            VisibleReferences: visibleReferences,
            VisibleWater: visibleWater,
            VisibleWireframe: visibleWireframe,
            TotalCells: totalCells,
            RenderDistanceCells: _renderDistance / _cellSize,
            ViewportWidth: surface.Width,
            ViewportHeight: surface.Height,
            DescriptorCount: _cbvSrvUavHeap12.CurrentFramePeak,
            RingBytes: _ringBuffer12.CurrentFrameBytes,
            GcGen0Collections: gcGen0Delta,
            GcGen1Collections: gcGen1Delta,
            GcGen2Collections: gcGen2Delta,
            ManagedMemoryBytes: GC.GetTotalMemory(false));

        if (_profileLogging)
        {
            MaybeLogProfile(sample, terrainStats, waterStats, wireframeStats, referenceStats);
        }

        if (_stallThresholdMilliseconds > 0 && sample.TotalMilliseconds >= _stallThresholdMilliseconds)
        {
            EmitFrameStall(sample, terrainStats, waterStats, wireframeStats, referenceStats);
            if (_gpuTimestampProfiler12 is null && !_gpuTimestampsAutoEnabled)
            {
                _gpuTimestampsAutoEnabled = true;
                EnableGpuTimestamps("auto-stall");
            }
        }
    }
}
