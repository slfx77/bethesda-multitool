using System.ComponentModel;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Xbox360MemoryCarver.Core.Converters;

namespace Xbox360MemoryCarver;

/// <summary>
///     Data model for DDX file entries in the list.
/// </summary>
public sealed class DdxFileEntry : INotifyPropertyChanged
{
    // Brushes are created lazily on first access (which happens on UI thread via binding)
    private static SolidColorBrush? _grayBrush;
    private static SolidColorBrush? _greenBrush;
    private static SolidColorBrush? _orangeBrush;
    private static SolidColorBrush? _redBrush;
    private static SolidColorBrush? _yellowBrush;
    private static SolidColorBrush? _blueBrush;

    private string _formatDescription = "";
    private bool _isSelected = true;
    private string _status = "Pending";

    private static SolidColorBrush GrayBrush => _grayBrush ??= new SolidColorBrush(Colors.Gray);
    private static SolidColorBrush GreenBrush => _greenBrush ??= new SolidColorBrush(Colors.Green);
    private static SolidColorBrush OrangeBrush => _orangeBrush ??= new SolidColorBrush(Colors.Orange);
    private static SolidColorBrush RedBrush => _redBrush ??= new SolidColorBrush(Colors.Red);
    private static SolidColorBrush YellowBrush => _yellowBrush ??= new SolidColorBrush(Colors.Yellow);
    private static SolidColorBrush BlueBrush => _blueBrush ??= new SolidColorBrush(Colors.DodgerBlue);

    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required long FileSize { get; init; }

    public string FileSizeDisplay => FileSize switch
    {
        < 1024 => $"{FileSize} B",
        < 1024 * 1024 => $"{FileSize / 1024.0:F1} KB",
        _ => $"{FileSize / (1024.0 * 1024.0):F2} MB"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusColor)); // Computed from Status
            }
        }
    }

    /// <summary>
    ///     Computed color based on Status. Evaluated on UI thread when binding reads it.
    /// </summary>
    public SolidColorBrush StatusColor => _status switch
    {
        "Converted" => GreenBrush,
        "Converting..." => YellowBrush,
        "Cancelled" => OrangeBrush,
        "Skipped (exists)" or "Pending" => GrayBrush,
        _ when _status.StartsWith("Error", StringComparison.Ordinal) => RedBrush,
        _ when _status.StartsWith("Failed", StringComparison.Ordinal) => RedBrush,
        _ => GrayBrush
    };

    public string FormatDescription
    {
        get => _formatDescription;
        set
        {
            if (_formatDescription != value)
            {
                _formatDescription = value;
                OnPropertyChanged(nameof(FormatDescription));
                OnPropertyChanged(nameof(FormatColor)); // Computed from FormatDescription
            }
        }
    }

    /// <summary>
    ///     Computed color based on FormatDescription. Evaluated on UI thread when binding reads it.
    /// </summary>
    public SolidColorBrush FormatColor => _formatDescription switch
    {
        "3XDO" => OrangeBrush,
        "3XDR" => BlueBrush,
        "Invalid" or "Error" => RedBrush,
        _ => GrayBrush
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
///     Tab for batch converting Xbox 360 DDX texture files to DDS format.
///     Uses DDXConv by kran27 for conversion.
/// </summary>
public sealed partial class DdxConverterTab : UserControl, IDisposable
{
    private readonly List<DdxFileEntry> _allDdxFiles = [];
    private readonly DdxFilesSorter _sorter = new();
    private CancellationTokenSource? _cts;
    private List<DdxFileEntry> _ddxFiles = [];
    private bool _dependencyCheckDone;
    private CancellationTokenSource? _scanCts;

    public DdxConverterTab()
    {
        InitializeComponent();
        SetupTextBoxContextMenus();
        Loaded += DdxConverterTab_Loaded;
    }

    /// <summary>
    ///     Helper to route status text to the global status bar.
    /// </summary>
#pragma warning disable CA1822, S2325
    private StatusTextHelper StatusTextBlock => new();
#pragma warning restore CA1822, S2325

    public void Dispose()
    {
        _cts?.Dispose();
        _scanCts?.Dispose();
    }

    private async void DdxConverterTab_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DdxConverterTab_Loaded;

        // Check dependencies on first load
        if (!_dependencyCheckDone)
        {
            _dependencyCheckDone = true;
            await CheckDependenciesAsync();
        }
    }

    private async Task CheckDependenciesAsync()
    {
        // Only show the dialog once per session
        if (DependencyChecker.DdxConverterDependenciesShown) return;

        // Small delay to ensure the UI is fully loaded
        await Task.Delay(100);

        var result = DependencyChecker.CheckDdxConverterDependencies();
        if (!result.AllAvailable)
        {
            DependencyChecker.DdxConverterDependenciesShown = true;
            await DependencyDialogHelper.ShowIfMissingAsync(result, XamlRoot);
        }
    }

    private void SetupTextBoxContextMenus()
    {
        TextBoxContextMenuHelper.AttachContextMenu(InputDirectoryTextBox);
        TextBoxContextMenuHelper.AttachContextMenu(OutputDirectoryTextBox);
    }

    private void UpdateButtonStates()
    {
        var hasOutput = !string.IsNullOrEmpty(OutputDirectoryTextBox.Text);
        var hasSelected = _ddxFiles.Any(f => f.IsSelected);
        ConvertButton.IsEnabled = hasOutput && hasSelected && (_cts == null || _cts.IsCancellationRequested);
        CancelButton.IsEnabled = _cts != null && !_cts.IsCancellationRequested;
    }

    private void UpdateFileCount()
    {
        var total = _ddxFiles.Count;
        var selected = _ddxFiles.Count(f => f.IsSelected);
        StatusTextBlock.Text = $"{selected} of {total} files selected";
    }

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(global::Xbox360MemoryCarver.App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        InputDirectoryTextBox.Text = folder.Path;
        OutputDirectoryTextBox.Text = Path.Combine(folder.Path, "converted_dds");
    }

    private async void InputDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Cancel any previous scan
        if (_scanCts != null)
        {
            await _scanCts.CancelAsync();
            _scanCts.Dispose();
            _scanCts = null;
        }

        var path = InputDirectoryTextBox.Text;
        if (Directory.Exists(path))
        {
            OutputDirectoryTextBox.Text = Path.Combine(path, "converted_dds");
            await ScanForDdxFilesAsync(path);
        }
        else
        {
            _ddxFiles = [];
            _allDdxFiles.Clear();
            DdxFilesListView.ItemsSource = null;
            _sorter.Reset();
            UpdateSortIcons();
            UpdateFileCount();
            UpdateButtonStates();
        }
    }

    private void OutputDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateButtonStates();
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(global::Xbox360MemoryCarver.App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            OutputDirectoryTextBox.Text = folder.Path;
            UpdateButtonStates();
        }
    }

    private async Task ScanForDdxFilesAsync(string directory)
    {
        // Create new cancellation token for this scan
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        // Clear previous data and force release of old entries
        _ddxFiles = [];
        _allDdxFiles.Clear();
        _allDdxFiles.TrimExcess(); // Release memory from previous scan
        DdxFilesListView.ItemsSource = null;
        _sorter.Reset();
        UpdateSortIcons();
        StatusTextBlock.Text = "Scanning for DDX files...";

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
            // Scan and create entries on background thread
            var entries = await ScanAndCreateDdxEntriesAsync(directory, token);

            // Check if cancelled before updating UI
            if (token.IsCancellationRequested) return;

            // Only the ItemsSource assignment happens on UI thread
            _allDdxFiles.Clear();
            _allDdxFiles.Capacity = entries.Length;
            _allDdxFiles.AddRange(entries);
            _ddxFiles = new List<DdxFileEntry>(_allDdxFiles);
            DdxFilesListView.ItemsSource = _ddxFiles;

            UpdateFileCount();
            UpdateButtonStates();

            var xdoCount = _ddxFiles.Count(f => f.FormatDescription == "3XDO");
            var xdrCount = _ddxFiles.Count(f => f.FormatDescription == "3XDR");
            StatusTextBlock.Text =
                $"Found {_ddxFiles.Count} DDX files. {xdoCount} 3XDO, {xdrCount} 3XDR.";
        }
        catch (OperationCanceledException)
        {
            // Scan was cancelled, another scan is starting
            StatusTextBlock.Text = "Scan cancelled.";
        }
        finally
        {
            ConversionProgressBar.Visibility = Visibility.Collapsed;
            ConversionProgressBar.IsIndeterminate = false;
        }
    }

    private async Task<DdxFileEntry[]> ScanAndCreateDdxEntriesAsync(string directory,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var ddxFiles = Directory.EnumerateFiles(directory, "*.ddx", SearchOption.AllDirectories).ToArray();

            if (ddxFiles.Length == 0) return Array.Empty<DdxFileEntry>();

            cancellationToken.ThrowIfCancellationRequested();

            InitializeScanProgress(ddxFiles.Length);

            var entries = new DdxFileEntry[ddxFiles.Length];
            var processedCount = 0;

            // Use Parallel.ForEach with synchronous I/O - much faster for small reads
            Parallel.ForEach(
                Enumerable.Range(0, ddxFiles.Length),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Environment.ProcessorCount * 2
                },
                index =>
                {
                    var filePath = ddxFiles[index];
                    var relativePath = Path.GetRelativePath(directory, filePath);
                    var (fileSize, formatDesc) = ReadDdxFileHeaderSync(filePath);

                    entries[index] = new DdxFileEntry
                    {
                        FullPath = filePath,
                        RelativePath = relativePath,
                        FileSize = fileSize,
                        FormatDescription = formatDesc,
                        IsSelected = formatDesc is "3XDO" or "3XDR"
                    };

                    // Update progress every 100 files
                    var current = Interlocked.Increment(ref processedCount);
                    if (current % 100 == 0 || current == ddxFiles.Length)
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (current > ConversionProgressBar.Value) ConversionProgressBar.Value = current;
                        });
                });

            cancellationToken.ThrowIfCancellationRequested();
            return entries;
        }, cancellationToken);
    }

    private static (long fileSize, string formatDesc) ReadDdxFileHeaderSync(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;

            Span<byte> header = stackalloc byte[4];
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4,
                FileOptions.SequentialScan);
            var bytesRead = fs.Read(header);

            var formatDesc = bytesRead < 4 ? "Invalid" : DetermineDdxFormat(header);
            return (fileSize, formatDesc);
        }
        catch
        {
            return (0, "Error");
        }
    }

    private static string DetermineDdxFormat(ReadOnlySpan<byte> header)
    {
        if (header[0] == '3' && header[1] == 'X' && header[2] == 'D')
            return header[3] switch
            {
                (byte)'O' => "3XDO",
                (byte)'R' => "3XDR",
                _ => "Invalid"
            };

        return "Invalid";
    }

    private void InitializeScanProgress(int fileCount)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ConversionProgressBar.IsIndeterminate = false;
            ConversionProgressBar.Maximum = fileCount;
            ConversionProgressBar.Value = 0;
            StatusTextBlock.Text = $"Scanning {fileCount} DDX files...";
        });
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in _ddxFiles) file.IsSelected = true;

        UpdateFileCount();
        UpdateButtonStates();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in _ddxFiles) file.IsSelected = false;

        UpdateFileCount();
        UpdateButtonStates();
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = _ddxFiles.Where(f => f.IsSelected).ToList();
        if (selectedFiles.Count == 0)
        {
            await ShowDialogAsync("No Files Selected", "Please select at least one DDX file to convert.");
            return;
        }

        // Check if DDXConv is available
        if (!DdxSubprocessConverter.IsAvailable())
        {
            await ShowDialogAsync("DDXConv Not Found",
                "DDXConv.exe was not found. Please ensure it is built and available in the expected location.");
            return;
        }

        var outputDir = OutputDirectoryTextBox.Text;
        var inputDir = InputDirectoryTextBox.Text;
        var preserveStructure = PreserveStructureCheckBox.IsChecked == true;
        var overwrite = OverwriteExistingCheckBox.IsChecked == true;

        DdxSubprocessConverter converter;
        try
        {
            converter = new DdxSubprocessConverter();
        }
        catch (FileNotFoundException ex)
        {
            await ShowDialogAsync("DDXConv Not Found", ex.Message);
            return;
        }

        _cts = new CancellationTokenSource();
        UpdateButtonStates();

        ConversionProgressBar.Visibility = Visibility.Visible;
        ConversionProgressBar.IsIndeterminate = false;
        ConversionProgressBar.Maximum = selectedFiles.Count;
        ConversionProgressBar.Value = 0;

        var converted = 0;
        var skipped = 0;
        var failed = 0;
        var unsupported = 0;
        var notSelected = 0;

        // Mark unchecked files as "Skipped (not selected)"
        foreach (var file in _ddxFiles.Where(f => !f.IsSelected))
        {
            file.Status = "Skipped (not selected)";
            notSelected++;
        }

        // Pre-process: mark unsupported files, check for existing outputs, prepare entries for conversion
        var filesToConvert = new List<DdxFileEntry>();
        var filePathToEntry = new Dictionary<string, DdxFileEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in selectedFiles)
        {
            // Calculate expected output path
            string outputPath;
            if (preserveStructure)
            {
                var relativePath = Path.GetRelativePath(inputDir, file.FullPath);
                var ddsRelativePath = Path.ChangeExtension(relativePath, ".dds");
                outputPath = Path.Combine(outputDir, ddsRelativePath);
            }
            else
            {
                var fileName = Path.ChangeExtension(Path.GetFileName(file.FullPath), ".dds");
                outputPath = Path.Combine(outputDir, fileName);
            }

            // Check if output exists
            if (File.Exists(outputPath) && !overwrite)
            {
                file.Status = "Skipped (exists)";
                skipped++;
                continue;
            }

            // Add to conversion list
            file.Status = "Queued";
            filesToConvert.Add(file);
            filePathToEntry[file.FullPath] = file;
        }

        // Update progress for pre-processed files
        ConversionProgressBar.Value = skipped + unsupported;

        if (filesToConvert.Count == 0)
        {
            var statusParts = new List<string>();
            if (skipped > 0) statusParts.Add($"Skipped: {skipped}");
            if (unsupported > 0) statusParts.Add($"Unsupported: {unsupported}");
            StatusTextBlock.Text = $"No files to convert. {string.Join(", ", statusParts)}";
            ConversionProgressBar.Visibility = Visibility.Collapsed;
            _cts.Dispose();
            _cts = null;
            UpdateButtonStates();
            return;
        }

        // Mark all files as converting
        foreach (var file in filesToConvert) file.Status = "Converting...";

        StatusTextBlock.Text = $"Converting {filesToConvert.Count} files using DDXConv batch mode...";

        try
        {
            Directory.CreateDirectory(outputDir);

            // Progress callback - update UI as files complete
            void OnFileCompleted(string inputPath, string status, string? error)
            {
                // Find the entry for this file
                if (!filePathToEntry.TryGetValue(inputPath, out var entry)) return;

                // Update on UI thread
                DispatcherQueue.TryEnqueue(() =>
                {
                    switch (status)
                    {
                        case "OK":
                            entry.Status = "Converted";
                            converted++;
                            break;
                        case "FAIL":
                            entry.Status = $"Failed: {error ?? "Unknown error"}";
                            failed++;
                            break;
                        case "UNSUPPORTED":
                            entry.Status = "Unsupported";
                            unsupported++;
                            break;
                    }

                    ConversionProgressBar.Value = skipped + unsupported + converted + failed;
                    StatusTextBlock.Text =
                        $"Converting... {converted + failed + unsupported}/{filesToConvert.Count} ({converted} converted, {failed} failed, {unsupported} unsupported)";
                });
            }

            // Run batch conversion
            _ = await converter.ConvertBatchAsync(
                inputDir,
                outputDir,
                OnFileCompleted,
                _cts.Token);

            // Mark any remaining files that didn't get a callback (shouldn't happen with --progress flag)
            foreach (var file in filesToConvert.Where(f => f.Status == "Converting...")) file.Status = "Unknown";

            var statusParts = new List<string>();
            if (converted > 0) statusParts.Add($"Converted: {converted}");
            if (skipped > 0) statusParts.Add($"Skipped: {skipped}");
            if (unsupported > 0) statusParts.Add($"Unsupported: {unsupported}");
            if (failed > 0) statusParts.Add($"Failed: {failed}");

            StatusTextBlock.Text = $"Conversion complete. {string.Join(", ", statusParts)}";
        }
        catch (OperationCanceledException)
        {
            // Mark remaining files as cancelled
            foreach (var file in filesToConvert.Where(f => f.Status is "Converting..." or "Queued"))
                file.Status = "Cancelled";

            StatusTextBlock.Text = "Conversion cancelled.";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            ConversionProgressBar.Visibility = Visibility.Collapsed;
            UpdateButtonStates();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusTextBlock.Text = "Cancelling...";
    }

    #region Sorting

    private void SortByFilePath_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(DdxFilesSorter.SortColumn.FilePath);
    }

    private void SortBySize_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(DdxFilesSorter.SortColumn.Size);
    }

    private void SortByFormat_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(DdxFilesSorter.SortColumn.Format);
    }

    private void SortByStatus_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(DdxFilesSorter.SortColumn.Status);
    }

    private void ApplySort(DdxFilesSorter.SortColumn column)
    {
        _sorter.CycleSortState(column);
        UpdateSortIcons();
        RefreshSortedList();
    }

    private void UpdateSortIcons()
    {
        FilePathSortIcon.Visibility = SizeSortIcon.Visibility =
            FormatSortIcon.Visibility = StatusSortIcon.Visibility = Visibility.Collapsed;

        var icon = _sorter.CurrentColumn switch
        {
            DdxFilesSorter.SortColumn.FilePath => FilePathSortIcon,
            DdxFilesSorter.SortColumn.Size => SizeSortIcon,
            DdxFilesSorter.SortColumn.Format => FormatSortIcon,
            DdxFilesSorter.SortColumn.Status => StatusSortIcon,
            _ => null
        };

        if (icon != null)
        {
            icon.Visibility = Visibility.Visible;
            icon.Glyph = _sorter.IsAscending ? "\uE70E" : "\uE70D";
        }
    }

    private void RefreshSortedList()
    {
        var selectedItem = DdxFilesListView.SelectedItem as DdxFileEntry;
        var sorted = _sorter.Sort(_allDdxFiles);

        _ddxFiles = sorted.ToList();
        DdxFilesListView.ItemsSource = _ddxFiles;

        if (selectedItem != null && _ddxFiles.Contains(selectedItem))
        {
            DdxFilesListView.SelectedItem = selectedItem;
            DdxFilesListView.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading);
        }
    }

    #endregion
}
