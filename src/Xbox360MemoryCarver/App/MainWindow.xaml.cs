using Windows.Graphics;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Xbox360MemoryCarver;

/// <summary>
///     Main application window with NavigationView sidebar.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        Instance = this;
        try
        {
            Console.WriteLine("[MainWindow] Constructor starting...");
            InitializeComponent();
            Console.WriteLine("[MainWindow] InitializeComponent complete");

            // Set minimum window size
            var appWindow = AppWindow;
            appWindow.Resize(new SizeInt32(1450, 900));

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

            Console.WriteLine("[MainWindow] Constructor complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] MainWindow constructor failed: {ex}");
            throw;
        }
    }

    public static MainWindow? Instance { get; private set; }

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

        // Configure caption button colors based on theme
        UpdateCaptionButtonColors();

        // Listen for theme changes
        if (Content is FrameworkElement rootElement)
            rootElement.ActualThemeChanged += (s, e) => UpdateCaptionButtonColors();

        Console.WriteLine("[MainWindow] Title bar extended with custom drag region");
    }

    private void UpdateCaptionButtonColors()
    {
        var titleBar = AppWindow.TitleBar;
        if (titleBar == null) return;

        // Detect current theme
        var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark
                     || ((Content as FrameworkElement)?.ActualTheme == ElementTheme.Default
                         && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        if (isDark)
        {
            // Dark theme - light buttons
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            // Light theme - dark buttons
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xC0, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x10, 0x00, 0x00, 0x00);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x80, 0x00, 0x00, 0x00);
        }

        // Transparent backgrounds for both themes (Mica shows through)
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem selectedItem)
        {
            var tag = selectedItem.Tag?.ToString();

            // Hide all content
            SingleFileTabContent.Visibility = Visibility.Collapsed;
            BatchModeTabContent.Visibility = Visibility.Collapsed;
            NifConverterTabContent.Visibility = Visibility.Collapsed;
            DdxConverterTabContent.Visibility = Visibility.Collapsed;

            // Clear status bar when switching tabs
            SetStatus("");

            // Show selected content
            switch (tag)
            {
                case "SingleFile":
                    SingleFileTabContent.Visibility = Visibility.Visible;
                    break;
                case "BatchMode":
                    BatchModeTabContent.Visibility = Visibility.Visible;
                    break;
                case "NifConverter":
                    NifConverterTabContent.Visibility = Visibility.Visible;
                    break;
                case "DdxConverter":
                    DdxConverterTabContent.Visibility = Visibility.Visible;
                    break;
            }

            Console.WriteLine($"[MainWindow] Navigated to: {tag}");
        }
    }

    /// <summary>
    ///     Updates the global status bar text.
    /// </summary>
    public void SetStatus(string message)
    {
        GlobalStatusTextBlock.Text = message;
    }
}
