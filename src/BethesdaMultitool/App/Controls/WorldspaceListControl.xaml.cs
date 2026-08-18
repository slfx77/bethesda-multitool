using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Searchable worldspace picker for the 3D viewer, the counterpart of <see cref="CellListControl" />.
///     The toolbar ComboBox lists every worldspace in load order, which is workable for Fallout's
///     handful and unusable for Starfield's ~750; this control adds search, a has-cells filter, and a
///     name/size sort over the SAME entries, so a pick maps straight back to a combo index.
/// </summary>
public sealed partial class WorldspaceListControl : UserControl
{
    private readonly DispatcherQueueTimer _filterDebounce;
    private List<WorldspaceListItem> _allItems = [];

    public WorldspaceListControl()
    {
        InitializeComponent();
        _filterDebounce = DispatcherQueue.CreateTimer();
        _filterDebounce.Interval = TimeSpan.FromMilliseconds(200);
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            RunFilter();
        };
    }

    /// <summary>Raised with the picked worldspace's index into the owning ComboBox.</summary>
    public event EventHandler<int>? WorldspaceActivated;

    /// <summary>
    ///     One row. <see cref="ComboIndex" /> is the authoritative link back to the toolbar picker —
    ///     the list is re-sorted and filtered, so row order never matches combo order.
    /// </summary>
    public sealed record WorldspaceListItem(
        int ComboIndex,
        string EditorId,
        string DisplayName,
        int CellCount)
    {
        public string CellCountLabel => CellCount > 0 ? $"{CellCount}" : "—";
    }

    /// <summary>Replaces the rows. <paramref name="items" /> must be in ComboBox order.</summary>
    public void Populate(IReadOnlyList<WorldspaceListItem> items)
    {
        _filterDebounce.Stop();
        _allItems = [.. items];
        SearchBox.Text = "";
        RunFilter();
    }

    /// <summary>Empties the list (e.g. when a new file is loaded).</summary>
    public void Clear()
    {
        _filterDebounce.Stop();
        _allItems = [];
        SearchBox.Text = "";
        WorldspaceListView.ItemsSource = null;
        CountText.Text = "";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Debounce like the cell browser: a 750-row re-sort per keystroke is visible jank.
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => RunFilter();

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RunFilter();

    private void RunFilter()
    {
        // SortCombo carries SelectedIndex="0", and WinUI raises SelectionChanged while the document
        // is still being parsed — before the x:Name fields declared after it (CountText,
        // WorldspaceListView) have been assigned. The resulting NullReferenceException is swallowed
        // and resurfaces as an opaque "Cannot create instance of type 'WorldspaceListControl'"
        // XamlParseException, so guard rather than relying on declaration order.
        if (SearchBox is null || FilterHasCells is null || SortCombo is null ||
            WorldspaceListView is null || CountText is null)
        {
            return;
        }

        var query = SearchBox.Text?.Trim() ?? "";
        var hasCellsOnly = FilterHasCells.IsChecked == true;
        var byCellCount = SortCombo.SelectedIndex == 1;

        IEnumerable<WorldspaceListItem> filtered = _allItems;
        if (hasCellsOnly)
        {
            filtered = filtered.Where(i => i.CellCount > 0);
        }

        if (query.Length > 0)
        {
            // Match either identifier — the EditorID is what a modder searches for ("akilacity"),
            // the display name is what a player recognises ("Akila City").
            filtered = filtered.Where(i =>
                i.EditorId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var result = byCellCount
            ? filtered.OrderByDescending(i => i.CellCount).ThenBy(i => i.EditorId, StringComparer.OrdinalIgnoreCase).ToList()
            : filtered.OrderBy(i => i.EditorId, StringComparer.OrdinalIgnoreCase).ToList();

        WorldspaceListView.ItemsSource = result;
        CountText.Text = $"{result.Count} worldspaces";
    }

    private void WorldspaceListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        // ItemClick, not SelectionChanged: only a real click loads a worldspace, so re-filtering
        // never auto-activates row 0 (the same trap CellListControl documents).
        if (e.ClickedItem is WorldspaceListItem item)
        {
            WorldspaceActivated?.Invoke(this, item.ComboIndex);
        }
    }
}
