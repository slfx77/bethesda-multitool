#if WINDOWS_GUI
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinRT;

namespace Xbox360MemoryCarver;

/// <summary>
///     Windows GUI entry point for WinUI 3 application.
/// </summary>
public static class GuiEntryPoint
{
    private const int ATTACH_PARENT_PROCESS = -1;

    public static int Run(string[] args)
    {
        // Attach to console early for crash logging
        AttachConsole(ATTACH_PARENT_PROCESS);

        // Set up global exception handlers
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Console.WriteLine("[Xbox360MemoryCarver] Starting GUI mode...");
            ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                _ = p;
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] Application crashed: {ex.GetType().Name}");
            Console.WriteLine($"[FATAL] Message: {ex.Message}");
            Console.WriteLine($"[FATAL] Stack trace:\n{ex.StackTrace}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"[FATAL] Inner exception: {ex.InnerException.GetType().Name}");
                Console.WriteLine($"[FATAL] Inner message: {ex.InnerException.Message}");
            }

            return 1;
        }
    }

    private static void OnUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Console.WriteLine($"[FATAL] Unhandled domain exception: {ex?.GetType().Name ?? "Unknown"}");
        Console.WriteLine($"[FATAL] Message: {ex?.Message ?? "No message"}");
        Console.WriteLine($"[FATAL] IsTerminating: {e.IsTerminating}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Console.WriteLine($"[ERROR] Unobserved task exception: {e.Exception.GetType().Name}");
        Console.WriteLine($"[ERROR] Message: {e.Exception.Message}");
        e.SetObserved();
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);
#pragma warning restore SYSLIB1054
}
#endif
