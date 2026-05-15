using Windows.Storage.Pickers;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
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
    private string? _currentNifViewerPath;
    private bool _dependencyCheckDone;

    // NIF Viewer state
    private NifBrowserService? _nifBrowserService;
    private List<NifTreeViewItem>? _nifViewerAllItems;
    private bool _nifViewerIsBsa;
    private string? _nifViewerSourcePath;
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
        return await Task.Run(() =>
        {
            var nifFiles = Directory.EnumerateFiles(directory, "*.nif", SearchOption.AllDirectories).ToList();
            if (nifFiles.Count == 0 || cancellationToken.IsCancellationRequested) return [];

            InitializeScanProgress(nifFiles.Count);

            var entries = new NifFileEntry[nifFiles.Count];
            var processedCount = 0;
            var dispatcher = DispatcherQueue;

            Parallel.ForEach(
                Enumerable.Range(0, nifFiles.Count),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Environment.ProcessorCount * 2,
                    CancellationToken = cancellationToken
                },
                index =>
                {
                    var filePath = nifFiles[index];
                    var relativePath = Path.GetRelativePath(directory, filePath);
                    var (fileSize, formatDesc) = ReadNifFileHeaderSync(filePath);
                    var isXbox360 = formatDesc == "Xbox 360 (BE)";

                    entries[index] = new NifFileEntry
                    {
                        FullPath = filePath,
                        RelativePath = relativePath,
                        FileSize = fileSize,
                        FormatDescription = formatDesc,
                        IsSelected = isXbox360
                    };

                    var current = Interlocked.Increment(ref processedCount);
                    if (current % 100 == 0 || current == nifFiles.Count)
                        dispatcher.TryEnqueue(() => ConversionProgressBar.Value = current);
                });

            return entries;
        }, cancellationToken);
    }

    private static (long fileSize, string formatDesc) ReadNifFileHeaderSync(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;

            Span<byte> headerBytes = stackalloc byte[50];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64);
            var bytesRead = fs.Read(headerBytes);

            var formatDesc = DetermineNifFormat(headerBytes[..bytesRead]);
            return (fileSize, formatDesc);
        }
        catch
        {
            return (0, "Error");
        }
    }

    private static string DetermineNifFormat(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < 50) return "Invalid";

        var newlinePos = headerBytes[..50].IndexOf((byte)0x0A);
        if (newlinePos <= 0 || newlinePos + 5 >= 50) return "Invalid";

        return headerBytes[newlinePos + 5] switch
        {
            0 => "Xbox 360 (BE)",
            1 => "PC (LE)",
            _ => "Unknown"
        };
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

        var outputDir = OutputDirectoryTextBox.Text;
        var inputDir = InputDirectoryTextBox.Text;
        var preserveStructure = PreserveStructureCheckBox.IsChecked == true;
        var overwrite = OverwriteExistingCheckBox.IsChecked == true;
        var verbose = VerboseOutputCheckBox.IsChecked == true;

        if (verbose) Core.Logger.Instance.Level = Core.LogLevel.Debug;

        ConversionCts = new CancellationTokenSource();
        UpdateButtonStates();

        ConversionProgressBar.Visibility = Visibility.Visible;
        ConversionProgressBar.IsIndeterminate = false;
        ConversionProgressBar.Maximum = selectedFiles.Count;
        ConversionProgressBar.Value = 0;

        var converted = 0;
        var skipped = 0;
        var failed = 0;

        try
        {
            Directory.CreateDirectory(outputDir);

            for (var i = 0; i < selectedFiles.Count; i++)
            {
                if (ConversionCts.Token.IsCancellationRequested) break;

                var file = selectedFiles[i];
                StatusTextBlock.Text = $"Converting {i + 1}/{selectedFiles.Count}: {file.RelativePath}";
                file.Status = "Converting...";

                try
                {
                    string outputPath;
                    if (preserveStructure)
                    {
                        var relativePath = Path.GetRelativePath(inputDir, file.FullPath);
                        outputPath = Path.Combine(outputDir, relativePath);
                        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                    }
                    else
                    {
                        outputPath = Path.Combine(outputDir, Path.GetFileName(file.FullPath));
                    }

                    if (File.Exists(outputPath) && !overwrite)
                    {
                        file.Status = "Skipped (exists)";
                        skipped++;
                        continue;
                    }

                    var inputData = await File.ReadAllBytesAsync(file.FullPath, ConversionCts.Token);
                    var result = await Task.Run(() => NifConverter.Convert(inputData), ConversionCts.Token);

                    if (result.Success && result.OutputData != null)
                    {
                        await File.WriteAllBytesAsync(outputPath, result.OutputData, ConversionCts.Token);
                        file.Status = "Converted";
                        converted++;
                    }
                    else
                    {
                        file.Status = result.ErrorMessage ?? "Failed";
                        failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    file.Status = "Cancelled";
                    throw;
                }
                catch (Exception ex)
                {
                    file.Status = $"Error: {ex.Message}";
                    failed++;
                }

                ConversionProgressBar.Value = i + 1;
            }

            StatusTextBlock.Text = $"Conversion complete. Converted: {converted}, Skipped: {skipped}, Failed: {failed}";
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
        if (!string.IsNullOrEmpty(_nifViewerSourcePath))
        {
            await LoadNifSourceAsync(_nifViewerSourcePath, _nifViewerIsBsa);
        }
    }

    private async Task LoadNifSourceAsync(string path, bool isBsa)
    {
        _nifBrowserService?.Dispose();
        NifViewerPathTextBox.Text = path;
        _nifViewerSourcePath = path;
        _nifViewerIsBsa = isBsa;

        try
        {
            var overrideText = NifViewerTextureBsaTextBox.Text?.Trim();
            var hasOverride = !string.IsNullOrEmpty(overrideText);
            var texturePathsOverride = hasOverride ? new[] { overrideText! } : null;

            _nifBrowserService = isBsa
                ? await Task.Run(() => NifBrowserService.CreateFromBsa(path, texturePathsOverride))
                : await Task.Run(() => NifBrowserService.CreateFromDirectory(path, texturePathsOverride));

            // Reflect the auto-detected textures path in the UI when the user hasn't overridden it.
            if (!hasOverride)
            {
                NifViewerTextureBsaTextBox.Text = string.Join("; ", _nifBrowserService.TexturePaths);
            }

            var entries = await Task.Run(() => _nifBrowserService.ListNifFiles());
            _nifViewerAllItems = NifTreeViewItem.FromTreeEntries(entries);
            PopulateNifTree(_nifViewerAllItems);

            var fileCount = _nifViewerAllItems.Sum(i => i.IsDirectory ? i.Children.Count : 1);
            NifViewerFileCount.Text = $"{fileCount} NIF files";
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
        if (_nifViewerAllItems == null) return;

        var search = NifViewerSearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(search))
        {
            PopulateNifTree(_nifViewerAllItems);
            return;
        }

        var filtered = new List<NifTreeViewItem>();
        foreach (var item in _nifViewerAllItems)
        {
            if (item.IsDirectory)
            {
                var matchingChildren = item.Children
                    .Where(c => c.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (matchingChildren.Count > 0)
                {
                    var clone = new NifTreeViewItem
                    {
                        DisplayName = item.DisplayName,
                        FullPath = item.FullPath,
                        IsDirectory = true,
                        IsExpanded = true
                    };
                    foreach (var child in matchingChildren)
                    {
                        clone.Children.Add(child);
                    }

                    filtered.Add(clone);
                }
            }
            else if (item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(item);
            }
        }

        PopulateNifTree(filtered);
    }

    private async void NifViewerTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not NifTreeViewItem item || item.IsDirectory) return;

        await LoadNifIntoViewerAsync(item);
    }

    private async Task LoadNifIntoViewerAsync(NifTreeViewItem item)
    {
        if (_nifBrowserService == null || !_nifViewerWebViewInitialized) return;

        _currentNifViewerPath = item.FullPath;
        NifModelLoadingRing.Visibility = Visibility.Visible;
        NifViewerPlaceholderText.Visibility = Visibility.Collapsed;

        try
        {
            await NifModelViewer.ExecuteScriptAsync("setStatus('Loading model...')");

            var nifData = await Task.Run(() => _nifBrowserService.ReadNifData(item.FullPath));
            if (nifData == null)
            {
                await NifModelViewer.ExecuteScriptAsync("setStatus('Failed to read NIF file')");
                NifModelLoadingRing.Visibility = Visibility.Collapsed;
                return;
            }

            // Update info panel
            var info = await Task.Run(() => NifBrowserService.GetNifInfo(nifData, item.DisplayName));
            if (info != null)
            {
                NifViewerInfoText.Text =
                    $"File: {info.FileName}\n" +
                    $"Size: {info.FileSize:N0} bytes\n" +
                    $"Format: {info.Format}\n" +
                    $"Blocks: {info.BlockCount}\n" +
                    $"BS Version: {info.BsVersion}\n" +
                    $"User Version: {info.UserVersion}";
                NifViewerBlockTypesText.Text = string.Join(", ", info.BlockTypeNames);
            }

            // Build GLB for 3D viewer
            var glbBytes = await Task.Run(() => _nifBrowserService.BuildGlb(nifData, item.DisplayName));
            if (glbBytes == null)
            {
                await NifModelViewer.ExecuteScriptAsync("setStatus('No exportable geometry')");
                NifModelLoadingRing.Visibility = Visibility.Collapsed;
                NifViewerExportGlbButton.IsEnabled = false;
                NifViewerRenderPngButton.IsEnabled = false;
                return;
            }

            var base64 = Convert.ToBase64String(glbBytes);
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
        if (_nifBrowserService == null || _currentNifViewerPath == null) return;

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("GLB File", [".glb"]);
        picker.SuggestedFileName = Path.ChangeExtension(
            Path.GetFileName(_currentNifViewerPath), ".glb");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        NifViewerExportGlbButton.IsEnabled = false;
        try
        {
            var nifData = await Task.Run(() => _nifBrowserService.ReadNifData(_currentNifViewerPath));
            if (nifData == null)
            {
                StatusTextBlock.Text = "Failed to read NIF file.";
                return;
            }

            var glbBytes = await Task.Run(() => _nifBrowserService.BuildGlb(nifData, _currentNifViewerPath));
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
        if (_nifBrowserService == null || _currentNifViewerPath == null) return;

        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        picker.SuggestedFileName = Path.ChangeExtension(
            Path.GetFileName(_currentNifViewerPath), ".png");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        NifViewerRenderPngButton.IsEnabled = false;
        try
        {
            var nifData = await Task.Run(() => _nifBrowserService.ReadNifData(_currentNifViewerPath));
            if (nifData == null)
            {
                StatusTextBlock.Text = "Failed to read NIF file.";
                return;
            }

            var spriteSize = Math.Clamp((int)NifViewerSizeNumberBox.Value, 64, 4096);
            var camera = BuildNifViewerCameraConfig();
            var views = camera.ResolveViews(defaultAzimuth: 90f);

            foreach (var (suffix, azimuth, elevation) in views)
            {
                var pngBytes = await Task.Run(() =>
                    _nifBrowserService.RenderPng(nifData, _currentNifViewerPath, spriteSize, azimuth, elevation));

                if (pngBytes != null)
                {
                    var outputPath = views.Length > 1
                        ? Path.Combine(
                            Path.GetDirectoryName(file.Path) ?? ".",
                            Path.GetFileNameWithoutExtension(file.Path) + suffix + ".png")
                        : file.Path;
                    await File.WriteAllBytesAsync(outputPath, pngBytes);
                }
            }

            // FileSavePicker reserves the chosen path. In multi-view mode we write only to
            // suffixed filenames, so delete the empty placeholder left at the base path.
            if (views.Length > 1)
            {
                try
                {
                    File.Delete(file.Path);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only; export already wrote the suffixed views.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort cleanup only; export already wrote the suffixed views.
                }
            }

            StatusTextBlock.Text = $"Rendered: {(views.Length > 1 ? $"{views.Length} views" : file.Name)}";
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

        return perspective switch
        {
            "iso" => new CameraConfig
            {
                Isometric = true, ElevationDeg = elevation, ElevationOverridden = true
            },
            "side" => new CameraConfig { SideProfile = true },
            "trimetric" => new CameraConfig { Trimetric = true },
            _ => new CameraConfig { ElevationDeg = elevation, ElevationOverridden = true }
        };
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
