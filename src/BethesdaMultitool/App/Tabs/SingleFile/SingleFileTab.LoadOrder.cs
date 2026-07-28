using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Load order management: dialog for adding/reordering supplementary ESM/ESP/DMP files
///     and the loading pipeline that resolves records in load order.
/// </summary>
public sealed partial class SingleFileTab
{
    // Pre-analyze load-order selection, staged on the TAB — never on _session.LoadOrder:
    // AnalyzeButton_Click calls _session.Open() mid-run, whose Dispose() wipes LoadOrder, so
    // anything written there before the Load run is silently destroyed. The stash is applied after
    // the session reopens (before the first tab populate) and SURVIVES the run, so re-Loading the
    // same or a different primary reuses the last selection. Kept in sync with post-analyze dialog
    // Apply/Clear All so a cleared selection can't resurrect on the next Load.
    private List<LoadOrderEntry>? _pendingLoadOrderEntries;
    private string? _pendingSubtitleCsvPath;

    private async void LoadOrderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_session.IsAnalyzed)
        {
            await ShowPreAnalyzeLoadOrderDialogAsync();
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
                _pendingLoadOrderEntries = null;
                _pendingSubtitleCsvPath = null;
                _session.LoadOrder.Dispose();
                await OnLoadOrderChanged();
                return;
        }

        var csvPath = dialogResult.SubtitleCsvPath?.Trim();
        var hasEntries = dialogResult.Entries.Count > 0;
        var hasCsv = !string.IsNullOrEmpty(csvPath) && File.Exists(csvPath);
        // Mirror the applied selection into the stash so a later re-Load keeps it.
        _pendingLoadOrderEntries = hasEntries ? dialogResult.Entries.ToList() : null;
        _pendingSubtitleCsvPath = hasCsv ? csvPath : null;
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

            // Switch back to Idle before OnLoadOrderChanged so the re-triggered tab handler
            // actually runs. SubTabView_SelectionChanged guards on `_pipelinePhase == Idle`
            // and early-returns otherwise — if we stayed in Parsing here, PopulateWorldMapAsync
            // would never re-run and the world map would keep its stale (empty) load-order
            // AdditionalDataPaths even though Entries was just populated.
            SetPipelinePhase(AnalysisPipelinePhase.Idle);
            AnalysisProgressBar.IsIndeterminate = false;

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
            // Idempotent — already Idle on the happy path; this catches the exception path.
            SetPipelinePhase(AnalysisPipelinePhase.Idle);
            AnalysisProgressBar.IsIndeterminate = false;
        }
    }

    /// <summary>
    ///     Pre-analyze "Load Order..." click: pick + reorder files WITHOUT loading anything — the
    ///     dialog's Apply only stages the selection (instant), and the Load run applies it after the
    ///     session opens. Eagerly calling ApplyAsync here would parse multi-GB masters into a
    ///     LoadOrder that _session.Open() is about to dispose.
    /// </summary>
    private async Task ShowPreAnalyzeLoadOrderDialogAsync()
    {
        var workingEntries = LoadOrderDialogService.CreateWorkingEntries(
            _pendingLoadOrderEntries ?? Enumerable.Empty<LoadOrderEntry>());
        var dialogResult = await LoadOrderDialogService.ShowAsync(
            XamlRoot,
            workingEntries,
            new LoadOrderDialogOptions
            {
                Title = "Load Order",
                IntroText = "Files later in the list override records from earlier files. " +
                            "They load together with the primary file when you click Load.",
                AllowSubtitleCsv = true,
                SubtitleCsvPath = _pendingSubtitleCsvPath,
                PrimaryFilePath = MinidumpPathTextBox.Text
            });

        switch (dialogResult.Action)
        {
            case LoadOrderDialogAction.Cancel:
                return;
            case LoadOrderDialogAction.ClearAll:
                _pendingLoadOrderEntries = null;
                _pendingSubtitleCsvPath = null;
                UpdateLoadOrderStatusText();
                return;
        }

        var csvPath = dialogResult.SubtitleCsvPath?.Trim();
        _pendingLoadOrderEntries = dialogResult.Entries.Count > 0 ? dialogResult.Entries.ToList() : null;
        _pendingSubtitleCsvPath = !string.IsNullOrEmpty(csvPath) && File.Exists(csvPath) ? csvPath : null;
        UpdateLoadOrderStatusText();
    }

    /// <summary>
    ///     Applies the staged pre-analyze selection to the (freshly reopened) session's LoadOrder.
    ///     Called by AnalyzeButton_Click AFTER _session.Open (which wiped the previous LoadOrder) and
    ///     BEFORE the first tab populate — populates read _session.LoadOrder at populate time, so the
    ///     first Data Browser / World Map build sees the merged view without a second rebuild. The
    ///     entries are re-filtered against the FINAL primary path (the dialog only dedups against the
    ///     textbox at add time; the user can change the primary afterwards).
    /// </summary>
    private async Task ApplyPendingLoadOrderAsync(string primaryFilePath)
    {
        if (_pendingLoadOrderEntries is not { Count: > 0 } && _pendingSubtitleCsvPath is null)
        {
            return;
        }

        StatusTextBlock.Text = "Loading load order data...";
        var entries = (_pendingLoadOrderEntries ?? [])
            .Where(entry => !string.Equals(entry.FilePath, primaryFilePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await LoadOrderDialogService.ApplyAsync(
            _session.LoadOrder,
            entries,
            _pendingSubtitleCsvPath,
            status => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = status));
        UpdateLoadOrderStatusText();
    }

    private async Task OnLoadOrderChanged()
    {
        UpdateLoadOrderStatusText();

        // Reset data browser so it rebuilds with new resolver
        DataBrowserContent.Visibility = Visibility.Collapsed;
        DataBrowserPlaceholder.Visibility = Visibility.Visible;
        _esmBrowserTree = null;
        _populateDataBrowserTask = null; // next populate must rebuild with the new load order

        // Invalidate the FormID nav index alongside the tree it indexes — a stale index full of
        // old-tree nodes would let NavigateToFormId skip awaiting the new build and walk the new
        // tree while the background task is still populating it.
        ResetNavigation();

        // Reset world map
        _session.WorldMapPopulated = false;
        _session.WorldViewData = null;
        ResetWorldMap();

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
            // Nothing applied to the session — surface the staged pre-analyze selection instead so
            // the footer confirms the pick before the Load run applies it.
            var pending = new List<string>();
            if (_pendingLoadOrderEntries is { Count: > 0 } p)
            {
                pending.Add($"{p.Count} file{(p.Count == 1 ? "" : "s")}");
            }

            if (_pendingSubtitleCsvPath != null)
            {
                pending.Add("+ subtitles");
            }

            LoadOrderStatusText.Text = pending.Count > 0 ? $"{string.Join(" ", pending)} (pending)" : "";
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
