#if WINDOWS_GUI
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;
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

        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        InstallNativeExceptionLogger();

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
    private static void CrashLog(string context, Exception? ex, bool isTerminating, string? extraStack = null)
    {
        long managedHeapMb;
        try { managedHeapMb = GC.GetTotalMemory(false) / (1024 * 1024); }
        catch { managedHeapMb = -1; }

        var detail = ex?.ToString() ?? extraStack ?? "(no exception object)";
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

    // --- Native (SEH) fault logging ----------------------------------------------------------
    // .NET does NOT deliver corrupted-state exceptions (access violations, stack overflow,
    // __fastfail) to AppDomain.UnhandledException, so a native fault in the D3D12 runtime/driver
    // (use-after-free of a GPU resource, OOB write into a mapped buffer, a device hang faulting in
    // the present/composition path) kills the process with NO managed log. A vectored exception
    // handler runs in the faulting thread's context BEFORE the OS tears the process down, so it can
    // record the fault code + address + the managed stack (which names the exact D3D call that
    // faulted) to the GUI log, then let the crash proceed.

    private const uint ExceptionAccessViolation = 0xC0000005;
    private const uint ExceptionStackOverflow = 0xC00000FD;
    private const uint ExceptionFastFail = 0xC0000409;       // __fastfail (heap corruption, GSL checks)
    private const uint ExceptionIllegalInstruction = 0xC000001D;
    private const int ExceptionContinueSearch = 0;            // log only; let normal teardown continue

    // Keep the delegate alive for the process lifetime — GC must not collect the thunk the OS holds.
    private static VectoredHandler? s_vectoredHandler;

    private static void InstallNativeExceptionLogger()
    {
        try
        {
            s_vectoredHandler = VectoredExceptionFilter;
            // First handler (1) so we log before anything else swallows it.
            AddVectoredExceptionHandler(1, s_vectoredHandler);
        }
        catch
        {
            // Diagnostics-only; never fail startup over it.
        }
    }

    private static int VectoredExceptionFilter(IntPtr exceptionPointers)
    {
        try
        {
            if (exceptionPointers == IntPtr.Zero)
            {
                return ExceptionContinueSearch;
            }

            // EXCEPTION_POINTERS.ExceptionRecord is the first pointer-sized field.
            var record = Marshal.ReadIntPtr(exceptionPointers);
            if (record == IntPtr.Zero)
            {
                return ExceptionContinueSearch;
            }

            // EXCEPTION_RECORD: ExceptionCode @0 (DWORD), ExceptionAddress @16 (x64 alignment).
            var code = (uint)Marshal.ReadInt32(record);
            if (code is not (ExceptionAccessViolation or ExceptionStackOverflow
                or ExceptionFastFail or ExceptionIllegalInstruction))
            {
                // Ignore first-chance CLR exceptions (0xE0434352) and everything non-fatal.
                return ExceptionContinueSearch;
            }

            var address = Marshal.ReadIntPtr(record, 16);
            var name = code switch
            {
                ExceptionAccessViolation => "ACCESS_VIOLATION",
                ExceptionStackOverflow => "STACK_OVERFLOW",
                ExceptionFastFail => "FASTFAIL (heap corruption / security check)",
                ExceptionIllegalInstruction => "ILLEGAL_INSTRUCTION",
                _ => "UNKNOWN"
            };

            // Stack overflow leaves too little stack to walk safely; skip the managed trace there.
            var managedStack = code == ExceptionStackOverflow
                ? "(stack overflow — managed trace omitted)"
                : SafeStackTrace();

            CrashLog(
                $"NATIVE FAULT 0x{code:X8} ({name}) at 0x{address.ToInt64():X}",
                ex: null,
                isTerminating: true,
                extraStack: managedStack);
        }
        catch
        {
            // The process is already faulting — never make it worse.
        }

        return ExceptionContinueSearch;
    }

    private static string SafeStackTrace()
    {
        try { return Environment.StackTrace; }
        catch { return "(managed stack unavailable)"; }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);

    private delegate int VectoredHandler(IntPtr exceptionPointers);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);
}
#endif
