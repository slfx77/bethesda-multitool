using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace FalloutXbox360Utils;

/// <summary>
///     Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>
    ///     Initializes the singleton application object.
    /// </summary>
    public App()
    {
        // Attach to parent console if launched from terminal
        AttachConsole(ATTACH_PARENT_PROCESS);
        Console.WriteLine("[FalloutXbox360Utils] Application starting...");

        // Global unhandled exception handler
        UnhandledException += App_UnhandledException;

        try
        {
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
    public new static App Current => (App)Application.Current;

    /// <summary>
    ///     Gets the main application window.
    /// </summary>
    public Window? MainWindow { get; private set; }

    // Console attachment for debug output when launched from terminal

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Console.WriteLine($"[CRASH] Unhandled exception: {e.Exception}");
        Console.WriteLine($"[CRASH] Message: {e.Message}");
        e.Handled = false; // Let it crash but we logged it
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
            MainWindow = new MainWindow();
            Console.WriteLine("[FalloutXbox360Utils] Activating main window...");
            MainWindow.Activate();
            Console.WriteLine("[FalloutXbox360Utils] Main window activated");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] OnLaunched failed: {ex}");
            throw;
        }
    }
}
