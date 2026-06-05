using System.Globalization;

namespace FalloutRendererProfiler;

internal sealed record RendererProfilerOptions
{
    internal static RendererProfilerOptions Default { get; } = new()
    {
        ProfileOutputPath = CreateDefaultProfileOutputPath()
    };

    internal string? InputPath { get; init; }
    internal string? DataDirectory { get; init; }
    internal IReadOnlyList<string> LoadOrderPaths { get; init; } = [];
    internal string ProfileOutputPath { get; init; } = CreateDefaultProfileOutputPath();
    internal int ProfileIntervalMilliseconds { get; init; } = 2000;
    internal int? DurationSeconds { get; init; }
    internal string? StressScene { get; init; }
    internal bool ShowFrameStats { get; init; } = true;
    internal bool EnablePersistentMeshCache { get; init; }
    internal bool Verbose { get; init; }
    internal int WindowWidth { get; init; } = 1450;
    internal int WindowHeight { get; init; } = 900;

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
          --profile-interval-ms <n>   Aggregate profile interval. Default: 2000.
          --duration-seconds <n>      Exit automatically after the viewer has loaded.
          --stress-scene <name>       Sets FALLOUT_VIEWER_STRESS_SCENE before viewer creation.
          --no-frame-stats            Do not set FALLOUT_VIEWER_FRAME_STATS=1.
          --persistent-mesh-cache     Enables the opt-in persistent decoded mesh cache.
          --verbose                   Enables debug logging.
          --width <px>                Window width. Default: 1450.
          --height <px>               Window height. Default: 900.

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
        string? stressScene = null;
        var loadOrder = new List<string>();
        var profileIntervalMs = 2000;
        int? durationSeconds = null;
        var showFrameStats = true;
        var persistentMeshCache = false;
        var verbose = false;
        var width = 1450;
        var height = 900;

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
                    stressScene = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options, error);
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

        options = new RendererProfilerOptions
        {
            InputPath = input is null ? null : Path.GetFullPath(input),
            DataDirectory = dataDir is null ? null : Path.GetFullPath(dataDir),
            LoadOrderPaths = loadOrder.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProfileOutputPath = string.IsNullOrWhiteSpace(profileOutput)
                ? CreateDefaultProfileOutputPath()
                : Path.GetFullPath(profileOutput),
            ProfileIntervalMilliseconds = profileIntervalMs,
            DurationSeconds = durationSeconds,
            StressScene = stressScene,
            ShowFrameStats = showFrameStats,
            EnablePersistentMeshCache = persistentMeshCache,
            Verbose = verbose,
            WindowWidth = Math.Max(width, 640),
            WindowHeight = Math.Max(height, 480)
        };
        error = null;
        return true;
    }

    private static bool Fail(out RendererProfilerOptions options, string? message)
    {
        options = Default;
        return false;
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
}
