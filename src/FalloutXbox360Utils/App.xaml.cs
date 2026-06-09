using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace FalloutXbox360Utils;

/// <summary>
///     Provides application-specific behavior to supplement the default Application class.
/// </summary>
public sealed partial class FalloutApp : Application
{
    private const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>
    ///     Initializes the singleton application object.
    /// </summary>
    public FalloutApp()
    {
        // Attach to parent console if launched from terminal
        AttachConsole(ATTACH_PARENT_PROCESS);
        Console.WriteLine("[FalloutXbox360Utils] Application starting...");

        // Global unhandled exception handler
        UnhandledException += App_UnhandledException;

        try
        {
            // The XAML markup compiler emits InitializeComponent into the partial class
            // (it also adds IXamlMetadataProvider, which WinUI needs in order to resolve
            // any XAML type at runtime). Calling it here both loads App.xaml resources
            // and registers the metadata provider on Application.Current.
            InitializeComponent();
            Console.WriteLine("[FalloutXbox360Utils] App initialized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] App.InitializeComponent failed: {ex}");
            throw;
        }
    }

    /// <summary>
    ///     Gets the current application instance.
    /// </summary>
    public new static FalloutApp Current => (FalloutApp)Application.Current;

    /// <summary>
    ///     Gets the main application window.
    /// </summary>
    public Window? MainWindow { get; private set; }

    /// <summary>
    ///     Optional companion-app launch hook. When set, the root Fallout application still owns
    ///     WinUI metadata/resources, but a specialized host window can replace the normal shell.
    /// </summary>
    internal static Func<Window>? LaunchWindowFactory { get; set; }

    // Console attachment for debug output when launched from terminal

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // A windowed WinUI 3 app has no visible console (see MainWindow's log-file comment), so
        // Console.WriteLine here lands nowhere the user can inspect — every UI-thread crash was
        // dying with the stack trace written only to a detached console. Route the full exception
        // (message + HRESULT + inner chain + stack) through the file logger so it survives in
        // %TEMP%\FalloutXbox360Utils-gui.log. AutoFlush is on, so it persists even as the process
        // tears down. Console output is kept for the terminal-launched case.
        Console.WriteLine($"[CRASH] Unhandled exception: {e.Exception}");
        Console.WriteLine($"[CRASH] Message: {e.Message}");
        PrintInnerExceptions(e.Exception);

        try
        {
            var log = FalloutXbox360Utils.Core.Logger.Instance;
            log.Error("[CRASH] Unhandled XAML exception: {0}", e.Message);
            LogInnerExceptions(log, e.Exception);
        }
        catch
        {
            // Never throw from the crash handler — the console lines above are the fallback.
        }

        e.Handled = false; // Let it crash but we logged it
    }

    /// <summary>
    ///     File-logger counterpart to <see cref="PrintInnerExceptions" />. Walks the inner-exception
    ///     chain, recording each layer's HRESULT, type, message, and stack trace to the persistent
    ///     GUI log so a UI-thread crash can be diagnosed post-mortem without a debugger attached.
    /// </summary>
    private static void LogInnerExceptions(FalloutXbox360Utils.Core.Logger log, Exception? ex)
    {
        var depth = 0;
        while (ex != null)
        {
            log.Error("[CRASH] [{0}] HRESULT=0x{1:X8} {2}: {3}",
                depth, ex.HResult, ex.GetType().FullName ?? "(unknown)", ex.Message);
            if (ex.StackTrace != null)
            {
                log.Error("[CRASH] [{0}] StackTrace:\n{1}", depth, ex.StackTrace);
            }

            ex = ex.InnerException;
            depth++;
        }
    }

    /// <summary>
    ///     Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Console.WriteLine("[FalloutXbox360Utils] Creating main window...");
            MainWindow = LaunchWindowFactory?.Invoke() ?? new MainWindow();
            Console.WriteLine("[FalloutXbox360Utils] Activating main window...");
            MainWindow.Activate();
            Console.WriteLine("[FalloutXbox360Utils] Main window activated");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] OnLaunched failed: {ex}");
            PrintInnerExceptions(ex);
            throw;
        }
    }

    /// <summary>
    ///     WinRT often wraps the actual XAML failure in an outer XamlParseException whose
    ///     Message is "The text associated with this error code could not be found." The real
    ///     diagnostic lives in the HResult and any chained inner exception. Print both so the
    ///     log captures actionable information.
    /// </summary>
    internal static void PrintInnerExceptions(Exception? ex)
    {
        var depth = 0;
        while (ex != null)
        {
            Console.WriteLine($"[CRASH] [{depth}] HRESULT=0x{ex.HResult:X8} {ex.GetType().FullName}: {ex.Message}");
            if (ex.StackTrace != null && depth > 0)
            {
                Console.WriteLine($"[CRASH] [{depth}] StackTrace: {ex.StackTrace}");
            }

            ex = ex.InnerException;
            depth++;
        }
    }
}
