using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace BethesdaMultitool;

/// <summary>Pointer (pan/zoom/click/hover) and WASD keyboard-pan input handling for the world map.</summary>
public sealed partial class WorldMapControl
{
    private void MapCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MapCanvas);
        _panStartScreen = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _panOffsetAtStart = _panOffset;
        _isPanning = true;
        _pointerWasDragged = false;
        _pointerDownScreen = _panStartScreen;
        MapCanvas.CapturePointer(e.Pointer);
        // Take keyboard focus so WASD panning works after a click without tabbing to the canvas.
        MapCanvas.Focus(FocusState.Pointer);
        e.Handled = true;
    }

    private void MapCanvas_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key is Windows.System.VirtualKey.W or Windows.System.VirtualKey.A
            or Windows.System.VirtualKey.S or Windows.System.VirtualKey.D)
        {
            _panKeysDown.Add(e.Key);
            EnsureViewportTimerRunning(); // the timer integrates the pan + won't idle-stop while keys are held
            e.Handled = true;
            return;
        }

        // R snaps the view back to the active extent (worldspace overview / open interior cell).
        // Guarded against text entry so the letter is never stolen from a focused text control.
        // Not added to _panKeysDown: it is not an integrated motion key, and the re-frame is
        // idempotent, so auto-repeat while held is harmless.
        if (e.Key == Windows.System.VirtualKey.R && !TextEntryFocusGuard.IsTextEntryFocused(XamlRoot))
        {
            ResetViewToActiveExtent();
            e.Handled = true;
        }
    }

    /// <summary>
    ///     R (reset view) / the toolbar's Zoom-to-Fit: re-frames on the active extent — the open
    ///     interior/cell-detail cell via <see cref="WorldMapViewportHelper.ZoomToFitCell" />, otherwise
    ///     the active worldspace via <see cref="ApplyZoomToFitWorldspace" />. Single entry point so the
    ///     keybind and the toolbar button can never frame differently.
    /// </summary>
    internal void ResetViewToActiveExtent()
    {
        if (_state.Mode == ViewMode.CellDetail && _state.SelectedCell != null)
        {
            WorldMapViewportHelper.ZoomToFitCell(_state.SelectedCell,
                (float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight,
                out _zoom, out _panOffset);
        }
        else if (GetActiveCells().Count > 0)
        {
            ApplyZoomToFitWorldspace();
        }

        // A re-frame invalidates any in-flight drag/pan momentum; the preload margin must not keep
        // biasing toward the direction the user was panning before the snap.
        _panVelocity = Vector2.Zero;
        MapCanvas.Invalidate();
    }

    private void MapCanvas_KeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_panKeysDown.Remove(e.Key)) e.Handled = true;
    }

    // Drop held keys when focus leaves the canvas (alt-tab / click away) so motion can't get stuck.
    private void MapCanvas_LostFocus(object sender, RoutedEventArgs e) => _panKeysDown.Clear();

    /// <summary>
    ///     Integrates held-WASD panning into <see cref="_panOffset" /> (screen-space, like the pointer
    ///     drag). W/S reveal north/south, A/D reveal west/east — i.e. the camera moves in the key's
    ///     direction. No-ops while a pointer drag is active so the two pan paths don't fight.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
        "S1244:Floating point numbers should not be tested for equality",
        Justification = "dx/dy are exact sums of the PanPixelsPerTick constant; opposed keys (A+D, W+S) " +
                        "cancel to exactly 0f, so the exact-zero 'no net pan this tick' test is well-defined.")]
    private void ApplyKeyboardPan()
    {
        if (_panKeysDown.Count == 0 || _isPanning) return;
        var dx = 0f;
        var dy = 0f;
        if (_panKeysDown.Contains(Windows.System.VirtualKey.A)) dx += PanPixelsPerTick;
        if (_panKeysDown.Contains(Windows.System.VirtualKey.D)) dx -= PanPixelsPerTick;
        if (_panKeysDown.Contains(Windows.System.VirtualKey.W)) dy += PanPixelsPerTick;
        if (_panKeysDown.Contains(Windows.System.VirtualKey.S)) dy -= PanPixelsPerTick;
        if (dx == 0f && dy == 0f) return;

        _panOffset = new Vector2(_panOffset.X + dx, _panOffset.Y + dy);
        _viewportRebuildPending = true; // re-stream terrain for the shifted viewport
        _topDownRequestPending = true;  // keep the rendered-models overlay in sync when it's on
        MapCanvas.Invalidate();
    }

    private void MapCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MapCanvas);
        var currentScreen = new Vector2((float)point.Position.X, (float)point.Position.Y);

        var worldPos = WorldMapViewportHelper.ScreenToWorld(currentScreen, _zoom, _panOffset);
        CoordsText.Text = $"X: {worldPos.X:F0}  Y: {-worldPos.Y:F0}";

        if (_isPanning)
        {
            var delta = currentScreen - _panStartScreen;
            if (delta.Length() > 3f) _pointerWasDragged = true;
            var newOffset = _panOffsetAtStart + delta;
            // EMA-smoothed pan velocity for the Preload margin in BuildTerrainTextureViewportRequest.
            // Coefficient picked empirically: low enough to ignore single-frame jitter, high enough
            // that a deliberate directional drag converges within ~5 frames.
            _panVelocity = Vector2.Lerp(_panVelocity, newOffset - _panOffset, 0.35f);
            _panOffset = newOffset;
            MapCanvas.Invalidate();
        }
        else if (_state.Mode == ViewMode.CellDetail && _state.SelectedCell != null)
        {
            var hitObj = WorldMapHitTester.HitTestPlacedObject(
                worldPos, _state.SelectedCell, _data!, _hiddenCategories, _hideDisabledActors, _zoom);
            if (hitObj != _hoveredObject)
            {
                _hoveredObject = hitObj;
                var hoverName = hitObj != null
                    ? PlacedObjectCategoryResolver.GetReferenceAwareName(hitObj, _data?.Resolver)
                    : null;
                HoverInfoText.Text = hitObj != null
                    ? $"{hitObj.RecordType}: {hoverName} at ({hitObj.X:F0}, {hitObj.Y:F0}, {hitObj.Z:F0})"
                    : FormatCellDisplayName(_state.SelectedCell);
                MapCanvas.Invalidate();
            }

            SetInteractiveCursor(hitObj != null);
        }
        else if (_state.Mode == ViewMode.WorldOverview && _data != null)
        {
            // Check dangling markers first so they take hover priority (matches click priority).
            DanglingRefPosition? hitDangling = null;
            if (_danglingThreshold != DanglingRefThreshold.None)
            {
                hitDangling = WorldMapDanglingRefOverlayRenderer.HitTest(
                    _data.DanglingRefs, _danglingThreshold,
                    _state.SelectedWorldspace?.FormId, worldPos, _zoom, _spatialIndex);
            }

            if (hitDangling != null)
            {
                var cellName = string.IsNullOrEmpty(hitDangling.CellEditorId)
                    ? $"cell 0x{hitDangling.CellFormId:X8}"
                    : hitDangling.CellEditorId;
                HoverInfoText.Text =
                    $"Dangling REFR 0x{hitDangling.FormId:X8} (base 0x{hitDangling.BaseFormId:X8}) " +
                    $"-> {cellName} [{hitDangling.Confidence}] at ({hitDangling.X:F0}, {hitDangling.Y:F0}, {hitDangling.Z:F0})";
                SetInteractiveCursor(true);
            }
            else
            {
                var hover = WorldMapHitTester.ProcessOverviewHover(
                    worldPos, _data, GetActiveCells(), _state.FilteredMarkers, _cellGridLookup,
                    _spatialIndex, _hiddenCategories, _hideDisabledActors, _zoom);
                HoverInfoText.Text = hover.StatusText;
                SetInteractiveCursor(hover.IsInteractive);
                if (hover.HoveredObject != _hoveredObject)
                {
                    _hoveredObject = hover.HoveredObject;
                    MapCanvas.Invalidate();
                }
            }
        }

        e.Handled = true;
    }

    private void MapCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        MapCanvas.ReleasePointerCapture(e.Pointer);
        if (_isPanning && !_pointerWasDragged)
        {
            // Dangling markers take priority — they're drawn on top of everything else, so a
            // click that lands on one should inspect the dangling REFR rather than fall through
            // to the cell or placed-object beneath.
            if (_data != null && _danglingThreshold != DanglingRefThreshold.None &&
                _state.Mode == ViewMode.WorldOverview)
            {
                var worldClick = WorldMapViewportHelper.ScreenToWorld(_pointerDownScreen, _zoom, _panOffset);
                var hitDangling = WorldMapDanglingRefOverlayRenderer.HitTest(
                    _data.DanglingRefs, _danglingThreshold,
                    _state.SelectedWorldspace?.FormId, worldClick, _zoom, _spatialIndex);
                if (hitDangling != null)
                {
                    var synthetic = WorldMapDanglingRefOverlayRenderer.SynthesizePlacedReference(hitDangling);
                    InspectObject?.Invoke(this, synthetic);
                    _isPanning = false;
                    e.Handled = true;
                    return;
                }
            }

            var result = WorldMapHitTester.HandleClick(
                _pointerDownScreen, _state.Mode, _data, GetActiveCells(),
                _state.SelectedCell,
                _state.FilteredMarkers, _cellGridLookup, _spatialIndex, _hiddenCategories, _hideDisabledActors,
                _zoom, _panOffset);

            switch (result.Action)
            {
                case WorldMapHitTester.ClickResult.ClickAction.ShowObject:
                    InspectObject?.Invoke(this, result.Object!);
                    break;
                case WorldMapHitTester.ClickResult.ClickAction.ShowCell:
                    InspectCell?.Invoke(this, result.Cell!);
                    break;
                case WorldMapHitTester.ClickResult.ClickAction.DeselectAndShowCell:
                    _state.SelectObject(null);
                    InspectCell?.Invoke(this, result.Cell!);
                    MapCanvas.Invalidate();
                    break;
            }
        }

        _isPanning = false;
        _panVelocity = Vector2.Zero;
        e.Handled = true;
    }

    private void MapCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(MapCanvas);
        var screenPos = new Vector2((float)point.Position.X, (float)point.Position.Y);
        var delta = point.Properties.MouseWheelDelta;

        var worldBeforeZoom = WorldMapViewportHelper.ScreenToWorld(screenPos, _zoom, _panOffset);
        var zoomFactor = delta > 0 ? 1.15f : 1f / 1.15f;
        _zoom = Math.Clamp(_zoom * zoomFactor, 0.001f, 50f);

        var newTransform = System.Numerics.Matrix3x2.CreateScale(_zoom);
        var worldAfterZoom = Vector2.Transform(worldBeforeZoom, newTransform);
        _panOffset = screenPos - worldAfterZoom;

        // Arm the zoom-settle window for the streaming layer only: defer the (expensive, higher-res)
        // re-stream until the zoom stops. The draw composites the existing bitmaps scaled meanwhile.
        // Other layers use a single bitmap and don't re-stream on zoom, so they need no settle.
        if (_state.Mode == ViewMode.WorldOverview && _currentLayer == WorldMapLayer.TerrainTextures)
        {
            _zoomSettleTicks = ZoomSettleTicks;
            EnsureViewportTimerRunning();
        }

        MapCanvas.Invalidate();
        e.Handled = true;
    }
}
