using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
/// Main code-behind for the SingleFileTab UserControl.
/// This partial class is split across multiple files:
/// - SingleFileTab.xaml.cs (this file): Fields, constructor, main event handlers
/// - SingleFileTab.Analysis.cs: Analysis methods and orchestration
/// - SingleFileTab.Display.cs: Display/UI update methods
/// - SingleFileTab.FileOperations.cs: File save/export/load operations
/// - SingleFileTab.TreeBuilder.cs: ESM browser tree building and filtering
/// - SingleFileTab.Helpers.cs: Helper/utility methods
/// </summary>
public sealed partial class SingleFileTab : UserControl, IDisposable, IHasSettingsDrawer
{
    #region Fields

    private readonly List<CarvedFileEntry> _allCarvedFiles = [];
    private readonly ObservableCollection<CarvedFileEntry> _carvedFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private readonly ObservableCollection<ReportEntry> _reportEntries = [];
    private readonly AnalysisSessionState _session = new();
    private readonly CarvedFilesSorter _sorter = new();

    private List<CoverageGapEntry> _allCoverageGapEntries = [];
    private AnalysisResult? _analysisResult;
    private CarvedFileEntry? _contextMenuTarget;
    private bool _coverageGapSortAscending = true;
    private CoverageGapSortColumn _coverageGapSortColumn = CoverageGapSortColumn.Index;
    private bool _coveragePopulated;
    private bool _dependencyCheckDone;
    private bool _recordBreakdownPopulated;
    private ObservableCollection<EsmBrowserNode>? _esmBrowserTree;
    private bool _flatListBuilt;
    private CancellationTokenSource? _searchDebounceToken;
    private string? _lastInputPath;
    private string _reportFullContent = "";
    private int[] _reportLineOffsets = [];
    private string[] _reportLines = [];
    private int _reportSearchIndex;
    private List<int> _reportSearchMatches = [];
    private string _reportSearchQuery = "";
    private int _reportViewportLineCount = 50;
    private double _measuredLineHeight;
    private EsmBrowserNode? _selectedBrowserNode;
    private Action<int, string>? _reconstructionProgressHandler;
    private Task<RecordCollection>? _semanticReconstructionTask;
    private string _currentSearchQuery = "";

    #endregion

    #region Properties

#pragma warning disable CA1822, S2325
    private StatusTextHelper StatusTextBlock => new();
#pragma warning restore CA1822, S2325

    #endregion

    #region Constructor

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

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _searchDebounceToken?.Dispose();
        _session.Dispose();
    }

    #endregion

    #region Lifecycle Events

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

    private void SingleFileTab_Unloaded(object sender, RoutedEventArgs e)
    {
        _session.Dispose();
    }

    #endregion

    #region Main Button Event Handlers

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
            HexViewer.Clear();
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
                AnalysisFileType.EsmFile => await EsmFileAnalyzer.AnalyzeAsync(filePath, progress),
                AnalysisFileType.Minidump => await new MinidumpAnalyzer().AnalyzeAsync(filePath, progress),
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
            SummaryTab.IsEnabled = true;
            UpdateFileInfoCard();
            await HexViewer.LoadDataAsync(filePath, _analysisResult, _session.Accessor!);

            // Enable data browser and reports tabs (depends on ESM records, not coverage)
            DataBrowserTab.IsEnabled = _session.HasEsmRecords;
            DialogueViewerTab.IsEnabled = _session.HasEsmRecords;
            WorldMapTab.IsEnabled = _session.HasEsmRecords;
            ReportsTab.IsEnabled = _session.HasEsmRecords;

            // Start semantic reconstruction eagerly in background so sub-tabs load faster
            if (_session.HasEsmRecords)
            {
                StartSemanticReconstructionInBackground();
            }

            // If already on Data Browser tab, auto-reconstruct
            if (_session.HasEsmRecords && ReferenceEquals(SubTabView.SelectedItem, DataBrowserTab))
            {
                ReconstructButton_Click(sender, e);
            }

            // If already on Reports tab, auto-generate reports
            if (_session.HasEsmRecords && ReferenceEquals(SubTabView.SelectedItem, ReportsTab))
            {
                await GenerateReportsAsync();
            }

            // Run coverage analysis (best-effort, doesn't block other functionality)
            try
            {
                StatusTextBlock.Text = Strings.Status_RunningCoverageAnalysis;
                _session.CoverageResult = await Task.Run(() =>
                    CoverageAnalyzer.Analyze(_session.AnalysisResult!, _session.Accessor!));

                if (_session.CoverageResult.Error == null)
                {
                    CoverageTab.IsEnabled = true;
                    await HexViewer.AddCoverageGapRegionsAsync(_session.CoverageResult);
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

    private async void ReconstructButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.SemanticResult == null)
        {
            ReconstructButton.IsEnabled = false;
            ReconstructProgressBar.Visibility = Visibility.Visible;
            ReconstructProgressBar.IsIndeterminate = false;
            _reconstructionProgressHandler = (percent, phase) =>
            {
                ReconstructProgressBar.Value = percent;
                ReconstructStatusText.Text = phase;
            };
            await EnsureSemanticReconstructionAsync();
            _reconstructionProgressHandler = null;
            ReconstructProgressBar.Visibility = Visibility.Collapsed;
            ReconstructButton.IsEnabled = true;
        }

        if (_session.SemanticResult != null)
        {
            await PopulateDataBrowserAsync();
        }
    }

    #endregion

    #region Tab Selection Events

    private async void SubTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var selected = SubTabView.SelectedItem;

        if (ReferenceEquals(selected, SummaryTab) && !_recordBreakdownPopulated && _session.HasEsmRecords)
        {
            await EnsureSemanticReconstructionAsync();
            PopulateRecordBreakdown();
        }

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

        // Auto-populate Dialogue Viewer when first selected
        if (ReferenceEquals(selected, DialogueViewerTab) &&
            !_dialogueViewerPopulated &&
            _session.HasEsmRecords)
        {
            _ = PopulateDialogueViewerAsync();
        }

        // Auto-populate World Map when first selected
        if (ReferenceEquals(selected, WorldMapTab) &&
            !_worldMapPopulated &&
            _session.HasEsmRecords)
        {
            _ = PopulateWorldMapAsync();
        }

        // Auto-generate reports when first selected
        if (ReferenceEquals(selected, ReportsTab) &&
            _reportEntries.Count == 0 &&
            _session.HasEsmRecords)
        {
            _ = GenerateReportsAsync();
        }
    }

    #endregion

    #region Settings Drawer

    public void ToggleSettingsDrawer() => SettingsDrawerHelper.Toggle(SettingsDrawer);
    public void CloseSettingsDrawer() => SettingsDrawerHelper.Close(SettingsDrawer);

    #endregion

    #region Results ListView Events

    private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsListView.SelectedItem is CarvedFileEntry f) HexViewer.NavigateToOffset(f.Offset);
    }

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

    #endregion

    #region Text Changed Events

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

    #endregion

    #region Results List Sorting

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

    #endregion
}
