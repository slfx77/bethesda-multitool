using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>
///     Groups worldspace map markers by their owning worldspace for the 2D world map. Pure computation over
///     Core record models (no UI dependency), so it lives under Core/ and is headless-unit-testable; the
///     WinUI <c>WorldMapOverlayBuilder</c> delegates here.
/// </summary>
internal static class WorldspaceMarkerGrouping
{
    /// <summary>
    ///     PNAM "sParentUseFlags" bit 2 = "Use Map Data" (xEdit <c>wbDefinitionsFNV</c>): the worldspace
    ///     shares its parent's world-map image instead of having its own.
    /// </summary>
    private const ushort UseMapDataParentFlag = 0x0004;

    /// <summary>
    ///     Build a map of worldspace FormID → its map markers (the markers placed in that worldspace's own
    ///     cells), then fold in markers inherited from child worldspaces that "Use Map Data" (see
    ///     <see cref="AppendChildMarkersToParents" />).
    /// </summary>
    public static Dictionary<uint, List<PlacedReference>> GroupByWorldspace(
        List<WorldspaceRecord> worldspaces)
    {
        var markersByWorldspace = new Dictionary<uint, List<PlacedReference>>();
        foreach (var ws in worldspaces)
        {
            var wsMarkers = new List<PlacedReference>();
            foreach (var cell in ws.Cells)
            {
                wsMarkers.AddRange(cell.PlacedObjects.Where(o => o.IsMapMarker));
            }

            if (wsMarkers.Count > 0)
            {
                markersByWorldspace[ws.FormId] = wsMarkers;
            }
        }

        AppendChildMarkersToParents(worldspaces, markersByWorldspace);
        return markersByWorldspace;
    }

    // A child worldspace flagged "Use Map Data" (WNAM parent + PNAM bit 2) is drawn on its parent's world
    // map, so the engine shows the child's map markers on the parent's map — e.g. the FNV/FO3 sub-worldspaces
    // that hang off the main wasteland. Fold each such child's markers into the parent's list so the 2D map
    // surfaces them when the parent worldspace is selected (WorldMapStateController keys off SelectedWorldspace).
    // Coordinates are taken 1:1: every shipped FNV/FO3/Oblivion "Use Map Data" child shares the parent's
    // coordinate space. The rare child whose map image is repositioned would transform here via ONAM map
    // offset/scale (WorldspaceRecord.MapOffsetScale*/MapOffsetZ); that is deferred. The child keeps its own
    // entry, so viewing the child directly is unaffected.
    private static void AppendChildMarkersToParents(
        List<WorldspaceRecord> worldspaces,
        Dictionary<uint, List<PlacedReference>> markersByWorldspace)
    {
        foreach (var child in worldspaces)
        {
            var flags = child.ParentUseFlags ?? 0;
            if (child.ParentWorldspaceFormId is not { } parentId || parentId == 0 ||
                (flags & UseMapDataParentFlag) == 0)
            {
                continue;
            }

            if (!markersByWorldspace.TryGetValue(child.FormId, out var childMarkers) || childMarkers.Count == 0)
            {
                continue;
            }

            if (!markersByWorldspace.TryGetValue(parentId, out var parentMarkers))
            {
                parentMarkers = [];
                markersByWorldspace[parentId] = parentMarkers;
            }

            parentMarkers.AddRange(childMarkers);
        }
    }
}
