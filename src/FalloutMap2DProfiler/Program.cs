using FalloutXbox360Utils;
using FalloutXbox360Utils.Core;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace FalloutMap2DProfiler;

public static class Program
{
    private const int AttachParentProcess = -1;

    [STAThread]
    public static void Main(string[] args)
    {
        AttachConsole(AttachParentProcess);

        var parse = Map2DProfilerOptions.TryParse(args, out var options, out var error);
        if (!parse)
        {
            if (!string.IsNullOrEmpty(error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
            }

            Console.WriteLine(Map2DProfilerOptions.Usage);
            return;
        }

        ConfigureEnvironment();
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

    private static void ConfigureEnvironment()
    {
        // Switches Map2DProfilerTrace on inside FalloutXbox360Utils — every cache mutation,
        // viewport rebuild, stream lifecycle event, and per-frame draw in WorldMapControl
        // is logged to the configured profile output.
        EnvironmentVariables.Set(EnvironmentVariables.Map2D.Trace, EnvironmentVariables.Enabled);
    }

    private static void ConfigureLogger(Map2DProfilerOptions options)
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
                Console.WriteLine($"[FalloutMap2DProfiler] Profile log: {options.ProfileOutputPath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FalloutMap2DProfiler] Failed to open profile log: {ex.Message}");
        }
    }
}
