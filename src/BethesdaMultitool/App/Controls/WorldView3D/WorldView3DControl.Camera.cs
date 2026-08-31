using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    /// <summary>
    ///     How far south of the framing target the camera sits, in cells. Was the literal 8192 (= 2 cells
    ///     at Fallout's 4096-unit grid) until Starfield arrived with a 100-unit cell, where the fixed
    ///     value put the camera 82 cells away and frustum-culled the entire worldspace.
    /// </summary>
    private const float FramingSouthOffsetCells = 2f;

    /// <summary>
    ///     Camera height above the ground plane when framing a worldspace, in cells (was the literal
    ///     32768 = 8 cells at 4096). See <see cref="FramingSouthOffsetCells" />.
    /// </summary>
    private const float FramingHeightCells = 8f;

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
            var selectionGeneration = BeginSceneSelection();
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
            MarkSceneSelectionReady(selectionGeneration);
            HideStatus();
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

    /// <summary>
    ///     Frames the camera on a single exterior cell's grid position, reusing the same
    ///     pitched-down posture as <see cref="ResetCameraToDataCentroid" />.
    /// </summary>
    private void CenterCameraOnCell(CellRecord cell)
    {
        if (cell.GridX is not int gx || cell.GridY is not int gy) return;
        var worldX = (gx + 0.5f) * _cellSize;
        var worldY = (gy + 0.5f) * _cellSize;
        _camera.Position = new Vector3(
            worldX,
            worldY - (FramingSouthOffsetCells * _cellSize),
            FramingHeightCells * _cellSize);
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
        _camera.Position = new Vector3(
            worldX,
            worldY - (FramingSouthOffsetCells * _cellSize),
            FramingHeightCells * _cellSize);
        _camera.Yaw = 0f;
        _camera.Pitch = -MathF.PI / 6f;
    }

    /// <summary>
    ///     Frames the camera on an interior cell's contents. Interiors are sealed shells, so the
    ///     camera is placed INSIDE the room rather than pulled back from it — the original framing
    ///     sat the camera outside and 4096 units above the bounding box, which for any enclosed
    ///     interior meant staring at the outside of the walls (and, with fog, at nothing at all).
    ///     <para>
    ///         Position comes from robust statistics over placement ORIGINS, deliberately not from a
    ///         min/max box: an intermediate version of this method min/maxed the OBND bounding
    ///         <em>spheres</em> (centre ± radius), and a single room-shell or trigger placement with a
    ///         multi-thousand-unit radius blew the box out far enough to push the camera back outside
    ///         the shell again. Measured on NovacMotelLobby, whose contents sit at Z≈8960-9214 and
    ///         Y≈859-1456, that produced a camera at Y=-1483, Z=4976 and a 100%-frustum-culled frame.
    ///         A mean centre with a low-percentile floor cannot be dragged that way by one outlier.
    ///     </para>
    /// </summary>
    private void ResetCameraToInteriorBounds(CellRecord interior)
    {
        // Prefer the render cache's resolved world-space placements; fall back to the raw record
        // origins when the cell has not been baked yet. Radii are read for the extent only.
        var origins = new List<Vector3>();
        var placements = _data?.RenderCache?.GetPlacementList(interior);
        if (placements is { Length: > 0 })
        {
            foreach (var placement in placements)
            {
                var c = placement.BoundsCenter;
                if (float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.Z)) origins.Add(c);
            }
        }

        if (origins.Count == 0)
        {
            foreach (var o in interior.PlacedObjects)
            {
                if (float.IsFinite(o.X) && float.IsFinite(o.Y) && float.IsFinite(o.Z))
                {
                    origins.Add(new Vector3(o.X, o.Y, o.Z));
                }
            }
        }

        if (origins.Count == 0) return;

        // Mean X/Y puts the camera among the room's contents rather than at the centre of whatever
        // box the outliers describe (markers, triggers and audio emitters are routinely placed well
        // outside the visible room and would otherwise pull the framing off the playable space).
        var centerX = 0f;
        var centerY = 0f;
        foreach (var o in origins)
        {
            centerX += o.X;
            centerY += o.Y;
        }

        centerX /= origins.Count;
        centerY /= origins.Count;

        // Floor = a low percentile of Z, not the minimum: interiors commonly carry a marker or
        // sound emitter far below the walkable floor, and standing eye-height above THAT is still
        // beneath the room. The 25th percentile lands on the floor clutter that rests on the floor.
        var zs = origins.Select(o => o.Z).Order().ToArray();
        var floorZ = zs[Math.Clamp((int)(zs.Length * 0.25f), 0, zs.Length - 1)];

        // Level gaze from inside — an interior reads far better at eye level than from a pitched-down
        // overhead vantage, and there is no back-off: any offset risks leaving the shell.
        _camera.Position = new Vector3(centerX, centerY, floorZ + _controller.EyeHeight);
        _camera.Yaw = 0f;
        _camera.Pitch = 0f;
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
                    Log.Info("WorldView3DControl: forced worldspace '{0}' → [{1}] '{2}'.", forced, i, ws.EditorId);
                    return i;
                }
            }

            for (var i = 0; i < data.Worldspaces.Count; i++)
            {
                var ws = data.Worldspaces[i];
                if (ContainsWorldspaceToken(ws.EditorId, forced) || ContainsWorldspaceToken(ws.FullName, forced))
                {
                    Log.Info("WorldView3DControl: forced worldspace '{0}' ~→ [{1}] '{2}'.", forced, i, ws.EditorId);
                    return i;
                }
            }

            // The forced name matched nothing — dump what IS available so headless repro scripts can
            // see why (merged load orders can leave override records with empty EditorIds).
            Log.Warn(
                "WorldView3DControl: forced worldspace '{0}' matched none of [{1}].",
                forced,
                string.Join(", ", data.Worldspaces.Select(w => $"{w.EditorId ?? "(null)"}/{w.FullName ?? "(null)"}")));
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

        // WastelandNVHeavy is an FNV/Mojave workload, not a generic dense-scene finder. Running its
        // all-cell placement search against Appalachia or New Atlantis populates the append-only
        // WorldRenderCache for an irrelevant world before frame 1. Fail closed before the finder can
        // materialize anything; explicit profiler worldspaces also suppress the implicit default.
        var selectedWorldspace = CurrentSelectedExteriorWorldspace();
        if (selectedWorldspace is null || !WorldspaceLooksLikeWastelandNv(selectedWorldspace))
        {
            _stressBookmarkApplied = true;
            Log.Warn(
                "WorldView3DControl: WastelandNV Heavy stress bookmark ignored for non-Mojave worldspace '{0}'.",
                selectedWorldspace?.EditorId ?? "(none)");
            return;
        }

        var bookmark = WorldViewStressBookmarkFinder.FindWastelandNvHeavyBookmark(
            _spatialIndex,
            _data.RenderCache,
            DefaultRenderDistanceCells * _cellSize);
        _stressBookmarkApplied = true;
        if (bookmark is not { } heavy)
        {
            Log.Warn(
                "WorldView3DControl: WastelandNV Heavy stress bookmark requested, but no renderable reference cluster was found.");
            return;
        }

        SetRenderDistance(DefaultRenderDistanceCells * _cellSize);
        var gameY = -heavy.CanvasCenter.Y;
        _camera.Position = new Vector3(
            heavy.CanvasCenter.X,
            gameY - (FramingSouthOffsetCells * _cellSize),
            FramingHeightCells * _cellSize);
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
