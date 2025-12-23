using Microsoft.UI.Xaml;
using System;

namespace Xbox360MemoryCarver.App;

/// <summary>
/// Main application window with tabbed interface.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        try
        {
            Console.WriteLine("[MainWindow] Constructor starting...");
            this.InitializeComponent();
            Console.WriteLine("[MainWindow] InitializeComponent complete");

            // Set minimum window size
            var appWindow = this.AppWindow;
            appWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));

            // Center the window
            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (displayArea != null)
            {
                var centeredPosition = new Windows.Graphics.PointInt32(
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
