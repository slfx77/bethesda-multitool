using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver.App;

/// <summary>
/// Batch processing tab for multiple dump files.
/// </summary>
public sealed partial class BatchModeTab : UserControl
{
    private readonly ObservableCollection<DumpFileEntry> _dumpFiles = new();
    private CancellationTokenSource? _cancellationTokenSource;

    public BatchModeTab()
    {
        this.InitializeComponent();
        DumpFilesListView.ItemsSource = _dumpFiles;
    }

    private void UpdateButtonStates()
    {
        var hasInputDir = !string.IsNullOrEmpty(InputDirectoryTextBox.Text)
                          && Directory.Exists(InputDirectoryTextBox.Text);
        var hasOutputDir = !string.IsNullOrEmpty(OutputDirectoryTextBox.Text);
        var hasSelectedFiles = _dumpFiles.Any(f => f.IsSelected);

        ScanButton.IsEnabled = hasInputDir;
        ProcessButton.IsEnabled = hasInputDir && hasOutputDir && hasSelectedFiles;
    }

    private async void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
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
        var picker = new FolderPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Current.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
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
            return;

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
            entry.PropertyChanged += (s, e) => UpdateButtonStates();
            _dumpFiles.Add(entry);
        }

        StatusTextBlock.Text = $"Found {_dumpFiles.Count} dump file(s)";
        UpdateButtonStates();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _dumpFiles)
        {
            entry.IsSelected = true;
        }
        UpdateButtonStates();
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _dumpFiles)
        {
            entry.IsSelected = false;
        }
        UpdateButtonStates();
    }

    private async void ProcessButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = _dumpFiles.Where(f => f.IsSelected).ToList();
        if (!selectedFiles.Any())
            return;

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

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

            var parallelCount = (int)ParallelCountBox.Value;
            var skipExisting = SkipExistingCheckBox.IsChecked == true;
            var processed = 0;
            var total = selectedFiles.Count;

            var semaphore = new SemaphoreSlim(parallelCount);
            var tasks = new List<Task>();

            foreach (var entry in selectedFiles)
            {
                if (token.IsCancellationRequested)
                    break;

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

                        var analyzer = new MemoryDumpAnalyzer();
                        var result = analyzer.Analyze(entry.FilePath, null);

                        var entryOptions = options with
                        {
                            OutputPath = outputSubdir
                        };

                        var extractor = new MemoryDumpExtractor();
                        await extractor.Extract(entry.FilePath, result, entryOptions, null);

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

                        var current = Interlocked.Increment(ref processed);
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
            await ShowErrorDialog("Batch Processing Failed", ex.Message);
        }
        finally
        {
            ProcessButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ScanButton.IsEnabled = true;
            BatchProgressBar.Visibility = Visibility.Collapsed;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateButtonStates();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        StatusTextBlock.Text = "Cancelling...";
    }

    private async Task ShowErrorDialog(string title, string message)
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
public class DumpFileEntry : INotifyPropertyChanged
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

    public string SizeFormatted
    {
        get
        {
            if (Size >= 1024 * 1024 * 1024)
                return $"{Size / (1024.0 * 1024.0 * 1024.0):F2} GB";
            if (Size >= 1024 * 1024)
                return $"{Size / (1024.0 * 1024.0):F2} MB";
            if (Size >= 1024)
                return $"{Size / 1024.0:F2} KB";
            return $"{Size} B";
        }
    }

    public Brush StatusColor => Status switch
    {
        "Complete" => new SolidColorBrush(Microsoft.UI.Colors.Green),
        "Skipped" => new SolidColorBrush(Microsoft.UI.Colors.Gray),
        "Processing..." => new SolidColorBrush(Microsoft.UI.Colors.Blue),
        _ when Status.StartsWith("Error") => new SolidColorBrush(Microsoft.UI.Colors.Red),
        _ => new SolidColorBrush(Microsoft.UI.Colors.Gray)
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
