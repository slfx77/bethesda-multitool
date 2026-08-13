#if WINDOWS_GUI
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core;
using UnhandledExceptionEventArgs = System.UnhandledExceptionEventArgs;

namespace BethesdaMultitool;

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

        // Open the GUI log file BEFORE Application.Start so a crash anywhere in app construction
        // (XAML metadata, App ctor, first window) is already being captured. Runs BEFORE anything
        // reads the Map2DProfilerTrace static gate so its auto-enable below takes effect.
        ConfigureDiagnostics();

        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Console.WriteLine("[BethesdaMultitool] Starting GUI mode...");
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
    ///     Opens the GUI log file — ALWAYS, before <c>Application.Start</c>. The default sink is a
    ///     per-process file (<c>%TEMP%\BethesdaMultitool-gui-&lt;pid&gt;.log</c>) so concurrent GUI
    ///     sessions stop interleaving into one shared file, and timestamps are forced on so a log
    ///     can be matched against a WER crash record's fault time — the old opt-in, shared,
    ///     timestamp-less log could not be correlated with the 0xC000027B WER dump it accompanied.
    ///     When <c>FALLOUT_GUI_LOG</c> names an explicit path, that path is used instead and the
    ///     2D-map cache/viewport/stream trace (<c>FALLOUT_MAP2D_TRACE</c>) is auto-enabled unless
    ///     the caller set it, mirroring the BethesdaMap2DProfiler's logger setup. Never throws.
    /// </summary>
    private static void ConfigureDiagnostics()
    {
        var logPath = EnvironmentVariables.Get(EnvironmentVariables.Diagnostics.GuiLogFile);
        var explicitPath = !string.IsNullOrWhiteSpace(logPath);

        try
        {
            // Default the 2D-map trace ON when a log is explicitly requested; an explicit value
            // (incl. "0") wins. Must precede any WorldMapControl code that reads Map2DProfilerTrace's
            // one-shot static gate. The always-on default log below does NOT auto-enable the trace —
            // it is a crash-capture sink, not a profiling run.
            if (explicitPath && EnvironmentVariables.Get(EnvironmentVariables.Map2D.Trace) is null)
            {
                EnvironmentVariables.Set(EnvironmentVariables.Map2D.Trace, EnvironmentVariables.Enabled);
            }

            var fullPath = explicitPath
                ? Path.GetFullPath(logPath!)
                : Path.Combine(Path.GetTempPath(), $"BethesdaMultitool-gui-{Environment.ProcessId}.log");
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var logger = Logger.Instance;
            if (explicitPath)
            {
                logger.UseSpectre = false;   // plain text — no Spectre/console in a windowed app
            }

            // Timestamps are NOT optional: without them a log line cannot be matched to a WER
            // fault time, which is exactly what made the last crash undiagnosable.
            logger.IncludeTimestamp = true;
            logger.IncludeLevel = true;
            if (EnvironmentVariables.IsEnabled(EnvironmentVariables.Diagnostics.GuiLogVerbose))
            {
                logger.SetVerbose(true);
            }

            logger.SetLogFile(fullPath);
            logger.Info(
                "[GuiEntryPoint] Diagnostics log started (pid={0}, map2d-trace={1}, verbose={2}).",
                Environment.ProcessId,
                EnvironmentVariables.IsEnabled(EnvironmentVariables.Map2D.Trace) ? "on" : "off",
                EnvironmentVariables.IsEnabled(EnvironmentVariables.Diagnostics.GuiLogVerbose) ? "on" : "off");
            Console.WriteLine($"[BethesdaMultitool] GUI diagnostics log: {fullPath}");
        }
        catch (Exception ex)
        {
            // Never let diagnostics setup break startup; the console fallback above is best-effort.
            Console.Error.WriteLine($"[BethesdaMultitool] Failed to open GUI diagnostics log: {ex.Message}");
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
    ///     GUI log file (<c>%TEMP%\BethesdaMultitool-gui-&lt;pid&gt;.log</c>) so the cause survives a crash —
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
        // Stowed/COM crashes bury the actual failure in the HRESULT + WinRT restricted-error info;
        // surface both alongside the managed detail (null when the exception carries neither).
        var winRtDetail = WinRtErrorInfo.Describe(ex);
        Console.WriteLine($"[FATAL] {context}: {ex?.GetType().Name ?? "native"} (managed heap {managedHeapMb} MB)");
        if (winRtDetail is not null)
        {
            Console.WriteLine($"[FATAL] {winRtDetail}");
        }
        Console.WriteLine(detail);

        try
        {
            var log = BethesdaMultitool.Core.Diagnostics.Logger.Instance;
            log.Error("[FATAL] {0} (managed heap {1} MB, terminating={2}):\n{3}",
                context, managedHeapMb, isTerminating, detail);
            if (winRtDetail is not null)
            {
                log.Error("[FATAL] {0}", winRtDetail);
            }
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
