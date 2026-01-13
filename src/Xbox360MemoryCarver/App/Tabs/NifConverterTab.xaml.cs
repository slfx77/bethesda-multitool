using System.Buffers;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Xbox360MemoryCarver.Core.Formats.Nif;

namespace Xbox360MemoryCarver;

/// <summary>
///     Data model for NIF file entries in the list.
/// </summary>
public sealed class NifFileEntry : INotifyPropertyChanged
{
    // Cached brushes to avoid creating new instances for each entry
    private static readonly SolidColorBrush GrayBrush = new(Colors.Gray);
    private static readonly SolidColorBrush GreenBrush = new(Colors.Green);
    private static readonly SolidColorBrush OrangeBrush = new(Colors.Orange);
    private static readonly SolidColorBrush RedBrush = new(Colors.Red);
    private static readonly SolidColorBrush YellowBrush = new(Colors.Yellow);

    private SolidColorBrush _formatColor = GrayBrush;
    private string _formatDescription = "";
    private bool _isSelected = true;
    private string _status = "Pending";
    private SolidColorBrush _statusColor = GrayBrush;

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
            }
        }
    }

    public SolidColorBrush StatusColor
    {
        get => _statusColor;
        set
        {
            if (_statusColor != value)
            {
                _statusColor = value;
                OnPropertyChanged(nameof(StatusColor));
            }
        }
    }

    public string FormatDescription
    {
        get => _formatDescription;
        set
        {
            if (_formatDescription != value)
            {
                _formatDescription = value;
                OnPropertyChanged(nameof(FormatDescription));
            }
        }
    }

    public SolidColorBrush FormatColor
    {
        get => _formatColor;
        set
        {
            if (_formatColor != value)
            {
                _formatColor = value;
                OnPropertyChanged(nameof(FormatColor));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    ///     Get cached brush for format description.
    /// </summary>
    public static SolidColorBrush GetFormatBrush(string formatDesc)
    {
        return formatDesc switch
        {
            "Xbox 360 (BE)" => OrangeBrush,
            "PC (LE)" => GreenBrush,
            "Invalid" or "Error" => RedBrush,
            _ => GrayBrush
        };
    }

    /// <summary>
    ///     Get cached brush for status.
    /// </summary>
    public static SolidColorBrush GetStatusBrush(string status)
    {
        return status switch
        {
            "Converted" => GreenBrush,
            "Converting..." => YellowBrush,
            "Cancelled" => OrangeBrush,
            "Skipped (exists)" or "Pending" => GrayBrush,
            _ when status.StartsWith("Error", StringComparison.Ordinal) => RedBrush,
            _ when status.StartsWith("Failed", StringComparison.Ordinal) => RedBrush,
            _ => GrayBrush
        };
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
///     Tab for batch converting Xbox 360 NIF files to PC format.
/// </summary>
public sealed partial class NifConverterTab : UserControl, IDisposable
{
    private const int MaxConcurrentFileReads = 8; // Limit concurrent file I/O

    private readonly List<NifFileEntry> _allNifFiles = [];
    private readonly ObservableCollection<NifFileEntry> _nifFiles = [];
    private readonly NifFilesSorter _sorter = new();
    private CancellationTokenSource? _cts;

    public NifConverterTab()
    {
        InitializeComponent();
        NifFilesListView.ItemsSource = _nifFiles;
        SetupTextBoxContextMenus();
    }

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private void SetupTextBoxContextMenus()
    {
        TextBoxContextMenuHelper.AttachContextMenu(InputDirectoryTextBox);
        TextBoxContextMenuHelper.AttachContextMenu(OutputDirectoryTextBox);
    }

    private void UpdateButtonStates()
    {
        var hasOutput = !string.IsNullOrEmpty(OutputDirectoryTextBox.Text);
        var hasSelected = _nifFiles.Any(f => f.IsSelected);
        ConvertButton.IsEnabled = hasOutput && hasSelected && (_cts == null || _cts.IsCancellationRequested);
        CancelButton.IsEnabled = _cts != null && !_cts.IsCancellationRequested;
    }

    private void UpdateFileCount()
    {
        var total = _nifFiles.Count;
        var selected = _nifFiles.Count(f => f.IsSelected);
        FileCountTextBlock.Text = $"({selected}/{total} selected)";
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
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        // Setting InputDirectoryTextBox.Text triggers TextChanged event which calls ScanForNifFilesAsync
        InputDirectoryTextBox.Text = folder.Path;
        OutputDirectoryTextBox.Text = Path.Combine(folder.Path, "converted_pc");
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
            _nifFiles.Clear();
            _allNifFiles.Clear();
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
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            OutputDirectoryTextBox.Text = folder.Path;
            UpdateButtonStates();
        }
    }

    private async Task ScanForNifFilesAsync(string directory)
    {
        _nifFiles.Clear();
        _allNifFiles.Clear();
        _sorter.Reset();
        UpdateSortIcons();
        StatusTextBlock.Text = "Scanning for NIF files...";

        if (!Directory.Exists(directory))
        {
            StatusTextBlock.Text = "Directory does not exist.";
            UpdateFileCount();
            UpdateButtonStates();
            return;
        }

        ScanProgressBar.Visibility = Visibility.Visible;
        ScanProgressBar.IsIndeterminate = true;
        ScanProgressBar.Value = 0;

        try
        {
            var entries = await ScanDirectoryForNifFilesAsync(directory);
            PopulateNifFilesList(entries);
            UpdateFileCount();
            UpdateButtonStates();
            StatusTextBlock.Text =
                $"Found {_nifFiles.Count} NIF files. {_nifFiles.Count(f => f.FormatDescription == "Xbox 360 (BE)")} require conversion.";
        }
        finally
        {
            ScanProgressBar.Visibility = Visibility.Collapsed;
            ScanProgressBar.IsIndeterminate = true;
        }
    }

    private async Task<(string fullPath, string relativePath, long fileSize, string formatDesc, bool isXbox360)[]>
        ScanDirectoryForNifFilesAsync(string directory)
    {
        return await Task.Run(async () =>
        {
            var nifFiles = Directory.EnumerateFiles(directory, "*.nif", SearchOption.AllDirectories).ToList();
            if (nifFiles.Count == 0)
            {
                return Array.Empty<(string, string, long, string, bool)>();
            }

            InitializeScanProgress(nifFiles.Count);

            var result = new (string fullPath, string relativePath, long fileSize, string formatDesc, bool isXbox360)
                [nifFiles.Count];
            var progressTracker = new ScanProgressTracker(nifFiles.Count, DispatcherQueue, ScanProgressBar);

            using var semaphore = new SemaphoreSlim(MaxConcurrentFileReads);
            var tasks = nifFiles.Select((filePath, index) =>
                ProcessNifFileAsync(directory, filePath, result, index, semaphore, progressTracker)).ToArray();

            await Task.WhenAll(tasks);
            return result;
        });
    }

    private sealed class ScanProgressTracker(int totalCount, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Microsoft.UI.Xaml.Controls.ProgressBar progressBar)
    {
        private int _processedCount;
        private int _lastReportedProgress;

        public void IncrementAndReport()
        {
            var currentCount = Interlocked.Increment(ref _processedCount);
            if (currentCount % 100 != 0 && currentCount != totalCount) return;

            var previousMax = _lastReportedProgress;
            while (currentCount > previousMax)
            {
                if (Interlocked.CompareExchange(ref _lastReportedProgress, currentCount, previousMax) == previousMax)
                {
                    var progressValue = currentCount;
                    dispatcher.TryEnqueue(() =>
                    {
                        if (progressValue > progressBar.Value)
                            progressBar.Value = progressValue;
                    });
                    break;
                }

                previousMax = _lastReportedProgress;
            }
        }
    }

    private void InitializeScanProgress(int fileCount)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ScanProgressBar.IsIndeterminate = false;
            ScanProgressBar.Maximum = fileCount;
            ScanProgressBar.Value = 0;
            StatusTextBlock.Text = $"Scanning {fileCount} NIF files...";
        });
    }

    private async Task ProcessNifFileAsync(
        string directory,
        string filePath,
        (string fullPath, string relativePath, long fileSize, string formatDesc, bool isXbox360)[] result,
        int index,
        SemaphoreSlim semaphore,
        ScanProgressTracker progressTracker)
    {
        await semaphore.WaitAsync();
        try
        {
            var relativePath = Path.GetRelativePath(directory, filePath);
            var (fileSize, formatDesc) = await ReadNifFileHeaderAsync(filePath);
            result[index] = (filePath, relativePath, fileSize, formatDesc, formatDesc == "Xbox 360 (BE)");

            progressTracker.IncrementAndReport();
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static async Task<(long fileSize, string formatDesc)> ReadNifFileHeaderAsync(string filePath)
    {
        var headerBytes = ArrayPool<byte>.Shared.Rent(50);
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;

            await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytesRead = await fs.ReadAsync(headerBytes.AsMemory(0, 50));

            var formatDesc = DetermineNifFormat(headerBytes, bytesRead);
            return (fileSize, formatDesc);
        }
        catch
        {
            return (0, "Error");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerBytes);
        }
    }

    private static string DetermineNifFormat(byte[] headerBytes, int bytesRead)
    {
        if (bytesRead < 50) return "Invalid";

        var newlinePos = Array.IndexOf(headerBytes, (byte)0x0A, 0, 50);
        if (newlinePos <= 0 || newlinePos + 5 >= 50) return "Invalid";

        return headerBytes[newlinePos + 5] switch
        {
            0 => "Xbox 360 (BE)",
            1 => "PC (LE)",
            _ => "Unknown"
        };
    }

    private void PopulateNifFilesList(
        (string fullPath, string relativePath, long fileSize, string formatDesc, bool isXbox360)[] entries)
    {
        NifFilesListView.ItemsSource = null;

        foreach (var (fullPath, relativePath, fileSize, formatDesc, isXbox360) in entries)
        {
            var entry = new NifFileEntry
            {
                FullPath = fullPath,
                RelativePath = relativePath,
                FileSize = fileSize,
                FormatDescription = formatDesc,
                FormatColor = NifFileEntry.GetFormatBrush(formatDesc),
                IsSelected = isXbox360
            };

            _allNifFiles.Add(entry);
            _nifFiles.Add(entry);
        }

        NifFilesListView.ItemsSource = _nifFiles;
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in _nifFiles) file.IsSelected = true;

        UpdateFileCount();
        UpdateButtonStates();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var file in _nifFiles) file.IsSelected = false;

        UpdateFileCount();
        UpdateButtonStates();
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = _nifFiles.Where(f => f.IsSelected).ToList();
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

        var converter = new NifConverter(verbose);

        _cts = new CancellationTokenSource();
        UpdateButtonStates();

        ConversionProgressBar.Visibility = Visibility.Visible;
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
                if (_cts.Token.IsCancellationRequested) break;

                var file = selectedFiles[i];
                StatusTextBlock.Text = $"Converting {i + 1}/{selectedFiles.Count}: {file.RelativePath}";
                file.Status = "Converting...";
                file.StatusColor = NifFileEntry.GetStatusBrush("Converting...");

                try
                {
                    // Determine output path
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

                    // Check if output exists
                    if (File.Exists(outputPath) && !overwrite)
                    {
                        file.Status = "Skipped (exists)";
                        file.StatusColor = NifFileEntry.GetStatusBrush("Skipped (exists)");
                        skipped++;
                        continue;
                    }

                    // Read and convert
                    var inputData = await File.ReadAllBytesAsync(file.FullPath, _cts.Token);
                    var result = await Task.Run(() => converter.Convert(inputData), _cts.Token);

                    if (result.Success && result.OutputData != null)
                    {
                        await File.WriteAllBytesAsync(outputPath, result.OutputData, _cts.Token);

                        file.Status = "Converted";
                        file.StatusColor = NifFileEntry.GetStatusBrush("Converted");
                        converted++;
                    }
                    else
                    {
                        file.Status = result.ErrorMessage ?? "Failed";
                        file.StatusColor = NifFileEntry.GetStatusBrush("Failed");
                        failed++;
                    }
                }
                catch (OperationCanceledException)
                {
                    file.Status = "Cancelled";
                    file.StatusColor = NifFileEntry.GetStatusBrush("Cancelled");
                    throw;
                }
                catch (Exception ex)
                {
                    file.Status = $"Error: {ex.Message}";
                    file.StatusColor = NifFileEntry.GetStatusBrush("Error");
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
            _cts.Dispose();
            _cts = null;
            ConversionProgressBar.Visibility = Visibility.Collapsed;
            UpdateButtonStates();
        }
    }

    private void CancelButton_Click(object _sender, RoutedEventArgs _e)
    {
        _cts?.Cancel();
        StatusTextBlock.Text = "Cancelling...";
    }

    #region Sorting

    private void SortByFilePath_Click(object _sender, RoutedEventArgs _e)
    {
        ApplySort(NifFilesSorter.SortColumn.FilePath);
    }

    private void SortBySize_Click(object _sender, RoutedEventArgs _e)
    {
        ApplySort(NifFilesSorter.SortColumn.Size);
    }

    private void SortByFormat_Click(object _sender, RoutedEventArgs _e)
    {
        ApplySort(NifFilesSorter.SortColumn.Format);
    }

    private void SortByStatus_Click(object _sender, RoutedEventArgs _e)
    {
        ApplySort(NifFilesSorter.SortColumn.Status);
    }

    private void ApplySort(NifFilesSorter.SortColumn column)
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
            NifFilesSorter.SortColumn.FilePath => FilePathSortIcon,
            NifFilesSorter.SortColumn.Size => SizeSortIcon,
            NifFilesSorter.SortColumn.Format => FormatSortIcon,
            NifFilesSorter.SortColumn.Status => StatusSortIcon,
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
        var selectedItem = NifFilesListView.SelectedItem as NifFileEntry;
        var sorted = _sorter.Sort(_allNifFiles);

        // Batch update - suspend UI binding during sort refresh
        NifFilesListView.ItemsSource = null;
        _nifFiles.Clear();
        foreach (var file in sorted) _nifFiles.Add(file);
        NifFilesListView.ItemsSource = _nifFiles;

        if (selectedItem != null && _nifFiles.Contains(selectedItem))
        {
            NifFilesListView.SelectedItem = selectedItem;
            NifFilesListView.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading);
        }
    }

    #endregion
}
