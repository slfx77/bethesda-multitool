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

    // ── Worldspace browser (searchable counterpart of the toolbar ComboBox; the same
    //    WorldspaceListControl the 3D viewer hosts) ──────────────────────────────────────────────

    private void WorldspacesButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorldspaceComboBox.Items.Count == 0) return;
        WorldspaceBrowserPanel.Visibility = Visibility.Visible;
    }

    private void WorldspaceBrowserCloseButton_Click(object sender, RoutedEventArgs e) =>
        HideWorldspaceBrowser();

    private void HideWorldspaceBrowser() =>
        WorldspaceBrowserPanel.Visibility = Visibility.Collapsed;

    /// <summary>
    ///     Applies a pick by driving the ComboBox, so the existing SelectionChanged path does the
    ///     actual switch (state, bitmaps, zoom-to-fit) and this adds no second way to change worlds.
    ///     Cell-browser mode clears the combo selection on entry, so a pick from there always fires
    ///     the switch and returns the control to canvas mode.
    /// </summary>
    private void WorldspaceList_WorldspaceActivated(object? sender, int comboIndex)
    {
        HideWorldspaceBrowser();
        if (comboIndex < 0 || comboIndex >= WorldspaceComboBox.Items.Count) return;
        WorldspaceComboBox.SelectedIndex = comboIndex;
    }
}
