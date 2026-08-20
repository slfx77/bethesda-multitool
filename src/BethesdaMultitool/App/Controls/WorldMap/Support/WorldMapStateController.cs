using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

/// <summary>
///     Holds the world map's navigation state (view mode, selected worldspace/cell/object) and the transitions
///     between modes.
/// </summary>
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

    /// <summary>Binds a world view and resets to the worldspace overview.</summary>
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

    /// <summary>Clears the bound world view and all selection state.</summary>
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

    /// <summary>Switches the active worldspace by combo index (the trailing index selects the unlinked-exterior set).</summary>
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

    /// <summary>Sets the currently selected placed object.</summary>
    public void SelectObject(PlacedReference? obj)
    {
        SelectedObject = obj;
    }

    /// <summary>Enters cell-browser mode for the given browser kind, clearing worldspace and cell selection.</summary>
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

    /// <summary>Switches to cell-detail mode focused on the given cell.</summary>
    public void NavigateToCell(CellRecord cell)
    {
        SelectedCell = cell;
        SelectedObject = null;
        Mode = WorldMapControl.ViewMode.CellDetail;
        ActiveBrowser = WorldMapControl.BrowserMode.None;
    }

    /// <summary>Returns to worldspace-overview mode, leaving the active worldspace selection intact.</summary>
    public void EnsureOverviewMode()
    {
        if (Mode == WorldMapControl.ViewMode.CellDetail)
        {
            SelectedCell = null;
        }

        EnterWorldOverview();
    }

    /// <summary>Returns the cells of the active worldspace, or the unlinked-exterior set, or empty.</summary>
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

    /// <summary>Builds a grid-coordinate lookup for the active cells, or null when there are none.</summary>
    public Dictionary<(int x, int y), CellRecord>? BuildCellGridLookup()
    {
        var cells = GetActiveCells();
        if (cells.Count == 0)
        {
            return null;
        }

        return WorldMapDataBuilder.BuildCellGridLookup(cells);
    }

    /// <summary>Finds a cell by FormID, preferring the active worldspace then falling back to all cells.</summary>
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

    /// <summary>Captures the current navigation state into a serializable snapshot for back/forward history.</summary>
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

/// <summary>Result of switching worldspaces: the new worldspace's default water height, if any.</summary>
internal sealed record WorldspaceSwitchResult(float? DefaultWaterHeight);
