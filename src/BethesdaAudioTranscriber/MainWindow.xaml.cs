using Windows.Graphics;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BethesdaAudioTranscriber;

public sealed partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _closeInProgress;
    private bool _preserveInitialPlaylistStatus;

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
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;
    }

    public static MainWindow? Instance { get; private set; }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeApproved)
        {
            return;
        }

        args.Cancel = true;
        if (_closeInProgress)
        {
            return;
        }

        _closeInProgress = true;
        try
        {
            // Let the native Closing event observe Cancel even when there is nothing to await.
            await Task.Yield();
            await PlaylistViewContent.PrepareForCloseAsync();
            _closeApproved = true;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not close: {ex.Message}");
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        AppWindow.Closing -= AppWindow_Closing;
        Closed -= MainWindow_Closed;
        LoadingViewContent.BuildLoaded -= OnBuildLoaded;
        Instance = null;
    }

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
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x20, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = Color.FromArgb(0xC0, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x10, 0x00, 0x00, 0x00);
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0x80, 0x00, 0x00, 0x00);
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
            if (tag == "Playlist" && _preserveInitialPlaylistStatus)
            {
                _preserveInitialPlaylistStatus = false;
                if (PlaylistViewContent.WhisperInitializationStatus is { } status) SetStatus(status);
            }
            else SetStatus("");
        }
    }

    private async void OnBuildLoaded(object? sender, EventArgs e)
    {
        if (_closeInProgress || _closeApproved)
        {
            return;
        }

        try
        {
            if (LoadingViewContent.LoadResult != null)
            {
                await PlaylistViewContent.SetBuildResultAsync(
                    LoadingViewContent.LoadResult,
                    LoadingViewContent.DataDirectory);
                NavPlaylist.IsEnabled = true;
                if (!ReferenceEquals(NavView.SelectedItem, NavPlaylist))
                {
                    _preserveInitialPlaylistStatus = true;
                    NavView.SelectedItem = NavPlaylist;
                }
                else if (PlaylistViewContent.WhisperInitializationStatus is { } status)
                {
                    SetStatus(status);
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load transcription project: {ex.Message}");
        }
    }

    public void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }
}
