using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils;

internal sealed class WorldMapNavigationController
{
    public WorldMapControl.ViewMode Mode { get; private set; } = WorldMapControl.ViewMode.WorldOverview;
    public WorldMapControl.BrowserMode ActiveBrowser { get; private set; } = WorldMapControl.BrowserMode.None;

    public void Reset()
    {
        EnterWorldOverview();
    }

    public void EnterWorldOverview()
    {
        Mode = WorldMapControl.ViewMode.WorldOverview;
        ActiveBrowser = WorldMapControl.BrowserMode.None;
    }

    public void EnterBrowser(WorldMapControl.BrowserMode browser)
    {
        Mode = WorldMapControl.ViewMode.CellBrowser;
        ActiveBrowser = browser;
    }

    public void EnterCellDetail()
    {
        Mode = WorldMapControl.ViewMode.CellDetail;
        ActiveBrowser = WorldMapControl.BrowserMode.None;
    }

    public bool ReturnToOverviewFromDetail()
    {
        if (Mode != WorldMapControl.ViewMode.CellDetail)
        {
            return false;
        }

        EnterWorldOverview();
        return true;
    }

    public WorldMapControl.WorldNavState Capture(int worldspaceComboIndex, CellRecord? selectedCell)
    {
        return new WorldMapControl.WorldNavState(
            Mode,
            ActiveBrowser,
            worldspaceComboIndex,
            selectedCell?.FormId);
    }
}
