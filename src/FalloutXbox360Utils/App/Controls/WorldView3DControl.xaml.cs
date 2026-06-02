using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
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

    private const float DefaultRenderDistance = WorldGridConstants.CellSize * 16f;
    private const float MinRenderDistance = WorldGridConstants.CellSize * 4f;
    private const float MaxRenderDistance = 800_000f;
    private const float RenderDistanceStep = 1.25f;

    private readonly CameraState _camera = new();
    private readonly FlythroughCameraController _controller;
    private readonly Vector2[] _pointerStartPosition = new Vector2[1];
    private readonly HashSet<VirtualKey> _toggleKeysDown = [];
    private readonly bool _showFrameStats =
        Environment.GetEnvironmentVariable("FALLOUT_VIEWER_FRAME_STATS") == "1";

    private CellGridDebugRenderer? _cellGrid;
    private TerrainRenderer? _terrain;
    private TerrainTextureResolver? _textureResolver;
    private TerrainOpacityTextureCache? _opacityCache;
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
    private WorldSpatialIndex? _spatialIndex;

    // Layer visibility — toggled by D1/D2/D3 keys, all default-on. D4 toggles textured-vs-VCLR-only.
    private bool _showWireframe = true;
    private bool _showTerrain = true;
    private bool _showWater = true;
    private bool _vclrOnlyMode;
    private float _renderDistance = DefaultRenderDistance;

    public WorldView3DControl()
    {
        InitializeComponent();
        _controller = new FlythroughCameraController(_camera);
        _camera.FarPlane = _renderDistance;
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

        // Tear down any prior terrain pipeline (a second LoadData = switching ESMs) so the
        // texture caches don't leak across data sets. Order: TerrainRenderer references the
        // resolver + opacity cache, so dispose it first.
        _terrain?.Dispose(); _terrain = null;
        _opacityCache?.Dispose(); _opacityCache = null;
        _textureResolver?.Dispose(); _textureResolver = null;

        if (_gpu is not null)
        {
            try
            {
                var bsas = DiscoverTextureBsaPaths(_data);
                if (bsas.Length == 0)
                {
                    // Common for standalone DMPs / standalone PC ESMs with no Load Order set.
                    // Every LTEX lookup will resolve to the white fallback, so terrain renders
                    // white-tinted instead of textured. Add an ESM with adjacent BSAs to the
                    // Load Order (e.g. from Full_Builds/.../Data) to fix.
                    Log.Warn(
                        "WorldView3DControl: no *Textures*.bsa from '{0}' or {1} Load Order paths — terrain will render white-tinted.",
                        Path.GetDirectoryName(_data.SourceFilePath ?? "") ?? "(unknown)",
                        _data.AdditionalDataPaths.Count);
                }
                else
                {
                    Log.Info("WorldView3DControl: discovered {0} texture BSA(s) for terrain.", bsas.Length);
                }
                _textureResolver = new TerrainTextureResolver(
                    _gpu.Device, _data.LandTexturesByFormId, _data.TextureSetsByFormId, bsas);
                _opacityCache = new TerrainOpacityTextureCache(_gpu.Device, capacity: 8192);
                _terrain = new TerrainRenderer(_gpu, _textureResolver, _opacityCache);
                _terrain.SetVclrOnlyMode(_vclrOnlyMode);
            }
            catch (Exception ex)
            {
                Log.Warn("WorldView3DControl: terrain pipeline init failed: {0}", ex.Message);
                _terrain = null;
                _opacityCache?.Dispose(); _opacityCache = null;
                _textureResolver?.Dispose(); _textureResolver = null;
            }
        }

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

    internal static void SelectObject(PlacedReference? obj)
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
            _water = new WaterRenderer(_gpu);
            // _terrain is deferred to LoadData since it needs the per-ESM LTEX/TXST dictionaries
            // and the BSA paths discovered from WorldViewData.SourceFilePath.
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
            : TerrainHeightSampler.Sample(_cellGridLookup, worldX, worldY, _data?.RenderCache);
    }

    /// <summary>
    ///     Collects texture-BSA paths from the primary data file plus every Load Order entry.
    ///     Each unique parent directory is globbed once (so a load order with 5 ESMs in the same
    ///     Data folder doesn't issue 5 identical filesystem scans). The result preserves Load
    ///     Order ordering: primary file first, then load-order entries in order, so a later DLC
    ///     ESM's BSAs win lookups for textures shared with the base game — matching the engine's
    ///     "later file overrides earlier" semantics that <see cref="NifTextureResolver" /> already
    ///     implements via source iteration order.
    /// </summary>
    private static string[] DiscoverTextureBsaPaths(WorldViewData data)
    {
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBsas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        AddFrom(data.SourceFilePath);
        // Defensive null guard: AdditionalDataPaths is settable, so a caller could in principle
        // null it out. Empty-list default is what we want.
        if (data.AdditionalDataPaths is not null)
        {
            foreach (var path in data.AdditionalDataPaths) AddFrom(path);
        }
        return result.ToArray();

        void AddFrom(string? candidatePath)
        {
            if (string.IsNullOrEmpty(candidatePath)) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(candidatePath));
            if (string.IsNullOrEmpty(dir) || !seenDirs.Add(dir)) return;
            var bsas = BsaDiscovery.Discover(candidatePath).TexturesBsaPaths;
            foreach (var bsa in bsas)
            {
                if (seenBsas.Add(bsa)) result.Add(bsa);
            }
        }
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
        _opacityCache?.Dispose();
        _opacityCache = null;
        _textureResolver?.Dispose();
        _textureResolver = null;
        _cellGrid?.Dispose();
        _cellGrid = null;
        _surface?.Dispose();
        _surface = null;
    }

    // Input ---------------------------------------------------------------------------------

    private void OnRenderPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // D1/D2/D3 toggle the wireframe / terrain / water layers; D4 toggles the
        // textured-terrain (Phase 2b) vs. VCLR-only (Phase 2a) debug mode; F toggles between
        // fly (free cam) and walk (ground-locked) camera modes. PageUp/PageDown adjust the
        // streaming render distance. WinUI emits auto-repeat KeyDown events while a key is held,
        // so guard with a "first press" set.
        if (e.Key is VirtualKey.Number1 or VirtualKey.Number2 or VirtualKey.Number3
            or VirtualKey.Number4 or VirtualKey.F or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            if (_toggleKeysDown.Add(e.Key))
            {
                if (e.Key == VirtualKey.Number1) _showWireframe = !_showWireframe;
                else if (e.Key == VirtualKey.Number2) _showTerrain = !_showTerrain;
                else if (e.Key == VirtualKey.Number3) _showWater = !_showWater;
                else if (e.Key == VirtualKey.Number4)
                {
                    _vclrOnlyMode = !_vclrOnlyMode;
                    _terrain?.SetVclrOnlyMode(_vclrOnlyMode);
                }
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
            // Capture the full exception (including stack trace and inner exception chain) so
            // we can identify which RenderFrame call is throwing — ex.Message alone strips the
            // frame where the NRE happens, which is what we need to root-cause this.
            Log.Warn("WorldView3DControl: render frame failed, detaching loop:\n{0}", ex);
            DetachRenderLoop();
        }
    }

    private void RenderFrame()
    {
        var ctx = _gpu!.Context;
        // Acquire a FRESH back-buffer RTV every frame (see GpuSwapChainSurface.AcquireBackBufferRtv
        // for the rationale — caching across frames produces a Vortice RTV wrapper with a stale
        // native COM pointer after WinUI's first composition pass on the swap chain).
        using var rtv = _surface!.AcquireBackBufferRtv();
        var dsv = _surface.DepthStencilView;

        ctx.ClearRenderTargetView(rtv, new Color4(0x1B / 255f, 0x24 / 255f, 0x36 / 255f, 1f));
        ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1f, 0);
        ctx.OMSetRenderTargets(rtv, dsv);
        ctx.RSSetViewport(0, 0, _surface.Width, _surface.Height, 0, 1);

        var aspect = _surface.Width / (float)_surface.Height;
        _camera.FarPlane = _renderDistance;
        var view = _camera.GetViewMatrix();
        var proj = _camera.GetProjectionMatrix(aspect);
        var viewProj = view * proj;

        // Cylinder culling: cells render iff their XY footprint is within the streaming
        // render distance of the camera's XY position. No yaw or pitch culling — only
        // translation moves cells in or out of the rendered set. We tried a yaw wedge but
        // no fixed half-angle is sound at every pitch: at near-vertical downward pitch,
        // the view-space condition y·cos(P) + H·sin(P) > 0 degenerates to "always true",
        // and cells at any azimuth from the yaw direction can be on-screen.
        var cylinder = new VisibilityCylinder(_camera.Position, _renderDistance);

        // Historical note: the projection FarPlane default was 800k world units (~195 cells).
        // That is useful for screenshots, but it makes the real-time path submit most or all
        // terrain in large worldspaces. PageUp/PageDown can widen/narrow the default budget.
        // Layer order: terrain (writes depth) → water (reads depth, alpha-blended) →
        // wireframe (depth-disabled, drawn on top). Terrain processes cells closest-first
        // when the upload budget is tight, so the area under the camera fills first.
        var visibleTerrain = (_showTerrain ? _terrain?.Render(viewProj, cylinder) : null) ?? 0;
        var visibleWater = (_showWater ? _water?.Render(viewProj, cylinder) : null) ?? 0;
        var visibleWireframe = (_showWireframe ? _cellGrid?.Render(viewProj, cylinder) : null) ?? 0;

        _surface.Present();

        var visible = Math.Max(visibleTerrain, visibleWireframe);
        var totalCells = _terrain?.CellCount ?? _cellGrid?.CellCount ?? 0;
        UpdateHud(visible, totalCells, visibleWater);
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

        var activeWorldspaceFormId = GetSelectedWorldspaceFormId(_data);
        var markers = GetSelectedWorldspaceMarkers(_data, activeWorldspaceFormId);
        _spatialIndex = WorldSpatialIndex.Build(
            _data, cellList, markers, activeWorldspaceFormId, defaultWaterHeight);

        _cellGridLookup = _spatialIndex.CellsByGrid.ToDictionary(kv => kv.Key, kv => kv.Value);
        _cellGrid?.LoadData(cellList, _spatialIndex);
        _terrain?.LoadData(_cellGridLookup, _spatialIndex, _data.RenderCache);
        _water?.LoadData(_cellGridLookup, defaultWaterHeight, _spatialIndex);
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

    private uint? GetSelectedWorldspaceFormId(WorldViewData data)
    {
        var index = WorldspaceComboBox.SelectedIndex;
        return index >= 0 && index < data.Worldspaces.Count
            ? data.Worldspaces[index].FormId
            : null;
    }

    private static List<PlacedReference> GetSelectedWorldspaceMarkers(WorldViewData data, uint? worldspaceFormId)
    {
        if (worldspaceFormId is uint ws &&
            data.MarkersByWorldspace.TryGetValue(ws, out var markers))
        {
            return markers;
        }

        return worldspaceFormId is null ? data.UnlinkedMapMarkers : [];
    }

    // HUD / status overlay ------------------------------------------------------------------

    private void UpdateHud(int visible, int total, int visibleWater)
    {
        var w = _showWireframe ? "on" : "off";
        var t = _showTerrain ? "on" : "off";
        var wa = _showWater ? "on" : "off";
        var v = _vclrOnlyMode ? "on" : "off";
        var mode = _controller.Mode == CameraMode.Walk ? "walk" : "fly";
        var keyHint = _controller.Mode == CameraMode.Walk ? "WASD" : "WASD + Q/E";
        var text =
            $"Cells: {visible} / {total}   " +
            $"pos: ({_camera.Position.X:0}, {_camera.Position.Y:0}, {_camera.Position.Z:0})   " +
            $"speed: {_controller.MoveSpeed:0}   " +
            $"dist: {_renderDistance / WorldGridConstants.CellSize:0.#}c   " +
            $"[F]mode:{mode} [1]wire:{w} [2]terrain:{t} [3]water:{wa} [4]vclr-only:{v}   " +
            $"PgUp/PgDn   {keyHint}   drag to look";
        if (_showFrameStats && _terrain is not null)
        {
            var stats = _terrain.LastStats;
            text +=
                $"   stats cand:{stats.VisibleCandidates} draw:{stats.TerrainDraws} " +
                $"up:{stats.NewUploads} texMiss:{stats.TextureCacheMisses} " +
                $"opMiss:{stats.OpacityCacheMisses} water:{visibleWater} cpu:{stats.CpuFrameMilliseconds:0.0}ms";
        }

        HudText.Text = text;
    }

    private void SetRenderDistance(float distance)
    {
        _renderDistance = Math.Clamp(distance, MinRenderDistance, MaxRenderDistance);
        _camera.FarPlane = _renderDistance;
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
