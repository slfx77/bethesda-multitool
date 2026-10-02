using Windows.Storage.Pickers;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.Core.Localization;
using Slfx77.Multitool.WinUI.Direct3D12;
using Slfx77.Multitool.WinUI.Workflows;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>Coordinates one source across the rich data, native map, and asset views.</summary>
public sealed partial class ExploreTab : UserControl, IAsyncDisposable
{
    private readonly AppBarButton _openWorkspaceButton;
    private readonly AppBarButton _reloadWorkspaceButton;
    private readonly AppBarButton _cancelWorkspaceButton;
    private readonly AppBarButton _loadOrderWorkspaceButton;
    private readonly BrowserSession _browser = new();
    private readonly SemaphoreSlim _sourceGate = new(1, 1);
    private CancellationTokenSource? _openCancellation;
    private ExploreSourcePlan? _plan;
    private bool _allowLoadNavigation;
    private bool _hasRecords;
    private bool _presentationActive;
    private Task? _disposeTask;
    private bool _disposed;

    /// <summary>Creates persistent data and asset hosts and connects their navigation and busy state to the shared shell.</summary>
    public ExploreTab()
    {
        InitializeComponent();
        var localization = MainWindow.Instance?.Localization
            ?? throw new InvalidOperationException("Create the Explore workspace after its owning window.");
        WorkspaceSourceToolbar.SetLocalization(localization, "Explore_SourcePath.PlaceholderText", "WorkspaceSourcePath",
            "Explore_SourcePath.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name");
        _openWorkspaceButton = WorkspaceSourceToolbar.AddCommand("Explore_Open.Content", "OpenWorkspaceButton", new SymbolIcon(Symbol.OpenFile));
        var openMenu = new MenuFlyout();
        var openFile = new MenuFlyoutItem();
        localization.Bind(openFile, MenuFlyoutItem.TextProperty, "Explore_OpenFile.Text");
        openFile.Click += OpenFile_Click;
        openMenu.Items.Add(openFile);
        var openFolder = new MenuFlyoutItem();
        localization.Bind(openFolder, MenuFlyoutItem.TextProperty, "Explore_OpenFolder.Text");
        openFolder.Click += OpenFolder_Click;
        openMenu.Items.Add(openFolder);
        _openWorkspaceButton.Flyout = openMenu;
        _reloadWorkspaceButton = WorkspaceSourceToolbar.AddCommand("Explore_Reload.Content", "ReloadWorkspaceButton", new SymbolIcon(Symbol.Refresh), Reload_Click);
        _reloadWorkspaceButton.IsEnabled = false;
        _loadOrderWorkspaceButton = WorkspaceSourceToolbar.AddCommand("Explore_LoadOrder.Content", "WorkspaceLoadOrderButton", new SymbolIcon(Symbol.List), LoadOrder_Click);
        _loadOrderWorkspaceButton.IsEnabled = false;
        _cancelWorkspaceButton = WorkspaceSourceToolbar.AddCommand("Explore_Cancel.Content", "CancelWorkspaceButton", new SymbolIcon(Symbol.Cancel), Cancel_Click);
        _cancelWorkspaceButton.Visibility = Visibility.Collapsed;
        DataBrowser.ConfigureWorkspaceHost(recovery: false);
        AssetsBrowser.ConfigureWorkspaceHost();
        AssetsBrowser.ArchiveOpenRequested += OnArchiveOpenRequested;
        DataBrowser.ExploreMapSelected += OnDataMapSelected;
        DataBrowser.BusyChanged += OnDataBusyChanged;
        UpdateView();
        Loaded += OnLoaded;
    }

    /// <summary>Borrows the containing window's native graphics and workflow services for the retained asset pane.</summary>
    /// <param name="graphics">Window-owned graphics retained until NativeResourcesRetired is true.</param>
    /// <param name="window">The same window's borrowed localization, picker and diagnostic services.</param>
    /// <remarks>The asset pane's matching implementation must track and drain Shadowkey preparation/adoption,
    /// then await its presenter before publishing NativeResourcesRetired. This bridge owns no second presenter.</remarks>
    internal void EnableShadowkeyPreview(Direct3D12Graphics graphics, IWindowWorkflowServices window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssetsBrowser.EnableShadowkeyPreview(graphics, window);
        AssetsBrowser.SetShadowkeyPresentationActive(_presentationActive && WorkspaceShell.SelectedView == "assets");
    }

    /// <summary>Publishes native retirement independently of unrelated data or source cleanup failures.</summary>
    internal bool NativeResourcesRetired => AssetsBrowser.NativeResourcesRetired;

    /// <summary>Publishes the complete scene/thumbnail prerequisite before workspace or window owners can retire.</summary>
    internal bool WindowResourcesRetired => AssetsBrowser.WindowResourcesRetired;

    /// <summary>Stops native activity on outer navigation while retaining the selected asset and camera.</summary>
    /// <param name="active">Whether this Explore workspace is the window's presented destination.</param>
    internal void SetPresentationActive(bool active)
    {
        if (_disposed) return;
        _presentationActive = active;
        AssetsBrowser.SetShadowkeyPresentationActive(active && WorkspaceShell.SelectedView == "assets");
    }

    /// <summary>The rich browser retains its record/world navigation history.</summary>
    internal SingleFileTab Records => DataBrowser;

    /// <summary>Updates shared labels and existing data viewers while retaining source, selection and render state.</summary>
    internal void RefreshLocalization()
    {
        WorkspaceShell.RefreshLocalization(SharedStrings.Create(Strings.Display.Culture, Strings.Display.IsPseudoLocalized));
        DataBrowser.RefreshLocalization();
    }

    /// <summary>The shared Data, Maps and Assets items mounted in the window's Explore navigation group.</summary>
    internal IReadOnlyList<NavigationViewItem> NavigationItems => WorkspaceShell.NavigationItems;

    /// <summary>The sidebar item representing the current source view.</summary>
    internal NavigationViewItem SelectedNavigationItem => WorkspaceShell.SelectedNavigationItem;

    /// <summary>Notifies the window when source opening or cross-record navigation changes the view.</summary>
    internal event EventHandler<string>? SelectedViewChanged;

    /// <summary>Routes only this workspace's available sidebar items through the shared view selection.</summary>
    /// <param name="item">The item selected in the window navigation.</param>
    /// <returns>Whether the selected item belongs to this Explore workspace.</returns>
    internal bool TrySelectNavigation(object? item) => WorkspaceShell.TrySelectView(item);

    /// <summary>Routes existing command-line source and view arguments through the same workspace.</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var path = Program.AutoAssetSource ?? Program.AutoLoadFile;
        if (!string.IsNullOrWhiteSpace(path))
            await OpenSourceAsync(path, autoOpen: Program.AutoAssetSource is null);
    }

    /// <summary>Opens an explicitly selected file through the workspace source planner.</summary>
    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));
        if (await picker.PickSingleFileAsync() is { } file) await OpenSourceAsync(file.Path);
    }

    /// <summary>Opens an explicitly selected folder as the workspace's record and asset source.</summary>
    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));
        if (await picker.PickSingleFolderAsync() is { } folder) await OpenSourceAsync(folder.Path);
    }

    /// <summary>Reopens the current source path so replacement bytes receive a new source generation.</summary>
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is { } plan) await OpenSourceAsync(plan.SourcePath);
    }

    /// <summary>Requests cancellation of the current source opening or record selection without closing Recovery.</summary>
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_openCancellation is { } cancellation) await cancellation.CancelAsync();
    }

    /// <summary>Opens an original loose archive through the workspace's normal replacement ownership.</summary>
    private async void OnArchiveOpenRequested(AssetArchiveOpenRequest request)
    {
        if (_disposed || _openCancellation is not null || DataBrowser.IsBusy ||
            !ReferenceEquals(request.Snapshot, _browser.Current) ||
            request.Snapshot.CancellationToken.IsCancellationRequested) return;
        await OpenSourceAsync(request.Path);
    }

    /// <summary>Opens off-thread and serializes publication with record loading and source replacement.</summary>
    internal async Task OpenSourceAsync(string path, bool autoOpen = false)
    {
        if (_disposed) return;
        var cancellation = new CancellationTokenSource();
        var previous = _openCancellation;
        _openCancellation = cancellation;
        if (previous is not null) await previous.CancelAsync();
        var entered = false;
        BethesdaBrowseSource? pendingSource = null;
        try
        {
            await _sourceGate.WaitAsync(cancellation.Token);
            entered = true;
            SetBusy(true);
            ShowLocalizedStatus("Explore_Opening", InfoBarSeverity.Informational);
            var plan = await Task.Run(() => ExploreSourcePlanner.Create(path, cancellation.Token), cancellation.Token);
            var sameSource = string.Equals(_plan?.SourcePath, plan.SourcePath, StringComparison.OrdinalIgnoreCase);
            var initialSource = _plan is null;
            if (sameSource && _plan?.AnalysisPath is { } previousPrimary &&
                plan.RecordCandidates.Contains(previousPrimary, StringComparer.OrdinalIgnoreCase))
                plan = plan with { AnalysisPath = previousPrimary, AnalysisType = AnalysisFileType.EsmFile };
            pendingSource = await Task.Run(() => BethesdaBrowseSource.Open(plan), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            await DataBrowser.ClearExploreSourceAsync(preserveLoadOrder: sameSource);
            _hasRecords = false;
            await AssetsBrowser.CloseSourceAsync();
            await _browser.ReplaceAsync(pendingSource, cancellation.Token);
            pendingSource = null;
            _plan = plan;
            _hasRecords = false;
            await AssetsBrowser.AttachWorkspaceSourceAsync(_browser.Current!);
            DataBrowser.AttachDialogueAudioSource(_browser.Current!);
            WorkspaceSourceToolbar.SourcePath = plan.SourcePath;
            UpdateRecordSourceSummary();
            if (plan.AnalysisPath is null)
            {
                RuntimeLocalization.SetText(RecordPlaceholder, plan.RecordCandidates.Count > 1
                    ? "Explore_ChoosePlugin" : "Explore_NoRecords");
                WorkspaceShell.SelectedView = "assets";
            }
            else
            {
                if (initialSource && WorkspaceShell.SelectedView != "maps") WorkspaceShell.SelectedView = "data";
                await LoadRecordsAsync(plan.AnalysisPath, autoOpen, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            WorkspaceStatus.IsOpen = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_openCancellation, cancellation))
                ShowLocalizedStatus("Explore_LoadCanceled", InfoBarSeverity.Informational);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_openCancellation, cancellation)) ShowStatus(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (pendingSource is not null && !ReferenceEquals(_browser.Current?.Source, pendingSource))
                await pendingSource.DisposeAsync();
            if (entered) _sourceGate.Release();
            if (ReferenceEquals(_openCancellation, cancellation))
            {
                _openCancellation = null;
                SetBusy(false);
                UpdateView();
            }
            cancellation.Dispose();
        }
    }

    /// <summary>Shows the selected primary; discovery and ordering share the Load Order dialog.</summary>
    private void UpdateRecordSourceSummary()
    {
        RecordSourceSummary.Text = _plan?.AnalysisPath is { } primary
            ? Path.GetFileName(primary) : string.Empty;
        RecordSourceSummary.Visibility = string.IsNullOrEmpty(RecordSourceSummary.Text)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Combines discovered primaries and supplementary ordering without changing the current pane.</summary>
    private async void LoadOrder_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || _openCancellation is not null || DataBrowser.IsBusy || _plan is not { } sourcePlan) return;
        var result = await LoadOrderDialogService.ShowAsync(XamlRoot, DataBrowser.CreateExploreLoadOrderEntries(),
            new LoadOrderDialogOptions
            {
                Title = Strings.Get("LoadOrder_Title"),
                IntroText = Strings.Get("LoadOrder_ExploreIntro"),
                RecordCandidates = sourcePlan.RecordCandidates,
                PrimaryFilePath = sourcePlan.AnalysisPath,
                AllowedExtensions = [".esm", ".esp", ".esl", ".dmp"],
                AllowSubtitleCsv = true,
                SubtitleCsvPath = DataBrowser.ExploreSubtitleCsvPath
            });
        if (result.Action == LoadOrderDialogAction.Cancel || _disposed ||
            !ReferenceEquals(_plan, sourcePlan) || _openCancellation is not null) return;
        var path = result.Action == LoadOrderDialogAction.ClearAll
            ? sourcePlan.AnalysisPath
            : result.PrimaryFilePath ?? sourcePlan.AnalysisPath;
        if (path is null) return;
        using var cancellation = new CancellationTokenSource();
        _openCancellation = cancellation;
        var entered = false;
        try
        {
            await _sourceGate.WaitAsync(cancellation.Token);
            entered = true;
            SetBusy(true);
            if (_hasRecords && string.Equals(path, sourcePlan.AnalysisPath, StringComparison.OrdinalIgnoreCase))
                await DataBrowser.ApplyExploreLoadOrderAsync(result, cancellation.Token);
            else
            {
                DataBrowser.StageExploreLoadOrder(result);
                await DataBrowser.ClearExploreSourceAsync(preserveLoadOrder: true);
                _hasRecords = false;
                await LoadRecordsAsync(path, autoOpen: false, cancellationToken: cancellation.Token);
                _plan = sourcePlan with { AnalysisPath = path, AnalysisType = DataBrowser.SourceType };
                UpdateRecordSourceSummary();
            }
            cancellation.Token.ThrowIfCancellationRequested();
            WorkspaceStatus.IsOpen = false;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowLocalizedStatus("Explore_RecordsCanceled", InfoBarSeverity.Informational);
        }
        catch (Exception exception) { ShowStatus(exception.Message, InfoBarSeverity.Error); }
        finally
        {
            if (entered) _sourceGate.Release();
            if (ReferenceEquals(_openCancellation, cancellation)) _openCancellation = null;
            SetBusy(false);
            UpdateView();
        }
    }

    /// <summary>Loads records while retaining the selected workspace pane and honoring explicit startup navigation.</summary>
    /// <param name="path">The primary plugin, save, dump, or supported classic data path.</param>
    /// <param name="autoOpen">Applies startup view arguments during the initial command-line opening.</param>
    /// <param name="cancellationToken">Cancels record analysis for a superseded opening.</param>
    private async Task LoadRecordsAsync(string path, bool autoOpen, CancellationToken cancellationToken)
    {
        ShowLocalizedStatus("Explore_LoadingRecords", InfoBarSeverity.Informational);
        RecordPlaceholder.Visibility = Visibility.Collapsed;
        _allowLoadNavigation = autoOpen;
        try { await DataBrowser.LoadExploreSourceAsync(path, cancellationToken, autoOpen); }
        finally { _allowLoadNavigation = false; }
        _hasRecords = DataBrowser.SourceType != AnalysisFileType.Unknown;
        UpdateView();
    }

    /// <summary>Keeps source controls disabled while either native record work or workspace opening is active.</summary>
    private void OnDataBusyChanged(object? sender, bool busy)
    {
        SetBusy(busy || _openCancellation is not null);
    }

    /// <summary>Reflects native record/map navigation in the shared shell's selected view.</summary>
    private void OnDataMapSelected(object? sender, bool maps)
    {
        if (_openCancellation is not null && !_allowLoadNavigation) return;
        WorkspaceShell.SelectedView = maps ? "maps" : "data";
    }

    /// <summary>Updates mounted viewer visibility after the shell's selected view changes.</summary>
    private void View_SelectionChanged(object? sender, string view)
    {
        UpdateView();
        SelectedViewChanged?.Invoke(this, view);
    }

    /// <summary>Switches presentation while keeping each expensive viewer instance mounted.</summary>
    private void UpdateView()
    {
        if (DataBrowser is null || AssetsBrowser is null) return;
        var assets = WorkspaceShell.SelectedView == "assets";
        var maps = WorkspaceShell.SelectedView == "maps";
        var classicMap = maps && (!_hasRecords || DataBrowser.SourceType == AnalysisFileType.ClassicGameData);
        AssetsBrowser.Visibility = assets || classicMap ? Visibility.Visible : Visibility.Collapsed;
        DataBrowser.Visibility = assets || classicMap ? Visibility.Collapsed : Visibility.Visible;
        if (assets || classicMap) AssetsBrowser.ShowWorkspaceMaps(classicMap);
        else DataBrowser.ShowExploreMaps(maps);
        AssetsBrowser.SetShadowkeyPresentationActive(_presentationActive && assets);
        RecordPlaceholder.Visibility = !assets && !classicMap && !_hasRecords
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Updates source-opening controls and exposes cancellation only while a cancellable operation exists.</summary>
    private void SetBusy(bool busy)
    {
        _openWorkspaceButton.IsEnabled = !busy;
        _reloadWorkspaceButton.IsEnabled = !busy && _plan is not null;
        _loadOrderWorkspaceButton.IsEnabled = !busy &&
            (_plan?.AnalysisPath is not null || _plan?.RecordCandidates.Count > 0);
        _cancelWorkspaceButton.Visibility = busy && _openCancellation is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Displays the current source operation's localized status or error in the workspace infobar.</summary>
    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        WorkspaceStatus.Message = message;
        WorkspaceStatus.Severity = severity;
        WorkspaceStatus.IsOpen = true;
    }

    /// <summary>Tracks the current operation's resource key without changing the operation itself.</summary>
    /// <param name="key">The status resource identifier.</param>
    /// <param name="severity">The existing status severity.</param>
    private void ShowLocalizedStatus(string key, InfoBarSeverity severity)
    {
        RuntimeLocalization.Set(WorkspaceStatus, InfoBar.MessageProperty, key);
        WorkspaceStatus.Severity = severity;
        WorkspaceStatus.IsOpen = true;
    }

    /// <summary>Cancels opening and releases viewers before retiring their shared source.</summary>
    /// <returns>Completion after every viewer and the shared source have attempted teardown.</returns>
    /// <exception cref="AggregateException">One or more owner cleanup operations failed.</exception>
    public ValueTask DisposeAsync()
    {
        if (_disposeTask is not null) return new ValueTask(_disposeTask);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _disposeTask = completion.Task;
        _ = DisposeExploreObservedAsync(completion);
        return new ValueTask(completion.Task);
    }

    /// <summary>Publishes complete retirement to the result retained before callbacks can reenter disposal.</summary>
    /// <param name="completion">The already published disposal result shared by all callers.</param>
    /// <returns>Completion after the original retirement outcome is forwarded without implicit retry.</returns>
    private async Task DisposeExploreObservedAsync(TaskCompletionSource completion)
    {
        try { await DisposeCoreAsync(); completion.TrySetResult(); }
        catch (Exception failure) { completion.TrySetException(failure); }
    }

    /// <summary>Retains one teardown result and preserves source prerequisites when native retirement fails.</summary>
    /// <returns>Completion after the pending source operation and each dependent viewer retire.</returns>
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        AssetsBrowser.ArchiveOpenRequested -= OnArchiveOpenRequested;
        _presentationActive = false;
        List<Exception>? failures = null;
        try { AssetsBrowser.SetShadowkeyPresentationActive(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        if (_openCancellation is { } cancellation)
        {
            try { await cancellation.CancelAsync(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        await _sourceGate.WaitAsync();
        try
        {
            try { await AssetsBrowser.DisposeAsync(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            try { await DataBrowser.ClearExploreSourceAsync(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            try { await DataBrowser.DisposeAsync(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            if (WindowResourcesRetired)
            {
                try { await _browser.DisposeAsync(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }
            else
            {
                (failures ??= []).Add(new InvalidOperationException(
                    "The workspace retains its source because scene or thumbnail retirement is incomplete."));
            }
            if (failures is { Count: > 0 })
            {
                throw new AggregateException("Workspace owner cleanup failed.", failures);
            }
        }
        finally { _sourceGate.Release(); }
    }
}
