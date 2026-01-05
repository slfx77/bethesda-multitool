using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private CancellationTokenSource? _cts;
    private bool _sortAscending = true;
    private BatchSortColumn _sortColumn = BatchSortColumn.None;

    public BatchModeTab()
    {
        InitializeComponent();
        DumpFilesListView.ItemsSource = _dumpFiles;
        InitializeFileTypeCheckboxes();
        ParallelCountBox.Maximum = Environment.ProcessorCount;
    }

    public void Dispose() => _cts?.Dispose();

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

    private void UpdateButtonStates()
    {
        ExtractButton.IsEnabled = !string.IsNullOrEmpty(OutputDirectoryTextBox.Text)
                                  && _dumpFiles.Any(f => f.IsSelected);
    }

#pragma warning disable CA1822, S2325 // Event handler cannot be static
    private void ParallelCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) sender.Value = 2;
    }
#pragma warning restore CA1822, S2325

    private async Task ShowDialogAsync(string title, string message)
    {
        var dialog = new ContentDialog
            { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = XamlRoot };
        await dialog.ShowAsync();
    }

#pragma warning disable RCS1163
    private async void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        InputDirectoryTextBox.Text = folder.Path;
        if (string.IsNullOrEmpty(OutputDirectoryTextBox.Text))
        {
            OutputDirectoryTextBox.Text = Path.Combine(folder.Path, "extracted");
        }

        ScanForDumpFiles();
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

    private void ScanForDumpFiles()
    {
        _dumpFiles.Clear();
        if (!Directory.Exists(InputDirectoryTextBox.Text)) return;

        foreach (var file in Directory.GetFiles(InputDirectoryTextBox.Text, "*.dmp", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            var entry = new DumpFileEntry
            {
                FilePath = file,
                FileName = info.Name,
                Size = info.Length,
                IsSelected = true
            };
            entry.PropertyChanged += (_, _) => UpdateButtonStates();
            _dumpFiles.Add(entry);
        }

        StatusTextBlock.Text = $"Found {_dumpFiles.Count} dump file(s)";
        UpdateButtonStates();
    }

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _dumpFiles) entry.IsSelected = true;
    }

    private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _dumpFiles) entry.IsSelected = false;
    }

    #region Sorting

    private void SortByFilename_Click(object sender, RoutedEventArgs e) => ApplySort(BatchSortColumn.Filename);
    private void SortBySize_Click(object sender, RoutedEventArgs e) => ApplySort(BatchSortColumn.Size);
    private void SortByStatus_Click(object sender, RoutedEventArgs e) => ApplySort(BatchSortColumn.Status);

    private void ApplySort(BatchSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        UpdateSortIcons();

        var sorted = _sortColumn switch
        {
            BatchSortColumn.Filename => _sortAscending
                ? _dumpFiles.OrderBy(f => f.FileName)
                : _dumpFiles.OrderByDescending(f => f.FileName),
            BatchSortColumn.Size => _sortAscending
                ? _dumpFiles.OrderBy(f => f.Size)
                : _dumpFiles.OrderByDescending(f => f.Size),
            BatchSortColumn.Status => _sortAscending
                ? _dumpFiles.OrderBy(f => f.Status)
                : _dumpFiles.OrderByDescending(f => f.Status),
            _ => _dumpFiles.AsEnumerable()
        };

        var list = sorted.ToList();
        _dumpFiles.Clear();
        foreach (var item in list) _dumpFiles.Add(item);
    }

    private void UpdateSortIcons()
    {
        var glyph = _sortAscending ? "\uE70D" : "\uE70E";
        FilenameSortIcon.Visibility =
            _sortColumn == BatchSortColumn.Filename ? Visibility.Visible : Visibility.Collapsed;
        FilenameSortIcon.Glyph = glyph;
        SizeSortIcon.Visibility = _sortColumn == BatchSortColumn.Size ? Visibility.Visible : Visibility.Collapsed;
        SizeSortIcon.Glyph = glyph;
        StatusSortIcon.Visibility = _sortColumn == BatchSortColumn.Status ? Visibility.Visible : Visibility.Collapsed;
        StatusSortIcon.Glyph = glyph;
    }

    #endregion

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedFiles = _dumpFiles.Where(f => f.IsSelected).ToList();
        if (selectedFiles.Count == 0) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var semaphore = new SemaphoreSlim((int)ParallelCountBox.Value);

        try
        {
            SetUIExtracting(true);

            var selectedTypes = FileTypeMapping
                .GetSignatureIds(_fileTypeCheckboxes.Where(kvp => kvp.Value.IsChecked is true).Select(kvp => kvp.Key))
                .ToList();

            var options = new ExtractionOptions
            {
                OutputPath = OutputDirectoryTextBox.Text,
                ConvertDdx = BatchConvertDdxCheckBox.IsChecked is true,
                SaveAtlas = BatchSaveAtlasCheckBox.IsChecked is true,
                Verbose = BatchVerboseCheckBox.IsChecked is true,
                FileTypes = selectedTypes.Count > 0 ? selectedTypes : null
            };

            int processed = 0;
            int total = selectedFiles.Count;
            bool skipExisting = SkipExistingCheckBox.IsChecked is true;

            var tasks = selectedFiles.Select(entry => ProcessEntryAsync(
                entry, options, skipExisting, semaphore,
                () => UpdateProgress(Interlocked.Increment(ref processed), total), token));

            await Task.WhenAll(tasks);
            StatusTextBlock.Text = $"Completed processing {processed} file(s)";
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
            SetUIExtracting(false);
            _cts?.Dispose();
            _cts = null;
            semaphore.Dispose();
        }
    }

    private async Task ProcessEntryAsync(
        DumpFileEntry entry, ExtractionOptions options, bool skipExisting,
        SemaphoreSlim semaphore, Action onComplete, CancellationToken token)
    {
        await semaphore.WaitAsync(token);
        try
        {
            var outputSubdir = Path.Combine(options.OutputPath, Path.GetFileNameWithoutExtension(entry.FileName));
            if (skipExisting && Directory.Exists(outputSubdir))
            {
                DispatcherQueue.TryEnqueue(() => entry.Status = "Skipped");
                return;
            }

            DispatcherQueue.TryEnqueue(() => entry.Status = "Processing...");
            await MemoryDumpExtractor.Extract(entry.FilePath, options with { OutputPath = outputSubdir }, null);
            DispatcherQueue.TryEnqueue(() => entry.Status = "Complete");
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() => entry.Status = $"Error: {ex.Message}");
        }
        finally
        {
            try
            {
                semaphore.Release();
            }
            catch (ObjectDisposedException)
            {
                // Semaphore disposed during cancellation - safe to ignore
            }

            onComplete();
        }
    }

    private void SetUIExtracting(bool extracting)
    {
        ExtractButton.IsEnabled = !extracting;
        CancelButton.IsEnabled = extracting;
        BatchProgressBar.Visibility = extracting ? Visibility.Visible : Visibility.Collapsed;
        ProgressTextBlock.Visibility = extracting ? Visibility.Visible : Visibility.Collapsed;
        if (extracting)
        {
            BatchProgressBar.Value = 0;
            ProgressTextBlock.Text = "Starting...";
        }
        else
        {
            ProgressTextBlock.Text = "";
            UpdateButtonStates();
        }
    }

    private void UpdateProgress(int current, int total)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            BatchProgressBar.Value = current * 100.0 / total;
            ProgressTextBlock.Text = $"Processing {current}/{total}...";
        });
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusTextBlock.Text = "Cancelling...";
    }
#pragma warning restore RCS1163
}
