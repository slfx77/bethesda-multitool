using System.Diagnostics;
using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using Microsoft.UI.Xaml.Media;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     Cascades allowed to re-render in one frame. Spreading an overdue ladder across frames turns
    ///     a single ~60 ms spike into a sequence of ~30 ms frames — the mean is unchanged but the
    ///     frame-to-frame VARIANCE is what reads as a pulsing movement speed, so evenness is the goal.
    /// </summary>
    private const int ShadowMaxCascadeRendersPerFrame = 2;

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
        ((uint)Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount * 3u + 1u) *
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

    // Render-loop failure tolerance: skip + retry on transient frame failures, detach only when
    // they persist (see the OnRendering catch block).
    private const int MaxConsecutiveRenderFailures = 3;

    // Grid pitch for the snapped camera-relative render origin (see sceneRenderOrigin). One FNV
    // cell; fixed across games — it's a precision/stability trade, not a world-structure quantity.
    private const float RenderOriginGridSize = 4096f;

    // Far plane for the synthetic perspective sky view in the ortho projection modes: only needs
    // to clear the fixed 12k dome radius (+ sun/moon billboards). Scene clipping is unaffected.
    private const float OrthoSkyFarPlane = 32_000f;
    private static readonly Vector2 WindDirection = Vector2.Normalize(new Vector2(0.82f, 0.57f));

    // Translation-tolerant cull cache kill-switch (FALLOUT_VIEWER_TOLERANT_CULL=0 → exact compare).
    private static readonly bool TolerantCullEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.TolerantCull) != "0";

    private static readonly bool ShadowsEnvEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.Shadows) != "0";

    // FIXED cascade radii ladder (near → far). Deliberately DECOUPLED from the render-distance
    // setting: quality bands and shadow reach are properties of the shadow system; the draw
    // distance only bounds where geometry — and therefore any shadow — exists at all. The far
    // cascade's 32-cell reach covers every practical draw distance at a constant ~64 units/texel;
    // FALLOUT_VIEWER_SHADOW_RADIUS (world units) can cap the LAST cascade for diagnostics.
    private static readonly float[] ShadowCascadeRadii = { 2048f, 8192f, 32768f, 131072f };

    /// <summary>
    ///     Per-cascade camera-drift quantum for the re-render pose key, ~radius/4.
    ///     <para>
    ///         A single key over all four cascades at <see cref="SunShadowMath.CenterSnap" /> (512)
    ///         re-rendered the ENTIRE ladder on the near cascade's schedule. At 4096 texels the far
    ///         cascade resolves 64 world units per texel, so a 512-unit camera move shifts it by
    ///         EIGHT texels — invisible, yet it cost a full replay of every caster and every resident
    ///         terrain cell. Scaling the quantum with the radius keeps each cascade's re-render rate
    ///         proportional to what it can actually resolve: at 2048 u/s the ladder goes from
    ///         {3.5, 3.5, 3.5, 3.5} frames between renders to {3.5, 14, 56, 226}.
    ///     </para>
    /// </summary>
    private static readonly float[] ShadowCascadeSnap = { 512f, 2048f, 8192f, 32768f };

    // Per-render console logging is DIAGNOSTIC-ONLY (FALLOUT_VIEWER_SHADOW_DIAG=1): the log line
    // fired on every re-render, which while walking meant constantly — console I/O at frame rate
    // was a measurable part of the reported lag.
    private static readonly bool ShadowDiagLogging =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.ShadowDiag) == "1";

    /// <summary>
    ///     Planar reflection infrastructure kill-switch (env). "0" restores the 2-row
    ///     sky-gradient stand-in regardless of the GUI toggle; the live per-session state is the
    ///     Video expander's Water Reflections toggle (<c>_waterReflectionsEnabled</c>), ANDed below.
    /// </summary>
    private static readonly bool WaterReflectionEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.WaterReflection) != "0";

    private static readonly float? ShadowRadiusEnvOverride = ParseShadowRadiusEnvOverride();

    private readonly int[] _lastShadowReferenceDrawsByCascade =
        new int[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    private readonly int[] _lastShadowReferenceInstancesByCascade =
        new int[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    private readonly int[] _lastShadowTerrainCellDrawsByCascade =
        new int[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    private readonly ShadowContentKey[] _shadowContentKeys =
        new ShadowContentKey[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    private readonly int[] _shadowContentThrottles =
        new int[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    // Re-render policy state (see RecordSunShadowPass), PER CASCADE: the pose key each cascade was
    // last rendered at, the exact content key it saw, its throttle counter, and the anchor it was
    // fitted around (used to rank staleness when the per-frame render cap defers one). Cascades come
    // due independently — each at a drift quantum scaled to what its texels can resolve — so none of
    // this can be shared, which is what made the far cascade re-render on the near cascade's clock.
    private readonly SunShadowMath.ShadowKey[] _shadowPoseKeys =
        new SunShadowMath.ShadowKey[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    private readonly Vector3[] _shadowPublishedAnchors =
        new Vector3[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    private readonly Vector3[] _shadowPublishedCylinderCenters =
        new Vector3[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    // The frustums the cascades were last PUBLISHED with (+ the render origin they were built
    // against and the terrain-cylinder center used). Animated-only refreshes MUST replay these
    // exact frustums (origin-folded to the current frame): the pose KEY snaps the anchor by
    // CenterSnap, so between key changes the raw camera anchor drifts up to ~512 units —
    // rebuilding frustums from it re-renders content the published sampling matrices no longer
    // describe, which strobed cell-sized false shadows during camera movement whenever swaying
    // foliage kept the per-frame refresh alive. The terrain cylinder replays the published
    // center for the same reason: a current-camera cylinder leaves the cleared trailing-edge
    // strip of the footprint unredrawn, popping terrain shadows there.
    /// <summary>
    ///     Each cascade's PUBLISHED frustum, expressed in <see cref="_shadowPublishedOrigins" />
    ///     at the same index. The two are written together and must never diverge — folding a matrix into
    ///     a new origin without updating the origin makes the next fold apply the delta twice.
    /// </summary>
    private readonly SunShadowMath.LightFrustum[] _shadowPublishedFrustums =
        new SunShadowMath.LightFrustum[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    private readonly Vector3[] _shadowPublishedOrigins =
        new Vector3[Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount];

    private int _consecutiveRenderFailures;

    // Median-of-3 camera timestep filter (single-writer: the render loop).
    private readonly FrameDeltaFilter _frameDeltaFilter = new();

    // GPU address of the most recent b3 atmosphere CB bound by BindAtmosphereConstants.
    private ulong _lastAtmosphereCbGpuAddress;
    private Vector4 _lastBoundShadowParams; // diagnostics: what the last b3 upload carried

    // This frame's raw camera timestep (see FrameProfileSample.DeltaSeconds).
    private float _lastDeltaSeconds;

    // The directional-light direction resolved by the LAST BindAtmosphereConstants call — the
    // frame-end shadow pass fits the light frustum around it (same frame, same resolve).
    private Vector3 _lastResolvedSunDirection = new(0.5f, 0.5f, 1f);

    private int _lastShadowCascadeMask;

    // Captured batches before cascade filtering. The arrays below are the submitted-work truth.
    private int _lastShadowDrawCount;

    // Shadow-pass telemetry. The mean GPU cost cannot distinguish a large full render every few
    // frames from a cheap animated refresh every frame, and those want opposite fixes — so the mode,
    // the cascade set and the submitted draw counts are recorded per pass and emitted to the trace.
    private ShadowPassMode _lastShadowMode;
    private int _lastShadowTerrainCellDraws;
    private bool _shadowAnimatedActive;

    // ── Unit-scaled ladder ─────────────────────────────────────────────────────────────────────
    // The radii/snap ladders above are HUMAN-SCALE distances in classic units (~70/metre). In
    // Starfield (1 unit = 1 m) the unscaled near cascade covers 2 km and resolves ~1 m per texel,
    // with the derived two-texel pixel bias reaching ~2 m — metre-scale geometry casts blocky
    // shadows detached from their bases. Scaled lazily against _unitScale so every shadow-pass
    // entry point stays correct without depending on grid-build ordering; classic games alias the
    // static arrays (zero per-frame cost, bit-identical behavior).
    private float[] _shadowCascadeRadiiScaled = ShadowCascadeRadii;
    private float[] _shadowCascadeSnapScaled = ShadowCascadeSnap;
    private float _shadowCasterRingRadiusScaled = ShadowCasterRingRadius;

    // Cascade anchor resolved when the shadow capture was armed, reused verbatim by the frame-end pass.
    private Vector3 _shadowFrameAnchor;

    // Rendered-scene world Z span sampled alongside the anchor, and reused verbatim for the same
    // reason: it feeds SunShadowMath.CascadeCasterReach on BOTH sides (the instance classification
    // at arm time and the frustum fit at frame end), and those two must agree exactly or the near
    // cascade gets a caster-shaped hole.
    private float _shadowFrameSceneZSpan;
    private float _shadowLadderScaledForUnit = 1f;

    // Sun shadow map (directional shadows: reference batches cast onto terrain + references).
    // Lazily created on the first shadow-enabled frame; disposed with the D3D12 backend
    // (DisposeD3D12Backend). Kill-switch FALLOUT_VIEWER_SHADOWS=0; UI toggle in the lighting flyout.
    private Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12? _shadowMap;

    private WaterStreamInterleave? _waterStreamInterleave;

    // SpeedTree leaf-wind animation clock + strength. Engine model (Sky::UpdateWind): S = the active
    // weather's wind-speed byte / 255, forced 0 in interiors — so by default wind FOLLOWS the same
    // weather selection that drives clouds/lighting. _windStrength is a MANUAL OVERRIDE: null = auto
    // (weather-driven); set via the lighting panel's "Override wind" row or the FALLOUT_VIEWER_SPT_WIND
    // env var.
    private float _windClockSeconds;
    private bool _windEnvChecked;
    private float? _windStrength;

    /// <summary>The live water-reflection gate: env kill-switch AND the Video-expander toggle.</summary>
    private bool WaterReflectionActive => WaterReflectionEnabled && _waterReflectionsEnabled;

    /// <summary>
    ///     Camera-relative scene rendering: the ONE definition, shared by the live frame and the
    ///     offscreen capture. These were independent before — the capture hardcoded absolute — which
    ///     silently made every headless repro run a different (and less precise) renderer than the
    ///     view whose coordinates produced the bug report.
    /// </summary>
    private static bool CameraRelativeSceneRendering => !string.Equals(
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.CameraRelative), "0",
        StringComparison.Ordinal);

    private void EnsureShadowLadderScale()
    {
        // Change detection on an ASSIGNED value (HumanScaleFactor output), so a tight epsilon is
        // purely to satisfy the float-equality rule — the real scales are 1 and 1/70.
        const float epsilon = 1e-6f;
        if (MathF.Abs(_shadowLadderScaledForUnit - _unitScale) < epsilon) return;
        _shadowLadderScaledForUnit = _unitScale;
        if (MathF.Abs(_unitScale - 1f) < epsilon)
        {
            _shadowCascadeRadiiScaled = ShadowCascadeRadii;
            _shadowCascadeSnapScaled = ShadowCascadeSnap;
            _shadowCasterRingRadiusScaled = ShadowCasterRingRadius;
            return;
        }

        _shadowCascadeRadiiScaled = ScaleLadder(ShadowCascadeRadii, _unitScale);
        _shadowCascadeSnapScaled = ScaleLadder(ShadowCascadeSnap, _unitScale);
        _shadowCasterRingRadiusScaled = ShadowCasterRingRadius * _unitScale;

        static float[] ScaleLadder(float[] source, float scale)
        {
            var scaled = new float[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                scaled[i] = source[i] * scale;
            }

            return scaled;
        }
    }

    private static float? ParseShadowRadiusEnvOverride()
    {
        var raw = EnvironmentVariables.Get(EnvironmentVariables.Viewer.ShadowRadius);
        return float.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0f
            ? v
            : null;
    }

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
        if (Visibility == Microsoft.UI.Xaml.Visibility.Collapsed)
        {
            ReseedFrameClocks();
            return;
        }
        if (_surface12 is null || _gpu12 is null || _commandRecorder12 is null)
        {
            ReseedFrameClocks();
            return;
        }

        // Skip the whole frame until a scene is loaded. The render loop attaches at device init (see
        // Lifecycle.AttachRenderLoop), BEFORE LoadData runs — so without this gate the control renders an
        // empty scene at vsync for the entire duration of a worldspace load happening on the background
        // thread, contending for the GPU and triggering GC pauses that stall both threads. That stretches
        // the load many-fold: a 5.6M-record FO76 parse measured ~8x slower with the loop running than the
        // same parse headless (world-data 2.5 min vs 17.6 s). Rendering resumes the instant LoadData sets _data.
        if (_data is null)
        {
            ReseedFrameClocks();
            return;
        }

        // Pay the GPU fence wait BEFORE sampling the clock and the camera. This frame cannot start
        // recording until the slot frees regardless, and in a GPU-bound scene that wait is most of the
        // frame (measured at 46 ms of 70 ms) — so a pose integrated ahead of it is already that stale
        // when recording begins, and the view visibly trails the mouse. Waiting first costs nothing and
        // hands the renderer the freshest pose the frame can deliver. BeginFrame re-checks the fence
        // and finds it satisfied.
        // Best-effort: this sits outside the frame's try/catch, so a device-removal here must not
        // bypass the recovery path below. Swallow it and let BeginFrame surface the real failure.
        try
        {
            _commandRecorder12.WaitForFrameSlot();
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: pre-update fence wait failed ({0}); deferring to BeginFrame.",
                ex.Message);
        }

        var now = Stopwatch.GetTimestamp();
        var deltaSeconds = (float)Stopwatch.GetElapsedTime(_lastFrameStartTimestamp, now).TotalSeconds;
        _lastFrameStartTimestamp = now;

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
                AdaptFactor = MathF.Pow(speed, Math.Clamp(15f * Math.Max(deltaSeconds, 0f), 0f, 1f))
            };
        }

        _surface12.TonemapSettings = tonemap;
        // Record the RAW timestep for the frame profiler before clamping: the clamp would hide
        // exactly the long-frame outliers a pacing investigation needs to see.
        _lastDeltaSeconds = deltaSeconds;
        // Median-of-3 then clamp — see FrameDeltaFilter for why that order matters and why the
        // first two frames pass through raw. Kept in Core/ so it is unit-testable; this TFM's
        // sources are invisible to the test project.
        deltaSeconds = _frameDeltaFilter.Push(deltaSeconds);
        _windClockSeconds += deltaSeconds; // drives the SpeedTree leaf-wind sway phase

        var controllerStarted = StartProfileTimestamp();
        _controller.Update(deltaSeconds);
        _lastControllerUpdateMilliseconds = ElapsedMilliseconds(controllerStarted);

        try
        {
            RenderFrameD3D12(tonemap);
            _consecutiveRenderFailures = 0;
            // After the frame: the reference renderer's heatmap scan is now current, so the
            // settings-panel key labels can mirror it (CompositionTarget.Rendering runs on the UI
            // thread; the refresh no-ops unless the window actually changed).
            UpdateFormIdHeatmapKey();
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
            try
            {
                _gpu12?.PumpDebugMessages();
            }
            catch (Exception pumpEx)
            {
                Log.Warn("PumpDebugMessages on render-frame failure threw: {0}", pumpEx.Message);
            }

            // If the GPU device was removed (TDR / page fault — the usual "crash with no cause"),
            // attribute it. No-op when the device is fine. Needs FALLOUT_VIEWER_DRED=1 for breadcrumbs.
            try
            {
                _gpu12?.LogDeviceRemovedDiagnostics("render-frame");
            }
            catch (Exception dredEx)
            {
                Log.Warn("LogDeviceRemovedDiagnostics on render-frame failure threw: {0}", dredEx.Message);
            }

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
        VisibilityCylinder? lightVisibility = null, GpuTonemapSettings? tonemapOverride = null,
        Vector4? clipPlane = null, Matrix4x4? lightViewProjection = null,
        int lightViewportWidth = 0, int lightViewportHeight = 0)
    {
        // The top-down overlay drives lighting from the 2D map's own time-of-day, passed via
        // gameHourOverride; the live perspective path uses the 3D control's _gameHour. enableLighting is
        // the per-call gate (still AND-ed with the scene's _showLighting toggle for the live path; the
        // overlay passes its own lighting-enabled flag as enableLighting with _showLighting left true).
        var gameHour = gameHourOverride ?? _gameHour;
        var lightingOn = enableLighting && _showLighting;
        // Advance the scripted day/night reference states to this frame's hour BEFORE the placed
        // -light gather below and the reference cull consume them (cheap no-op within a 3-minute
        // schedule slot). Overridden hours (top-down overlay) legitimately re-evaluate the shared
        // store — capture-equals-live requires the capture's hour to drive the same state machine.
        _dayNightStates.Apply(_data?.DayNightSchedule, gameHour);
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
            cmd, frameIndex, lightVisibility, cameraOrigin, lightingOn,
            lightViewProjection, lightViewportWidth, lightViewportHeight);
        _references?.SetPlacedLightCount(placedLightCount);
        if (_references is { } references)
        {
            var tileTelemetry = _lastPlacedLightTileTelemetry;
            references.SetPlacedLightTileTelemetry(
                tileTelemetry.BuildMilliseconds,
                tileTelemetry.TileCount,
                tileTelemetry.UploadBytes,
                tileTelemetry.AverageLightsPerTile,
                tileTelemetry.MaxLightsPerTile,
                tileTelemetry.EmptyTilePercent,
                tileTelemetry.FallbackReason);
        }
        // Per-game ambient fill scale (uAmbientColor.w): FNV's 0.3 is too dark for the ambient-heavier
        // TES4-era engines, so Oblivion etc. raise it (see GameProfile.AmbientLightScale).
        var ambientScale = BethesdaMultitool.Core.Games.GameProfiles
            .For(_data?.Game ?? BethesdaMultitool.Core.Games.BethesdaGame.Unknown).AmbientLightScale;
        // uCameraOrigin.w = the active imagespace's EmissiveMult (hdrData[3], scene-pass emissive
        // brightness). Keyed off the same per-game resolution the display pass uses so the two stay
        // consistent.
        var tonemap = tonemapOverride ?? ResolveTonemapSettings();
        var hdrActive = HdrGuiActive && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0";
        var game = _data?.Game ?? Core.Games.BethesdaGame.Unknown;
        var sceneSunlightScale = GpuTonemapSettings.ResolveSceneSunlightScale(
            tonemap,
            game,
            hdrActive,
            isInterior: _selectedInterior is not null);
        // Grass takes the IMGS GrassDimmer INSTEAD of the SunlightDimmer, not on top of it.
        var sceneGrassScale = GpuTonemapSettings.ResolveSceneGrassScale(
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
        var shadow = default(Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.ShadowSampleConstants);
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
        // Interiors have no sky, so the sky-gradient REFLECTION stand-in must not run there: indoor
        // water was mirroring a flat grey "sky" instead of its authored DNAM ReflectionColor, which
        // is what retail's RT-free path uses when VarAmounts.y == 0. This lane is read only by the
        // water shaders (water_fnv/fnv001/oblivion/fo4) — the skybox itself is CPU-gated on _showSky
        // and is unaffected.
        var waterSkyReflectionEnabled = _showSky && _selectedInterior is null;
        var constants = AtmosphereConstants.From(
            resolved, game, gameHour, shadingCameraPos, lightingEnabled: lightingOn ? 1f : 0f,
            skyEnabled: waterSkyReflectionEnabled ? 1f : 0f, fogEnabled: enableFog && _showFog ? 1f : 0f,
            placedLightCount: placedLightCount,
            cameraOrigin: cameraOrigin, ambientScale: ambientScale, shadow: shadow,
            emissiveMult: tonemap.EmissiveMult, hdrActive: hdrActive,
            // FO3/FNV ApplyCurrentParameterData exposes hdrData[11] to directional-light setup.
            // Its exact retail consumer is exterior HDR directional light only. It is scene state,
            // so a display-operator override must not suppress it.
            sunlightScale: sceneSunlightScale,
            grassScale: sceneGrassScale);
        // Neutral (0,0,0,1) clips nothing; the water-reflection mirror pass passes its plane here.
        constants.ClipPlane = clipPlane ?? new Vector4(0f, 0f, 0f, 1f);
        var alloc = _ringBuffer12!.Allocate(frameIndex, AtmosphereConstants.ByteSize, GpuRingBuffer12.CbAlignment);
        unsafe
        {
            *(AtmosphereConstants*)alloc.CpuPtr = constants;
        }

        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, alloc.GpuAddress);
        // Stash the bound address so a nested pass (the mirror block) can restore the MAIN b3
        // after rebinding its own — water/blended passes read b3 afterward.
        _lastAtmosphereCbGpuAddress = alloc.GpuAddress;
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
    /// <summary>
    ///     Vertical anchor for the cascade fit: the camera XY with Z clamped into the loaded geometry's
    ///     band. Shared by the capture-arm site and the pass itself so both classify and fit against
    ///     bit-identical inputs.
    /// </summary>
    private Vector3 ResolveShadowAnchor(Vector3 sceneCenter)
    {
        var zExtent = _references?.GetRenderedSceneWorldZExtentCached();
        return zExtent is { } ze
            ? new Vector3(sceneCenter.X, sceneCenter.Y, Math.Clamp(sceneCenter.Z, ze.Min, ze.Max))
            : sceneCenter;
    }

    /// <summary>
    ///     World Z span of the rendered scene — the height a caster can stand above the cascade
    ///     anchor, and therefore how far up-sun the shadow box has to reach
    ///     (<see cref="SunShadowMath.CascadeCasterReach" />). Sampled from the same memoized extent
    ///     the anchor clamp uses, at the same moment, for the same cull-epoch reason. 0 when the
    ///     extent is not yet known, which collapses the reach to the box's own depth.
    /// </summary>
    private float ResolveShadowSceneZSpan()
    {
        var zExtent = _references?.GetRenderedSceneWorldZExtentCached();
        return zExtent is { } ze ? MathF.Max(ze.Max - ze.Min, 0f) : 0f;
    }

    /// <summary>Effective per-cascade radii after the env override clamp (unit-scaled).</summary>
    private float[] ResolveShadowCascadeRadii()
    {
        EnsureShadowLadderScale();
        var lastRadius = MathF.Min(
                             _shadowCascadeRadiiScaled[^1], ShadowRadiusEnvOverride ?? float.MaxValue)
                         + (SunShadowMath.CenterSnap * _unitScale);
        var radii = new float[_shadowCascadeRadiiScaled.Length];
        for (var i = 0; i < radii.Length; i++)
        {
            radii[i] = MathF.Min(_shadowCascadeRadiiScaled[i], lastRadius);
        }

        return radii;
    }

    private void ResetShadowPassTelemetry()
    {
        _lastShadowMode = ShadowPassMode.Skipped;
        _lastShadowCascadeMask = 0;
        _lastShadowDrawCount = 0;
        _lastShadowTerrainCellDraws = 0;
        Array.Clear(_lastShadowReferenceDrawsByCascade);
        Array.Clear(_lastShadowReferenceInstancesByCascade);
        Array.Clear(_lastShadowTerrainCellDrawsByCascade);
    }

    private void RecordSunShadowPass(
        Vortice.Direct3D12.ID3D12GraphicsCommandList cmd, Vector3 renderOrigin, Vector3 sceneCenter)
    {
        ResetShadowPassTelemetry();

        var terrainCasts = _showTerrain && _terrain is not null;
        if (_shadowMap is null || _references is null)
        {
            return;
        }

        var cascadeCount = Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.CascadeCount;
        var referenceVisibility = _references.VisibilityKey;
        var contentKey = new ShadowContentKey(
            _references.BatchContentVersion,
            _terrain?.ContentVersion ?? 0,
            referenceVisibility,
            terrainCasts);
        var hasCapturedCasters = _references.HasShadowDraws || terrainCasts;
        if (!hasCapturedCasters)
        {
            // Preserve published maps across an incidental empty/degenerate frame, as before. An
            // intentional reference- or terrain-visibility transition is different: the previous maps
            // may contain the now-hidden final caster, so run the due cascades once to clear them.
            if (!_shadowMap.HasContent) return;

            var visibilityChanged = false;
            for (var i = 0; i < cascadeCount; i++)
            {
                if (_shadowMap.IsCascadePublished(i) &&
                    (_shadowContentKeys[i].ReferenceVisibility != referenceVisibility ||
                     _shadowContentKeys[i].TerrainVisible != terrainCasts))
                {
                    visibilityChanged = true;
                    break;
                }
            }

            if (!visibilityChanged) return;
        }

        // The anchor resolved at capture-arm time (see the ArmShadowCapture call site). Re-deriving it
        // here could disagree with what the instance sort used.
        var anchor = _shadowFrameAnchor;

        EnsureShadowLadderScale();
        var lastRadius = MathF.Min(
                             _shadowCascadeRadiiScaled[^1], ShadowRadiusEnvOverride ?? float.MaxValue)
                         + (SunShadowMath.CenterSnap * _unitScale);
        var animated = _references.ShadowDrawsIncludeAnimatedLeaves
                       || _references.ShadowDrawsIncludeAnimatedMeshes;

        // === Per-cascade re-render policy ===
        // Each cascade carries its OWN pose key at a drift quantum scaled to its radius, and its own
        // content throttle. Previously one key at a fixed 512-unit quantum governed the whole ladder,
        // so the far cascade — which resolves 64 world units per texel and cannot show a 512-unit
        // shift — re-rendered on the NEAR cascade's schedule. Measured: 33% of frames re-rendered all
        // four cascades at 60.2 ms of GPU against 29.2 ms for a near-cascade-only refresh, and that
        // 31 ms alternation is what a pulsing frame rate feels like.
        var firstPublish = !_shadowMap.HasContent;
        Span<bool> cascadeDue = stackalloc bool[cascadeCount];
        Span<bool> visibilityPending = stackalloc bool[cascadeCount];
        Span<float> cascadeOverdue = stackalloc float[cascadeCount];
        var poseKeys = new SunShadowMath.ShadowKey[cascadeCount];
        var dueCount = 0;
        var anyVisibilityPending = false;
        var referenceExtentIdentityChanged = false;
        for (var i = 0; i < cascadeCount; i++)
        {
            var radius = MathF.Min(_shadowCascadeRadiiScaled[i], lastRadius);
            poseKeys[i] = SunShadowMath.BuildKey(
                _lastResolvedSunDirection, anchor, radius, 0, _shadowCascadeSnapScaled[i]);
            _shadowContentThrottles[i]++;

            var posePending = !_shadowMap.IsCascadePublished(i) || poseKeys[i] != _shadowPoseKeys[i];
            // Coarser cascades also tolerate stale CONTENT for longer: a mesh streaming in 2 s ago is
            // imperceptible at 16-64 units per texel, and the far cascades are the expensive gathers.
            // Visibility is a direct user action and is therefore never held behind that throttle.
            var referenceVisibilityChanged =
                contentKey.ReferenceVisibility != _shadowContentKeys[i].ReferenceVisibility;
            visibilityPending[i] = referenceVisibilityChanged ||
                                   contentKey.TerrainVisible != _shadowContentKeys[i].TerrainVisible;
            anyVisibilityPending |= visibilityPending[i];
            var referenceContentChanged = contentKey.ReferenceBatchContentVersion !=
                                          _shadowContentKeys[i].ReferenceBatchContentVersion;
            referenceExtentIdentityChanged |= referenceVisibilityChanged || referenceContentChanged;
            var residentContentChanged = referenceContentChanged ||
                                         contentKey.TerrainContentVersion !=
                                         _shadowContentKeys[i].TerrainContentVersion;
            var contentPending = visibilityPending[i] ||
                                 (residentContentChanged &&
                                  _shadowContentThrottles[i] >= ShadowContentRerenderFrames << i);
            cascadeDue[i] = posePending || contentPending;
            if (cascadeDue[i])
            {
                dueCount++;
                // Rank by how far past its own quantum the cascade is, so the most stale renders first
                // when the per-frame cap defers the rest.
                if (visibilityPending[i])
                {
                    cascadeOverdue[i] = float.MaxValue;
                }
                else if (posePending)
                {
                    cascadeOverdue[i] =
                        (Vector3.Distance(anchor, _shadowPublishedAnchors[i]) / _shadowCascadeSnapScaled[i]) + 1f;
                }
                else
                {
                    cascadeOverdue[i] =
                        (float)_shadowContentThrottles[i] / (ShadowContentRerenderFrames << i);
                }
            }
        }

        // ArmShadowCapture classified this frame's instances with the PRE-render survivor extent.
        // A reference toggle/load can make Render recull to a different extent; publishing those
        // captured prefixes through a differently-sized frustum would clip a newly shown tall caster.
        // Keep the prior maps for this frame and retry from the now-current extent on the next one.
        if (SunShadowMath.ShouldDeferForReferenceExtentChange(
                referenceExtentIdentityChanged,
                _shadowFrameSceneZSpan,
                ResolveShadowSceneZSpan()))
        {
            _references.DisarmShadowCapture();
            return;
        }

        // Animated foliage/skinning refreshes the near cascades in place every frame; that path needs
        // a prior publish to replay against.
        var animatedRefresh = animated && !firstPublish;
        if (dueCount == 0 && !animatedRefresh)
        {
            _lastShadowMode = ShadowPassMode.Skipped;
            _lastShadowCascadeMask = 0;
            return;
        }

        // Cap ordinary pose/streaming work and defer the rest — an overdue ladder becomes several even
        // frames instead of one spike. First publish and direct visibility actions are exempt: neither
        // may expose a ladder mixing old-visible and new-visible reference/terrain content.
        if (!firstPublish && !anyVisibilityPending && dueCount > ShadowMaxCascadeRendersPerFrame)
        {
            for (var dropped = dueCount - ShadowMaxCascadeRendersPerFrame; dropped > 0; dropped--)
            {
                var leastOverdue = -1;
                for (var i = 0; i < cascadeCount; i++)
                {
                    if (cascadeDue[i] && (leastOverdue < 0 || cascadeOverdue[i] < cascadeOverdue[leastOverdue]))
                    {
                        leastOverdue = i;
                    }
                }

                if (leastOverdue < 0) break;
                cascadeDue[leastOverdue] = false;
            }
        }

        var frustums = new SunShadowMath.LightFrustum[cascadeCount];
        for (var i = 0; i < cascadeCount; i++)
        {
            if (cascadeDue[i])
            {
                var radius = MathF.Min(_shadowCascadeRadiiScaled[i], lastRadius);
                // Same helper, same inputs as the arm-time instance classification
                // (ReferenceRenderer12.ClassifyCascade). Do not inline a different number here.
                frustums[i] = SunShadowMath.BuildLightFrustum(
                    _lastResolvedSunDirection, anchor, renderOrigin, radius, _shadowMap.Resolution,
                    SunShadowMath.CascadeCasterReach(
                        _lastResolvedSunDirection, radius, _shadowFrameSceneZSpan));
            }
            else if (_shadowMap.IsCascadePublished(i))
            {
                // Not re-fitting: replay the PUBLISHED frustum, re-expressed against this frame's
                // render origin with the SAME fold the sampler applies — content and sampling matrices
                // then agree by construction. Rebuilding from the current camera anchor here is wrong:
                // the pose key's quantization means the raw anchor drifts between key changes, and
                // content rendered at the drifted anchor strobes against the stale published matrices
                // (the whole-cell flicker).
                // _shadowPublishedFrustums[i] must stay expressed in _shadowPublishedOrigins[i] — the
                // pair is the cascade's PUBLISHED state. Writing the folded result back would leave the
                // matrix in this frame's origin while the origin field still named the older one, so
                // the next fold would apply the delta twice and compound every time the origin moved.
                var published = _shadowPublishedFrustums[i];
                frustums[i] = published with
                {
                    ViewProj = SunShadowMath.FoldSampleMatrix(
                        published.ViewProj, _shadowPublishedOrigins[i], renderOrigin)
                };
            }
        }

        if (ShadowDiagLogging)
        {
            if (dueCount > 0)
            {
                _shadowAnimatedActive = false;
                Log.Info(
                    "[Shadow] re-fitting cascades: draws={0} due={1} dir=({2:0.00},{3:0.00},{4:0.00}) anchorZ={5:0}",
                    _references.ShadowDrawCount, dueCount,
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

        // A cascade renders when it is due (re-fitted, then published) or when animated content needs
        // its in-place refresh (near cascades only — sway lives in the near field, and the far
        // cascades' full-distance gathers are the expensive part).
        _lastShadowMode = dueCount > 0 ? ShadowPassMode.Full : ShadowPassMode.Animated;
        _lastShadowCascadeMask = 0;
        _lastShadowDrawCount = _references.ShadowDrawCount;
        Span<bool> cascadeHasDraws = stackalloc bool[cascadeCount];
        for (var i = 0; i < cascadeCount; i++)
        {
            var refit = cascadeDue[i];
            var refresh = animatedRefresh && i < ShadowAnimatedCascadeCount &&
                          _shadowMap.IsCascadePublished(i);
            if (!refit && !refresh)
            {
                continue;
            }

            _lastShadowCascadeMask |= 1 << i;
            _gpuTimestampProfiler12?.Write(cmd, GpuTimestampProfiler12.ShadowCascadeStart[i]);
            _shadowMap.BeginCascade(cmd, i);
            var drewCascade = _references.RenderShadowDepth(frustums[i], i);
            var referenceReplayCompleted = _references.LastShadowReplayCompleted;
            _lastShadowReferenceDrawsByCascade[i] = _references.LastShadowSubmittedDrawCount;
            _lastShadowReferenceInstancesByCascade[i] = _references.LastShadowSubmittedInstanceCount;
            _gpuTimestampProfiler12?.Write(cmd, GpuTimestampProfiler12.ShadowCascadeRefs[i]);
            var terrainCellDraws = 0;
            // Meaningful only when terrainCasts — ShouldCommitCascadeState gates it on that flag,
            // exactly as the reference side is gated on its own completion.
            var terrainReplayCompleted = false;
            if (terrainCasts)
            {
                // Same cylinder footprint the main pass streamed, clamped to this cascade's
                // coverage — draws only RESIDENT cells (the designed RenderInternal double-call).
                // An in-place refresh centers it on THIS CASCADE'S published anchor so the whole
                // cleared footprint is redrawn (a current-camera cylinder leaves a trailing-edge
                // strip of terrain depth un-redrawn).
                var terrainCenter = refit ? sceneCenter : _shadowPublishedCylinderCenters[i];
                var terrainRadius = MathF.Min(MathF.Min(_shadowCascadeRadiiScaled[i], lastRadius), _renderDistance);
                terrainCellDraws = _terrain!.RenderShadowDepth(
                    frustums[i].ViewProj, new VisibilityCylinder(terrainCenter, terrainRadius));
                terrainReplayCompleted = _terrain!.LastShadowReplayCompleted;
                _lastShadowTerrainCellDrawsByCascade[i] = terrainCellDraws;
                _lastShadowTerrainCellDraws += terrainCellDraws;
                drewCascade |= terrainCellDraws > 0;
            }

            _shadowMap.EndCascade(cmd, i);
            _gpuTimestampProfiler12?.Write(cmd, GpuTimestampProfiler12.ShadowCascadeEnd[i]);
            cascadeHasDraws[i] = drewCascade;

            if (!refit)
            {
                // In-place refresh: the matrix and descriptor are unchanged, only availability moves.
                _shadowMap.SetCascadeAvailability(i, drewCascade);
                continue;
            }

            // Publish availability even for a degenerate render: the target was just cleared, so
            // retaining an enabled bit would create a fully-lit cascade-shaped hole. Empty maps are
            // disabled and fall through to farther cascades. Whether the POLICY KEYS are also
            // committed is a separate question answered below, and it turns on whether the pass was
            // authoritative — not on whether it drew.
            if (!firstPublish)
            {
                _shadowMap.PublishCascade(i, frustums[i], renderOrigin, drewCascade);
            }

            // The host's published frustum/origin/cylinder MUST track whatever was just published to
            // the map, even for a degenerate render that drew nothing. They are what a later in-place
            // refresh renders content through, while the map samples what PublishCascade recorded — if
            // the two describe different spaces the refreshed content lands somewhere the sampling
            // matrix does not expect, and re-enabling the cascade paints whole regions as shadowed.
            _shadowPublishedFrustums[i] = frustums[i];
            _shadowPublishedOrigins[i] = renderOrigin;
            _shadowPublishedCylinderCenters[i] = sceneCenter;

            // Commit only when BOTH sub-passes were authoritative, and commit whenever they were —
            // including the EMPTY case. Keying this on "something drew" instead left an
            // authoritatively-empty cascade permanently pose-pending, so it re-cleared its target and
            // re-gathered terrain every frame forever. A pass that could not RUN (either side's ring
            // allocation failing) still retains the old keys and retries, which is what
            // TerrainRenderer12's own "the host ... retries" comment always promised but never got.
            if (SunShadowMath.ShouldCommitCascadeState(
                    referenceReplayCompleted, terrainCasts, terrainReplayCompleted))
            {
                _shadowPoseKeys[i] = poseKeys[i];
                _shadowContentKeys[i] = contentKey;
                _shadowContentThrottles[i] = 0;
                _shadowPublishedAnchors[i] = anchor;
            }
        }

        if (firstPublish)
        {
            // Nothing can be sampled until every cascade exists, so the very first render commits the
            // whole ladder at once. Steady state uses the per-cascade path above.
            _shadowMap.Publish(frustums, renderOrigin, cascadeHasDraws);
            // Publish marks EVERY cascade published, including any that drew nothing — so seed the
            // whole frustum/origin pair here. Otherwise a cascade that drew nothing would report
            // published while holding a default (zero) frustum, and a later in-place refresh would
            // render its content through that garbage matrix.
            for (var i = 0; i < cascadeCount; i++)
            {
                _shadowPublishedFrustums[i] = frustums[i];
                _shadowPublishedOrigins[i] = renderOrigin;
            }
        }
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
        EmitCompletedReferencePipelineStatistics();
        _gpuTimestampProfiler12?.BeginFrame(recorder.FrameIndex);
        _gpuReferencePipelineStatistics12?.BeginFrame(recorder.FrameIndex);
        var cmd = recorder.CommandList;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.FrameStart);
        // Deletion queue advances frame counter — releases resources whose hold has elapsed.
        // Must run AFTER BeginFrame's fence wait so the prior GPU work is known complete.
        _deletionQueue12!.Tick();
        // Immediately after the deletion queue, so the usage read is not inflated by resources the
        // fence has already proven dead. Read-only in this phase: it publishes a pressure zone and
        // keeps the OS reservation honest; nothing consumes the zone to evict anything yet.
        _gpu12!.VideoMemory.Tick();
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
        // Camera and visibility cylinder share the same projection inputs so their visible-cell sets agree.
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
            cameraRelative = CameraRelativeSceneRendering;
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
        ResetShadowPassTelemetry();
        var shadowsActive = ShadowsEnvEnabled && _showShadows && _showLighting && !projectionActive &&
                            _selectedInterior is null && _references is not null && _showReferences;
        var shadowRingReserved = false;
        if (shadowsActive)
        {
            shadowRingReserved = _ringBuffer12!.TryReserveTail(ShadowPassRingReservationBytes);
            if (shadowRingReserved)
            {
                _shadowMap ??= new Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12(
                    _gpu12!, _cbvSrvUavHeap12!);
                // Resolve the cascade fit NOW, before the reference pass, and hand it to the capture so
                // the per-cascade instance sort classifies against exactly the anchor the frame-end pass
                // will fit its frustums to. Deriving it again later is not safe: the Z extent is memoized
                // per CULL EPOCH, and that epoch flips inside Render() — so a caster could be sorted into
                // a cascade whose box no longer contains it.
                _shadowFrameAnchor = ResolveShadowAnchor(_camera.Position);
                _shadowFrameSceneZSpan = ResolveShadowSceneZSpan();
                EnsureShadowLadderScale();
                _references!.ArmShadowCapture(
                    MathF.Min(_shadowCasterRingRadiusScaled, _renderDistance),
                    (_shadowFrameAnchor, Vector3.Normalize(_lastResolvedSunDirection),
                        ResolveShadowCascadeRadii(), _shadowCascadeSnapScaled, _shadowFrameSceneZSpan));
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
            HdrGuiActive && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0",
            isInterior: _selectedInterior is not null);
        BindAtmosphereConstants(
            cmd, recorder.FrameIndex, enableFog: !projectionActive, cameraRelative: cameraRelative,
            shadingCameraPosOverride: projectionActive ? orthoEye : null,
            cameraOriginOverride: cameraRelative ? sceneRenderOrigin : null,
            enableShadows: shadowsActive, lightVisibility: cylinder, tonemapOverride: tonemap,
            lightViewProjection: viewProjScene,
            lightViewportWidth: checked((int)surface.Width),
            lightViewportHeight: checked((int)surface.Height));

        // Sky FIRST — gradient + clouds + stars into the cleared color target (depth OFF, so terrain
        // overwrites it via the normal depth pass; OFF ⇒ the flat dark-blue clear shows), then the
        // sun/moon billboards over it. Reads the b3 atmosphere CB bound just above. Sky renders
        // CAMERA-RELATIVE: the translation-free viewProjSky with the dome centered at the origin
        // (domeCenter 0), so far-from-origin camera coords don't jitter the dome. In the ortho
        // projection modes viewProjSky is the synthetic perspective view built above, and the
        // billboard basis comes from the ortho camera (the parked perspective camera is stale).
        if (_showSky)
        {
            _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.SkyStart);
            RenderSky(viewProjSky, Vector3.Zero,
                projectionActive ? ProjectionCameraBasis() : null,
                skyColorScale: sceneSkyScale);
            _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.SkyEnd);
        }

        // The mirror planner reads the current material's FNAM Reflective bit, so Oblivion must
        // select the camera CELL's XCWT before planning (the ordinary water draw refresh is later).
        if (_showWater && _data?.Game == Core.Games.BethesdaGame.Oblivion)
        {
            RefreshWaterAppearanceForCurrentCell();
        }

        // WATER-REFLECTION SCENE MIRROR — planned up front: when the loaded game's water shader
        // has a projective RT arm (Oblivion WATER007), the toggle is on, and a dominant visible
        // water plane exists below the camera, the reflection target gets MIRRORED SCENE content
        // (terrain + captured references + sky) in a dedicated block AFTER the main opaque passes
        // — the reference replay needs this frame's captured draws. The sky-only block below then
        // skips; every other case keeps it.
        var sceneMirrorPlanned = false;
        var mirrorPlaneHeight = 0f;
        if (_showSky && WaterReflectionActive && !projectionActive && _showWater &&
            _data?.Game == Core.Games.BethesdaGame.Oblivion &&
            _water is not null && _water.AllowsProjectiveSceneReflection &&
            _references is not null && _selectedInterior is null &&
            _water.TryGetDominantVisibleWaterPlaneHeight(
                cylinder, _camera.Position.Z, out mirrorPlaneHeight) &&
            TryEnsureWaterReflectionTarget((int)surface.Width, (int)surface.Height, sceneContent: true))
        {
            sceneMirrorPlanned = true;
            _references.ArmMirrorCapture();
        }

        // PLANAR SKY REFLECTION — the sky again, with the view's Z axis mirrored, into a small
        // offscreen target the water shaders sample. Retail WATER000 reflects a planar RT; our
        // 2-row gradient stand-in could not carry cloud mottling, warm tint or the sun glitter
        // band, which is why water read flat. Under the mirror the pixel at a screen position
        // holds the sky along reflect(eyeDir, up), so water samples it by its own screen UV — and
        // because the dome is at infinity this is independent of every water plane's height.
        //
        // Runs here, immediately after the main sky: the b3 atmosphere CB the gradient reads is
        // already bound, and nothing has written the scene depth yet that we would have to preserve.
        var waterReflectionBound = false;
        if (!sceneMirrorPlanned &&
            _showSky && WaterReflectionActive && !projectionActive && _water is not null &&
            TryEnsureWaterReflectionTarget((int)surface.Width, (int)surface.Height) &&
            _waterReflectionTarget is { } reflectionTarget)
        {
            try
            {
                // Return LAST frame's snapshot to its copy/resolve baseline FIRST. Without this the
                // reflection froze on frame 1: TryPrepareWaterOpaqueSnapshot early-returns while a
                // snapshot is still marked prepared, so every later frame silently kept sampling
                // the first frame's mirrored sky — a reflection that never followed the camera.
                // Restoring here (rather than after the water pass) is self-healing: it also
                // recovers from any frame that threw before its restore could run.
                reflectionTarget.RestoreWaterOpaqueSnapshot(cmd);
                // Clear to the HORIZON colour, not black. The mirrored dome covers only the
                // lower half of the reflected frame (the real upper hemisphere maps below the
                // mirrored horizon), so any lookup the ripple distortion pushes across that
                // line — grazing/near-horizon water especially — sampled an opaque-black clear
                // and read as a dark fringe. The horizon tint degrades to the old 2-row
                // stand-in's value instead, which is what those rays reflected before the RT.
                var reflectionClear = ResolveSceneAtmosphere(_gameHour, _showLighting).SkyHorizonColor
                                      * sceneSkyScale;
                reflectionTarget.Bind(cmd, new Vortice.Mathematics.Color4(
                    reflectionClear.X, reflectionClear.Y, reflectionClear.Z, 1f));
                // Mirror about the camera's horizontal plane. The sky is drawn camera-relative
                // (dome at the origin, translation-free viewProj), so a plain Z flip IS the
                // reflection — no plane height enters, and the dome's CullMode.None makes the
                // reversed winding a non-issue.
                RenderSky(Matrix4x4.CreateScale(1f, 1f, -1f) * viewProjSky, Vector3.Zero,
                    skyColorScale: sceneSkyScale,
                    // The cloud scroll integrates once per Render call. This is the frame's SECOND
                    // sky draw, so it must not advance it again — that would drift the clouds at 2x
                    // and put the reflected clouds somewhere the sky's clouds are not.
                    advanceCloudScroll: false);
                // Bind only when THIS frame's copy actually recorded: a failed prepare would
                // otherwise hand water a stale (or never-written) image.
                waterReflectionBound = reflectionTarget.TryPrepareWaterOpaqueSnapshot(cmd);
            }
            catch (Exception ex)
            {
                Log.Warn("WorldView3DControl: water reflection pass failed; " +
                         "falling back to the sky gradient: {0}", ex.Message);
            }
            finally
            {
                // Restore the scene targets + viewport/scissor the reflection target overwrote.
                cmd.OMSetRenderTargets(sceneRtv, sceneDsv);
                cmd.RSSetViewport(0, 0, surface.Width, surface.Height);
                cmd.RSSetScissorRect((int)surface.Width, (int)surface.Height);
            }
        }

        // SetWaterReflection moves to AFTER the scene-mirror block below: when the mirror runs,
        // the binding must carry its matrix; when it does not, the sky-only binding made here
        // would be identical anyway.

        // Layer order is terrain → references → water → wireframe. Water is
        // alpha-blended depth-read, so it must come after terrain + references (which write
        // the depth that water samples). The cell-grid wireframe follows water but remains depth-tested;
        // the post-tonemap diagnostics are recorded later in their separate LDR block.
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.TerrainStart);
        var visibleTerrain = _showTerrain
            ? _terrain?.Render(viewProjScene, cylinder, sceneRenderOrigin) ?? 0
            : 0;
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
        Core.Formats.Nif.Rendering.D3D12.ReferenceRenderer12.CullCameraPose? cullPose =
            projectionActive || !TolerantCullEnabled
                ? null
                : new Core.Formats.Nif.Rendering.D3D12.ReferenceRenderer12.CullCameraPose(
                    _camera.Forward, _camera.FovYRadians, aspect);
        // Unified transparency stream (engine parity): decided per FRAME before the reference
        // render, because the blended ROUTING inside it depends on this (depth-writing blends fold
        // into the single sorted stream instead of the hoisted pre-water list). Non-FNV games and
        // frames without visible cell water keep the legacy split pass order.
        var streamTransparency =
            Core.Formats.Nif.Rendering.D3D12.ReferenceRenderer12.UnifiedTransparencyEnabled &&
            !projectionActive && _showWater && _showReferences &&
            _water is not null && _references is not null &&
            _water.CanStreamTransparency(cylinder);
        _references?.SetTransparencyStreamActive(streamTransparency);

        // Re-assert this view's own category filter. The 2D map's top-down overlay borrows the shared
        // reference renderer and applies the map legend's set for its pass; it deliberately does NOT
        // restore ours afterwards, because restoring per pass flipped the filter twice per overlay
        // render and invalidated the cull cache every time (measured: cullVeto=Invalidated on 205/205
        // WastelandNV zoom-to-fit passes — the two sets genuinely differ, this view hides Sky and the
        // map hides nothing). Re-asserting here instead makes the live view authoritative at the only
        // point it matters. Free when unchanged: SetHiddenCategories is change-detecting.
        _references?.SetHiddenCategories(_hiddenCategories);

        var visibleReferences = 0;
        if (_gpuReferencePipelineStatistics12 is { } pipelineStatistics)
        {
            pipelineStatistics.BeginReferencePass(cmd);
            try
            {
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
            }
            finally
            {
                pipelineStatistics.EndReferencePass(cmd);
            }
        }
        else if (_showReferences)
        {
            // Keep the default-off path free of query commands and the enabled path's
            // try/finally region. The only residual CPU cost is this one predictable null branch.
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
        // water shader instead of the flat slab. Cheap reference assignment; the stable list updates
        // as meshes stream in and as their owning references enter or leave non-spatial visibility.
        if (_water is not null && _references is not null)
        {
            _water.SetNifWaterPlanes(_references.NifWaterPlanes);
        }

        // ── WATER-REFLECTION SCENE MIRROR ────────────────────────────────────────────────────
        // Same frame, after the main opaque passes: the reference replay re-issues this frame's
        // captured draws (their per-draw CB/t8 ring addresses are frame-local), so one-frame
        // latency is impossible. Sequence: restore snapshot → bind + horizon clear (depth 0) →
        // mirror b3 (mirrored shading camera + clip plane) → mirrored sky → terrain mirror →
        // reference replay → prepare snapshot; the finally restores scene targets, viewport, and
        // the MAIN b3 (water/blended read it next). Failures fall back to the gradient stand-in.
        Matrix4x4? sceneMirrorMatrix = null;
        if (sceneMirrorPlanned && _waterReflectionTarget is { } sceneMirrorTarget)
        {
            var mainAtmosphereCb = _lastAtmosphereCbGpuAddress;
            var mainPointLightTiles = _lastPointLightTilesGpuAddress;
            try
            {
                // Reflection about the dominant plane in ORIGIN-RELATIVE space (the VS transforms
                // p − origin): z = h′ plane, System.Numerics row-vector composition — reflect,
                // then view-project.
                var mirrorPlaneRel = mirrorPlaneHeight - sceneRenderOrigin.Z;
                var mirrorViewProjScene =
                    Matrix4x4.CreateReflection(new System.Numerics.Plane(0f, 0f, 1f, -mirrorPlaneRel))
                    * viewProjScene;

                sceneMirrorTarget.RestoreWaterOpaqueSnapshot(cmd);
                var mirrorClear = ResolveSceneAtmosphere(_gameHour, _showLighting).SkyHorizonColor
                                  * sceneSkyScale;
                sceneMirrorTarget.Bind(cmd, new Vortice.Mathematics.Color4(
                    mirrorClear.X, mirrorClear.Y, mirrorClear.Z, 1f));
                // Mirror b3: the shading camera reflects too (specular/fog track the mirrored
                // eye), and the clip plane trims everything below the water surface out of the
                // mirror. Same fog/shadow/tonemap state as the main pass.
                var mirroredCameraAbs = new Vector3(
                    _camera.Position.X, _camera.Position.Y,
                    (2f * mirrorPlaneHeight) - _camera.Position.Z);
                BindAtmosphereConstants(
                    cmd, recorder.FrameIndex, enableFog: true, cameraRelative: cameraRelative,
                    shadingCameraPosOverride: cameraRelative
                        ? mirroredCameraAbs - sceneRenderOrigin
                        : mirroredCameraAbs,
                    cameraOriginOverride: cameraRelative ? sceneRenderOrigin : null,
                    enableShadows: shadowsActive, lightVisibility: cylinder,
                    tonemapOverride: tonemap,
                    clipPlane: new Vector4(0f, 0f, 1f, -mirrorPlaneRel),
                    lightViewProjection: mirrorViewProjScene,
                    lightViewportWidth: sceneMirrorTarget.Width,
                    lightViewportHeight: sceneMirrorTarget.Height);
                // Sky first (depth-off background): the dome is at infinity, so the camera-plane
                // Z flip is the same reflection as the water plane's — directions are unaffected
                // by the translation difference. Cloud scroll must not advance twice per frame.
                RenderSky(Matrix4x4.CreateScale(1f, 1f, -1f) * viewProjSky, Vector3.Zero,
                    skyColorScale: sceneSkyScale, advanceCloudScroll: false);
                if (_showTerrain)
                {
                    _terrain?.RenderMirror(mirrorViewProjScene, cylinder);
                }

                _references?.RenderMirrorColor(mirrorViewProjScene);
                waterReflectionBound = sceneMirrorTarget.TryPrepareWaterOpaqueSnapshot(cmd);
                if (waterReflectionBound)
                {
                    sceneMirrorMatrix = mirrorViewProjScene;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("WorldView3DControl: water scene-mirror pass failed; " +
                         "falling back to the sky gradient: {0}", ex.Message);
            }
            finally
            {
                _references?.DisarmMirrorCapture();
                cmd.OMSetRenderTargets(sceneRtv, sceneDsv);
                cmd.RSSetViewport(0, 0, surface.Width, surface.Height);
                cmd.RSSetScissorRect((int)surface.Width, (int)surface.Height);
                // Water/blended passes read b3 next — the mirror's CB must not leak forward.
                cmd.SetGraphicsRootConstantBufferView(
                    GpuRootSignature12.Slots.AtmosphereCbv, mainAtmosphereCb);
                cmd.SetGraphicsRootShaderResourceView(
                    (uint)GpuRootSignature12.Slots.PointLightTilesSrv, mainPointLightTiles);
            }
        }

        _water?.SetWaterReflection(
            waterReflectionBound ? _waterReflectionSrv!.Value.BindlessIndex : null,
            surface.Width,
            surface.Height,
            sceneMirrorMatrix);

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
        var waterDepthSampled = false;
        var sceneDepthSampled = waterUsesDepth || referencesUseDepth;
        var sampledDepthState = Vortice.Direct3D12.ResourceStates.DepthRead |
                                Vortice.Direct3D12.ResourceStates.PixelShaderResource;
        // Water projects CAMERA-RELATIVE like terrain/references (same viewProjScene + render origin, VS
        // subtracts) — the absolute path lost float32 precision far from the origin and the water edge
        // shimmered/ended at view-angle-dependent Z as the camera rotated. Ripple UVs + the PS view vector
        // stay world-anchored: vWorldPos is still the absolute position (see water.vert.hlsl).
        var visibleWater = 0;
        if (streamTransparency)
        {
            // ── UNIFIED TRANSPARENCY STREAM ─────────────────────────────────────────────────────
            // One engine-style back-to-front pass: blended references and FNV water batches merge
            // by the shared clip-w key. The live DSV stays WRITABLE throughout (depth-writing
            // blends need it); water/soft-particles sample the POST-OPAQUE depth snapshot instead
            // of the read-only-DSV dance — the Xenon-resolve-equivalent.
            var opaqueDepthSnapshotPrepared = false;
            try
            {
                RefreshWaterAppearanceForCurrentCell();
                var streamDepthReady = waterUsesDepth &&
                                       surface.TryEnsureOpaqueDepthSnapshotResource() &&
                                       TryEnsureOpaqueDepthSnapshotSrv() &&
                                       surface.TryPrepareOpaqueDepthSnapshot(cmd);
                opaqueDepthSnapshotPrepared = streamDepthReady;
                var streamDepthIndex = streamDepthReady
                    ? _opaqueDepthSnapshotSrv!.Value.BindlessIndex
                    : NoDepthSrv;
                _water!.SetSceneDepth(
                    streamDepthIndex, _camera.NearPlane, _camera.FarPlane, 1);
                _references!.SetSceneDepth(
                    referencesUseDepth && streamDepthReady ? streamDepthIndex : NoDepthSrv,
                    _camera.NearPlane, _camera.FarPlane, 1);

                var fnvWater001Preflight = _water.GetFnvWater001Preflight(
                    cylinder, isPerspectiveProjection: true);
                if (fnvWater001Preflight.Candidate &&
                    TryEnsureWaterOpaqueSnapshotSrv() &&
                    surface.TryPrepareWaterOpaqueSnapshot(cmd))
                {
                    fnvWater001SnapshotPrepared = true;
                    _water.SetFnvWater001Snapshot(
                        _waterOpaqueSnapshotSrv!.Value.BindlessIndex,
                        surface.Width,
                        surface.Height);
                }
                else
                {
                    _water.SetFnvWater001Snapshot(null, 0, 0);
                }

                // The stream is the water reservation's consumer, but it must NOT be released
                // yet: the reference renderer's blended capacity plan (inside
                // RenderBlendedDeferredUnified) reserves a CB for every surviving blended draw up
                // front, and with the tail already free it would spend the water budget on far
                // blended draws — dense frames then drop water wholesale, the exact 07-24
                // ring-starvation flicker the reservation exists to prevent. Hand the release to
                // the water renderer instead: it opens the tail lazily, after the blended plan
                // has allocated and immediately before its first batch draws.
                if (waterRingReserved)
                {
                    _water.DeferStreamTailReservationRelease(WaterPassRingReservationBytes);
#pragma warning disable S1854 // release-once latch: kept accurate so a later-added consumer cannot double-release the ring reservation
                    waterRingReserved = false;
#pragma warning restore S1854
                }

                // Submerged translucent geometry is drawn BEFORE the stream opens, exactly as the
                // legacy split order does. Water is queued per cell, so its sort key is a
                // 4096-unit quad's centroid: a decal in the near half of its own cell sorts nearer
                // than the surface above it and the global depth merge composites it ON TOP. Being
                // under the surface is a classification, not a distance — it cannot be expressed as
                // a sort key, so it stays a partition and the stream takes the complement.
                var streamWaterProbe = _showWater && _references is not null &&
                                       _water.HasVisibleWaterToPartition(cylinder)
                    ? _water
                    : null;
                if (streamWaterProbe is not null)
                {
                    _references!.RenderBlendedDeferredBelowWater(
                        streamWaterProbe, _camera.Position.Z);
                    waterTransparencyPartitioned = true;
                }

                visibleWater = _water.PrepareTransparencyStream(
                    viewProjScene, cylinder, referenceRenderOrigin, isPerspectiveProjection: true);
                _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.BlendedStart);
                if (_waterStreamInterleave?.Water != _water)
                {
                    _waterStreamInterleave = new WaterStreamInterleave(_water);
                }

                // Null-conditional to match every other _references use in this file: the reference
                // pipeline can fail to initialize and the view stays up reporting PLACED OBJECTS
                // UNAVAILABLE, so this path is reachable with no reference renderer. Water still
                // drains and finishes below, which is the correct degradation — the water stream
                // renders, only the interleaved reference draws are missing.
                _references?.RenderBlendedDeferredUnified(
                    _waterStreamInterleave, streamWaterProbe, _camera.Position.Z,
                    _water.StreamMaxQueuedSurfaceHeight);
                _water.DrainAllTransparency();
                // Blended draws no queued surface can occlude (bounds bottom + camera above the
                // frame's highest queued surface) issue AFTER the final drain: water writes no
                // depth, so the camera-cell quad — whose CENTROID sort key is nearer than almost
                // everything — would otherwise composite over smoke standing well above it. Same
                // plane value as the unified call, so the partitions stay complementary.
                if (streamWaterProbe is not null)
                {
                    _references?.RenderBlendedDeferredAboveAllWater(
                        streamWaterProbe, _camera.Position.Z, _water.StreamMaxQueuedSurfaceHeight);
                }

                _water.FinishTransparencyStream();
                _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.BlendedEnd);

                _water.SetFnvWater001Snapshot(null, 0, 0);
                if (fnvWater001SnapshotPrepared)
                {
                    surface.RestoreWaterOpaqueSnapshot(cmd);
                    fnvWater001SnapshotPrepared = false;
                }

                if (opaqueDepthSnapshotPrepared)
                {
                    surface.RestoreOpaqueDepthSnapshot(cmd);
                    opaqueDepthSnapshotPrepared = false;
                }
            }
            catch
            {
                try
                {
                    // Forget queued batches (a later frame must not drain them against a dead
                    // command list) and free the deferred tail reservation within the frame.
                    _water?.AbandonTransparencyStream();
                    _water?.SetFnvWater001Snapshot(null, 0, 0);
                    if (fnvWater001SnapshotPrepared)
                    {
                        surface.RestoreWaterOpaqueSnapshot(cmd);
                    }

                    if (opaqueDepthSnapshotPrepared)
                    {
                        surface.RestoreOpaqueDepthSnapshot(cmd);
                    }

                    // Submit only the valid partial scene recorded before the failure (see the
                    // legacy branch's identical rationale). Depth never left DEPTH_WRITE here.
                    recorder.EndFrame();
                }
                catch
                {
                    surface.DiscardWaterOpaqueSnapshotPreparation();
                    surface.DiscardOpaqueDepthSnapshotPreparation();
                    try
                    {
                        recorder.AbortFrame();
                    }
                    catch
                    {
                        /* The outer render failure path logs device/recorder failures. */
                    }
                }

                throw;
            }
        }
        else
        {
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
                    // NOTE: the water pass's protected ring reservation is deliberately still held here
                    // and released only after this split (below). Releasing it first — so the split could
                    // not truncate a submerged draw — let the below-water leg consume the water pass's
                    // budget on dense frames, and water dropped out of individual frames: the ring-
                    // starvation flicker this reservation exists to prevent. Losing the far tail of the
                    // submerged partition is the far cheaper failure, and it degrades gracefully because
                    // ReserveNearestBlendedDrawConstants keeps the draws NEAREST the camera.
                    // Split translucent references around the water surface BEFORE the WATER001 snapshot
                    // decision — the two are independent concerns. Water draws with DepthWriteMask.Zero,
                    // so anything issued after it composites on top no matter where it sits in the world;
                    // ordering is the only lever, and submerged decals must therefore be issued first.
                    // Game-agnostic: every title draws water without depth writes, and the classification
                    // is now data-driven (per-XY authored water height) with no per-game constants.
                    if (_showReferences && _references is not null &&
                        _water.HasVisibleWaterToPartition(cylinder))
                    {
                        _references.RenderBlendedDeferredBelowWater(_water, _camera.Position.Z);
                        // The complementary at/above-water partition draws after the surface, so the
                        // split is self-contained: a failed WATER001 snapshot must NOT fall back to
                        // redrawing everything post-water — that would double-blend the submerged half
                        // and restore the bug.
                        waterTransparencyPartitioned = true;
                    }

                    var fnvWater001Preflight = _water.GetFnvWater001Preflight(
                        cylinder,
                        isPerspectiveProjection: !projectionActive);
                    if (fnvWater001Preflight.Candidate &&
                        TryEnsureWaterOpaqueSnapshotSrv() &&
                        surface.TryPrepareWaterOpaqueSnapshot(cmd))
                    {
                        fnvWater001SnapshotPrepared = true;
                        _water.SetFnvWater001Snapshot(
                            _waterOpaqueSnapshotSrv!.Value.BindlessIndex,
                            surface.Width,
                            surface.Height);
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
#pragma warning disable S1854 // release-once latch: kept accurate so a later-added consumer cannot double-release the ring reservation
                    waterRingReserved = false;
#pragma warning restore S1854
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
                    try
                    {
                        recorder.AbortFrame();
                    }
                    catch
                    {
                        /* The outer render failure path logs device/recorder failures. */
                    }
                }

                throw;
            }
        }

        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WaterEnd);
        var waterMs = ElapsedMilliseconds(segmentStarted);
        // Legacy split order only: blended references draw AFTER water under the read-only DSV.
        // The unified stream drew everything above with the live DSV writable throughout.
        if (!streamTransparency)
        {
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
                _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.BlendedStart);
                if (waterTransparencyPartitioned && _water is not null)
                {
                    _references?.RenderBlendedDeferredAtOrAboveWater(_water, _camera.Position.Z);
                }
                else
                {
                    _references?.RenderBlendedDeferred();
                }

                _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.BlendedEnd);
            }

            if (sceneDepthSampled)
            {
                cmd.OMSetRenderTargets(sceneRtv); // release read-only DSV before returning to DepthWrite
                cmd.ResourceBarrierTransition(depthRes!,
                    sampledDepthState,
                    Vortice.Direct3D12.ResourceStates.DepthWrite);
                cmd.OMSetRenderTargets(sceneRtv, sceneDsv);
            }
        }

        // Cell-grid wireframe stays in the HDR scene pass: unlike the depth-disabled diagnostics it
        // deliberately depth-TESTS against the scene so terrain occludes the grid cage ("part of
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
        var shadowMs = 0.0;
        if (shadowsActive)
        {
            _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ShadowStart);
            // All preceding allocations honored the protected tail. Release it only when the
            // shadow pass is ready to consume it; no screen-space renderer allocates in between.
            if (shadowRingReserved)
            {
                _ringBuffer12!.ReleaseTailReservation();
            }

            segmentStarted = StartProfileTimestamp();
            RecordSunShadowPass(cmd, referenceRenderOrigin, _camera.Position);
            shadowMs = ElapsedMilliseconds(segmentStarted);
            _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ShadowEnd);
        }

        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ResolveStart);
        // Tonemap the HDR scene color into the back buffer (resolving MSAA first when active). The
        // back buffer deliberately stays in RENDER_TARGET for LDR editor diagnostics: overlays like
        // the navmesh, selection outline, and collision cages must not feed eye adaptation or bloom
        // (nor be tonemapped/eye-adapted themselves — a debug overlay's brightness must not track
        // scene exposure). Collision additionally draws camera-relative because its large absolute
        // coordinates wobbled against camera-relative reference geometry in the HDR frame.
        surface.ResolveTo(cmd, backBuffer);

        // A retained selection is allowed while Meshes is hidden, but its reference-derived outline
        // follows the live reference layer. Export framing is independent, so include it explicitly
        // instead of relying on the selection renderer to keep the shared LDR overlay block active.
        // The retained selection follows the same non-spatial eligibility as its reference pixels:
        // global/category/authored decisions remain independent of per-reference previews, but every
        // one suppresses editor chrome when it suppresses the selected object.
        var wantsSelectionOverlay = _selectionHighlight is not null && IsSelectedReferenceVisible();
        var wantsCollisionOverlay = _showReferences && _showCollision && _collisionDebug is not null;
        var exportFramingOverlay = ExportFramingVisible ? _exportFraming : null;
        var visibleNavMesh = 0;
        if ((_showNavMesh && _navMesh is not null) || wantsCollisionOverlay ||
            exportFramingOverlay is not null || wantsSelectionOverlay)
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

            if (wantsCollisionOverlay && _collisionDebug is not null)
            {
                _collisionDebug.Render(
                    viewProjScene, cylinder, surface.Width, surface.Height,
                    sceneRenderOrigin, ldrTarget: true,
                    compositionScaleX: RenderPanel.CompositionScaleX,
                    compositionScaleY: RenderPanel.CompositionScaleY,
                    showReferences: _showReferences,
                    hiddenCategories: _hiddenCategories);
            }

            // Export framing preview (Export tab + "Show framing"): the captured world AABB + view
            // gizmo, in absolute world coords so it uses the absolute view-proj (like navmesh/selection)
            // — no camera-relative origin math for a static box.
            if (exportFramingOverlay is not null)
            {
                EnsureExportFramingLines();
                if (_exportFramingLines is { Length: >= 2 } framing)
                {
                    exportFramingOverlay.Render(
                        viewProjAbsolute, framing, ExportFramingColor,
                        surface.Width, surface.Height, ldrTarget: true,
                        compositionScaleX: RenderPanel.CompositionScaleX,
                        compositionScaleY: RenderPanel.CompositionScaleY);
                }
            }

            // Selection outline, drawn last + depth-disabled so it stays visible on top of
            // everything (no-op when nothing is selected). The Meshes parent gate is folded into
            // wantsSelectionOverlay above; the selected reference itself remains latent.
            if (wantsSelectionOverlay)
            {
                _selectionHighlight!.Render(viewProjAbsolute, ldrTarget: true);
            }
        }

        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ResolveEnd);

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
        _gpuReferencePipelineStatistics12?.ResolveActiveFrame(cmd);
        recorder.EndFrame();
        _gpuTimestampProfiler12?.MarkActiveFrameSubmitted(frameNumber, recorder.LastSubmittedFenceValue);
        _gpuReferencePipelineStatistics12?.MarkActiveFrameSubmitted(
            frameNumber,
            recorder.LastSubmittedFenceValue);
        var endFrameMs = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartProfileTimestamp();
        surface.Present();
        var presentMs = ElapsedMilliseconds(segmentStarted);

        var renderBodyMs = ElapsedMilliseconds(frameStarted);
        // WaitForFrameSlot runs before RenderFrameD3D12 (and therefore before frameStarted), so
        // renderBodyMs is useful stage diagnostics but is not a throughput denominator. Measure
        // completion-to-completion here: unlike the camera's post-wait start interval, this window
        // contains THIS frame's wait/controller/body and therefore attributes a hitch to the row
        // whose stages caused it.
        var frameCompletedTimestamp = Stopwatch.GetTimestamp();
        var wallFrameMs = Stopwatch.GetElapsedTime(
            _lastFrameCompletionTimestamp, frameCompletedTimestamp).TotalMilliseconds;
        _lastFrameCompletionTimestamp = frameCompletedTimestamp;
        if (!double.IsFinite(wallFrameMs) || wallFrameMs <= 0)
        {
            // Defensive first-frame fallback. LoadData/surface creation normally seed the
            // completion clock immediately before rendering resumes.
            wallFrameMs = renderBodyMs + recorder.LastFrameFenceWaitMilliseconds +
                          _lastControllerUpdateMilliseconds;
        }
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
            TotalMilliseconds: wallFrameMs,
            RenderBodyMilliseconds: renderBodyMs,
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
            ShadowMilliseconds: shadowMs,
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
            AllocatedBytes: allocatedBytes,
            DeltaSeconds: _lastDeltaSeconds);

        // Feed the pacing window unconditionally — the HUD reads it whether or not profile logging
        // is on, and it is the only place total frame time reaches the screen.
        _performanceSampler.Add(sample.TotalMilliseconds);

        if (_profileLogging)
        {
            MaybeLogProfile(sample, terrainStats, waterStats, wireframeStats, referenceStats);
        }

        if (IsProfileWindowFrame(sample.FrameNumber) &&
            _stallThresholdMilliseconds > 0 &&
            sample.TotalMilliseconds >= _stallThresholdMilliseconds)
        {
            EmitFrameStall(sample, terrainStats, waterStats, wireframeStats, referenceStats);
            if (_gpuTimestampProfiler12 is null && !_gpuTimestampsAutoEnabled)
            {
                _gpuTimestampsAutoEnabled = true;
                EnableGpuTimestamps("auto-stall");
            }
        }
    }

    /// <summary>
    ///     Exact stationary-shadow content identity. Reference and terrain visibility are separate
    ///     from resident content versions so user-facing filter changes can refresh immediately while
    ///     mesh streaming remains subject to the ordinary content throttle.
    /// </summary>
    private readonly record struct ShadowContentKey(
        int ReferenceBatchContentVersion,
        int TerrainContentVersion,
        ReferenceVisibilityKey ReferenceVisibility,
        bool TerrainVisible);

    /// <summary>Which path <see cref="RecordSunShadowPass" /> took on the most recent frame.</summary>
    internal enum ShadowPassMode
    {
        /// <summary>Cache hit — no cascade re-rendered.</summary>
        Skipped = 0,

        /// <summary>Wind/skinning refresh of the near cascades only.</summary>
        Animated = 1,

        /// <summary>Pose/content-driven refit path. The cascade mask says which subset rendered.</summary>
        Full = 2
    }

    /// <summary>
    ///     Adapter handing the reference renderer's blended loop the water drain (the unified
    ///     transparency stream's merge hook). Cached per water-renderer instance.
    /// </summary>
    private sealed class WaterStreamInterleave(
        Core.Formats.Nif.Rendering.D3D12.WaterRenderer12 water)
        : Core.Formats.Nif.Rendering.D3D12.ITransparencyInterleave
    {
        public Core.Formats.Nif.Rendering.D3D12.WaterRenderer12 Water { get; } = water;

        public bool DrainDownTo(float depth) => Water.DrainTransparencyDownTo(depth);
    }

    // CPU mirror of the b3 `cbuffer Atmosphere` the lighting/sky/water shaders declare. 10 float4
    // = 160 bytes before the shadow constants (CB-aligned by the ring buffer). When a flag is 0 the shader falls back to its
    // pre-atmosphere behavior, so a disabled toggle keeps that aspect of the scene looking flat.
    // (Shaders that don't apply fog — e.g. skybox — declare only the leading float4s they use, which
    // is layout-safe since later fields are appended at the end.)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct AtmosphereConstants
    {
        public Vector4 SunDirIntensity; // xyz = sun world dir (toward sun), w = intensity
        public Vector4 SunColorLighting; // rgb = sun color, w = lightingEnabled (0/1)
        public Vector4 AmbientColor; // rgb = ambient, w = spare
        public Vector4 SkyTopSkyEnabled; // rgb = sky-top color, w = skyEnabled (0/1)
        public Vector4 SkyHorizon; // rgb = sky-horizon color, w = effective HDR active (0/1)
        public Vector4 FogColorFogEnabled; // rgb = fog color, w = fogEnabled (0/1)
        public Vector4 Params; // x = gameHour, y = fogNear, z = fogFar, w = placed-light count
        public Vector4 CameraPosFogPower; // xyz = camera world pos (0 in camera-relative mode), w = fog power

        public Vector4 FogFarColorMax; // rgb = far-fog color, w = maximum powered fog amount

        // Camera-relative render origin the scene VS subtract from each world vertex before
        // projection (0 when camera-relative is off). The PS shaders that read this CB
        // declare only the prefix they use, so fields appended after their prefix are layout-safe.
        public Vector4 CameraOrigin; // xyz = render origin (= camera world pos),

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

        /// <summary>
        ///     Classic FO3/FNV grass sun term: rgb = the sun colour WITHOUT the imagespace
        ///     SunlightDimmer (retail's TallGrassShader copies NiLight m_kDiffuseColor raw; only
        ///     BSShaderLightingProperty applies the dimmer, i.e. to statics/actors/LAND), w = the
        ///     imagespace GrassDimmer the engine writes into the grass VS AddlParams.x. w == 0 marks
        ///     the lane unset so consumers fall back to today's behaviour.
        /// </summary>
        public Vector4 GrassSunColorScale;

        /// <summary>
        ///     Half-space clip plane in origin-relative <c>vWorldPos</c> space, consumed by the
        ///     terrain + reference PS entry (<c>clip(dot(p, xyz) + w)</c>). The neutral default
        ///     (0,0,0,1) clips nothing; the water-reflection mirror pass binds (0,0,1,−h′) so
        ///     below-plane geometry never enters the mirror.
        /// </summary>
        public Vector4 ClipPlane;

        public const uint ByteSize = 10 * 16 + 4 * 64 + 4 * 16 + 6 * 16 + 16 + 16;

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
            Core.Formats.Nif.Rendering.D3D12.ShadowMapRenderer12.ShadowSampleConstants shadow = default,
            float emissiveMult = 1f,
            bool hdrActive = true,
            float sunlightScale = 1f,
            float grassScale = 1f)
        {
            var directionalAmbient = AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
                game, AuthoredSkyArchitecture.ExplicitOverride, a.DirectionalAmbient);

            return new()
            {
                SunDirIntensity = new Vector4(a.SunWorldDirection, a.SunIntensity),
                SunColorLighting = new Vector4(a.SunColor * sunlightScale, lightingEnabled),
                // Grass takes the sun colour RAW plus its own dimmer — see the field doc above.
                GrassSunColorScale = new Vector4(a.SunColor, grassScale),
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
                    : Vector4.Zero
            };
        }
    }
}
