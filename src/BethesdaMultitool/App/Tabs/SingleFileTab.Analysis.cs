using System.Collections.ObjectModel;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Extraction;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.SaveGame;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Recovery;
using BethesdaMultitool.Core.Semantic;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Analysis methods: RunAnalysis*, ProcessPhase*, analysis orchestration
/// </summary>
public sealed partial class SingleFileTab
{
    private Dictionary<int, DecodedFormData>? _pendingDecodedForms;

    // Monotonic floor for AnalysisProgressBar: the multi-phase load (scan → BA2 localization → typed
    // parse → coverage) and DMP's ×0.8 scaling report through DIFFERENT sinks whose per-phase
    // percentages restart low, which made the bar visibly jump backwards. All writers go through
    // SetAnalysisProgress so the bar only ever advances until the next operation resets it.
    private double _analysisProgressFloor;

    private void SetAnalysisProgress(double value)
    {
        _analysisProgressFloor = Math.Max(_analysisProgressFloor, value);
        AnalysisProgressBar.Value = _analysisProgressFloor;
    }

    private void ResetAnalysisProgress()
    {
        _analysisProgressFloor = 0;
        AnalysisProgressBar.Value = 0;
    }

    // Temporary fields to pass save data from AnalyzeSaveFileAsync to the session
    private SaveFile? _pendingSaveData;

    #region Dependency Checking

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

    #endregion

    #region Extraction

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        var filePath = MinidumpPathTextBox.Text;
        var outputPath = OutputPathTextBox.Text;
        if (_analysisResult == null || string.IsNullOrEmpty(outputPath)) return;
        try
        {
            SetPipelinePhase(AnalysisPipelinePhase.Extracting);
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
            ResetAnalysisProgress(); // extraction is its own operation — restart the monotonic floor
            var progress = new Progress<ExtractionProgress>(p => DispatcherQueue.TryEnqueue(() =>
            {
                AnalysisProgressBar.IsIndeterminate = false;
                SetAnalysisProgress(p.PercentComplete);
            }));
            var analysisData = _analysisResult;
            var summary = await Task.Run(() => MinidumpExtractor.Extract(filePath, opts, progress, analysisData));

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

            if (summary.RuntimeTexturesExported > 0)
            {
                msg += $"\nRuntime textures: {summary.RuntimeTexturesExported} exported as DDS";
            }

            if (summary.RuntimeMeshesExported > 0)
            {
                msg += $"\nRuntime meshes: {summary.RuntimeMeshesExported} exported as OBJ";
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
            SetPipelinePhase(AnalysisPipelinePhase.Idle);
        }
    }

    #endregion

    #region Save File Browser

    private async Task PopulateSaveBrowserAsync()
    {
        if (_session.SaveData == null || _session.DecodedForms == null) return;

        // Prevent double-population
        if (DataBrowserContent.Visibility == Visibility.Visible) return;

        ParseProgressBar.Visibility = Visibility.Visible;
        ParseProgressBar.IsIndeterminate = true;
        ParseStatusText.Text = "Building save records tree...";
        StatusTextBlock.Text = "Building save records tree...";

        try
        {
            var save = _session.SaveData;
            var decodedForms = _session.DecodedForms;
            var resolver = _session.EffectiveResolver;
            var subtitles = _session.EffectiveSubtitles;

            // Build tree on background thread (with optional enrichment from supplementary data)
            var tree = await Task.Run(() => SaveBrowserTreeBuilder.BuildTree(save, decodedForms, resolver, subtitles));

            _esmBrowserTree = tree;
            _placementIndex = null;
            _factionMembersIndex = null;
            _raceLookup = null;
            _usageIndex = null;
            _flatListBuilt = false;

            StatusTextBlock.Text = "Loading tree view...";

            // Add nodes to tree (must be on UI thread)
            // Only show chevrons for nodes that actually have children
            EsmTreeView.RootNodes.Clear();
            foreach (var node in tree)
            {
                var hasChildren = node.Children.Count > 0 || node.HasUnrealizedChildren;
                var treeNode = new TreeViewNode { Content = node, HasUnrealizedChildren = hasChildren };
                EsmTreeView.RootNodes.Add(treeNode);
            }

            DataBrowserPlaceholder.Visibility = Visibility.Collapsed;
            DataBrowserContent.Visibility = Visibility.Visible;

            // Build FormID navigation index for save data. Inputs captured on the UI thread
            // (resolver above; the save tree has no placement/usage/race/faction indexes).
            var navRecords = _session.SemanticResult;
            var navGeneration = Volatile.Read(ref _navIndexGeneration);
            _formIdBuildTask = Task.Run(() =>
            {
                BuildFormIdNodeIndex(tree, navRecords, resolver,
                    placementIndex: null, usageIndex: null, raceLookup: null,
                    factionMembersIndex: null, navGeneration);
                DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = "");
            });
        }
        finally
        {
            ParseProgressBar.Visibility = Visibility.Collapsed;
            ParseProgressBar.IsIndeterminate = false;
            ParseStatusText.Text = "";
            StatusTextBlock.Text = "";
        }
    }

    #endregion

    #region Analysis Pipeline

    private sealed record FileAnalysisArtifacts(AnalysisResult Result, byte[]? EsmFileBuffer);

    private async Task<FileAnalysisArtifacts> RunFileAnalysisWithArtifactsAsync(
        string filePath, AnalysisFileType fileType, IProgress<AnalysisProgress> progress)
    {
        switch (fileType)
        {
            case AnalysisFileType.EsmFile:
            {
                var artifacts = await EsmFileAnalyzer.AnalyzeWithArtifactsAsync(
                    filePath,
                    progress,
                    VerboseCheckBox.IsChecked == true);
                return new FileAnalysisArtifacts(artifacts.Result, artifacts.FileBuffer);
            }
            case AnalysisFileType.Minidump:
            {
                var result = await SemanticFileLoader.AnalyzeOnlyAsync(
                    filePath,
                    new SemanticFileLoadOptions
                    {
                        FileType = fileType,
                        AnalysisProgress = progress,
                        GapRecovery = DmpGapRecoveryOptions.DiscoverOnly
                    });
                return new FileAnalysisArtifacts(result, null);
            }
            case AnalysisFileType.SaveFile:
            {
                var result = await AnalyzeSaveFileAsync(filePath, progress);
                return new FileAnalysisArtifacts(result, null);
            }
            default:
                throw new NotSupportedException($"Unknown file type: {filePath}");
        }
    }

    private async Task<UnifiedAnalysisResult> LoadSemanticResultAsync(
        IProgress<(int percent, string phase)> reconProgress,
        byte[]? esmFileBuffer)
    {
        if (_session.IsEsmFile && esmFileBuffer != null)
        {
            return await Task.Run(() =>
            {
                var accessor = new ByteArrayMemoryAccessor(esmFileBuffer);
                return SemanticFileLoader.LoadFromAnalysisResult(
                    _session.FilePath!,
                    _analysisResult!,
                    _session.FileType,
                    new SemanticFileLoadOptions
                    {
                        FileType = _session.FileType,
                        ParseProgress = reconProgress
                    },
                    accessor,
                    esmFileBuffer.LongLength);
            });
        }

        return await Task.Run(() => SemanticFileLoader.LoadFromAnalysisResult(
            _session.FilePath!,
            _analysisResult!,
            _session.FileType,
            reconProgress));
    }

    private async Task<AnalysisResult> AnalyzeSaveFileAsync(string filePath, IProgress<AnalysisProgress> progress)
    {
        var (save, decodedForms, result) = await SingleFileAnalysisHelper.AnalyzeSaveFileAsync(filePath, progress);
        _pendingSaveData = save;
        _pendingDecodedForms = decodedForms;
        return result;
    }

    private async Task RunSemanticParsePipelineAsync(
        byte[]? esmFileBuffer = null,
        EsmLoadProfile? profile = null,
        bool refreshCarvedFiles = true)
    {
        SetPipelinePhase(AnalysisPipelinePhase.Parsing);
        StatusTextBlock.Text = _session.IsEsmFile
            ? Strings.Status_ParsingEsmRecords
            : Strings.Status_ParsingRecords;

        var reconProgress = new Progress<(int percent, string phase)>(p =>
            DispatcherQueue.TryEnqueue(() =>
            {
                SetAnalysisProgress(80 + p.percent * 0.15);
                StatusTextBlock.Text = p.phase is "Complete" or "Analysis Complete"
                    ? "Finalizing semantic data..."
                    : p.phase;
            }));

        _semanticLoadTask = profile == null
            ? LoadSemanticResultAsync(reconProgress, esmFileBuffer)
            : profile.TimeAsync("Semantic parse", () => LoadSemanticResultAsync(reconProgress, esmFileBuffer));

        var loaded = await _semanticLoadTask;
        if (profile == null)
        {
            _session.AdoptSemanticSession(loaded);
        }
        else
        {
            profile.Time("Session adoption", () => _session.AdoptSemanticSession(loaded));
        }

        if (_session.SemanticResult != null)
        {
            // TESForm struct regions are added by the core pipeline (PostProcessMetadataAsync).
            // Terrain mesh regions depend on semantic parse enrichment, so add them here.
            if (profile == null)
            {
                SingleFileAnalysisHelper.AddRuntimeTerrainMeshRegions(_analysisResult!);
            }
            else
            {
                profile.Time("Runtime metadata", () =>
                    SingleFileAnalysisHelper.AddRuntimeTerrainMeshRegions(_analysisResult!));
            }

            if (refreshCarvedFiles)
            {
                if (profile == null)
                {
                    RefreshCarvedFilesList();
                    BuildResultsFilterCheckboxes();
                }
                else
                {
                    profile.Time("Carved file list", RefreshCarvedFilesList);
                    profile.Time("Filter UI", BuildResultsFilterCheckboxes);
                }
            }

            // Emit BSStringT read diagnostics (visible in VS Output window)
            var bsReport = BSStringDiagnostics.GetReport();
            System.Diagnostics.Debug.WriteLine("[BSStringT Diagnostics]\n" + bsReport);

            // The semantic model + cell PlacedReferences are now built; drop the parse-only scan
            // intermediates the parser already consumed (see ReleaseEsmScanIntermediates).
            ReleaseEsmScanIntermediates();
        }
    }

    private async Task RunCoverageAnalysisAsync()
    {
        try
        {
            SetPipelinePhase(AnalysisPipelinePhase.Coverage);
            StatusTextBlock.Text = Strings.Status_RunningCoverageAnalysis;
            SetAnalysisProgress(96);
            _session.CoverageResult = await Task.Run(() =>
                CoverageAnalyzer.Analyze(_session.AnalysisResult!, _session.Accessor!));

            if (_session.CoverageResult.Error == null)
            {
                await HexViewer.AddCoverageGapRegionsAsync(
                    _session.CoverageResult,
                    _session.AnalysisResult?.RecoverableGapCandidates);
            }
        }
        catch (Exception coverageEx)
        {
            StatusTextBlock.Text = Strings.Status_CoverageAnalysisFailed(coverageEx.Message);
        }
    }

    #endregion

    #region Semantic Parse

    /// <summary>
    ///     Safety guard: ensures semantic parse is complete before proceeding.
    ///     Under the unified flow, parsing completes eagerly during AnalyzeButton_Click,
    ///     so this should return immediately. Retained as a guard for edge cases.
    /// </summary>
    private async Task EnsureSemanticParseAsync()
    {
        if (_session.SemanticResult != null) return;
        if (_semanticLoadTask == null) return;

        try
        {
            var loaded = await _semanticLoadTask;
            if (_session.SemanticResult == null)
            {
                _session.AdoptSemanticSession(loaded);
            }

            if (_session.SemanticResult != null)
            {
                StatusTextBlock.Text =
                    Strings.Status_ParsedRecords(_session.SemanticResult.TotalRecordsParsed);

                // Lazy-parse path (tab opened before the load pipeline finished adopting): same
                // intermediate release as RunSemanticParsePipelineAsync. Idempotent.
                ReleaseEsmScanIntermediates();
            }
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(Strings.Dialog_ParseFailed_Title,
                $"{ex.GetType().Name}: {ex.Message}", true);
        }
    }

    private async Task PopulateDataBrowserAsync()
    {
        if (_session.SemanticResult == null) return;

        ParseProgressBar.Visibility = Visibility.Visible;
        ParseProgressBar.IsIndeterminate = true;
        ParseStatusText.Text = Strings.Status_BuildingDataBrowserTree;
        StatusTextBlock.Text = Strings.Status_BuildingDataBrowserTree;

        try
        {
            var semanticResult = _session.SemanticResult;

            // Merge load order records so DLC content appears in the browser
            var loadOrderRecords = _session.LoadOrder.BuildMergedRecords();
            if (loadOrderRecords != null)
                semanticResult = loadOrderRecords.MergeWith(semanticResult);

            var resolver = _session.EffectiveResolver ?? _session.Resolver;

            // Progress callback for status updates
            var progress = new Progress<string>(status =>
                DispatcherQueue.TryEnqueue(() =>
                {
                    ParseStatusText.Text = status;
                    StatusTextBlock.Text = status;
                }));

            // Build tree and lookup indexes on a background thread
            var (tree, placements, usageIndex, factionMembers, raceLookup) = await Task.Run(() =>
            {
                ((IProgress<string>)progress).Report(Strings.Status_BuildingCategoryTree);
                var builtTree = EsmBrowserTreeBuilder.BuildTree(semanticResult, resolver);
                EsmBrowserTreeBuilder.AppendRecoverableGapCategory(
                    builtTree,
                    _session.AnalysisResult?.RecoverableGapCandidates);

                // Build reverse placement index for Count (base FormID → world placements)
                var placementIndex = semanticResult.BuildBaseToPlacementsMap();

                // Build reverse usage index for GECK-style Use (scripts, lists, containers, packages)
                var formUsageIndex = FormUsageIndex.Build(semanticResult);

                // Build reverse faction index (faction FormID → NPC/creature members)
                var factionIndex = semanticResult.BuildFactionMembersIndex();

                // Build race lookup for FaceGen slider computation in property panels
                var races = semanticResult.Races.Count > 0
                    ? (IReadOnlyDictionary<uint, RaceRecord>)semanticResult.Races
                        .DistinctBy(r => r.FormId)
                        .ToDictionary(r => r.FormId)
                    : null;

                ((IProgress<string>)progress).Report(Strings.Status_SortingRecords);
                EsmBrowserTreeBuilder.SortRecordChildren(builtTree, EsmBrowserTreeBuilder.RecordSortMode.Name);

                return (builtTree, placementIndex, formUsageIndex, factionIndex, races);
            });

            _esmBrowserTree = tree;
            _placementIndex = placements;
            _usageIndex = usageIndex;
            _factionMembersIndex = factionMembers;
            _raceLookup = raceLookup;
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
            StatusTextBlock.Text = Strings.Status_BuildingNavIndex;

            // Pre-build FormID navigation index in the background (avoids delay on first link click)
            // Tracked via _formIdBuildTask so NavigateToFormId can await it if needed.
            // All inputs are captured HERE on the UI thread: the resolver getter enumerates
            // LoadOrder.Entries (UI-thread-mutated), and re-reading instance fields mid-task
            // would race a reload swapping them. The generation token lets ResetNavigation
            // invalidate this build if a reload/load-order change orphans it.
            var navGeneration = Volatile.Read(ref _navIndexGeneration);
            _formIdBuildTask = Task.Run(() =>
            {
                BuildFormIdNodeIndex(
                    tree, semanticResult, resolver,
                    placements, usageIndex, raceLookup, factionMembers,
                    navGeneration);
                DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = "");
            });
        }
        finally
        {
            ParseProgressBar.Visibility = Visibility.Collapsed;
            ParseProgressBar.IsIndeterminate = false;
            ParseStatusText.Text = "";
            StatusTextBlock.Text = "";
        }
    }

    #endregion

    #region Carved Files and Auto-Population

    private void RefreshCarvedFilesList()
    {
        if (_analysisResult == null) return;

        _allCarvedFiles.Clear();
        _allCarvedFiles.AddRange(SingleFileAnalysisHelper.BuildCarvedFileList(
            _analysisResult, isEsmFile: _session.IsEsmFile));
        _carvedFiles.ReplaceAll(_allCarvedFiles);
    }

    /// <summary>
    ///     Releases ESM scan-time intermediates that the record parser has already consumed to build
    ///     the semantic <c>RecordCollection</c> and cell PlacedReferences.
    ///     <c>RefrRecords</c> (ExtractedRefrRecord + the PositionSubrecords they uniquely hold) and
    ///     <c>NameReferences</c> have NO reader in any GUI / render / report path after the semantic
    ///     model exists — every reference to them lives inside the scan/parse pipeline. For a 922 MB
    ///     ESM (Fallout 76's SeventySix.esm) this frees ~2 GB / ~10M objects, which also shortens every
    ///     later GC pause (pause time scales with live-object count). Idempotent — safe to call from
    ///     both the load path and the lazy-parse path. ESM-only: DMP runtime extraction has different
    ///     post-parse consumers. KEPT alive: MainRecords (hex-viewer record overlay), LandRecords
    ///     (heightmap viewer), the GRUP maps, Positions (dangling-ref attribution), and runtime lists.
    /// </summary>
    private void ReleaseEsmScanIntermediates()
    {
        if (!_session.IsEsmFile || _analysisResult?.EsmRecords is not { } scan)
        {
            return;
        }

        scan.RefrRecords.Clear();
        scan.RefrRecords.TrimExcess();
        scan.NameReferences.Clear();
        scan.NameReferences.TrimExcess();
    }

    private async Task AutoPopulateCurrentTabAsync(object? selectedTab)
    {
        if (_session.IsSaveFile)
        {
            if (ReferenceEquals(selectedTab, DataBrowserTab) && _session.SaveData != null)
            {
                await PopulateSaveBrowserAsync();
            }

            return;
        }

        if (!_session.HasEsmRecords) return;

        var selected = selectedTab;

        if (ReferenceEquals(selected, SummaryTab) && _session.SemanticResult != null)
        {
            PopulateRecordBreakdown();
        }
        else if (ReferenceEquals(selected, DataBrowserTab))
        {
            ParseButton_Click(this, new RoutedEventArgs());
        }
        else if (ReferenceEquals(selected, DialogueViewerTab))
        {
            _tasks.Post("populate-dialogue", PopulateDialogueViewerAsync);
        }
        else if (ReferenceEquals(selected, WorldMapTab))
        {
            _tasks.Post("populate-worldmap", PopulateWorldMapAsync);
        }
        else if (ReferenceEquals(selected, NpcBrowserTab))
        {
            _tasks.Post("populate-npcs", PopulateNpcBrowserAsync);
        }
        else if (ReferenceEquals(selected, ReportsTab))
        {
            await _tasks.RunExclusiveAsync("generate-reports", GenerateReportsAsync);
        }
    }

    #endregion
}

