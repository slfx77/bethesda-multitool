using System.Collections.ObjectModel;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Extraction;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
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

    #region Semantic Reconstruction

    /// <summary>
    ///     Safety guard: ensures semantic reconstruction is complete before proceeding.
    ///     Under the unified flow, reconstruction completes eagerly during AnalyzeButton_Click,
    ///     so this should return immediately. Retained as a guard for edge cases.
    /// </summary>
    private async Task EnsureSemanticReconstructionAsync()
    {
        if (_session.SemanticResult != null) return;
        if (_semanticReconstructionTask == null) return;

        try
        {
            _session.SemanticResult = await _semanticReconstructionTask;

            if (_session.SemanticResult != null)
            {
                _session.Resolver = _session.SemanticResult.CreateResolver();
                StatusTextBlock.Text =
                    Strings.Status_ReconstructedRecords(_session.SemanticResult.TotalRecordsReconstructed);
            }
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(Strings.Dialog_ReconstructionFailed_Title,
                $"{ex.GetType().Name}: {ex.Message}", true);
        }
    }

    private async Task PopulateDataBrowserAsync()
    {
        if (_session.SemanticResult == null) return;

        ReconstructProgressBar.Visibility = Visibility.Visible;
        ReconstructProgressBar.IsIndeterminate = true;
        ReconstructStatusText.Text = Strings.Status_BuildingDataBrowserTree;
        StatusTextBlock.Text = Strings.Status_BuildingDataBrowserTree;

        try
        {
            var semanticResult = _session.SemanticResult;
            var resolver = _session.Resolver;

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
                var builtTree = EsmBrowserTreeBuilder.BuildTree(semanticResult, resolver);

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
            ReconstructProgressBar.Visibility = Visibility.Collapsed;
            ReconstructProgressBar.IsIndeterminate = false;
            ReconstructStatusText.Text = "";
            StatusTextBlock.Text = "";
        }
    }

    #endregion
}
