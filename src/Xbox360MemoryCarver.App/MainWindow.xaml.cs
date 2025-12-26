using Windows.Graphics;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Main application window with tabbed interface.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        try
        {
            Console.WriteLine("[MainWindow] Constructor starting...");
            InitializeComponent();
            Console.WriteLine("[MainWindow] InitializeComponent complete");

            // Set minimum window size
            var appWindow = AppWindow;
            appWindow.Resize(new SizeInt32(1400, 900));

            // Center the window
            var displayArea = DisplayArea.GetFromWindowId(
                appWindow.Id, DisplayAreaFallback.Nearest);
            if (displayArea != null)
            {
                var centeredPosition = new PointInt32(
                    (displayArea.WorkArea.Width - appWindow.Size.Width) / 2,
                    (displayArea.WorkArea.Height - appWindow.Size.Height) / 2);
                appWindow.Move(centeredPosition);
            }

            Console.WriteLine("[MainWindow] Constructor complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] MainWindow constructor failed: {ex}");
            throw;
        }
    }
}
