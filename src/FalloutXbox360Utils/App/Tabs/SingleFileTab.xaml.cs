using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats;
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
            await EnsureSemanticReconstructionAsync();
            PopulateRecordBreakdown();
        }

        if (ReferenceEquals(selected, CoverageTab) && !_session.CoveragePopulated && _session.CoverageResult != null)
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
            !_session.DialogueViewerPopulated &&
            _session.HasEsmRecords)
        {
            _ = PopulateDialogueViewerAsync();
        }

        // Auto-populate World Map when first selected
        if (ReferenceEquals(selected, WorldMapTab) &&
            !_session.WorldMapPopulated &&
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
    private Task<RecordCollection>? _semanticReconstructionTask;
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
                // MinidumpAnalyzer reports 0-100%; remap to 0-80% so reconstruction (80-95%)
                // and coverage (96%) phases don't cause the progress bar to jump backwards.
                // EsmFileAnalyzer already caps at 80% natively.
                AnalysisProgressBar.Value = fileType == AnalysisFileType.Minidump
                    ? p.PercentComplete * 0.8
                    : p.PercentComplete;

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
                    "Geometry Scan" => Strings.Status_ScanningGeometry,
                    "Texture Scan" => Strings.Status_ScanningTextures,
                    "Scene Graph" => Strings.Status_WalkingSceneGraph,
                    "Runtime Assets" => $"Runtime assets detected ({p.FilesFound} total files)",
                    "Complete" or "Analysis Complete" => Strings.Status_AnalysisComplete(p.FilesFound),
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

            // Build _allCarvedFiles but do NOT populate the observable _carvedFiles yet.
            // The file table should only appear once all analysis (including semantic
            // reconstruction) is complete, so the user sees the final list in one shot.
            // For ESM files, CarvedFiles contains memory-map visualization groups ("ESM Record Group"),
            // not user-actionable files — skip them so only individual records appear in the list.
            if (fileType != AnalysisFileType.EsmFile)
            {
                foreach (var entry in _analysisResult.CarvedFiles)
                {
                    _allCarvedFiles.Add(new CarvedFileEntry
                    {
                        Offset = entry.Offset,
                        Length = entry.Length,
                        FileType = entry.FileType,
                        FileName = entry.FileName
                    });
                }
            }

            // Add ESM records to the backing list
            if (_analysisResult.EsmRecords?.MainRecords != null)
            {
                foreach (var esmRecord in _analysisResult.EsmRecords.MainRecords)
                {
                    _allCarvedFiles.Add(new CarvedFileEntry
                    {
                        Offset = esmRecord.Offset,
                        Length = esmRecord.DataSize + 24,
                        FileType = "ESM Record",
                        EsmRecordType = esmRecord.RecordType,
                        FormId = esmRecord.FormId,
                        FileName = _analysisResult.FormIdMap.GetValueOrDefault(esmRecord.FormId),
                        Status = ExtractionStatus.Skipped
                    });
                }

                _allCarvedFiles.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            }

            // Open shared session (needed for accessor before reconstruction)
            _session.Open(filePath, _analysisResult, fileType);
            UpdateFileInfoCard();

            // Store runtime asset data in session for extraction
            _session.RuntimeMeshes = _analysisResult.RuntimeMeshes;
            _session.RuntimeTextures = _analysisResult.RuntimeTextures;
            _session.SceneGraphMap = _analysisResult.SceneGraphMap;

            // Run semantic reconstruction BEFORE loading HexViewer so the memory map
            // includes TESForm struct regions and terrain mesh regions from the start
            if (_session.HasEsmRecords)
            {
                SetPipelinePhase(AnalysisPipelinePhase.Reconstructing);
                StatusTextBlock.Text = _session.IsEsmFile
                    ? Strings.Status_ParsingEsmRecords
                    : Strings.Status_ReconstructingRecords;

                var reconProgress = new Progress<(int percent, string phase)>(p =>
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        // Map reconstruction progress (0-100) into the 80-95 range
                        AnalysisProgressBar.Value = 80 + p.percent * 0.15;
                        StatusTextBlock.Text = p.phase;
                    }));

                _semanticReconstructionTask = Task.Run(() =>
                {
                    var reconstructor = new RecordParser(
                        _analysisResult.EsmRecords!,
                        _analysisResult.FormIdMap,
                        _session.Accessor!,
                        _session.FileSize,
                        _analysisResult.MinidumpInfo);
                    return reconstructor.ReconstructAll(reconProgress);
                });

                try
                {
                    _session.SemanticResult = await _semanticReconstructionTask;
                    if (_session.SemanticResult != null)
                    {
                        _session.Resolver = _session.SemanticResult.CreateResolver();

                        // Add TESForm struct regions to the memory map
                        AddTesFormStructRegions(_analysisResult);

                        // Add runtime terrain mesh regions from enriched LAND records
                        AddRuntimeTerrainMeshRegions(_analysisResult);

                        // Refresh carved files list with the new entries
                        RefreshCarvedFilesList();
                        BuildResultsFilterCheckboxes();
                    }
                }
                catch (Exception ex)
                {
                    await ShowDialogAsync(Strings.Dialog_ReconstructionFailed_Title,
                        $"{ex.GetType().Name}: {ex.Message}", true);
                }
            }

            // Populate the observable file table now that all analysis is complete.
            // For ESM files, RefreshCarvedFilesList() already ran above and rebuilt the list;
            // for non-ESM files (or if reconstruction was skipped/failed), flush here.
            if (_carvedFiles.Count == 0 && _allCarvedFiles.Count > 0)
            {
                foreach (var item in _allCarvedFiles)
                {
                    _carvedFiles.Add(item);
                }
            }

            // Build type filter checkboxes (idempotent - safe to call even if ESM path already called it)
            BuildResultsFilterCheckboxes();

            // Load HexViewer AFTER all analysis and reconstruction is complete
            // so the memory map renders with all regions from the start
            SetPipelinePhase(AnalysisPipelinePhase.LoadingMap);
            await HexViewer.LoadDataAsync(filePath, _analysisResult, _session.Accessor!);

            // Auto-populate whichever tab is currently selected
            await AutoPopulateCurrentTabAsync(selectedTabForAutoPopulate);

            // Run coverage analysis for memory dumps only (not meaningful for ESM files)
            if (!_session.IsEsmFile)
            {
                try
                {
                    SetPipelinePhase(AnalysisPipelinePhase.Coverage);
                    StatusTextBlock.Text = Strings.Status_RunningCoverageAnalysis;
                    AnalysisProgressBar.Value = 96;
                    _session.CoverageResult = await Task.Run(() =>
                        CoverageAnalyzer.Analyze(_session.AnalysisResult!, _session.Accessor!));

                    if (_session.CoverageResult.Error == null)
                    {
                        await HexViewer.AddCoverageGapRegionsAsync(_session.CoverageResult);
                    }
                }
                catch (Exception coverageEx)
                {
                    StatusTextBlock.Text = Strings.Status_CoverageAnalysisFailed(coverageEx.Message);
                }
            }

            var totalCount = _allCarvedFiles.Count;
            var fileCount = _allCarvedFiles.Count(f => !f.IsEsmRecord);
            var recordCount = _allCarvedFiles.Count(f => f.IsEsmRecord);
            var coveragePct = _session.CoverageResult?.RecognizedPercent ?? 0;
            StatusTextBlock.Text = fileCount > 0
                ? Strings.Status_FoundFilesToCarve(totalCount, coveragePct, fileCount, recordCount)
                : Strings.Status_FoundRecords(recordCount);
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
        // Safety guard: await reconstruction if it hasn't completed yet (normally a no-op)
        if (_session.SemanticResult == null)
        {
            await EnsureSemanticReconstructionAsync();
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

    #region Unified Analysis Helpers

    private static void AddTesFormStructRegions(AnalysisResult result)
    {
        if (result.EsmRecords == null) return;

        var shift = RuntimeBuildOffsets.GetPdbShift(null);
        foreach (var entry in result.EsmRecords.RuntimeEditorIds)
        {
            if (entry.TesFormOffset is not > 0) continue;

            var typeCode = RuntimeBuildOffsets.GetRecordTypeCode(entry.FormType);
            var structSize = RuntimeBuildOffsets.GetStructSize(entry.FormType, shift);

            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = entry.TesFormOffset.Value,
                Length = structSize,
                FileType = typeCode != null ? $"TESForm: {typeCode}" : $"TESForm: 0x{entry.FormType:X2}",
                FileName = entry.EditorId,
                SignatureId = "tesform_struct",
                Category = FileCategory.Struct
            });
        }
    }

    private static void AddRuntimeTerrainMeshRegions(AnalysisResult result)
    {
        if (result.EsmRecords == null) return;

        foreach (var land in result.EsmRecords.LandRecords
                     .Where(l => l.RuntimeTerrainMesh is { VertexDataOffset: > 0 }))
        {
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = land.RuntimeTerrainMesh!.VertexDataOffset,
                Length = 33 * 33 * 3 * 4, // 33x33 grid, 3 floats (x,y,z) each
                FileType = "Terrain Mesh",
                FileName = land.Header.FormId > 0 ? $"LAND {land.Header.FormId:X8}" : null,
                SignatureId = "terrain_mesh",
                Category = FileCategory.Model
            });
        }
    }

    private void RefreshCarvedFilesList()
    {
        if (_analysisResult == null) return;

        // Rebuild the full list from scratch to include new CarvedFileInfo entries.
        // For ESM files, CarvedFiles contains memory-map visualization groups, not user results.
        _allCarvedFiles.Clear();

        if (!_session.IsEsmFile)
        {
            foreach (var entry in _analysisResult.CarvedFiles)
            {
                _allCarvedFiles.Add(new CarvedFileEntry
                {
                    Offset = entry.Offset,
                    Length = entry.Length,
                    FileType = entry.FileType,
                    FileName = entry.FileName
                });
            }
        }

        if (_analysisResult.EsmRecords?.MainRecords != null)
        {
            foreach (var esmRecord in _analysisResult.EsmRecords.MainRecords)
            {
                _allCarvedFiles.Add(new CarvedFileEntry
                {
                    Offset = esmRecord.Offset,
                    Length = esmRecord.DataSize + 24,
                    FileType = "ESM Record",
                    EsmRecordType = esmRecord.RecordType,
                    FormId = esmRecord.FormId,
                    FileName = _analysisResult.FormIdMap.GetValueOrDefault(esmRecord.FormId),
                    Status = ExtractionStatus.Skipped
                });
            }
        }

        _allCarvedFiles.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        _carvedFiles.Clear();
        foreach (var item in _allCarvedFiles)
        {
            _carvedFiles.Add(item);
        }
    }

    private async Task AutoPopulateCurrentTabAsync(object? selectedTab)
    {
        if (!_session.HasEsmRecords) return;

        // Use the passed-in tab reference (saved before SubTabView was disabled)
        var selected = selectedTab;

        if (ReferenceEquals(selected, SummaryTab) && _session.SemanticResult != null)
        {
            PopulateRecordBreakdown();
        }
        else if (ReferenceEquals(selected, DataBrowserTab))
        {
            ReconstructButton_Click(this, new RoutedEventArgs());
        }
        else if (ReferenceEquals(selected, DialogueViewerTab))
        {
            _ = PopulateDialogueViewerAsync();
        }
        else if (ReferenceEquals(selected, WorldMapTab))
        {
            _ = PopulateWorldMapAsync();
        }
        else if (ReferenceEquals(selected, ReportsTab))
        {
            await GenerateReportsAsync();
        }
    }

    #endregion
}
