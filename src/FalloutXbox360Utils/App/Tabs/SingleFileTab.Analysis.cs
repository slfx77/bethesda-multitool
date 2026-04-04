using System.Collections.ObjectModel;
using FalloutXbox360Utils.App.Helpers;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Extraction;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.SaveGame;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FalloutXbox360Utils;

/// <summary>
///     Analysis methods: RunAnalysis*, ProcessPhase*, analysis orchestration
/// </summary>
public sealed partial class SingleFileTab
{
    private Dictionary<int, DecodedFormData>? _pendingDecodedForms;

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
            var progress = new Progress<ExtractionProgress>(p => DispatcherQueue.TryEnqueue(() =>
            {
                AnalysisProgressBar.IsIndeterminate = false;
                AnalysisProgressBar.Value = p.PercentComplete;
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
        ParseStatusText.Text = "Building save data browser...";
        StatusTextBlock.Text = "Building save data browser...";

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

            // Build FormID navigation index for save data
            _formIdBuildTask = Task.Run(() =>
            {
                BuildFormIdNodeIndex();
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

    private async Task<AnalysisResult> RunFileAnalysisAsync(
        string filePath, AnalysisFileType fileType, IProgress<AnalysisProgress> progress)
    {
        return fileType switch
        {
            AnalysisFileType.EsmFile => await EsmFileAnalyzer.AnalyzeAsync(filePath, progress),
            AnalysisFileType.Minidump => await new MinidumpAnalyzer().AnalyzeAsync(filePath, progress),
            AnalysisFileType.SaveFile => await AnalyzeSaveFileAsync(filePath, progress),
            _ => throw new NotSupportedException($"Unknown file type: {filePath}")
        };
    }

    private async Task<AnalysisResult> AnalyzeSaveFileAsync(string filePath, IProgress<AnalysisProgress> progress)
    {
        var (save, decodedForms, result) = await SingleFileAnalysisHelper.AnalyzeSaveFileAsync(filePath, progress);
        _pendingSaveData = save;
        _pendingDecodedForms = decodedForms;
        return result;
    }

    private async Task RunSemanticParsePipelineAsync()
    {
        SetPipelinePhase(AnalysisPipelinePhase.Parsing);
        StatusTextBlock.Text = _session.IsEsmFile
            ? Strings.Status_ParsingEsmRecords
            : Strings.Status_ParsingRecords;

        var reconProgress = new Progress<(int percent, string phase)>(p =>
            DispatcherQueue.TryEnqueue(() =>
            {
                AnalysisProgressBar.Value = 80 + p.percent * 0.15;
                StatusTextBlock.Text = p.phase;
            }));

        _semanticParseTask = Task.Run(() =>
        {
            var parser = new RecordParser(
                _analysisResult!.EsmRecords!,
                _analysisResult.FormIdMap,
                _session.Accessor!,
                _session.FileSize,
                _analysisResult.MinidumpInfo);
            return parser.ParseAll(reconProgress);
        });

        try
        {
            _session.SemanticResult = await _semanticParseTask;
            if (_session.SemanticResult != null)
            {
                _session.Resolver = _session.SemanticResult.CreateResolver();
                // TESForm struct regions are added by the core pipeline (PostProcessMetadataAsync).
                // Terrain mesh regions depend on semantic parse enrichment, so add them here.
                SingleFileAnalysisHelper.AddRuntimeTerrainMeshRegions(_analysisResult!);
                RefreshCarvedFilesList();
                BuildResultsFilterCheckboxes();

                // Emit BSStringT read diagnostics (visible in VS Output window)
                var bsReport = BSStringDiagnostics.GetReport();
                System.Diagnostics.Debug.WriteLine("[BSStringT Diagnostics]\n" + bsReport);
            }
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(Strings.Dialog_ParseFailed_Title,
                $"{ex.GetType().Name}: {ex.Message}", true);
        }
    }

    private async Task RunCoverageAnalysisAsync()
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
        if (_semanticParseTask == null) return;

        try
        {
            _session.SemanticResult = await _semanticParseTask;

            if (_session.SemanticResult != null)
            {
                _session.Resolver = _session.SemanticResult.CreateResolver();
                StatusTextBlock.Text =
                    Strings.Status_ParsedRecords(_session.SemanticResult.TotalRecordsParsed);
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
            // Tracked via _formIdBuildTask so NavigateToFormId can await it if needed
            _formIdBuildTask = Task.Run(() =>
            {
                BuildFormIdNodeIndex();
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
        _carvedFiles.Clear();
        foreach (var item in _allCarvedFiles)
        {
            _carvedFiles.Add(item);
        }
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
            _ = PopulateDialogueViewerAsync();
        }
        else if (ReferenceEquals(selected, WorldMapTab))
        {
            _ = PopulateWorldMapAsync();
        }
        else if (ReferenceEquals(selected, NpcBrowserTab))
        {
            _ = PopulateNpcBrowserAsync();
        }
        else if (ReferenceEquals(selected, ReportsTab))
        {
            await GenerateReportsAsync();
        }
    }

    #endregion
}
