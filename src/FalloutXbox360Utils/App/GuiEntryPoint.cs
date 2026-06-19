#if WINDOWS_GUI
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;
using FalloutXbox360Utils.Core;
using UnhandledExceptionEventArgs = System.UnhandledExceptionEventArgs;

namespace FalloutXbox360Utils;

/// <summary>
///     Windows GUI entry point for WinUI 3 application.
/// </summary>
public static class GuiEntryPoint
{
    private const int ATTACH_PARENT_PROCESS = -1;

    public static int Run(string[] args)
    {
        // args parameter reserved for future command-line GUI options
        _ = args;

        // Attach to console early for crash logging
        AttachConsole(ATTACH_PARENT_PROCESS);

        // Route the whole app's logging to a file when FALLOUT_GUI_LOG is set, so the real GUI is
        // traceable (the windowed app has no visible console). Runs BEFORE anything reads the
        // Map2DProfilerTrace static gate so its auto-enable below takes effect.
        ConfigureDiagnostics();

        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Console.WriteLine("[FalloutXbox360Utils] Starting GUI mode...");
            ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                _ = p;
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new FalloutApp();
            });

            return 0;
        }
        catch (Exception ex)
        {
            CrashLog("Application crashed (startup/main)", ex, isTerminating: true);
            return 1;
        }
    }

    /// <summary>
    ///     When <c>FALLOUT_GUI_LOG</c> names a path, routes every <see cref="Logger" /> line (the whole
    ///     GUI: 2D map, 3D viewer, loaders, …) to that file in append mode, mirroring the
    ///     FalloutMap2DProfiler's logger setup. Also auto-enables the 2D-map cache/viewport/stream trace
    ///     (<c>FALLOUT_MAP2D_TRACE</c>) unless the caller set it explicitly, so a single env var captures
    ///     the full trace from the real app — used to debug issues the profiler can't reproduce. No-op
    ///     (and never throws) when the var is unset.
    /// </summary>
    private static void ConfigureDiagnostics()
    {
        var logPath = EnvironmentVariables.Get(EnvironmentVariables.Diagnostics.GuiLogFile);
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        try
        {
            // Default the 2D-map trace ON when a log is requested; an explicit value (incl. "0") wins.
            // Must precede any WorldMapControl code that reads Map2DProfilerTrace's one-shot static gate.
            if (EnvironmentVariables.Get(EnvironmentVariables.Map2D.Trace) is null)
            {
                EnvironmentVariables.Set(EnvironmentVariables.Map2D.Trace, EnvironmentVariables.Enabled);
            }

            var fullPath = Path.GetFullPath(logPath);
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var logger = Logger.Instance;
            logger.UseSpectre = false;       // plain text — no Spectre/console in a windowed app
            logger.IncludeTimestamp = true;
            logger.IncludeLevel = true;
            if (EnvironmentVariables.IsEnabled(EnvironmentVariables.Diagnostics.GuiLogVerbose))
            {
                logger.SetVerbose(true);
            }

            logger.SetLogFile(fullPath);
            logger.Info(
                "[GuiEntryPoint] Diagnostics log started (map2d-trace={0}, verbose={1}).",
                EnvironmentVariables.IsEnabled(EnvironmentVariables.Map2D.Trace) ? "on" : "off",
                EnvironmentVariables.IsEnabled(EnvironmentVariables.Diagnostics.GuiLogVerbose) ? "on" : "off");
            Console.WriteLine($"[FalloutXbox360Utils] GUI diagnostics log: {fullPath}");
        }
        catch (Exception ex)
        {
            // Never let diagnostics setup break startup; the console fallback above is best-effort.
            Console.Error.WriteLine($"[FalloutXbox360Utils] Failed to open GUI diagnostics log: {ex.Message}");
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        CrashLog(
            $"Unhandled domain exception (IsTerminating={e.IsTerminating})",
            e.ExceptionObject as Exception,
            e.IsTerminating);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashLog("Unobserved task exception", e.Exception, isTerminating: false);
        e.SetObserved();
    }

    /// <summary>
    ///     Writes a fatal/unhandled exception to BOTH the console (for terminal launches) and the
    ///     GUI log file (<c>%TEMP%\FalloutXbox360Utils-gui.log</c>) so the cause survives a crash —
    ///     a windowed app has no console, so console-only logging lost every background-thread crash.
    ///     Includes the managed-heap size, the cheapest signal that distinguishes a managed OOM from
    ///     a GPU/native fault.
    /// </summary>
    private static void CrashLog(string context, Exception? ex, bool isTerminating)
    {
        long managedHeapMb;
        try { managedHeapMb = GC.GetTotalMemory(false) / (1024 * 1024); }
        catch { managedHeapMb = -1; }

        var detail = ex?.ToString() ?? "(no exception object)";
        Console.WriteLine($"[FATAL] {context}: {ex?.GetType().Name ?? "native"} (managed heap {managedHeapMb} MB)");
        Console.WriteLine(detail);

        try
        {
            var log = FalloutXbox360Utils.Core.Logger.Instance;
            log.Error("[FATAL] {0} (managed heap {1} MB, terminating={2}):\n{3}",
                context, managedHeapMb, isTerminating, detail);
        }
        catch
        {
            // Never throw from a crash handler — the console line above is the fallback.
        }
    }

    // --- Native (SEH) fault logging: DO NOT add a vectored exception handler here. ------------
    // A previous revision registered a MANAGED delegate via AddVectoredExceptionHandler(1, …) to
    // log native faults (AV/fastfail/stowed) before process teardown. That handler was itself a
    // process killer: Windows invokes vectored handlers for EVERY hardware exception, including
    // ones raised on threads executing managed code (cooperative GC mode). Entering a managed
    // callback from a cooperative-mode thread is an illegal reverse-P/Invoke transition, so
    // coreclr!ReversePInvokeBadTransition fail-fasts the process (exit 0x80131506) BEFORE the
    // filter body can inspect or ignore the exception code. On CET/shadow-stack hardware (default
    // for .NET on supported CPUs) this fired on every GC suspension hijack —
    // STATUS_RETURN_ADDRESS_HIJACK_ATTEMPT (0x80000033), a benign exception coreclr itself fixes
    // up — turning routine GCs under allocation-heavy streaming into instant silent crashes
    // (confirmed via fallout-pageheap.57000.dmp). It would likewise turn any recoverable managed
    // AV into process death. Native-fault forensics belong to WER LocalDumps,
    // DOTNET_DbgEnableMiniDump, and the Application event log — never an in-process managed VEH.

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);
}
#endif
