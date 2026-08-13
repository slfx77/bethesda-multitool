using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaRendererProfiler;

internal sealed record RendererProfilerOptions
{
    internal const string DefaultStressScene = "WastelandNVHeavy";

    internal static RendererProfilerOptions Default { get; } = CreateDefault();

    internal string? InputPath { get; init; }
    internal string? DataDirectory { get; init; }
    internal IReadOnlyList<string> LoadOrderPaths { get; init; } = [];
    internal string ProfileOutputPath { get; init; } = CreateDefaultProfileOutputPath();

    internal string ProfileJsonlOutputPath { get; init; } =
        CreateDefaultProfileJsonlOutputPath(CreateDefaultProfileOutputPath());

    internal int ProfileIntervalMilliseconds { get; init; } = 2000;
    internal int? DurationSeconds { get; init; }
    internal string? StressScene { get; init; } = DefaultStressScene;
    internal RendererCameraMotionKind CameraMotion { get; init; } = RendererCameraMotionKind.Static;
    internal float CameraSpeed { get; init; } = 2048f;

    /// <summary>Render distance in cells. Null = keep the worldspace/bookmark default (16 cells).</summary>
    internal float? RenderDistanceCells { get; init; }

    internal double StallThresholdMilliseconds { get; init; } = 50;
    internal bool ForceGpuTimestamps { get; init; }
    internal bool ShowFrameStats { get; init; } = true;
    internal bool EnablePersistentMeshCache { get; init; }
    internal bool Verbose { get; init; }
    internal int WindowWidth { get; init; } = 1450;
    internal int WindowHeight { get; init; } = 900;

    /// <summary>
    ///     When set, renders one top-down "Rendered models" overlay (the 2D-map feature) of a
    ///     window around the camera, saves it to this PNG, logs coverage, and exits. Autonomous test for
    ///     the offscreen top-down render path.
    /// </summary>
    internal string? CaptureTopDownPath { get; init; }

    /// <summary>Width/height of the top-down capture window in cells (default 6).</summary>
    internal int CaptureSpanCells { get; init; } = 6;

    /// <summary>
    ///     When set with --capture-topdown, targets this exterior worldspace by index (centered
    ///     on its centroid) instead of the camera position — exercises the top-down worldspace sync.
    /// </summary>
    internal int? CaptureWorldspaceIndex { get; init; }

    /// <summary>
    ///     When set with --capture-topdown, overrides the capture window center with these world
    ///     coordinates (the selected worldspace's FormId is still used for the overlay sync). Lets a
    ///     headless capture be aimed at a specific landmark (e.g. Camp McCarran) rather than the
    ///     camera pose or worldspace centroid.
    /// </summary>
    internal float? CaptureCenterX { get; init; }

    internal float? CaptureCenterY { get; init; }

    /// <summary>
    ///     Camera height (world Z) for a perspective <see cref="CaptureFramePath" /> aimed via
    ///     <see cref="CaptureCenterX" />/<see cref="CaptureCenterY" /> — e.g. hover a few hundred units
    ///     above a water/lava plane. When unset the framing height from worldspace selection is kept.
    /// </summary>
    internal float? CaptureZ { get; init; }

    /// <summary>
    ///     When set, renders ONE live perspective frame (sky dome + sun/moon + scene) to this PNG and exits
    ///     — the sky-verification capture. Honors <see cref="CaptureWorldspaceName" /> /
    ///     <see cref="CaptureWeatherName" /> / <see cref="CaptureHour" /> / <see cref="CapturePitchDegrees" /> /
    ///     <see cref="CaptureYawDegrees" /> and the explicit capture dimensions.
    /// </summary>
    internal string? CaptureFramePath { get; init; }

    /// <summary>Exterior worldspace EditorID to frame for <see cref="CaptureFramePath" /> (e.g. WastelandNV).</summary>
    internal string? CaptureWorldspaceName { get; init; }

    /// <summary>
    ///     Interior CELL EditorID or hexadecimal FormID to select before <see cref="CaptureFramePath" />
    ///     runs (e.g. <c>GSDocMitchellHouse</c> or <c>0x00103DF9</c>).
    /// </summary>
    internal string? CaptureInterior { get; init; }

    /// <summary>
    ///     Worldspace EditorID/FullName the 3D control selects at load (sets
    ///     <c>FALLOUT_VIEWER_WORLDSPACE</c> IN-PROCESS — the WinUI activation path doesn't inherit
    ///     the launching shell's environment, so the env var alone silently does nothing). Applies
    ///     to every mode, including the live <c>--duration-seconds</c> profile loop.
    /// </summary>
    internal string? WorldspaceName { get; init; }

    /// <summary>Weather EditorID to force for the capture (e.g. NVWastelandClear).</summary>
    internal string? CaptureWeatherName { get; init; }

    /// <summary>Time-of-day (0..24) for the capture. Default 12 (noon).</summary>
    internal float CaptureHour { get; init; } = 12f;

    /// <summary>
    ///     Day of the lunar cycle for the capture — drives the moon phase + orbit position
    ///     (Profiler_SetGameDay). Default 0.
    /// </summary>
    internal float CaptureDay { get; init; }

    /// <summary>
    ///     Pinned renderer animation clock, or NULL (the DEFAULT) to run the live clock. Pinning
    ///     freezes every time-varying path — water noise scroll, UV animation, wind sway — at one
    ///     phase, so nothing that animates can be observed. Opt in only when byte-comparable repeat
    ///     captures are the actual goal.
    /// </summary>
    internal float? CaptureAnimationTimeSeconds { get; init; }

    /// <summary>
    ///     Optional camera pitch in degrees for the capture; positive looks UP (90 ≈ straight up at the sky).
    ///     Null preserves the selected scene's pitch — the interior framing from <see cref="CaptureInterior" />,
    ///     or the worldspace bookmark. This used to default to 80° because the capture path began life as a
    ///     sky/celestial check, but it has since become the general render-verification path: an unqualified
    ///     capture of water, grass or an interior was silently aimed at the sky (or at an interior's ceiling),
    ///     which reads as "the scene renders nothing". Sky captures now ask for <c>--capture-pitch 80</c>.
    /// </summary>
    internal float? CapturePitchDegrees { get; init; }

    /// <summary>
    ///     Optional camera yaw in degrees for the capture. Null preserves the selected scene bookmark's yaw.
    /// </summary>
    internal float? CaptureYawDegrees { get; init; }

    /// <summary>
    ///     Optional perspective vertical FOV in degrees for the capture (clamped to the viewer's [30,110]
    ///     range). Null keeps the camera's 60° default. Matches the live FOV slider so a P-key pose
    ///     reproduces the on-screen zoom instead of silently reframing at 60°.
    /// </summary>
    internal float? CaptureFovDegrees { get; init; }

    /// <summary>
    ///     Number of consecutive frames a perspective capture renders, each with the camera advanced
    ///     by <see cref="CaptureMotionYawStepDegrees" /> / <see cref="CaptureMotionForwardStep" />.
    ///     Default 1 (a single static frame).
    ///     <para>
    ///         A one-shot static frame cannot reproduce anything that depends on FRAME-TO-FRAME
    ///         renderer state — snapshot prepare/restore pairing, cloud scroll, batch reuse, cascade
    ///         re-render throttles, or any artefact the user only sees while panning. Those are
    ///         exactly the reports that arrive with a camera pose attached, so the harness has to be
    ///         able to move the camera the way the live view does.
    ///     </para>
    /// </summary>
    internal int CaptureMotionFrames { get; init; } = 1;

    /// <summary>Yaw advance in degrees applied between consecutive motion-capture frames.</summary>
    internal float CaptureMotionYawStepDegrees { get; init; }

    /// <summary>Forward translation in world units applied between consecutive motion-capture frames.</summary>
    internal float CaptureMotionForwardStep { get; init; }

    /// <summary>
    ///     Live render-loop frames driven at each new pose BEFORE the offscreen capture of that pose
    ///     (default 6, i.e. ~100 ms of real motion). Zero reverts to isolated offscreen frames.
    ///     <para>
    ///         A capture detaches the render loop and collapses the view, so a motion run built only
    ///         from captures never exercises the live path with a moving camera — the exact thing a
    ///         "only happens while I move" report describes. It also made the harness window look
    ///         frozen while it ran.
    ///     </para>
    /// </summary>
    internal int CaptureMotionLiveFrames { get; init; } = 6;

    /// <summary>Pitch advance in degrees applied between consecutive motion-capture frames.</summary>
    internal float CaptureMotionPitchStepDegrees { get; init; }

    /// <summary>
    ///     Angular radius, in degrees, of a CIRCULAR look pattern: yaw and pitch sweep 90° out of
    ///     phase, one full revolution over <see cref="CaptureMotionFrames" />, starting and ending on
    ///     the requested pose. This is what a user circling the mouse actually produces, and it is
    ///     not reachable by stepping yaw alone — a pure pan holds pitch constant, so anything that
    ///     depends on the pitch axis (view-angle-dependent depth, grazing-angle terms, per-frame
    ///     re-fit heuristics keyed on the view vector) never varies.
    /// </summary>
    internal float CaptureMotionOrbitDegrees { get; init; }

    /// <summary>Pixel width of a perspective frame capture. Default 768.</summary>
    internal int CaptureWidth { get; init; } = 768;

    /// <summary>Pixel height of a perspective frame capture. Default 480.</summary>
    internal int CaptureHeight { get; init; } = 480;

    /// <summary>
    ///     Maximum seconds a one-shot perspective capture waits for reference AND terrain streaming
    ///     to quiesce (sustained for 4 consecutive polls). 120 because the gate now includes terrain
    ///     and a decoded-mesh cache version bump makes the first capture after it a full cold decode;
    ///     the timeout is FATAL (exit 1), so generosity costs wall-clock only on broken scenes.
    /// </summary>
    internal int CaptureSettleTimeoutSeconds { get; init; } = 120;

    /// <summary>Canonical named same-process acceptance scenario, or null for the legacy modes.</summary>
    internal string? ScenarioName { get; init; }

    /// <summary>Artifact directory for a named scenario. Contains its PNG matrix and, by default, logs.</summary>
    internal string? ScenarioOutputDirectory { get; init; }

    internal static string Usage =>
        """
        BethesdaRendererProfiler

        Required:
          --input <esm|esp|dmp>       Open one semantic source directly.
             or
          --data-dir <directory>      Resolve and open all ESM/ESP files in a Data directory.

        Optional:
          --load-order <paths>        Extra ESM/ESP/DMP files, repeatable or semicolon-separated.
          --profile-output <path>     Profile/log output file. Defaults to a timestamped temp log.
          --profile-jsonl <path>      Structured JSONL output. Defaults to profile-output with .jsonl.
          --profile-interval-ms <n>   Aggregate profile interval. Default: 2000.
          --duration-seconds <n>      Exit automatically after the viewer has loaded.
          --scenario <name>           Run a deterministic same-process acceptance scenario and exit.
                                      Names: fnv-water-night-matrix, fnv-water001-synthetic,
                                      fnv-cloud-motion, fnv-celestial, fnv-prospector-neon-bloom, fnv-sunlight-dimmer,
                                      fnv-adaptation-history, fnv-weather-imagespace-bands, fnv-active-adt-base.
          --scenario-output <dir>     Scenario artifact directory. Defaults beside the timestamped profile log.
          --stress-scene <name>       Sets FALLOUT_VIEWER_STRESS_SCENE. Default: WastelandNV; use none to disable.
          --worldspace <name>         Select this worldspace at load (EditorID/FullName; in-process FALLOUT_VIEWER_WORLDSPACE).
          --camera-motion <mode>      static, forward, orbit, or sweep. Default: static.
          --camera-speed <n>          Camera automation speed in world units/sec. Default: 2048.
          --render-distance <cells>   Override the view/render distance in cells. Default: scene default (16).
          --stall-threshold-ms <n>    Emit per-frame stall events at/above this time. Default: 50; 0 disables.
          --gpu-timestamps            Collect D3D12 GPU timestamps from the first rendered frame.
          --no-frame-stats            Do not set FALLOUT_VIEWER_FRAME_STATS=1.
          --persistent-mesh-cache     Enables the opt-in persistent decoded mesh cache.
          --verbose                   Enables debug logging.
          --width <px>                Window width. Default: 1450.
          --height <px>               Window height. Default: 900.
          --capture-topdown <path>    Render one top-down "Rendered models" overlay to a PNG, log coverage, then exit.
          --capture-frame <path>      Render one perspective frame to a PNG, then exit.
          --capture-width <px>        Perspective capture width. Default: 768; maximum: 16384.
          --capture-height <px>       Perspective capture height. Default: 480; maximum: 16384.
          --capture-worldspace-name <id>
                                      Select this exterior Worldspace EditorID for --capture-frame; mismatch fails.
          --capture-interior <cell>   Select an interior by EditorID or 0xFormID for --capture-frame.
          --capture-weather <id>      Select this Weather EditorID for --capture-frame; mismatch fails.
          --capture-hour <n>          Capture time of day from 0 through 24. Default: 12.
          --capture-day <n>           Day of the lunar cycle for the capture (moon phase + orbit). Default: 0.
          --capture-animation-time <s>   (OPT-IN; default is the LIVE clock, same as the on-screen view)
                                      Pin UV/particle/tree/cloud/water animation time. Default: 0 seconds.
          --capture-pitch <degrees>   Camera pitch; positive looks up. Default: preserve the selected
                                      scene's pitch (interior framing / worldspace bookmark). Sky and
                                      celestial captures want an explicit --capture-pitch 80.
          --capture-yaw <degrees>     Camera yaw. Default: preserve the selected scene bookmark yaw.
          --capture-motion-frames <n> Render n consecutive frames, advancing the camera between each, and
                                      write <out>.NNN.png per frame (default 1 = one static frame). Needed
                                      for anything that only appears while the camera MOVES.
          --capture-motion-yaw-step <degrees>
                                      Yaw advance per motion frame (e.g. 2 = a slow pan).
          --capture-motion-live-frames <n>
                                      Live render-loop frames driven at each pose before capturing it
                                      (default 6). 0 = isolated offscreen frames only.
          --capture-motion-pitch-step <degrees>
                                      Pitch advance per motion frame.
          --capture-motion-orbit-degrees <degrees>
                                      Circular look: yaw+pitch sweep 90 degrees out of phase, one full
                                      revolution over --capture-motion-frames (what circling the mouse does).
          --capture-motion-forward-step <units>
                                      Forward translation per motion frame.
          --capture-fov <degrees>     Perspective vertical FOV (30-110). Default: 60 (the camera default).
          --capture-settle-timeout-seconds <n>
                                      Fail unless reference streaming quiesces within n seconds. Default: 60.
          --capture-cells <n>         Top-down capture window size in cells (default 6).
          --capture-worldspace <i>    Target exterior worldspace index i (centered on its centroid) — tests the top-down worldspace sync.
          --capture-center-x <x>      Override the capture window center X (world units). Aims the capture at a landmark.
          --capture-center-y <y>      Override the capture window center Y (world units).
          --capture-z <z>             Camera height for an aimed perspective --capture-frame (world units).

        Examples:
          BethesdaRendererProfiler --input "C:\Games\Fallout New Vegas\Data\FalloutNV.esm" --duration-seconds 60
          BethesdaRendererProfiler --input capture.dmp --data-dir "C:\Games\Fallout New Vegas\Data" --stress-scene WastelandNVHeavy
        """;

    internal static bool TryParse(
        string[] args,
        out RendererProfilerOptions options,
        out string? error)
    {
        string? input = null;
        string? dataDir = null;
        string? profileOutput = null;
        string? profileJsonl = null;
        var stressScene = DefaultStressScene;
        var loadOrder = new List<string>();
        var profileIntervalMs = 2000;
        int? durationSeconds = null;
        var cameraMotion = RendererCameraMotionKind.Static;
        var cameraSpeed = 2048f;
        float? renderDistanceCells = null;
        var stallThresholdMs = 50d;
        var forceGpuTimestamps = false;
        var showFrameStats = true;
        var persistentMeshCache = false;
        var verbose = false;
        var width = 1450;
        var height = 900;
        string? captureTopDown = null;
        var captureSpanCells = 6;
        int? captureWorldspaceIndex = null;
        float? captureCenterX = null;
        float? captureCenterY = null;
        float? captureZ = null;
        string? captureFrame = null;
        string? captureWorldspaceName = null;
        string? captureInterior = null;
        string? worldspaceName = null;
        string? captureWeatherName = null;
        var captureHour = 12f;
        var captureDay = 0f;
        float? captureAnimationTimeSeconds = null;
        float? capturePitchDegrees = null;
        float? captureYawDegrees = null;
        var captureMotionFrames = 1;
        var captureMotionYawStepDegrees = 0f;
        var captureMotionForwardStep = 0f;
        var captureMotionPitchStepDegrees = 0f;
        var captureMotionLiveFrames = 6;
        var captureMotionOrbitDegrees = 0f;
        float? captureFovDegrees = null;
        var captureWidth = 768;
        var captureHeight = 480;
        var captureSettleTimeoutSeconds = 60;
        string? scenarioName = null;
        string? scenarioOutput = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                case "/?":
                    options = Default;
                    error = null;
                    return false;

                case "-i":
                case "--input":
                    input = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--data-dir":
                    dataDir = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--load-order":
                    var value = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    AddPathList(loadOrder, value);
                    break;

                case "--profile-output":
                case "--log":
                    profileOutput = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--profile-jsonl":
                    profileJsonl = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--profile-interval-ms":
                    if (!TryReadPositiveInt(args, ref i, arg, out profileIntervalMs, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--duration-seconds":
                    if (!TryReadPositiveInt(args, ref i, arg, out var seconds, out error))
                    {
                        return Fail(out options);
                    }

                    durationSeconds = seconds;
                    break;

                case "--scenario":
                    scenarioName = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--scenario-output":
                    scenarioOutput = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--stress-scene":
                    stressScene = NormalizeStressScene(RequireValue(args, ref i, arg, out error));
                    if (error != null) return Fail(out options);
                    break;

                case "--camera-motion":
                    var motionRaw = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    if (!RendererCameraMotion.TryParseKind(motionRaw, out cameraMotion))
                    {
                        error = $"Unknown camera motion: {motionRaw}";
                        return Fail(out options);
                    }

                    break;

                case "--camera-speed":
                    if (!TryReadPositiveFloat(args, ref i, arg, out cameraSpeed, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--render-distance":
                    if (!TryReadPositiveFloat(args, ref i, arg, out var renderDistanceValue, out error))
                    {
                        return Fail(out options);
                    }

                    renderDistanceCells = renderDistanceValue;
                    break;

                case "--stall-threshold-ms":
                    if (!TryReadNonNegativeDouble(args, ref i, arg, out stallThresholdMs, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--gpu-timestamps":
                    forceGpuTimestamps = true;
                    break;

                case "--no-frame-stats":
                    showFrameStats = false;
                    break;

                case "--persistent-mesh-cache":
                    persistentMeshCache = true;
                    break;

                case "--verbose":
                    verbose = true;
                    break;

                case "--capture-topdown":
                    captureTopDown = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--capture-cells":
                    if (!TryReadPositiveInt(args, ref i, arg, out captureSpanCells, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-worldspace":
                    var wsRaw = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    if (!int.TryParse(wsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wsIdx) ||
                        wsIdx < 0)
                    {
                        error = $"{arg} requires a non-negative integer.";
                        return Fail(out options);
                    }

                    captureWorldspaceIndex = wsIdx;
                    break;

                // Read the value directly (not via RequireValue) so negative coordinates,
                // which start with '-', are accepted rather than treated as a missing value.
                case "--capture-center-x":
                    if (!TryReadFiniteFloat(args, ref i, arg, "a finite number", out var ccx, out error))
                    {
                        return Fail(out options);
                    }

                    captureCenterX = ccx;
                    break;

                case "--capture-center-y":
                    if (!TryReadFiniteFloat(args, ref i, arg, "a finite number", out var ccy, out error))
                    {
                        return Fail(out options);
                    }

                    captureCenterY = ccy;
                    break;

                case "--capture-z":
                    if (!TryReadFiniteFloat(args, ref i, arg, "a finite number", out var ccz, out error))
                    {
                        return Fail(out options);
                    }

                    captureZ = ccz;
                    break;

                case "--capture-frame":
                    captureFrame = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--capture-width":
                    if (!TryReadCaptureDimension(args, ref i, arg, out captureWidth, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-height":
                    if (!TryReadCaptureDimension(args, ref i, arg, out captureHeight, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-settle-timeout-seconds":
                    if (!TryReadPositiveInt(args, ref i, arg, out captureSettleTimeoutSeconds, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-worldspace-name":
                    captureWorldspaceName = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--capture-interior":
                    captureInterior = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--worldspace":
                    worldspaceName = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--capture-weather":
                    captureWeatherName = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--capture-hour":
                    if (!TryReadNonNegativeDouble(args, ref i, arg, out var hour, out error))
                    {
                        return Fail(out options);
                    }

                    captureHour = (float)hour;
                    if (captureHour > 24f)
                    {
                        error = $"{arg} must be between 0 and 24 (inclusive).";
                        return Fail(out options);
                    }

                    break;

                case "--capture-day":
                    if (!TryReadNonNegativeDouble(args, ref i, arg, out var day, out error))
                    {
                        return Fail(out options);
                    }

                    captureDay = (float)day;
                    break;

                case "--capture-animation-time":
                    if (!TryReadNonNegativeFloat(args, ref i, arg, out var pinnedAnimationClock, out error))
                    {
                        return Fail(out options);
                    }

                    captureAnimationTimeSeconds = pinnedAnimationClock;
                    break;

                // Pitch can be negative (look down), so read the value directly rather than via RequireValue.
                case "--capture-pitch":
                    if (!TryReadFiniteFloat(
                            args, ref i, arg, "a finite number (degrees; positive looks up)",
                            out var pitchDegrees, out error))
                    {
                        return Fail(out options);
                    }

                    capturePitchDegrees = pitchDegrees;
                    break;

                // Yaw can be negative, so read the value directly rather than via RequireValue.
                case "--capture-yaw":
                    if (!TryReadFiniteFloat(args, ref i, arg, "a finite number (degrees)", out var yawDegrees,
                            out error))
                    {
                        return Fail(out options);
                    }

                    captureYawDegrees = yawDegrees;
                    break;

                case "--capture-motion-frames":
                    if (!TryReadPositiveInt(args, ref i, arg, out captureMotionFrames, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-motion-yaw-step":
                    if (!TryReadFiniteFloat(args, ref i, arg, "a finite number (degrees per frame)",
                            out captureMotionYawStepDegrees, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-motion-live-frames":
                    if (!TryReadNonNegativeInt(args, ref i, arg, out captureMotionLiveFrames, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-motion-pitch-step":
                    if (!TryReadFiniteFloat(args, ref i, arg, "a finite number (degrees per frame)",
                            out captureMotionPitchStepDegrees, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-motion-orbit-degrees":
                    if (!TryReadFiniteFloat(args, ref i, arg, "a finite number (degrees)",
                            out captureMotionOrbitDegrees, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-motion-forward-step":
                    if (!TryReadFiniteFloat(args, ref i, arg, "a finite number (world units per frame)",
                            out captureMotionForwardStep, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--capture-fov":
                    if (!TryReadFiniteFloat(args, ref i, arg, "a finite number (degrees)", out var fovDegrees,
                            out error))
                    {
                        return Fail(out options);
                    }

                    captureFovDegrees = fovDegrees;
                    break;

                case "--width":
                    if (!TryReadPositiveInt(args, ref i, arg, out width, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                case "--height":
                    if (!TryReadPositiveInt(args, ref i, arg, out height, out error))
                    {
                        return Fail(out options);
                    }

                    break;

                default:
                    error = $"Unknown argument: {arg}";
                    return Fail(out options);
            }
        }

        if (string.IsNullOrWhiteSpace(input) && string.IsNullOrWhiteSpace(dataDir))
        {
            error = "Either --input or --data-dir is required.";
            return Fail(out options);
        }

        if (!string.IsNullOrWhiteSpace(input) && !File.Exists(input))
        {
            error = $"Input file not found: {input}";
            return Fail(out options);
        }

        if (!string.IsNullOrWhiteSpace(dataDir) && !Directory.Exists(dataDir))
        {
            error = $"Data directory not found: {dataDir}";
            return Fail(out options);
        }

        foreach (var path in loadOrder)
        {
            if (!File.Exists(path))
            {
                error = $"Load-order file not found: {path}";
                return Fail(out options);
            }
        }

        string? normalizedScenarioName = null;
        if (!string.IsNullOrWhiteSpace(scenarioName) &&
            !RendererProfilerScenarioCatalog.TryNormalizeName(scenarioName, out normalizedScenarioName))
        {
            error = $"Unknown scenario: {scenarioName}. Expected one of: " +
                    string.Join(", ", RendererProfilerScenarioCatalog.Names) + ".";
            return Fail(out options);
        }

        if (!string.IsNullOrWhiteSpace(scenarioOutput) && normalizedScenarioName is null)
        {
            error = "--scenario-output requires --scenario.";
            return Fail(out options);
        }

        if (normalizedScenarioName is not null &&
            (!string.IsNullOrWhiteSpace(captureFrame) || !string.IsNullOrWhiteSpace(captureTopDown)))
        {
            error = "--scenario cannot be combined with --capture-frame or --capture-topdown.";
            return Fail(out options);
        }

        if (normalizedScenarioName is not null && !string.IsNullOrWhiteSpace(captureInterior))
        {
            error = "--scenario cannot be combined with --capture-interior.";
            return Fail(out options);
        }

        if (normalizedScenarioName is not null && durationSeconds is not null)
        {
            error = "--scenario owns process completion and cannot be combined with --duration-seconds.";
            return Fail(out options);
        }

        if (normalizedScenarioName is not null && cameraMotion != RendererCameraMotionKind.Static)
        {
            error = "--scenario requires deterministic static camera motion.";
            return Fail(out options);
        }

        if (!string.IsNullOrWhiteSpace(captureInterior) && string.IsNullOrWhiteSpace(captureFrame))
        {
            error = "--capture-interior requires --capture-frame.";
            return Fail(out options);
        }

        if (!string.IsNullOrWhiteSpace(captureInterior) && !string.IsNullOrWhiteSpace(captureWorldspaceName))
        {
            error = "--capture-interior cannot be combined with --capture-worldspace-name.";
            return Fail(out options);
        }

        if (!string.IsNullOrWhiteSpace(captureInterior) && !string.IsNullOrWhiteSpace(captureTopDown))
        {
            error = "--capture-interior cannot be combined with --capture-topdown.";
            return Fail(out options);
        }

        var defaultProfileOutput = CreateDefaultProfileOutputPath();
        string? resolvedScenarioOutput;
        if (normalizedScenarioName is null)
        {
            resolvedScenarioOutput = null;
        }
        else if (string.IsNullOrWhiteSpace(scenarioOutput))
        {
            resolvedScenarioOutput = Path.Combine(
                Path.GetDirectoryName(defaultProfileOutput)!,
                Path.GetFileNameWithoutExtension(defaultProfileOutput) + "-" + normalizedScenarioName);
        }
        else
        {
            resolvedScenarioOutput = Path.GetFullPath(scenarioOutput);
        }

        string resolvedProfileOutput;
        if (!string.IsNullOrWhiteSpace(profileOutput))
        {
            resolvedProfileOutput = Path.GetFullPath(profileOutput);
        }
        else if (resolvedScenarioOutput is null)
        {
            resolvedProfileOutput = defaultProfileOutput;
        }
        else
        {
            resolvedProfileOutput = Path.Combine(resolvedScenarioOutput, "scenario.log");
        }

        string resolvedProfileJsonl;
        if (!string.IsNullOrWhiteSpace(profileJsonl))
        {
            resolvedProfileJsonl = Path.GetFullPath(profileJsonl);
        }
        else if (resolvedScenarioOutput is null)
        {
            resolvedProfileJsonl = CreateDefaultProfileJsonlOutputPath(resolvedProfileOutput);
        }
        else
        {
            resolvedProfileJsonl = Path.Combine(resolvedScenarioOutput, "scenario.jsonl");
        }

        options = new RendererProfilerOptions
        {
            InputPath = input is null ? null : Path.GetFullPath(input),
            DataDirectory = dataDir is null ? null : Path.GetFullPath(dataDir),
            LoadOrderPaths = loadOrder.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProfileOutputPath = resolvedProfileOutput,
            ProfileJsonlOutputPath = resolvedProfileJsonl,
            ProfileIntervalMilliseconds = profileIntervalMs,
            DurationSeconds = durationSeconds,
            StressScene = normalizedScenarioName is null ? stressScene : null,
            CameraMotion = cameraMotion,
            CameraSpeed = cameraSpeed,
            RenderDistanceCells = renderDistanceCells,
            StallThresholdMilliseconds = stallThresholdMs,
            ForceGpuTimestamps = forceGpuTimestamps,
            ShowFrameStats = showFrameStats,
            EnablePersistentMeshCache = persistentMeshCache,
            Verbose = verbose,
            WindowWidth = Math.Max(width, 640),
            WindowHeight = Math.Max(height, 480),
            CaptureTopDownPath = string.IsNullOrWhiteSpace(captureTopDown) ? null : Path.GetFullPath(captureTopDown),
            CaptureSpanCells = captureSpanCells,
            CaptureWorldspaceIndex = captureWorldspaceIndex,
            CaptureCenterX = captureCenterX,
            CaptureCenterY = captureCenterY,
            CaptureZ = captureZ,
            CaptureFramePath = string.IsNullOrWhiteSpace(captureFrame) ? null : Path.GetFullPath(captureFrame),
            CaptureWorldspaceName = captureWorldspaceName,
            CaptureInterior = captureInterior?.Trim(),
            WorldspaceName = worldspaceName,
            CaptureWeatherName = captureWeatherName,
            CaptureHour = captureHour,
            CaptureDay = captureDay,
            CaptureAnimationTimeSeconds = captureAnimationTimeSeconds,
            CapturePitchDegrees = capturePitchDegrees,
            CaptureYawDegrees = captureYawDegrees,
            CaptureMotionFrames = captureMotionFrames,
            CaptureMotionYawStepDegrees = captureMotionYawStepDegrees,
            CaptureMotionForwardStep = captureMotionForwardStep,
            CaptureMotionPitchStepDegrees = captureMotionPitchStepDegrees,
            CaptureMotionLiveFrames = captureMotionLiveFrames,
            CaptureMotionOrbitDegrees = captureMotionOrbitDegrees,
            CaptureFovDegrees = captureFovDegrees,
            CaptureWidth = captureWidth,
            CaptureHeight = captureHeight,
            CaptureSettleTimeoutSeconds = captureSettleTimeoutSeconds,
            ScenarioName = normalizedScenarioName,
            ScenarioOutputDirectory = resolvedScenarioOutput
        };
        error = null;
        return true;
    }

    private static bool Fail(out RendererProfilerOptions options)
    {
        options = Default;
        return false;
    }

    private static string? NormalizeStressScene(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.Equals(trimmed, "WastelandNV", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(trimmed, DefaultStressScene, StringComparison.OrdinalIgnoreCase)
            ? DefaultStressScene
            : trimmed;
    }

    private static RendererProfilerOptions CreateDefault()
    {
        var profileOutputPath = CreateDefaultProfileOutputPath();
        return new RendererProfilerOptions
        {
            ProfileOutputPath = profileOutputPath,
            ProfileJsonlOutputPath = CreateDefaultProfileJsonlOutputPath(profileOutputPath)
        };
    }

    private static string? RequireValue(string[] args, ref int index, string option, out string? error)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
        {
            error = $"{option} requires a value.";
            return null;
        }

        error = null;
        index++;
        return args[index];
    }

    private static bool TryReadPositiveInt(
        string[] args,
        ref int index,
        string option,
        out int value,
        out string? error)
    {
        var raw = RequireValue(args, ref index, option, out error);
        if (error != null)
        {
            value = 0;
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            error = $"{option} requires a positive integer.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>As <see cref="TryReadPositiveInt" /> but admits 0, which is a meaningful setting for
    /// counts that mean "none" (e.g. driving no live frames between motion poses).</summary>
    private static bool TryReadNonNegativeInt(
        string[] args,
        ref int index,
        string option,
        out int value,
        out string? error)
    {
        var raw = RequireValue(args, ref index, option, out error);
        if (error != null)
        {
            value = 0;
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
        {
            error = $"{option} requires a non-negative integer.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadCaptureDimension(
        string[] args,
        ref int index,
        string option,
        out int value,
        out string? error)
    {
        if (!TryReadPositiveInt(args, ref index, option, out value, out error))
        {
            return false;
        }

        // D3D12's maximum Texture2D dimension is 16384. Rejecting larger requests here avoids
        // turning a typo into an opaque GPU allocation/capture failure later in the run.
        if (value > 16384)
        {
            error = $"{option} must be between 1 and 16384 (inclusive).";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadPositiveFloat(
        string[] args,
        ref int index,
        string option,
        out float value,
        out string? error)
    {
        var raw = RequireValue(args, ref index, option, out error);
        if (error != null)
        {
            value = 0;
            return false;
        }

        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            value <= 0 ||
            float.IsNaN(value) ||
            float.IsInfinity(value))
        {
            error = $"{option} must be a positive number.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadNonNegativeDouble(
        string[] args,
        ref int index,
        string option,
        out double value,
        out string? error)
    {
        var raw = RequireValue(args, ref index, option, out error);
        if (error != null)
        {
            value = 0;
            return false;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            value < 0 ||
            double.IsNaN(value) ||
            double.IsInfinity(value))
        {
            error = $"{option} must be a non-negative number.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadNonNegativeFloat(
        string[] args,
        ref int index,
        string option,
        out float value,
        out string? error)
    {
        // Read directly so a negative value reaches validation instead of being mistaken for an option.
        if (index + 1 >= args.Length)
        {
            value = 0f;
            error = $"{option} requires a value.";
            return false;
        }

        var raw = args[++index];
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            value < 0f ||
            !float.IsFinite(value))
        {
            error = $"{option} must be a finite non-negative number.";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryReadFiniteFloat(
        string[] args,
        ref int index,
        string option,
        string requirement,
        out float value,
        out string? error)
    {
        // Read directly so a negative value reaches validation instead of being mistaken for an option.
        if (index + 1 >= args.Length)
        {
            value = 0f;
            error = $"{option} requires a value.";
            return false;
        }

        var raw = args[++index];
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
            !float.IsFinite(value))
        {
            error = $"{option} must be {requirement}.";
            return false;
        }

        error = null;
        return true;
    }

    private static void AddPathList(List<string> paths, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var piece in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            paths.Add(piece);
        }
    }

    private static string CreateDefaultProfileOutputPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "BethesdaRendererProfiler");
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"profile-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        return Path.Combine(dir, name);
    }

    private static string CreateDefaultProfileJsonlOutputPath(string profileOutputPath)
    {
        var directory = Path.GetDirectoryName(profileOutputPath);
        var fileName = Path.GetFileNameWithoutExtension(profileOutputPath);
        return Path.Combine(
            string.IsNullOrEmpty(directory) ? Directory.GetCurrentDirectory() : directory,
            fileName + ".jsonl");
    }
}
