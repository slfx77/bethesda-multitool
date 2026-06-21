using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
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
                // Each toggle key flips its toolbar ToggleButton, whose Changed handler updates the
                // backing field — so keyboard and toolbar stay in sync.
                if (e.Key == VirtualKey.Number1) CellsCheckBox.IsChecked = !_showWireframe;
                else if (e.Key == VirtualKey.Number2) TerrainToggle.IsChecked = !_showTerrain;
                else if (e.Key == VirtualKey.Number3) WaterCheckBox.IsChecked = !_showWater;
                else if (e.Key == VirtualKey.Number4) VertexColorsToggle.IsChecked = !_showVertexColors;
                else if (e.Key == VirtualKey.Number5) RefsToggle.IsChecked = !_showReferences;
                else if (e.Key == VirtualKey.Number6) SetShowNavMesh(!_showNavMesh);
                else if (e.Key == VirtualKey.Number7) SetShowDisabled(!_showDisabled);
                else if (e.Key == VirtualKey.Number8) LightingToggle.IsOn = !_showLighting;
                else if (e.Key == VirtualKey.Number9) SkyboxToggle.IsChecked = !_showSky;
                else if (e.Key == VirtualKey.Number0) FogToggle.IsOn = !_showFog;
                else if (e.Key == VirtualKey.F)
                    _controller.Mode = _controller.Mode == CameraMode.Walk ? CameraMode.Fly : CameraMode.Walk;
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

        // Enter warps to a selected teleport door's destination (XTEL arrival pose). Guarded so a held
        // key fires once.
        if (e.Key == VirtualKey.Enter)
        {
            if (_toggleKeysDown.Add(e.Key)) WarpToSelectedDoor();
            e.Handled = true;
            return;
        }

        _controller.OnKeyDown(e.Key);
        e.Handled = true;
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
        if (!point.Properties.IsLeftButtonPressed) return;

        _mouseDragActive = RenderPanel.CapturePointer(e.Pointer);
        _previousPointerPosition = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _pointerPressPosition = _previousPointerPosition;
        _pointerDragMoved = false;
        RenderPanel.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void OnRenderPanelPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_mouseDragActive) return;
        var point = e.GetCurrentPoint(RenderPanel);
        var current = new Vector2((float)point.Position.X, (float)point.Position.Y);
        var delta = current - _previousPointerPosition;
        _previousPointerPosition = current;
        if ((current - _pointerPressPosition).Length() > ClickMoveThresholdPixels) _pointerDragMoved = true;
        if (delta != Vector2.Zero) _controller.OnMouseDelta(delta);
    }

    private void OnRenderPanelPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_mouseDragActive) return;
        RenderPanel.ReleasePointerCapture(e.Pointer);
        _mouseDragActive = false;
        // A press released without a look-drag is a pick click.
        if (!_pointerDragMoved)
        {
            var point = e.GetCurrentPoint(RenderPanel);
            TryPickObject(new Vector2((float)point.Position.X, (float)point.Position.Y));
        }
        e.Handled = true;
    }

    /// <summary>
    ///     Screen-ray object picking: unprojects the click through the (reversed-Z) view-projection,
    ///     ray-tests every nearby placed reference's bounding sphere, and raises
    ///     <see cref="InspectObject" /> for the nearest hit (wired to the shared inspection panel).
    /// </summary>
    private void TryPickObject(Vector2 screen)
    {
        if (_data is null || _spatialIndex is null) return;
        var width = (float)RenderPanel.ActualWidth;
        var height = (float)RenderPanel.ActualHeight;
        if (width <= 0f || height <= 0f) return;

        // Rebuild the exact view-projection the render loop uses (CameraState.GetProjectionMatrix
        // applies reversed-Z), then invert it to unproject the click into a world-space ray.
        var view = _camera.GetViewMatrix();
        var proj = _camera.GetProjectionMatrix(width / height);
        if (!Matrix4x4.Invert(view * proj, out var invViewProj)) return;

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

        // Limit candidates to cells within the render distance (what's actually drawn).
        _pickCellScratch.Clear();
        _spatialIndex.QueryCellsInRadius(_camera.Position.X, -_camera.Position.Y, _renderDistance, _pickCellScratch);

        // Click-through: collect ALL hits along the ray (same filters as the renderer so the
        // pickable set matches the visible set), sorted nearest-first.
        _pickHitScratch.Clear();
        _pickSphereFallbackScratch.Clear();
        foreach (var spatialCell in _pickCellScratch)
        {
            foreach (var placement in spatialCell.Cell.PlacedObjects)
            {
                if (RenderableReference.TryBuild(placement) is not { } r) continue;
                if (!_showDisabled && r.IsInitiallyDisabled) continue;
                if (r.IsMarker || r.IsImposter) continue; // hidden in the view by default
                // Broadphase: cheap bounding-sphere reject. Narrowphase: ray vs the OBND-tight
                // oriented box — the exact box the selection highlight draws — so the pick lands on
                // the clicked mesh instead of the near edge of an oversized bounding sphere.
                if (!RaySphereHit(nearWorld, rayDir, r.BoundsCenter, r.BoundsRadius, out var sphereT)) continue;
                if (placement.Bounds is { } b)
                {
                    if (RayObbHit(nearWorld, rayDir, b, r.WorldMatrix, out var obbT))
                    {
                        _pickHitScratch.Add(new PickHit(placement, placement.FormId, obbT));
                    }
                    else
                    {
                        // OBND missed but the broadphase sphere hit — some meshes' visible geometry
                        // overspills a too-tight base-record OBND (notably SpeedTree canopies), making
                        // them "impossible to click" under an OBB-only gate. Keep as a fallback.
                        _pickSphereFallbackScratch.Add(new PickHit(placement, placement.FormId, sphereT));
                    }
                }
                else
                {
                    // No OBND at all — the broadphase sphere is the only available signal.
                    _pickHitScratch.Add(new PickHit(placement, placement.FormId, sphereT));
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
        _pickHitScratch.Sort(static (a, b) => a.T.CompareTo(b.T));

        // If the current selection is still under this ray, advance to the next hit behind it (wrapping
        // past the last back to the nearest); otherwise select the nearest hit. Membership is recomputed
        // from the fresh list each click, so the cycle can't desync.
        var current = -1;
        if (_selectedReference is { } sel)
        {
            for (var i = 0; i < _pickHitScratch.Count; i++)
            {
                if (_pickHitScratch[i].FormId == sel.FormId) { current = i; break; }
            }
        }
        var next = current >= 0 ? (current + 1) % _pickHitScratch.Count : 0;

        _selectedReference = _pickHitScratch[next].Placement;
        UpdateHighlightFromSelection();
        InspectObject?.Invoke(this, _selectedReference);
    }

    private readonly record struct PickHit(PlacedReference Placement, uint FormId, float T);

    /// <summary>Rebuilds the selection outline from the current <see cref="_selectedReference" />.</summary>
    private void UpdateHighlightFromSelection()
    {
        if (_selectionHighlight is null) return;
        if (_selectedReference is not { } placement || RenderableReference.TryBuild(placement) is not { } r)
        {
            _selectionHighlight.ClearSelection();
            return;
        }

        if (placement.Bounds is { } b)
        {
            // OBND is the object's local-space AABB; the world matrix (which includes scale) places it.
            _selectionHighlight.SetSelection(
                new Vector3(b.X1, b.Y1, b.Z1),
                new Vector3(b.X2, b.Y2, b.Z2),
                r.WorldMatrix);
        }
        else
        {
            // No OBND — fall back to a world-space cube around the bounding sphere (identity world).
            var c = r.BoundsCenter;
            var rad = r.BoundsRadius;
            _selectionHighlight.SetSelection(
                new Vector3(c.X - rad, c.Y - rad, c.Z - rad),
                new Vector3(c.X + rad, c.Y + rad, c.Z + rad),
                Matrix4x4.Identity);
        }
    }

    /// <summary>Clears the 3D selection + its outline. Called on Esc, worldspace switch, and reload.</summary>
    private void ClearSelection3D()
    {
        _selectedReference = null;
        _pickHitScratch.Clear();
        _selectionHighlight?.ClearSelection();
    }

    /// <summary>Ray vs sphere; returns the nearest non-negative hit distance along a unit-length ray.</summary>
    private static bool RaySphereHit(Vector3 origin, Vector3 dir, Vector3 center, float radius, out float t)
    {
        t = 0f;
        var m = origin - center;
        var b = Vector3.Dot(m, dir);
        var c = Vector3.Dot(m, m) - radius * radius;
        if (c > 0f && b > 0f) return false;       // outside the sphere and pointing away
        var disc = b * b - c;
        if (disc < 0f) return false;              // misses
        var hit = -b - MathF.Sqrt(disc);
        t = hit < 0f ? 0f : hit;                  // origin inside the sphere → 0
        return true;
    }

    /// <summary>
    ///     Ray vs oriented bounding box — the object's OBND transformed by its world matrix (the same
    ///     box the selection highlight draws). The ray is mapped into the box's local space, where the
    ///     OBND is axis-aligned, and slab-tested. The affine map preserves the ray parameter, so the
    ///     returned <paramref name="t" /> stays in world-ray units (consistent with the sphere
    ///     broadphase). Returns the entry distance, clamped to 0 when the camera is inside the box.
    /// </summary>
    private static bool RayObbHit(Vector3 origin, Vector3 dir, ObjectBounds bounds, Matrix4x4 world, out float t)
    {
        t = 0f;
        if (!Matrix4x4.Invert(world, out var invWorld)) return false;
        var lo = Vector3.Transform(origin, invWorld);
        var ld = Vector3.TransformNormal(dir, invWorld);

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
        _controller.OnScroll(delta);
        e.Handled = true;
    }
}
