using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using System.Globalization;

namespace FalloutRendererProfiler;

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

    internal static string Usage =>
        """
        FalloutRendererProfiler

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
          --stress-scene <name>       Sets FALLOUT_VIEWER_STRESS_SCENE. Default: WastelandNV; use none to disable.
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
          --capture-cells <n>         Top-down capture window size in cells (default 6).
          --capture-worldspace <i>    Target exterior worldspace index i (centered on its centroid) — tests the top-down worldspace sync.
          --capture-center-x <x>      Override the capture window center X (world units). Aims the capture at a landmark.
          --capture-center-y <y>      Override the capture window center Y (world units).

        Examples:
          FalloutRendererProfiler --input "C:\Games\Fallout New Vegas\Data\FalloutNV.esm" --duration-seconds 60
          FalloutRendererProfiler --input capture.dmp --data-dir "C:\Games\Fallout New Vegas\Data" --stress-scene WastelandNVHeavy
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
                    if (error != null) return Fail(out options, error);
                    break;

                case "--data-dir":
                    dataDir = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options, error);
                    break;

                case "--load-order":
                    var value = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options, error);
                    AddPathList(loadOrder, value);
                    break;

                case "--profile-output":
                case "--log":
                    profileOutput = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options, error);
                    break;

                case "--profile-jsonl":
                    profileJsonl = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options, error);
                    break;

                case "--profile-interval-ms":
                    if (!TryReadPositiveInt(args, ref i, arg, out profileIntervalMs, out error))
                    {
                        return Fail(out options, error);
                    }

                    break;

                case "--duration-seconds":
                    if (!TryReadPositiveInt(args, ref i, arg, out var seconds, out error))
                    {
                        return Fail(out options, error);
                    }

                    durationSeconds = seconds;
                    break;

                case "--stress-scene":
                    stressScene = NormalizeStressScene(RequireValue(args, ref i, arg, out error));
                    if (error != null) return Fail(out options, error);
                    break;

                case "--camera-motion":
                    var motionRaw = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options, error);
                    if (!RendererCameraMotion.TryParseKind(motionRaw, out cameraMotion))
                    {
                        return Fail(out options, $"Unknown camera motion: {motionRaw}");
                    }

                    break;

                case "--camera-speed":
                    if (!TryReadPositiveFloat(args, ref i, arg, out cameraSpeed, out error))
                    {
                        return Fail(out options, error);
                    }

                    break;

                case "--render-distance":
                    if (!TryReadPositiveFloat(args, ref i, arg, out var renderDistanceValue, out error))
                    {
                        return Fail(out options, error);
                    }

                    renderDistanceCells = renderDistanceValue;
                    break;

                case "--stall-threshold-ms":
                    if (!TryReadNonNegativeDouble(args, ref i, arg, out stallThresholdMs, out error))
                    {
                        return Fail(out options, error);
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
                    if (error != null) return Fail(out options, error);
                    break;

                case "--capture-cells":
                    if (!TryReadPositiveInt(args, ref i, arg, out captureSpanCells, out error))
                    {
                        return Fail(out options, error);
                    }

                    break;

                case "--capture-worldspace":
                    var wsRaw = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options, error);
                    if (!int.TryParse(wsRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var wsIdx) ||
                        wsIdx < 0)
                    {
                        return Fail(out options, $"{arg} requires a non-negative integer.");
                    }

                    captureWorldspaceIndex = wsIdx;
                    break;

                // Read the value directly (not via RequireValue) so negative coordinates,
                // which start with '-', are accepted rather than treated as a missing value.
                case "--capture-center-x":
                    if (i + 1 >= args.Length)
                    {
                        error = $"{arg} requires a value.";
                        return Fail(out options, error);
                    }

                    if (!float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var ccx) ||
                        !float.IsFinite(ccx))
                    {
                        error = $"{arg} must be a finite number.";
                        return Fail(out options, error);
                    }

                    captureCenterX = ccx;
                    break;

                case "--capture-center-y":
                    if (i + 1 >= args.Length)
                    {
                        error = $"{arg} requires a value.";
                        return Fail(out options, error);
                    }

                    if (!float.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var ccy) ||
                        !float.IsFinite(ccy))
                    {
                        error = $"{arg} must be a finite number.";
                        return Fail(out options, error);
                    }

                    captureCenterY = ccy;
                    break;

                case "--width":
                    if (!TryReadPositiveInt(args, ref i, arg, out width, out error))
                    {
                        return Fail(out options, error);
                    }

                    break;

                case "--height":
                    if (!TryReadPositiveInt(args, ref i, arg, out height, out error))
                    {
                        return Fail(out options, error);
                    }

                    break;

                default:
                    error = $"Unknown argument: {arg}";
                    return Fail(out options, error);
            }
        }

        if (string.IsNullOrWhiteSpace(input) && string.IsNullOrWhiteSpace(dataDir))
        {
            error = "Either --input or --data-dir is required.";
            return Fail(out options, error);
        }

        if (!string.IsNullOrWhiteSpace(input) && !File.Exists(input))
        {
            error = $"Input file not found: {input}";
            return Fail(out options, error);
        }

        if (!string.IsNullOrWhiteSpace(dataDir) && !Directory.Exists(dataDir))
        {
            error = $"Data directory not found: {dataDir}";
            return Fail(out options, error);
        }

        foreach (var path in loadOrder)
        {
            if (!File.Exists(path))
            {
                error = $"Load-order file not found: {path}";
                return Fail(out options, error);
            }
        }

        var resolvedProfileOutput = string.IsNullOrWhiteSpace(profileOutput)
            ? CreateDefaultProfileOutputPath()
            : Path.GetFullPath(profileOutput);
        var resolvedProfileJsonl = string.IsNullOrWhiteSpace(profileJsonl)
            ? CreateDefaultProfileJsonlOutputPath(resolvedProfileOutput)
            : Path.GetFullPath(profileJsonl);

        options = new RendererProfilerOptions
        {
            InputPath = input is null ? null : Path.GetFullPath(input),
            DataDirectory = dataDir is null ? null : Path.GetFullPath(dataDir),
            LoadOrderPaths = loadOrder.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProfileOutputPath = resolvedProfileOutput,
            ProfileJsonlOutputPath = resolvedProfileJsonl,
            ProfileIntervalMilliseconds = profileIntervalMs,
            DurationSeconds = durationSeconds,
            StressScene = stressScene,
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
            CaptureCenterY = captureCenterY
        };
        error = null;
        return true;
    }

    private static bool Fail(out RendererProfilerOptions options, string? message)
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
        var dir = Path.Combine(Path.GetTempPath(), "FalloutRendererProfiler");
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
