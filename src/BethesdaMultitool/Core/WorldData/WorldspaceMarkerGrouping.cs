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
    // that hang off the main wasteland. Fold transformed copies into the parent's list so the 2D map surfaces
    // them when the parent worldspace is selected (WorldMapStateController keys off SelectedWorldspace). The
    // child keeps its original entry, so viewing the child directly is unaffected.
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

            parentMarkers.AddRange(childMarkers.Select(marker => TransformMarkerToParentMap(child, marker)));
        }
    }

    /// <summary>
    ///     Applies FNV/FO3 <c>TESWorldSpace::AdjustMapMarkerCoord(..., false)</c>: scale about the center of
    ///     the child's NAM0/NAM9 bounds, then add the ONAM X/Y offsets. Scale 0 is a sentinel that skips
    ///     scaling (the same as the engine), while absent ONAM data has the initialized 1/0/0 defaults.
    /// </summary>
    internal static PlacedReference TransformMarkerToParentMap(
        WorldspaceRecord child,
        PlacedReference marker)
    {
        // FNV/FO3 ONAM is { World Map Scale, X Offset, Y Offset }. These model property names predate that
        // recovered layout, but retain the three floats in their on-disk order.
        var scale = child.MapOffsetScaleX ?? 1.0f;
        var offsetX = child.MapOffsetScaleY ?? 0.0f;
        var offsetY = child.MapOffsetZ ?? 0.0f;

        var x = marker.X;
        var y = marker.Y;
        var z = marker.Z;
#pragma warning disable S1244 // Exact sentinel comparison mirrors the engine's own float equality (0 skips scaling, 1 is identity)
        if (scale != 1.0f && scale != 0.0f)
#pragma warning restore S1244
        {
            var centerX = ((child.BoundsMinX ?? 0.0f) + (child.BoundsMaxX ?? 0.0f)) * 0.5f;
            var centerY = ((child.BoundsMinY ?? 0.0f) + (child.BoundsMaxY ?? 0.0f)) * 0.5f;
            x = centerX + ((x - centerX) * scale);
            y = centerY + ((y - centerY) * scale);
            z *= scale;
        }

        return marker with
        {
            X = x + offsetX,
            Y = y + offsetY,
            Z = z
        };
    }
}
