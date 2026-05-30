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

    private CellGridDebugRenderer? _cellGrid;
    private WorldViewData? _data;
    private GpuDevice? _gpu;
    private DateTime _lastFrameTime;
    private bool _mouseDragActive;
    private Vector2 _previousPointerPosition;
    private bool _renderLoopAttached;
    private GpuSwapChainSurface? _surface;

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
        TryBuildCellGrid();
        ResetCameraToDataCentroid();
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

        TryBuildCellGrid();
        TryEnsureSurface();
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
        _cellGrid?.Dispose();
        _cellGrid = null;
        _surface?.Dispose();
        _surface = null;
    }

    // Input ---------------------------------------------------------------------------------

    private void OnRenderPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _controller.OnKeyDown(e.Key);
        e.Handled = true;
    }

    private void OnRenderPanelKeyUp(object sender, KeyRoutedEventArgs e)
    {
        _controller.OnKeyUp(e.Key);
        e.Handled = true;
    }

    private void OnRenderPanelLostFocus(object sender, RoutedEventArgs e)
    {
        // Avoid stuck movement keys when focus drops (e.g. user clicks elsewhere mid-stride).
        _controller.ClearKeys();
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
        RenderFrame();
    }

    private void RenderFrame()
    {
        var ctx = _gpu!.Context;
        var rtv = _surface!.BackBufferRtv;

        ctx.ClearRenderTargetView(rtv, new Color4(0x1B / 255f, 0x24 / 255f, 0x36 / 255f, 1f));
        ctx.OMSetRenderTargets(rtv);
        ctx.RSSetViewport(0, 0, _surface.Width, _surface.Height, 0, 1);

        var aspect = _surface.Width / (float)_surface.Height;
        var view = _camera.GetViewMatrix();
        var proj = _camera.GetProjectionMatrix(aspect);
        var viewProj = view * proj;
        var frustum = Frustum.FromViewProjection(viewProj);

        var visibleCells = _cellGrid?.Render(viewProj, frustum) ?? 0;
        var totalCells = _cellGrid?.CellCount ?? 0;

        _surface.Present();

        UpdateHud(visibleCells, totalCells);
    }

    // Camera framing ------------------------------------------------------------------------

    private void ResetCameraToDataCentroid()
    {
        if (_data is null) return;

        // Compute the centroid of all exterior cells across all worldspaces. Phase 1 doesn't
        // filter by worldspace; Phase 2 will add a worldspace selector mirroring the 2D view.
        double sumX = 0, sumY = 0;
        var count = 0;
        foreach (var cell in EnumerateExteriorCells(_data))
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

    private void TryBuildCellGrid()
    {
        if (_cellGrid is null || _data is null) return;
        _cellGrid.LoadData(EnumerateExteriorCells(_data));
    }

    private static IEnumerable<CellRecord> EnumerateExteriorCells(WorldViewData data)
    {
        foreach (var worldspace in data.Worldspaces)
        {
            foreach (var cell in worldspace.Cells)
            {
                if (cell.GridX is int && cell.GridY is int)
                    yield return cell;
            }
        }
    }

    // HUD / status overlay ------------------------------------------------------------------

    private void UpdateHud(int visible, int total)
    {
        HudText.Text =
            $"Cells: {visible} / {total}   " +
            $"pos: ({_camera.Position.X:0}, {_camera.Position.Y:0}, {_camera.Position.Z:0})   " +
            $"speed: {_controller.MoveSpeed:0}   " +
            $"WASD + Q/E   drag to look   scroll = speed";
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
