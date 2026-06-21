using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>Cell-browser list: Interiors / All Cells modes, filtering, sorting, and selection.</summary>
public sealed partial class WorldMapControl
{
    private void InteriorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null || _data.InteriorCells.Count == 0) return;
        EnterBrowserMode(BrowserMode.Interiors);
        PopulateInteriorCellBrowser();
    }

    private void AllCellsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;
        EnterBrowserMode(BrowserMode.AllCells);
        PopulateCellBrowser();
    }

    private void EnterBrowserMode(BrowserMode browser)
    {
        NotifyBeforeNavigate();
        _state.EnterBrowser(browser);
        SetCanvasMode(false);
        ExportButton.IsEnabled = false;
        ClearWorldspaceSelection();
        FilterHasObjects.IsChecked = false;
        FilterNamedOnly.IsChecked = false;
    }

    private void CellFilter_Changed(object sender, RoutedEventArgs e) => ApplyCellFilters();

    private void CellSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyCellFilters();

    private void CellSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _cellSortMode = CellSortCombo.SelectedIndex == 1 ? CellSortMode.ObjectCount : CellSortMode.Grid;
        if (_allCellItems.Count > 0) ApplyCellFilters();
    }

    private void ApplyCellFilters()
    {
        var query = CellSearchBox.Text?.Trim() ?? "";
        var hasObjects = FilterHasObjects.IsChecked == true;
        var namedOnly = FilterNamedOnly.IsChecked == true;
        var filtered = WorldMapCellBrowser.ApplyFilters(_allCellItems, query, hasObjects, namedOnly);
        RebuildCellListFromItems(filtered);
    }

    private void PopulateCellBrowser()
    {
        if (_data == null) return;
        _allCellItems = WorldMapCellBrowser.BuildCellListItems(_data.AllCells, groupInteriors: true, _data);
        CellSearchBox.Text = "";
        RebuildCellListFromItems(_allCellItems);
    }

    private void PopulateInteriorCellBrowser()
    {
        if (_data == null) return;
        _allCellItems = WorldMapCellBrowser.BuildCellListItems(_data.InteriorCells, groupInteriors: false, _data);
        CellSearchBox.Text = "";
        RebuildCellListFromItems(_allCellItems);
    }

    private void RebuildCellListFromItems(List<CellListItem> items)
    {
        var source = WorldMapCellBrowser.BuildGroupedSource(items, _cellSortMode);
        var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource
        {
            IsSourceGrouped = true, Source = source
        };
        CellListView.ItemsSource = cvs.View;
        HoverInfoText.Text = $"{items.Count} cells";
        ZoomLevelText.Text = "";
        CoordsText.Text = "";
    }

    private void CellListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CellListView.SelectedItem is CellListItem item)
        {
            InspectCell?.Invoke(this, item.Cell);
        }
    }
}
