using System.Runtime.InteropServices;
using FalloutXbox360Utils;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace FalloutRendererProfiler;

public static class Program
{
    private const int AttachParentProcess = -1;

    [STAThread]
    public static void Main(string[] args)
    {
        AttachConsole(AttachParentProcess);

        var parse = RendererProfilerOptions.TryParse(args, out var options, out var error);
        if (!parse)
        {
            if (!string.IsNullOrEmpty(error))
            {
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
        Environment.SetEnvironmentVariable("FALLOUT_VIEWER_PROFILE_LOG", "1");
        Environment.SetEnvironmentVariable(
            "FALLOUT_VIEWER_PROFILE_INTERVAL_MS",
            options.ProfileIntervalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("FALLOUT_VIEWER_PROFILE_JSONL", options.ProfileJsonlOutputPath);
        Environment.SetEnvironmentVariable(
            "FALLOUT_VIEWER_STALL_THRESHOLD_MS",
            options.StallThresholdMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (options.ShowFrameStats)
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_FRAME_STATS", "1");
        }

        if (!string.IsNullOrWhiteSpace(options.StressScene))
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_STRESS_SCENE", options.StressScene);
        }

        if (options.EnablePersistentMeshCache)
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_PERSISTENT_MESH_CACHE", "1");
        }

        if (options.ForceGpuTimestamps)
        {
            Environment.SetEnvironmentVariable("FALLOUT_VIEWER_GPU_TIMESTAMPS", "1");
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
                Console.WriteLine($"[FalloutRendererProfiler] Profile log: {options.ProfileOutputPath}");
                RendererProfilerTrace.Configure(options.ProfileJsonlOutputPath);
                Console.WriteLine($"[FalloutRendererProfiler] Profile JSONL: {options.ProfileJsonlOutputPath}");
                RendererProfilerTrace.Event("startup", new Dictionary<string, object?>
                {
                    ["input"] = options.InputPath,
                    ["dataDirectory"] = options.DataDirectory,
                    ["profileOutput"] = options.ProfileOutputPath,
                    ["profileJsonl"] = options.ProfileJsonlOutputPath,
                    ["profileIntervalMs"] = options.ProfileIntervalMilliseconds,
                    ["durationSeconds"] = options.DurationSeconds,
                    ["stressScene"] = options.StressScene,
                    ["cameraMotion"] = options.CameraMotion.ToString(),
                    ["cameraSpeed"] = options.CameraSpeed,
                    ["stallThresholdMs"] = options.StallThresholdMilliseconds,
                    ["gpuTimestamps"] = options.ForceGpuTimestamps
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FalloutRendererProfiler] Failed to open profile log: {ex.Message}");
        }
    }
}
