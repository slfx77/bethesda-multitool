using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Semantic;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace FalloutXbox360Utils;

/// <summary>
///     Main code-behind for the SingleFileTab UserControl.
///     This partial class is split across multiple files:
///     - SingleFileTab.xaml.cs (this file): Fields, constructor, main event handlers
///     - SingleFileTab.Analysis.cs: Analysis methods and orchestration
///     - SingleFileTab.Display.cs: Display/UI update methods
///     - SingleFileTab.FileOperations.cs: File save/export/load operations
///     - SingleFileTab.TreeBuilder.cs: ESM browser tree building and filtering
///     - SingleFileTab.Helpers.cs: Helper/utility methods
/// </summary>
public sealed partial class SingleFileTab : UserControl, IDisposable, IHasSettingsDrawer
{
    #region Constructor

    public SingleFileTab()
    {
        InitializeComponent();
        ResultsListView.ItemsSource = _carvedFiles;
        ReportListView.ItemsSource = _reportEntries;
        InitializeFileTypeCheckboxes();
        SetupTextBoxContextMenus();
        WorldMapControl.BeforeNavigate += WorldMap_BeforeNavigate;
        KeyDown += SingleFileTab_KeyDown;
        Loaded += SingleFileTab_Loaded;
        Unloaded += SingleFileTab_Unloaded;
    }

    #endregion

    #region Properties

#pragma warning disable CA1822, S2325
    private StatusTextHelper StatusTextBlock => new();
#pragma warning restore CA1822, S2325

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _searchDebounceToken?.Dispose();
        _session.Dispose();
    }

    #endregion

    #region Tab Selection Events

    private async void SubTabView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 || _pipelinePhase != AnalysisPipelinePhase.Idle) return;
        var selected = SubTabView.SelectedItem;

        if (ReferenceEquals(selected, SummaryTab) && !_session.RecordBreakdownPopulated && _session.HasEsmRecords)
        {
            await EnsureSemanticParseAsync();
            PopulateRecordBreakdown();
        }

        if (ReferenceEquals(selected, CoverageTab) && !_session.CoveragePopulated && _session.CoverageResult != null)
        {
            PopulateCoverageTab();
        }

        // Auto-populate Data Browser when first selected
        if (ReferenceEquals(selected, DataBrowserTab) &&
            DataBrowserContent.Visibility == Visibility.Collapsed)
        {
            if (_session.IsSaveFile && _session.SaveData != null)
            {
                await PopulateSaveBrowserAsync();
            }
            else if (_session.HasEsmRecords)
            {
                ParseButton_Click(sender, new RoutedEventArgs());
            }
        }

        // Auto-populate Dialogue Viewer when first selected
        if (ReferenceEquals(selected, DialogueViewerTab) &&
            !_session.DialogueViewerPopulated &&
            _session.HasEsmRecords)
        {
            _ = PopulateDialogueViewerAsync();
        }

        // Auto-populate World Map when first selected (also available for save files)
        if (ReferenceEquals(selected, WorldMapTab) &&
            !_session.WorldMapPopulated &&
            (_session.HasEsmRecords || _session.IsSaveFile))
        {
            _ = PopulateWorldMapAsync();
        }

        // Auto-populate NPC Browser when first selected
        if (ReferenceEquals(selected, NpcBrowserTab) &&
            !_session.NpcBrowserPopulated &&
            _session.HasEsmRecords)
        {
            _ = PopulateNpcBrowserAsync();
        }

        // Auto-generate reports when first selected (ESM or save files)
        if (ReferenceEquals(selected, ReportsTab) &&
            _reportEntries.Count == 0 &&
            (_session.HasEsmRecords || _session.IsSaveFile))
        {
            _ = GenerateReportsAsync();
        }
    }

    #endregion

    #region Fields

    private readonly List<CarvedFileEntry> _allCarvedFiles = [];
    private readonly ObservableCollection<CarvedFileEntry> _carvedFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private readonly ObservableCollection<ReportEntry> _reportEntries = [];
    private readonly AnalysisSessionState _session = new();
    private readonly CarvedFilesSorter _sorter = new();

    private AnalysisResult? _analysisResult;
    private CarvedFileEntry? _contextMenuTarget;
    private bool _coverageGapSortAscending = true;
    private CoverageGapSortColumn _coverageGapSortColumn = CoverageGapSortColumn.Index;
    private bool _dependencyCheckDone;
    private ObservableCollection<EsmBrowserNode>? _esmBrowserTree;
    private bool _flatListBuilt;
    private Dictionary<uint, List<(uint FormId, string? Name)>>? _factionMembersIndex;
    private Dictionary<uint, List<WorldPlacement>>? _placementIndex;
    private IReadOnlyDictionary<uint, RaceRecord>? _raceLookup;
    private FormUsageIndex? _usageIndex;
    private CancellationTokenSource? _searchDebounceToken;
    private string? _lastInputPath;
    private string _reportFullContent = "";
    private int[] _reportLineOffsets = [];
    private string[] _reportLines = [];
    private int _reportSearchIndex;
    private List<int> _reportSearchMatches = [];
    private string _reportSearchQuery = "";
    private int _reportViewportLineCount = 20;
    private double _measuredLineHeight;
    private EsmBrowserNode? _selectedBrowserNode;
    private Task<UnifiedAnalysisResult>? _semanticLoadTask;
    private string _currentSearchQuery = "";
    private AnalysisPipelinePhase _pipelinePhase;

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

    private void SingleFileTab_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var altDown = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (!altDown) return;

        if (e.Key == Windows.System.VirtualKey.Left)
        {
            UnifiedBack_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Right)
        {
            UnifiedForward_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    #endregion

    #region Main Button Event Handlers

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e)
    {
        var filePath = MinidumpPathTextBox.Text;
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            // Save selected tab before disabling — SelectedItem may not be readable while disabled
            var selectedTabForAutoPopulate = SubTabView.SelectedItem;
            SetPipelinePhase(AnalysisPipelinePhase.Scanning);
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

            var fileType = FileTypeDetector.Detect(filePath);
            if (fileType == AnalysisFileType.Unknown)
            {
                await ShowDialogAsync("Analysis Failed", $"Unknown file type: {filePath}");
                return;
            }

            StatusTextBlock.Text = fileType switch
            {
                AnalysisFileType.EsmFile => Strings.Status_StartingEsmAnalysis,
                AnalysisFileType.SaveFile => "Parsing save file...",
                _ => Strings.Status_StartingAnalysis
            };

            var progress = new Progress<AnalysisProgress>(p => DispatcherQueue.TryEnqueue(() =>
            {
                AnalysisProgressBar.IsIndeterminate = false;
                AnalysisProgressBar.Value = fileType == AnalysisFileType.Minidump
                    ? p.PercentComplete * 0.8
                    : p.PercentComplete;
                StatusTextBlock.Text = SingleFileAnalysisHelper.ResolvePhaseText(p, fileType);
            }));

            _analysisResult = await RunFileAnalysisAsync(filePath, fileType, progress);

            // Build _allCarvedFiles but do NOT populate the observable _carvedFiles yet.
            if (fileType != AnalysisFileType.SaveFile)
            {
                _allCarvedFiles.AddRange(SingleFileAnalysisHelper.BuildCarvedFileList(
                    _analysisResult, isEsmFile: fileType == AnalysisFileType.EsmFile));
            }

            _session.Open(filePath, _analysisResult, fileType, openAccessor: fileType == AnalysisFileType.SaveFile);
            UpdateFileInfoCard();

            _session.RuntimeMeshes = _analysisResult.RuntimeMeshes;
            _session.RuntimeTextures = _analysisResult.RuntimeTextures;
            _session.SceneGraphMap = _analysisResult.SceneGraphMap;

            if (_pendingSaveData != null)
            {
                _session.SaveData = _pendingSaveData;
                _session.DecodedForms = _pendingDecodedForms;
                _pendingSaveData = null;
                _pendingDecodedForms = null;
            }

            // Run semantic parse BEFORE loading HexViewer
            if (_session.HasEsmRecords)
            {
                await RunSemanticParsePipelineAsync();
            }

            // Flush the observable file table if not already populated
            if (_carvedFiles.Count == 0 && _allCarvedFiles.Count > 0)
            {
                foreach (var item in _allCarvedFiles)
                {
                    _carvedFiles.Add(item);
                }
            }

            BuildResultsFilterCheckboxes();

            // Load HexViewer AFTER all analysis and parsing is complete
            SetPipelinePhase(AnalysisPipelinePhase.LoadingMap);
            await HexViewer.LoadDataAsync(filePath, _analysisResult, _session.Accessor!);

            await AutoPopulateCurrentTabAsync(selectedTabForAutoPopulate);

            // Run coverage analysis for memory dumps only
            if (!_session.IsEsmFile && !_session.IsSaveFile)
            {
                await RunCoverageAnalysisAsync();
            }

            LoadOrderButton.Visibility = Visibility.Visible;
            StatusTextBlock.Text = SingleFileAnalysisHelper.BuildCompletionStatus(_session, _allCarvedFiles);
        }
        catch (Exception ex)
        {
            await ShowDialogAsync("Analysis Failed", $"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                true);
        }
        finally
        {
            SetPipelinePhase(AnalysisPipelinePhase.Idle);
        }
    }

    private async void OpenMinidumpButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".dmp");
        picker.FileTypeFilter.Add(".esm");
        picker.FileTypeFilter.Add(".esp");
        picker.FileTypeFilter.Add(".fxs");
        picker.FileTypeFilter.Add(".fos");
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

    private async void ParseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsSaveFile && _session.SaveData != null)
        {
            await PopulateSaveBrowserAsync();
            return;
        }

        if (_session.SemanticResult == null)
        {
            await EnsureSemanticParseAsync();
        }

        if (_session.SemanticResult != null)
        {
            await PopulateDataBrowserAsync();
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

    private void SortByOffset_Click(object sender, RoutedEventArgs e) =>
        ApplySort(CarvedFilesSorter.SortColumn.Offset);

    private void SortByLength_Click(object sender, RoutedEventArgs e) =>
        ApplySort(CarvedFilesSorter.SortColumn.Length);

    private void SortByType_Click(object sender, RoutedEventArgs e) =>
        ApplySort(CarvedFilesSorter.SortColumn.Type);

    private void SortByFilename_Click(object sender, RoutedEventArgs e) =>
        ApplySort(CarvedFilesSorter.SortColumn.Filename);

    private void ApplySort(CarvedFilesSorter.SortColumn col)
    {
        _sorter.CycleSortState(col);
        UpdateSortIcons();
        RefreshSortedList();
    }

    #endregion
}
