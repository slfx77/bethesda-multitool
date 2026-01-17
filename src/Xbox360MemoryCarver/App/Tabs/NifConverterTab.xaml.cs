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
    // Brushes are created lazily on first access (which happens on UI thread via binding)
    private static SolidColorBrush? _grayBrush;
    private static SolidColorBrush? _greenBrush;
    private static SolidColorBrush? _orangeBrush;
    private static SolidColorBrush? _redBrush;
    private static SolidColorBrush? _yellowBrush;

    private string _formatDescription = "";
    private bool _isSelected = true;
    private string _status = "Pending";

    private static SolidColorBrush GrayBrush => _grayBrush ??= new SolidColorBrush(Colors.Gray);
    private static SolidColorBrush GreenBrush => _greenBrush ??= new SolidColorBrush(Colors.Green);
    private static SolidColorBrush OrangeBrush => _orangeBrush ??= new SolidColorBrush(Colors.Orange);
    private static SolidColorBrush RedBrush => _redBrush ??= new SolidColorBrush(Colors.Red);
    private static SolidColorBrush YellowBrush => _yellowBrush ??= new SolidColorBrush(Colors.Yellow);

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
        "Xbox 360 (BE)" => OrangeBrush,
        "PC (LE)" => GreenBrush,
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
///     Tab for batch converting Xbox 360 NIF files to PC format.
/// </summary>
public sealed partial class NifConverterTab : UserControl, IDisposable
{
    private readonly List<NifFileEntry> _allNifFiles = [];
    private readonly NifFilesSorter _sorter = new();
    private CancellationTokenSource? _cts;
    private bool _dependencyCheckDone;
    private List<NifFileEntry> _nifFiles = []; // Using List instead of ObservableCollection for batch performance
    private CancellationTokenSource? _scanCts;

    public NifConverterTab()
    {
        InitializeComponent();
        SetupTextBoxContextMenus();
        Loaded += NifConverterTab_Loaded;
    }

    /// <summary>
    ///     Helper to route status text to the global status bar.
    /// </summary>
    private StatusTextHelper StatusTextBlock => new();

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async void NifConverterTab_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= NifConverterTab_Loaded;

        // Check dependencies on first load
        if (!_dependencyCheckDone)
        {
            _dependencyCheckDone = true;
            await CheckDependenciesAsync();
        }
    }

    private async Task CheckDependenciesAsync()
    {
        // Small delay to ensure the UI is fully loaded
        await Task.Delay(100);

        var result = DependencyChecker.CheckNifConverterDependencies();
        if (!result.AllAvailable) await DependencyDialogHelper.ShowIfMissingAsync(result, XamlRoot);
        // NIF Converter has no external dependencies, so this will always pass
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
            _nifFiles = [];
            _allNifFiles.Clear();
            NifFilesListView.ItemsSource = null;
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
        // Cancel any in-progress scan
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var cancellationToken = _scanCts.Token;

        _nifFiles = [];
        _allNifFiles.Clear();
        _allNifFiles.TrimExcess();
        NifFilesListView.ItemsSource = null;
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

        ConversionProgressBar.Visibility = Visibility.Visible;
        ConversionProgressBar.IsIndeterminate = true;
        ConversionProgressBar.Value = 0;

        try
        {
            // Scan and create entries on background thread
            var entries = await ScanAndCreateNifEntriesAsync(directory, cancellationToken);

            // Check if cancelled before updating UI
            if (cancellationToken.IsCancellationRequested) return;

            // Only the ItemsSource assignment happens on UI thread
            _allNifFiles.Clear();
            _allNifFiles.Capacity = entries.Length;
            _allNifFiles.AddRange(entries);
            _nifFiles = new List<NifFileEntry>(_allNifFiles);
            NifFilesListView.ItemsSource = _nifFiles;

            UpdateFileCount();
            UpdateButtonStates();
            StatusTextBlock.Text =
                $"Found {_nifFiles.Count} NIF files. {_nifFiles.Count(f => f.FormatDescription == "Xbox 360 (BE)")} require conversion.";
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
            if (nifFiles.Count == 0 || cancellationToken.IsCancellationRequested) return Array.Empty<NifFileEntry>();

            InitializeScanProgress(nifFiles.Count);

            var entries = new NifFileEntry[nifFiles.Count];
            var processedCount = 0;
            var dispatcher = DispatcherQueue;

            // Use Parallel.ForEach with synchronous I/O - much faster than async for small reads
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

                    // Update progress every 100 files
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

            // Use stackalloc for small header buffer - no heap allocation
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

    private void InitializeScanProgress(int fileCount)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ConversionProgressBar.IsIndeterminate = false;
            ConversionProgressBar.Maximum = fileCount;
            ConversionProgressBar.Value = 0;
            StatusTextBlock.Text = $"Scanning {fileCount} NIF files...";
        });
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
                if (_cts.Token.IsCancellationRequested) break;

                var file = selectedFiles[i];
                StatusTextBlock.Text = $"Converting {i + 1}/{selectedFiles.Count}: {file.RelativePath}";
                file.Status = "Converting...";

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

        // Replace list entirely - much faster than clear + add loop
        _nifFiles = sorted.ToList();
        NifFilesListView.ItemsSource = _nifFiles;

        if (selectedItem != null && _nifFiles.Contains(selectedItem))
        {
            NifFilesListView.SelectedItem = selectedItem;
            NifFilesListView.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading);
        }
    }

    #endregion
}
