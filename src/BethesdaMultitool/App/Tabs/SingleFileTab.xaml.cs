using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Collections;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Orchestration;
using BethesdaMultitool.Core.Semantic;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace BethesdaMultitool;

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
    #region Properties

#pragma warning disable CA1822, S2325
    private StatusTextHelper StatusTextBlock => new();
#pragma warning restore CA1822, S2325

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _searchDebounceToken?.Dispose();
        _tasks.Dispose();
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

        // Auto-populate Records when first selected
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

        // Auto-populate Dialogue when first selected
        if (ReferenceEquals(selected, DialogueViewerTab) &&
            !_session.DialogueViewerPopulated &&
            _session.HasEsmRecords)
        {
            _tasks.Post("populate-dialogue", PopulateDialogueViewerAsync);
        }

        // Auto-populate World Map when first selected (also available for save files)
        if (ReferenceEquals(selected, WorldMapTab) &&
            !_session.WorldMapPopulated &&
            (_session.HasEsmRecords || _session.IsSaveFile))
        {
            await _tasks.RunExclusiveAsync("populate-worldmap", PopulateWorldMapAsync);
        }

        // Auto-populate NPC Browser when first selected
        if (ReferenceEquals(selected, NpcBrowserTab) &&
            !_session.NpcBrowserPopulated &&
            _session.HasEsmRecords)
        {
            _tasks.Post("populate-npcs", PopulateNpcBrowserAsync);
        }

        // Auto-generate reports when first selected (ESM or save files)
        if (ReferenceEquals(selected, ReportsTab) &&
            _reportEntries.Count == 0 &&
            (_session.HasEsmRecords || _session.IsSaveFile))
        {
            _tasks.Post("generate-reports", GenerateReportsAsync);
        }
    }

    #endregion

    #region Constructor

    public SingleFileTab()
    {
        InitializeComponent();
        ReorderSubTabsForGameDataWorkflow();
        ResultsListView.ItemsSource = _carvedFiles;
        ReportListView.ItemsSource = _reportEntries;
        InitializeFileTypeCheckboxes();
        SetupTextBoxContextMenus();
        WorldMapControl.BeforeNavigate += WorldMap_BeforeNavigate;
        KeyDown += SingleFileTab_KeyDown;
        Loaded += SingleFileTab_Loaded;
        Unloaded += SingleFileTab_Unloaded;
    }

    private void ReorderSubTabsForGameDataWorkflow()
    {
        SubTabView.TabItems.Clear();
        SubTabView.TabItems.Add(SummaryTab);
        SubTabView.TabItems.Add(DataBrowserTab);
        SubTabView.TabItems.Add(WorldMapTab);
        SubTabView.TabItems.Add(DialogueViewerTab);
        SubTabView.TabItems.Add(NpcBrowserTab);
        SubTabView.TabItems.Add(ReportsTab);
        SubTabView.TabItems.Add(RawViewTab);
        SubTabView.TabItems.Add(CoverageTab);
        SubTabView.SelectedItem = SummaryTab;
    }

    #endregion

    #region Fields

    private readonly List<CarvedFileEntry> _allCarvedFiles = [];
    private readonly BulkObservableCollection<CarvedFileEntry> _carvedFiles = [];
    private readonly Dictionary<string, CheckBox> _fileTypeCheckboxes = [];
    private readonly ObservableCollection<ReportEntry> _reportEntries = [];
    private readonly AnalysisSessionState _session = new();
    private readonly CarvedFilesSorter _sorter = new();

    /// <summary>
    ///     Session-scoped background populates (dialogue/world/NPC/reports). Single-flight per key;
    ///     drained before the session is reopened or disposed so no populate outlives the state it
    ///     reads (the torn-collection bug class).
    /// </summary>
    private readonly SessionTaskRunner _tasks =
        new SessionTaskRunner(nameof(SingleFileTab)).RegisterWith(ResourceRegistry.Instance);

    private readonly SemaphoreSlim _worldMapLoadGate = new(1, 1);

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
    private int _worldMapLoadGeneration;

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

        // Pre-select the requested sub-tab BEFORE analysis. AnalyzeButton_Click captures the selected
        // tab at the START and auto-populates THAT tab at the END (after the semantic parse), so this is
        // what makes the world map populate as part of the analyze pass — instead of a second, race-prone
        // populate afterward (HasEsmRecords flips true mid-analysis, well before the parse finishes).
        var targetTab = ResolveAutoOpenTab();
        if (targetTab is not null) SubTabView.SelectedItem = targetTab;

        // Wait for the Analyze button to enable (the dependency check + file setup can lag well past a
        // fixed delay) before kicking analysis — the auto-open below depends on it actually running.
        for (var i = 0; i < 200 && !AnalyzeButton.IsEnabled; i++) await Task.Delay(50);
        if (AnalyzeButton.IsEnabled) AnalyzeButton_Click(this, new RoutedEventArgs());

        if (targetTab is not null) await AutoOpenRequestedViewAsync(targetTab);
    }

    private Microsoft.UI.Xaml.Controls.TabViewItem? ResolveAutoOpenTab() =>
        Program.AutoOpenView?.Trim().ToLowerInvariant() switch
        {
            "worldmap" or "world" or "map" or "2d" or "3d" => WorldMapTab,
            "dialogue" => DialogueViewerTab,
            "data" or "databrowser" => DataBrowserTab,
            "actors" or "npcs" => NpcBrowserTab,
            "reports" => ReportsTab,
            _ => null
        };

    /// <summary>
    ///     Finishes driving the GUI to the screen requested via the startup flags (<c>--view</c> /
    ///     <c>--worldspace</c> / <c>--layer</c> / <c>--rendered-models</c>) with NO manual clicks — the
    ///     tab was pre-selected before analysis, so the app's own pipeline populates it; this waits for
    ///     that (the populate runs fire-and-forget inside AnalyzeButton_Click) then sets worldspace
    ///     (densest by default), layer, and overlay so an instrumented run lands on the exact failing view.
    /// </summary>
    private async Task AutoOpenRequestedViewAsync(Microsoft.UI.Xaml.Controls.TabViewItem targetTab)
    {
        var view = Program.AutoOpenView?.Trim().ToLowerInvariant() ?? "";
        var log = BethesdaMultitool.Core.Diagnostics.Logger.Instance;
        log.Info("[AutoOpen] view={0} ws={1} layer={2} — waiting for analysis + map populate…",
            view, Program.AutoOpenWorldspace ?? "(densest)", Program.AutoOpenLayer ?? "(default)");

        if (!ReferenceEquals(targetTab, WorldMapTab))
        {
            return; // non-map tabs: AnalyzeButton_Click's own end-of-run auto-populate is enough.
        }

        // AnalyzeButton_Click populates the world map at the end of its run (it captured this tab at
        // start); wait for that to finish + the worldspace picker to fill.
        var mapReady = await WaitForAsync(
            () => _session.WorldMapPopulated && WorldMapControl.Profiler_WorldspaceCount > 0,
            timeoutMs: 120_000);
        log.Info("[AutoOpen] worldmap populated={0} worldspaces={1}",
            mapReady, WorldMapControl.Profiler_WorldspaceCount);
        if (!mapReady) return;

        // Re-assert the tab selection (analysis resets sub-tab content) so the map is actually shown.
        SubTabView.SelectedItem = targetTab;

        // 2D vs 3D viewer.
        if (view == "3d" && WorldViewModeComboBox is not null) WorldViewModeComboBox.SelectedIndex = 1;

        // Worldspace: match --worldspace by name; else the densest (most cells), so it never sits on an
        // empty test worldspace (the default the picker lands on).
        SelectAutoWorldspace(Program.AutoOpenWorldspace);

        // Layer (e.g. --layer textures).
        if (TryParseWorldMapLayer(Program.AutoOpenLayer, out var layer))
        {
            WorldMapControl.Profiler_Layer = layer;
        }

        // Rendered-models overlay — routed through the real checkbox, which only enables once the 3D
        // provider reports ready, so retry briefly.
        if (Program.AutoRenderedModels)
        {
            for (var i = 0; i < 120 && !WorldMapControl.Profiler_ShowRenderedObjects; i++)
            {
                WorldMapControl.Profiler_ShowRenderedObjects = true;
                if (WorldMapControl.Profiler_ShowRenderedObjects) break;
                await Task.Delay(250);
            }
        }

        log.Info("[AutoOpen] done: worldspace='{0}' layer={1} renderedModels={2}",
            WorldMapControl.Profiler_WorldspaceSelectedIndex >= 0
                ? WorldMapControl.Profiler_WorldspaceLabels[WorldMapControl.Profiler_WorldspaceSelectedIndex]
                : "(none)",
            WorldMapControl.Profiler_Layer,
            WorldMapControl.Profiler_ShowRenderedObjects);

        if (Program.AutoReproLayerToggle)
        {
            await ReproLayerToggleAsync();
        }
    }

    /// <summary>
    ///     Repro harness for the "terrain textures render until you switch layers and back" bug: logs
    ///     cache/aggregate state, switches to Heightmap, then back to the original layer, logging again at
    ///     each step so the broken post-switch state is visible in the trace.
    /// </summary>
    private async Task ReproLayerToggleAsync()
    {
        var log = BethesdaMultitool.Core.Diagnostics.Logger.Instance;
        var original = WorldMapControl.Profiler_Layer;

        void LogState(string tag) => log.Info(
            "[Repro] {0}: layer={1} zoom={2:F5} aggActive={3} aggBmp={4} aggUnavail={5} hmBmp={6} cacheSize={7} cellLayer={8} buildVer={9} cacheGen={10}",
            tag, WorldMapControl.Profiler_Layer, WorldMapControl.Profiler_Zoom,
            WorldMapControl.Profiler_TerrainAggregateActive, WorldMapControl.Profiler_AggregateBitmapPresent,
            WorldMapControl.Profiler_AggregateUnavailable, WorldMapControl.Profiler_HeightmapBitmapPresent,
            WorldMapControl.Profiler_CacheCount, WorldMapControl.Profiler_CellBitmapsLayer,
            WorldMapControl.Profiler_BuildVersion, WorldMapControl.Profiler_CacheGen);

        async Task Toggle(string phase, int heightmapDwellMs)
        {
            log.Info("[Repro] === {0} (heightmap dwell {1}ms) ===", phase, heightmapDwellMs);
            await Task.Delay(3000);
            LogState(phase + " initial");

            WorldMapControl.Profiler_Layer = WorldMapLayer.Heightmap;
            await Task.Delay(heightmapDwellMs);
            LogState(phase + " after-heightmap");

            WorldMapControl.Profiler_Layer = original;
            // Observe the post-switchback state OVER TIME — the heightmap-wins bug surfaces a beat later
            // when a late async build lands, not in the first frame after the switch.
            var elapsed = 0;
            foreach (var step in new[] { 1500, 1500, 3000, 5000 })
            {
                await Task.Delay(step);
                elapsed += step;
                LogState($"{phase} switchback+{elapsed}ms");
            }
        }

        // First, at the landing zoom (zoomed way out → aggregate LOD path), fast switch-back to race
        // the in-flight build.
        await Toggle("zoomed-out", heightmapDwellMs: 800);

        // Then, zoomed in so the per-cell streaming path is active; fast switch-back here too.
        log.Info("[Repro] zooming in to per-cell (~245 px/cell)…");
        WorldMapControl.Profiler_CenterOnActiveCells(0.06f);
        await Task.Delay(5000);
        await Toggle("zoomed-in", heightmapDwellMs: 800);

        log.Info("[Repro] scenario complete — exiting in 3s.");
        await Task.Delay(3000);
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    /// <summary>Polls <paramref name="condition" /> every 100 ms up to <paramref name="timeoutMs" />.</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        for (var elapsed = 0; elapsed < timeoutMs; elapsed += 100)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    /// <summary>
    ///     Selects the world map worldspace: by <paramref name="nameSubstring" /> when given, else the
    ///     densest (highest cell count parsed from the "Name — N cells" picker labels).
    /// </summary>
    private void SelectAutoWorldspace(string? nameSubstring)
    {
        var labels = WorldMapControl.Profiler_WorldspaceLabels;
        if (labels.Count == 0) return;

        if (!string.IsNullOrWhiteSpace(nameSubstring))
        {
            for (var i = 0; i < labels.Count; i++)
            {
                if (labels[i].Contains(nameSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    WorldMapControl.Profiler_WorldspaceSelectedIndex = i;
                    return;
                }
            }
        }

        var bestIndex = -1;
        var bestCells = -1;
        for (var i = 0; i < labels.Count; i++)
        {
            var cells = ParseCellCount(labels[i]);
            if (cells > bestCells)
            {
                bestCells = cells;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0) WorldMapControl.Profiler_WorldspaceSelectedIndex = bestIndex;
    }

    /// <summary>Pulls the integer that precedes "cells" in a "Name — N cells" picker label (0 if none).</summary>
    private static int ParseCellCount(string label)
    {
        var idx = label.IndexOf("cells", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0) return 0;
        var end = idx;
        while (end > 0 && !char.IsDigit(label[end - 1])) end--;
        var start = end;
        while (start > 0 && char.IsDigit(label[start - 1])) start--;
        return start < end && int.TryParse(label.AsSpan(start, end - start), out var n) ? n : 0;
    }

    private static bool TryParseWorldMapLayer(string? name, out WorldMapLayer layer)
    {
        layer = WorldMapLayer.Heightmap;
        if (string.IsNullOrWhiteSpace(name)) return false;
        switch (name.Trim().ToLowerInvariant())
        {
            case "textures" or "texture" or "textured" or "terraintextures":
                layer = WorldMapLayer.TerrainTextures;
                return true;
            case "heightmap" or "height":
                layer = WorldMapLayer.Heightmap;
                return true;
            case "vertexcolors" or "vertex":
                layer = WorldMapLayer.VertexColors;
                return true;
            case "regions" or "terrainregions":
                layer = WorldMapLayer.TerrainRegions;
                return true;
            case "slope":
                layer = WorldMapLayer.Slope;
                return true;
            default:
                return false;
        }
    }

    private async void SingleFileTab_Unloaded(object sender, RoutedEventArgs e)
    {
        // Quiesce background populates before tearing down the session state they read from.
        await _tasks.CancelAllAndDrainAsync();
        _tasks.Dispose();
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

            // The phase gate stops NEW populates; this stops the OLD session's in-flight ones
            // before their state is torn down and reopened below.
            await _tasks.CancelAllAndDrainAsync();

            _carvedFiles.ReplaceAll([]);
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

            ResetAnalysisProgress();
            var progress = new Progress<AnalysisProgress>(p => DispatcherQueue.TryEnqueue(() =>
            {
                AnalysisProgressBar.IsIndeterminate = false;
                SetAnalysisProgress(fileType == AnalysisFileType.Minidump
                    ? p.PercentComplete * 0.8
                    : p.PercentComplete);
                StatusTextBlock.Text = SingleFileAnalysisHelper.ResolvePhaseText(p, fileType);
            }));

            var profile = fileType == AnalysisFileType.EsmFile ? new EsmLoadProfile() : null;
            var artifacts = profile == null
                ? await RunFileAnalysisWithArtifactsAsync(filePath, fileType, progress)
                : await profile.TimeAsync("Analyze", () =>
                    RunFileAnalysisWithArtifactsAsync(filePath, fileType, progress));

            _analysisResult = artifacts.Result;

            // Build _allCarvedFiles but do NOT populate the observable _carvedFiles yet.
            if (fileType != AnalysisFileType.SaveFile && fileType != AnalysisFileType.EsmFile)
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
                await RunSemanticParsePipelineAsync(artifacts.EsmFileBuffer, profile, refreshCarvedFiles: false);
            }

            // Apply any pre-analyze load-order selection now: AFTER _session.Open above (which
            // disposed the previous LoadOrder — entries staged earlier would have been wiped) and
            // BEFORE AutoPopulateCurrentTabAsync (populates read _session.LoadOrder at populate
            // time, so the first build sees the merged view without a double rebuild).
            await ApplyPendingLoadOrderAsync(filePath);

            if (fileType != AnalysisFileType.SaveFile)
            {
                profile?.Time("Carved file list", RefreshCarvedFilesList);
                profile?.Time("Filter UI", BuildResultsFilterCheckboxes);
                if (profile == null)
                {
                    RefreshCarvedFilesList();
                    BuildResultsFilterCheckboxes();
                }
            }
            else
            {
                BuildResultsFilterCheckboxes();
            }

            // Load HexViewer AFTER all analysis and parsing is complete
            SetPipelinePhase(AnalysisPipelinePhase.LoadingMap);
            StatusTextBlock.Text = "Preparing raw view...";
            if (profile == null)
            {
                await HexViewer.LoadDataAsync(filePath, _analysisResult, _session.Accessor!);
            }
            else
            {
                await profile.TimeAsync("Hex/raw metadata",
                    () => HexViewer.LoadDataAsync(filePath, _analysisResult, _session.Accessor!));
            }

            StatusTextBlock.Text = "Preparing selected tab...";
            if (profile == null)
            {
                await AutoPopulateCurrentTabAsync(selectedTabForAutoPopulate);
            }
            else
            {
                await profile.TimeAsync("Current tab population",
                    () => AutoPopulateCurrentTabAsync(selectedTabForAutoPopulate));
            }

            // Run coverage analysis for memory dumps only
            if (!_session.IsEsmFile && !_session.IsSaveFile)
            {
                await RunCoverageAnalysisAsync();
            }

            StatusTextBlock.Text = SingleFileAnalysisHelper.BuildCompletionStatus(_session, _allCarvedFiles);
            profile?.Log(filePath);
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
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        MinidumpPathTextBox.Text = file.Path;
        _analysisResult = null;
        _carvedFiles.ReplaceAll([]);
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
            WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));
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
                _carvedFiles.ReplaceAll([]);
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
