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

    /// <summary>Copies the applied or staged supplementary selection for the combined Explore dialog.</summary>
    internal ObservableCollection<LoadOrderEntry> CreateExploreLoadOrderEntries() =>
        LoadOrderDialogService.CreateWorkingEntries(_session.IsAnalyzed
            ? _session.LoadOrder.Entries : _pendingLoadOrderEntries ?? Enumerable.Empty<LoadOrderEntry>());

    /// <summary>The subtitle selection belonging to the current record document.</summary>
    internal string? ExploreSubtitleCsvPath => _session.IsAnalyzed
        ? _session.LoadOrder.SubtitleCsvPath : _pendingSubtitleCsvPath;

    /// <summary>Retains explicit choices for application after a different primary document opens.</summary>
    internal void StageExploreLoadOrder(LoadOrderDialogResult result)
    {
        _pendingLoadOrderEntries = result.Action == LoadOrderDialogAction.ClearAll || result.Entries.Count == 0
            ? null : result.Entries.ToList();
        var csvPath = result.SubtitleCsvPath?.Trim();
        _pendingSubtitleCsvPath = result.Action != LoadOrderDialogAction.ClearAll &&
            !string.IsNullOrEmpty(csvPath) && File.Exists(csvPath) ? csvPath : null;
        UpdateLoadOrderStatusText();
    }

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
                IntroText = "Files later in the list override earlier files. For Fallout 3/New Vegas plugins, " +
                    "the primary file is placed before its dependents. Missing masters stay unresolved. " +
                    "The resulting order is shown in the status and reports.",
                AllowSubtitleCsv = true,
                SubtitleCsvPath = _session.LoadOrder.SubtitleCsvPath,
                PrimaryFilePath = _session.FilePath
            });

        await ApplyExploreLoadOrderAsync(dialogResult);
    }

    /// <summary>Applies supplementary choices to the current primary without moving the selected pane.</summary>
    internal async Task ApplyExploreLoadOrderAsync(LoadOrderDialogResult dialogResult, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
        if (!hasEntries && !hasCsv)
        {
            _pendingLoadOrderEntries = null;
            _pendingSubtitleCsvPath = null;
            _session.LoadOrder.Dispose();
            await OnLoadOrderChanged();
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
                status => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = status),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            // Only successful publication changes the selection retained for a later reload.
            _pendingLoadOrderEntries = hasEntries ? dialogResult.Entries.ToList() : null;
            _pendingSubtitleCsvPath = hasCsv ? csvPath : null;

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
    private async Task ApplyPendingLoadOrderAsync(string primaryFilePath, CancellationToken cancellationToken)
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
            status => DispatcherQueue.TryEnqueue(() => StatusTextBlock.Text = status),
            cancellationToken);
        UpdateLoadOrderStatusText();
    }

    private async Task OnLoadOrderChanged()
    {
        // Retire old-source publishers before clearing their UI and starting the new selection.
        await _tasks.CancelAllAndDrainAsync();
        await CancelNpcViewerLoadAndDrainAsync();
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

        // Actor identities and details use the same selection as the record browser.
        _session.NpcBrowserPopulated = false;
        ResetNpcBrowser();

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

    private async Task<Core.Semantic.LoadOrder.LoadOrderSelectionView?> GetSelectedLoadOrderViewAsync()
    {
        var view = await _session.GetSelectedViewAsync();
        UpdateLoadOrderStatusText();
        return view;
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

        if (lo.SelectedView is { } selected)
        {
            LoadOrderStatusText.Text = $"{selected.Order.Entries.Count} plugins · static selection" +
                (selected.Order.MissingMasters.Count > 0 ? $" · {selected.Order.MissingMasters.Count} unresolved masters" : "");
            ToolTipService.SetToolTip(LoadOrderStatusText, string.Join(" → ", selected.Order.Entries.Select(e => e.Name)) +
                (selected.Order.MissingMasters.Count > 0 ? "\nUnresolved: " + string.Join(", ", selected.Order.MissingMasters) : ""));
            return;
        }
        ToolTipService.SetToolTip(LoadOrderStatusText, null);
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
