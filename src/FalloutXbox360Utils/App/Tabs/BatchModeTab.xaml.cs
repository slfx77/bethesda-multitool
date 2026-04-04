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
        var dmpFiles = selectedFiles
            .Where(f => f.FilePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.FilePath)
            .OrderBy(f => new FileInfo(f).LastWriteTimeUtc)
            .ToList();

        if (dmpFiles.Count < 2) return;

        var dumpData =
            new List<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)>();

        foreach (var dmpFile in dmpFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var analyzer = new MinidumpAnalyzer();
                var result = await analyzer.AnalyzeAsync(dmpFile, includeMetadata: true, verbose: false);
                if (result.EsmRecords == null || result.EsmRecords.MainRecords.Count == 0) continue;

                var fileSize = new FileInfo(dmpFile).Length;
                using var mmf = MemoryMappedFile.CreateFromFile(
                    dmpFile, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
                using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                var parser = new RecordParser(
                    result.EsmRecords, result.FormIdMap, accessor, fileSize, result.MinidumpInfo);
                var records = parser.ParseAll();
                var resolver = records.CreateResolver(result.FormIdMap);
                dumpData.Add((dmpFile, records, resolver, result.MinidumpInfo));
            }
            catch
            {
                // Skip dumps that fail to parse
            }
        }

        if (dumpData.Count < 2) return;

        var index = CrossDumpAggregator.Aggregate(dumpData);
        var htmlFiles = CrossDumpHtmlWriter.GenerateAll(index);

        var comparisonDir = Path.Combine(outputPath, "comparison");
        Directory.CreateDirectory(comparisonDir);
        foreach (var (filename, content) in htmlFiles)
        {
            await File.WriteAllTextAsync(Path.Combine(comparisonDir, filename), content, ct);
        }
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
        // Create working copy of current load order (preserves already-loaded data)
        var workingEntries = new ObservableCollection<LoadOrderEntry>(
            _loadOrder.Entries.Select(existing => new LoadOrderEntry
            {
                FilePath = existing.FilePath,
                FileType = existing.FileType,
                Resolver = existing.Resolver,
                Records = existing.Records
            }));

        // Build dialog content
        var panel = new StackPanel { Spacing = 12 };

        panel.Children.Add(new TextBlock
        {
            Text = "Add supplementary ESM/ESP/DMP files to enrich batch reports with NPC names, quest names, etc.",
            TextWrapping = TextWrapping.Wrap,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        });

        // Load order ListView
        var listView = new ListView
        {
            ItemsSource = workingEntries,
            CanReorderItems = true,
            AllowDrop = true,
            SelectionMode = ListViewSelectionMode.None,
            MinHeight = 80,
            MaxHeight = 300
        };

        listView.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Grid ColumnDefinitions="Auto,*,Auto" Margin="0,2">
                    <TextBlock Grid.Column="0" VerticalAlignment="Center"
                               Margin="0,0,12,0" Opacity="0.6"
                               Text="&#x2261;" FontSize="16" />
                    <TextBlock Grid.Column="1" VerticalAlignment="Center"
                               Text="{Binding DisplayName}" TextTrimming="CharacterEllipsis" />
                    <Button Grid.Column="2" Content="&#xE711;" FontFamily="Segoe MDL2 Assets"
                            FontSize="10" Padding="6,4" Margin="8,0,0,0"
                            Background="Transparent" Tag="{Binding}" />
                </Grid>
            </DataTemplate>
            """);

        listView.ContainerContentChanging += (_, args) =>
        {
            if (args.Phase != 0) return;
            var root = args.ItemContainer.ContentTemplateRoot as Grid;
            var removeBtn = root?.Children.OfType<Button>().FirstOrDefault();
            if (removeBtn != null)
            {
                removeBtn.Click -= RemoveEntryClick;
                removeBtn.Click += RemoveEntryClick;
            }
        };

        void RemoveEntryClick(object s, RoutedEventArgs _)
        {
            if (s is Button btn && btn.Tag is LoadOrderEntry entry)
                workingEntries.Remove(entry);
        }

        panel.Children.Add(listView);

        // Empty state text
        var emptyText = new TextBlock
        {
            Text = "No files added. Click \"Add Files\" to get started.",
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Visibility = workingEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed,
            Margin = new Thickness(0, -4, 0, 0)
        };
        workingEntries.CollectionChanged += (_, _) =>
            emptyText.Visibility = workingEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        panel.Children.Add(emptyText);

        // Add Files button
        var addButton = new Button
        {
            Content = "Add Files...",
            Margin = new Thickness(0, 4, 0, 0)
        };
        addButton.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            foreach (var ext in new[] { ".esm", ".esp", ".dmp" })
                picker.FileTypeFilter.Add(ext);
            InitializeWithWindow.Initialize(picker,
                WindowNative.GetWindowHandle(App.Current.MainWindow));

            var files = await picker.PickMultipleFilesAsync();
            if (files == null || files.Count == 0) return;

            foreach (var file in files)
            {
                var path = file.Path;

                // Skip duplicates
                if (workingEntries.Any(en => string.Equals(en.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var fileType = FileTypeDetector.Detect(path);
                if (fileType == AnalysisFileType.Unknown) continue;

                workingEntries.Add(new LoadOrderEntry
                {
                    FilePath = path,
                    FileType = fileType
                });
            }
        };
        panel.Children.Add(addButton);

        // Show dialog
        var dialog = new ContentDialog
        {
            Title = "Load Order",
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            },
            PrimaryButtonText = "Load",
            SecondaryButtonText = _loadOrder.HasData ? "Clear All" : null,
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Secondary)
        {
            _loadOrder.Dispose();
            UpdateLoadOrderStatusText();
            return;
        }

        if (result != ContentDialogResult.Primary) return;
        if (workingEntries.Count == 0) return;

        try
        {
            LoadOrderButton.IsEnabled = false;
            StatusTextBlock.Text = "Loading load order data...";

            // Load new entries that haven't been loaded yet
            for (var i = 0; i < workingEntries.Count; i++)
            {
                var entry = workingEntries[i];
                if (entry.IsLoaded) continue;

                StatusTextBlock.Text = $"Loading {entry.DisplayName} ({i + 1}/{workingEntries.Count})...";
                await LoadSingleEntryAsync(entry);
            }

            // Replace load order entries with working copy
            _loadOrder.Dispose();
            foreach (var entry in workingEntries)
                _loadOrder.Entries.Add(entry);

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

    private async Task LoadSingleEntryAsync(LoadOrderEntry entry)
    {
        var path = entry.FilePath;
        var fileType = entry.FileType;

        if (fileType != AnalysisFileType.EsmFile && fileType != AnalysisFileType.Minidump)
        {
            await ShowDialogAsync("Load Failed",
                $"Only ESM, ESP, and DMP files are supported: {entry.DisplayName}", true);
            return;
        }

        // Run analysis
        AnalysisResult analysisResult;
        if (fileType == AnalysisFileType.EsmFile)
        {
            analysisResult = await EsmFileAnalyzer.AnalyzeAsync(path, null);
        }
        else
        {
            analysisResult = await new MinidumpAnalyzer().AnalyzeAsync(path, null);
        }

        if (analysisResult.EsmRecords == null)
        {
            await ShowDialogAsync("Load Failed",
                $"No ESM records found in: {entry.DisplayName}", true);
            return;
        }

        // Parse records
        StatusTextBlock.Text = $"Parsing {entry.DisplayName}...";

        var fileSize = new FileInfo(path).Length;
        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileSize, MemoryMappedFileAccess.Read);

        var records = await Task.Run(() =>
        {
            var parser = new RecordParser(
                analysisResult.EsmRecords,
                analysisResult.FormIdMap,
                accessor,
                fileSize,
                analysisResult.MinidumpInfo);
            return parser.ParseAll();
        });

        entry.Resolver = records.CreateResolver();
        entry.Records = records;
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
