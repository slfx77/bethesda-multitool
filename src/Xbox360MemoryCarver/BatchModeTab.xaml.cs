using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage.Pickers;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.App;

/// <summary>
///     Batch processing tab for multiple dump files.
/// </summary>
public sealed partial class BatchModeTab : UserControl, IDisposable
{
    private readonly ObservableCollection<DumpFileEntry> _dumpFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private CancellationTokenSource? _cancellationTokenSource;
    private BatchSortColumn _currentSortColumn = BatchSortColumn.None;
    private bool _disposed;
    private bool _sortAscending = true;

    public BatchModeTab()
    {
        InitializeComponent();
        DumpFilesListView.ItemsSource = _dumpFiles;
        InitializeFileTypeCheckboxes();
        ParallelCountBox.Maximum = Environment.ProcessorCount;
    }

    private void InitializeFileTypeCheckboxes()
    {
        FileTypeCheckboxPanel.Children.Clear();
        _fileTypeCheckboxes.Clear();
        foreach (var fileType in FileTypeMapping.DisplayNames)
        {
            var cb = new CheckBox { Content = fileType, IsChecked = true, Margin = new Thickness(0, 0, 8, 0) };
            _fileTypeCheckboxes[fileType] = cb;
            FileTypeCheckboxPanel.Children.Add(cb);
        }
    }

    /// <summary>
    ///     Dispose managed resources.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            _disposed = true;
        }
    }

    private void UpdateButtonStates()
    {
        var hasOutputDir = !string.IsNullOrEmpty(OutputDirectoryTextBox.Text);
        var hasSelectedFiles = _dumpFiles.Any(f => f.IsSelected);

        ExtractButton.IsEnabled = hasOutputDir && hasSelectedFiles;
    }

#pragma warning disable CA1822 // Event handler cannot be static
    private void ParallelCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        // Reset to default value (2) if the user clears the box or enters an invalid value
        if (double.IsNaN(args.NewValue))
        {
            sender.Value = 2;
        }
    }
#pragma warning restore CA1822

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

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private async void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            InputDirectoryTextBox.Text = folder.Path;

            // Auto-set output if not set
            if (string.IsNullOrEmpty(OutputDirectoryTextBox.Text))
                OutputDirectoryTextBox.Text = Path.Combine(folder.Path, "extracted");

            // Auto-scan for dump files
            ScanForDumpFiles();
        }
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(App.Current.MainWindow);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            OutputDirectoryTextBox.Text = folder.Path;
            UpdateButtonStates();
        }
    }

    private void ScanForDumpFiles()
    {
        _dumpFiles.Clear();

        if (!Directory.Exists(InputDirectoryTextBox.Text)) return;

        var dmpFiles = Directory.GetFiles(InputDirectoryTextBox.Text, "*.dmp", SearchOption.AllDirectories);

        foreach (var file in dmpFiles)
        {
            var fileInfo = new FileInfo(file);
            var entry = new DumpFileEntry
            {
                FilePath = file,
                FileName = Path.GetFileName(file),
                Size = fileInfo.Length,
                IsSelected = true,
                Status = "Pending"
            };
            entry.PropertyChanged += OnEntryPropertyChanged;
            _dumpFiles.Add(entry);
        }

        StatusTextBlock.Text = $"Found {_dumpFiles.Count} dump file(s)";
        UpdateButtonStates();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateButtonStates();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _dumpFiles) entry.IsSelected = true;
        UpdateButtonStates();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _dumpFiles) entry.IsSelected = false;
        UpdateButtonStates();
    }

    #region Sorting

    private void SortByFilename_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(BatchSortColumn.Filename);
    }

    private void SortBySize_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(BatchSortColumn.Size);
    }

    private void SortByStatus_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(BatchSortColumn.Status);
    }

    private void ApplySort(BatchSortColumn column)
    {
        if (_currentSortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _currentSortColumn = column;
            _sortAscending = true;
        }

        UpdateSortIcons();

        var sorted = _currentSortColumn switch
        {
            BatchSortColumn.Filename => _sortAscending
                ? _dumpFiles.OrderBy(f => f.FileName).ToList()
                : _dumpFiles.OrderByDescending(f => f.FileName).ToList(),
            BatchSortColumn.Size => _sortAscending
                ? _dumpFiles.OrderBy(f => f.Size).ToList()
                : _dumpFiles.OrderByDescending(f => f.Size).ToList(),
            BatchSortColumn.Status => _sortAscending
                ? _dumpFiles.OrderBy(f => f.Status).ToList()
                : _dumpFiles.OrderByDescending(f => f.Status).ToList(),
            _ => _dumpFiles.ToList()
        };

        _dumpFiles.Clear();
        foreach (var item in sorted)
        {
            _dumpFiles.Add(item);
        }
    }

    private void UpdateSortIcons()
    {
        var glyph = _sortAscending ? "\uE70D" : "\uE70E"; // Up or Down arrow

        FilenameSortIcon.Visibility =
            _currentSortColumn == BatchSortColumn.Filename ? Visibility.Visible : Visibility.Collapsed;
        FilenameSortIcon.Glyph = glyph;

        SizeSortIcon.Visibility =
            _currentSortColumn == BatchSortColumn.Size ? Visibility.Visible : Visibility.Collapsed;
        SizeSortIcon.Glyph = glyph;

        StatusSortIcon.Visibility =
            _currentSortColumn == BatchSortColumn.Status ? Visibility.Visible : Visibility.Collapsed;
        StatusSortIcon.Glyph = glyph;
    }

    #endregion

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = _dumpFiles.Where(f => f.IsSelected).ToList();
        if (selectedFiles.Count == 0) return;

        SemaphoreSlim? semaphore = null;

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            ExtractButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            BatchProgressBar.Visibility = Visibility.Visible;
            BatchProgressBar.Value = 0;
            ProgressTextBlock.Visibility = Visibility.Visible;
            ProgressTextBlock.Text = "Starting...";

            // Get selected file types
            var selectedTypes = FileTypeMapping
                .GetSignatureIds(_fileTypeCheckboxes
                    .Where(kvp => kvp.Value.IsChecked == true)
                    .Select(kvp => kvp.Key))
                .ToList();

            var options = new ExtractionOptions
            {
                OutputPath = OutputDirectoryTextBox.Text,
                ConvertDdx = BatchConvertDdxCheckBox.IsChecked == true,
                SaveAtlas = BatchSaveAtlasCheckBox.IsChecked == true,
                Verbose = BatchVerboseCheckBox.IsChecked == true,
                FileTypes = selectedTypes.Count > 0 ? selectedTypes : null
            };

            var parallelCount = (int)ParallelCountBox.Value;
            var skipExisting = SkipExistingCheckBox.IsChecked == true;
            var processed = 0;
            var total = selectedFiles.Count;

            semaphore = new SemaphoreSlim(parallelCount);
            var tasks = new List<Task>();

            foreach (var entry in selectedFiles)
            {
                if (token.IsCancellationRequested) break;

                await semaphore.WaitAsync(token);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        // Check if already extracted
                        var outputSubdir = Path.Combine(options.OutputPath,
                            Path.GetFileNameWithoutExtension(entry.FileName));

                        if (skipExisting && Directory.Exists(outputSubdir))
                        {
                            DispatcherQueue.TryEnqueue(() => { entry.Status = "Skipped"; });
                            return;
                        }

                        DispatcherQueue.TryEnqueue(() => { entry.Status = "Processing..."; });

                        var entryOptions = options with
                        {
                            OutputPath = outputSubdir
                        };

                        await MemoryDumpExtractor.Extract(entry.FilePath, entryOptions, null);

                        DispatcherQueue.TryEnqueue(() => { entry.Status = "Complete"; });
                    }
                    catch (Exception ex)
                    {
                        DispatcherQueue.TryEnqueue(() => { entry.Status = $"Error: {ex.Message}"; });
                    }
                    finally
                    {
                        try
                        {
                            semaphore.Release();
                        }
                        catch (ObjectDisposedException)
                        {
                            // Semaphore was disposed during cancellation, ignore
                        }

                        var current = Interlocked.Increment(ref processed);
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            BatchProgressBar.Value = current * 100.0 / total;
                            ProgressTextBlock.Text = $"Processing {current}/{total}...";
                        });
                    }
                }, token);

                tasks.Add(task);
            }

            await Task.WhenAll(tasks);

            StatusTextBlock.Text = $"Completed processing {processed} file(s)";
            ProgressTextBlock.Text = "";
        }
        catch (OperationCanceledException)
        {
            StatusTextBlock.Text = "Processing cancelled";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Batch Processing Failed", ex.Message);
        }
        finally
        {
            ExtractButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            BatchProgressBar.Visibility = Visibility.Collapsed;
            ProgressTextBlock.Visibility = Visibility.Collapsed;
            ProgressTextBlock.Text = "";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            semaphore?.Dispose();
            UpdateButtonStates();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        StatusTextBlock.Text = "Cancelling...";
    }
#pragma warning restore RCS1163
}

/// <summary>
///     Represents a dump file in the batch list.
/// </summary>
public partial class DumpFileEntry : INotifyPropertyChanged
{
    private bool _isSelected;

    private string _status = "Pending";
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusColor)));
            }
        }
    }

    public string SizeFormatted => Size switch
    {
        >= 1024 * 1024 * 1024 => $"{Size / (1024.0 * 1024.0 * 1024.0):F2} GB",
        >= 1024 * 1024 => $"{Size / (1024.0 * 1024.0):F2} MB",
        >= 1024 => $"{Size / 1024.0:F2} KB",
        _ => $"{Size} B"
    };

    public Brush StatusColor => Status switch
    {
        "Complete" => new SolidColorBrush(Colors.Green),
        "Skipped" => new SolidColorBrush(Colors.Gray),
        "Processing..." => new SolidColorBrush(Colors.Blue),
        _ when Status.StartsWith("Error", StringComparison.Ordinal) => new SolidColorBrush(Colors.Red),
        _ => new SolidColorBrush(Colors.Gray)
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
///     Sort columns for the batch dump files list.
/// </summary>
public enum BatchSortColumn
{
    None,
    Filename,
    Size,
    Status
}
