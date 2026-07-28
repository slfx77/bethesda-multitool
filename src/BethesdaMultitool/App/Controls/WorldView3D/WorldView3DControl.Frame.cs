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

    // FIXED cascade radii ladder (near → far). Deliberately DECOUPLED from the render-distance
    // setting: quality bands and shadow reach are properties of the shadow system; the draw
    // distance only bounds where geometry — and therefore any shadow — exists at all. The far
    // cascade's 32-cell reach covers every practical draw distance at a constant ~64 units/texel;
    // FALLOUT_VIEWER_SHADOW_RADIUS (world units) can cap the LAST cascade for diagnostics.
    private static readonly float[] ShadowCascadeRadii = { 2048f, 8192f, 32768f, 131072f };

    // On wind-animated frames only the cascades up to this index re-render (the swaying canopy
    // lives in the near field; the far cascades' full-distance terrain gathers are the expensive
    // part and their coarse texels can't resolve leaf sway anyway).
    private const int ShadowAnimatedCascadeCount = 2;

    // Content-only re-renders (geometry streamed in; pose unchanged) are throttled to once per
    // this many frames: streaming bumps the content version almost every frame while moving, and
    // re-rendering 4 cascades + terrain at frame rate was the "shadows lag the renderer" report.
    // A streamed-in mesh's shadow appearing a quarter-second late is imperceptible.
    private const int ShadowContentRerenderFrames = 15;

    // Off-screen shadow casters: frustum-rejected refs within this ring of the camera still cast
    // (their shadows can land on-screen — without it, shadows pop when their caster leaves the
    // frustum). Matches the mid cascade: beyond it the far cascades' coarse texels make a missing
    // off-screen caster's shadow hard to notice, and the ring's price is streaming those meshes.
    private const float ShadowCasterRingRadius = 8192f;

    // The shadow pass runs after every screen-space draw. Protect its worst-case ring footprint at
    // frame start so a dense scene cannot clear a near cascade and then fail to allocate the three
    // constants needed to repopulate it (reference b0 + terrain b0/b2). Each allocation is CB-aligned.
    private const uint ShadowPassRingReservationBytes =
        ((uint)Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12.CascadeCount * 3u + 1u) *
        GpuRingBuffer12.CbAlignment; // three CBs per cascade + worst-case initial alignment pad

    // Water draws after terrain + references, which in a whole-map / streaming frame fill the ring and
    // self-truncate. Protect the water pass's worst-case CB footprint — one per-frame uniforms CB plus,
    // on FNV, a noise-prepass CB and a uniforms CB for every visible WATR material batch — so a dense
    // frame can never starve it and drop the surface for a frame (the flicker seen crossing into new
    // water cells). Stacked ON TOP of the shadow reservation at frame start and handed back to the pass
    // immediately before it draws. Sized for up to WaterPassMaxReservedBatches distinct visible WATR
    // materials; beyond that the pass reverts to the ordinary soft-skip, exactly like the reference pass.
    private const uint WaterPassMaxReservedBatches = 64;
    private const uint WaterPassRingReservationBytes =
        WaterPassMaxReservedBatches * 4u * GpuRingBuffer12.CbAlignment; // per batch: noise CB + uniforms CB + pad

    // Re-render policy state (see RecordSunShadowPass): the last rendered pose key + content
    // version, the content throttle counter, and the animated-mode transition latch for logging.
    private SunShadowMath.ShadowKey _shadowPoseKey;
    private int _shadowContentVersion;
    private int _shadowContentThrottle;
    private bool _shadowAnimatedActive;

    // The frustums the cascades were last PUBLISHED with (+ the render origin they were built
    // against and the terrain-cylinder center used). Animated-only refreshes MUST replay these
    // exact frustums (origin-folded to the current frame): the pose KEY snaps the anchor by
    // CenterSnap, so between key changes the raw camera anchor drifts up to ~512 units —
    // rebuilding frustums from it re-renders content the published sampling matrices no longer
    // describe, which strobed cell-sized false shadows during camera movement whenever swaying
    // foliage kept the per-frame refresh alive. The terrain cylinder replays the published
    // center for the same reason: a current-camera cylinder leaves the cleared trailing-edge
    // strip of the footprint unredrawn, popping terrain shadows there.
    private SunShadowMath.LightFrustum[]? _shadowPublishedFrustums;
    private Vector3 _shadowPublishedOrigin;
    private Vector3 _shadowPublishedCylinderCenter;

    // Per-render console logging is DIAGNOSTIC-ONLY (FALLOUT_VIEWER_SHADOW_DIAG=1): the log line
    // fired on every re-render, which while walking meant constantly — console I/O at frame rate
    // was a measurable part of the reported lag.
    private static readonly bool ShadowDiagLogging =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.ShadowDiag) == "1";

    private static readonly float? ShadowRadiusEnvOverride = ParseShadowRadiusEnvOverride();

    private static float? ParseShadowRadiusEnvOverride()
    {
        var raw = EnvironmentVariables.Get(EnvironmentVariables.Viewer.ShadowRadius);
        return float.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0f
            ? v
            : null;
    }

    // Render-loop failure tolerance: skip + retry on transient frame failures, detach only when
    // they persist (see the OnRendering catch block).
    private const int MaxConsecutiveRenderFailures = 3;
    private int _consecutiveRenderFailures;

    // Grid pitch for the snapped camera-relative render origin (see sceneRenderOrigin). One FNV
    // cell; fixed across games — it's a precision/stability trade, not a world-structure quantity.
    private const float RenderOriginGridSize = 4096f;

    // Far plane for the synthetic perspective sky view in the ortho projection modes: only needs
    // to clear the fixed 12k dome radius (+ sun/moon billboards). Scene clipping is unaffected.
    private const float OrthoSkyFarPlane = 32_000f;

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

        // Per-frame refresh is a cheap struct assignment; keeps the tonemap operator in lockstep with
        // interior/exterior + worldspace switches without invalidation bookkeeping. AdaptFactor is the
        // engine ADAPT blend weight for THIS frame (k = EyeAdaptSpeed^clamp(15·dt, 0, 1)) — the temporal
        // smoothing that keeps the sparse-grid exposure from flickering while the camera moves.
        var tonemap = ResolveTonemapSettings();
        if (tonemap.Mode == Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapMode.CreationModern)
        {
            // The modern temporal response has not been recovered. Replace the average immediately
            // instead of applying the unrelated FO3/FNV ADAPT equation.
            tonemap = tonemap with { AdaptFactor = 1f };
        }
        else if (tonemap.EyeAdaptSpeed > 0f)
        {
            var speed = Math.Clamp(tonemap.EyeAdaptSpeed, 0.01f, 0.999f);
            tonemap = tonemap with
            {
                AdaptFactor = MathF.Pow(speed, Math.Clamp(15f * Math.Max(deltaSeconds, 0f), 0f, 1f)),
            };
        }

        _surface12.TonemapSettings = tonemap;
        // Clamp pathological deltas (long pause, debugger break) to keep camera step bounded.
        if (deltaSeconds > 0.1f) deltaSeconds = 0.1f;
        _windClockSeconds += deltaSeconds; // drives the SpeedTree leaf-wind sway phase

        var controllerStarted = StartProfileTimestamp();
        _controller.Update(deltaSeconds);
        _lastControllerUpdateMilliseconds = ElapsedMilliseconds(controllerStarted);

        try
        {
            RenderFrameD3D12(tonemap);
            _consecutiveRenderFailures = 0;
        }
        catch (Exception ex)
        {
            // A recording failure before EndFrame has not changed GPU resource states. Discard the
            // one-shot WATER001 CPU state and close (without submitting) the open command list so the
            // next CompositionTarget tick can BeginFrame on the same slot. The scoped water recovery
            // below normally submits a cleaned partial frame; this is the final safety net for failures
            // elsewhere in the frame or while recording that cleanup.
            try
            {
                _water?.SetFnvWater001Snapshot(null, 0, 0);
                _surface12?.DiscardWaterOpaqueSnapshotPreparation();
                _commandRecorder12?.AbortFrame();
            }
            catch (Exception recoveryEx)
            {
                Log.Warn("WorldView3DControl: failed to abandon the incomplete render frame: {0}",
                    recoveryEx.Message);
            }

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
        float? gameHourOverride = null, Vector3? cameraOriginOverride = null, bool enableShadows = true,
        VisibilityCylinder? lightVisibility = null, GpuTonemapSettings? tonemapOverride = null)
    {
        // The top-down overlay drives lighting from the 2D map's own time-of-day, passed via
        // gameHourOverride; the live perspective path uses the 3D control's _gameHour. enableLighting is
        // the per-call gate (still AND-ed with the scene's _showLighting toggle for the live path; the
        // overlay passes its own lighting-enabled flag as enableLighting with _showLighting left true).
        var gameHour = gameHourOverride ?? _gameHour;
        var lightingOn = enableLighting && _showLighting;
        var resolved = ResolveSceneAtmosphere(gameHour, lightingOn);
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
        // Root SRV t9 is bound on every path, including lighting-off/top-down frames. Its returned
        // count rides the previously spare b3 Params.w slot so both shading PSOs read one list.
        var placedLightCount = BindPlacedLights(
            cmd, frameIndex, lightVisibility, cameraOrigin, lightingOn);
        _references?.SetPlacedLightCount(placedLightCount);
        // Per-game ambient fill scale (uAmbientColor.w): FNV's 0.3 is too dark for the ambient-heavier
        // TES4-era engines, so Oblivion etc. raise it (see GameProfile.AmbientLightScale).
        var ambientScale = BethesdaMultitool.Core.Games.GameProfiles
            .For(_data?.Game ?? BethesdaMultitool.Core.Games.BethesdaGame.Unknown).AmbientLightScale;
        // uCameraOrigin.w = the active imagespace's EmissiveMult (hdrData[3], scene-pass emissive
        // brightness). Keyed off the same per-game resolution the display pass uses so the two stay
        // consistent.
        var tonemap = tonemapOverride ?? ResolveTonemapSettings();
        var hdrActive = _hdrEnabled && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0";
        var game = _data?.Game ?? Core.Games.BethesdaGame.Unknown;
        var sceneSunlightScale = GpuTonemapSettings.ResolveSceneSunlightScale(
            tonemap,
            game,
            hdrActive,
            isInterior: _selectedInterior is not null);
        var sceneSkyScale = GpuTonemapSettings.ResolveSceneSkyScale(
            tonemap, game, hdrActive, isInterior: _selectedInterior is not null);
        resolved = AtmosphereState.ApplySkyColorScale(resolved, sceneSkyScale);
        // Sun-shadow sampling constants: the rendered cascades' light matrices (with this frame's
        // render origin folded in) + packed params. Disabled (zero) until the cascades have
        // content, when the caller opts out (ortho export / top-down), or when the toggle / env
        // kill-switch is off — the shader's ShadowFactor then returns 1.0 and the scene is
        // pixel-identical to before.
        var shadow = default(Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12.ShadowSampleConstants);
        if (enableShadows && lightingOn && _showShadows && ShadowsEnvEnabled &&
            _shadowMap is { HasContent: true } shadowMap)
        {
            shadow = shadowMap.GetSampleConstants(cameraOrigin);
        }
        var projectedSunShadowActive =
            shadow.Params0.X > 0f || shadow.Params1.X > 0f ||
            shadow.Params2.X > 0f || shadow.Params3.X > 0f;
        var fogEnabled = enableFog && _showFog;
        _references?.SetFnvActiveAdtBaseState(
            lightingOn, projectedSunShadowActive, fogEnabled);
        _lastBoundShadowParams = shadow.Params0;
        var constants = AtmosphereConstants.From(
            resolved, game, gameHour, shadingCameraPos, lightingEnabled: lightingOn ? 1f : 0f,
            skyEnabled: _showSky ? 1f : 0f, fogEnabled: enableFog && _showFog ? 1f : 0f,
            placedLightCount: placedLightCount,
            cameraOrigin: cameraOrigin, ambientScale: ambientScale, shadow: shadow,
            emissiveMult: tonemap.EmissiveMult, hdrActive: hdrActive,
            // FO3/FNV ApplyCurrentParameterData exposes hdrData[11] to directional-light setup.
            // Its exact retail consumer is exterior HDR directional light only. It is scene state,
            // so a display-operator override must not suppress it.
            sunlightScale: sceneSunlightScale);
        var alloc = _ringBuffer12!.Allocate(frameIndex, AtmosphereConstants.ByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(AtmosphereConstants*)alloc.CpuPtr = constants; }
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, alloc.GpuAddress);
    }

    // CPU mirror of the b3 `cbuffer Atmosphere` the lighting/sky/water shaders declare. 10 float4
    // = 160 bytes before the shadow constants (CB-aligned by the ring buffer). When a flag is 0 the shader falls back to its
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
        public Vector4 SkyHorizon;         // rgb = sky-horizon color, w = effective HDR active (0/1)
        public Vector4 FogColorFogEnabled; // rgb = fog color, w = fogEnabled (0/1)
        public Vector4 Params;             // x = gameHour, y = fogNear, z = fogFar, w = placed-light count
        public Vector4 CameraPosFogPower;  // xyz = camera world pos (0 in camera-relative mode), w = fog power
        public Vector4 FogFarColorMax;     // rgb = far-fog color, w = maximum powered fog amount
        // Camera-relative render origin the scene VS subtract from each world vertex before
        // projection (0 when camera-relative is off). The PS shaders that read this CB
        // declare only the prefix they use, so fields appended after their prefix are layout-safe.
        public Vector4 CameraOrigin;       // xyz = render origin (= camera world pos),
                                           // w = IMGS EmissiveMult (explicit 1 when inactive)
        // Sun shadow CASCADES (appended after CameraOrigin — same append-safe contract). Each
        // matrix maps origin-relative world positions (the PS's vWorldPos space) into that
        // cascade's shadow clip; each params float4 packs (enabled, texel UV size, normalized
        // depth bias, bindless SRV slot). The PS picks the smallest containing cascade; all
        // params.x == 0 short-circuits ShadowFactor to 1.0, keeping the scene pixel-identical.
        public Matrix4x4 ShadowMatrix0;
        public Matrix4x4 ShadowMatrix1;
        public Matrix4x4 ShadowMatrix2;
        public Matrix4x4 ShadowMatrix3;
        public Vector4 ShadowParams0;
        public Vector4 ShadowParams1;
        public Vector4 ShadowParams2;
        public Vector4 ShadowParams3;
        // Full WTHR DALC directional-ambient cube. PositiveX.w is the explicit presence flag;
        // legacy weather keeps all six zero and shaders use AmbientColor.rgb unchanged.
        public Vector4 AmbientPositiveX;
        public Vector4 AmbientNegativeX;
        public Vector4 AmbientPositiveY;
        public Vector4 AmbientNegativeY;
        public Vector4 AmbientPositiveZ;
        public Vector4 AmbientNegativeZ;

        public const uint ByteSize = 10 * 16 + 4 * 64 + 4 * 16 + 6 * 16;

        public static AtmosphereConstants From(
            AtmosphereState.Resolved a,
            Core.Games.BethesdaGame game,
            float gameHour,
            Vector3 cameraPos,
            float lightingEnabled,
            float skyEnabled,
            float fogEnabled,
            float placedLightCount,
            Vector3 cameraOrigin,
            float ambientScale = 0.3f,
            Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12.ShadowSampleConstants shadow = default,
            float emissiveMult = 1f,
            bool hdrActive = true,
            float sunlightScale = 1f)
        {
            var directionalAmbient = AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
                game, AuthoredSkyArchitecture.Enabled, a.DirectionalAmbient);

            return new()
            {
                SunDirIntensity = new Vector4(a.SunWorldDirection, a.SunIntensity),
                SunColorLighting = new Vector4(a.SunColor * sunlightScale, lightingEnabled),
                // w carries the per-game ambient ("fill") scale read by the object/terrain shaders.
                AmbientColor = new Vector4(a.AmbientColor, ambientScale),
                SkyTopSkyEnabled = new Vector4(a.SkyTopColor, skyEnabled),
                // Spare w is the explicit HDR branch used only by classic Lighting30. This avoids
                // overloading EmissiveMult=0, which is valid authored IMGS data.
                SkyHorizon = new Vector4(a.SkyHorizonColor, hdrActive ? 1f : 0f),
                FogColorFogEnabled = new Vector4(a.FogColor, fogEnabled),
                Params = new Vector4(gameHour, a.FogNear, a.FogFar, placedLightCount),
                CameraPosFogPower = new Vector4(cameraPos, a.FogPower),
                FogFarColorMax = new Vector4(a.FogFarColor, a.FogMaxOpacity),
                // w = IMGS EmissiveMult (hdrData[3]); manual/headless CB writers likewise upload
                // an explicit neutral 1 when no imagespace is active.
                CameraOrigin = new Vector4(cameraOrigin, emissiveMult),
                ShadowMatrix0 = shadow.Matrix0,
                ShadowMatrix1 = shadow.Matrix1,
                ShadowMatrix2 = shadow.Matrix2,
                ShadowMatrix3 = shadow.Matrix3,
                ShadowParams0 = shadow.Params0,
                ShadowParams1 = shadow.Params1,
                ShadowParams2 = shadow.Params2,
                ShadowParams3 = shadow.Params3,
                AmbientPositiveX = directionalAmbient is { } cube
                    ? new Vector4(cube.PositiveX, 1f)
                    : Vector4.Zero,
                AmbientNegativeX = directionalAmbient is { } cubeNx
                    ? new Vector4(cubeNx.NegativeX, 0f)
                    : Vector4.Zero,
                AmbientPositiveY = directionalAmbient is { } cubePy
                    ? new Vector4(cubePy.PositiveY, 0f)
                    : Vector4.Zero,
                AmbientNegativeY = directionalAmbient is { } cubeNy
                    ? new Vector4(cubeNy.NegativeY, 0f)
                    : Vector4.Zero,
                AmbientPositiveZ = directionalAmbient is { } cubePz
                    ? new Vector4(cubePz.PositiveZ, 0f)
                    : Vector4.Zero,
                AmbientNegativeZ = directionalAmbient is { } cubeNz
                    ? new Vector4(cubeNz.NegativeZ, 0f)
                    : Vector4.Zero,
            };
        }
    }

    /// <summary>
    ///     Re-renders the sun shadow CASCADES per the re-render policy: a POSE change (sun
    ///     direction / snapped anchor) renders everything immediately; a CONTENT-only change
    ///     (streamed-in geometry) is throttled to every <see cref="ShadowContentRerenderFrames" />
    ///     frames; wind-animated foliage re-renders only the NEAR cascades every frame (their
    ///     frustums are pose-derived, so the published sampling constants stay valid). Each
    ///     rendered cascade binds its DSV, replays the frame's captured reference draws depth-only
    ///     (main + shadow-only off-screen casters), and draws the resident terrain cells (terrain
    ///     casts — hillsides shade valleys and self-shadow). Must be recorded AFTER the reference
    ///     pass (the replay uses this frame's ring-buffer CB addresses) and after every
    ///     screen-target draw (it rebinds the OM targets + viewport without restoring them). The
    ///     cascades rendered here are sampled from the NEXT frame on via the atmosphere CB.
    ///     <para>
    ///         The cascade ANCHOR is the camera XY with Z clamped into the loaded scene's vertical
    ///         extent: anchoring at the raw camera Z shifted the near cascades' footprints off the
    ///         ground below by ~sin(sunSlant)×altitude, so flying up silently degraded every
    ///         shadow to the far cascade.
    ///     </para>
    /// </summary>
    private void RecordSunShadowPass(
        Vortice.Direct3D12.ID3D12GraphicsCommandList cmd, Vector3 renderOrigin, Vector3 sceneCenter)
    {
        var terrainCasts = _showTerrain && _terrain is not null;
        if (_shadowMap is null || _references is null || (!_references.HasShadowDraws && !terrainCasts))
        {
            return; // nothing to cast (or a degenerate frame) — keep the previous cascades
        }

        // Vertical anchor: clamp the center into the loaded geometry's Z band (epoch-cached scan).
        var zExtent = _references.GetRenderedSceneWorldZExtentCached();
        var anchor = zExtent is { } ze
            ? new Vector3(sceneCenter.X, sceneCenter.Y, Math.Clamp(sceneCenter.Z, ze.Min, ze.Max))
            : sceneCenter;

        var lastRadius = MathF.Min(
            ShadowCascadeRadii[^1], ShadowRadiusEnvOverride ?? float.MaxValue) + SunShadowMath.CenterSnap;
        var poseKey = SunShadowMath.BuildKey(_lastResolvedSunDirection, anchor, lastRadius, 0);
        var contentVersion = HashCode.Combine(
            _references.BatchContentVersion, _terrain?.ContentVersion ?? 0);
        var animated = _references.ShadowDrawsIncludeAnimatedLeaves
                       || _references.ShadowDrawsIncludeAnimatedMeshes;

        _shadowContentThrottle++;
        var poseChanged = !_shadowMap.HasContent || poseKey != _shadowPoseKey;
        var contentDue = contentVersion != _shadowContentVersion &&
                         _shadowContentThrottle >= ShadowContentRerenderFrames;
        var fullRender = poseChanged || contentDue;
        if (!fullRender && !animated)
        {
            return;
        }
        if (!fullRender && _shadowPublishedFrustums is null)
        {
            return; // animated refresh before any publish — nothing consistent to refresh yet
        }

        var cascadeCount = Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12.CascadeCount;
        var frustums = new SunShadowMath.LightFrustum[cascadeCount];
        for (var i = 0; i < cascadeCount; i++)
        {
            if (fullRender)
            {
                var radius = MathF.Min(ShadowCascadeRadii[i], lastRadius);
                frustums[i] = SunShadowMath.BuildLightFrustum(
                    _lastResolvedSunDirection, anchor, renderOrigin, radius, _shadowMap.Resolution);
            }
            else
            {
                // Animated-only refresh: replay the PUBLISHED frustum, re-expressed against this
                // frame's render origin with the SAME fold the sampler applies — content and
                // sampling matrices then agree by construction. Rebuilding from the current
                // camera anchor here is wrong: the pose key's CenterSnap quantization means the
                // raw anchor drifts between key changes, and content rendered at the drifted
                // anchor strobes against the stale published matrices (the whole-cell flicker).
                var published = _shadowPublishedFrustums![i];
                frustums[i] = published with
                {
                    ViewProj = SunShadowMath.FoldSampleMatrix(
                        published.ViewProj, _shadowPublishedOrigin, renderOrigin),
                };
            }
        }

        if (ShadowDiagLogging)
        {
            if (fullRender)
            {
                _shadowAnimatedActive = false;
                Log.Info("[Shadow] rendering cascades: draws={0} pose={1} content={2} dir=({3:0.00},{4:0.00},{5:0.00}) anchorZ={6:0}",
                    _references.ShadowDrawCount, poseChanged, contentDue,
                    _lastResolvedSunDirection.X, _lastResolvedSunDirection.Y, _lastResolvedSunDirection.Z,
                    anchor.Z);
            }
            else if (!_shadowAnimatedActive)
            {
                _shadowAnimatedActive = true;
                Log.Info("[Shadow] continuous near-cascade re-render engaged (wind-animated foliage): draws={0}",
                    _references.ShadowDrawCount);
            }
        }

        // Animated-only frames refresh just the near cascades (sway lives in the near field; the
        // far cascades' full-distance terrain gathers are the cost). They replay the published
        // frustums verbatim (folded above), so no re-publish is needed on that path.
        var renderCount = fullRender ? cascadeCount : ShadowAnimatedCascadeCount;
        var drewAny = false;
        Span<bool> cascadeHasDraws = stackalloc bool[cascadeCount];
        for (var i = 0; i < renderCount; i++)
        {
            _shadowMap.BeginCascade(cmd, i);
            var drewCascade = _references.RenderShadowDepth(frustums[i]);
            if (terrainCasts)
            {
                // Same cylinder footprint the main pass streamed, clamped to this cascade's
                // coverage — draws only RESIDENT cells (the designed RenderInternal double-call).
                // Animated refreshes center it on the PUBLISHED anchor so the whole cleared
                // footprint is redrawn (a current-camera cylinder leaves a trailing-edge strip
                // of terrain depth un-redrawn).
                var terrainCenter = fullRender ? sceneCenter : _shadowPublishedCylinderCenter;
                var terrainRadius = MathF.Min(MathF.Min(ShadowCascadeRadii[i], lastRadius), _renderDistance);
                drewCascade |= _terrain!.RenderShadowDepth(
                    frustums[i].ViewProj, new VisibilityCylinder(terrainCenter, terrainRadius)) > 0;
            }
            _shadowMap.EndCascade(cmd, i);
            cascadeHasDraws[i] = drewCascade;
            drewAny |= drewCascade;
            if (!fullRender)
            {
                _shadowMap.SetCascadeAvailability(i, drewCascade);
            }
        }

        if (fullRender)
        {
            // Publish availability even for a degenerate render: every target was just cleared, so
            // retaining an enabled bit would create a fully-lit cascade-shaped hole. Empty maps are
            // disabled and fall through to farther cascades. Keep the old policy keys when nothing
            // drew so the next frame retries instead of caching the degenerate result.
            _shadowMap.Publish(frustums, renderOrigin, cascadeHasDraws);
            if (drewAny)
            {
                _shadowPoseKey = poseKey;
                _shadowContentVersion = contentVersion;
                _shadowContentThrottle = 0;
                _shadowPublishedFrustums = frustums;
                _shadowPublishedOrigin = renderOrigin;
                _shadowPublishedCylinderCenter = sceneCenter;
            }
        }
        // Animated-only refreshes keep their published matrices but update each refreshed map's
        // availability bit above. A degenerate full render publishes all-disabled and retries.
    }

    private void RenderFrameD3D12(GpuTonemapSettings tonemap)
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
        // The scene ALWAYS renders into the HDR float scene color (MSAA or 1-sample); the back buffer
        // is now an 8-bit display target that surface.ResolveTo tonemaps the HDR scene into before
        // post-tonemap diagnostics + Present (the surface owns the state transitions). The scene
        // color stays in RENDER_TARGET across frames, so nothing to transition here.
        var sceneRtv = surface.MsaaColorRtv;
        var sceneDsv = surface.DepthStencilView;
        var acquireMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
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
            // Sky backdrop for the ortho modes: parallel ortho rays would all sample ONE sky
            // direction (a flat color fill), so render the camera-centered dome through a synthetic
            // PERSPECTIVE view aimed along the ortho view direction instead. Sky draws depth-off
            // first; the ortho scene overwrites it wherever geometry exists, leaving the gradient +
            // clouds + sun/moon as the backdrop. The far plane just needs to clear the fixed-radius
            // dome (12k) — scene clipping is untouched (the scene uses viewProjScene).
            var skyElevation = OrthoViewProjBuilder.ElevationDegFor(_projectionMode);
            var skyForward = -OrthoViewProjBuilder.EyeDirection(_azimuthDeg, skyElevation);
            var (_, skyUp) = ProjectionCameraBasis();
            _camera.FarPlane = OrthoSkyFarPlane;
            viewProjSky = Matrix4x4.CreateLookAt(Vector3.Zero, skyForward, skyUp)
                          * _camera.GetProjectionMatrix(aspect);
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
        var weatherTransition = ResolveSelectedWeatherTransition();
        var currentWind = (weatherTransition.CurrentWeather?.Data?.WindSpeed ?? 0) / 255f;
        var outgoingWind = (weatherTransition.OutgoingWeather?.Data?.WindSpeed ?? 0) / 255f;
        float weatherWind;
        if (_selectedInterior is not null)
        {
            weatherWind = 0f;
        }
        else if (weatherTransition.OutgoingWeather is null)
        {
            weatherWind = currentWind;
        }
        else
        {
            weatherWind = outgoingWind + ((currentWind - outgoingWind) * weatherTransition.CurrentWeatherWeight);
        }
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
        var shadowRingReserved = false;
        if (shadowsActive)
        {
            shadowRingReserved = _ringBuffer12!.TryReserveTail(ShadowPassRingReservationBytes);
            if (shadowRingReserved)
            {
                _shadowMap ??= new Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12(
                    _gpu12!, _cbvSrvUavHeap12!);
                _references!.ArmShadowCapture(MathF.Min(ShadowCasterRingRadius, _renderDistance));
            }
            else
            {
                // A deliberately tiny environment override may not fit the protected pass at all.
                // Keep the existing maps untouched instead of clearing an enabled cascade to white.
                shadowsActive = false;
                _references!.DisarmShadowCapture();
            }
        }
        else
        {
            _references?.DisarmShadowCapture();
        }

        // Reserve the water pass's ring budget NOW — stacked on top of any shadow reservation — so the
        // terrain + reference draws (which fill the ring in dense/streaming frames and self-truncate)
        // cannot consume the constants the water pass needs later. Handed back to water immediately
        // before it draws (below). Best-effort: a too-small env ring override just reverts to soft-skip.
        var waterRingReserved = _showWater && _water is not null &&
                                _ringBuffer12!.TryReserveTail(WaterPassRingReservationBytes);

        // Resolve + upload the shared atmosphere CB (b3) once per frame, bound for the whole scene.
        // Terrain/reference/water read it for directional + ambient lighting; the sky/fog flags drive
        // those shader paths and are each on by default. Bound once — the renderers only set their
        // own PSO + slots, never the root signature, so this CBV survives every scene pass.
        // Ortho modes: force fog OFF (distance-from-a-1,000,000-unit-eye fog would max out everywhere)
        // and feed the ortho eye as the shading camera position so the specular view vector is parallel.
        var sceneSkyScale = GpuTonemapSettings.ResolveSceneSkyScale(
            tonemap,
            _data?.Game ?? Core.Games.BethesdaGame.Unknown,
            _hdrEnabled && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0",
            isInterior: _selectedInterior is not null);
        BindAtmosphereConstants(
            cmd, recorder.FrameIndex, enableFog: !projectionActive, cameraRelative: cameraRelative,
            shadingCameraPosOverride: projectionActive ? orthoEye : null,
            cameraOriginOverride: cameraRelative ? sceneRenderOrigin : null,
            lightVisibility: cylinder, tonemapOverride: tonemap);

        // Sky FIRST — gradient + clouds + stars into the cleared color target (depth OFF, so terrain
        // overwrites it via the normal depth pass; OFF ⇒ the flat dark-blue clear shows), then the
        // sun/moon billboards over it. Reads the b3 atmosphere CB bound just above. Sky renders
        // CAMERA-RELATIVE: the translation-free viewProjSky with the dome centered at the origin
        // (domeCenter 0), so far-from-origin camera coords don't jitter the dome. In the ortho
        // projection modes viewProjSky is the synthetic perspective view built above, and the
        // billboard basis comes from the ortho camera (the parked perspective camera is stale).
        if (_showSky)
        {
            RenderSky(viewProjSky, Vector3.Zero,
                projectionActive ? ProjectionCameraBasis() : null,
                skyColorScale: sceneSkyScale);
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
        var visibleReferences = 0;
        if (_showReferences)
        {
            visibleReferences = _references?.Render(
                viewProjScene, cylinder, deferBlended: true, cullViewProj: viewProjAbsolute,
                renderOrigin: referenceRenderOrigin, cullCameraPose: cullPose,
                cameraPosition: projectionActive ? orthoEye : _camera.Position,
                cameraForward: projectionActive
                    ? Vector3.Normalize(_projectionFocus - orthoEye)
                    : _camera.Forward) ?? 0;
        }
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
        // Feed water/effects the real scene depth (terrain + opaque references already wrote it).
        // Both paths select Texture2D or Texture2DMS from the surface sample count.
        var depthRes = surface.DepthResource;
        // Ortho modes skip the depth-SRV soft-fade: it linearizes with the perspective camera near/far,
        // which don't describe the ortho depth range. Fall back to the hardware depth-test water PSO
        // (same path the top-down overlay uses) so water stays height-correct in ortho.
        var waterUsesDepth = !projectionActive && _showWater && _water is not null
                             && _depthSrv is not null && depthRes is not null;
        // The same R32 view feeds eligible deferred effects. A read-only DSV stays bound for that
        // pass, preserving exact hardware depth for ordinary alpha decals/foliage and blend modes.
        var referencesUseDepth = !projectionActive && _showReferences && _references is not null
                                 && _depthSrv is not null && depthRes is not null;
        var fnvWater001SnapshotPrepared = false;
        var waterTransparencyPartitioned = false;
        var belowWaterTransparencyDrawn = false;
        var partitionedWaterPlaneHeight = 0f;
        var waterDepthSampled = false;
        var sceneDepthSampled = waterUsesDepth || referencesUseDepth;
        var sampledDepthState = Vortice.Direct3D12.ResourceStates.DepthRead |
                                Vortice.Direct3D12.ResourceStates.PixelShaderResource;
        // Water projects CAMERA-RELATIVE like terrain/references (same viewProjScene + render origin, VS
        // subtracts) — the absolute path lost float32 precision far from the origin and the water edge
        // shimmered/ended at view-angle-dependent Z as the camera rotated. Ripple UVs + the PS view vector
        // stay world-anchored: vWorldPos is still the absolute position (see water.vert.hlsl).
        var visibleWater = 0;
        try
        {
            RefreshWaterAppearanceForCurrentCell();
            _water?.SetSceneDepth(
                waterUsesDepth ? _depthSrv!.Value.BindlessIndex : NoDepthSrv,
                _camera.NearPlane,
                _camera.FarPlane,
                surface.SampleCount);
            if (_showWater && _water is not null)
            {
                var fnvWater001Preflight = _water.GetFnvWater001Preflight(
                    cylinder,
                    isPerspectiveProjection: !projectionActive);
                if (fnvWater001Preflight.Candidate &&
                    TryEnsureWaterOpaqueSnapshotSrv())
                {
                    // Underwater translucent decals/effects belong in the refraction source. Draw
                    // that conservative partition before copying scene color; the complementary
                    // intersecting/above-water partition remains after the water surface.
                    if (_showReferences && _references is not null)
                    {
                        partitionedWaterPlaneHeight = fnvWater001Preflight.PlaneHeight;
                        _references.RenderBlendedDeferredBelowWater(partitionedWaterPlaneHeight);
                        belowWaterTransparencyDrawn = true;
                    }

                    if (surface.TryPrepareWaterOpaqueSnapshot(cmd))
                    {
                        fnvWater001SnapshotPrepared = true;
                        waterTransparencyPartitioned = belowWaterTransparencyDrawn;
                        _water.SetFnvWater001Snapshot(
                            _waterOpaqueSnapshotSrv!.Value.BindlessIndex,
                            surface.Width,
                            surface.Height);
                    }
                    else
                    {
                        // WATER003 can cover the pre-snapshot below-water attempt. Leave the split
                        // disabled so the ordinary post-water pass redraws every translucent item.
                        _water.SetFnvWater001Snapshot(null, 0, 0);
                    }
                }
                else
                {
                    // Keep the positive preflight armed when allocation/capture failed: Render repeats
                    // the check and records SnapshotUnavailable as the local WATER003 fallback reason.
                    _water.SetFnvWater001Snapshot(null, 0, 0);
                }
            }
            else
            {
                _water?.SetFnvWater001Snapshot(null, 0, 0);
            }
            _references?.SetSceneDepth(
                referencesUseDepth ? _depthSrv!.Value.BindlessIndex : NoDepthSrv,
                _camera.NearPlane,
                _camera.FarPlane,
                surface.SampleCount);
            if (waterUsesDepth)
            {
                // Depth stays bound as a READ-ONLY DSV while it is simultaneously the water
                // shader's SRV: the depth-sample PSOs keep the hardware GreaterEqual test, whose
                // per-sample rejection antialiases water edges at MSAA'd mesh silhouettes (the
                // old shader-side clip was pixel-rate and left bright fringes).
                cmd.OMSetRenderTargets(sceneRtv, surface.ReadOnlyDepthStencilView);
                cmd.ResourceBarrierTransition(depthRes!,
                    Vortice.Direct3D12.ResourceStates.DepthWrite,
                    sampledDepthState);
                waterDepthSampled = true;
            }

            // Hand the water pass its protected ring budget now that every earlier consumer (terrain,
            // references, the below-water transparency partition) has finished allocating. The shadow
            // reservation, stacked beneath this one, stays protected for the frame-end replay.
            if (waterRingReserved)
            {
                _ringBuffer12!.ReleaseTailReservation(WaterPassRingReservationBytes);
                waterRingReserved = false;
            }

            visibleWater = _showWater
                ? _water?.Render(
                    viewProjScene,
                    cylinder,
                    referenceRenderOrigin,
                    isPerspectiveProjection: !projectionActive) ?? 0
                : 0;

            // Render consumes the descriptor up front; clear it again defensively and return the
            // borrowed/copy snapshot to its ordinary ResolveDest/CopyDest baseline before blends.
            _water?.SetFnvWater001Snapshot(null, 0, 0);
            if (fnvWater001SnapshotPrepared)
            {
                surface.RestoreWaterOpaqueSnapshot(cmd);
                fnvWater001SnapshotPrepared = false;
            }
        }
        catch
        {
            try
            {
                _water?.SetFnvWater001Snapshot(null, 0, 0);
                if (fnvWater001SnapshotPrepared)
                {
                    surface.RestoreWaterOpaqueSnapshot(cmd);
                }
                if (waterDepthSampled)
                {
                    cmd.OMSetRenderTargets(sceneRtv);
                    cmd.ResourceBarrierTransition(depthRes!, sampledDepthState,
                        Vortice.Direct3D12.ResourceStates.DepthWrite);
                    cmd.OMSetRenderTargets(sceneRtv, sceneDsv);
                }

                // Submit only the valid partial scene recorded before the failure. Scene color,
                // depth, and the borrowed/copy snapshot are all back at their next-frame baselines;
                // the back buffer was never touched because final resolve has not run yet.
                recorder.EndFrame();
            }
            catch
            {
                // If cleanup itself cannot be recorded, discard the entire unsubmitted list. Its
                // barriers never reach the GPU, so forgetting the CPU preparation flag is correct.
                surface.DiscardWaterOpaqueSnapshotPreparation();
                try { recorder.AbortFrame(); }
                catch { /* The outer render failure path logs device/recorder failures. */ }
            }
            throw;
        }
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WaterEnd);
        var waterMs = ElapsedMilliseconds(segmentStarted);
        // Blended (transparent) reference submeshes draw AFTER water so water never paints over
        // them. Eligible effects sample scene depth and manually reject/fade intersections; the
        // read-only DSV keeps the established GreaterEqual hardware test for every blended draw.
        if (referencesUseDepth)
        {
            if (!waterUsesDepth)
            {
                cmd.OMSetRenderTargets(sceneRtv);
                cmd.ResourceBarrierTransition(depthRes!,
                    Vortice.Direct3D12.ResourceStates.DepthWrite,
                    sampledDepthState);
            }
            cmd.OMSetRenderTargets(sceneRtv, surface.ReadOnlyDepthStencilView);
        }
        if (_showReferences)
        {
            if (waterTransparencyPartitioned)
            {
                _references?.RenderBlendedDeferredAtOrAboveWater(partitionedWaterPlaneHeight);
            }
            else
            {
                _references?.RenderBlendedDeferred();
            }
        }
        if (sceneDepthSampled)
        {
            cmd.OMSetRenderTargets(sceneRtv); // release read-only DSV before returning to DepthWrite
            cmd.ResourceBarrierTransition(depthRes!,
                sampledDepthState,
                Vortice.Direct3D12.ResourceStates.DepthWrite);
            cmd.OMSetRenderTargets(sceneRtv, sceneDsv);
        }
        // Cell-grid wireframe stays in the HDR scene pass: unlike the depth-disabled diagnostics it
        // deliberately depth-TESTS against the scene so terrain occludes the grid walls ("part of
        // the 3D scene, not painted over it") — the post-resolve LDR target has no scene depth.
        // The navmesh overlay and selection outline move to the LDR block after ResolveTo below.
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WireframeStart);
        var visibleWireframe = _showWireframe ? _cellGrid?.Render(viewProjAbsolute, cylinder) ?? 0 : 0;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WireframeEnd);
        var wireframeMs = ElapsedMilliseconds(segmentStarted);

        // Sun-shadow depth pass — recorded LAST (nothing after it draws to the screen targets, so
        // the shadow DSV/viewport need no restore). Replays this frame's captured reference batches
        // from the light's view; skipped entirely while the cache key (sun dir, snapped camera,
        // content version) is unchanged, so a settled scene records zero shadow work.
        if (shadowsActive)
        {
            // All preceding allocations honored the protected tail. Release it only when the
            // shadow pass is ready to consume it; no screen-space renderer allocates in between.
            if (shadowRingReserved)
            {
                _ringBuffer12!.ReleaseTailReservation();
            }
            RecordSunShadowPass(cmd, referenceRenderOrigin, _camera.Position);
        }

        segmentStarted = StartProfileTimestamp();
        // Tonemap the HDR scene color into the back buffer (resolving MSAA first when active). The
        // back buffer deliberately stays in RENDER_TARGET for LDR editor diagnostics: overlays like
        // the navmesh, selection outline, and collision cages must not feed eye adaptation or bloom
        // (nor be tonemapped/eye-adapted themselves — a debug overlay's brightness must not track
        // scene exposure). Collision additionally draws camera-relative because its large absolute
        // coordinates wobbled against camera-relative reference geometry in the HDR frame.
        surface.ResolveTo(cmd, backBuffer);

        var wantsSelectionOverlay = _selectionHighlight is not null;
        var visibleNavMesh = 0;
        if ((_showNavMesh && _navMesh is not null) || (_showCollision && _collisionDebug is not null) ||
            wantsSelectionOverlay)
        {
            cmd.OMSetRenderTargets(backRtv);
            cmd.RSSetViewport(new Vortice.Mathematics.Viewport(
                0, 0, surface.Width, surface.Height, 0f, 1f));
            cmd.RSSetScissorRect((int)surface.Width, (int)surface.Height);
            // GpuTonemapPass12 binds its own fullscreen root signature + descriptor heap. Restore
            // the scene root before the overlay renderers bind their b0 constant buffers.
            cmd.SetDescriptorHeaps(1, new[] { _cbvSrvUavHeap12.Heap });
            cmd.SetGraphicsRootSignature(_rootSignature12!.RootSignature);

            // Navmesh overlay — translucent and depth-disabled so authored NAVM remains visible
            // even where it sits slightly below rendered ground.
            if (_showNavMesh)
            {
                visibleNavMesh = _navMesh?.Render(viewProjAbsolute, cylinder, ldrTarget: true) ?? 0;
            }

            if (_showCollision && _collisionDebug is not null)
            {
                _collisionDebug.Render(
                    viewProjScene, cylinder, surface.Width, surface.Height,
                    sceneRenderOrigin, ldrTarget: true);
            }

            // Export framing preview (Export tab + "Show framing"): the captured world AABB + view
            // gizmo, in absolute world coords so it uses the absolute view-proj (like navmesh/selection)
            // — no camera-relative origin math for a static box.
            if (ExportFramingVisible && _exportFraming is not null)
            {
                EnsureExportFramingLines();
                if (_exportFramingLines is { Length: >= 2 } framing)
                {
                    _exportFraming.Render(
                        viewProjAbsolute, framing, ExportFramingColor,
                        surface.Width, surface.Height, ldrTarget: true);
                }
            }

            // Selection outline, drawn last + depth-disabled so it stays visible on top of
            // everything (no-op when nothing is selected).
            _selectionHighlight?.Render(viewProjAbsolute, ldrTarget: true);
        }

        var totalCells = _terrain?.CellCount ?? _cellGrid?.CellCount ?? 0;
        // In interior mode the single loaded cell has no LAND, so the terrain and (off-by-default)
        // wireframe passes both report 0 drawn — even though the camera sits inside that exact cell.
        // Count the loaded cell(s) as visible so the HUD reads "1 / 1" instead of a misleading "0 / 1".
        var visible = _selectedInterior is not null
            ? totalCells
            : Math.Max(visibleTerrain, visibleWireframe);
        UpdateHud(visible, totalCells, visibleWater, visibleReferences, visibleNavMesh);
        var hudMs = ElapsedMilliseconds(segmentStarted);

        GpuSwapChainSurface12.FinishBackBuffer(cmd, backBuffer);

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
        var totalAllocatedBytes = GC.GetTotalAllocatedBytes(false);
        var allocatedBytes = Math.Max(0L, totalAllocatedBytes - _lastTotalAllocatedBytes);
        _lastTotalAllocatedBytes = totalAllocatedBytes;

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
            ManagedMemoryBytes: GC.GetTotalMemory(false),
            AllocatedBytes: allocatedBytes);

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
