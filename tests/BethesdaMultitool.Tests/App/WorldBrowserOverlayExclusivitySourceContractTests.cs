using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Pins the mutual exclusivity of the two world-viewer browser overlays (the searchable worldspace
///     list and the shared cell list).
///     <para>
///         Both are full-bleed overlays in the SAME layout cell, and the worldspace browser is declared
///         last so it stacks above the cell browser. When they were independent, opening the 3D viewer's
///         worldspace browser and then pressing All Cells / Interiors put the new list BEHIND it (the
///         button looked dead), and closing the worldspace browser revealed that list instead of the 3D
///         view — so the only way back to the viewport was picking a worldspace, which is the one path
///         that collapsed both. These contracts keep each open path collapsing the other.
///     </para>
/// </summary>
public sealed class WorldBrowserOverlayExclusivitySourceContractTests
{
    [Fact]
    public void ThreeDViewerCollapsesTheOtherOverlayOnEveryOpenPath()
    {
        var cells = SourceContract.ReadAppSource("WorldView3DControl.Cells.cs");

        // Both cell-list entry points route through one helper that hides the worldspace browser first.
        var show = SourceContract.Extract(
            cells, "private void ShowCellBrowser(string header)", "private void CellBrowserCloseButton_Click");
        SourceContract.AssertOrder(
            show,
            "HideWorldspaceBrowser();",
            "CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;");
        Assert.Contains("ShowCellBrowser(\"Interior cells\");", cells, StringComparison.Ordinal);
        Assert.Contains("ShowCellBrowser(\"All cells\");", cells, StringComparison.Ordinal);
        Assert.Equal(1, SourceContract.CountOccurrences(
            cells, "CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;"));

        // And the worldspace browser collapses the cell browser before it shows.
        var worldspaces = SourceContract.Extract(
            cells,
            "private void WorldspacesButton_Click",
            "private void WorldspaceBrowserCloseButton_Click");
        SourceContract.AssertOrder(
            worldspaces,
            "HideInteriorBrowser();",
            "WorldspaceBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;");
    }

    [Fact]
    public void TwoDMapCollapsesTheWorldspaceBrowserWhenEnteringCellBrowserMode()
    {
        var browser = SourceContract.ReadAppSource("WorldMapControl.CellBrowser.cs");

        // 2D is deliberately one-directional: the cell list REPLACED the canvas here, so the worldspace
        // browser legitimately overlays it and closing returns to the list rather than an empty canvas.
        var enter = SourceContract.Extract(
            browser, "private void EnterBrowserMode(BrowserMode browser)", "private void WorldspacesButton_Click");
        SourceContract.AssertOrder(
            enter,
            "HideWorldspaceBrowser();",
            "SetCanvasMode(false);");
    }

    [Fact]
    public void OpeningABrowserMovesFocusIntoItsSearchBox()
    {
        // The 3D viewport binds WASD/Q/E to the flythrough camera, so an unfocused search box means
        // typing flies the camera behind the list instead of filtering it.
        foreach (var control in new[] { "CellListControl.xaml.cs", "WorldspaceListControl.xaml.cs" })
        {
            Assert.Contains(
                "public void FocusSearch() => SearchBox.Focus(FocusState.Programmatic);",
                SourceContract.ReadAppSource(control),
                StringComparison.Ordinal);
        }

        Assert.Contains(
            "CellList.FocusSearch();",
            SourceContract.ReadAppSource("WorldView3DControl.Cells.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "WorldspaceList.FocusSearch();",
            SourceContract.ReadAppSource("WorldView3DControl.Cells.cs"),
            StringComparison.Ordinal);
        Assert.Contains(
            "WorldspaceList.FocusSearch();",
            SourceContract.ReadAppSource("WorldMapControl.CellBrowser.cs"),
            StringComparison.Ordinal);
    }
}
