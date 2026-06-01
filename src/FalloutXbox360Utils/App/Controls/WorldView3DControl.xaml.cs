using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Windows.System;

namespace FalloutXbox360Utils;

/// <summary>
///     v3 Phase 1 worldspace view. Hosts a <see cref="SwapChainPanel" /> backed by a Vortice
///     D3D11 swapchain (<see cref="GpuSwapChainSurface" />) and renders a wireframe grid of
///     exterior cells using <see cref="CellGridDebugRenderer" />. A
///     <see cref="FlythroughCameraController" /> integrates WASD/Q/E + mouse-look + scroll
///     into a <see cref="CameraState" /> each frame.
///     <para>
///         Mirrors <c>WorldMapControl</c>'s public surface (events, LoadData, SelectObject)
///         so the host tab can toggle between 2D and 3D without reshaping data flow.
///     </para>
/// </summary>
public sealed partial class WorldView3DControl : UserControl, IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly CameraState _camera = new();
    private readonly FlythroughCameraController _controller;
    private readonly Vector2[] _pointerStartPosition = new Vector2[1];
    private readonly HashSet<VirtualKey> _toggleKeysDown = [];

    private CellGridDebugRenderer? _cellGrid;
    private TerrainRenderer? _terrain;
    private WaterRenderer? _water;
    private WorldViewData? _data;
    private GpuDevice? _gpu;
    private DateTime _lastFrameTime;
    private bool _mouseDragActive;
    private Vector2 _previousPointerPosition;
    private bool _renderLoopAttached;
    private GpuSwapChainSurface? _surface;
    private bool _suppressWorldspaceSelectionEvent;
    private Dictionary<(int gx, int gy), CellRecord>? _cellGridLookup;

    // Layer visibility — toggled by D1/D2/D3 keys, all default-on.
    private bool _showWireframe = true;
    private bool _showTerrain = true;
    private bool _showWater = true;

    public WorldView3DControl()
    {
        InitializeComponent();
        _controller = new FlythroughCameraController(_camera);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Dispose()
    {
        DetachRenderLoop();
        DisposeRenderResources();
    }

    // Public surface mirroring WorldMapControl ----------------------------------------------

    public event EventHandler<PlacedReference>? InspectObject;
    public event EventHandler<CellRecord>? InspectCell;

    internal void LoadData(WorldViewData data)
    {
        _data = data;

        // Mirror WorldMapControl's worldspace picker: one entry per worldspace, plus a final
        // "Unlinked Exterior" entry when DMP-only loads surface cells with no parent worldspace.
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.Items.Clear();
        foreach (var ws in data.Worldspaces)
        {
            var name = WorldMapColors.FormatWorldspaceName(ws);
            WorldspaceComboBox.Items.Add($"{name} — {ws.Cells.Count} cells");
        }
        if (data.UnlinkedExteriorCells.Count > 0)
        {
            WorldspaceComboBox.Items.Add($"Unlinked Exterior ({data.UnlinkedExteriorCells.Count} cells)");
        }
        _suppressWorldspaceSelectionEvent = false;

        if (WorldspaceComboBox.Items.Count > 0)
        {
            // Triggers SelectionChanged → TryBuildCellGrid + ResetCameraToDataCentroid.
            WorldspaceComboBox.SelectedIndex = 0;
        }
        else
        {
            // No worldspaces and no unlinked exteriors — just ensure the renderers are empty.
            TryBuildCellGrid();
            ResetCameraToDataCentroid();
        }

        _ = InspectObject; // suppress unused-event warning until Phase 5 picking
        _ = InspectCell;
    }

    internal void SelectObject(PlacedReference? obj)
    {
        // Phase 5 will frame the camera on the selected object. For now: no-op.
        _ = obj;
    }

    // Lifecycle -----------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _gpu = FalloutApp.Current.GetOrCreateGpuDevice();
        if (_gpu is null)
        {
            ShowStatus("3D view unavailable: no D3D11 backend on this machine.");
            return;
        }

        try
        {
            _cellGrid = new CellGridDebugRenderer(_gpu);
            _terrain = new TerrainRenderer(_gpu);
            _water = new WaterRenderer(_gpu);
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: render resource init failed: {0}", ex.Message);
            ShowStatus("3D view init failed — see logs.");
            return;
        }

        // The XAML markup compiler types RenderPanel as SwapChainPanel; subscribe input handlers.
        RenderPanel.SizeChanged += OnRenderPanelSizeChanged;
        RenderPanel.CompositionScaleChanged += OnRenderPanelCompositionScaleChanged;
        RenderPanel.KeyDown += OnRenderPanelKeyDown;
        RenderPanel.KeyUp += OnRenderPanelKeyUp;
        RenderPanel.LostFocus += OnRenderPanelLostFocus;
        RenderPanel.PointerPressed += OnRenderPanelPointerPressed;
        RenderPanel.PointerMoved += OnRenderPanelPointerMoved;
        RenderPanel.PointerReleased += OnRenderPanelPointerReleased;
        RenderPanel.PointerWheelChanged += OnRenderPanelPointerWheelChanged;

        RenderPanel.Focus(FocusState.Programmatic);

        // Walk-mode ground sampling: read the camera's current cell heightmap and bilinearly
        // interpolate. _cellGridLookup is set in TryBuildCellGrid below.
        _controller.GroundHeightSampler = SampleGroundHeight;

        TryBuildCellGrid();
        TryEnsureSurface();
    }

    private float? SampleGroundHeight(float worldX, float worldY)
    {
        return _cellGridLookup is null
            ? null
            : TerrainHeightSampler.Sample(_cellGridLookup, worldX, worldY);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachRenderLoop();

        RenderPanel.SizeChanged -= OnRenderPanelSizeChanged;
        RenderPanel.CompositionScaleChanged -= OnRenderPanelCompositionScaleChanged;
        RenderPanel.KeyDown -= OnRenderPanelKeyDown;
        RenderPanel.KeyUp -= OnRenderPanelKeyUp;
        RenderPanel.LostFocus -= OnRenderPanelLostFocus;
        RenderPanel.PointerPressed -= OnRenderPanelPointerPressed;
        RenderPanel.PointerMoved -= OnRenderPanelPointerMoved;
        RenderPanel.PointerReleased -= OnRenderPanelPointerReleased;
        RenderPanel.PointerWheelChanged -= OnRenderPanelPointerWheelChanged;

        DisposeRenderResources();
        _gpu = null;
        _ = _pointerStartPosition;
    }

    private void OnRenderPanelSizeChanged(object sender, SizeChangedEventArgs e) => TryEnsureSurface();
    private void OnRenderPanelCompositionScaleChanged(SwapChainPanel sender, object args) => TryEnsureSurface();

    private void TryEnsureSurface()
    {
        if (_gpu is null) return;

        var widthLayout = RenderPanel.ActualWidth;
        var heightLayout = RenderPanel.ActualHeight;
        if (widthLayout <= 0 || heightLayout <= 0) return;

        var width = (uint)Math.Max(1, Math.Round(widthLayout * RenderPanel.CompositionScaleX));
        var height = (uint)Math.Max(1, Math.Round(heightLayout * RenderPanel.CompositionScaleY));

        if (_surface is null)
        {
            _surface = GpuSwapChainSurface.Create(_gpu, RenderPanel, width, height);
            if (_surface is null)
            {
                ShowStatus("3D view: swapchain bind failed (see logs).");
                return;
            }
            HideStatus();
            _lastFrameTime = DateTime.UtcNow;
            AttachRenderLoop();
        }
        else
        {
            _surface.Resize(width, height);
        }
    }

    private void DisposeRenderResources()
    {
        _water?.Dispose();
        _water = null;
        _terrain?.Dispose();
        _terrain = null;
        _cellGrid?.Dispose();
        _cellGrid = null;
        _surface?.Dispose();
        _surface = null;
    }

    // Input ---------------------------------------------------------------------------------

    private void OnRenderPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // D1/D2/D3 toggle the wireframe / terrain / water layers; F toggles between fly
        // (free cam) and walk (ground-locked) camera modes. WinUI emits auto-repeat KeyDown
        // events while a key is held, so guard with a "first press" set to avoid flickering
        // the toggle 30 times per second.
        if (e.Key is VirtualKey.Number1 or VirtualKey.Number2 or VirtualKey.Number3 or VirtualKey.F)
        {
            if (_toggleKeysDown.Add(e.Key))
            {
                if (e.Key == VirtualKey.Number1) _showWireframe = !_showWireframe;
                else if (e.Key == VirtualKey.Number2) _showTerrain = !_showTerrain;
                else if (e.Key == VirtualKey.Number3) _showWater = !_showWater;
                else if (e.Key == VirtualKey.F)
                    _controller.Mode = _controller.Mode == CameraMode.Walk ? CameraMode.Fly : CameraMode.Walk;
            }
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
        if (delta != Vector2.Zero) _controller.OnMouseDelta(delta);
    }

    private void OnRenderPanelPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_mouseDragActive) return;
        RenderPanel.ReleasePointerCapture(e.Pointer);
        _mouseDragActive = false;
        e.Handled = true;
    }

    private void OnRenderPanelPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(RenderPanel).Properties.MouseWheelDelta;
        _controller.OnScroll(delta);
        e.Handled = true;
    }

    // Render loop ---------------------------------------------------------------------------

    private void AttachRenderLoop()
    {
        if (_renderLoopAttached) return;
        CompositionTarget.Rendering += OnRendering;
        _renderLoopAttached = true;
    }

    private void DetachRenderLoop()
    {
        if (!_renderLoopAttached) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderLoopAttached = false;
    }

    private void OnRendering(object? sender, object e)
    {
        if (_surface is null || _gpu is null) return;
        if (Visibility == Visibility.Collapsed) return;

        var now = DateTime.UtcNow;
        var deltaSeconds = (float)(now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;
        // Clamp pathological deltas (long pause, debugger break) to keep camera step bounded.
        if (deltaSeconds > 0.1f) deltaSeconds = 0.1f;

        _controller.Update(deltaSeconds);

        try
        {
            RenderFrame();
        }
        catch (Exception ex)
        {
            // App close races teardown of the SwapChainPanel's native buffers against
            // CompositionTarget.Rendering callbacks — Vortice's RTV wrapper can hold a stale
            // pointer that NPEs inside the COM call. Detach the loop on first failure so
            // we don't crash the process during normal shutdown.
            Log.Warn("WorldView3DControl: render frame failed, detaching loop: {0}", ex.Message);
            DetachRenderLoop();
        }
    }

    private void RenderFrame()
    {
        var ctx = _gpu!.Context;
        var rtv = _surface!.BackBufferRtv;
        var dsv = _surface.DepthStencilView;

        ctx.ClearRenderTargetView(rtv, new Color4(0x1B / 255f, 0x24 / 255f, 0x36 / 255f, 1f));
        ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1f, 0);
        ctx.OMSetRenderTargets(rtv, dsv);
        ctx.RSSetViewport(0, 0, _surface.Width, _surface.Height, 0, 1);

        var aspect = _surface.Width / (float)_surface.Height;
        var view = _camera.GetViewMatrix();
        var proj = _camera.GetProjectionMatrix(aspect);
        var viewProj = view * proj;

        // Cylinder culling: cells render iff their XY footprint is within FarPlane of the
        // camera's XY position. No yaw or pitch culling — only translation moves cells in or
        // out of the rendered set. We tried a yaw wedge but no fixed half-angle is sound at
        // every pitch: at near-vertical downward pitch, the view-space condition
        // y·cos(P) + H·sin(P) > 0 degenerates to "always true", and cells at any azimuth
        // from the yaw direction can be on-screen (visible as the missing chunk in the screenshot).
        var cylinder = new VisibilityCylinder(_camera.Position, _camera.FarPlane);

        // Layer order: terrain (writes depth) → water (reads depth, alpha-blended) →
        // wireframe (depth-disabled, drawn on top). Terrain processes cells closest-first
        // when the upload budget is tight, so the area under the camera fills first.
        var visibleTerrain = (_showTerrain ? _terrain?.Render(viewProj, cylinder) : null) ?? 0;
        if (_showWater) _water?.Render(viewProj, cylinder);
        var visibleWireframe = (_showWireframe ? _cellGrid?.Render(viewProj, cylinder) : null) ?? 0;

        _surface.Present();

        var visible = Math.Max(visibleTerrain, visibleWireframe);
        var totalCells = _terrain?.CellCount ?? _cellGrid?.CellCount ?? 0;
        UpdateHud(visible, totalCells);
    }

    // Camera framing ------------------------------------------------------------------------

    private void ResetCameraToDataCentroid()
    {
        if (_data is null) return;

        // Centroid of the currently selected worldspace's exterior cells. With the v3 Phase 2a
        // worldspace picker we always scope to one worldspace, so the camera frames the chosen
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
        var worldX = (float)(avgGridX * WorldGridConstants.CellSize);
        var worldY = (float)(avgGridY * WorldGridConstants.CellSize);

        // Position 2 cells south and well above the ground, pitched down ~30° looking north.
        _camera.Position = new Vector3(worldX, worldY - 8192f, 32768f);
        _camera.Yaw = 0f;
        _camera.Pitch = -MathF.PI / 6f;
    }

    private void WorldspaceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorldspaceSelectionEvent || _data is null) return;
        if (WorldspaceComboBox.SelectedIndex < 0) return;

        TryBuildCellGrid();
        ResetCameraToDataCentroid();
    }

    private void TryBuildCellGrid()
    {
        if (_data is null) return;

        var (cells, defaultWaterHeight) = GetSelectedWorldspaceCells(_data);
        var cellList = cells.ToList();
        _cellGrid?.LoadData(cellList);

        _cellGridLookup = BuildCellGridLookup(cellList);
        _terrain?.LoadData(_cellGridLookup);
        _water?.LoadData(_cellGridLookup, defaultWaterHeight);
    }

    /// <summary>
    ///     Returns the exterior-cell list + default water height for whatever entry is currently
    ///     selected in <c>WorldspaceComboBox</c>. ComboBox layout: indices 0..N-1 map to
    ///     <c>_data.Worldspaces</c>; an optional final entry maps to <c>_data.UnlinkedExteriorCells</c>.
    ///     Returns empty when nothing is selected (e.g. an empty file).
    /// </summary>
    private (IEnumerable<CellRecord> Cells, float? DefaultWaterHeight) GetSelectedWorldspaceCells(WorldViewData data)
    {
        var index = WorldspaceComboBox.SelectedIndex;
        if (index < 0) return (Enumerable.Empty<CellRecord>(), null);

        if (index < data.Worldspaces.Count)
        {
            var ws = data.Worldspaces[index];
            return (ws.Cells.Where(c => c.GridX is int && c.GridY is int), ws.DefaultWaterHeight);
        }

        // Tail entry: unlinked exterior cells. No worldspace → no DefaultWaterHeight fallback.
        return (data.UnlinkedExteriorCells.Where(c => c.GridX is int && c.GridY is int), null);
    }

    private static Dictionary<(int gx, int gy), CellRecord> BuildCellGridLookup(IEnumerable<CellRecord> exteriorCells)
    {
        // First-wins dedup by (GridX, GridY), matching CellGridDebugRenderer.LoadData's
        // policy so the three layers see the same per-cell record.
        var lookup = new Dictionary<(int gx, int gy), CellRecord>();
        foreach (var cell in exteriorCells)
        {
            if (cell.GridX is not int gx || cell.GridY is not int gy) continue;
            var key = (gx, gy);
            if (!lookup.ContainsKey(key)) lookup[key] = cell;
        }
        return lookup;
    }

    // HUD / status overlay ------------------------------------------------------------------

    private void UpdateHud(int visible, int total)
    {
        var w = _showWireframe ? "on" : "off";
        var t = _showTerrain ? "on" : "off";
        var wa = _showWater ? "on" : "off";
        var mode = _controller.Mode == CameraMode.Walk ? "walk" : "fly";
        var keyHint = _controller.Mode == CameraMode.Walk ? "WASD" : "WASD + Q/E";
        HudText.Text =
            $"Cells: {visible} / {total}   " +
            $"pos: ({_camera.Position.X:0}, {_camera.Position.Y:0}, {_camera.Position.Z:0})   " +
            $"speed: {_controller.MoveSpeed:0}   " +
            $"[F]mode:{mode} [1]wire:{w} [2]terrain:{t} [3]water:{wa}   " +
            $"{keyHint}   drag to look";
    }

    private void ShowStatus(string message)
    {
        StatusOverlay.Text = message;
        StatusOverlay.Visibility = Visibility.Visible;
        HudPanel.Visibility = Visibility.Collapsed;
    }

    private void HideStatus()
    {
        StatusOverlay.Visibility = Visibility.Collapsed;
        HudPanel.Visibility = Visibility.Visible;
    }
}
