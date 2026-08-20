using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using BethesdaMultitool.Core.Games;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace BethesdaMultitool;

public sealed partial class WorldView3DControl
{
    private void OnRenderPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // D1/D2/D3 toggle the wireframe / terrain / water layers; D4 toggles the
        // textured-terrain vs. VCLR-only (vertex-color) debug mode; F toggles between
        // fly (free cam) and walk (ground-locked) camera modes. PageUp/PageDown adjust the
        // streaming render distance. WinUI emits auto-repeat KeyDown events while a key is held,
        // so guard with a "first press" set.
        if (e.Key is VirtualKey.Number1 or VirtualKey.Number2 or VirtualKey.Number3
            or VirtualKey.Number4 or VirtualKey.Number5 or VirtualKey.Number6 or VirtualKey.Number7
            or VirtualKey.Number8 or VirtualKey.Number9 or VirtualKey.Number0
            or VirtualKey.F or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            if (_toggleKeysDown.Add(e.Key))
            {
                // Toggle shortcuts route through the corresponding settings control or state setter
                // so keyboard and panel stay in sync. Disabled subordinate controls do not accept
                // their equivalent shortcut either; their latent preference is preserved.
                if (e.Key == VirtualKey.Number1) CellsCheckBox.IsChecked = !_showWireframe;
                else if (e.Key == VirtualKey.Number2) TerrainToggle.IsChecked = !_showTerrain;
                else if (e.Key == VirtualKey.Number3) WaterCheckBox.IsChecked = !_showWater;
                else if (e.Key == VirtualKey.Number4 && _showTerrain)
                    VertexColorsToggle.IsChecked = !_showVertexColors;
                else if (e.Key == VirtualKey.Number5) RefsToggle.IsChecked = !_showReferences;
                else if (e.Key == VirtualKey.Number6) SetShowNavMesh(!_showNavMesh);
                else if (e.Key == VirtualKey.Number7 && _showReferences)
                    SetShowDisabled(!_showDisabled);
                else if (e.Key == VirtualKey.Number8) LightingPanel.LightingEnabled = !_showLighting;
                else if (e.Key == VirtualKey.Number9) LightingPanel.SkyboxEnabled = !_showSky;
                else if (e.Key == VirtualKey.Number0) LightingPanel.FogEnabled = !_showFog;
                else if (e.Key == VirtualKey.F)
                    SetCameraMode(_controller.Mode == CameraMode.Walk ? CameraMode.Fly : CameraMode.Walk);
                else if (e.Key == VirtualKey.PageUp)
                    SetRenderDistance(_renderDistance * RenderDistanceStep);
                else if (e.Key == VirtualKey.PageDown)
                    SetRenderDistance(_renderDistance / RenderDistanceStep);
            }

            e.Handled = true;
            return;
        }

        // Esc clears the current selection (no InspectObject fired; nothing to inspect).
        if (e.Key == VirtualKey.Escape)
        {
            ClearSelection3D();
            e.Handled = true;
            return;
        }

        // E in walk mode selects the object at the viewport center (first-person reticle pick), reusing
        // the click-pick path with a centered screen point. In fly mode E still climbs, so only intercept
        // it while walking. First-press guard so a held key doesn't cycle through stacked objects.
        if (e.Key == VirtualKey.E && _controller.Mode == CameraMode.Walk)
        {
            if (_toggleKeysDown.Add(e.Key))
            {
                var w = (float)RenderPanel.ActualWidth;
                var h = (float)RenderPanel.ActualHeight;
                if (w > 0f && h > 0f) TryPickObject(new Vector2(w / 2f, h / 2f));
            }

            e.Handled = true;
            return;
        }

        // Q in walk mode steps back through the selection history (the keyboard twin of right-click).
        // In fly mode Q still descends (handled by the controller below), so only intercept while
        // walking. First-press guarded so a held key fires once.
        if (e.Key == VirtualKey.Q && _controller.Mode == CameraMode.Walk)
        {
            if (_toggleKeysDown.Add(e.Key)) SelectPrevious();
            e.Handled = true;
            return;
        }

        // Enter warps to a selected teleport door's destination (XTEL arrival pose). Guarded so a held
        // key fires once.
        if (e.Key == VirtualKey.Enter)
        {
            if (_toggleKeysDown.Add(e.Key)) WarpToSelectedDoor();
            e.Handled = true;
            return;
        }

        // P copies the current camera pose as ready-to-run profiler --capture-frame arguments —
        // the bridge from "I can see the bug here" to a headless repro. Guarded so a held key
        // copies once.
        if (e.Key == VirtualKey.P)
        {
            if (_toggleKeysDown.Add(e.Key)) CopyCameraPoseToClipboard();
            e.Handled = true;
            return;
        }

        // R snaps the view back to the loaded scene's framing (worldspace centroid / interior room).
        // Guarded against text entry so the letter is never stolen from a focused text control, and
        // first-press guarded so a held key re-frames once.
        if (e.Key == VirtualKey.R && !TextEntryFocusGuard.IsTextEntryFocused(XamlRoot))
        {
            if (_toggleKeysDown.Add(e.Key)) ResetViewToSceneFraming();
            e.Handled = true;
            return;
        }

        _controller.OnKeyDown(e.Key);
        e.Handled = true;
    }

    /// <summary>
    ///     R (reset view): re-frames on the loaded scene by calling the same
    ///     <see cref="ResetCameraToDataCentroid" /> the load path uses, so the keybind and the initial
    ///     view can never drift apart — it already routes interiors to
    ///     <see cref="ResetCameraToInteriorBounds" /> and exteriors to the worldspace centroid. In a
    ///     projection (ortho) mode the visible framing comes from the ortho focus rather than the
    ///     camera, so the focus is re-seeded from the reset camera.
    /// </summary>
    private void ResetViewToSceneFraming()
    {
        ResetCameraToDataCentroid();
        if (ProjectionActive) InitProjectionFocusFromCamera();
    }

    private void OnRenderPanelKeyUp(object sender, KeyRoutedEventArgs e)
    {
        _toggleKeysDown.Remove(e.Key);
        _controller.OnKeyUp(e.Key);
        e.Handled = true;
    }

    private void OnRenderPanelLostFocus(object sender, RoutedEventArgs e)
    {
        // Avoid stuck movement keys when focus drops (e.g. user clicks elsewhere mid-stride).
        _controller.ClearKeys();
        _toggleKeysDown.Clear();
    }

    private void OnRenderPanelPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RenderPanel);
        var props = point.Properties;
        var pos = new Vector2((float)point.Position.X, (float)point.Position.Y);

        if (props.IsLeftButtonPressed)
        {
            _mouseDragActive = RenderPanel.CapturePointer(e.Pointer);
            _previousPointerPosition = pos;
            _pointerPressPosition = pos;
            _pointerDragMoved = false;
            RenderPanel.Focus(FocusState.Pointer);
            e.Handled = true;
        }
        else if (props.IsRightButtonPressed)
        {
            // Right-click (released without a drag) steps back through the selection history. Track the
            // press here; the drag check + SelectPrevious happen on release. No pointer capture — the
            // left-drag look path owns that; we only need the down/up pair on the panel.
            _rightButtonActive = true;
            _rightPressPosition = pos;
            _rightDragMoved = false;
            RenderPanel.Focus(FocusState.Pointer);
            e.Handled = true;
        }
    }

    private void OnRenderPanelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RenderPanel);
        var current = new Vector2((float)point.Position.X, (float)point.Position.Y);

        // A right-button move past the click threshold marks the gesture a drag, so its release won't
        // fire the back-nav (keeps right-drag free for future use).
        if (_rightButtonActive && (current - _rightPressPosition).Length() > ClickMoveThresholdPixels)
            _rightDragMoved = true;

        if (!_mouseDragActive) return;
        var delta = current - _previousPointerPosition;
        _previousPointerPosition = current;
        if ((current - _pointerPressPosition).Length() > ClickMoveThresholdPixels) _pointerDragMoved = true;
        if (delta == Vector2.Zero) return;
        // In an ortho projection mode left-drag PANS the focus (the camera look angle is locked), except
        // with Shift held, which free-rotates the azimuth (the next ◄ ► click re-snaps to 90°). Outside
        // a projection mode it drives the flythrough mouse-look. A clean (non-drag) click still picks.
        if (ProjectionActive)
        {
            if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift))
                RotateProjectionAzimuth(delta.X);
            else
                PanProjectionFocus(delta);
        }
        else
        {
            _controller.OnMouseDelta(delta);
        }
    }

    private void OnRenderPanelPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RenderPanel);
        var kind = point.Properties.PointerUpdateKind;

        // Right-button release: a clean (non-drag) right-click steps back through the selection history.
        if (kind == PointerUpdateKind.RightButtonReleased)
        {
            var wasActive = _rightButtonActive;
            _rightButtonActive = false;
            if (wasActive && !_rightDragMoved) SelectPrevious();
            e.Handled = true;
            return;
        }

        // Left-button release: a press released without a look-drag is a pick click.
        if (kind == PointerUpdateKind.LeftButtonReleased)
        {
            if (!_mouseDragActive) return;
            RenderPanel.ReleasePointerCapture(e.Pointer);
            _mouseDragActive = false;
            if (!_pointerDragMoved)
            {
                TryPickObject(new Vector2((float)point.Position.X, (float)point.Position.Y));
            }

            e.Handled = true;
        }
    }

    /// <summary>
    ///     Screen-ray object picking: unprojects the click through the (reversed-Z) view-projection,
    ///     ray-tests every nearby placed reference's bounding sphere, and raises
    ///     <see cref="InspectObject" /> for the nearest hit (wired to the shared inspection panel).
    /// </summary>
    private void TryPickObject(Vector2 screen)
    {
        // Meshes is the parent visibility switch for placed references. Keep the current selection
        // latent while it is off, but do not let a click or walk-mode E select an object that the
        // live view is deliberately not drawing.
        if (!_showReferences || _data is null || _spatialIndex is null) return;
        var width = (float)RenderPanel.ActualWidth;
        var height = (float)RenderPanel.ActualHeight;
        if (width <= 0f || height <= 0f) return;

        // Rebuild the exact view-projection the render loop uses (reversed-Z applied) for the active
        // projection mode, then invert it to unproject the click into a world-space ray. The unproject
        // is projection-agnostic — for ortho the inverse yields the (constant) parallel view direction.
        Matrix4x4 viewProj;
        Vector3 queryCenter;
        float queryRadius;
        if (ProjectionActive)
        {
            viewProj = BuildProjectionViewProj(width / height, out var orthoCylinder, out _);
            queryCenter = _projectionFocus;
            queryRadius = orthoCylinder.Radius;
        }
        else
        {
            viewProj = _camera.GetViewMatrix() * _camera.GetProjectionMatrix(width / height);
            queryCenter = _camera.Position;
            queryRadius = _renderDistance;
        }

        if (!Matrix4x4.Invert(viewProj, out var invViewProj)) return;

        var ndcX = 2f * (screen.X / width) - 1f;
        var ndcY = 1f - 2f * (screen.Y / height);

        Vector3 Unproject(float ndcZ)
        {
            var h = Vector4.Transform(new Vector4(ndcX, ndcY, ndcZ, 1f), invViewProj);
            return new Vector3(h.X, h.Y, h.Z) / h.W;
        }

        var nearWorld = Unproject(1f); // reversed-Z: near plane = depth 1
        var rayDir = Unproject(0f) - nearWorld; // toward the far plane (depth 0)
        var rayLen = rayDir.Length();
        if (rayLen < 1e-4f) return;
        rayDir /= rayLen;

        // Limit candidates to cells within the drawn radius (around the camera, or the ortho focus).
        _pickCellScratch.Clear();
        _spatialIndex.QueryCellsInRadius(queryCenter.X, -queryCenter.Y, queryRadius, _pickCellScratch);

        // Match the depth-tested scene: visible terrain is an occluder for reference selection. CPU
        // raycast the same heightfield triangles the terrain renderer uploads; this avoids a blocking
        // depth-buffer readback and works for both perspective and orthographic click rays.
        var terrainT = float.PositiveInfinity;
        if (_showTerrain && _terrain is not null)
        {
            foreach (var spatialCell in _pickCellScratch)
            {
                if (TerrainRaycaster.RaycastNearest(
                        spatialCell.Cell, nearWorld, rayDir, out var cellTerrainT,
                        _data.RenderCache, terrainT))
                {
                    terrainT = cellTerrainT;
                }
            }
        }

        // Click-through: collect ALL hits along the ray (same filters as the renderer so the
        // pickable set matches the visible set), sorted nearest-first.
        _pickHitScratch.Clear();
        _pickSphereFallbackScratch.Clear();
        foreach (var spatialCell in _pickCellScratch)
        {
            foreach (var placement in spatialCell.Cell.PlacedObjects)
            {
                var xespDisabled = _data.XespDisabledRefs.Contains(placement.FormId);
                var category = _data.CategoryIndex.GetValueOrDefault(
                    placement.BaseFormId, PlacedObjectCategory.Unknown);
                if (RenderableReference.TryBuild(
                        placement,
                        category,
                        xespDisabled: xespDisabled,
                        game: _data.Game) is not { } r)
                    continue;
                if (!_referenceEnabledOverrides.IsVisible(r.FormId, r.IsInitiallyDisabled, _showDisabled)) continue;
                // Markers are pickable when VISIBLE (the pickable set mirrors the visible set);
                // imposters are render-only LOD stand-ins and never pickable.
                if (r.IsMarker && !_showMarkers) continue;
                if (r.IsImposter) continue;
                if (_hiddenCategories.Contains(r.Category)) continue;
                // Warm collision meshes use their local AABB as broadphase and exact triangles as
                // narrowphase. Cold/unavailable meshes retain the OBND/sphere path below.
                // Refs with no OBND bake an oversized cell-wide cull sphere
                // (NoBoundsFallbackRadius); reusing it for the pick turns each into a "massive click
                // zone" (→ overlapping fallbacks swallow every click in Oblivion, where trees never
                // decode). When the mesh has resolved, use its real local bounds (tight). When it has
                // NOT, drop to a small prop-sized SelectionFallbackRadius instead of the cull sphere.
                // Refs WITH an OBND use the tight OBB narrowphase below, so this only affects OBND-less.
                // An authored all-zero OBND is "no data" in disguise (zero-extent box = unclickable);
                // route it through the same no-OBND fallback path everywhere below.
                var usableBounds = placement.Bounds is { IsDegenerate: false } ub ? ub : null;
                var sphereRadius = r.BoundsRadius;
                if (usableBounds is null)
                {
                    if (_references is not null
                        && _references.TryGetMeshLocalRadius(r.MeshId, out var meshRadius) && meshRadius > 0f)
                    {
                        var s = placement.Scale > 0f ? placement.Scale : 1f;
                        sphereRadius = meshRadius * s;
                    }
                    else
                    {
                        sphereRadius = RenderableReference.SelectionFallbackRadiusFor(_cellSize);
                    }
                }

                // A collision entry is usable only after decoded geometry has published it. Transform the
                // world ray into the same mesh-local frame and let CollisionMesh's local AABB reject
                // cheaply before its exact triangle loop. A warm triangle miss is authoritative: do
                // not resurrect it through the looser OBND/sphere fallback.
                var collisionResolution = _referenceMeshCache12?.ResolveCollisionMesh(
                    r.ModelPath,
                    r.Category) ?? CollisionMeshResolution.Unresolved;
                if (collisionResolution.Mesh is { } collision &&
                    Matrix4x4.Invert(r.WorldMatrix, out var inverseWorld))
                {
                    var localOrigin = Vector3.Transform(nearWorld, inverseWorld);
                    var localDirection = Vector3.TransformNormal(rayDir, inverseWorld);
                    if (!collision.RaycastNearest(localOrigin, localDirection, out var collisionT)) continue;

                    // Preserve the existing origin-inside deprioritization: OBND-backed refs use the
                    // same local box containment test; no-OBND refs retain their sphere containment.
                    var collisionInside = usableBounds is { } collisionBounds
                        ? localOrigin.X >= collisionBounds.X1 && localOrigin.X <= collisionBounds.X2 &&
                          localOrigin.Y >= collisionBounds.Y1 && localOrigin.Y <= collisionBounds.Y2 &&
                          localOrigin.Z >= collisionBounds.Z1 && localOrigin.Z <= collisionBounds.Z2
                        : Vector3.DistanceSquared(nearWorld, r.BoundsCenter) <= sphereRadius * sphereRadius;
                    if (!TerrainRaycaster.IsHitBehindTerrain(collisionT, terrainT))
                    {
                        _pickHitScratch.Add(
                            new PickHit(placement, placement.FormId, collisionT, collisionInside));
                    }

                    continue;
                }

                // Cold collision cache (or a non-invertible placement): retain the previous cheap
                // bounding-sphere broadphase and OBND-oriented-box narrowphase unchanged.
                if (!RaySphereHit(nearWorld, rayDir, r.BoundsCenter, sphereRadius, out var sphereT,
                        out var sphereInside)) continue;
                if (usableBounds is { } b)
                {
                    if (RayObbHit(nearWorld, rayDir, b, r.WorldMatrix, out var obbT, out var obbInside))
                    {
                        if (!TerrainRaycaster.IsHitBehindTerrain(obbT, terrainT))
                        {
                            _pickHitScratch.Add(new PickHit(placement, placement.FormId, obbT, obbInside));
                        }
                    }
                    else
                    {
                        // OBND missed but the broadphase sphere hit — some meshes' visible geometry
                        // overspills a too-tight base-record OBND (notably SpeedTree canopies), making
                        // them "impossible to click" under an OBB-only gate. Keep as a fallback.
                        if (!TerrainRaycaster.IsHitBehindTerrain(sphereT, terrainT))
                        {
                            _pickSphereFallbackScratch.Add(
                                new PickHit(placement, placement.FormId, sphereT, sphereInside));
                        }
                    }
                }
                else
                {
                    // No OBND at all — the broadphase sphere is the only available signal.
                    if (!TerrainRaycaster.IsHitBehindTerrain(sphereT, terrainT))
                    {
                        _pickHitScratch.Add(new PickHit(placement, placement.FormId, sphereT, sphereInside));
                    }
                }
            }
        }

        // Prefer tight OBND hits; fall back to broadphase-sphere hits only when nothing tighter lies
        // under the ray — everyday picking keeps its precision, but overspilling meshes still select.
        if (_pickHitScratch.Count == 0)
        {
            if (_pickSphereFallbackScratch.Count == 0) return; // empty space → keep current selection
            _pickHitScratch.AddRange(_pickSphereFallbackScratch);
        }

        // Order: hits whose box the camera is OUTSIDE first (the thing you're aiming at), then by ray
        // distance; hits whose box encloses the camera (fog/wind/snow effect volumes) go last so they no
        // longer steal the click — but they stay in the list, cycle-selectable on repeat clicks.
        _pickHitScratch.Sort(static (a, b) =>
        {
            if (a.OriginInside != b.OriginInside) return a.OriginInside ? 1 : -1;
            return a.T.CompareTo(b.T);
        });

        // If the current selection is still under this ray, advance to the next hit behind it (wrapping
        // past the last back to the nearest); otherwise select the nearest hit. Membership is recomputed
        // from the fresh list each click, so the cycle can't desync.
        var current = -1;
        if (_selectedReference is { } sel)
        {
            for (var i = 0; i < _pickHitScratch.Count; i++)
            {
                if (_pickHitScratch[i].FormId == sel.FormId)
                {
                    current = i;
                    break;
                }
            }
        }

        var next = current >= 0 ? (current + 1) % _pickHitScratch.Count : 0;

        var picked = _pickHitScratch[next].Placement;
        PushSelection(picked);
        UpdateHighlightFromSelection();
        InspectObject?.Invoke(this, picked);
    }

    /// <summary>
    ///     Assigns the current selection, pushing the prior selection onto the back-navigation history
    ///     so right-click (and Q in walk mode) can step back to it. Skips the push when nothing is
    ///     selected yet or the same object is re-selected, so a click-through cycle or a no-op set never
    ///     stacks duplicates. Bounded to <see cref="SelectionHistoryMax" /> (oldest dropped).
    /// </summary>
    private void PushSelection(PlacedReference? next)
    {
        if (_selectedReference is { } prev && (next is null || next.FormId != prev.FormId))
        {
            _selectionHistory.Add(prev);
            if (_selectionHistory.Count > SelectionHistoryMax) _selectionHistory.RemoveAt(0);
        }

        _selectedReference = next;
    }

    /// <summary>
    ///     Back-navigation: pops the most recent prior selection off the history and restores it
    ///     (right-click, or Q in walk mode). Pops without re-pushing, so repeated calls walk back
    ///     through the click-through cycle and earlier picks. No-op when the history is empty.
    /// </summary>
    private void SelectPrevious()
    {
        if (_selectionHistory.Count == 0) return;
        var prev = _selectionHistory[^1];
        _selectionHistory.RemoveAt(_selectionHistory.Count - 1);
        _selectedReference = prev;
        _pickHitScratch.Clear();
        UpdateHighlightFromSelection();
        InspectObject?.Invoke(this, prev);
    }

    /// <summary>Rebuilds the selection outline from the current <see cref="_selectedReference" />.</summary>
    private void UpdateHighlightFromSelection()
    {
        if (_selectionHighlight is null) return;
        if (_selectedReference is not { } placement ||
            RenderableReference.TryBuild(
                placement,
                game: _data?.Game ?? BethesdaGame.Unknown) is not { } r)
        {
            _selectionHighlight.ClearSelection();
            return;
        }

        if (placement.Bounds is { IsDegenerate: false } b)
        {
            // OBND is the object's local-space AABB; the world matrix (which includes scale) places it.
            // An authored all-zero OBND draws an invisible point box — fall through to the mesh AABB.
            _selectionHighlight.SetSelection(
                new Vector3(b.X1, b.Y1, b.Z1),
                new Vector3(b.X2, b.Y2, b.Z2),
                r.WorldMatrix);
        }
        else if (_references is not null
                 && _references.TryGetMeshLocalBounds(r.MeshId, out var localMin, out var localMax)
                 && localMin != localMax)
        {
            // No OBND (every Oblivion ref; some FO3+ records omit it too) but the mesh has resolved —
            // box its real mesh-local AABB, placed by the world matrix (carrying scale/rotation), so the
            // highlight hugs the geometry instead of the oversized no-bounds fallback sphere (OBLIV-1).
            // Same local-AABB × world-matrix path the OBND branch uses above.
            _selectionHighlight.SetSelection(localMin, localMax, r.WorldMatrix);
        }
        else
        {
            // No OBND and the mesh has not resolved yet — fall back to a small prop-sized cube around
            // the ref origin (identity world), matching the pick's SelectionFallbackRadius so the
            // outline isn't a cell-wide box. Re-selecting once the mesh has streamed in upgrades it to
            // the tight AABB above.
            var c = r.BoundsCenter;
            var rad = RenderableReference.SelectionFallbackRadiusFor(_cellSize);
            _selectionHighlight.SetSelection(
                new Vector3(c.X - rad, c.Y - rad, c.Z - rad),
                new Vector3(c.X + rad, c.Y + rad, c.Z + rad),
                Matrix4x4.Identity);
        }
    }

    /// <summary>
    ///     Applies the non-spatial live renderer gates to the retained selection. This keeps editor
    ///     chrome from outlining a reference whose pixels are absent because of authored state, a
    ///     per-reference preview, or an independent live category/layer decision.
    /// </summary>
    private bool IsSelectedReferenceVisible()
    {
        if (!_showReferences || _selectedReference is not { } placement || _data is null) return false;

        var category = _data.CategoryIndex.GetValueOrDefault(
            placement.BaseFormId, PlacedObjectCategory.Unknown);
        if (RenderableReference.TryBuild(
                placement,
                category,
                xespDisabled: _data.XespDisabledRefs.Contains(placement.FormId),
                game: _data.Game) is not { } reference)
        {
            return false;
        }

        if (!_referenceEnabledOverrides.IsVisible(
                reference.FormId, reference.IsInitiallyDisabled, _showDisabled)) return false;
        if (reference.IsGrass && !_showGrass) return false;
        if (reference.IsMarker && !_showMarkers) return false;
        if (_hiddenCategories.Contains(reference.Category)) return false;

        return !reference.IsImposter || BethesdaMultitool.Core.EnvironmentVariables.IsEnabled(
            BethesdaMultitool.Core.EnvironmentVariables.Viewer.ShowImposters);
    }

    /// <summary>Clears the 3D selection + its outline. Called on Esc, worldspace switch, and reload.</summary>
    private void ClearSelection3D()
    {
        _selectedReference = null;
        _pickHitScratch.Clear();
        // Drop the back-nav history too: Esc is a full deselect, and on a scene rebuild the prior
        // selections belong to the worldspace/cell being replaced (FormIDs can be reused).
        _selectionHistory.Clear();
        _selectionHighlight?.ClearSelection();
    }

    /// <summary>Ray vs sphere; returns the nearest non-negative hit distance along a unit-length ray.</summary>
    private static bool RaySphereHit(
        Vector3 origin, Vector3 dir, Vector3 center, float radius, out float t, out bool originInside)
    {
        t = 0f;
        var m = origin - center;
        var b = Vector3.Dot(m, dir);
        var c = Vector3.Dot(m, m) - radius * radius;
        originInside = c <= 0f; // origin within the sphere
        if (c > 0f && b > 0f) return false; // outside the sphere and pointing away
        var disc = b * b - c;
        if (disc < 0f) return false; // misses
        var hit = -b - MathF.Sqrt(disc);
        t = hit < 0f ? 0f : hit; // origin inside the sphere → 0
        return true;
    }

    /// <summary>
    ///     Ray vs oriented bounding box — the object's OBND transformed by its world matrix (the same
    ///     box the selection highlight draws). The ray is mapped into the box's local space, where the
    ///     OBND is axis-aligned, and slab-tested. The affine map preserves the ray parameter, so the
    ///     returned <paramref name="t" /> stays in world-ray units (consistent with the sphere
    ///     broadphase). Returns the entry distance, clamped to 0 when the camera is inside the box.
    /// </summary>
    private static bool RayObbHit(
        Vector3 origin, Vector3 dir, ObjectBounds bounds, Matrix4x4 world, out float t, out bool originInside)
    {
        t = 0f;
        originInside = false;
        if (!Matrix4x4.Invert(world, out var invWorld)) return false;
        var lo = Vector3.Transform(origin, invWorld);
        var ld = Vector3.TransformNormal(dir, invWorld);

        // The camera/ray origin is inside the box when its local point falls within all three slabs.
        // Such a hit clamps to t=0 below, which would wrongly sort the enclosing volume (a fog/wind/snow
        // effect box you're standing in) ahead of the object you're actually aiming at — so the caller
        // deprioritizes inside-box hits.
        originInside = lo.X >= bounds.X1 && lo.X <= bounds.X2 &&
                       lo.Y >= bounds.Y1 && lo.Y <= bounds.Y2 &&
                       lo.Z >= bounds.Z1 && lo.Z <= bounds.Z2;

        var tMin = 0f;
        var tMax = float.PositiveInfinity;
        if (!SlabClip(lo.X, ld.X, bounds.X1, bounds.X2, ref tMin, ref tMax)) return false;
        if (!SlabClip(lo.Y, ld.Y, bounds.Y1, bounds.Y2, ref tMin, ref tMax)) return false;
        if (!SlabClip(lo.Z, ld.Z, bounds.Z1, bounds.Z2, ref tMin, ref tMax)) return false;
        t = tMin;
        return true;

        static bool SlabClip(float o, float d, float lo, float hi, ref float tMin, ref float tMax)
        {
            if (MathF.Abs(d) < 1e-8f) return o >= lo && o <= hi; // parallel: inside the slab, else miss
            var inv = 1f / d;
            var t1 = (lo - o) * inv;
            var t2 = (hi - o) * inv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            return tMin <= tMax;
        }
    }

    private void OnRenderPanelPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(RenderPanel).Properties.MouseWheelDelta;
        // Ortho modes: the wheel zooms the ortho extent. Perspective: it adjusts the fly/walk speed.
        if (!TryZoomProjection(delta)) _controller.OnScroll(delta);
        e.Handled = true;
    }

    private readonly record struct PickHit(PlacedReference Placement, uint FormId, float T, bool OriginInside);
}
