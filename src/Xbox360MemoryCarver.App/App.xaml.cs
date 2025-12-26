using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Xbox360MemoryCarver.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    // Console attachment for debug output when launched from terminal
#pragma warning disable SYSLIB1054 // Use LibraryImport - we keep DllImport to avoid requiring /unsafe
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);
#pragma warning restore SYSLIB1054

    private const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>
    /// Gets the current application instance.
    /// </summary>
    public static new App Current => (App)Application.Current;

    /// <summary>
    /// Gets the main application window.
    /// </summary>
    public Window? MainWindow => _window;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        // Attach to parent console if launched from terminal
        AttachConsole(ATTACH_PARENT_PROCESS);
        Console.WriteLine("[Xbox360MemoryCarver] Application starting...");

        // Global unhandled exception handler
        this.UnhandledException += App_UnhandledException;

        try
        {
            this.InitializeComponent();
            Console.WriteLine("[Xbox360MemoryCarver] App initialized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] App.InitializeComponent failed: {ex}");
            throw;
        }
    }

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private static void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Console.WriteLine($"[CRASH] Unhandled exception: {e.Exception}");
        Console.WriteLine($"[CRASH] Message: {e.Message}");
        e.Handled = false; // Let it crash but we logged it
    }
#pragma warning restore RCS1163

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Console.WriteLine("[Xbox360MemoryCarver] Creating main window...");
            _window = new MainWindow();
            Console.WriteLine("[Xbox360MemoryCarver] Activating main window...");
            _window.Activate();
            Console.WriteLine("[Xbox360MemoryCarver] Main window activated");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] OnLaunched failed: {ex}");
            throw;
        }
    }
}
