using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Microsoft.UI.Xaml;
using UnhandledExceptionEventArgs = Microsoft.UI.Xaml.UnhandledExceptionEventArgs;

namespace FalloutXbox360Utils;

/// <summary>
///     Provides application-specific behavior to supplement the default Application class.
/// </summary>
public sealed partial class FalloutApp : Application
{
    private const int ATTACH_PARENT_PROCESS = -1;
    private GpuDevice? _gpuDevice;
    private bool _gpuDeviceCreated;

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
    ///     Lazily-created shared GPU device for live rendering surfaces (e.g. the v3 3D world
    ///     view). Returns null on machines with no D3D11 backend. CLI render commands continue
    ///     to manage their own short-lived devices independently.
    /// </summary>
    internal GpuDevice? GetOrCreateGpuDevice()
    {
        if (_gpuDeviceCreated) return _gpuDevice;
        _gpuDevice = GpuDevice.Create();
        _gpuDeviceCreated = true;
        return _gpuDevice;
    }

    // Console attachment for debug output when launched from terminal

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Console.WriteLine($"[CRASH] Unhandled exception: {e.Exception}");
        Console.WriteLine($"[CRASH] Message: {e.Message}");
        PrintInnerExceptions(e.Exception);
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
            MainWindow.Closed += (_, _) =>
            {
                _gpuDevice?.Dispose();
                _gpuDevice = null;
            };
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
