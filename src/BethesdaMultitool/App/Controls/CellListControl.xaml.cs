using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace BethesdaMultitool;

/// <summary>
///     Shared searchable/filterable/grouped cell list used by the 2D viewer (All Cells + Interiors) and
///     the 3D viewer (Interiors). One control, populated with different cell sets, so all three lists look
///     identical. Building items, filtering, and grouping run off the UI thread with a debounced search
///     box so large FO3+FNV cell sets no longer freeze the app. All list logic stays in the static
///     <see cref="WorldMapCellBrowser" />; this code-behind only marshals threads and raises events.
/// </summary>
public sealed partial class CellListControl : UserControl
{
    /// <summary>How a user picks a cell: 3D uses ItemClick (only a real click loads), 2D uses selection.</summary>
    public enum ActivationMode
    {
        ItemClick,
        SelectionChanged
    }

    /// <summary>Which grouping the list uses. AllCells groups interiors/worldspaces; Interiors groups A–Z.</summary>
    public enum CellListMode
    {
        AllCells,
        Interiors
    }

    private readonly DispatcherQueueTimer _filterDebounce;
    private List<WorldMapControl.CellListItem> _allItems = [];
    private CellSortMode _sortMode = CellSortMode.Grid;
    private bool _suppressSortEvent;
    private bool _suppressFilterEvents;

    public CellListControl()
    {
        InitializeComponent();
        _filterDebounce = DispatcherQueue.CreateTimer();
        _filterDebounce.Interval = TimeSpan.FromMilliseconds(200);
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            _ = RunFilterAsync();
        };
    }

    /// <summary>Whether a cell is activated by ItemClick (3D) or by selection (2D). Defaults to selection.</summary>
    public ActivationMode Activation { get; set; } = ActivationMode.SelectionChanged;

    /// <summary>Raised when the user picks a cell (per <see cref="Activation" />).</summary>
    public event EventHandler<CellRecord>? CellActivated;

    /// <summary>Current within-group sort. Setting it syncs the combo and re-sorts without re-firing.
    ///     Internal because <see cref="CellSortMode" /> is internal (all callers are in this assembly).</summary>
    internal CellSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (_sortMode == value)
            {
                return;
            }

            _sortMode = value;
            _suppressSortEvent = true;
            SortCombo.SelectedIndex = value == CellSortMode.ObjectCount ? 1 : 0;
            _suppressSortEvent = false;
            if (_allItems.Count > 0)
            {
                _ = RunFilterAsync();
            }
        }
    }

    /// <summary>
    ///     Populates the list with <paramref name="cells" /> in the given <paramref name="mode" />. Builds
    ///     the row items off the UI thread (the expensive part for big cell sets), then filters + groups.
    /// </summary>
    internal async Task PopulateAsync(IReadOnlyList<CellRecord> cells, CellListMode mode, WorldViewData data)
    {
        var groupInteriors = mode == CellListMode.AllCells;
        var cellList = cells as List<CellRecord> ?? cells.ToList();

        // Reset search + filters without firing the change handlers (they would queue a stale re-filter).
        _suppressFilterEvents = true;
        SearchBox.Text = "";
        FilterHasObjects.IsChecked = false;
        FilterNamedOnly.IsChecked = false;
        _suppressFilterEvents = false;

        var items = await Task.Run(
            () => WorldMapCellBrowser.BuildCellListItems(cellList, groupInteriors, data));
        _allItems = items;
        await RunFilterAsync();
    }

    /// <summary>Empties the list and resets the search box + filters (e.g. when leaving browser mode).</summary>
    public void Clear()
    {
        _filterDebounce.Stop();
        _allItems = [];
        _suppressFilterEvents = true;
        SearchBox.Text = "";
        FilterHasObjects.IsChecked = false;
        FilterNamedOnly.IsChecked = false;
        _suppressFilterEvents = false;
        CellListView.ItemsSource = null;
        CountText.Text = "";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ScheduleFilter();

    private void Filter_Changed(object sender, RoutedEventArgs e) => ScheduleFilter();

    private void ScheduleFilter()
    {
        if (_suppressFilterEvents)
        {
            return;
        }

        // Debounce: re-filter ~200 ms after the last keystroke so each char doesn't spawn a sort/group.
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSortEvent)
        {
            return;
        }

        _sortMode = SortCombo.SelectedIndex == 1 ? CellSortMode.ObjectCount : CellSortMode.Grid;
        if (_allItems.Count > 0)
        {
            _ = RunFilterAsync();
        }
    }

    private async Task RunFilterAsync()
    {
        // Read the UI state on the UI thread, do the pure filter + sort + group off it, then assign the
        // ItemsSource back on the UI thread (the await resumes on the captured UI SynchronizationContext).
        var query = SearchBox.Text?.Trim() ?? "";
        var hasObjects = FilterHasObjects.IsChecked == true;
        var namedOnly = FilterNamedOnly.IsChecked == true;
        var items = _allItems;
        var sort = _sortMode;

        var (source, count) = await Task.Run(() =>
        {
            var filtered = WorldMapCellBrowser.ApplyFilters(items, query, hasObjects, namedOnly);
            return (WorldMapCellBrowser.BuildGroupedSource(filtered, sort), filtered.Count);
        });

        var cvs = new CollectionViewSource { IsSourceGrouped = true, Source = source };
        CellListView.ItemsSource = cvs.View;
        CountText.Text = $"{count} cells";
    }

    private void CellListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        // ItemClick fires only on a real user click (never on programmatic ItemsSource reassignment or
        // type-ahead), so 3D never auto-loads item 0 on open/filter.
        if (Activation != ActivationMode.ItemClick)
        {
            return;
        }

        if (e.ClickedItem is WorldMapControl.CellListItem item)
        {
            CellActivated?.Invoke(this, item.Cell);
        }
    }

    private void CellListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Activation != ActivationMode.SelectionChanged)
        {
            return;
        }

        if (CellListView.SelectedItem is WorldMapControl.CellListItem item)
        {
            CellActivated?.Invoke(this, item.Cell);
        }
    }
}
