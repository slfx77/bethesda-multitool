using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Hit testing logic for the world map: object bounds, placed objects,
///     map markers, and click handling.
/// </summary>
internal static class WorldMapHitTester
{
    private const float CellWorldSize = 4096f;

    /// <summary>Maximum half-extent for hit testing (matches rendering clamp).</summary>
    private const float MaxHalfExtent = 2048f;

    /// <summary>
    ///     Bounding area threshold (in square world units) above which an object is considered
    ///     "large" for hit testing purposes. Large objects are deprioritized so smaller objects
    ///     beneath them remain clickable.
    /// </summary>
    private const float LargeBoundsAreaThreshold = 500f * 500f;

    /// <summary>
    ///     Tests whether worldPos hits the object's visual bounds (rotated AABB or circle fallback).
    ///     Returns distance to object center if hit, float.MaxValue otherwise.
    /// </summary>
    internal static float HitTestObjectBounds(
        Vector2 worldPos, PlacedReference obj, WorldViewData data, float zoom)
    {
        var pos = new Vector2(obj.X, -obj.Y);

        if (data.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds))
        {
            var halfW = Math.Min((bounds.X2 - bounds.X1) * 0.5f * obj.Scale, MaxHalfExtent);
            var halfH = Math.Min((bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale, MaxHalfExtent);

            if (halfW >= 1f || halfH >= 1f)
            {
                // Transform test point into object's local (unrotated) space
                var inverseRotation = Matrix3x2.CreateRotation(obj.RotZ, pos);
                var localPoint = Vector2.Transform(worldPos, inverseRotation);

                // Add small padding for usability (5 screen pixels)
                var pad = 5f / zoom;
                if (localPoint.X >= pos.X - halfW - pad && localPoint.X <= pos.X + halfW + pad &&
                    localPoint.Y >= pos.Y - halfH - pad && localPoint.Y <= pos.Y + halfH + pad)
                {
                    return Vector2.Distance(worldPos, pos);
                }

                return float.MaxValue;
            }
        }

        // No valid OBND -- fallback to circle
        var dist = Vector2.Distance(worldPos, pos);
        return dist <= 12f / zoom ? dist : float.MaxValue;
    }

    /// <summary>
    ///     Returns the clamped bounding area (halfW * halfH) for an object.
    ///     Objects with no bounds or point-like bounds return 0 (smallest possible).
    /// </summary>
    private static float GetBoundsArea(PlacedReference obj, WorldViewData data)
    {
        if (data.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds))
        {
            var halfW = Math.Min((bounds.X2 - bounds.X1) * 0.5f * obj.Scale, MaxHalfExtent);
            var halfH = Math.Min((bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale, MaxHalfExtent);
            return halfW * halfH;
        }

        return 0f;
    }

    internal static PlacedReference? HitTestPlacedObject(
        Vector2 worldPos, CellRecord? selectedCell, WorldViewData data,
        HashSet<PlacedObjectCategory> hiddenCategories, bool hideDisabledActors, float zoom)
    {
        if (selectedCell == null)
        {
            return null;
        }

        // Two-pass: prefer normal-sized objects over large-bounds objects so that
        // items beneath oversized bounding boxes remain clickable.
        PlacedReference? closestSmall = null;
        var closestSmallDist = float.MaxValue;
        PlacedReference? closestLarge = null;
        var closestLargeDist = float.MaxValue;

        foreach (var obj in selectedCell.PlacedObjects)
        {
            if (hiddenCategories.Contains(WorldMapOverviewRenderer.GetObjectCategory(obj, data)))
            {
                continue;
            }

            if (hideDisabledActors && obj.IsInitiallyDisabled)
            {
                continue;
            }

            var dist = HitTestObjectBounds(worldPos, obj, data, zoom);
            if (dist >= float.MaxValue)
            {
                continue;
            }

            if (GetBoundsArea(obj, data) > LargeBoundsAreaThreshold)
            {
                if (dist < closestLargeDist)
                {
                    closestLargeDist = dist;
                    closestLarge = obj;
                }
            }
            else
            {
                if (dist < closestSmallDist)
                {
                    closestSmallDist = dist;
                    closestSmall = obj;
                }
            }
        }

        return closestSmall ?? closestLarge;
    }

    internal static PlacedReference? HitTestPlacedObjectInOverview(
        Vector2 worldPos, WorldViewData data,
        List<CellRecord> activeCells,
        Dictionary<(int x, int y), CellRecord>? cellGridLookup,
        HashSet<PlacedObjectCategory> hiddenCategories,
        bool hideDisabledActors, float zoom)
    {
        if (zoom < 0.02f || activeCells.Count == 0)
        {
            return null;
        }

        var useBounds = zoom > 0.07f;
        var hitRadius = 30f / zoom;

        // Two-pass: prefer normal-sized objects over large-bounds objects so that
        // items beneath oversized bounding boxes remain clickable.
        PlacedReference? closestSmall = null;
        var closestSmallDist = float.MaxValue;
        PlacedReference? closestLarge = null;
        var closestLargeDist = float.MaxValue;

        // Only check cells near the cursor (3x3 grid around cursor cell)
        var cellX = (int)Math.Floor(worldPos.X / CellWorldSize);
        var cellY = (int)Math.Floor(-worldPos.Y / CellWorldSize);

        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (cellGridLookup?.TryGetValue((cellX + dx, cellY + dy), out var cell) != true)
                {
                    continue;
                }

                foreach (var obj in cell!.PlacedObjects)
                {
                    if (hiddenCategories.Contains(WorldMapOverviewRenderer.GetObjectCategory(obj, data)))
                    {
                        continue;
                    }

                    if (hideDisabledActors && obj.IsInitiallyDisabled)
                    {
                        continue;
                    }

                    // At low zoom, only actors and map markers are rendered
                    if (zoom < 0.05f && obj.RecordType is not ("ACHR" or "ACRE") && !obj.IsMapMarker)
                    {
                        continue;
                    }

                    ClassifyHit(worldPos, obj, data, useBounds, hitRadius, zoom,
                        ref closestSmall, ref closestSmallDist,
                        ref closestLarge, ref closestLargeDist);
                }
            }
        }

        // Also check persistent cells
        foreach (var cell in activeCells)
        {
            if (cell.GridX.HasValue && cell.GridY.HasValue && !cell.HasPersistentObjects)
            {
                continue;
            }

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.IsMapMarker ||
                    hiddenCategories.Contains(WorldMapOverviewRenderer.GetObjectCategory(obj, data)))
                {
                    continue;
                }

                if (hideDisabledActors && obj.IsInitiallyDisabled)
                {
                    continue;
                }

                // At low zoom, only actors are rendered via DrawActorDots
                if (zoom < 0.05f && obj.RecordType is not ("ACHR" or "ACRE"))
                {
                    continue;
                }

                ClassifyHit(worldPos, obj, data, useBounds, hitRadius, zoom,
                    ref closestSmall, ref closestSmallDist,
                    ref closestLarge, ref closestLargeDist);
            }
        }

        return closestSmall ?? closestLarge;
    }

    /// <summary>
    ///     Tests a single object for a hit and classifies it as small or large bounds,
    ///     updating the respective closest candidate.
    /// </summary>
    private static void ClassifyHit(
        Vector2 worldPos, PlacedReference obj, WorldViewData data,
        bool useBounds, float hitRadius, float zoom,
        ref PlacedReference? closestSmall, ref float closestSmallDist,
        ref PlacedReference? closestLarge, ref float closestLargeDist)
    {
        float dist;
        if (useBounds)
        {
            dist = HitTestObjectBounds(worldPos, obj, data, zoom);
        }
        else
        {
            var objPos = new Vector2(obj.X, -obj.Y);
            dist = Vector2.Distance(worldPos, objPos);
            if (dist >= hitRadius)
            {
                dist = float.MaxValue;
            }
        }

        if (dist >= float.MaxValue)
        {
            return;
        }

        if (GetBoundsArea(obj, data) > LargeBoundsAreaThreshold)
        {
            if (dist < closestLargeDist)
            {
                closestLargeDist = dist;
                closestLarge = obj;
            }
        }
        else
        {
            if (dist < closestSmallDist)
            {
                closestSmallDist = dist;
                closestSmall = obj;
            }
        }
    }

    internal static PlacedReference? HitTestMapMarker(
        Vector2 worldPos, List<PlacedReference> filteredMarkers,
        HashSet<PlacedObjectCategory> hiddenCategories, float zoom)
    {
        if (filteredMarkers.Count == 0 || hiddenCategories.Contains(PlacedObjectCategory.MapMarker))
        {
            return null;
        }

        PlacedReference? closest = null;
        var closestDist = float.MaxValue;
        var hitRadius = 20f / zoom;

        foreach (var marker in filteredMarkers)
        {
            var markerPos = new Vector2(marker.X, -marker.Y);
            var dist = Vector2.Distance(worldPos, markerPos);

            if (dist < hitRadius && dist < closestDist)
            {
                closestDist = dist;
                closest = marker;
            }
        }

        return closest;
    }

    /// <summary>
    ///     Handles a click at the given screen position. Returns the action to take.
    /// </summary>
    internal static ClickResult HandleClick(
        Vector2 screenPos,
        WorldMapControl.ViewMode mode,
        WorldViewData? data,
        List<CellRecord> activeCells,
        CellRecord? selectedCell,
        List<PlacedReference> filteredMarkers,
        Dictionary<(int x, int y), CellRecord>? cellGridLookup,
        HashSet<PlacedObjectCategory> hiddenCategories,
        bool hideDisabledActors,
        float zoom, Vector2 panOffset)
    {
        if (data == null)
        {
            return ClickResult.None;
        }

        var worldPos = WorldMapViewportHelper.ScreenToWorld(screenPos, zoom, panOffset);

        if (mode == WorldMapControl.ViewMode.WorldOverview)
        {
            // Check map markers first (they're drawn on top)
            var marker = HitTestMapMarker(worldPos, filteredMarkers, hiddenCategories, zoom);
            if (marker != null)
            {
                return ClickResult.InspectObject(marker);
            }

            // Check placed objects
            var obj = HitTestPlacedObjectInOverview(worldPos, data, activeCells, cellGridLookup,
                hiddenCategories, hideDisabledActors, zoom);
            if (obj != null)
            {
                return ClickResult.InspectObject(obj);
            }

            // Find cell at click position
            if (activeCells.Count > 0)
            {
                var cellX = (int)Math.Floor(worldPos.X / CellWorldSize);
                var cellY = (int)Math.Floor(-worldPos.Y / CellWorldSize);

                if (cellGridLookup != null &&
                    cellGridLookup.TryGetValue((cellX, cellY), out var cell))
                {
                    return ClickResult.InspectCell(cell);
                }
            }
        }
        else if (mode == WorldMapControl.ViewMode.CellDetail && selectedCell != null)
        {
            var hitObj = HitTestPlacedObject(worldPos, selectedCell, data,
                hiddenCategories, hideDisabledActors, zoom);
            if (hitObj != null)
            {
                return ClickResult.InspectObject(hitObj);
            }

            // Click on empty space: deselect object, show cell info
            return ClickResult.DeselectAndShowCell(selectedCell);
        }

        return ClickResult.None;
    }

    /// <summary>
    ///     Computes hover state for the world overview mode. Returns the hovered object
    ///     (if any) and a status bar text string.
    /// </summary>
    internal static HoverResult ProcessOverviewHover(
        Vector2 worldPos,
        WorldViewData data,
        List<CellRecord> activeCells,
        List<PlacedReference> filteredMarkers,
        Dictionary<(int x, int y), CellRecord>? cellGridLookup,
        HashSet<PlacedObjectCategory> hiddenCategories,
        bool hideDisabledActors, float zoom)
    {
        // Check map markers first
        var marker = HitTestMapMarker(worldPos, filteredMarkers, hiddenCategories, zoom);
        if (marker != null)
        {
            var markerName = marker.MarkerName ?? "Unknown";
            var markerType = marker.MarkerType?.ToString() ?? "";
            return new HoverResult($"Marker: {markerName} ({markerType})", null, true);
        }

        // Check placed objects
        var hitObj = HitTestPlacedObjectInOverview(worldPos, data, activeCells, cellGridLookup,
            hiddenCategories, hideDisabledActors, zoom);
        if (hitObj != null)
        {
            var name = hitObj.BaseEditorId ?? $"0x{hitObj.BaseFormId:X8}";
            return new HoverResult(
                $"{hitObj.RecordType}: {name} at ({hitObj.X:F0}, {hitObj.Y:F0}, {hitObj.Z:F0})",
                hitObj, true);
        }

        // Show cell info
        var cellX = (int)Math.Floor(worldPos.X / CellWorldSize);
        var cellY = (int)Math.Floor(-worldPos.Y / CellWorldSize);
        if (cellGridLookup?.TryGetValue((cellX, cellY), out var cell) == true)
        {
            var cellName = cell.EditorId ?? cell.FullName ?? "";
            return new HoverResult(
                $"Cell [{cellX}, {cellY}] {cellName} \u2014 {cell.PlacedObjects.Count} objects",
                null, true);
        }

        return new HoverResult($"Cell [{cellX}, {cellY}]", null, false);
    }

    internal readonly struct HoverResult(string statusText, PlacedReference? hoveredObject, bool isInteractive)
    {
        internal string StatusText { get; } = statusText;
        internal PlacedReference? HoveredObject { get; } = hoveredObject;
        internal bool IsInteractive { get; } = isInteractive;
    }

    internal readonly struct ClickResult
    {
        internal enum ClickAction
        {
            Nothing,
            ShowObject,
            ShowCell,
            DeselectAndShowCell
        }

        internal ClickAction Action { get; init; }
        internal PlacedReference? Object { get; init; }
        internal CellRecord? Cell { get; init; }

        internal static ClickResult None => new() { Action = ClickAction.Nothing };

        internal static ClickResult InspectObject(PlacedReference obj) =>
            new() { Action = ClickAction.ShowObject, Object = obj };

        internal static ClickResult InspectCell(CellRecord cell) =>
            new() { Action = ClickAction.ShowCell, Cell = cell };

        internal static ClickResult DeselectAndShowCell(CellRecord cell) =>
            new() { Action = ClickAction.DeselectAndShowCell, Cell = cell };
    }
}
