using System.Globalization;

namespace FalloutMap2DProfiler;

internal sealed record Map2DProfilerOptions
{
    internal static Map2DProfilerOptions Default { get; } = new()
    {
        ProfileOutputPath = CreateDefaultProfileOutputPath()
    };

    internal string? InputPath { get; init; }
    internal string? DataDirectory { get; init; }
    internal IReadOnlyList<string> LoadOrderPaths { get; init; } = [];
    internal string ProfileOutputPath { get; init; } = CreateDefaultProfileOutputPath();
    internal int? DurationSeconds { get; init; }
    internal int? WorldspaceIndex { get; init; }
    internal string? ScenarioName { get; init; }
    internal bool Verbose { get; init; }

    /// <summary>
    ///     Brings up the 3D <c>WorldView3DControl</c> as the map's top-down provider AND enables the
    ///     "Rendered models" overlay once it is ready — so the profiler exercises the FULL draw path
    ///     (terrain tiles + the per-frame top-down model+water overlay), not the 2D-only one. Implies
    ///     the 2D+3D coupling regime (same as <c>FALLOUT_PROFILER_WITH_3D=1</c>) and selects the
    ///     TerrainTextures layer where the overlay + mip/perf work live.
    /// </summary>
    internal bool RenderedModels { get; init; }

    internal int WindowWidth { get; init; } = 1450;
    internal int WindowHeight { get; init; } = 900;

    internal static string Usage =>
        """
        FalloutMap2DProfiler

        Required:
          --input <esm|esp|dmp>       Open one semantic source directly.
             or
          --data-dir <directory>      Resolve and open all ESM/ESP files in a Data directory.

        Optional:
          --load-order <paths>        Extra ESM/ESP/DMP files, repeatable or semicolon-separated.
          --profile-output <path>     Profile/log output file. Defaults to a timestamped temp log.
          --worldspace <index>        Select worldspace by index after data loads.
          --scenario <name>           Run a scripted scenario after worldspace selection.
                                      Names: zoom-pan-zigzag (default if --duration is set)
          --rendered-models           Bring up the 3D viewer + enable the "Rendered models" overlay
                                      so the run is a TRUE full-path perf test (terrain tiles + the
                                      per-frame top-down model/water overlay). Selects TerrainTextures.
          --duration-seconds <n>      Exit automatically after the scenario completes (or after N seconds).
          --verbose                   Enables debug logging.
          --width <px>                Window width. Default: 1450.
          --height <px>               Window height. Default: 900.

        The profiler sets FALLOUT_MAP2D_TRACE=1 in the process environment before launching
        the WinUI host, so every cache mutation, viewport rebuild, stream lifecycle event,
        and per-frame draw inside WorldMapControl is logged to the --profile-output file.

        Examples:
          FalloutMap2DProfiler --input "C:\Games\Fallout New Vegas\Data\FalloutNV.esm"
          FalloutMap2DProfiler --data-dir "C:\Games\Fallout New Vegas\Data" --worldspace 0 \
                               --scenario zoom-pan-zigzag --duration-seconds 60
        """;

    internal static bool TryParse(
        string[] args,
        out Map2DProfilerOptions options,
        out string? error)
    {
        string? input = null;
        string? dataDir = null;
        string? profileOutput = null;
        var loadOrder = new List<string>();
        int? durationSeconds = null;
        int? worldspaceIndex = null;
        string? scenarioName = null;
        var verbose = false;
        var renderedModels = false;
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

                case "--worldspace":
                    if (!TryReadNonNegativeInt(args, ref i, arg, out var ws, out error))
                    {
                        return Fail(out options);
                    }

                    worldspaceIndex = ws;
                    break;

                case "--scenario":
                    scenarioName = RequireValue(args, ref i, arg, out error);
                    if (error != null) return Fail(out options);
                    break;

                case "--duration-seconds":
                    if (!TryReadPositiveInt(args, ref i, arg, out var seconds, out error))
                    {
                        return Fail(out options);
                    }

                    durationSeconds = seconds;
                    break;

                case "--verbose":
                    verbose = true;
                    break;

                case "--rendered-models":
                    renderedModels = true;
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

        options = new Map2DProfilerOptions
        {
            InputPath = input is null ? null : Path.GetFullPath(input),
            DataDirectory = dataDir is null ? null : Path.GetFullPath(dataDir),
            LoadOrderPaths = loadOrder.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProfileOutputPath = string.IsNullOrWhiteSpace(profileOutput)
                ? CreateDefaultProfileOutputPath()
                : Path.GetFullPath(profileOutput),
            DurationSeconds = durationSeconds,
            WorldspaceIndex = worldspaceIndex,
            ScenarioName = scenarioName,
            Verbose = verbose,
            RenderedModels = renderedModels,
            WindowWidth = Math.Max(width, 640),
            WindowHeight = Math.Max(height, 480)
        };
        error = null;
        return true;
    }

    private static bool Fail(out Map2DProfilerOptions options)
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
        var dir = Path.Combine(Path.GetTempPath(), "FalloutMap2DProfiler");
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"profile-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        return Path.Combine(dir, name);
    }
}
