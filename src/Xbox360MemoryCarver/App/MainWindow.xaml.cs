using Windows.Graphics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Xbox360MemoryCarver;

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

            // Apply Mica backdrop
            TrySetMicaBackdrop();

            // Extend content into title bar and set up drag region
            SetupTitleBar();

            // Handle tab selection changes
            MainTabView.SelectionChanged += MainTabView_SelectionChanged;

            Console.WriteLine("[MainWindow] Constructor complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] MainWindow constructor failed: {ex}");
            throw;
        }
    }

    private void TrySetMicaBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
            Console.WriteLine("[MainWindow] Mica backdrop applied");
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            Console.WriteLine("[MainWindow] Acrylic backdrop applied (Mica not supported)");
        }
        else
        {
            Console.WriteLine("[MainWindow] No system backdrop supported");
        }
    }

    private void SetupTitleBar()
    {
        // Extend content into the title bar
        ExtendsContentIntoTitleBar = true;

        // Set the custom title bar as the drag region
        SetTitleBar(AppTitleBar);

        Console.WriteLine("[MainWindow] Title bar extended with custom drag region");
    }

    private void MainTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedIndex = MainTabView.SelectedIndex;

        SingleFileTabContent.Visibility = selectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        BatchModeTabContent.Visibility = selectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        NifConverterTabContent.Visibility = selectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }
}
