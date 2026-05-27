using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils;

internal sealed class WorldMapStateController
{
    private WorldViewData? _data;
    private List<CellRecord>? _unlinkedCells;

    public WorldMapControl.ViewMode Mode { get; private set; } = WorldMapControl.ViewMode.WorldOverview;
    public WorldMapControl.BrowserMode ActiveBrowser { get; private set; } = WorldMapControl.BrowserMode.None;
    public WorldspaceRecord? SelectedWorldspace { get; private set; }
    public CellRecord? SelectedCell { get; private set; }
    public PlacedReference? SelectedObject { get; private set; }
    public List<PlacedReference> FilteredMarkers { get; private set; } = [];

    public void LoadData(WorldViewData data)
    {
        _data = data;
        SelectedWorldspace = null;
        _unlinkedCells = null;
        SelectedCell = null;
        SelectedObject = null;
        FilteredMarkers = [];
        EnterWorldOverview();
    }

    public void Reset()
    {
        _data = null;
        SelectedWorldspace = null;
        _unlinkedCells = null;
        SelectedCell = null;
        SelectedObject = null;
        FilteredMarkers = [];
        EnterWorldOverview();
    }

    public WorldspaceSwitchResult? SelectWorldspaceIndex(int index)
    {
        if (_data == null || index < 0)
        {
            return null;
        }

        var unlinkedIndex = _data.UnlinkedExteriorCells.Count > 0 ? _data.Worldspaces.Count : -1;
        if (index >= 0 && index < _data.Worldspaces.Count)
        {
            SelectedWorldspace = _data.Worldspaces[index];
            _unlinkedCells = null;
            FilteredMarkers = _data.MarkersByWorldspace.GetValueOrDefault(SelectedWorldspace.FormId) ?? [];
            ApplyWorldspaceSwitch();
            return new WorldspaceSwitchResult(SelectedWorldspace.DefaultWaterHeight);
        }

        if (index == unlinkedIndex)
        {
            SelectedWorldspace = null;
            _unlinkedCells = _data.UnlinkedExteriorCells;
            FilteredMarkers = _data.UnlinkedMapMarkers;
            ApplyWorldspaceSwitch();
            return new WorldspaceSwitchResult(null);
        }

        return null;
    }

    public void SelectObject(PlacedReference? obj)
    {
        SelectedObject = obj;
    }

    public void EnterBrowser(WorldMapControl.BrowserMode browser)
    {
        SelectedWorldspace = null;
        _unlinkedCells = null;
        Mode = WorldMapControl.ViewMode.CellBrowser;
        ActiveBrowser = browser;
        SelectedCell = null;
        SelectedObject = null;
        FilteredMarkers = [];
    }

    public void NavigateToCell(CellRecord cell)
    {
        SelectedCell = cell;
        SelectedObject = null;
        Mode = WorldMapControl.ViewMode.CellDetail;
        ActiveBrowser = WorldMapControl.BrowserMode.None;
    }

    public void EnsureOverviewMode()
    {
        if (Mode == WorldMapControl.ViewMode.CellDetail)
        {
            SelectedCell = null;
        }

        EnterWorldOverview();
    }

    public List<CellRecord> GetActiveCells()
    {
        if (SelectedWorldspace != null)
        {
            return SelectedWorldspace.Cells;
        }

        if (_unlinkedCells != null)
        {
            return _unlinkedCells;
        }

        return [];
    }

    public Dictionary<(int x, int y), CellRecord>? BuildCellGridLookup()
    {
        var cells = GetActiveCells();
        if (cells.Count == 0)
        {
            return null;
        }

        return WorldMapDataBuilder.BuildCellGridLookup(cells);
    }

    public CellRecord? FindCellByFormId(uint formId)
    {
        if (SelectedWorldspace != null)
        {
            var cell = SelectedWorldspace.Cells.Find(c => c.FormId == formId);
            if (cell != null)
            {
                return cell;
            }
        }

        return _data?.AllCells.Find(c => c.FormId == formId);
    }

    public WorldMapControl.WorldNavState Capture(int worldspaceComboIndex)
    {
        return new WorldMapControl.WorldNavState(
            Mode,
            ActiveBrowser,
            worldspaceComboIndex,
            SelectedCell?.FormId);
    }

    private void ApplyWorldspaceSwitch()
    {
        EnterWorldOverview();
        SelectedCell = null;
        SelectedObject = null;
    }

    private void EnterWorldOverview()
    {
        Mode = WorldMapControl.ViewMode.WorldOverview;
        ActiveBrowser = WorldMapControl.BrowserMode.None;
    }
}

internal sealed record WorldspaceSwitchResult(float? DefaultWaterHeight);
