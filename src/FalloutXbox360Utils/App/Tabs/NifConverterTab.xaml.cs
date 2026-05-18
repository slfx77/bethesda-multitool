using Windows.Storage.Pickers;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     Tab for batch converting Xbox 360 NIF files to PC format.
/// </summary>
public sealed partial class NifConverterTab : NifFileConverterBase
{
    private bool _dependencyCheckDone;

    // NIF Viewer state
    private NifBrowserService? _nifBrowserService;
    private readonly NifConverterViewModel _nifViewer = new();
    private bool _nifViewerWebViewInitialized;

    public NifConverterTab()
    {
        InitializeComponent();
        ReorderTabsForModelWorkflow();
        SetupTextBoxContextMenus();
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

        if (verbose) Core.Logger.Instance.Level = Core.LogLevel.Debug;

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
            StatusTextBlock.Text = "Conversion cancelled.";
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
        StatusTextBlock.Text = "Cancelling...";
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
        // Lazy init WebView when viewer tab is first selected
        if (ReferenceEquals(NifTabView.SelectedItem, NifViewerTab) && !_nifViewerWebViewInitialized)
        {
            _ = InitializeNifViewerWebViewAsync();
        }
    }

    private async Task InitializeNifViewerWebViewAsync()
    {
        if (_nifViewerWebViewInitialized) return;

        try
        {
            await NifModelViewer.EnsureCoreWebView2Async();

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
            _nifViewerWebViewInitialized = true;

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
            NifViewerPlaceholderText.Text = $"WebView2 init failed: {ex.Message}";
        }
    }

    private async void NifViewerBrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder != null)
        {
            await LoadNifSourceAsync(folder, isBsa: false);
        }
    }

    private async void NifViewerBrowseBsa_Click(object sender, RoutedEventArgs e)
    {
        var filePicker = new FileOpenPicker();
        filePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        filePicker.FileTypeFilter.Add(".bsa");
        InitializeWithWindow.Initialize(filePicker,
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await filePicker.PickSingleFileAsync();
        if (file != null)
        {
            await LoadNifSourceAsync(file.Path, isBsa: true);
        }
    }

    private async void NifViewerBrowseTextureBsa_Click(object sender, RoutedEventArgs e)
    {
        var filePicker = new FileOpenPicker();
        filePicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        filePicker.FileTypeFilter.Add(".bsa");
        InitializeWithWindow.Initialize(filePicker,
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await filePicker.PickSingleFileAsync();
        if (file == null) return;

        NifViewerTextureBsaTextBox.Text = file.Path;
        if (!string.IsNullOrEmpty(_nifViewer.CurrentPath))
        {
            await LoadNifSourceAsync(_nifViewer.CurrentPath, _nifViewer.IsBsa);
        }
    }

    private async Task LoadNifSourceAsync(string path, bool isBsa)
    {
        _nifBrowserService?.Dispose();
        NifViewerPathTextBox.Text = path;

        try
        {
            var overrideText = NifViewerTextureBsaTextBox.Text?.Trim();
            var hasOverride = !string.IsNullOrEmpty(overrideText);
            var result = await NifConverterWorkflowService.LoadSourceAsync(path, isBsa, overrideText);
            _nifBrowserService = result.Service;
            var state = _nifViewer.ApplySource(path, isBsa, result, hasOverride);

            // Reflect the auto-detected textures path in the UI when the user hasn't overridden it.
            if (state.TexturePathsDisplay != null)
            {
                NifViewerTextureBsaTextBox.Text = state.TexturePathsDisplay;
            }

            PopulateNifTree(state.Items);
            NifViewerFileCount.Text = state.FileCountText;
        }
        catch (Exception ex)
        {
            NifViewerFileCount.Text = $"Error: {ex.Message}";
        }
    }

    private void PopulateNifTree(List<NifTreeViewItem> items)
    {
        NifViewerTreeView.ItemsSource = items;
    }

    private void NifViewerSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PopulateNifTree(_nifViewer.FilterTree(NifViewerSearchBox.Text));
    }

    private async void NifViewerTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not NifTreeViewItem item || item.IsDirectory) return;

        await LoadNifIntoViewerAsync(item);
    }

    private async Task LoadNifIntoViewerAsync(NifTreeViewItem item)
    {
        if (_nifBrowserService == null || !_nifViewerWebViewInitialized) return;

        _nifViewer.SelectNif(item);
        NifModelLoadingRing.Visibility = Visibility.Visible;
        NifViewerPlaceholderText.Visibility = Visibility.Collapsed;

        try
        {
            await NifModelViewer.ExecuteScriptAsync("setStatus('Loading model...')");

            var result = await NifConverterWorkflowService.LoadModelAsync(_nifBrowserService, item);
            if (result.ErrorMessage != null)
            {
                await NifModelViewer.ExecuteScriptAsync($"setStatus('{EscapeJsString(result.ErrorMessage)}')");
                NifModelLoadingRing.Visibility = Visibility.Collapsed;
                return;
            }

            // Update info panel
            if (result.Info != null)
            {
                NifViewerInfoText.Text = NifConverterViewModel.FormatModelInfo(result.Info);
                NifViewerBlockTypesText.Text = NifConverterViewModel.FormatBlockTypes(result.Info);
            }

            // Build GLB for 3D viewer
            if (result.GlbBytes == null)
            {
                await NifModelViewer.ExecuteScriptAsync("setStatus('No exportable geometry')");
                NifModelLoadingRing.Visibility = Visibility.Collapsed;
                NifViewerExportGlbButton.IsEnabled = false;
                NifViewerRenderPngButton.IsEnabled = false;
                return;
            }

            var base64 = Convert.ToBase64String(result.GlbBytes);
            await NifModelViewer.ExecuteScriptAsync($"loadModel('{base64}')");

            NifViewerExportGlbButton.IsEnabled = true;
            NifViewerRenderPngButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            try
            {
                await NifModelViewer.ExecuteScriptAsync(
                    $"setStatus('Error: {EscapeJsString(ex.Message)}')");
            }
            catch
            {
                // WebView may not be ready
            }

            NifViewerExportGlbButton.IsEnabled = false;
            NifViewerRenderPngButton.IsEnabled = false;
        }
        finally
        {
            NifModelLoadingRing.Visibility = Visibility.Collapsed;
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
            var glbBytes = await NifConverterWorkflowService.BuildGlbAsync(
                _nifBrowserService,
                _nifViewer.SelectedNifPath);
            if (glbBytes != null)
            {
                await File.WriteAllBytesAsync(file.Path, glbBytes);
                StatusTextBlock.Text = $"Exported: {file.Name}";
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _nifBrowserService?.Dispose();
        }

        base.Dispose(disposing);
    }

    #endregion
}
