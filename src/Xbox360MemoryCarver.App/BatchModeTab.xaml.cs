using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.Storage.Pickers;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.App;

/// <summary>
/// Batch processing tab for multiple dump files.
/// </summary>
public sealed partial class BatchModeTab : UserControl, IDisposable
{
    private readonly ObservableCollection<DumpFileEntry> _dumpFiles = [];
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _disposed;

    public BatchModeTab()
    {
        this.InitializeComponent();
        DumpFilesListView.ItemsSource = _dumpFiles;
    }

    /// <summary>
    /// Dispose managed resources.
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
        bool hasInputDir = !string.IsNullOrEmpty(InputDirectoryTextBox.Text)
                          && Directory.Exists(InputDirectoryTextBox.Text);
        bool hasOutputDir = !string.IsNullOrEmpty(OutputDirectoryTextBox.Text);
        bool hasSelectedFiles = _dumpFiles.Any(f => f.IsSelected);

        ScanButton.IsEnabled = hasInputDir;
        ProcessButton.IsEnabled = hasInputDir && hasOutputDir && hasSelectedFiles;
    }

#pragma warning disable RCS1163 // Unused parameter - required for event handler signature
    private async void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        StorageFolder folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            InputDirectoryTextBox.Text = folder.Path;

            // Auto-set output if not set
            if (string.IsNullOrEmpty(OutputDirectoryTextBox.Text))
            {
                OutputDirectoryTextBox.Text = Path.Combine(folder.Path, "extracted");
            }

            UpdateButtonStates();
        }
    }

    private async void BrowseOutputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");

        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        StorageFolder folder = await picker.PickSingleFolderAsync();
        if (folder != null)
        {
            OutputDirectoryTextBox.Text = folder.Path;
            UpdateButtonStates();
        }
    }

    private void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        _dumpFiles.Clear();

        if (!Directory.Exists(InputDirectoryTextBox.Text))
        {
            return;
        }

        string[] dmpFiles = Directory.GetFiles(InputDirectoryTextBox.Text, "*.dmp", SearchOption.AllDirectories);

        foreach (string file in dmpFiles)
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

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateButtonStates();

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (DumpFileEntry entry in _dumpFiles)
        {
            entry.IsSelected = true;
        }
        UpdateButtonStates();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (DumpFileEntry entry in _dumpFiles)
        {
            entry.IsSelected = false;
        }
        UpdateButtonStates();
    }

    private async void ProcessButton_Click(object sender, RoutedEventArgs e)
    {
        List<DumpFileEntry> selectedFiles = _dumpFiles.Where(f => f.IsSelected).ToList();
        if (selectedFiles.Count == 0)
        {
            return;
        }

        SemaphoreSlim? semaphore = null;

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;

            ProcessButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            ScanButton.IsEnabled = false;
            BatchProgressBar.Visibility = Visibility.Visible;
            BatchProgressBar.Value = 0;

            var options = new ExtractionOptions
            {
                OutputPath = OutputDirectoryTextBox.Text,
                ConvertDdx = BatchConvertDdxCheckBox.IsChecked == true,
                SaveAtlas = BatchSaveAtlasCheckBox.IsChecked == true,
                Verbose = BatchVerboseCheckBox.IsChecked == true
            };

            int parallelCount = (int)ParallelCountBox.Value;
            bool skipExisting = SkipExistingCheckBox.IsChecked == true;
            int processed = 0;
            int total = selectedFiles.Count;

            semaphore = new SemaphoreSlim(parallelCount);
            var tasks = new List<Task>();

            foreach (DumpFileEntry? entry in selectedFiles)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                await semaphore.WaitAsync(token);

                Task task = Task.Run(async () =>
                {
                    try
                    {
                        // Check if already extracted
                        string outputSubdir = Path.Combine(options.OutputPath,
                            Path.GetFileNameWithoutExtension(entry.FileName));

                        if (skipExisting && Directory.Exists(outputSubdir))
                        {
                            DispatcherQueue.TryEnqueue(() =>
                            {
                                entry.Status = "Skipped";
                            });
                            return;
                        }

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            entry.Status = "Processing...";
                        });

                        ExtractionOptions entryOptions = options with
                        {
                            OutputPath = outputSubdir
                        };

                        await MemoryDumpExtractor.Extract(entry.FilePath, entryOptions, null);

                        DispatcherQueue.TryEnqueue(() =>
                        {
                            entry.Status = "Complete";
                        });
                    }
                    catch (Exception ex)
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            entry.Status = $"Error: {ex.Message}";
                        });
                    }
                    finally
                    {
                        semaphore.Release();

                        int current = Interlocked.Increment(ref processed);
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            BatchProgressBar.Value = (current * 100.0) / total;
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
            ProcessButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ScanButton.IsEnabled = true;
            BatchProgressBar.Visibility = Visibility.Collapsed;
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

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.XamlRoot
        };
        await dialog.ShowAsync();
    }
}

/// <summary>
/// Represents a dump file in the batch list.
/// </summary>
public partial class DumpFileEntry : INotifyPropertyChanged
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }

    private bool _isSelected;
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

    private string _status = "Pending";
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
        "Complete" => new SolidColorBrush(Microsoft.UI.Colors.Green),
        "Skipped" => new SolidColorBrush(Microsoft.UI.Colors.Gray),
        "Processing..." => new SolidColorBrush(Microsoft.UI.Colors.Blue),
        _ when Status.StartsWith("Error", StringComparison.Ordinal) => new SolidColorBrush(Microsoft.UI.Colors.Red),
        _ => new SolidColorBrush(Microsoft.UI.Colors.Gray)
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
