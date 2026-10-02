using Slfx77.Multitool.Core.Localization;
using Slfx77.Multitool.WinUI.Localization;
using Slfx77.Multitool.WinUI.Settings;
using Slfx77.Multitool.WinUI.Direct3D12;
using Slfx77.Multitool.WinUI.Workflows;
using Windows.Graphics;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Localization;

namespace BethesdaMultitool;

/// <summary>
///     Main application window with NavigationView sidebar.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WinUI owns the window lifetime. AppWindow.Closing awaits DisposeWindowOwnersAsync and retains " +
                    "graphics, localization, chrome and HWND when scene or thumbnail retirement fails; a separate IDisposable path would bypass that barrier.")]
public sealed partial class MainWindow : Window
{
    private Slfx77.Multitool.WinUI.Shell.WindowChrome? _windowChrome;
    private Task? _shutdownTask;
    private Direct3D12Graphics? _nativeGraphics;
    private bool _isClosing;
    private readonly Dictionary<string, SolidColorBrush> _captionShortcutBrushes = new(StringComparer.Ordinal);
    private bool _captionShortcutLayoutPending;
    private int _captionInputDiagnosticsRemaining = 8;
    private int _captionRegionChangeDiagnosticsRemaining = 16;
    private int _captionHelpPointerDiagnosticsRemaining = 64;
    private int _captionButtonPointerDiagnosticsRemaining = 64;
    private int _captionOtherPointerDiagnosticsRemaining = 64;
    private int _captionInputEventDiagnosticsRemaining = 64;
    private int _captionMetricsSkippedDiagnosticsRemaining = 8;
    private InputNonClientPointerSource? _captionDiagnosticSource;
    private InputNonClientPointerSource? _captionInputSource;
    private bool _captionShortcutHovered;
    private bool _captionShortcutPressed;

    /// <summary>The shared navigation owning implemented application destinations.</summary>
    private NavigationView NavView => ApplicationFrame.Navigation;
    /// <summary>The shared Explore parent of this application's supported views.</summary>
    private NavigationViewItem ExploreNavigationItem => ApplicationFrame.ExploreNavigationItem;
    /// <summary>The shared status display for the application's current operation.</summary>
    private TextBlock GlobalStatusTextBlock => ApplicationFrame.StatusElement;


    /// <summary>The display language owned by this window.</summary>
    public DisplayLanguage DisplayLanguage { get; } = new(BethesdaMultitool.Localization.MrtStringCatalog.Create);
    /// <summary>The common binding owner for this window's retained controls.</summary>
    public LocalizationController Localization { get; }

    public MainWindow()
    {
        Instance = this;
        Localization = new LocalizationController(DisplayLanguage, BethesdaMultitool.Core.Localization.EnglishResources.All.Keys);
        try
        {
            // GuiEntryPoint.ConfigureDiagnostics opens the per-process GUI log BEFORE
            // Application.Start, so by the time this constructor runs a file sink is normally
            // already live (and crashes during app construction were captured). This is only a
            // FALLBACK for a host that created the window without going through GuiEntryPoint.
            // It must not reopen when a sink exists: SetLogFile disposes the active writer, and
            // an unconditional reopen here would break the "open before Application.Start"
            // crash-capture guarantee. Same per-process path + forced timestamps as the entry
            // path — a shared, timestamp-less log cannot be matched to a WER fault time.
            try
            {
                if (!BethesdaMultitool.Core.Diagnostics.Logger.Instance.HasLogFile)
                {
                    var logOverride = BethesdaMultitool.Core.EnvironmentVariables.Get(
                        BethesdaMultitool.Core.EnvironmentVariables.Diagnostics.GuiLogFile);
                    var logPath = string.IsNullOrWhiteSpace(logOverride)
                        ? Path.Combine(Path.GetTempPath(), $"BethesdaMultitool-gui-{Environment.ProcessId}.log")
                        : Path.GetFullPath(logOverride);
                    BethesdaMultitool.Core.Diagnostics.Logger.Instance.IncludeTimestamp = true;
                    BethesdaMultitool.Core.Diagnostics.Logger.Instance.SetLogFile(logPath);
                }
            }
            catch
            {
                // File-logging is a diagnostics-only convenience; never fail startup over it.
            }

            Console.WriteLine("[MainWindow] Constructor starting...");
            InitializeComponent();
            // Bind attached properties directly: the pinned UID vocabulary does not include
            // the namespace-qualified AutomationProperties.HelpText resource suffix.
            Localization.Bind(HelpShortcutsButton, Microsoft.UI.Xaml.Automation.AutomationProperties.NameProperty,
                "HelpShortcuts.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name");
            Localization.Bind(HelpShortcutsButton, Microsoft.UI.Xaml.Automation.AutomationProperties.HelpTextProperty,
                "HelpShortcuts.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.HelpText");
            Localization.Bind(HelpShortcutsButton, ToolTipService.ToolTipProperty,
                "HelpShortcuts.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip");
        ApplicationFrame.BindLocalization(Localization);
        NavView.SelectionChanged += NavView_SelectionChanged;
        ApplicationFrame.SettingsNavigationItem.Tag = "Settings";
        ExploreNavigationItem.Tag = "Explore";
            Slfx77.Multitool.WinUI.Localization.Localization.SetContext((FrameworkElement)Content, Localization);
            RecoveryFileTabContent.ConfigureWorkspaceHost(recovery: true);
            var nativeWindow = new WindowWorkflowServices(this, Localization,
                SetStatus, SetLocalizedStatus, ReportNativeWorkflowFailure);
            _nativeGraphics = new Direct3D12Graphics();
            ExploreTabContent.EnableShadowkeyPreview(_nativeGraphics, nativeWindow);
            InitializeExploreNavigation();
            Strings.Display.Changed += OnDisplayLanguageChanged;
            SettingsTabContent.InitializePreferences(Content as FrameworkElement);
            Closed += MainWindow_Closed;
            AppWindow.Closing += MainWindow_Closing;
            Console.WriteLine("[MainWindow] InitializeComponent complete");

            // Memory budget hygiene: periodic CPU-cache budget checks (FALLOUT_MEMORY_BUDGET_MB,
            // default 3 GB) + aggressive trims under real GC pressure. No-op when
            // FALLOUT_MEMORY_DISABLE=1.
            BethesdaMultitool.Core.Diagnostics.MemoryBudgetCoordinator.Instance.Start();

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

            _windowChrome = new Slfx77.Multitool.WinUI.Shell.WindowChrome(this, ApplicationFrame);
            ApplicationFrame.TitleBar.LayoutUpdated += CaptionShortcut_LayoutUpdated;
            ApplicationFrame.ActualThemeChanged += CaptionShortcut_ThemeChanged;
            UpdateCaptionShortcutColors();
            AttachCaptionShortcutInput();
            AttachCaptionPointerDiagnostics();
            Console.WriteLine("[MainWindow] Constructor complete");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CRASH] MainWindow constructor failed: {ex}");
            FalloutApp.PrintInnerExceptions(ex);
            throw;
        }
    }

    public static MainWindow? Instance { get; private set; }

    /// <summary>Keeps the native tree mounted until its asynchronous owners finish teardown.</summary>
    /// <param name="sender">The native window receiving a system close request.</param>
    /// <param name="args">The cancelable close request.</param>
    private void MainWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        if (_isClosing)
        {
            return;
        }
        _isClosing = true;
        ApplicationFrame.IsEnabled = false;
        HelpShortcutsButton.IsEnabled = false;
        _shutdownTask = DisposeWindowOwnersAsync();
        _ = CompleteCloseAsync(_shutdownTask);
    }

    /// <summary>Closes the window once cleanup completes and the original close notification has returned.</summary>
    /// <param name="shutdown">The single retained owner-cleanup operation.</param>
    private async Task CompleteCloseAsync(Task shutdown)
    {
        // Even synchronous cleanup must not destroy the window inside its canceled Closing event.
        await Task.Yield();
        try { await shutdown; }
        catch (Exception exception) { Logger.Instance.Warn("[MainWindow] Teardown failed: {0}", exception); }
        if (!ExploreTabContent.WindowResourcesRetired || _nativeGraphics is not null)
        {
            // A failed scene or thumbnail drain retains the same terminal ownership boundary.
            // Keep its source/window prerequisites and disabled tree; do not imply a release retry.
            SetLocalizedStatus("WindowResources_CloseBlocked.Text");
            return;
        }
        try { Close(); }
        catch (Exception exception) { Logger.Instance.Warn("[MainWindow] Final close failed: {0}", exception); }
    }

    /// <summary>Observes the existing cleanup task, with one fallback for direct programmatic closure.</summary>
    /// <param name="sender">The closing native window.</param>
    /// <param name="args">The native close notification.</param>
    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        Closed -= MainWindow_Closed;
        AppWindow.Closing -= MainWindow_Closing;
        _isClosing = true;
        try { await (_shutdownTask ??= DisposeWindowOwnersAsync()); }
        catch (Exception exception) { Logger.Instance.Warn("[MainWindow] Teardown failed: {0}", exception); }
    }

    /// <summary>Attempts every owner cleanup while releasing preview leases before their source owner.</summary>
    /// <returns>Completion after all owners have been visited and collected failures have been logged.</returns>
    private async Task DisposeWindowOwnersAsync()
    {
        List<Exception> failures = [];
        try { await SettingsTabContent.DisposeAsync(); }
        catch (Exception exception) { failures.Add(exception); }
        try { await ExploreTabContent.DisposeAsync(); }
        catch (Exception exception) { failures.Add(exception); }
        if (!ExploreTabContent.WindowResourcesRetired)
        {
            failures.Add(new InvalidOperationException("Scene or thumbnail retirement is incomplete; its source and window resources remain retained."));
            throw new AggregateException("Preview teardown failed; window closure remains blocked.", failures);
        }
        if (_nativeGraphics is { } graphics)
        {
            try
            {
                graphics.Dispose();
                _nativeGraphics = null;
            }
            catch (Exception exception)
            {
                failures.Add(exception);
                throw new AggregateException("Native scene teardown failed; window closure remains blocked.", failures);
            }
        }
        Strings.Display.Changed -= OnDisplayLanguageChanged;
        ApplicationFrame.TitleBar.LayoutUpdated -= CaptionShortcut_LayoutUpdated;
        ApplicationFrame.ActualThemeChanged -= CaptionShortcut_ThemeChanged;
        try { DetachCaptionPointerDiagnostics(); }
        catch (Exception exception) { failures.Add(exception); }
        try { DetachCaptionShortcutInput(); }
        catch (Exception exception) { failures.Add(exception); }
        try { _windowChrome?.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        try { Localization.Dispose(); }
        catch (Exception exception) { failures.Add(exception); }
        foreach (var exception in failures)
        {
            Logger.Instance.Warn("[MainWindow] Teardown failed: {0}", exception);
        }
    }

    /// <summary>Logs native failures while the exact source owner controls its own visible status.</summary>
    /// <param name="workflow">The shared workflow reporting the failure.</param>
    /// <param name="exception">The original preparation, presentation or capture error.</param>
    private static void ReportNativeWorkflowFailure(string workflow, Exception exception)
    {
        Logger.Instance.Warn("[{0}] {1}", workflow, exception);
    }

    /// <summary>Refreshes text on persistent viewers without reopening sources or changing navigation.</summary>
    /// <param name="sender">The display-language service.</param>
    /// <param name="args">The applied language notification.</param>
    private void OnDisplayLanguageChanged(object? sender, EventArgs args)
    {
        Title = Strings.Get("AppTitle.Text");
        ApplicationFrame.Title = Title;
        ExploreTabContent.RefreshLocalization();
        RecoveryFileTabContent.RefreshLocalization();
        NifConverterTabContent.RefreshLocalization();
    }

    /// <summary>Publishes a keyed global status that can be reformatted without rerunning its operation.</summary>
    /// <param name="key">The UI resource identifier.</param>
    /// <param name="arguments">Immutable status arguments.</param>
    internal void SetLocalizedStatus(string key, params object?[] arguments)
        => RuntimeLocalization.SetText(GlobalStatusTextBlock, key, arguments);

    // ── Global keyboard shortcuts ──

    /// <summary>Matches the shortcut to the native caption geometry after layout or display-scale changes.</summary>
    /// <param name="sender">The title bar completing layout.</param>
    /// <param name="args">The layout notification.</param>
    private void CaptionShortcut_LayoutUpdated(object? sender, object args) => UpdateCaptionShortcutGeometry();

    /// <summary>Sizes and places the overlay from the native caption metrics.</summary>
    /// <remarks>Runs after every XAML layout pass and, queued, after the system replaces its caption regions without one.</remarks>
    private void UpdateCaptionShortcutGeometry()
    {
        if (_isClosing || ApplicationFrame.TitleBar.XamlRoot is not { } root) return;
        var titleBar = AppWindow.TitleBar;
        var scale = root.RasterizationScale;
        // A minimized or transitioning window reports caption metrics that cannot be applied: a right inset of -8
        // physical pixels was read one second after a restore, and a negative GridLength throws. Skip such passes;
        // the next region change or layout pass brings usable values.
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }
            || !double.IsFinite(scale) || scale <= 0 || titleBar.Height <= 0 || titleBar.LeftInset < 0 || titleBar.RightInset < 0)
        {
            LogCaptionMetricsSkipped(titleBar, scale);
            return;
        }
        // This window retains the three standard system buttons. Insets are physical pixels; XAML uses DIPs.
        var width = Math.Max(titleBar.LeftInset, titleBar.RightInset) / (3 * scale);
        var layoutChanged = UpdateCaptionInsets(scale, width);
        // The system draws its caption hover one physical pixel shorter than the reported caption height.
        var height = (titleBar.Height - 1) / scale;
        // The overlay keeps its logical end alignment in both flow directions: XAML mirrors alignment and margins
        // under right-to-left, so only the inset source swaps, as UpdateCaptionInsets does for the padding columns.
        var rtl = ApplicationFrame.TitleBar.FlowDirection == FlowDirection.RightToLeft;
        var captionWidth = (rtl ? titleBar.LeftInset : titleBar.RightInset) / scale;
        var margin = new Thickness(0, 0, captionWidth, 0);
        if (width > 0 && (!double.IsFinite(HelpShortcutsButton.Width) || Math.Abs(HelpShortcutsButton.Width - width) > 0.01))
        {
            HelpShortcutsButton.Width = width;
            layoutChanged = true;
        }
        if (height > 0 && (!double.IsFinite(HelpShortcutsButton.Height) || Math.Abs(HelpShortcutsButton.Height - height) > 0.01))
        {
            HelpShortcutsButton.Height = height;
            layoutChanged = true;
        }
        if (captionWidth > 0 && HelpShortcutsButton.Margin != margin)
        {
            HelpShortcutsButton.Margin = margin;
            layoutChanged = true;
        }
        if (layoutChanged)
            _captionShortcutLayoutPending = true;
        else if (_captionShortcutLayoutPending)
        {
            _captionShortcutLayoutPending = false;
            // The title bar refreshes its own passthrough rectangles on layout; the overlay registers none.
            var bounds = HelpShortcutsButton.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(0, 0, HelpShortcutsButton.ActualWidth, HelpShortcutsButton.ActualHeight));
            Logger.Instance.Debug("[TitleBar] Arranged shortcut: left={0:F2}, right={1:F2}, native content bounds={2:F2}..{3:F2}, scale={4:F2}.",
                bounds.Left, bounds.Right, titleBar.LeftInset / root.RasterizationScale,
                root.Size.Width - titleBar.RightInset / root.RasterizationScale, root.RasterizationScale);
            LogCaptionInputRegions();
        }
    }

    /// <summary>Refreshes the pinned TitleBar template's reserved system-button space after native metrics change.</summary>
    /// <param name="scale">The current physical-pixel to XAML scale.</param>
    /// <param name="shortcutWidth">The overlaid help shortcut's width in DIPs, reserved as native drag space so title content never runs under it.</param>
    /// <returns>Whether padding changed and needs another layout pass.</returns>
    private bool UpdateCaptionInsets(double scale, double shortcutWidth)
    {
        var control = ApplicationFrame.TitleBar;
        // WindowChrome can select Tall after the template's initial inset calculation.
        // Keep the actual reservation current; the optional drag spacer is not caption padding.
        if (VisualTreeHelper.GetChildrenCount(control) != 1
            || VisualTreeHelper.GetChild(control, 0) is not Grid { Name: "PART_LayoutRoot" } layout
            || layout.FindName("LeftPaddingColumn") is not ColumnDefinition left
            || layout.FindName("RightPaddingColumn") is not ColumnDefinition right
            || layout.ColumnDefinitions.Count != 12
            || !ReferenceEquals(layout.ColumnDefinitions[0], left)
            || !ReferenceEquals(layout.ColumnDefinitions[11], right)) return false;

        var native = AppWindow.TitleBar;
        var rtl = control.FlowDirection == FlowDirection.RightToLeft;
        var leftWidth = (rtl ? native.RightInset : native.LeftInset) / scale;
        var rightWidth = (rtl ? native.LeftInset : native.RightInset) / scale;
        // The caller already skips unusable metrics; a GridLength must never be negative.
        if (leftWidth < 0 || rightWidth < 0 || !double.IsFinite(leftWidth) || !double.IsFinite(rightWidth)) return false;
        // The template's minimum drag column reserves the space the overlaid shortcut occupies; nothing in the
        // title bar is a passthrough region, so the caption buttons and the shortcut share native hover handling.
        var inputSeparator = layout.ColumnDefinitions[10];
        var separatorWidth = shortcutWidth > 0 ? shortcutWidth : inputSeparator.Width.Value;
        if (Math.Abs(left.Width.Value - leftWidth) <= 0.01 && Math.Abs(right.Width.Value - rightWidth) <= 0.01
            && Math.Abs(inputSeparator.Width.Value - separatorWidth) <= 0.01)
            return false;

        Logger.Instance.Debug("[TitleBar] Refresh caption padding: left={0:F2}->{1:F2}, right={2:F2}->{3:F2}, separator={4:F2}->{5:F2}, scale={6:F2}.",
            left.Width.Value, leftWidth, right.Width.Value, rightWidth, inputSeparator.Width.Value, separatorWidth, scale);
        left.Width = new GridLength(leftWidth);
        right.Width = new GridLength(rightWidth);
        inputSeparator.Width = new GridLength(separatorWidth);
        return true;
    }

    /// <summary>Records actual physical input partitions after at most eight settled layouts in verbose mode.</summary>
    private void LogCaptionInputRegions()
    {
        if (Logger.Instance.Level < LogLevel.Debug || _captionInputDiagnosticsRemaining <= 0) return;
        _captionInputDiagnosticsRemaining--;
        try
        {
            var source = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            LogCaptionInputRegion(source, NonClientRegionKind.Minimize);
            LogCaptionInputRegion(source, NonClientRegionKind.Caption);
            LogCaptionInputRegion(source, NonClientRegionKind.Passthrough);
            // The overlay claims to lie inside the caption rectangle; log its own physical bounds beside it.
            if (GetCaptionShortcutPhysicalBounds() is { } bounds)
            {
                Logger.Instance.Debug("[TitleBar] Shortcut physical bounds: x={0:F2}, y={1:F2}, width={2:F2}, height={3:F2}.",
                    bounds.X, bounds.Y, bounds.Width, bounds.Height);
            }
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            Logger.Instance.Debug("[TitleBar] Input-region diagnostics unavailable: {0}.", exception.Message);
        }
    }

    /// <summary>Logs only the first eight rectangles; coordinates come directly from the native input API.</summary>
    /// <param name="source">This window's existing non-client input source, borrowed without changing its regions.</param>
    /// <param name="kind">The native region to inspect.</param>
    private static void LogCaptionInputRegion(InputNonClientPointerSource source, NonClientRegionKind kind)
    {
        // An empty kind comes back as null through the projection, not as an empty array.
        var regions = source.GetRegionRects(kind) ?? [];
        Logger.Instance.Debug("[TitleBar] {0} physical input regions: count={1}, showing={2}.",
            kind, regions.Length, Math.Min(regions.Length, 8));
        foreach (var region in regions.Take(8))
        {
            Logger.Instance.Debug("[TitleBar] {0}: x={1}, y={2}, width={3}, height={4}.",
                kind, region.X, region.Y, region.Width, region.Height);
        }
    }

    /// <summary>Mirrors native caption-region pointer state onto the overlaid shortcut and opens it on a caption tap.</summary>
    /// <remarks>The shortcut is not hit-testable by XAML; the system owns the region and reports enter, move, exit, press, release and tap.</remarks>
    private void AttachCaptionShortcutInput()
    {
        // The button's own focus handling resets its common state; re-apply the mirrored one afterwards.
        HelpShortcutsButton.GotFocus += CaptionShortcut_FocusChanged;
        HelpShortcutsButton.LostFocus += CaptionShortcut_FocusChanged;
        try
        {
            _captionInputSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            _captionInputSource.PointerEntered += CaptionInput_PointerMoved;
            _captionInputSource.PointerMoved += CaptionInput_PointerMoved;
            _captionInputSource.PointerExited += CaptionInput_PointerExited;
            _captionInputSource.PointerPressed += CaptionInput_PointerPressed;
            _captionInputSource.PointerReleased += CaptionInput_PointerReleased;
            _captionInputSource.CaptionTapped += CaptionInput_CaptionTapped;
            _captionInputSource.EnteringMoveSize += CaptionInput_EnteringMoveSize;
            _captionInputSource.ExitedMoveSize += CaptionInput_ExitedMoveSize;
            _captionInputSource.RegionsChanged += CaptionInput_RegionsChanged;
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            Logger.Instance.Warn("[TitleBar] Native caption input is unavailable; the shortcut stays keyboard-only: {0}.", exception.Message);
        }
    }

    /// <summary>Releases the native input subscriptions during window teardown.</summary>
    private void DetachCaptionShortcutInput()
    {
        HelpShortcutsButton.GotFocus -= CaptionShortcut_FocusChanged;
        HelpShortcutsButton.LostFocus -= CaptionShortcut_FocusChanged;
        if (_captionInputSource is not { } source) return;
        _captionInputSource = null;
        source.PointerEntered -= CaptionInput_PointerMoved;
        source.PointerMoved -= CaptionInput_PointerMoved;
        source.PointerExited -= CaptionInput_PointerExited;
        source.PointerPressed -= CaptionInput_PointerPressed;
        source.PointerReleased -= CaptionInput_PointerReleased;
        source.CaptionTapped -= CaptionInput_CaptionTapped;
        source.EnteringMoveSize -= CaptionInput_EnteringMoveSize;
        source.ExitedMoveSize -= CaptionInput_ExitedMoveSize;
        source.RegionsChanged -= CaptionInput_RegionsChanged;
    }

    /// <summary>Runs a native caption callback on the UI thread.</summary>
    /// <remarks>The input source's thread affinity is not documented; a callback that arrives elsewhere is queued instead of touching XAML.</remarks>
    /// <param name="action">The state change to apply.</param>
    private void OnCaptionUiThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else if (!DispatcherQueue.TryEnqueue(() => action()))
            Logger.Instance.Debug("[TitleBar] Caption input dropped: the dispatcher queue is shutting down.");
    }

    /// <summary>The shortcut's arranged bounds in physical window pixels, the coordinate space native input reports.</summary>
    /// <returns>The rectangle, or null before the shortcut has been arranged.</returns>
    private Windows.Foundation.Rect? GetCaptionShortcutPhysicalBounds()
    {
        if (HelpShortcutsButton.XamlRoot is not { } root || HelpShortcutsButton.ActualWidth <= 0 || HelpShortcutsButton.ActualHeight <= 0) return null;
        var scale = root.RasterizationScale;
        var bounds = HelpShortcutsButton.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, HelpShortcutsButton.ActualWidth, HelpShortcutsButton.ActualHeight));
        return new Windows.Foundation.Rect(bounds.X * scale, bounds.Y * scale, bounds.Width * scale, bounds.Height * scale);
    }

    /// <summary>Whether a native caption point falls on the shortcut.</summary>
    /// <param name="args">The native pointer event.</param>
    /// <returns>True only for the caption region and a point inside the shortcut's bounds.</returns>
    private bool IsOnCaptionShortcut(NonClientPointerEventArgs args) =>
        args.RegionKind == NonClientRegionKind.Caption && IsInsideCaptionShortcut(args.Point);

    /// <summary>Whether a physical window point lies inside the shortcut's arranged bounds.</summary>
    /// <param name="point">The point in physical window pixels.</param>
    /// <returns>False before the shortcut has been arranged.</returns>
    private bool IsInsideCaptionShortcut(Windows.Foundation.Point point)
    {
        if (GetCaptionShortcutPhysicalBounds() is not { } bounds) return false;
        return point.X >= bounds.X && point.X < bounds.X + bounds.Width && point.Y >= bounds.Y && point.Y < bounds.Y + bounds.Height;
    }

    /// <summary>Applies the mirrored common state; the button cannot derive it because XAML never hit-tests it.</summary>
    private void ApplyCaptionShortcutVisualState()
    {
        var state = "Normal";
        if (_captionShortcutPressed) state = "Pressed";
        else if (_captionShortcutHovered) state = "PointerOver";
        VisualStateManager.GoToState(HelpShortcutsButton, state, true);
    }

    /// <summary>Re-applies the mirrored state after the button's own focus handling returned it to Normal.</summary>
    /// <param name="sender">The shortcut button.</param>
    /// <param name="e">The focus notification.</param>
    private void CaptionShortcut_FocusChanged(object sender, RoutedEventArgs e)
    {
        if (!_isClosing && (_captionShortcutHovered || _captionShortcutPressed)) ApplyCaptionShortcutVisualState();
    }

    /// <summary>Applies the hover state the button cannot derive itself; leaving the shortcut also ends a press.</summary>
    /// <param name="hovered">Whether the native pointer is over the shortcut.</param>
    /// <param name="args">The native event that caused the change, for verbose diagnostics.</param>
    private void SetCaptionShortcutHover(bool hovered, NonClientPointerEventArgs? args)
    {
        if (_isClosing || _captionShortcutHovered == hovered) return;
        _captionShortcutHovered = hovered;
        if (!hovered) _captionShortcutPressed = false;
        ApplyCaptionShortcutVisualState();
        if (args is not null) LogCaptionShortcutPointer(hovered ? "entered" : "exited", args);
    }

    /// <summary>Tracks the pointer across the caption region; the system already cleared any caption button it left.</summary>
    /// <param name="sender">The window's non-client input source.</param>
    /// <param name="args">The native pointer event.</param>
    private void CaptionInput_PointerMoved(InputNonClientPointerSource sender, NonClientPointerEventArgs args) =>
        OnCaptionUiThread(() => SetCaptionShortcutHover(IsOnCaptionShortcut(args), args));

    /// <summary>Clears the shortcut when the pointer leaves the caption region.</summary>
    /// <param name="sender">The window's non-client input source.</param>
    /// <param name="args">The native pointer event.</param>
    private void CaptionInput_PointerExited(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
    {
        if (args.RegionKind == NonClientRegionKind.Caption) OnCaptionUiThread(() => SetCaptionShortcutHover(false, args));
    }

    /// <summary>Shows the pressed state while a pointer is down on the shortcut.</summary>
    /// <param name="sender">The window's non-client input source.</param>
    /// <param name="args">The native pointer event.</param>
    private void CaptionInput_PointerPressed(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
    {
        var raisedOnUiThread = DispatcherQueue.HasThreadAccess;
        OnCaptionUiThread(() =>
        {
            var onShortcut = IsOnCaptionShortcut(args);
            LogCaptionInputEvent("pressed", args.RegionKind, args.Point, args.PointerDeviceType, onShortcut, raisedOnUiThread);
            if (_isClosing || !onShortcut) return;
            _captionShortcutPressed = true;
            _captionShortcutHovered = true;
            ApplyCaptionShortcutVisualState();
        });
    }

    /// <summary>Ends a press when the system reports the release; the tap event decides whether the shortcut opens.</summary>
    /// <remarks>The release is not guaranteed after a caption press (the system's move loop can capture it), so the tap and the move loop's exit also end a press.</remarks>
    /// <param name="sender">The window's non-client input source.</param>
    /// <param name="args">The native pointer event.</param>
    private void CaptionInput_PointerReleased(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
    {
        var raisedOnUiThread = DispatcherQueue.HasThreadAccess;
        OnCaptionUiThread(() =>
        {
            var onShortcut = IsOnCaptionShortcut(args);
            LogCaptionInputEvent("released", args.RegionKind, args.Point, args.PointerDeviceType, onShortcut, raisedOnUiThread);
            if (_isClosing || !_captionShortcutPressed) return;
            _captionShortcutPressed = false;
            _captionShortcutHovered = onShortcut;
            ApplyCaptionShortcutVisualState();
        });
    }

    /// <summary>Observes the notification the system raises for every caption press before its move-size loop.</summary>
    /// <remarks>This is not a drag start, so it never ends a press; the release, the tap or the loop's exit does.</remarks>
    /// <param name="sender">The window's non-client input source.</param>
    /// <param name="args">The native notification.</param>
    private void CaptionInput_EnteringMoveSize(InputNonClientPointerSource sender, EnteringMoveSizeEventArgs args)
    {
        var raisedOnUiThread = DispatcherQueue.HasThreadAccess;
        var operation = args.MoveSizeOperation.ToString();
        double x = args.PointerScreenPoint.X;
        double y = args.PointerScreenPoint.Y;
        OnCaptionUiThread(() => LogCaptionMoveSize("entering move-size", operation, x, y, raisedOnUiThread));
    }

    /// <summary>Ends a press once the system's move-size loop finishes, whether or not a release was reported.</summary>
    /// <param name="sender">The window's non-client input source.</param>
    /// <param name="args">The native notification.</param>
    private void CaptionInput_ExitedMoveSize(InputNonClientPointerSource sender, ExitedMoveSizeEventArgs args)
    {
        var raisedOnUiThread = DispatcherQueue.HasThreadAccess;
        var operation = args.MoveSizeOperation.ToString();
        double x = args.PointerScreenPoint.X;
        double y = args.PointerScreenPoint.Y;
        OnCaptionUiThread(() =>
        {
            LogCaptionMoveSize("exited move-size", operation, x, y, raisedOnUiThread);
            if (_isClosing || !_captionShortcutPressed) return;
            _captionShortcutPressed = false;
            ApplyCaptionShortcutVisualState();
        });
    }

    /// <summary>Opens the shortcuts dialog for a caption tap on the shortcut, the same action as its Click.</summary>
    /// <remarks>A tap ends any press. A mouse still rests on the shortcut afterwards; touch and pen have left it.</remarks>
    /// <param name="sender">The window's non-client input source.</param>
    /// <param name="args">The native tap event.</param>
    private async void CaptionInput_CaptionTapped(InputNonClientPointerSource sender, NonClientCaptionTappedEventArgs args)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            LogCaptionInputEvent("tapped", NonClientRegionKind.Caption, args.Point, args.PointerDeviceType, false, false);
            if (!DispatcherQueue.TryEnqueue(() => CaptionInput_CaptionTapped(sender, args)))
                Logger.Instance.Debug("[TitleBar] Caption tap dropped: the dispatcher queue is shutting down.");
            return;
        }
        var onShortcut = IsInsideCaptionShortcut(args.Point);
        LogCaptionInputEvent("tapped", NonClientRegionKind.Caption, args.Point, args.PointerDeviceType, onShortcut, true);
        if (_isClosing) return;
        _captionShortcutPressed = false;
        if (args.PointerDeviceType != PointerDeviceType.Mouse) _captionShortcutHovered = false;
        ApplyCaptionShortcutVisualState();
        if (!onShortcut) return;
        await ShowShortcutsDialogAsync();
    }

    /// <summary>Re-measures the overlay when the system replaces its caption regions without a XAML layout pass.</summary>
    /// <remarks>The Tall transition and display-scale changes arrive this way; the work is queued so it never runs inside the notification.</remarks>
    /// <param name="sender">The window's non-client input source.</param>
    /// <param name="args">The region-change notification.</param>
    private void CaptionInput_RegionsChanged(InputNonClientPointerSource sender, NonClientRegionsChangedEventArgs args)
    {
        var kinds = args.ChangedRegions ?? [];
        if (_isClosing || !kinds.Any(static kind => kind is NonClientRegionKind.Minimize or NonClientRegionKind.Maximize
                or NonClientRegionKind.Close or NonClientRegionKind.Caption)) return;
        if (!DispatcherQueue.TryEnqueue(RefreshCaptionShortcutGeometryQueued))
            Logger.Instance.Debug("[TitleBar] Caption geometry refresh dropped: the dispatcher queue is shutting down.");
    }

    /// <summary>Runs the queued geometry refresh; a failure there must never end the process, since nothing above it handles it.</summary>
    private void RefreshCaptionShortcutGeometryQueued()
    {
        try
        {
            UpdateCaptionShortcutGeometry();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            Logger.Instance.Warn("[TitleBar] Queued caption geometry refresh failed and was ignored: {0}.", exception.Message);
        }
    }

    /// <summary>Observes hover handoff only in verbose runs, without handling, capturing or rerouting pointer input.</summary>
    private void AttachCaptionPointerDiagnostics()
    {
        if (Logger.Instance.Level < LogLevel.Debug) return;
        try
        {
            _captionDiagnosticSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            _captionDiagnosticSource.PointerEntered += CaptionNative_PointerEntered;
            _captionDiagnosticSource.PointerExited += CaptionNative_PointerExited;
            _captionDiagnosticSource.RegionsChanged += CaptionNative_RegionsChanged;
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            Logger.Instance.Debug("[TitleBar] Native pointer diagnostics unavailable: {0}.", exception.Message);
        }
    }

    /// <summary>Detaches the borrowed input source after the existing window-retirement prerequisite succeeds.</summary>
    private void DetachCaptionPointerDiagnostics()
    {
        if (_captionDiagnosticSource is not { } source) return;
        _captionDiagnosticSource = null;
        try { source.PointerEntered -= CaptionNative_PointerEntered; }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            Logger.Instance.Debug("[TitleBar] Native entry diagnostic detach unavailable: {0}.", exception.Message);
        }
        try { source.PointerExited -= CaptionNative_PointerExited; }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            Logger.Instance.Debug("[TitleBar] Native exit diagnostic detach unavailable: {0}.", exception.Message);
        }
        try { source.RegionsChanged -= CaptionNative_RegionsChanged; }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            Logger.Instance.Debug("[TitleBar] Native region-change diagnostic detach unavailable: {0}.", exception.Message);
        }
    }

    /// <summary>Records native region replacements so a settle-time rect snapshot is not mistaken for the final layout.</summary>
    /// <remarks>Only the kinds the overlay depends on spend the budget; the eight per-kind registrations at startup would otherwise exhaust it.</remarks>
    /// <param name="sender">The borrowed native pointer source.</param>
    /// <param name="args">The unmodified region-change notification.</param>
    private void CaptionNative_RegionsChanged(InputNonClientPointerSource sender, NonClientRegionsChangedEventArgs args)
    {
        if (_isClosing || Logger.Instance.Level < LogLevel.Debug || _captionRegionChangeDiagnosticsRemaining <= 0) return;
        var kinds = args.ChangedRegions ?? [];
        if (!kinds.Any(static kind => kind is NonClientRegionKind.Minimize or NonClientRegionKind.Caption or NonClientRegionKind.Passthrough)) return;
        _captionRegionChangeDiagnosticsRemaining--;
        Logger.Instance.Debug("[TitleBar] Native regions changed: kinds={0}.", string.Join(",", kinds));
        try
        {
            foreach (var kind in kinds.Distinct())
            {
                if (kind is NonClientRegionKind.Minimize or NonClientRegionKind.Caption or NonClientRegionKind.Passthrough) LogCaptionInputRegion(sender, kind);
            }
        }
        catch (System.Runtime.InteropServices.COMException exception)
        {
            Logger.Instance.Debug("[TitleBar] Region-change diagnostics unavailable: {0}.", exception.Message);
        }
    }

    /// <summary>Records the native region's hover entry without changing its event handling.</summary>
    /// <param name="sender">The borrowed native pointer source.</param>
    /// <param name="args">The unmodified native pointer event.</param>
    private void CaptionNative_PointerEntered(InputNonClientPointerSource sender, NonClientPointerEventArgs args) =>
        LogCaptionNativePointer("entered", args);

    /// <summary>Records the native region's hover exit without changing its event handling.</summary>
    /// <param name="sender">The borrowed native pointer source.</param>
    /// <param name="args">The unmodified native pointer event.</param>
    private void CaptionNative_PointerExited(InputNonClientPointerSource sender, NonClientPointerEventArgs args) =>
        LogCaptionNativePointer("exited", args);

    /// <summary>Spends one entry from a family budget and announces exhaustion once, so silence is distinguishable from a spent budget.</summary>
    /// <param name="remaining">The budget shared by one event family.</param>
    /// <param name="family">The family named in the exhaustion line.</param>
    /// <returns>Whether the caller may log this event.</returns>
    private bool AdmitCaptionPointerDiagnostic(ref int remaining, string family)
    {
        if (_isClosing || Logger.Instance.Level < LogLevel.Debug || remaining <= 0) return false;
        remaining--;
        if (remaining == 0) Logger.Instance.Debug("[TitleBar] Pointer diagnostic budget exhausted: {0}.", family);
        return true;
    }

    /// <summary>Logs the shortcut's derived hover transitions in DIPs relative to the shortcut, in the same format the XAML events used.</summary>
    /// <param name="transition">The derived entry or exit.</param>
    /// <param name="args">The native event that caused it.</param>
    private void LogCaptionShortcutPointer(string transition, NonClientPointerEventArgs args)
    {
        if (!AdmitCaptionPointerDiagnostic(ref _captionHelpPointerDiagnosticsRemaining, "help")) return;
        var scale = HelpShortcutsButton.XamlRoot?.RasterizationScale ?? 1;
        var bounds = GetCaptionShortcutPhysicalBounds() ?? new Windows.Foundation.Rect(0, 0, 0, 0);
        Logger.Instance.Debug("[TitleBar] Help pointer {0}: local DIP x={1:F2}, y={2:F2}, handled={3}.",
            transition, (args.Point.X - bounds.X) / scale, (args.Point.Y - bounds.Y) / scale, false);
    }

    /// <summary>Logs a native press, release or tap so an observation run can pin the order the pressed visual depends on.</summary>
    /// <param name="transition">The native event.</param>
    /// <param name="region">The region the system reported.</param>
    /// <param name="point">The reported point in physical window pixels.</param>
    /// <param name="device">The reporting device.</param>
    /// <param name="onShortcut">Whether the point fell on the shortcut.</param>
    /// <param name="raisedOnUiThread">Whether the source raised the event on the UI thread.</param>
    private void LogCaptionInputEvent(string transition, NonClientRegionKind region, Windows.Foundation.Point point, PointerDeviceType device, bool onShortcut, bool raisedOnUiThread)
    {
        if (!AdmitCaptionPointerDiagnostic(ref _captionInputEventDiagnosticsRemaining, "caption input")) return;
        Logger.Instance.Debug("[TitleBar] Caption input {0}: region={1}, device={2}, onShortcut={3}, reported x={4:F2}, y={5:F2}, uiThread={6}.",
            transition, region, device, onShortcut, point.X, point.Y, raisedOnUiThread);
    }

    /// <summary>Records a pass skipped over unusable caption metrics, at most eight times per run.</summary>
    /// <param name="titleBar">The native title bar whose metrics were read.</param>
    /// <param name="scale">The XAML root's rasterization scale at that moment.</param>
    private void LogCaptionMetricsSkipped(AppWindowTitleBar titleBar, double scale)
    {
        if (!AdmitCaptionPointerDiagnostic(ref _captionMetricsSkippedDiagnosticsRemaining, "caption metrics")) return;
        var state = AppWindow.Presenter is OverlappedPresenter presenter ? presenter.State.ToString() : AppWindow.Presenter?.Kind.ToString();
        Logger.Instance.Debug("[TitleBar] Skipped caption metrics: height={0}, leftInset={1}, rightInset={2}, scale={3:F2}, presenter={4}.",
            titleBar.Height, titleBar.LeftInset, titleBar.RightInset, scale, state);
    }

    /// <summary>Logs the system's move-size loop boundaries in the same family as the presses they follow.</summary>
    /// <param name="transition">The loop boundary.</param>
    /// <param name="operation">The reported move or size operation.</param>
    /// <param name="x">The reported screen x coordinate.</param>
    /// <param name="y">The reported screen y coordinate.</param>
    /// <param name="raisedOnUiThread">Whether the source raised the event on the UI thread.</param>
    private void LogCaptionMoveSize(string transition, string operation, double x, double y, bool raisedOnUiThread)
    {
        if (!AdmitCaptionPointerDiagnostic(ref _captionInputEventDiagnosticsRemaining, "caption input")) return;
        Logger.Instance.Debug("[TitleBar] Caption input {0}: operation={1}, screen x={2:F2}, y={3:F2}, uiThread={4}.",
            transition, operation, x, y, raisedOnUiThread);
    }

    /// <summary>Logs the native API's reported region and point without assuming it shares XAML coordinates.</summary>
    /// <param name="transition">The observed entry or exit.</param>
    /// <param name="args">The unchanged non-client event.</param>
    private void LogCaptionNativePointer(string transition, NonClientPointerEventArgs args)
    {
        var isCaptionButton = args.RegionKind is NonClientRegionKind.Minimize or NonClientRegionKind.Maximize or NonClientRegionKind.Close;
        var admitted = isCaptionButton
            ? AdmitCaptionPointerDiagnostic(ref _captionButtonPointerDiagnosticsRemaining, "caption buttons")
            : AdmitCaptionPointerDiagnostic(ref _captionOtherPointerDiagnosticsRemaining, "other native regions");
        if (!admitted) return;
        Logger.Instance.Debug("[TitleBar] Native pointer {0}: region={1}, inRegion={2}, reported x={3:F2}, y={4:F2}.",
            transition, args.RegionKind, args.IsPointInRegion, args.Point.X, args.Point.Y);
    }

    /// <summary>Reuses the caption colors already selected by the shared window chrome for the new theme.</summary>
    /// <param name="sender">The shell whose theme changed.</param>
    /// <param name="args">The theme notification.</param>
    private void CaptionShortcut_ThemeChanged(FrameworkElement sender, object args) => UpdateCaptionShortcutColors();

    /// <summary>Applies the native neutral caption hover and press colors to the standard accessible button template.</summary>
    private void UpdateCaptionShortcutColors()
    {
        var titleBar = AppWindow.TitleBar;
        // The style has already resolved its normal foreground when this runs; assign it directly as well.
        if (titleBar.ButtonForegroundColor is { } normalForeground)
        {
            if (HelpShortcutsButton.Foreground is SolidColorBrush ownedForeground && _captionShortcutBrushes.ContainsValue(ownedForeground))
                ownedForeground.Color = normalForeground;
            else
            {
                var foregroundBrush = new SolidColorBrush(normalForeground);
                _captionShortcutBrushes["HelpShortcutsButton.Foreground"] = foregroundBrush;
                HelpShortcutsButton.Foreground = foregroundBrush;
            }
        }
        SetCaptionShortcutBrush("ButtonForeground", titleBar.ButtonForegroundColor);
        SetCaptionShortcutBrush("ButtonForegroundPointerOver", titleBar.ButtonHoverForegroundColor);
        SetCaptionShortcutBrush("ButtonForegroundPressed", titleBar.ButtonPressedForegroundColor);
        SetCaptionShortcutBrush("ButtonBackgroundPointerOver", titleBar.ButtonHoverBackgroundColor);
        SetCaptionShortcutBrush("ButtonBackgroundPressed", titleBar.ButtonPressedBackgroundColor);
    }

    /// <summary>Updates a retained brush so theme changes also reach an already loaded button template.</summary>
    /// <param name="key">The standard Button theme-resource key.</param>
    /// <param name="color">The color supplied by the shared native window chrome.</param>
    private void SetCaptionShortcutBrush(string key, Windows.UI.Color? color)
    {
        if (color is not { } value) return;
        // Resource lookup may return a protected framework brush. Mutate only brushes this window created.
        if (_captionShortcutBrushes.TryGetValue(key, out var brush))
            brush.Color = value;
        else
        {
            brush = new SolidColorBrush(value);
            _captionShortcutBrushes.Add(key, brush);
            HelpShortcutsButton.Resources[key] = brush;
        }
    }

    private async void F1_ShortcutsDialog_Invoked(
        Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowShortcutsDialogAsync();
    }

    // The title-bar "?" button — the discoverable twin of the F1 accelerator.
    private async void HelpShortcuts_Click(object sender, RoutedEventArgs e) =>
        await ShowShortcutsDialogAsync();

    private async Task ShowShortcutsDialogAsync()
    {
        if (_isClosing)
        {
            return;
        }
        var dialog = new KeyboardShortcutsDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)Content).ActualTheme
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch
        {
            // ContentDialog.ShowAsync can throw if another dialog is already open
            // (WinUI 3 enforces at most one ContentDialog per XamlRoot). Swallow so
            // the request doesn't crash the app — user can try again after closing.
        }
    }

    // ── Title bar navigation buttons ──

    private void NavBack_Click(object sender, RoutedEventArgs e)
    {
        ExploreTabContent.Records.UnifiedBack_Click(sender, e);
    }

    private void NavForward_Click(object sender, RoutedEventArgs e)
    {
        ExploreTabContent.Records.UnifiedForward_Click(sender, e);
    }

    internal void SetNavButtonStates(bool backEnabled, bool forwardEnabled)
    {
        NavBackButton.IsEnabled = backEnabled;
        NavForwardButton.IsEnabled = forwardEnabled;
    }

    private void UpdateNavButtonVisibility(string? tag)
    {
        NavButtonPanel.Visibility = tag == "Explore" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Mounts shared views once in the sidebar and follows view changes only while Explore is active.</summary>
    private void InitializeExploreNavigation()
    {
        foreach (var item in ExploreTabContent.NavigationItems) ExploreNavigationItem.MenuItems.Add(item);
        ExploreTabContent.SelectedViewChanged += OnExploreViewChanged;
        NavView.SelectedItem = ExploreTabContent.SelectedNavigationItem;
    }

    /// <summary>Reflects record/map links and source-driven view changes without leaving an active recovery workflow.</summary>
    private void OnExploreViewChanged(object? sender, string view)
    {
        if (_isClosing)
        {
            return;
        }
        if (ExploreTabContent.Visibility != Visibility.Visible) return;
        ExploreNavigationItem.IsExpanded = true;
        if (!ReferenceEquals(NavView.SelectedItem, ExploreTabContent.SelectedNavigationItem))
            NavView.SelectedItem = ExploreTabContent.SelectedNavigationItem;
    }

    /// <summary>Selects a sidebar view while retaining all existing workspace and recovery instances.</summary>
    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isClosing)
        {
            return;
        }
        if (args.SelectedItem is NavigationViewItem selectedItem)
        {
            var tag = ExploreTabContent.TrySelectNavigation(selectedItem) ? "Explore" : selectedItem.Tag?.ToString();

            // Hide all content
            ExploreTabContent.Visibility = Visibility.Collapsed;
            RecoveryFileTabContent.Visibility = Visibility.Collapsed;
            BatchModeTabContent.Visibility = Visibility.Collapsed;
            NifConverterTabContent.Visibility = Visibility.Collapsed;
            DdxConverterTabContent.Visibility = Visibility.Collapsed;
            BsaExtractorTabContent.Visibility = Visibility.Collapsed;
            RepackerTabContent.Visibility = Visibility.Collapsed;
            DmpToEsmConverterTabContent.Visibility = Visibility.Collapsed;
            DiagnosticsTabContent.Visibility = Visibility.Collapsed;
            SettingsTabContent.Visibility = Visibility.Collapsed;

            // Clear status bar when switching tabs
            SetStatus("");

            // Show/hide title bar nav buttons based on active tab
            UpdateNavButtonVisibility(tag);

            // Show selected content
            switch (tag)
            {
                case "Explore":
                    ExploreTabContent.Visibility = Visibility.Visible;
                    break;
                case "RecoveryFile":
                    RecoveryFileTabContent.Visibility = Visibility.Visible;
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
                case "BsaExtractor":
                    BsaExtractorTabContent.Visibility = Visibility.Visible;
                    break;
                case "Repacker":
                    RepackerTabContent.Visibility = Visibility.Visible;
                    break;
                case "DmpToEsmConverter":
                    DmpToEsmConverterTabContent.Visibility = Visibility.Visible;
                    break;
                case "Diagnostics":
                    DiagnosticsTabContent.Visibility = Visibility.Visible;
                    break;
                case "Settings":
                    SettingsTabContent.Visibility = Visibility.Visible;
                    break;
            }

            ExploreTabContent.SetPresentationActive(tag == "Explore");
            Console.WriteLine($"[MainWindow] Navigated to: {tag}");
        }
    }

    /// <summary>
    ///     Updates the global status bar text.
    /// </summary>
    public void SetStatus(string message)
    {
        RuntimeLocalization.SetRaw(GlobalStatusTextBlock, TextBlock.TextProperty, message);
    }
}
