using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Ui;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BethesdaMultitool;

/// <summary>Hosts the existing rich record and world views inside the shared Explore workspace.</summary>
public sealed partial class SingleFileTab
{
    private bool _exploreHost;
    private bool _recoveryHost;
    private bool _exploreMaps;
    private Grid? _exploreTabStrip;
    private AnalysisSubTab _lastExploreDataTab = AnalysisSubTab.Summary;

    internal event EventHandler<bool>? ExploreMapSelected;
    internal event EventHandler<bool>? BusyChanged;
    internal bool IsBusy => _pipelinePhase != AnalysisPipelinePhase.Idle;
    internal AnalysisFileType SourceType => _session.FileType;

    /// <summary>Reformats retained presentation values without repeating analysis or changing the active scene.</summary>
    internal void RefreshLocalization()
    {
        RefreshActorDetailsText();
        _ = CompatibilityViewerLocalization.RefreshAsync(NpcModelViewer, "Viewer_SelectNpc");
    }

    /// <summary>Configures this instance as an Explore document or an independent recovery document.</summary>
    internal void ConfigureWorkspaceHost(bool recovery)
    {
        _exploreHost = !recovery;
        _recoveryHost = recovery;
        DocumentLayout.Padding = new Thickness(recovery ? 16 : 0);
        SourceInputPanel.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        ToolOptionsPanel.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        ToolOptionsColumn.MinWidth = recovery ? 220 : 0;
        ToolOptionsColumn.Width = new GridLength(recovery ? 280 : 0);
        ToolOptionsSplitterColumn.Width = new GridLength(recovery ? 8 : 0);
        ToolOptionsSplitter.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        LoadOrderButton.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        LoadOrderStatusText.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        ConfigureSubTabsForFileType(_session.FileType);
        UpdateExploreTabStrip();
    }

    /// <summary>Selects Data or Maps through the existing TabView content and render-surface lifecycle.</summary>
    internal void ShowExploreMaps(bool maps)
    {
        if (!_exploreHost || _exploreMaps == maps) return;
        if (maps && SubTabOf(SubTabView.SelectedItem as Microsoft.UI.Xaml.Controls.TabViewItem) is { } tab)
            _lastExploreDataTab = tab;
        _exploreMaps = maps;
        ConfigureSubTabsForFileType(_session.FileType);
        if (!maps) TrySelectSubTab(_lastExploreDataTab);
        UpdateExploreTabStrip();
    }

    /// <summary>Hides only the redundant Maps strip after the TabView template has been applied.</summary>
    private void ExploreSubTabView_Loaded(object sender, RoutedEventArgs args)
    {
        UpdateExploreTabStrip();
        if (_exploreTabStrip is null)
        {
            SubTabView.LayoutUpdated -= ExploreSubTabView_LayoutUpdated;
            SubTabView.LayoutUpdated += ExploreSubTabView_LayoutUpdated;
        }
    }

    /// <summary>Retires the template lookup when the owning view is unloaded.</summary>
    private void ExploreSubTabView_Unloaded(object sender, RoutedEventArgs args)
    {
        SubTabView.LayoutUpdated -= ExploreSubTabView_LayoutUpdated;
        _exploreTabStrip = null;
    }

    /// <summary>Retries only until the current TabView has created its template parts.</summary>
    private void ExploreSubTabView_LayoutUpdated(object? sender, object args) => UpdateExploreTabStrip();

    /// <summary>Preserves selected-content ownership while collapsing the pinned WinUI template's Auto strip row.</summary>
    private void UpdateExploreTabStrip()
    {
        if (_exploreTabStrip is null && VisualTreeHelper.GetChildrenCount(SubTabView) > 0 &&
            VisualTreeHelper.GetChild(SubTabView, 0) is Grid templateRoot &&
            templateRoot.Children.OfType<ContentPresenter>().Any(child => child.Name == "TabContentPresenter"))
        {
            // WinUI 2.3.6: these are sibling parts in Auto,* rows. Do not search nested map controls.
            _exploreTabStrip = templateRoot.Children.OfType<Grid>()
                .FirstOrDefault(child => child.Name == "TabContainerGrid");
        }
        if (_exploreTabStrip is not { } strip) return;
        var visibility = _exploreHost && _exploreMaps ? Visibility.Collapsed : Visibility.Visible;
        if (strip.Visibility != visibility) strip.Visibility = visibility;
        SubTabView.LayoutUpdated -= ExploreSubTabView_LayoutUpdated;
    }

    /// <summary>Quiesces derived work before clearing the previous Explore document.</summary>
    internal async Task ClearExploreSourceAsync(bool preserveLoadOrder = false)
    {
        if (IsBusy) throw new InvalidOperationException("Wait for the current analysis to finish.");
        await DialogueAudioPanel.ClearAsync();
        await _tasks.CancelAllAndDrainAsync();
        await CancelNpcViewerLoadAndDrainAsync();
        _carvedFiles.ReplaceAll([]);
        _allCarvedFiles.Clear();
        HexViewer.Clear();
        ResetSubTabs();
        _analysisResult = null;
        MinidumpPathTextBox.Text = string.Empty;
        if (!preserveLoadOrder)
        {
            _pendingLoadOrderEntries = null;
            _pendingSubtitleCsvPath = null;
        }
        ConfigureSubTabsForFileType(AnalysisFileType.Unknown);
        UpdateButtonStates();
    }

    /// <summary>Loads a selected record source through the existing analysis pipeline.</summary>
    internal async Task LoadExploreSourceAsync(string path, CancellationToken cancellationToken, bool autoOpen = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MinidumpPathTextBox.Text = path;
        UpdateButtonStates();
        var requested = autoOpen ? ResolveAutoOpenTab() : null;
        if (requested is { } tab) TrySelectSubTab(tab);
        await AnalyzeCurrentSourceAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (requested is { } view) await AutoOpenRequestedViewAsync(view);
    }
}
