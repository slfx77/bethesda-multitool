using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     Captures the current location + selection as a <see cref="WorldViewFocus" /> so the 2D map can
    ///     resume in the same area when the user switches views. Exterior: the camera's ground XY (the
    ///     shared world frame that <c>PlacedReference.X/Y</c> uses). Interior: the loaded interior cell.
    /// </summary>
    internal WorldViewFocus CaptureViewFocus()
    {
        if (_selectedInterior is { } interior)
        {
            return new WorldViewFocus(-1, true, interior, 0f, 0f, _selectedReference, CellList.SortMode);
        }

        var pos = _camera.Position;
        return new WorldViewFocus(
            WorldspaceComboBox.SelectedIndex, false, null, pos.X, pos.Y, _selectedReference, CellList.SortMode);
    }

    /// <summary>
    ///     Resumes the view at a <see cref="WorldViewFocus" /> captured from the 2D map: switches to the
    ///     same worldspace synchronously (no camera reset), moves the camera to the shared world XY
    ///     (preserving its height + look angle), and restores the selection + cell-list sort. Interiors
    ///     load the captured cell.
    /// </summary>
    internal void ApplyViewFocus(WorldViewFocus focus)
    {
        if (_data is null) return;
        ApplyInteriorSortMode(focus.SortMode);

        if (focus.IsInterior && focus.InteriorCell is { } interior)
        {
            NavigateToCell(interior);
            SelectObject(focus.Selected);
            return;
        }

        if (focus.WorldspaceComboIndex >= 0 && focus.WorldspaceComboIndex <= _data.Worldspaces.Count)
        {
            // index < Count → a worldspace; index == Count → the unlinked-exterior tail (FormId null).
            var worldspaceFormId = focus.WorldspaceComboIndex < _data.Worldspaces.Count
                ? (uint?)_data.Worldspaces[focus.WorldspaceComboIndex].FormId
                : null;
            if (EnsureActiveExteriorWorldspace(worldspaceFormId))
            {
                var pos = _camera.Position;
                _camera.Position = new Vector3(focus.WorldX, focus.WorldY, pos.Z);
            }
        }

        SelectObject(focus.Selected);
    }

    // The CellListControl owns the sort state + combo; its setter syncs both and re-sorts in place.
    private void ApplyInteriorSortMode(CellSortMode mode) => CellList.SortMode = mode;

    /// <summary>
    ///     Navigates the 3D scene to a specific cell — the 3D counterpart of
    ///     <see cref="WorldMapControl.NavigateToCell" />, used when a door-destination / linked-cell
    ///     link is clicked while the 3D view is the active one. Interiors load as a single-cell scene;
    ///     exteriors select the parent worldspace (when it isn't already shown) and frame the camera on
    ///     the cell's grid position.
    /// </summary>
    internal void NavigateToCell(CellRecord cell, (Vector3 pos, float yaw)? warpPose = null)
    {
        if (_data is null) return;

        // Interiors have no grid coords — same single-cell load path as a cell-browser pick.
        if (cell.GridX is not int || cell.GridY is not int)
        {
            _pendingNavigateCell = null;
            _pendingNavigateWarpPose = null;
            _selectedInterior = cell;
            _suppressWorldspaceSelectionEvent = true;
            WorldspaceComboBox.SelectedIndex = -1;
            _suppressWorldspaceSelectionEvent = false;
            HideInteriorBrowser();
            TryBuildCellGrid();
            if (warpPose is { } pose) ApplyWarpPose(pose);
            else ResetCameraToInteriorBounds(cell);
            RefreshAtmosphereForCurrentWorldspace();
            return;
        }

        // Exterior: switch to the parent worldspace if we're not already showing it (or we're in
        // interior mode). The async SelectionChanged handler rebuilds the grid and would reset the
        // camera to the worldspace centroid, so stash the target (and any warp pose) for it to
        // re-frame on instead.
        var wsIndex = cell.WorldspaceFormId is uint wsFormId
            ? _data.Worldspaces.FindIndex(ws => ws.FormId == wsFormId)
            : -1;
        if (wsIndex >= 0 && (wsIndex != WorldspaceComboBox.SelectedIndex || _selectedInterior is not null))
        {
            _pendingNavigateCell = cell;
            _pendingNavigateWarpPose = warpPose;
            WorldspaceComboBox.SelectedIndex = wsIndex;
            return;
        }

        // Already showing the right exterior worldspace — just move the camera.
        if (warpPose is { } p) ApplyWarpPose(p);
        else CenterCameraOnCell(cell);
    }

    /// <summary>
    ///     Places the camera at a door's XTEL arrival pose: standing at the teleport point, looking
    ///     level along the recorded yaw (first-person). The next walk-mode <c>SnapToGround</c> corrects
    ///     Z to the actual floor beneath the arrival X/Y.
    /// </summary>
    private void ApplyWarpPose((Vector3 pos, float yaw) pose)
    {
        _camera.Position = pose.pos;
        _camera.Yaw = pose.yaw;
        _camera.Pitch = 0f;
    }

    /// <summary>
    ///     Enter-key handler: if the current selection is a teleport door, navigates to its destination
    ///     cell and places the camera at the door's XTEL arrival pose. Brief status (no-op) when nothing
    ///     is selected or the selection carries no teleport link.
    /// </summary>
    private void WarpToSelectedDoor()
    {
        if (_data is null) return;
        if (_selectedReference is not { } door)
        {
            ShowStatus("No object selected — pick a door first (click, or press E in walk mode).", autoDismiss: true);
            return;
        }

        if (door.DestinationCellFormId is not uint destFormId ||
            !_data.CellByFormId.TryGetValue(destFormId, out var destCell))
        {
            ShowStatus("Selected object is not a teleport door.", autoDismiss: true);
            return;
        }

        // XTEL position is the arrival point in the destination cell's space (same frame as placed-ref
        // X/Y/Z). Raise by eye height; yaw from the recorded Z rotation (negated to match the engine's
        // placement convention). Null pose → just frame the destination cell.
        (Vector3 pos, float yaw)? warpPose = door.TeleportPosRot is { } t
            ? (new Vector3(t.X, t.Y, t.Z + _controller.EyeHeight), -t.RotZ)
            : null;
        NavigateToCell(destCell, warpPose);
    }

    /// <summary>Frames the camera on a single exterior cell's grid position, reusing the same
    /// pitched-down posture as <see cref="ResetCameraToDataCentroid" />.</summary>
    private void CenterCameraOnCell(CellRecord cell)
    {
        if (cell.GridX is not int gx || cell.GridY is not int gy) return;
        var worldX = (gx + 0.5f) * _cellSize;
        var worldY = (gy + 0.5f) * _cellSize;
        _camera.Position = new Vector3(worldX, worldY - 8192f, 32768f);
        _camera.Yaw = 0f;
        _camera.Pitch = -MathF.PI / 6f;
    }

    private void ResetCameraToDataCentroid()
    {
        if (_data is null) return;

        // Interiors have no grid coords (GridX/GridY null) — frame on the placed-object bounds
        // instead, and never run the GridX!.Value dereference below.
        if (_selectedInterior is { } interior)
        {
            ResetCameraToInteriorBounds(interior);
            return;
        }

        // Centroid of the currently selected worldspace's exterior cells. The worldspace picker
        // always scopes to one worldspace, so the camera frames the chosen
        // one rather than the union of every worldspace in the file.
        double sumX = 0, sumY = 0;
        var count = 0;
        foreach (var cell in GetSelectedWorldspaceCells(_data).Cells)
        {
            sumX += cell.GridX!.Value;
            sumY += cell.GridY!.Value;
            count++;
        }
        if (count == 0) return;

        var avgGridX = sumX / count;
        var avgGridY = sumY / count;

        var worldX = (float)(avgGridX * _cellSize);
        var worldY = (float)(avgGridY * _cellSize);

        // Position 2 cells south and well above the ground, pitched down ~30° looking north.
        _camera.Position = new Vector3(worldX, worldY - 8192f, 32768f);
        _camera.Yaw = 0f;
        _camera.Pitch = -MathF.PI / 6f;
    }

    /// <summary>
    ///     Frames the camera on an interior cell's placed-object bounds (absolute coords; interiors
    ///     have no grid). Sizes the pull-back + streaming render distance to the cell extent so the
    ///     cylinder snugly covers it. Same pitched-down posture as the exterior framing.
    /// </summary>
    private void ResetCameraToInteriorBounds(CellRecord interior)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        var count = 0;
        foreach (var o in interior.PlacedObjects)
        {
            if (!float.IsFinite(o.X) || !float.IsFinite(o.Y) || !float.IsFinite(o.Z)) continue;
            minX = MathF.Min(minX, o.X); minY = MathF.Min(minY, o.Y); minZ = MathF.Min(minZ, o.Z);
            maxX = MathF.Max(maxX, o.X); maxY = MathF.Max(maxY, o.Y); maxZ = MathF.Max(maxZ, o.Z);
            count++;
        }
        if (count == 0) return;

        var centerX = (minX + maxX) * 0.5f;
        var centerY = (minY + maxY) * 0.5f;
        var centerZ = (minZ + maxZ) * 0.5f;
        var extent = MathF.Max(maxX - minX, maxY - minY);
        var dist = MathF.Max(extent, _cellSize) * 0.75f;

        // No render-distance change: the default streaming radius (16 cells) dwarfs any interior,
        // and only this single cell exists in the index, so there's nothing else to stream in.
        _camera.Position = new Vector3(centerX, centerY - dist, centerZ + extent * 0.5f + 4096f);
        _camera.Yaw = 0f;
        _camera.Pitch = -MathF.PI / 6f;
    }

    private int SelectInitialWorldspaceIndex(WorldViewData data)
    {
        // Debug/repro hook: FALLOUT_VIEWER_WORLDSPACE=<substring> forces the initial worldspace
        // by EditorID/FullName match (e.g. "Strip"), so the profiler can target a specific
        // worldspace headlessly. Falls through to the normal selection when unset or unmatched.
        var forced = EnvironmentVariables.Get(EnvironmentVariables.Viewer.Worldspace);
        if (!string.IsNullOrWhiteSpace(forced))
        {
            // Exact EditorID/FullName match first so e.g. "TheStripWorld" does not match
            // "TheStripWorldNew"; only fall back to a substring match if nothing is exact.
            for (var i = 0; i < data.Worldspaces.Count; i++)
            {
                var ws = data.Worldspaces[i];
                if (string.Equals(ws.EditorId, forced, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ws.FullName, forced, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
            for (var i = 0; i < data.Worldspaces.Count; i++)
            {
                var ws = data.Worldspaces[i];
                if (ContainsWorldspaceToken(ws.EditorId, forced) || ContainsWorldspaceToken(ws.FullName, forced))
                {
                    return i;
                }
            }
        }

        if (!IsWastelandNvHeavyStressScene())
        {
            return 0;
        }

        // Prefer the exact "WastelandNV" worldspace (FNV's full Mojave). The loose
        // WorldspaceLooksLikeWastelandNv match below also matches near-names like "WastelandNVMini",
        // which would silently pick a far lighter scene than the stress test intends.
        for (var i = 0; i < data.Worldspaces.Count; i++)
        {
            if (string.Equals(data.Worldspaces[i].EditorId, "WastelandNV", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        for (var i = 0; i < data.Worldspaces.Count; i++)
        {
            if (WorldspaceLooksLikeWastelandNv(data.Worldspaces[i]))
            {
                return i;
            }
        }

        return 0;
    }

    private void ApplyStressSceneBookmarkIfRequested()
    {
        if (_stressBookmarkApplied || !IsWastelandNvHeavyStressScene())
        {
            return;
        }

        if (_data is null || _spatialIndex is null)
        {
            return;
        }

        var bookmark = WorldViewStressBookmarkFinder.FindWastelandNvHeavyBookmark(
            _spatialIndex,
            _data.RenderCache,
            DefaultRenderDistanceCells * _cellSize);
        _stressBookmarkApplied = true;
        if (bookmark is not { } heavy)
        {
            Log.Warn("WorldView3DControl: WastelandNV Heavy stress bookmark requested, but no renderable reference cluster was found.");
            return;
        }

        SetRenderDistance(DefaultRenderDistanceCells * _cellSize);
        var gameY = -heavy.CanvasCenter.Y;
        _camera.Position = new Vector3(
            heavy.CanvasCenter.X,
            gameY - _cellSize * 2f,
            32768f);
        _camera.Yaw = 0f;
        _camera.Pitch = -MathF.PI / 6f;

        Log.Info(
            "WorldView3DControl: applied WastelandNV Heavy stress bookmark at ({0:0}, {1:0}); nearby renderable refs={2}.",
            heavy.CanvasCenter.X,
            gameY,
            heavy.Score);
    }

    private bool IsWastelandNvHeavyStressScene() =>
        string.Equals(_stressScene, "WastelandNVHeavy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_stressScene, "WastelandNV", StringComparison.OrdinalIgnoreCase);

    private static bool WorldspaceLooksLikeWastelandNv(WorldspaceRecord worldspace) =>
        ContainsWorldspaceToken(worldspace.EditorId, "WastelandNV") ||
        ContainsWorldspaceToken(worldspace.FullName, "WastelandNV") ||
        ContainsWorldspaceToken(worldspace.FullName, "Mojave");

    private static bool ContainsWorldspaceToken(string? value, string token) =>
        value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;
}
