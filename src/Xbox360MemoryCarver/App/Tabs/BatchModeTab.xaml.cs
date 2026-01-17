using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Xbox360MemoryCarver.Core;

namespace Xbox360MemoryCarver;

/// <summary>
///     Batch processing tab for multiple dump files.
/// </summary>
public sealed partial class BatchModeTab : UserControl, IDisposable
{
    private readonly ObservableCollection<DumpFileEntry> _dumpFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private CancellationTokenSource? _cts;
    private bool _dependencyCheckDone;
    private bool _sortAscending = true;
    private BatchSortColumn _sortColumn = BatchSortColumn.None;

    public BatchModeTab()
    {
        InitializeComponent();
        DumpFilesListView.ItemsSource = _dumpFiles;
        InitializeFileTypeCheckboxes();
        SetupTextBoxContextMenus();
        ParallelCountBox.Maximum = Environment.ProcessorCount;
        Loaded += BatchModeTab_Loaded;
    }

    /// <summary>
    ///     Helper to route status text to the global status bar.
    /// </summary>
    private StatusTextHelper StatusTextBlock => new();

    public void Dispose()
    {
        _cts?.Dispose();
    }

    private async void BatchModeTab_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= BatchModeTab_Loaded;

        // Check dependencies on first load
        if (!_dependencyCheckDone)
        {
            _dependencyCheckDone = true;
            await CheckDependenciesAsync();
        }
    }

    private async Task CheckDependenciesAsync()
    {
        // Only show the dialog once per session (shared with SingleFileTab)
        if (DependencyChecker.CarverDependenciesShown) return;

        // Small delay to ensure the UI is fully loaded
        await Task.Delay(100);

        var result = DependencyChecker.CheckCarverDependencies();
        if (!result.AllAvailable)
        {
            DependencyChecker.CarverDependenciesShown = true;
            await DependencyDialogHelper.ShowIfMissingAsync(result, XamlRoot);
        }
    }

    private void SetupTextBoxContextMenus()
    {
        TextBoxContextMenuHelper.AttachContextMenu(InputDirectoryTextBox);
        TextBoxContextMenuHelper.AttachContextMenu(OutputDirectoryTextBox);
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

    private void UpdateButtonStates()
    {
        ExtractButton.IsEnabled = !string.IsNullOrEmpty(OutputDirectoryTextBox.Text)
                                  && _dumpFiles.Any(f => f.IsSelected);
    }

    // XAML event handlers require instance methods - cannot be made static
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "XAML event handler requires instance method")]
    [SuppressMessage("SonarQube", "S2325:Methods should be static",
        Justification = "XAML event handler requires instance method")]
    private void ParallelCountBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) sender.Value = 2;
    }

    private async Task ShowDialogAsync(string title, string message, bool isError = false)
    {
        if (isError)
            await ErrorDialogHelper.ShowErrorAsync(title, message, XamlRoot);
        else
            await ErrorDialogHelper.ShowInfoAsync(title, message, XamlRoot);
    }

    private async void BrowseInputButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        InputDirectoryTextBox.Text = folder.Path;
        if (string.IsNullOrEmpty(OutputDirectoryTextBox.Text))
            OutputDirectoryTextBox.Text = Path.Combine(folder.Path, "extracted");

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
        var directory = InputDirectoryTextBox.Text;

        if (!Directory.Exists(directory))
        {
            StatusTextBlock.Text = "";
            UpdateButtonStates();
            return;
        }

        foreach (var file in Directory.GetFiles(directory, "*.dmp", SearchOption.AllDirectories))
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

    private void InputDirectoryTextBox_TextChanged(object _sender, TextChangedEventArgs _e)
    {
        if (Directory.Exists(InputDirectoryTextBox.Text))
        {
            if (string.IsNullOrEmpty(OutputDirectoryTextBox.Text))
                OutputDirectoryTextBox.Text = Path.Combine(InputDirectoryTextBox.Text, "extracted");
            ScanForDumpFiles();
        }
        else
        {
            _dumpFiles.Clear();
            StatusTextBlock.Text = "";
            UpdateButtonStates();
        }
    }

    private void OutputDirectoryTextBox_TextChanged(object _sender, TextChangedEventArgs _e)
    {
        UpdateButtonStates();
    }

    private void SelectAllButton_Click(object _sender, RoutedEventArgs _e)
    {
        foreach (var entry in _dumpFiles) entry.IsSelected = true;
    }

    private void SelectNoneButton_Click(object _sender, RoutedEventArgs _e)
    {
        foreach (var entry in _dumpFiles) entry.IsSelected = false;
    }

    private async void ExtractButton_Click(object _sender, RoutedEventArgs _e)
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

            var processed = 0;
            var total = selectedFiles.Count;
            var skipExisting = SkipExistingCheckBox.IsChecked is true;

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
            await ShowDialogAsync("Batch Processing Failed", $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                true);
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

            // Run extraction on background thread to avoid blocking UI
            await Task.Run(
                async () => await MemoryDumpExtractor.Extract(
                    entry.FilePath,
                    options with { OutputPath = outputSubdir },
                    null),
                token);

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

    private void CancelButton_Click(object _sender, RoutedEventArgs _e)
    {
        _cts?.Cancel();
        StatusTextBlock.Text = "Cancelling...";
    }

    #region Sorting

    private void SortByFilename_Click(object _sender, RoutedEventArgs _e)
    {
        ApplySort(BatchSortColumn.Filename);
    }

    private void SortBySize_Click(object _sender, RoutedEventArgs _e)
    {
        ApplySort(BatchSortColumn.Size);
    }

    private void SortByStatus_Click(object _sender, RoutedEventArgs _e)
    {
        ApplySort(BatchSortColumn.Status);
    }

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

        // Batch update - suspend UI binding during sort refresh
        DumpFilesListView.ItemsSource = null;
        _dumpFiles.Clear();
        foreach (var item in list) _dumpFiles.Add(item);
        DumpFilesListView.ItemsSource = _dumpFiles;
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
}
