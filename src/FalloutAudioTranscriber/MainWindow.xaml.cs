using Windows.Graphics;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FalloutAudioTranscriber;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        Instance = this;
        InitializeComponent();

        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(1200, 800));

        // Center window
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        if (displayArea != null)
        {
            var center = new PointInt32(
                (displayArea.WorkArea.Width - appWindow.Size.Width) / 2,
                (displayArea.WorkArea.Height - appWindow.Size.Height) / 2);
            appWindow.Move(center);
        }

        TrySetMicaBackdrop();
        SetupTitleBar();

        // Shift title bar margin synchronously when IsPaneOpen changes —
        // fires before the animation starts, unlike PaneOpened/PaneClosed.
        NavView.RegisterPropertyChangedCallback(
            NavigationView.IsPaneOpenProperty, OnIsPaneOpenChanged);

        // Wire up the loading view's completion event
        LoadingViewContent.BuildLoaded += OnBuildLoaded;
    }

    public static MainWindow? Instance { get; private set; }

    private void TrySetMicaBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
    }

    private void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Set window/taskbar icon from .ico file
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            AppWindow.SetIcon(iconPath);
        }

        UpdateCaptionButtonColors();

        if (Content is FrameworkElement rootElement)
        {
            rootElement.ActualThemeChanged += (_, _) => UpdateCaptionButtonColors();
        }
    }

    private void UpdateCaptionButtonColors()
    {
        var titleBar = AppWindow.TitleBar;
        if (titleBar == null)
        {
            return;
        }

        var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark
                     || ((Content as FrameworkElement)?.ActualTheme == ElementTheme.Default
                         && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        if (isDark)
        {
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(0x20, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(0xC0, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(0x10, 0x00, 0x00, 0x00);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0x80, 0x00, 0x00, 0x00);
        }

        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }

    private void OnIsPaneOpenChanged(DependencyObject sender, DependencyProperty dp)
    {
        var nav = (NavigationView)sender;
        AppTitleBar.Margin = new Thickness(nav.IsPaneOpen ? nav.OpenPaneLength : 48, 0, 0, 0);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            LoadingViewContent.Visibility = tag == "Loading" ? Visibility.Visible : Visibility.Collapsed;
            PlaylistViewContent.Visibility = tag == "Playlist" ? Visibility.Visible : Visibility.Collapsed;
            SetStatus("");
        }
    }

    private void OnBuildLoaded(object? sender, EventArgs e)
    {
        NavPlaylist.IsEnabled = true;
        NavView.SelectedItem = NavPlaylist;

        if (LoadingViewContent.LoadResult != null)
        {
            PlaylistViewContent.SetBuildResult(
                LoadingViewContent.LoadResult,
                LoadingViewContent.DataDirectory);
        }
    }

    public void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }
}
