using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Export;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     Single file analysis and extraction tab with sub-tabs for
///     Memory Map, Data Browser, Reports, and Coverage.
/// </summary>
public sealed partial class SingleFileTab : UserControl
{
    private readonly List<CarvedFileEntry> _allCarvedFiles = [];
    private readonly ObservableCollection<CarvedFileEntry> _carvedFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private readonly ObservableCollection<ReportEntry> _reportEntries = [];
    private readonly AnalysisSessionState _session = new();
    private readonly CarvedFilesSorter _sorter = new();

    // Coverage gap sorting state
    private List<CoverageGapEntry> _allCoverageGapEntries = [];

    private AnalysisResult? _analysisResult;
    private CarvedFileEntry? _contextMenuTarget;
    private bool _coverageGapSortAscending = true;
    private CoverageGapSortColumn _coverageGapSortColumn = CoverageGapSortColumn.Index;
    private bool _coveragePopulated;

    private bool _dependencyCheckDone;
    private ObservableCollection<EsmBrowserNode>? _esmBrowserTree;
    private bool _flatListBuilt;
    private CancellationTokenSource? _searchDebounceToken;
    private string? _lastInputPath;
    private string _reportFullContent = "";
    private int[] _reportLineOffsets = [];

    // Report viewer virtualization — only renders visible lines for memory efficiency
    private string[] _reportLines = [];
    private int _reportSearchIndex;

    // Report search state
    private List<int> _reportSearchMatches = [];
    private string _reportSearchQuery = "";
    private int _reportViewportLineCount = 50;
    private EsmBrowserNode? _selectedBrowserNode;

    public SingleFileTab()
    {
        InitializeComponent();
        ResultsListView.ItemsSource = _carvedFiles;
        ReportListView.ItemsSource = _reportEntries;
        InitializeFileTypeCheckboxes();
        SetupTextBoxContextMenus();
        Loaded += SingleFileTab_Loaded;
        Unloaded += SingleFileTab_Unloaded;
    }

    /// <summary>Helper to route status messages to the global status bar.</summary>
#pragma warning disable CA1822, S2325
    private StatusTextHelper StatusTextBlock => new();
#pragma warning restore CA1822, S2325

    private void SingleFileTab_Unloaded(object sender, RoutedEventArgs e)
    {
        _session.Dispose();
    }

    private void SetupTextBoxContextMenus()
    {
        TextBoxContextMenuHelper.AttachContextMenu(MinidumpPathTextBox);
        TextBoxContextMenuHelper.AttachContextMenu(OutputPathTextBox);
    }

    private async void SingleFileTab_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= SingleFileTab_Loaded;

        if (!_dependencyCheckDone)
        {
            _dependencyCheckDone = true;
            await CheckDependenciesAsync();
        }

        var autoLoadFile = Program.AutoLoadFile;
        if (string.IsNullOrEmpty(autoLoadFile) || !File.Exists(autoLoadFile)) return;

        MinidumpPathTextBox.Text = autoLoadFile;
        UpdateOutputPathFromInput(autoLoadFile);
        UpdateButtonStates();
        await Task.Delay(500);
        if (AnalyzeButton.IsEnabled) AnalyzeButton_Click(this, new RoutedEventArgs());
    }

    private async Task CheckDependenciesAsync()
    {
        if (DependencyChecker.CarverDependenciesShown) return;
        await Task.Delay(100);
        var result = DependencyChecker.CheckCarverDependencies();
        if (!result.AllAvailable)
        {
            DependencyChecker.CarverDependenciesShown = true;
            await DependencyDialogHelper.ShowIfMissingAsync(result, XamlRoot);
        }
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

    private void MinidumpPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var currentPath = MinidumpPathTextBox.Text;
        if (!string.IsNullOrEmpty(currentPath) && currentPath != _lastInputPath &&
            File.Exists(currentPath) && FileTypeDetector.IsSupportedExtension(currentPath))
        {
            UpdateOutputPathFromInput(currentPath);
            _lastInputPath = currentPath;
            if (_analysisResult != null)
            {
                _analysisResult = null;
                _carvedFiles.Clear();
                _allCarvedFiles.Clear();
                HexViewer.Clear();
                ResetSubTabs();
            }
        }

        UpdateButtonStates();
    }

    private void OutputPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateButtonStates();
    }

    private void UpdateOutputPathFromInput(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        OutputPathTextBox.Text = Path.Combine(dir, $"{name}_extracted");
    }

    private void UpdateButtonStates()
    {
        var valid = !string.IsNullOrEmpty(MinidumpPathTextBox.Text) && File.Exists(MinidumpPathTextBox.Text) &&
                    FileTypeDetector.IsSupportedExtension(MinidumpPathTextBox.Text);
        AnalyzeButton.IsEnabled = valid;
        ExtractButton.IsEnabled = valid && _analysisResult != null && !string.IsNullOrEmpty(OutputPathTextBox.Text);
    }

    private void ResetSubTabs()
    {
        _session.Dispose();
        CoverageTab.IsEnabled = false;
        DataBrowserTab.IsEnabled = false;
        ReportsTab.IsEnabled = false;
        _coveragePopulated = false;
        _allCoverageGapEntries = [];
        _reportEntries.Clear();
        ReportPreviewTextBox.Text = "";
        _reportLines = [];
        _reportLineOffsets = [];
        _reportFullContent = "";
        ReportViewerScrollBar.Maximum = 0;
        ReportViewerScrollBar.Value = 0;
        _reportSearchMatches = [];
        _reportSearchIndex = 0;
        _reportSearchQuery = "";
        DataBrowserPlaceholder.Visibility = Visibility.Visible;
        DataBrowserContent.Visibility = Visibility.Collapsed;
        ReconstructStatusText.Text = Strings.Empty_RunAnalysisForEsm;
        EsmTreeView.RootNodes.Clear();
        _flatListBuilt = false;
        _esmBrowserTree = null;
        _currentSearchQuery = "";
        EsmSearchBox.Text = "";
        PropertyPanel.Children.Clear();
        SelectedRecordTitle.Text = Strings.Empty_SelectARecord;
        GoToOffsetButton.Visibility = Visibility.Collapsed;
        ExportAllReportsButton.IsEnabled = false;
        ExportSelectedReportButton.IsEnabled = false;
    }

    private async Task ShowDialogAsync(string title, string message, bool isError = false)
    {
        if (isError)
        {
            await ErrorDialogHelper.ShowErrorAsync(title, message, XamlRoot);
        }
        else
        {
            await ErrorDialogHelper.ShowInfoAsync(title, message, XamlRoot);
        }
    }

    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsListView.SelectedItem is CarvedFileEntry f) HexViewer.NavigateToOffset(f.Offset);
    }

    private async void OpenMinidumpButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".dmp");
        picker.FileTypeFilter.Add(".esm");
        picker.FileTypeFilter.Add(".esp");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        MinidumpPathTextBox.Text = file.Path;
        _analysisResult = null;
        _carvedFiles.Clear();
        _allCarvedFiles.Clear();
        HexViewer.Clear();
        ResetSubTabs();
        UpdateButtonStates();
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
            OutputPathTextBox.Text = folder.Path;
            UpdateButtonStates();
        }
    }

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        var filePath = MinidumpPathTextBox.Text;
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            AnalyzeButton.IsEnabled = false;
            AnalysisProgressBar.Visibility = Visibility.Visible;
            _carvedFiles.Clear();
            _allCarvedFiles.Clear();
            _sorter.Reset();
            UpdateSortIcons();
            ResetSubTabs();

            if (!File.Exists(filePath))
            {
                await ShowDialogAsync("Analysis Failed", $"File not found: {filePath}");
                return;
            }

            // Detect file type by magic bytes
            var fileType = FileTypeDetector.Detect(filePath);
            if (fileType == AnalysisFileType.Unknown)
            {
                await ShowDialogAsync("Analysis Failed", $"Unknown file type: {filePath}");
                return;
            }

            StatusTextBlock.Text = fileType == AnalysisFileType.EsmFile
                ? Strings.Status_StartingEsmAnalysis
                : Strings.Status_StartingAnalysis;

            var progress = new Progress<AnalysisProgress>(p => DispatcherQueue.TryEnqueue(() =>
            {
                AnalysisProgressBar.IsIndeterminate = false;
                AnalysisProgressBar.Value = p.PercentComplete;

                // Display phase and progress in status bar
                var phaseText = p.Phase switch
                {
                    // ESM file analysis phases
                    "Loading" => Strings.Status_LoadingFile,
                    "Parsing Header" => Strings.Status_ParsingEsmHeader,
                    "Scanning Records" when p.FilesFound > 0 => Strings.Status_ScanningRecords(p.FilesFound),
                    "Scanning Records" => Strings.Status_Scanning,
                    "Building Index" => Strings.Status_BuildingIndex_Count(p.FilesFound),
                    "Mapping FormIDs" => Strings.Status_MappingFormIds,
                    "Building Memory Map" => Strings.Status_BuildingMemoryMap,
                    // Memory dump analysis phases
                    "Scanning" when p.TotalBytes > 0 =>
                        Strings.Status_ScanningPercent((int)(p.BytesProcessed * 100 / p.TotalBytes), p.FilesFound),
                    "Scanning" => Strings.Status_ScanningPercent(0, p.FilesFound),
                    "Parsing" => Strings.Status_ParsingMatches(p.FilesFound),
                    "Scripts" => Strings.Status_ExtractingScripts,
                    "ESM Records" when p.TotalBytes > 0 =>
                        Strings.Status_ScanningEsmRecordsPercent((int)(p.BytesProcessed * 100 / p.TotalBytes)),
                    "ESM Records" => Strings.Status_ScanningForEsmRecords,
                    "LAND Records" => Strings.Status_ExtractingLandHeightmaps,
                    "REFR Records" => Strings.Status_ExtractingRefrPositions,
                    "Asset Strings" => Strings.Status_ScanningAssetStrings,
                    "Runtime EditorIDs" => Strings.Status_ExtractingRuntimeEditorIds,
                    "FormIDs" => Strings.Status_CorrelatingFormIdNames,
                    "Complete" => Strings.Status_AnalysisComplete(p.FilesFound),
                    _ => $"{p.Phase}..."
                };
                StatusTextBlock.Text = phaseText;
            }));

            // Fork based on file type
            _analysisResult = fileType switch
            {
                AnalysisFileType.EsmFile => await new EsmFileAnalyzer().AnalyzeAsync(filePath, progress),
                AnalysisFileType.Minidump => await new MemoryDumpAnalyzer().AnalyzeAsync(filePath, progress),
                _ => throw new NotSupportedException($"Unknown file type: {filePath}")
            };

            foreach (var entry in _analysisResult.CarvedFiles)
            {
                var item = new CarvedFileEntry
                {
                    Offset = entry.Offset,
                    Length = entry.Length,
                    FileType = entry.FileType,
                    FileName = entry.FileName
                };
                _allCarvedFiles.Add(item);
                _carvedFiles.Add(item);
            }

            // Add ESM records to the list
            if (_analysisResult.EsmRecords?.MainRecords != null)
            {
                foreach (var esmRecord in _analysisResult.EsmRecords.MainRecords)
                {
                    var item = new CarvedFileEntry
                    {
                        Offset = esmRecord.Offset,
                        Length = esmRecord.DataSize + 24,
                        FileType = "ESM Record",
                        EsmRecordType = esmRecord.RecordType,
                        FormId = esmRecord.FormId,
                        FileName = _analysisResult.FormIdMap.GetValueOrDefault(esmRecord.FormId),
                        Status = ExtractionStatus.Skipped // ESM records start as Skipped
                    };
                    _allCarvedFiles.Add(item);
                }

                _allCarvedFiles.Sort((a, b) => a.Offset.CompareTo(b.Offset));
                _carvedFiles.Clear();
                foreach (var item in _allCarvedFiles)
                {
                    _carvedFiles.Add(item);
                }
            }

            // Enable extract button immediately after successful analysis
            UpdateButtonStates();

            // Open shared session and load hex viewer with shared accessor
            _session.Open(filePath, _analysisResult, fileType);
            HexViewer.LoadData(filePath, _analysisResult, _session.Accessor!);

            // Enable data browser and reports tabs (depends on ESM records, not coverage)
            DataBrowserTab.IsEnabled = _session.HasEsmRecords;
            ReportsTab.IsEnabled = _session.HasEsmRecords;

            // If already on Data Browser tab, auto-reconstruct
            if (_session.HasEsmRecords && ReferenceEquals(SubTabView.SelectedItem, DataBrowserTab))
            {
                ReconstructButton_Click(sender, e);
            }

            // Run coverage analysis (best-effort, doesn't block other functionality)
            try
            {
                StatusTextBlock.Text = Strings.Status_RunningCoverageAnalysis;
                _session.CoverageResult = await Task.Run(() =>
                    new CoverageAnalyzer().Analyze(_session.AnalysisResult!, _session.Accessor!));

                if (_session.CoverageResult.Error == null)
                {
                    HexViewer.AddCoverageGapRegions(_session.CoverageResult);
                    CoverageTab.IsEnabled = true;
                }
            }
            catch (Exception coverageEx)
            {
                StatusTextBlock.Text = Strings.Status_CoverageAnalysisFailed(coverageEx.Message);
            }

            var fileCount = _allCarvedFiles.Count;
            var coveragePct = _session.CoverageResult?.RecognizedPercent ?? 0;
            StatusTextBlock.Text = Strings.Status_FoundFilesToCarve(fileCount, coveragePct);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Analysis Failed", $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                true);
        }
        finally
        {
            AnalyzeButton.IsEnabled = true;
            AnalysisProgressBar.Visibility = Visibility.Collapsed;
            AnalysisProgressBar.IsIndeterminate = true;
        }
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        var filePath = MinidumpPathTextBox.Text;
        var outputPath = OutputPathTextBox.Text;
        if (_analysisResult == null || string.IsNullOrEmpty(outputPath)) return;
        try
        {
            ExtractButton.IsEnabled = false;
            AnalysisProgressBar.Visibility = Visibility.Visible;
            var types = FileTypeMapping
                .GetSignatureIds(_fileTypeCheckboxes.Where(kvp => kvp.Value.IsChecked == true).Select(kvp => kvp.Key))
                .ToList();
            var opts = new ExtractionOptions
            {
                OutputPath = outputPath,
                ConvertDdx = ConvertDdxCheckBox.IsChecked == true,
                SaveAtlas = SaveAtlasCheckBox.IsChecked == true,
                Verbose = VerboseCheckBox.IsChecked == true,
                FileTypes = types,
                PcFriendly = true,
                GenerateEsmReports = true
            };
            var progress = new Progress<ExtractionProgress>(p => DispatcherQueue.TryEnqueue(() =>
            {
                AnalysisProgressBar.IsIndeterminate = false;
                AnalysisProgressBar.Value = p.PercentComplete;
            }));
            var analysisData = _analysisResult;
            var summary = await Task.Run(() => MemoryDumpExtractor.Extract(filePath, opts, progress, analysisData));

            foreach (var entry in _allCarvedFiles.Where(x => summary.ExtractedOffsets.Contains(x.Offset)))
            {
                if (summary.FailedConversionOffsets.Contains(entry.Offset))
                {
                    entry.Status = ExtractionStatus.Failed;
                }
                else
                {
                    entry.Status = ExtractionStatus.Extracted;
                }
            }

            foreach (var entry in _allCarvedFiles.Where(x => summary.ExtractedModuleOffsets.Contains(x.Offset)))
            {
                entry.Status = ExtractionStatus.Extracted;
            }

            var msg = $"Extraction complete!\n\nFiles extracted: {summary.TotalExtracted}\n";
            if (summary.ModulesExtracted > 0) msg += $"Modules extracted: {summary.ModulesExtracted}\n";
            if (summary.ScriptsExtracted > 0)
            {
                msg +=
                    $"Scripts extracted: {summary.ScriptsExtracted} ({summary.ScriptQuestsGrouped} quests grouped)\n";
            }

            if (summary.DdxConverted > 0 || summary.DdxFailed > 0)
            {
                msg += $"\nDDX conversion: {summary.DdxConverted} ok, {summary.DdxFailed} failed (PC-friendly)";
            }

            if (summary.EsmReportGenerated)
            {
                msg += "\nESM report: generated";
            }

            if (summary.HeightmapsExported > 0)
            {
                msg += $"\nHeightmaps: {summary.HeightmapsExported} exported";
            }

            await ShowDialogAsync("Extraction Complete", msg + $"\n\nOutput: {outputPath}");
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Extraction Failed", $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                true);
        }
        finally
        {
            ExtractButton.IsEnabled = true;
            AnalysisProgressBar.Visibility = Visibility.Collapsed;
            AnalysisProgressBar.IsIndeterminate = true;
        }
    }

    #region Sub-Tab Management

    private void SubTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var selected = SubTabView.SelectedItem;

        if (ReferenceEquals(selected, CoverageTab) && !_coveragePopulated && _session.CoverageResult != null)
        {
            PopulateCoverageTab();
        }

        // Auto-populate Data Browser when first selected
        if (ReferenceEquals(selected, DataBrowserTab) &&
            DataBrowserContent.Visibility == Visibility.Collapsed &&
            _session.HasEsmRecords)
        {
            ReconstructButton_Click(sender, new RoutedEventArgs());
        }

        // Auto-generate reports when first selected
        if (ReferenceEquals(selected, ReportsTab) &&
            _reportEntries.Count == 0 &&
            _session.HasEsmRecords)
        {
            GenerateReportsButton_Click(sender, new RoutedEventArgs());
        }
    }

    #endregion

    #region Coverage Tab

    private void PopulateCoverageTab()
    {
        var coverage = _session.CoverageResult;
        if (coverage == null) return;

        var totalRegion = coverage.TotalRegionBytes;

        // Summary text
        var summary = $"File size:           {coverage.FileSize,15:N0} bytes\n" +
                      $"Memory regions:      {coverage.TotalMemoryRegions,6:N0}   (total: {totalRegion:N0} bytes)\n" +
                      $"Minidump overhead:   {coverage.MinidumpOverhead,15:N0} bytes\n\n" +
                      $"Recognized data:     {coverage.TotalRecognizedBytes,15:N0} bytes  ({coverage.RecognizedPercent:F1}%)\n";

        foreach (var (cat, bytes) in coverage.CategoryBytes.OrderByDescending(kv => kv.Value))
        {
            var pct = totalRegion > 0 ? bytes * 100.0 / totalRegion : 0;
            var label = cat switch
            {
                CoverageCategory.Header => "Minidump header",
                CoverageCategory.Module => "Modules",
                CoverageCategory.CarvedFile => "Carved files",
                CoverageCategory.EsmRecord => "ESM records",
                CoverageCategory.ScdaScript => "SCDA scripts",
                _ => cat.ToString()
            };
            summary += $"  {label + ":",-19}{bytes,15:N0} bytes  ({pct,5:F1}%)\n";
        }

        summary += $"\nUncovered:           {coverage.TotalGapBytes,15:N0} bytes  ({coverage.GapPercent:F1}%)";
        CoverageSummaryText.Text = summary;

        // Classification summary - use friendly display names
        var totalGap = coverage.TotalGapBytes;
        var classText = "";
        if (totalGap > 0)
        {
            var byClass = coverage.Gaps
                .GroupBy(g => g.Classification)
                .Select(g => new { Classification = g.Key, TotalBytes = g.Sum(x => x.Size), Count = g.Count() })
                .OrderByDescending(x => x.TotalBytes);

            foreach (var entry in byClass)
            {
                var pct = totalGap > 0 ? entry.TotalBytes * 100.0 / totalGap : 0;
                var displayName = FileTypeColors.GapDisplayNames.GetValueOrDefault(
                    entry.Classification, entry.Classification.ToString());
                classText +=
                    $"{displayName + ":",-18}{entry.TotalBytes,15:N0} bytes  ({pct,5:F1}%)  — {entry.Count:N0} regions\n";
            }
        }
        else
        {
            classText = "No gaps detected — 100% coverage!";
        }

        CoverageClassificationText.Text = classText;

        // Build full gap details list (no limit)
        _allCoverageGapEntries = [];
        for (var i = 0; i < coverage.Gaps.Count; i++)
        {
            var gap = coverage.Gaps[i];
            var gapDisplayName = FileTypeColors.GapDisplayNames.GetValueOrDefault(
                gap.Classification, gap.Classification.ToString());
            _allCoverageGapEntries.Add(new CoverageGapEntry
            {
                Index = i + 1,
                FileOffset = $"0x{gap.FileOffset:X8}",
                Size = FormatSize(gap.Size),
                Classification = gapDisplayName,
                Context = gap.Context,
                RawFileOffset = gap.FileOffset,
                RawSize = gap.Size
            });
        }

        _coverageGapSortColumn = CoverageGapSortColumn.Index;
        _coverageGapSortAscending = true;
        RefreshCoverageGapList();
        CoverageGapListView.ItemTemplate = CreateCoverageGapItemTemplate();
        _coveragePopulated = true;
    }

    private void RefreshCoverageGapList()
    {
        IEnumerable<CoverageGapEntry> sorted = _coverageGapSortColumn switch
        {
            CoverageGapSortColumn.Offset => _coverageGapSortAscending
                ? _allCoverageGapEntries.OrderBy(g => g.RawFileOffset)
                : _allCoverageGapEntries.OrderByDescending(g => g.RawFileOffset),
            CoverageGapSortColumn.Size => _coverageGapSortAscending
                ? _allCoverageGapEntries.OrderBy(g => g.RawSize)
                : _allCoverageGapEntries.OrderByDescending(g => g.RawSize),
            CoverageGapSortColumn.Classification => _coverageGapSortAscending
                ? _allCoverageGapEntries.OrderBy(g => g.Classification, StringComparer.OrdinalIgnoreCase)
                : _allCoverageGapEntries.OrderByDescending(g => g.Classification, StringComparer.OrdinalIgnoreCase),
            _ => _coverageGapSortAscending
                ? _allCoverageGapEntries.OrderBy(g => g.Index)
                : _allCoverageGapEntries.OrderByDescending(g => g.Index)
        };

        CoverageGapListView.ItemsSource = new ObservableCollection<CoverageGapEntry>(sorted);
    }

    private void CycleCoverageSort(CoverageGapSortColumn column)
    {
        if (_coverageGapSortColumn == column)
        {
            if (_coverageGapSortAscending)
            {
                _coverageGapSortAscending = false;
            }
            else
            {
                _coverageGapSortColumn = CoverageGapSortColumn.Index;
                _coverageGapSortAscending = true;
            }
        }
        else
        {
            _coverageGapSortColumn = column;
            _coverageGapSortAscending = true;
        }

        UpdateCoverageSortIcons();
        RefreshCoverageGapList();
    }

    private void UpdateCoverageSortIcons()
    {
        CoverageIndexSortIcon.Visibility = CoverageOffsetSortIcon.Visibility =
            CoverageSizeSortIcon.Visibility = CoverageClassSortIcon.Visibility = Visibility.Collapsed;

        var icon = _coverageGapSortColumn switch
        {
            CoverageGapSortColumn.Index => CoverageIndexSortIcon,
            CoverageGapSortColumn.Offset => CoverageOffsetSortIcon,
            CoverageGapSortColumn.Size => CoverageSizeSortIcon,
            CoverageGapSortColumn.Classification => CoverageClassSortIcon,
            _ => null
        };

        if (icon != null)
        {
            icon.Visibility = Visibility.Visible;
            icon.Glyph = _coverageGapSortAscending ? "\uE70E" : "\uE70D";
        }
    }

    private void CoverageSortByIndex_Click(object sender, RoutedEventArgs e)
    {
        CycleCoverageSort(CoverageGapSortColumn.Index);
    }

    private void CoverageSortByOffset_Click(object sender, RoutedEventArgs e)
    {
        CycleCoverageSort(CoverageGapSortColumn.Offset);
    }

    private void CoverageSortBySize_Click(object sender, RoutedEventArgs e)
    {
        CycleCoverageSort(CoverageGapSortColumn.Size);
    }

    private void CoverageSortByClassification_Click(object sender, RoutedEventArgs e)
    {
        CycleCoverageSort(CoverageGapSortColumn.Classification);
    }

    private static DataTemplate CreateCoverageGapItemTemplate()
    {
        var xaml = """
                   <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                       <Grid Padding="8,2">
                           <Grid.ColumnDefinitions>
                               <ColumnDefinition Width="50" />
                               <ColumnDefinition Width="110" />
                               <ColumnDefinition Width="90" />
                               <ColumnDefinition Width="140" />
                               <ColumnDefinition Width="*" />
                           </Grid.ColumnDefinitions>
                           <TextBlock Grid.Column="0" FontSize="11" Text="{Binding Index}" />
                           <TextBlock Grid.Column="1" FontFamily="Consolas" FontSize="11" Text="{Binding FileOffset}" />
                           <TextBlock Grid.Column="2" FontSize="11" Text="{Binding Size}" />
                           <TextBlock Grid.Column="3" FontSize="11" Text="{Binding Classification}" />
                           <TextBlock Grid.Column="4" FontSize="11" Text="{Binding Context}" TextTrimming="CharacterEllipsis" />
                       </Grid>
                   </DataTemplate>
                   """;
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private void CoverageGapListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CoverageGapListView.SelectedItem is CoverageGapEntry gap)
        {
            SubTabView.SelectedIndex = 0; // Switch to Memory Map tab
            HexViewer.NavigateToOffset(gap.RawFileOffset);
        }
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes:N0} B"
        };
    }

    #endregion

    #region Data Browser Tab

    private async void ReconstructButton_Click(object sender, RoutedEventArgs e)
    {
        await RunSemanticReconstructionAsync();
        if (_session.SemanticResult != null)
        {
            await PopulateDataBrowserAsync();
        }
    }

    private async Task RunSemanticReconstructionAsync()
    {
        if (_session.SemanticResult != null) return;
        if (!_session.HasEsmRecords || !_session.HasAccessor) return;

        ReconstructButton.IsEnabled = false;
        ReconstructProgressRing.IsActive = true;
        ReconstructProgressRing.Visibility = Visibility.Visible;
        ReconstructStatusText.Text = Strings.Status_ReconstructingRecords;
        StatusTextBlock.Text = Strings.Status_ReconstructingRecords;

        try
        {
            var result = _session.AnalysisResult!;
            var accessor = _session.Accessor!;
            var fileSize = _session.FileSize;

            _session.SemanticResult = await Task.Run(() =>
            {
                var reconstructor = new SemanticReconstructor(
                    result.EsmRecords!,
                    result.FormIdMap,
                    accessor,
                    fileSize,
                    result.MinidumpInfo);
                return reconstructor.ReconstructAll();
            });

            StatusTextBlock.Text = Strings.Status_ReconstructedRecords(_session.SemanticResult.TotalRecordsReconstructed);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(Strings.Dialog_ReconstructionFailed_Title,
                $"{ex.GetType().Name}: {ex.Message}", true);
        }
        finally
        {
            ReconstructProgressRing.IsActive = false;
            ReconstructProgressRing.Visibility = Visibility.Collapsed;
            ReconstructButton.IsEnabled = true;
        }
    }

    private async Task PopulateDataBrowserAsync()
    {
        if (_session.SemanticResult == null) return;

        ReconstructProgressRing.IsActive = true;
        ReconstructProgressRing.Visibility = Visibility.Visible;
        ReconstructStatusText.Text = Strings.Status_BuildingDataBrowserTree;
        StatusTextBlock.Text = Strings.Status_BuildingDataBrowserTree;

        try
        {
            var semanticResult = _session.SemanticResult;
            var lookup = _session.AnalysisResult?.FormIdMap;

            // Progress callback for status updates
            var progress = new Progress<string>(status =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    ReconstructStatusText.Text = status;
                    StatusTextBlock.Text = status;
                }));

            // Build tree on background thread (fast - just category nodes)
            var tree = await Task.Run(() =>
            {
                ((IProgress<string>)progress).Report(Strings.Status_BuildingCategoryTree);
                var builtTree = EsmBrowserTreeBuilder.BuildTree(semanticResult, lookup);

                ((IProgress<string>)progress).Report(Strings.Status_SortingRecords);
                // Apply default sort (By Name) after building
                EsmBrowserTreeBuilder.SortRecordChildren(builtTree, EsmBrowserTreeBuilder.RecordSortMode.Name);

                return builtTree;
            });

            _esmBrowserTree = tree;
            _flatListBuilt = false;

            StatusTextBlock.Text = Strings.Status_BuildingTreeView;

            // Add category nodes to tree with chevrons (must be on UI thread)
            EsmTreeView.RootNodes.Clear();
            foreach (var node in _esmBrowserTree)
            {
                // Always show chevron for categories (they always have children)
                var treeNode = new TreeViewNode { Content = node, HasUnrealizedChildren = true };
                EsmTreeView.RootNodes.Add(treeNode);
            }

            DataBrowserPlaceholder.Visibility = Visibility.Collapsed;
            DataBrowserContent.Visibility = Visibility.Visible;
            StatusTextBlock.Text = "Data browser ready. Search will index records on first use.";
        }
        finally
        {
            ReconstructProgressRing.IsActive = false;
            ReconstructProgressRing.Visibility = Visibility.Collapsed;
            ReconstructStatusText.Text = "";
            StatusTextBlock.Text = "";
        }
    }

    private void EsmTreeView_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not EsmBrowserNode browserNode) return;
        if (args.Node.Children.Count > 0) return; // Already has TreeViewNode children

        // Load data children if needed
        if (browserNode.NodeType == "Category" && browserNode.Children.Count == 0)
        {
            EsmBrowserTreeBuilder.LoadCategoryChildren(browserNode);
        }
        else if (browserNode.NodeType == "RecordType" && browserNode.Children.Count == 0)
        {
            EsmBrowserTreeBuilder.LoadRecordTypeChildren(
                browserNode,
                _session.AnalysisResult?.FormIdMap,
                _session.SemanticResult?.FormIdToDisplayName);
        }

        // Add child TreeViewNodes
        foreach (var child in browserNode.Children)
        {
            var childNode = new TreeViewNode { Content = child, HasUnrealizedChildren = child.HasUnrealizedChildren };
            args.Node.Children.Add(childNode);
        }
    }

    private void EsmTreeView_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode treeNode) return;
        if (treeNode.Content is not EsmBrowserNode browserNode) return;

        // For Category/RecordType nodes, expand on click (not just chevron)
        if (browserNode.NodeType is "Category" or "RecordType")
        {
            if (!treeNode.IsExpanded)
            {
                // Load children if not yet loaded
                if (treeNode.Children.Count == 0)
                {
                    if (browserNode.NodeType == "Category" && browserNode.Children.Count == 0)
                        EsmBrowserTreeBuilder.LoadCategoryChildren(browserNode);
                    else if (browserNode.NodeType == "RecordType" && browserNode.Children.Count == 0)
                        EsmBrowserTreeBuilder.LoadRecordTypeChildren(
                            browserNode,
                            _session.AnalysisResult?.FormIdMap,
                            _session.SemanticResult?.FormIdToDisplayName);

                    foreach (var child in browserNode.Children)
                    {
                        var childNode = new TreeViewNode
                        {
                            Content = child,
                            HasUnrealizedChildren = child.HasUnrealizedChildren ||
                                                    child.NodeType is "Category" or "RecordType"
                        };
                        treeNode.Children.Add(childNode);
                    }
                }

                treeNode.IsExpanded = true;
            }
            else
            {
                treeNode.IsExpanded = false;
            }
        }

        SelectBrowserNode(browserNode);
    }

    private void SelectBrowserNode(EsmBrowserNode browserNode)
    {
        _selectedBrowserNode = browserNode;

        if (browserNode.NodeType == "Record")
        {
            SelectedRecordTitle.Text = browserNode.DisplayName;
            BuildPropertyPanel(browserNode.Properties);

            if (browserNode.FileOffset.HasValue && browserNode.FileOffset.Value > 0)
            {
                GoToOffsetButton.Visibility = Visibility.Visible;
            }
            else
            {
                GoToOffsetButton.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            SelectedRecordTitle.Text = browserNode.DisplayName;
            PropertyPanel.Children.Clear();
            GoToOffsetButton.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildPropertyPanel(List<EsmPropertyEntry> properties)
    {
        PropertyPanel.Children.Clear();

        // Use a single Grid so all rows share the same column widths (dynamic table alignment)
        // 5 columns: icon, name, value col1 (FullName/EditorID), value col2 (EditorID), value col3 (FormID)
        var mainGrid = new Grid();
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // name
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // value col1
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // value col2
        mainGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // value col3

        var currentRow = 0;
        var propertyRowIndex = 0; // For alternating row colors (excludes category headers)
        string? lastCategory = null;

        // Use theme-aware foreground with low opacity for subtle alternating rows
        // This adapts to both light and dark modes automatically
        var foregroundBrush = (Microsoft.UI.Xaml.Media.SolidColorBrush)
            Application.Current.Resources["TextFillColorPrimaryBrush"];
        var altRowBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(foregroundBrush.Color) { Opacity = 0.05 };

        foreach (var prop in properties)
        {
            // Add category header when category changes
            if (prop.Category != null && prop.Category != lastCategory)
            {
                lastCategory = prop.Category;
                propertyRowIndex = 0; // Reset alternating for each category
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Add more visible background for category headers (distinct from row stripes)
                var categoryBgBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(foregroundBrush.Color) { Opacity = 0.12 };
                var categoryBg = new Border { Background = categoryBgBrush };
                Grid.SetRow(categoryBg, currentRow);
                Grid.SetColumnSpan(categoryBg, 5); // Spans all 5 columns
                mainGrid.Children.Add(categoryBg);

                var categoryHeader = new TextBlock
                {
                    Text = prop.Category,
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground =
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    Margin = new Thickness(8, 5, 0, 7) // Vertically centered (up 1px)
                };
                Grid.SetRow(categoryHeader, currentRow);
                Grid.SetColumnSpan(categoryHeader, 5); // Spans all 5 columns
                mainGrid.Children.Add(categoryHeader);
                currentRow++;
            }

            if (prop.IsExpandable && prop.SubItems?.Count > 0)
            {
                // Expandable entry - header row
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Add alternating row background
                if (propertyRowIndex % 2 == 1)
                {
                    var bgBorder = new Border { Background = altRowBrush };
                    Grid.SetRow(bgBorder, currentRow);
                    Grid.SetColumnSpan(bgBorder, 5);
                    mainGrid.Children.Add(bgBorder);
                }

                var expandIcon = new TextBlock
                {
                    Text = "▶",
                    FontSize = 10,
                    Width = 18,
                    Padding = new Thickness(4, 3, 0, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground =
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
                Grid.SetRow(expandIcon, currentRow);
                Grid.SetColumn(expandIcon, 0);
                mainGrid.Children.Add(expandIcon);

                var nameText = new TextBlock
                {
                    Text = prop.Name,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Padding = new Thickness(0, 3, 16, 2),
                    IsTextSelectionEnabled = true
                };
                Grid.SetRow(nameText, currentRow);
                Grid.SetColumn(nameText, 1);
                mainGrid.Children.Add(nameText);

                var countText = new TextBlock
                {
                    Text = prop.Value,
                    FontSize = 12,
                    Padding = new Thickness(0, 3, 4, 2),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    Foreground =
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
                Grid.SetRow(countText, currentRow);
                Grid.SetColumn(countText, 2);
                Grid.SetColumnSpan(countText, 3); // Spans value columns
                mainGrid.Children.Add(countText);

                var headerRow = currentRow;
                currentRow++;

                // Create a separate grid for sub-items (isolates column widths from mainGrid)
                var subItemsGrid = new Grid { Visibility = Visibility.Collapsed };
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col1: EditorID
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col2: FullName
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Col3: FormID
                subItemsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Col4: Qty

                var subRow = 0;
                foreach (var sub in prop.SubItems)
                {
                    subItemsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    // 4-column sub-item (Inventory, Factions)
                    if (sub.Col1 != null || sub.Col2 != null || sub.Col3 != null || sub.Col4 != null)
                    {
                        var col1Text = new TextBlock
                        {
                            Text = sub.Col1 ?? "",
                            FontSize = 11,
                            Padding = new Thickness(0, 1, 12, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(col1Text, subRow);
                        Grid.SetColumn(col1Text, 0);
                        subItemsGrid.Children.Add(col1Text);

                        var col2Text = new TextBlock
                        {
                            Text = sub.Col2 ?? "",
                            FontSize = 11,
                            Padding = new Thickness(0, 1, 12, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(col2Text, subRow);
                        Grid.SetColumn(col2Text, 1);
                        subItemsGrid.Children.Add(col2Text);

                        var col3Text = new TextBlock
                        {
                            Text = sub.Col3 ?? "",
                            FontSize = 11,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            Padding = new Thickness(0, 1, 12, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(col3Text, subRow);
                        Grid.SetColumn(col3Text, 2);
                        subItemsGrid.Children.Add(col3Text);

                        var col4Text = new TextBlock
                        {
                            Text = sub.Col4 ?? "",
                            FontSize = 11,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            Padding = new Thickness(0, 1, 4, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(col4Text, subRow);
                        Grid.SetColumn(col4Text, 3);
                        subItemsGrid.Children.Add(col4Text);
                    }
                    else if (string.IsNullOrEmpty(sub.Name))
                    {
                        // Value-only sub-item (FaceGen hex blocks)
                        var valText = new TextBlock
                        {
                            Text = sub.Value,
                            FontSize = 11,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                            Padding = new Thickness(0, 1, 0, 1)
                        };
                        Grid.SetRow(valText, subRow);
                        Grid.SetColumnSpan(valText, 4);
                        subItemsGrid.Children.Add(valText);
                    }
                    else
                    {
                        // Name + Value sub-item (Skills)
                        var subNameText = new TextBlock
                        {
                            Text = sub.Name,
                            FontSize = 11,
                            Padding = new Thickness(0, 1, 16, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(subNameText, subRow);
                        Grid.SetColumnSpan(subNameText, 2);
                        subItemsGrid.Children.Add(subNameText);

                        var valText = new TextBlock
                        {
                            Text = sub.Value,
                            FontSize = 11,
                            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                            Padding = new Thickness(0, 1, 4, 1),
                            IsTextSelectionEnabled = true
                        };
                        Grid.SetRow(valText, subRow);
                        Grid.SetColumn(valText, 2);
                        Grid.SetColumnSpan(valText, 2);
                        subItemsGrid.Children.Add(valText);
                    }
                    subRow++;
                }

                // Add sub-items grid as a single row in mainGrid (spans all columns, isolated)
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var subItemsContainer = new Border
                {
                    Child = subItemsGrid,
                    Margin = new Thickness(18, 0, 0, 0) // Indent
                };
                Grid.SetRow(subItemsContainer, currentRow);
                Grid.SetColumn(subItemsContainer, 1);
                Grid.SetColumnSpan(subItemsContainer, 4);
                mainGrid.Children.Add(subItemsContainer);
                currentRow++;

                // Click handler - make header row clickable
                expandIcon.PointerPressed += (_, _) => ToggleSubItems();
                nameText.PointerPressed += (_, _) => ToggleSubItems();
                countText.PointerPressed += (_, _) => ToggleSubItems();

                void ToggleSubItems()
                {
                    var isCollapsed = subItemsGrid.Visibility == Visibility.Collapsed;
                    subItemsGrid.Visibility = isCollapsed ? Visibility.Visible : Visibility.Collapsed;
                    expandIcon.Text = isCollapsed ? "▼" : "▶";
                }

                propertyRowIndex++;
            }
            else
            {
                // Normal property row
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Add alternating row background
                if (propertyRowIndex % 2 == 1)
                {
                    var bgBorder = new Border { Background = altRowBrush };
                    Grid.SetRow(bgBorder, currentRow);
                    Grid.SetColumnSpan(bgBorder, 5);
                    mainGrid.Children.Add(bgBorder);
                }

                // Empty spacer for icon column alignment
                var spacer = new TextBlock { Width = 18, Padding = new Thickness(4, 3, 0, 2) };
                Grid.SetRow(spacer, currentRow);
                Grid.SetColumn(spacer, 0);
                mainGrid.Children.Add(spacer);

                var nameText = new TextBlock
                {
                    Text = prop.Name,
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Padding = new Thickness(0, 3, 16, 2),
                    IsTextSelectionEnabled = true
                };
                Grid.SetRow(nameText, currentRow);
                Grid.SetColumn(nameText, 1);
                mainGrid.Children.Add(nameText);

                // Single value column (spans all value columns)
                var valueText = new TextBlock
                {
                    Text = prop.Value,
                    FontSize = 12,
                    Padding = new Thickness(0, 3, 4, 2),
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                };
                Grid.SetRow(valueText, currentRow);
                Grid.SetColumn(valueText, 2);
                Grid.SetColumnSpan(valueText, 3);
                mainGrid.Children.Add(valueText);

                propertyRowIndex++;
                currentRow++;
            }
        }

        PropertyPanel.Children.Add(mainGrid);
    }

    private void GoToRecordOffset_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBrowserNode?.FileOffset is > 0)
        {
            SubTabView.SelectedIndex = 0; // Switch to Memory Map
            HexViewer.NavigateToOffset(_selectedBrowserNode.FileOffset.Value);
        }
    }

    private string _currentSearchQuery = "";

    private async void EsmSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = EsmSearchBox.Text?.Trim() ?? "";

        if (_esmBrowserTree == null || _esmBrowserTree.Count == 0)
        {
            return;
        }

        _currentSearchQuery = query;

        // Cancel any pending search
        _searchDebounceToken?.Cancel();
        _searchDebounceToken = new CancellationTokenSource();
        var token = _searchDebounceToken.Token;

        if (string.IsNullOrEmpty(query))
        {
            // Restore full tree view immediately (no debounce for clearing)
            RebuildTreeViewFromSource();
            StatusTextBlock.Text = "";
            return;
        }

        // Debounce: wait 250ms after user stops typing before searching
        try
        {
            await Task.Delay(250, token);
        }
        catch (TaskCanceledException)
        {
            return; // User typed more, abort this search
        }

        // Ensure all children are loaded before filtering (lazy loading)
        if (!_flatListBuilt)
        {
            StatusTextBlock.Text = "Building search index...";
            var lookup = _session.AnalysisResult?.FormIdMap;
            var displayNameLookup = _session.SemanticResult?.FormIdToDisplayName;
            var tree = _esmBrowserTree;
            await Task.Run(() => EnsureAllChildrenLoaded(tree, lookup, displayNameLookup));
            _flatListBuilt = true;
        }

        // Filter tree and rebuild with only matching records
        var matchCount = FilterAndRebuildTreeView(query);
        StatusTextBlock.Text = matchCount > 0
            ? $"Found {matchCount:N0} matching records"
            : "No matches found";
    }

    private static void EnsureAllChildrenLoaded(
        ObservableCollection<EsmBrowserNode> tree,
        Dictionary<uint, string>? lookup,
        Dictionary<uint, string>? displayNameLookup)
    {
        foreach (var categoryNode in tree)
        {
            if (categoryNode.HasUnrealizedChildren && categoryNode.Children.Count == 0)
            {
                EsmBrowserTreeBuilder.LoadCategoryChildren(categoryNode);
            }

            foreach (var typeNode in categoryNode.Children)
            {
                if (typeNode.HasUnrealizedChildren && typeNode.Children.Count == 0)
                {
                    EsmBrowserTreeBuilder.LoadRecordTypeChildren(typeNode, lookup, displayNameLookup);
                }
            }
        }
    }

    private void RebuildTreeViewFromSource()
    {
        if (_esmBrowserTree == null) return;

        EsmTreeView.RootNodes.Clear();
        foreach (var node in _esmBrowserTree)
        {
            var treeNode = new TreeViewNode { Content = node, HasUnrealizedChildren = node.HasUnrealizedChildren };
            EsmTreeView.RootNodes.Add(treeNode);
        }
    }

    private int FilterAndRebuildTreeView(string query, int maxResults = 200)
    {
        if (_esmBrowserTree == null) return 0;

        var totalMatches = 0;
        EsmTreeView.RootNodes.Clear();

        foreach (var categoryNode in _esmBrowserTree)
        {
            if (totalMatches >= maxResults) break; // Stop if limit reached

            var filteredCategoryNode = FilterCategoryNode(categoryNode, query, ref totalMatches, maxResults);
            if (filteredCategoryNode != null)
            {
                EsmTreeView.RootNodes.Add(filteredCategoryNode);
            }
        }

        return totalMatches;
    }

    private static TreeViewNode? FilterCategoryNode(
        EsmBrowserNode category, string query, ref int totalMatches, int maxResults)
    {
        var matchingTypeNodes = new List<TreeViewNode>();

        foreach (var typeNode in category.Children)
        {
            if (totalMatches >= maxResults) break; // Stop if limit reached

            // Filter records within this type (preserves existing sort order)
            // Only take up to remaining limit to avoid processing extra records
            var matchingRecords = typeNode.Children
                .Where(r => MatchesSearchQuery(r, query))
                .Take(maxResults - totalMatches)
                .ToList();

            if (matchingRecords.Count > 0)
            {
                totalMatches += matchingRecords.Count;

                // Create type node with only matching children
                var typeTreeNode = new TreeViewNode
                {
                    Content = typeNode,
                    HasUnrealizedChildren = false,
                    IsExpanded = true // Auto-expand to show matches
                };

                foreach (var record in matchingRecords)
                {
                    typeTreeNode.Children.Add(new TreeViewNode { Content = record });
                }

                matchingTypeNodes.Add(typeTreeNode);
            }
        }

        if (matchingTypeNodes.Count > 0)
        {
            // Create category node with only types that have matches
            var categoryTreeNode = new TreeViewNode
            {
                Content = category,
                HasUnrealizedChildren = false,
                IsExpanded = true // Auto-expand to show matches
            };

            foreach (var typeNode in matchingTypeNodes)
            {
                categoryTreeNode.Children.Add(typeNode);
            }

            return categoryTreeNode;
        }

        return null;
    }

    private static bool MatchesSearchQuery(EsmBrowserNode node, string query)
    {
        return (node.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
               (node.EditorId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
               (node.FormIdHex?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }

    private void EsmSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_esmBrowserTree == null || _esmBrowserTree.Count == 0) return;

        var mode = EsmSortComboBox.SelectedIndex switch
        {
            1 => EsmBrowserTreeBuilder.RecordSortMode.EditorId,
            2 => EsmBrowserTreeBuilder.RecordSortMode.FormId,
            _ => EsmBrowserTreeBuilder.RecordSortMode.Name
        };

        EsmBrowserTreeBuilder.SortRecordChildren(_esmBrowserTree, mode);

        // Rebuild tree view with new sort order, respecting any active filter
        if (!string.IsNullOrEmpty(_currentSearchQuery))
        {
            // Re-apply filter with new sort order
            FilterAndRebuildTreeView(_currentSearchQuery);
        }
        else
        {
            // No filter - rebuild full tree
            RebuildTreeViewFromSource();
        }
    }

    #endregion

    #region Reports Tab

    private async void GenerateReportsButton_Click(object sender, RoutedEventArgs e)
    {
        GenerateReportsButton.IsEnabled = false;
        ReportsProgressRing.IsActive = true;
        ReportsProgressRing.Visibility = Visibility.Visible;

        try
        {
            await RunSemanticReconstructionAsync();
            if (_session.SemanticResult == null) return;

            StatusTextBlock.Text = "Generating reports...";

            var semanticResult = _session.SemanticResult;
            var formIdMap = _session.AnalysisResult?.FormIdMap;

            var splitReports = await Task.Run(() =>
                GeckReportGenerator.GenerateSplit(semanticResult, formIdMap));

            _reportEntries.Clear();
            foreach (var (filename, content) in splitReports.OrderBy(kvp => kvp.Key))
            {
                _reportEntries.Add(new ReportEntry
                {
                    FileName = filename,
                    Content = content,
                    ReportType = filename.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "CSV" : "TXT"
                });
            }

            ExportAllReportsButton.IsEnabled = _reportEntries.Count > 0;
            StatusTextBlock.Text = $"Generated {_reportEntries.Count} reports.";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Report Generation Failed",
                $"{ex.GetType().Name}: {ex.Message}", true);
        }
        finally
        {
            GenerateReportsButton.IsEnabled = true;
            ReportsProgressRing.IsActive = false;
            ReportsProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    private static void CollectReconstructedFormIds(SemanticReconstructionResult result, HashSet<uint> formIds)
    {
        // Collect FormIDs from all reconstructed record lists using reflection
        foreach (var prop in result.GetType().GetProperties())
        {
            if (!prop.CanRead) continue;
            if (prop.GetValue(result) is not System.Collections.IList list) continue;

            foreach (var item in list)
            {
                var formIdProp = item.GetType().GetProperty("FormId");
                if (formIdProp?.GetValue(item) is uint formId && formId != 0)
                {
                    formIds.Add(formId);
                }
            }
        }
    }

    private void ReportListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReportListView.SelectedItem is ReportEntry report)
        {
            SetReportContent(report.Content);
            ExportSelectedReportButton.IsEnabled = true;
            _reportSearchMatches = [];
            _reportSearchIndex = 0;
            ReportSearchStatus.Text = "";
        }
        else
        {
            SetReportContent("");
            ExportSelectedReportButton.IsEnabled = false;
        }
    }

    /// <summary>
    ///     Sets up virtualized display of report content. Only the visible
    ///     viewport of lines is loaded into the TextBox for memory efficiency.
    /// </summary>
    private void SetReportContent(string content)
    {
        _reportFullContent = content.Replace("\r\n", "\n").Replace("\r", "\n");
        _reportLines = _reportFullContent.Length > 0
            ? _reportFullContent.Split('\n')
            : [];

        // Build cumulative character offset per line for search navigation
        _reportLineOffsets = new int[_reportLines.Length];
        var offset = 0;
        for (var i = 0; i < _reportLines.Length; i++)
        {
            _reportLineOffsets[i] = offset;
            offset += _reportLines[i].Length + 1; // +1 for \n
        }

        // Configure scrollbar
        var maxTop = Math.Max(0, _reportLines.Length - _reportViewportLineCount);
        ReportViewerScrollBar.Maximum = maxTop;
        ReportViewerScrollBar.ViewportSize = _reportViewportLineCount;
        ReportViewerScrollBar.LargeChange = Math.Max(1, _reportViewportLineCount - 2);
        ReportViewerScrollBar.Value = 0;

        UpdateReportViewport();
    }

    /// <summary>
    ///     Updates the TextBox with only the lines visible in the current viewport.
    /// </summary>
    private void UpdateReportViewport()
    {
        if (_reportLines.Length == 0)
        {
            ReportPreviewTextBox.Text = "";
            return;
        }

        var topLine = Math.Max(0, (int)ReportViewerScrollBar.Value);
        var endLine = Math.Min(topLine + _reportViewportLineCount, _reportLines.Length);
        ReportPreviewTextBox.Text = string.Join("\n", _reportLines[topLine..endLine]);
    }

    private void ReportPreviewTextBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0) return;

        const double lineHeight = 15.0; // Approximate for Consolas 11pt
        _reportViewportLineCount = Math.Max(10, (int)(e.NewSize.Height / lineHeight) - 2);

        // Update scrollbar to reflect new viewport size
        var maxTop = Math.Max(0, _reportLines.Length - _reportViewportLineCount);
        ReportViewerScrollBar.Maximum = maxTop;
        ReportViewerScrollBar.ViewportSize = _reportViewportLineCount;
        ReportViewerScrollBar.LargeChange = Math.Max(1, _reportViewportLineCount - 2);

        if (ReportViewerScrollBar.Value > maxTop)
        {
            ReportViewerScrollBar.Value = maxTop;
        }

        UpdateReportViewport();
    }

    private void ReportViewerScrollBar_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateReportViewport();
    }

    private void ReportPreviewTextBox_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ReportPreviewTextBox).Properties.MouseWheelDelta;
        if (delta == 0) return;

        var linesToScroll = delta > 0 ? -3 : 3;
        ReportViewerScrollBar.Value = Math.Clamp(
            ReportViewerScrollBar.Value + linesToScroll,
            0, ReportViewerScrollBar.Maximum);
        e.Handled = true;
    }

    /// <summary>
    ///     Binary search to find which line a character offset falls on.
    /// </summary>
    private int FindLineForCharOffset(int charOffset)
    {
        var lo = 0;
        var hi = _reportLineOffsets.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_reportLineOffsets[mid] <= charOffset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return Math.Max(0, lo - 1);
    }

    private void ReportSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            PerformReportSearch();
            e.Handled = true;
        }
    }

    private void ReportSearchNext_Click(object sender, RoutedEventArgs e)
    {
        if (_reportSearchMatches.Count == 0)
        {
            PerformReportSearch();
            return;
        }

        if (_reportSearchMatches.Count > 0)
        {
            _reportSearchIndex = (_reportSearchIndex + 1) % _reportSearchMatches.Count;
            NavigateToReportMatch();
        }
    }

    private void ReportSearchPrev_Click(object sender, RoutedEventArgs e)
    {
        if (_reportSearchMatches.Count == 0)
        {
            PerformReportSearch();
            return;
        }

        if (_reportSearchMatches.Count > 0)
        {
            _reportSearchIndex = (_reportSearchIndex - 1 + _reportSearchMatches.Count) % _reportSearchMatches.Count;
            NavigateToReportMatch();
        }
    }

    private void PerformReportSearch()
    {
        var query = ReportSearchBox.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(_reportFullContent))
        {
            _reportSearchMatches = [];
            _reportSearchIndex = 0;
            ReportSearchStatus.Text = "";
            return;
        }

        _reportSearchQuery = query;
        _reportSearchMatches = [];

        // Search the full content, not just the visible viewport
        var idx = 0;
        while (true)
        {
            var found = _reportFullContent.IndexOf(query, idx, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            _reportSearchMatches.Add(found);
            idx = found + 1;
        }

        if (_reportSearchMatches.Count == 0)
        {
            ReportSearchStatus.Text = "No matches";
            _reportSearchIndex = 0;
            return;
        }

        _reportSearchIndex = 0;
        NavigateToReportMatch();
    }

    private void NavigateToReportMatch()
    {
        if (_reportSearchMatches.Count == 0) return;

        var pos = _reportSearchMatches[_reportSearchIndex];
        var matchLine = FindLineForCharOffset(pos);

        // Scroll viewport to show the match line (one-third from top for context)
        var targetTop = Math.Max(0, matchLine - _reportViewportLineCount / 3);
        targetTop = Math.Min(targetTop, (int)ReportViewerScrollBar.Maximum);
        ReportViewerScrollBar.Value = targetTop;

        // Calculate match position within the viewport text and select it
        var topLine = (int)ReportViewerScrollBar.Value;
        if (topLine < _reportLineOffsets.Length)
        {
            var viewportStartOffset = _reportLineOffsets[topLine];
            var posInViewport = pos - viewportStartOffset;
            if (posInViewport >= 0 && posInViewport + _reportSearchQuery.Length <= ReportPreviewTextBox.Text.Length)
            {
                ReportPreviewTextBox.Select(posInViewport, _reportSearchQuery.Length);
            }
        }

        ReportSearchStatus.Text = $"{_reportSearchIndex + 1} of {_reportSearchMatches.Count}";
    }

    private async void ExportSelectedReport_Click(object sender, RoutedEventArgs e)
    {
        if (ReportListView.SelectedItem is not ReportEntry report) return;

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("Text file", [".txt", ".csv"]);
        picker.SuggestedFileName = report.FileName;
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file != null)
        {
            await File.WriteAllTextAsync(file.Path, report.Content);
            StatusTextBlock.Text = $"Saved: {file.Path}";
        }
    }

    private async void ExportAllReports_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker,
            WindowNative.GetWindowHandle(App.Current.MainWindow));

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        var count = 0;
        foreach (var report in _reportEntries)
        {
            var filePath = Path.Combine(folder.Path, report.FileName);
            await File.WriteAllTextAsync(filePath, report.Content);
            count++;
        }

        StatusTextBlock.Text = $"Exported {count} reports to {folder.Path}";
    }

    #endregion

    #region Sorting

    private void SortByOffset_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(CarvedFilesSorter.SortColumn.Offset);
    }

    private void SortByLength_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(CarvedFilesSorter.SortColumn.Length);
    }

    private void SortByType_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(CarvedFilesSorter.SortColumn.Type);
    }

    private void SortByFilename_Click(object sender, RoutedEventArgs e)
    {
        ApplySort(CarvedFilesSorter.SortColumn.Filename);
    }

    private void ApplySort(CarvedFilesSorter.SortColumn col)
    {
        _sorter.CycleSortState(col);
        UpdateSortIcons();
        RefreshSortedList();
    }

    private void UpdateSortIcons()
    {
        OffsetSortIcon.Visibility = LengthSortIcon.Visibility =
            TypeSortIcon.Visibility = FilenameSortIcon.Visibility = Visibility.Collapsed;
        var icon = _sorter.CurrentColumn switch
        {
            CarvedFilesSorter.SortColumn.Offset => OffsetSortIcon,
            CarvedFilesSorter.SortColumn.Length => LengthSortIcon,
            CarvedFilesSorter.SortColumn.Type => TypeSortIcon,
            CarvedFilesSorter.SortColumn.Filename => FilenameSortIcon,
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
        var selectedItem = ResultsListView.SelectedItem as CarvedFileEntry;
        var sorted = _sorter.Sort(_allCarvedFiles);
        _carvedFiles.Clear();
        foreach (var f in sorted) _carvedFiles.Add(f);
        if (selectedItem != null && _carvedFiles.Contains(selectedItem))
        {
            ResultsListView.SelectedItem = selectedItem;
            ResultsListView.ScrollIntoView(selectedItem, ScrollIntoViewAlignment.Leading);
        }
    }

    #endregion

    #region Context Menu

    private void ResultsListView_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: CarvedFileEntry entry })
        {
            _contextMenuTarget = entry;
            CopyFilenameMenuItem.IsEnabled = !string.IsNullOrEmpty(entry.FileName);
        }
        else
        {
            _contextMenuTarget = ResultsListView.SelectedItem as CarvedFileEntry;
            CopyFilenameMenuItem.IsEnabled =
                _contextMenuTarget != null && !string.IsNullOrEmpty(_contextMenuTarget.FileName);
        }
    }

    private void GoToStart_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target != null) HexViewer.NavigateToOffset(target.Offset);
    }

    private void GoToEnd_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target != null)
        {
            var endOffset = target.Offset + target.Length - 16;
            if (endOffset > 0) HexViewer.NavigateToOffset(endOffset);
        }
    }

    private void CopyOffset_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target != null) ClipboardHelper.CopyText($"0x{target.Offset:X8}");
    }

    private void CopyFilename_Click(object sender, RoutedEventArgs e)
    {
        var target = _contextMenuTarget ?? ResultsListView.SelectedItem as CarvedFileEntry;
        if (target != null && !string.IsNullOrEmpty(target.FileName)) ClipboardHelper.CopyText(target.FileName);
    }

    #endregion
}