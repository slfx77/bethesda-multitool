using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.MemoryMappedFiles;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Extraction;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Minidump;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     Batch processing tab for multiple dump files.
/// </summary>
public sealed partial class BatchModeTab : UserControl, IDisposable, IHasSettingsDrawer
{
    private readonly ObservableCollection<DumpFileEntry> _dumpFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private readonly LoadOrder _loadOrder = new();
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
#pragma warning disable CA1822, S2325
    private StatusTextHelper StatusTextBlock => new();
#pragma warning restore CA1822, S2325
    public void ToggleSettingsDrawer() => SettingsDrawerHelper.Toggle(SettingsDrawer);
    public void CloseSettingsDrawer() => SettingsDrawerHelper.Close(SettingsDrawer);

    public void Dispose()
    {
        _cts?.Dispose();
        _loadOrder.Dispose();
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
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

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
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

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

    private void InputDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
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

    private void OutputDirectoryTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
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
                FileTypes = selectedTypes.Count > 0 ? selectedTypes : null,
                PcFriendly = true,
                GenerateEsmReports = BatchGenerateReportsCheckBox.IsChecked is true
            };

            // Pre-build merged load order records once for all entries
            var supplementaryRecords = _loadOrder.HasData ? _loadOrder.BuildMergedRecords() : null;

            var processed = 0;
            var total = selectedFiles.Count;
            var skipExisting = SkipExistingCheckBox.IsChecked is true;

            var tasks = selectedFiles.Select(entry => ProcessEntryAsync(
                entry, options, skipExisting, semaphore,
                () => UpdateProgress(Interlocked.Increment(ref processed), total),
                supplementaryRecords, token));

            await Task.WhenAll(tasks);

            // Cross-dump comparison report (if enabled)
            if (BatchCrossDumpCompareCheckBox.IsChecked is true)
            {
                StatusTextBlock.Text = "Generating cross-dump comparison...";
                await GenerateCrossDumpComparisonAsync(selectedFiles, options.OutputPath, token);
            }

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

    private async Task GenerateCrossDumpComparisonAsync(
        List<DumpFileEntry> selectedFiles, string outputPath, CancellationToken ct)
    {
        var sourceFiles = selectedFiles
            .Where(f =>
                f.FilePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ||
                f.FilePath.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FilePath)
            .OrderBy(f => new FileInfo(f).LastWriteTimeUtc)
            .ToList();

        if (sourceFiles.Count < 2)
        {
            return;
        }

        var comparisonDir = Path.Combine(outputPath, "comparison");
        var comparison = await CrossDumpComparisonPipeline.BuildAsync(
            new CrossDumpComparisonRequest
            {
                SourceFiles = sourceFiles,
                OutputPath = comparisonDir,
                TypeFilter = null,
                OutputFormat = "html",
                Verbose = false
            },
            status => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = status),
            ct);

        await CrossDumpOutputWriter.WriteAsync(comparison.Index, comparisonDir, "html", ct);
    }

    private async Task ProcessEntryAsync(
        DumpFileEntry entry, ExtractionOptions options, bool skipExisting,
        SemaphoreSlim semaphore, Action onComplete,
        RecordCollection? supplementaryRecords, CancellationToken token)
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

            // Phase 1: Run analysis if report generation is enabled
            AnalysisResult? analysisResult = null;
            if (options.GenerateEsmReports)
            {
                DispatcherQueue.TryEnqueue(() => entry.Status = "Analyzing...");
                analysisResult = await Task.Run(async () =>
                {
                    var analyzer = new MinidumpAnalyzer();
                    return await analyzer.AnalyzeAsync(
                        entry.FilePath, null, true, options.Verbose);
                }, token);
            }

            // Phase 2: Extract files (and generate reports if analysis was run)
            DispatcherQueue.TryEnqueue(() => entry.Status = "Extracting...");
            await Task.Run(
                async () => await MinidumpExtractor.Extract(
                    entry.FilePath,
                    options with { OutputPath = outputSubdir },
                    null,
                    analysisResult,
                    supplementaryRecords),
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

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusTextBlock.Text = "Cancelling...";
    }

    #region Load Order

    private async void LoadOrderButton_Click(object sender, RoutedEventArgs e)
    {
        var workingEntries = LoadOrderDialogService.CreateWorkingEntries(_loadOrder.Entries);
        var dialogResult = await LoadOrderDialogService.ShowAsync(
            XamlRoot,
            workingEntries,
            new LoadOrderDialogOptions
            {
                Title = "Load Order",
                IntroText =
                    "Add supplementary ESM/ESP/DMP files to enrich batch reports with NPC names, quest names, etc."
            });

        switch (dialogResult.Action)
        {
            case LoadOrderDialogAction.Cancel:
                return;
            case LoadOrderDialogAction.ClearAll:
                _loadOrder.Dispose();
                UpdateLoadOrderStatusText();
                return;
        }

        if (dialogResult.Entries.Count == 0)
        {
            return;
        }

        try
        {
            LoadOrderButton.IsEnabled = false;
            StatusTextBlock.Text = "Loading load order data...";

            await LoadOrderDialogService.ApplyAsync(
                _loadOrder,
                dialogResult.Entries,
                dialogResult.SubtitleCsvPath,
                status => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = status));

            UpdateLoadOrderStatusText();
            StatusTextBlock.Text = "Load order data loaded.";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Load Failed",
                $"Failed to load load order data:\n{ex.GetType().Name}: {ex.Message}", true);
        }
        finally
        {
            LoadOrderButton.IsEnabled = true;
        }
    }

    private void UpdateLoadOrderStatusText()
    {
        if (!_loadOrder.HasData)
        {
            LoadOrderStatusText.Text = "";
            return;
        }

        var count = _loadOrder.Entries.Count;
        LoadOrderStatusText.Text = $"{count} file{(count == 1 ? "" : "s")}";
    }

    #endregion

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
