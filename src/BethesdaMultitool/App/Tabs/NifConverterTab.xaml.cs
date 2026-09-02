using Windows.Storage.Pickers;
using BethesdaMultitool.CLI.Rendering.Nif;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>
///     Tab for batch converting Xbox 360 NIF files to PC format.
/// </summary>
public sealed partial class NifConverterTab : NifFileConverterBase
{
    private readonly NifConverterViewModel _nifViewer = new();
    private bool _dependencyCheckDone;

    // NIF Viewer state
    private NifBrowserService? _nifBrowserService;
    private BethesdaViewerScene? _nifViewerScene;
    private CancellationTokenSource? _nifViewerLoadCts;
    private Task? _nifViewerLoadTask;
    private int _nifViewerLoadGeneration;
    private int _nifViewerSourceLoadingGeneration;
    private bool _nifViewerNativeReady;
    private TaskCompletionSource<BethesdaSceneViewerRenderState>? _nifViewerNativeOutcome;
    private BethesdaViewerScene? _nifViewerNativeOutcomeScene;
    private int _nifViewerNativeOutcomeGeneration;
    private bool _nifViewerWebViewInitialized;
    private Task? _nifViewerWebViewInitializationTask;
    private bool _nifViewerDisposed;

    public NifConverterTab()
    {
        InitializeComponent();
        ReorderTabsForModelWorkflow();
        SetupTextBoxContextMenus();
        NifSceneViewer.RenderStateChanged += NifSceneViewer_RenderStateChanged;
        NifSceneViewer.AttachRenderSession(new BethesdaViewerRenderSession12());
        Loaded += NifConverterTab_Loaded;
    }

    // Wire abstract properties to XAML-declared elements
    protected override ListView FilesListView => NifFilesListView;
    protected override ProgressBar ConversionProgressBar => NifConversionProgressBar;
    protected override Button ConvertButtonElement => NifConvertButton;
    protected override Button CancelButtonElement => NifCancelButton;
    protected override TextBox InputDirectoryTextBox => NifInputDirectoryTextBox;
    protected override TextBox OutputDirectoryTextBox => NifOutputDirectoryTextBox;
    protected override FontIcon FilePathSortIcon => NifFilePathSortIcon;
    protected override FontIcon SizeSortIcon => NifSizeSortIcon;
    protected override FontIcon FormatSortIcon => NifFormatSortIcon;
    protected override FontIcon StatusSortIcon => NifStatusSortIcon;
    protected override Border SettingsDrawerElement => SettingsDrawer;

    private void ReorderTabsForModelWorkflow()
    {
        NifTabView.TabItems.Clear();
        NifTabView.TabItems.Add(NifViewerTab);
        NifTabView.TabItems.Add(NifBatchConvertTab);
        NifTabView.SelectedItem = NifViewerTab;
    }

    private async void NifConverterTab_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= NifConverterTab_Loaded;

        if (!_dependencyCheckDone)
        {
            _dependencyCheckDone = true;
            await CheckDependenciesAsync();
        }
    }

    private async Task CheckDependenciesAsync()
    {
        await Task.Delay(100);
        var result = DependencyChecker.CheckNifConverterDependencies();
        if (!result.AllAvailable) await DependencyDialogHelper.ShowIfMissingAsync(result, XamlRoot);
    }

    #region Browse & Scan

    private async void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder == null) return;

        InputDirectoryTextBox.Text = folder;
        OutputDirectoryTextBox.Text = Path.Combine(folder, "converted_pc");
    }

    private async void InputDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var path = InputDirectoryTextBox.Text;
        if (Directory.Exists(path))
        {
            OutputDirectoryTextBox.Text = Path.Combine(path, "converted_pc");
            await ScanForNifFilesAsync(path);
        }
        else
        {
            ClearFileList();
        }
    }

    private void OutputDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateButtonStates();
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder != null)
        {
            OutputDirectoryTextBox.Text = folder;
            UpdateButtonStates();
        }
    }

    private async Task ScanForNifFilesAsync(string directory)
    {
        if (ScanCts != null)
        {
            await ScanCts.CancelAsync();
            ScanCts.Dispose();
        }

        ScanCts = new CancellationTokenSource();
        var cancellationToken = ScanCts.Token;

        Files = [];
        AllFiles.Clear();
        AllFiles.TrimExcess();
        FilesListView.ItemsSource = null;
        Sorter.Reset();
        UpdateSortIcons();
        StatusTextBlock.Text = "Scanning for NIF files...";

        if (!Directory.Exists(directory))
        {
            StatusTextBlock.Text = "Directory does not exist.";
            UpdateFileCount();
            UpdateButtonStates();
            return;
        }

        ConversionProgressBar.Visibility = Visibility.Visible;
        ConversionProgressBar.IsIndeterminate = true;
        ConversionProgressBar.Value = 0;

        try
        {
            var entries = await ScanAndCreateNifEntriesAsync(directory, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            OnScanComplete(entries);
            StatusTextBlock.Text =
                $"Found {Files.Count} NIF files. {Files.Count(f => f.FormatDescription == "Xbox 360 (BE)")} require conversion.";
        }
        finally
        {
            ConversionProgressBar.Visibility = Visibility.Collapsed;
            ConversionProgressBar.IsIndeterminate = false;
        }
    }

    private async Task<NifFileEntry[]> ScanAndCreateNifEntriesAsync(string directory,
        CancellationToken cancellationToken)
    {
        var progress = new Progress<NifScanProgress>(p =>
        {
            if (p.Total > 0 && Math.Abs(ConversionProgressBar.Maximum - p.Total) > 0.1)
            {
                ConversionProgressBar.IsIndeterminate = false;
                ConversionProgressBar.Maximum = p.Total;
                ConversionProgressBar.Value = 0;
                StatusTextBlock.Text = $"Scanning {p.Total} NIF files...";
            }

            ConversionProgressBar.Value = p.Current;
        });

        return await NifConverterWorkflowService.ScanNifEntriesAsync(
            directory,
            progress,
            cancellationToken);
    }

    #endregion

    #region Selection

    private void SelectAllButton_Click(object sender, RoutedEventArgs e) => SelectAll();
    private void SelectNoneButton_Click(object sender, RoutedEventArgs e) => SelectNone();

    #endregion

    #region Conversion

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = Files.Where(f => f.IsSelected).ToList();
        if (selectedFiles.Count == 0)
        {
            await ShowDialogAsync("No Files Selected", "Please select at least one NIF file to convert.");
            return;
        }

        var options = new NifConversionOptions(
            InputDirectoryTextBox.Text,
            OutputDirectoryTextBox.Text,
            PreserveStructureCheckBox.IsChecked == true,
            OverwriteExistingCheckBox.IsChecked == true);
        var verbose = VerboseOutputCheckBox.IsChecked == true;

        if (verbose) Core.Diagnostics.Logger.Instance.Level = Core.Diagnostics.LogLevel.Debug;

        ConversionCts = new CancellationTokenSource();
        UpdateButtonStates();

        ConversionProgressBar.Visibility = Visibility.Visible;
        ConversionProgressBar.IsIndeterminate = false;
        ConversionProgressBar.Maximum = selectedFiles.Count;
        ConversionProgressBar.Value = 0;

        try
        {
            var progress = new Progress<NifConversionProgress>(p =>
            {
                StatusTextBlock.Text = $"Converting {p.Current}/{p.Total}: {p.RelativePath}";
                ConversionProgressBar.Value = p.Current;
            });
            var summary = await NifConverterWorkflowService.ConvertFilesAsync(
                selectedFiles,
                options,
                progress,
                ConversionCts.Token);

            StatusTextBlock.Text =
                $"Conversion complete. Converted: {summary.Converted}, Skipped: {summary.Skipped}, Failed: {summary.Failed}";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Conversion canceled.";
        }
        finally
        {
            ConversionCts.Dispose();
            ConversionCts = null;
            ConversionProgressBar.Visibility = Visibility.Collapsed;
            UpdateButtonStates();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        ConversionCts?.Cancel();
        StatusTextBlock.Text = "Canceling...";
    }

    #endregion

    #region Sorting

    private void SortByFilePath_Click(object sender, RoutedEventArgs e) => ApplySort(ConvertibleSortColumn.FilePath);
    private void SortBySize_Click(object sender, RoutedEventArgs e) => ApplySort(ConvertibleSortColumn.Size);
    private void SortByFormat_Click(object sender, RoutedEventArgs e) => ApplySort(ConvertibleSortColumn.Format);
    private void SortByStatus_Click(object sender, RoutedEventArgs e) => ApplySort(ConvertibleSortColumn.Status);

    #endregion

    #region NIF Viewer

    private void NifTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var viewerSelected = ReferenceEquals(NifTabView.SelectedItem, NifViewerTab);
        NifSceneViewer.SetPresentationActive(viewerSelected);
        if (!viewerSelected) return;

        NifSceneViewer.InvalidateViewport();
        // Do not start Chromium merely because the tab is visible. The first selected scene is
        // offered to D3D12 directly; WebView is initialized only if that native attempt cannot
        // reach Ready.
    }

    private async Task InitializeNifViewerWebViewAsync()
    {
        if (_nifViewerNativeReady || _nifViewerWebViewInitialized)
        {
            return;
        }

        if (_nifViewerWebViewInitializationTask is { } pending)
        {
            await pending;
            return;
        }

        var initialization = InitializeNifViewerWebViewCoreAsync();
        _nifViewerWebViewInitializationTask = initialization;
        try
        {
            await initialization;
        }
        finally
        {
            if (ReferenceEquals(_nifViewerWebViewInitializationTask, initialization))
            {
                _nifViewerWebViewInitializationTask = null;
            }
        }
    }

    private async Task InitializeNifViewerWebViewCoreAsync()
    {
        try
        {
            await NifModelViewer.EnsureCoreWebView2Async();

            // EnsureCoreWebView2Async cannot be cancelled. Native rendering may become Ready, or
            // this tab may be disposed, while Chromium is starting. Mark the initialized host as
            // owned before doing any more work so the Ready/dispose path can close it, then honor
            // either terminal state here instead of resurrecting a visible fallback.
            _nifViewerWebViewInitialized = true;
            if (_nifViewerNativeReady || _nifViewerDisposed)
            {
                CloseNifViewerCompatibilityHost();
                return;
            }

            var assetsDir = Path.Combine(AppContext.BaseDirectory, "App", "Assets");
            NifModelViewer.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "nif-viewer-assets",
                assetsDir,
                CoreWebView2HostResourceAccessKind.Allow);

            NifModelViewer.CoreWebView2.Navigate(
#pragma warning disable S1075
                "https://nif-viewer-assets/npc-viewer.html"
#pragma warning restore S1075
            );

            if (_nifViewerNativeReady || _nifViewerDisposed)
            {
                CloseNifViewerCompatibilityHost();
                return;
            }

            NifModelViewer.Visibility = Visibility.Visible;

            // Set initial status after page loads. The WebView2 page renders its own
            // "Select a NIF file to view" message via setStatus, so hide the XAML
            // placeholder TextBlock to avoid rendering the same text twice stacked.
            NifModelViewer.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    await NifModelViewer.ExecuteScriptAsync("setStatus('Select a NIF file to view')");
                    NifViewerPlaceholderText.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    // Page may not have setStatus yet — leave the XAML placeholder up as a fallback.
                }
            };
        }
        catch (Exception ex)
        {
            if (_nifViewerDisposed) return;

            CloseNifViewerCompatibilityHost();
            NifViewerPlaceholderText.Text = $"WebView2 init failed: {ex.Message}";
        }
    }

    private async void NifViewerBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder != null)
        {
            await LoadNifSourceAsync(folder, isArchive: false);
        }
    }

    private async void NifViewerBrowseArchive_Click(object sender, RoutedEventArgs e)
    {
        var filePicker = new FileOpenPicker();
        filePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        filePicker.FileTypeFilter.Add(".bsa");
        filePicker.FileTypeFilter.Add(".ba2");
        InitializeWithWindow.Initialize(filePicker,
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await filePicker.PickSingleFileAsync();
        if (file != null)
        {
            await LoadNifSourceAsync(file.Path, isArchive: true);
        }
    }

    private async void NifViewerBrowseTextureArchive_Click(object sender, RoutedEventArgs e)
    {
        var filePicker = new FileOpenPicker();
        filePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        filePicker.FileTypeFilter.Add(".bsa");
        filePicker.FileTypeFilter.Add(".ba2");
        InitializeWithWindow.Initialize(filePicker,
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await filePicker.PickSingleFileAsync();
        if (file == null) return;

        NifViewerTextureOverrideTextBox.Text = file.Path;
        if (!string.IsNullOrEmpty(_nifViewer.CurrentPath))
        {
            await LoadNifSourceAsync(_nifViewer.CurrentPath, _nifViewer.IsArchive);
        }
    }

    private async Task LoadNifSourceAsync(string path, bool isArchive)
    {
        var sourceGeneration = unchecked(++_nifViewerLoadGeneration);
        _nifViewerSourceLoadingGeneration = sourceGeneration;
        _nifViewerScene = null;
        _nifViewer.ClearSource();
        NifSceneViewer.ClearScene();
        SetNifViewerGeometryWarning(null);
        NifViewerPathTextBox.Text = path;
        PopulateNifTree([]);
        NifViewerSearchBox.Text = string.Empty;
        NifViewerFileCount.Text = string.Empty;
        NifViewerTextureSourcesText.Text = "Texture sources: resolving...";
        ToolTipService.SetToolTip(NifViewerTextureSourcesText, null);
        NifViewerExportGlbButton.IsEnabled = false;
        NifViewerRenderPngButton.IsEnabled = false;
        NifViewerCaptureNativePngButton.IsEnabled = false;
        SetNifViewerSourceLoadingState(
            isLoading: true,
            isArchive
                ? "Opening archive and related asset indexes..."
                : "Opening folder and related asset indexes...");

        try
        {
            // Show source progress before draining a superseded model load so a large source begins
            // with immediate feedback rather than an apparently frozen file list.
            await CancelNifViewerLoadAndDrainAsync();
            if (sourceGeneration != _nifViewerLoadGeneration)
            {
                return;
            }

            var previousService = _nifBrowserService;
            _nifBrowserService = null;
            previousService?.Dispose();

            if (_nifViewerWebViewInitialized && !_nifViewerNativeReady)
            {
                try
                {
                    await NifModelViewer.ExecuteScriptAsync("clearModel()");
                }
                catch
                {
                    // A navigation still in progress will receive the next selected model instead.
                }
            }

            var overrideText = NifViewerTextureOverrideTextBox.Text;
            var progress = new Progress<NifViewerSourceLoadProgress>(sourceProgress =>
                UpdateNifViewerSourceProgress(sourceGeneration, sourceProgress));
            var result = await NifConverterWorkflowService.LoadSourceAsync(
                path,
                isArchive,
                overrideText,
                progress);
            if (sourceGeneration != _nifViewerLoadGeneration)
            {
                result.Service.Dispose();
                return;
            }

            _nifBrowserService = result.Service;
            var state = _nifViewer.ApplySource(path, isArchive, result);

            NifViewerTextureSourcesText.Text = string.IsNullOrEmpty(state.TexturePathsDisplay)
                ? "Texture sources: none detected"
                : $"Texture sources: {state.TexturePathsDisplay}";
            ToolTipService.SetToolTip(NifViewerTextureSourcesText, state.TexturePathsDisplay);

            PopulateNifTree(state.Items);
            NifViewerFileCount.Text = state.FileCountText;
        }
        catch (Exception ex)
        {
            if (sourceGeneration == _nifViewerLoadGeneration)
            {
                NifViewerTextureSourcesText.Text = "Texture sources: unavailable";
                NifViewerFileCount.Text = $"Error: {ex.Message}";
            }
        }
        finally
        {
            // A superseded source owns the panel and controls. Never let an older completion hide it.
            if (_nifViewerSourceLoadingGeneration == sourceGeneration)
            {
                _nifViewerSourceLoadingGeneration = 0;
                SetNifViewerSourceLoadingState(isLoading: false, status: null);
            }
        }
    }

    private void UpdateNifViewerSourceProgress(
        int sourceGeneration,
        NifViewerSourceLoadProgress progress)
    {
        // Progress<T> posts asynchronously to the UI dispatcher. This second gate also rejects
        // callbacks that were queued just before the operation's finally block completed.
        if (_nifViewerSourceLoadingGeneration != sourceGeneration ||
            _nifViewerLoadGeneration != sourceGeneration)
        {
            return;
        }

        switch (progress.Phase)
        {
            case NifViewerSourceLoadPhase.OpeningArchiveIndexes:
                SetNifViewerSourceProgressIndeterminate("Opening archive and related asset indexes...");
                break;
            case NifViewerSourceLoadPhase.OpeningDirectory:
                SetNifViewerSourceProgressIndeterminate("Opening folder and related asset indexes...");
                break;
            case NifViewerSourceLoadPhase.ScanningArchiveEntries:
            {
                var total = Math.Max(0, progress.TotalEntries ?? 0);
                NifViewerSourceProgressBar.IsIndeterminate = false;
                NifViewerSourceProgressBar.Maximum = Math.Max(1, total);
                NifViewerSourceProgressBar.Value = total == 0
                    ? 1
                    : Math.Clamp(progress.CurrentEntry, 0, total);
                NifViewerSourceProgressText.Text =
                    $"Scanning archive entries: {progress.CurrentEntry:N0} of {total:N0}; " +
                    $"{progress.NifFilesFound:N0} NIF files found.";
                break;
            }
            case NifViewerSourceLoadPhase.ScanningDirectory:
                SetNifViewerSourceProgressIndeterminate(
                    $"Scanning folder: {progress.NifFilesFound:N0} NIF files found...");
                break;
            case NifViewerSourceLoadPhase.BuildingTree:
                SetNifViewerSourceProgressIndeterminate(
                    $"Building mesh list for {progress.NifFilesFound:N0} NIF files...");
                break;
        }
    }

    private void SetNifViewerSourceProgressIndeterminate(string status)
    {
        NifViewerSourceProgressBar.IsIndeterminate = true;
        NifViewerSourceProgressBar.Maximum = 1;
        NifViewerSourceProgressBar.Value = 0;
        NifViewerSourceProgressText.Text = status;
    }

    private void SetNifViewerSourceLoadingState(bool isLoading, string? status)
    {
        NifViewerSourceProgressPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        if (isLoading && status is not null)
        {
            SetNifViewerSourceProgressIndeterminate(status);
        }
        else if (!isLoading)
        {
            NifViewerSourceProgressBar.IsIndeterminate = false;
            NifViewerSourceProgressBar.Value = 0;
        }

        NifViewerPathTextBox.IsEnabled = !isLoading;
        NifViewerSourceBrowseButton.IsEnabled = !isLoading;
        NifViewerTextureOverrideTextBox.IsEnabled = !isLoading;
        NifViewerTextureBrowseButton.IsEnabled = !isLoading;
        NifViewerSearchBox.IsEnabled = !isLoading;
        NifViewerTreeView.IsEnabled = !isLoading;
    }

    private void PopulateNifTree(List<NifTreeViewItem> items)
    {
        NifViewerTreeView.ItemsSource = items;
    }

    private void NifViewerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_nifViewerSourceLoadingGeneration != 0) return;
        PopulateNifTree(_nifViewer.FilterTree(NifViewerSearchBox.Text));
    }

    private async void NifViewerTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (_nifViewerSourceLoadingGeneration != 0) return;
        if (args.InvokedItem is not NifTreeViewItem item || item.IsDirectory) return;

        var load = LoadNifIntoViewerAsync(item);
        _nifViewerLoadTask = load;
        await load;
    }

    private async Task LoadNifIntoViewerAsync(NifTreeViewItem item)
    {
        await CancelNifViewerLoadAndDrainAsync();
        var service = _nifBrowserService;
        if (service == null) return;

        var loadCts = new CancellationTokenSource();
        _nifViewerLoadCts = loadCts;
        var cancellationToken = loadCts.Token;
        var generation = unchecked(++_nifViewerLoadGeneration);
        TaskCompletionSource<BethesdaSceneViewerRenderState>? nativeOutcome = null;

        _nifViewer.SelectNif(item);
        SetNifViewerGeometryWarning(null);
        NifModelLoadingRing.Visibility = Visibility.Visible;
        NifViewerPlaceholderText.Visibility = Visibility.Collapsed;

        try
        {
            var result = await NifConverterWorkflowService.LoadModelAsync(
                service,
                item,
                includeCompatibilityGlb: false,
                cancellationToken: cancellationToken);
            if (cancellationToken.IsCancellationRequested ||
                generation != _nifViewerLoadGeneration ||
                !ReferenceEquals(service, _nifBrowserService) ||
                !string.Equals(_nifViewer.SelectedNifPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (result.ErrorMessage != null)
            {
                _nifViewerScene = null;
                NifSceneViewer.ClearScene();
                NifViewerCaptureNativePngButton.IsEnabled = false;
                await SetNifViewerFallbackStatusAsync(result.ErrorMessage);
                return;
            }

            // Update info panel
            if (result.Info != null)
            {
                NifViewerInfoText.Text = NifConverterViewModel.FormatModelInfo(result.Info);
                NifViewerBlockTypesText.Text = NifConverterViewModel.FormatBlockTypes(result.Info);
            }

            SetNifViewerGeometryWarning(result.WarningMessage);

            if (result.Scene == null)
            {
                _nifViewerScene = null;
                NifSceneViewer.ClearScene();
                await SetNifViewerFallbackStatusAsync("No viewable geometry");
                NifViewerExportGlbButton.IsEnabled = false;
                NifViewerRenderPngButton.IsEnabled = false;
                NifViewerCaptureNativePngButton.IsEnabled = false;
                return;
            }

            if (!_nifViewerNativeReady)
            {
                nativeOutcome = new TaskCompletionSource<BethesdaSceneViewerRenderState>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _nifViewerNativeOutcome = nativeOutcome;
                _nifViewerNativeOutcomeScene = result.Scene;
                _nifViewerNativeOutcomeGeneration = generation;
            }

            _nifViewerScene = result.Scene;
            NifSceneViewer.SetScene(result.Scene);
            NifSceneViewer.FrameScene();
            NifSceneViewer.InvalidateViewport();

            // Yield the UI thread until this exact scene either survives its first Present or
            // faults. Starting Chromium here unconditionally would race ahead of CompositionTarget
            // and make every successful native load pay the WebView process/RAM cost.
            if (nativeOutcome is not null)
            {
                if (NifSceneViewer.RenderState == BethesdaSceneViewerRenderState.Faulted)
                {
                    nativeOutcome.TrySetResult(BethesdaSceneViewerRenderState.Faulted);
                }

                var nativeState = await nativeOutcome.Task.WaitAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested ||
                    generation != _nifViewerLoadGeneration ||
                    !ReferenceEquals(service, _nifBrowserService) ||
                    !ReferenceEquals(result.Scene, _nifViewerScene))
                {
                    return;
                }

                // Native is authoritative. Only an observed validation/setup/first-frame failure
                // initializes Chromium and serializes this exact already-built scene.
                if (nativeState == BethesdaSceneViewerRenderState.Faulted && !_nifViewerNativeReady)
                {
                    await InitializeNifViewerWebViewAsync();
                    if (cancellationToken.IsCancellationRequested ||
                        generation != _nifViewerLoadGeneration ||
                        !ReferenceEquals(service, _nifBrowserService) ||
                        !ReferenceEquals(result.Scene, _nifViewerScene))
                    {
                        return;
                    }

                    if (!_nifViewerNativeReady && _nifViewerWebViewInitialized)
                    {
                        try
                        {
                            await NifModelViewer.ExecuteScriptAsync("setStatus('Loading compatibility model...')");
                            var compatibilityGlb = await Task.Run(
                                () => service.ExportViewerSceneToGlb(result.Scene),
                                cancellationToken);
                            if (cancellationToken.IsCancellationRequested ||
                                generation != _nifViewerLoadGeneration ||
                                !ReferenceEquals(service, _nifBrowserService) ||
                                !ReferenceEquals(result.Scene, _nifViewerScene) ||
                                _nifViewerNativeReady)
                            {
                                return;
                            }

                            var base64 = Convert.ToBase64String(compatibilityGlb);
                            await NifModelViewer.ExecuteScriptAsync($"loadModel('{base64}')");
                        }
                        catch (Exception ex) when (_nifViewerNativeReady)
                        {
                            // Promotion closes the compatibility host. A racing script completion must not
                            // clear the already-valid native scene through the outer failure path.
                            Logger.Instance.Warn(
                                "NIF Viewer: compatibility load ended during native promotion: {0}",
                                ex.Message);
                        }
                    }
                }
            }

            NifViewerExportGlbButton.IsEnabled = true;
            NifViewerRenderPngButton.IsEnabled = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer source/selection owns the visible state.
        }
        catch (Exception ex)
        {
            if (generation == _nifViewerLoadGeneration && ReferenceEquals(service, _nifBrowserService))
            {
                if (!_nifViewerNativeReady)
                {
                    _nifViewerScene = null;
                    NifSceneViewer.ClearScene();
                    await SetNifViewerFallbackStatusAsync($"Error: {ex.Message}");
                    NifViewerExportGlbButton.IsEnabled = false;
                    NifViewerRenderPngButton.IsEnabled = false;
                    NifViewerCaptureNativePngButton.IsEnabled = false;
                }
                else
                {
                    Logger.Instance.Warn("NIF Viewer: post-promotion UI update failed: {0}", ex.Message);
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_nifViewerNativeOutcome, nativeOutcome))
            {
                _nifViewerNativeOutcome = null;
                _nifViewerNativeOutcomeScene = null;
            }

            if (ReferenceEquals(_nifViewerLoadCts, loadCts))
            {
                _nifViewerLoadCts = null;
                NifModelLoadingRing.Visibility = Visibility.Collapsed;
            }

            loadCts.Dispose();
        }
    }

    private async Task SetNifViewerFallbackStatusAsync(string message)
    {
        if (_nifViewerNativeReady) return;
        if (!_nifViewerWebViewInitialized)
        {
            NifViewerPlaceholderText.Text = message;
            NifViewerPlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            await NifModelViewer.ExecuteScriptAsync($"setStatus('{EscapeJsString(message)}')");
        }
        catch
        {
            NifViewerPlaceholderText.Text = message;
            NifViewerPlaceholderText.Visibility = Visibility.Visible;
        }
    }

    private async Task CancelNifViewerLoadAndDrainAsync()
    {
        // Snapshot the previous operation before the first await. The selection handler stores the
        // newly returned task as soon as CancelAsync yields; reading the field afterward would make
        // this operation await itself forever.
        var load = _nifViewerLoadTask;
        var cancellation = _nifViewerLoadCts;
        if (cancellation is not null && !cancellation.IsCancellationRequested)
        {
            await cancellation.CancelAsync();
        }

        if (load is null || load.IsCompleted) return;

        try
        {
            await load;
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer source/selection supersedes the load.
        }
    }

    private void NifSceneViewer_RenderStateChanged(
        object? sender,
        BethesdaSceneViewerRenderStateChangedEventArgs e)
    {
        CompleteNifViewerNativeOutcome(e.State);
        NifViewerCaptureNativePngButton.IsEnabled =
            e.State == BethesdaSceneViewerRenderState.Ready && _nifViewerScene is not null;

        if (e.State == BethesdaSceneViewerRenderState.Faulted)
        {
            SetNifViewerGeometryWarning(_nifViewerNativeReady
                ? $"The native renderer could not display this scene. {e.Message}"
                : $"Native renderer unavailable; opening the compatibility preview. {e.Message}");
            return;
        }

        if (e.State != BethesdaSceneViewerRenderState.Ready || _nifViewerNativeReady) return;

        _nifViewerNativeReady = true;
        NifModelViewer.Visibility = Visibility.Collapsed;
        CloseNifViewerCompatibilityHost();
        NifViewerPlaceholderText.Visibility = Visibility.Collapsed;
        if (_nifViewerScene is not null)
        {
            NifSceneViewer.SetScene(_nifViewerScene);
            NifSceneViewer.FrameScene();
            NifSceneViewer.InvalidateViewport();
        }
    }

    private void CompleteNifViewerNativeOutcome(BethesdaSceneViewerRenderState state)
    {
        if (state is not (BethesdaSceneViewerRenderState.Ready or BethesdaSceneViewerRenderState.Faulted) ||
            _nifViewerNativeOutcome is not { } outcome ||
            _nifViewerNativeOutcomeGeneration != _nifViewerLoadGeneration ||
            !ReferenceEquals(_nifViewerNativeOutcomeScene, _nifViewerScene))
        {
            return;
        }

        outcome.TrySetResult(state);
    }

    private void CloseNifViewerCompatibilityHost()
    {
        if (!_nifViewerWebViewInitialized) return;

        try
        {
            // Close releases the Chromium process, decoded GLB, and WebView compositor resources;
            // merely hiding the compatibility control would keep all of them resident.
            NifModelViewer.Close();
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("NIF Viewer: compatibility WebView close failed: {0}", ex.Message);
        }
        finally
        {
            _nifViewerWebViewInitialized = false;
            NifModelViewer.Visibility = Visibility.Collapsed;
        }
    }

    private async void NifViewerExportGlb_Click(object sender, RoutedEventArgs e)
    {
        if (_nifBrowserService == null || _nifViewer.SelectedNifPath == null) return;

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("GLB File", [".glb"]);
        picker.SuggestedFileName = Path.ChangeExtension(
            Path.GetFileName(_nifViewer.SelectedNifPath), ".glb");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        NifViewerExportGlbButton.IsEnabled = false;
        try
        {
            var build = await NifConverterWorkflowService.BuildGlbAsync(
                _nifBrowserService,
                _nifViewer.SelectedNifPath);
            SetNifViewerGeometryWarning(build?.ExternalGeometry.IncompleteWarningMessage);
            if (build?.GlbBytes is { } glbBytes)
            {
                await File.WriteAllBytesAsync(file.Path, glbBytes);
                StatusTextBlock.Text = build.ExternalGeometry.IsComplete
                    ? $"Exported: {file.Name}"
                    : $"Exported incomplete model: {file.Name}";
            }
            else
            {
                StatusTextBlock.Text = "No geometry to export.";
            }
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Export failed: {ex.Message}";
        }
        finally
        {
            NifViewerExportGlbButton.IsEnabled = true;
        }
    }

    private async void NifViewerRenderPng_Click(object sender, RoutedEventArgs e)
    {
        if (_nifBrowserService == null || _nifViewer.SelectedNifPath == null) return;

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        picker.SuggestedFileName = Path.ChangeExtension(
            Path.GetFileName(_nifViewer.SelectedNifPath), ".png");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        NifViewerRenderPngButton.IsEnabled = false;
        try
        {
            var spriteSize = NifConverterViewModel.ClampSpriteSize(NifViewerSizeNumberBox.Value);
            var camera = BuildNifViewerCameraConfig();
            var viewCount = await NifConverterWorkflowService.RenderPngViewsAsync(
                _nifBrowserService,
                _nifViewer.SelectedNifPath,
                file.Path,
                spriteSize,
                camera);

            StatusTextBlock.Text = NifConverterViewModel.FormatRenderStatus(viewCount, file.Name);
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Render failed: {ex.Message}";
        }
        finally
        {
            NifViewerRenderPngButton.IsEnabled = true;
        }
    }

    private async void NifViewerCaptureNativePng_Click(object sender, RoutedEventArgs e)
    {
        if (_nifViewerScene is null ||
            NifSceneViewer.RenderState != BethesdaSceneViewerRenderState.Ready)
        {
            return;
        }

        var outputPath = EnvironmentVariables.Get(EnvironmentVariables.Viewer.NativeCaptureOutput);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            var picker = new FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("PNG Image", [".png"]);
            picker.SuggestedFileName = Path.ChangeExtension(
                Path.GetFileName(_nifViewer.SelectedNifPath), ".native.png");
            InitializeWithWindow.Initialize(picker,
                WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            outputPath = file.Path;
        }
        else
        {
            outputPath = Path.GetFullPath(outputPath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }
        }

        NifViewerCaptureNativePngButton.IsEnabled = false;
        try
        {
            var pngBytes = await NifSceneViewer.CapturePngAsync();
            await File.WriteAllBytesAsync(outputPath, pngBytes);
            StatusTextBlock.Text = $"Captured native viewport: {Path.GetFileName(outputPath)}";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = $"Native capture failed: {ex.Message}";
        }
        finally
        {
            NifViewerCaptureNativePngButton.IsEnabled =
                _nifViewerScene is not null &&
                NifSceneViewer.RenderState == BethesdaSceneViewerRenderState.Ready;
        }
    }

    private void NifViewerElevationSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (NifViewerElevationLabel != null)
        {
            NifViewerElevationLabel.Text = $"{(int)e.NewValue}";
        }
    }

    private CameraConfig BuildNifViewerCameraConfig()
    {
        var perspective = "front";
        if (NifViewerPerspectiveComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            perspective = tag;
        }

        var elevation = (float)NifViewerElevationSlider.Value;

        return NifConverterViewModel.BuildCameraConfig(perspective, elevation);
    }

    private static string EscapeJsString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "");
    }

    private void SetNifViewerGeometryWarning(string? message)
    {
        NifViewerGeometryWarning.Message = message ?? string.Empty;
        NifViewerGeometryWarning.IsOpen = !string.IsNullOrWhiteSpace(message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_nifViewerDisposed)
        {
            _nifViewerDisposed = true;
            unchecked { _nifViewerLoadGeneration++; }
            _nifViewerLoadCts?.Cancel();
            _nifViewerLoadCts = null;
            NifSceneViewer.RenderStateChanged -= NifSceneViewer_RenderStateChanged;
            NifSceneViewer.ClearScene();
            NifSceneViewer.Dispose();
            CloseNifViewerCompatibilityHost();

            var service = _nifBrowserService;
            _nifBrowserService = null;
            var load = _nifViewerLoadTask;
            if (service is not null && load is { IsCompleted: false })
            {
                _ = load.ContinueWith(
                    static (_, state) => ((NifBrowserService)state!).Dispose(),
                    service,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                service?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    #endregion
}
