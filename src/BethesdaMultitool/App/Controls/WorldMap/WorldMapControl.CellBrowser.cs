using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>Cell-browser list: Interiors / All Cells modes, filtering, sorting, and selection.</summary>
public sealed partial class WorldMapControl
{
    private async void InteriorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null || _data.InteriorCells.Count == 0) return;
        EnterBrowserMode(BrowserMode.Interiors);
        await CellList.PopulateAsync(_data.InteriorCells, CellListControl.CellListMode.Interiors, _data);
    }

    private async void AllCellsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;
        EnterBrowserMode(BrowserMode.AllCells);
        await CellList.PopulateAsync(_data.AllCells, CellListControl.CellListMode.AllCells, _data);
    }

    private void EnterBrowserMode(BrowserMode browser)
    {
        NotifyBeforeNavigate();
        _state.EnterBrowser(browser);
        SetCanvasMode(false);
        ExportButton.IsEnabled = false;
        ClearWorldspaceSelection();
        // The canvas status fields are stale while the cell list is shown (the list reports its own count).
        ZoomLevelText.Text = "";
        CoordsText.Text = "";
    }
}
