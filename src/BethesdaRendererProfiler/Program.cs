using System.Globalization;
using System.Runtime.InteropServices;
using BethesdaMultitool;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace BethesdaRendererProfiler;

public static class Program
{
    private const int AttachParentProcess = -1;

    [STAThread]
    public static void Main(string[] args)
    {
        AttachConsole(AttachParentProcess);

        // Headless single-NIF render: renders one NIF through the real viewer D3D12 stack
        // (ReferenceRenderer12) to a PNG and exits, without launching the profiler window. Used to
        // self-verify viewer-specific NIF rendering (material/alpha/effect) bugs offscreen.
        if (Array.IndexOf(args, "--render-nif") >= 0)
        {
            Environment.ExitCode = NifHeadlessRenderer.Run(args);
            return;
        }

        var parse = RendererProfilerOptions.TryParse(args, out var options, out var error);
        if (!parse)
        {
            if (!string.IsNullOrEmpty(error))
            {
                Environment.ExitCode = 2;
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
            }

            Console.WriteLine(RendererProfilerOptions.Usage);
            return;
        }

        ConfigureEnvironment(options);
        ConfigureLogger(options);

        ComWrappersSupport.InitializeComWrappers();
        FalloutApp.LaunchWindowFactory = () => new MainWindow(options);
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            var app = new FalloutApp();
            _ = app;
        });
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void ConfigureEnvironment(RendererProfilerOptions options)
    {
        EnvironmentVariables.Set(EnvironmentVariables.Viewer.ProfileLog, EnvironmentVariables.Enabled);
        EnvironmentVariables.Set(
            EnvironmentVariables.Viewer.ProfileIntervalMilliseconds,
            options.ProfileIntervalMilliseconds.ToString(CultureInfo.InvariantCulture));
        EnvironmentVariables.Set(EnvironmentVariables.Viewer.ProfileJsonl, options.ProfileJsonlOutputPath);
        EnvironmentVariables.Set(
            EnvironmentVariables.Viewer.StallThresholdMilliseconds,
            options.StallThresholdMilliseconds.ToString(CultureInfo.InvariantCulture));

        if (options.ShowFrameStats)
        {
            EnvironmentVariables.Set(EnvironmentVariables.Viewer.FrameStats, EnvironmentVariables.Enabled);
        }

        if (!string.IsNullOrWhiteSpace(options.StressScene))
        {
            EnvironmentVariables.Set(EnvironmentVariables.Viewer.StressScene, options.StressScene);
        }

        // Set in-process: the WinUI activation path does not inherit the launching shell's
        // environment, so `FALLOUT_VIEWER_WORLDSPACE=… profiler.exe` silently does nothing.
        if (!string.IsNullOrWhiteSpace(options.WorldspaceName))
        {
            EnvironmentVariables.Set(EnvironmentVariables.Viewer.Worldspace, options.WorldspaceName);
        }

        if (options.EnablePersistentMeshCache)
        {
            EnvironmentVariables.Set(EnvironmentVariables.Viewer.PersistentMeshCache, EnvironmentVariables.Enabled);
        }

        if (options.ForceGpuTimestamps)
        {
            EnvironmentVariables.Set(EnvironmentVariables.Viewer.GpuTimestamps, EnvironmentVariables.Enabled);
        }
    }

    private static void ConfigureLogger(RendererProfilerOptions options)
    {
        var logger = Logger.Instance;
        logger.UseSpectre = false;
        logger.IncludeTimestamp = true;
        logger.IncludeLevel = true;
        logger.SetVerbose(options.Verbose);

        try
        {
            if (!string.IsNullOrWhiteSpace(options.ProfileOutputPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(options.ProfileOutputPath)!);
                logger.SetLogFile(options.ProfileOutputPath);
                Console.WriteLine($"[BethesdaRendererProfiler] Profile log: {options.ProfileOutputPath}");
                RendererProfilerTrace.Configure(options.ProfileJsonlOutputPath);
                Console.WriteLine($"[BethesdaRendererProfiler] Profile JSONL: {options.ProfileJsonlOutputPath}");
                RendererProfilerTrace.Event("startup", new Dictionary<string, object?>
                {
                    ["input"] = options.InputPath,
                    ["dataDirectory"] = options.DataDirectory,
                    ["profileOutput"] = options.ProfileOutputPath,
                    ["profileJsonl"] = options.ProfileJsonlOutputPath,
                    ["profileIntervalMs"] = options.ProfileIntervalMilliseconds,
                    ["durationSeconds"] = options.DurationSeconds,
                    ["profileEndCapture"] = options.ProfileEndCapturePath,
                    ["profileSettleTimeoutSeconds"] = options.ProfileSettleTimeoutSeconds,
                    ["scenario"] = options.ScenarioName,
                    ["scenarioOutput"] = options.ScenarioOutputDirectory,
                    ["stressScene"] = options.StressScene,
                    ["cameraMotion"] = options.CameraMotion.ToString(),
                    ["cameraSpeed"] = options.CameraSpeed,
                    ["stallThresholdMs"] = options.StallThresholdMilliseconds,
                    ["gpuTimestamps"] = options.ForceGpuTimestamps,
                    ["captureFrame"] = options.CaptureFramePath,
                    ["captureWidth"] = options.CaptureWidth,
                    ["captureHeight"] = options.CaptureHeight,
                    ["captureRequestedWorldspace"] = options.CaptureWorldspaceName,
                    ["captureRequestedWeather"] = options.CaptureWeatherName,
                    ["captureHour"] = options.CaptureHour,
                    ["captureDay"] = options.CaptureDay,
                    ["captureAnimationTimeSeconds"] = options.CaptureAnimationTimeSeconds,
                    ["capturePitchDegrees"] = options.CapturePitchDegrees,
                    ["captureYawDegrees"] = options.CaptureYawDegrees,
                    ["captureSettleTimeoutSeconds"] = options.CaptureSettleTimeoutSeconds,
                    // Record raw architectural overrides in the startup trace. Game-scoped effective
                    // state is recorded by the scene capture after the input's game has been decoded.
                    ["liveParticles"] = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_LIVE_PARTICLES"),
                    ["speedTreeRuntimeLod"] = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_SPT_RUNTIME_LOD"),
                    ["modernWater"] = EnvironmentVariables.Get(EnvironmentVariables.Viewer.ModernWater),
                    ["modernImageSpace"] = EnvironmentVariables.Get(EnvironmentVariables.Viewer.ModernImageSpace),
                    ["authoredSkyOverride"] =
                        EnvironmentVariables.Get(EnvironmentVariables.Viewer.AuthoredSky),
                    ["placedLightTiles"] = EnvironmentVariables.Get(EnvironmentVariables.Viewer.PlacedLightTiles),
                    ["tolerantCull"] = EnvironmentVariables.Get(EnvironmentVariables.Viewer.TolerantCull),
                    ["shadows"] = EnvironmentVariables.Get(EnvironmentVariables.Viewer.Shadows),
                    ["referenceGeometryHeap"] =
                        EnvironmentVariables.Get(EnvironmentVariables.Viewer.ReferenceGeometryHeap),
                    ["referenceOpaqueIndirect"] =
                        EnvironmentVariables.Get(EnvironmentVariables.Viewer.ReferenceOpaqueIndirect),
                    ["referenceModernStandardShader"] =
                        EnvironmentVariables.Get(
                            EnvironmentVariables.Viewer.ReferenceModernStandardShader),
                    ["referenceStaticOpaquePacket"] =
                        EnvironmentVariables.Get(
                            EnvironmentVariables.Viewer.ReferenceStaticOpaquePacket),
                    ["referenceOpaqueFrontToBack"] =
                        EnvironmentVariables.Get(
                            EnvironmentVariables.Viewer.ReferenceOpaqueFrontToBack),
                    ["referencePipelineStatistics"] =
                        EnvironmentVariables.Get(
                            EnvironmentVariables.Viewer.ReferencePipelineStatistics),
                    ["shadowComparisonPcf"] =
                        EnvironmentVariables.Get(EnvironmentVariables.Viewer.ShadowComparisonPcf)
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BethesdaRendererProfiler] Failed to open profile log: {ex.Message}");
        }
    }
}
