using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FalloutXbox360Utils;

/// <summary>
///     Load order management: dialog for adding/reordering supplementary ESM/ESP/DMP files
///     and the loading pipeline that resolves records in load order.
/// </summary>
public sealed partial class SingleFileTab
{
    private async void LoadOrderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsAnalyzed)
        {
            return;
        }

        var workingEntries = LoadOrderDialogService.CreateWorkingEntries(_session.LoadOrder.Entries);
        var dialogResult = await LoadOrderDialogService.ShowAsync(
            XamlRoot,
            workingEntries,
            new LoadOrderDialogOptions
            {
                Title = "Load Order",
                IntroText = "Files later in the list override records from earlier files.",
                AllowSubtitleCsv = true,
                SubtitleCsvPath = _session.LoadOrder.SubtitleCsvPath,
                PrimaryFilePath = _session.FilePath
            });

        switch (dialogResult.Action)
        {
            case LoadOrderDialogAction.Cancel:
                return;
            case LoadOrderDialogAction.ClearAll:
                _session.LoadOrder.Dispose();
                await OnLoadOrderChanged();
                return;
        }

        var csvPath = dialogResult.SubtitleCsvPath?.Trim();
        var hasEntries = dialogResult.Entries.Count > 0;
        var hasCsv = !string.IsNullOrEmpty(csvPath) && File.Exists(csvPath);
        if (!hasEntries && !hasCsv)
        {
            return;
        }

        try
        {
            SetPipelinePhase(AnalysisPipelinePhase.Parsing);
            StatusTextBlock.Text = "Loading load order data...";
            AnalysisProgressBar.IsIndeterminate = true;

            await LoadOrderDialogService.ApplyAsync(
                _session.LoadOrder,
                dialogResult.Entries,
                csvPath,
                status => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = status));

            await OnLoadOrderChanged();
            StatusTextBlock.Text = "Load order data loaded.";
        }
        catch (Exception ex)
        {
            await ShowDialogAsync(
                "Load Failed",
                $"Failed to load load order data:\n{ex.GetType().Name}: {ex.Message}",
                true);
        }
        finally
        {
            SetPipelinePhase(AnalysisPipelinePhase.Idle);
            AnalysisProgressBar.IsIndeterminate = false;
        }
    }

    private async Task OnLoadOrderChanged()
    {
        UpdateLoadOrderStatusText();

        // Reset data browser so it rebuilds with new resolver
        DataBrowserContent.Visibility = Visibility.Collapsed;
        DataBrowserPlaceholder.Visibility = Visibility.Visible;
        _esmBrowserTree = null;

        // Reset world map
        _session.WorldMapPopulated = false;
        _session.WorldViewData = null;

        // Reset dialogue viewer so it rebuilds with new resolver/subtitles
        _session.DialogueViewerPopulated = false;
        _session.DialogueTree = null;
        _session.TopicsBySpeaker = null;
        _session.DialogueFormIdIndex = null;

        // Reset reports so they regenerate with new resolver
        _reportEntries.Clear();

        // Re-trigger the currently selected tab
        var selected = SubTabView.SelectedItem;
        if (selected != null)
        {
            SubTabView_SelectionChanged(this, new SelectionChangedEventArgs([], [selected]));
        }

        await Task.CompletedTask;
    }

    private void UpdateLoadOrderStatusText()
    {
        var lo = _session.LoadOrder;
        if (!lo.HasData)
        {
            LoadOrderStatusText.Text = "";
            return;
        }

        var parts = new List<string>();
        if (lo.Entries.Count > 0)
        {
            parts.Add($"{lo.Entries.Count} file{(lo.Entries.Count == 1 ? "" : "s")}");
        }

        if (lo.SubtitleCsvPath != null)
        {
            parts.Add("+ subtitles");
        }

        LoadOrderStatusText.Text = string.Join(" ", parts);
    }
}
