using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
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
    private readonly bool _profileLogging =
        Environment.GetEnvironmentVariable("FALLOUT_VIEWER_PROFILE_LOG") == "1";
    private readonly int _profileLogIntervalMilliseconds =
        ParsePositiveInt(Environment.GetEnvironmentVariable("FALLOUT_VIEWER_PROFILE_INTERVAL_MS"), 2000);
    private readonly FrameProfileAccumulator _profileAccumulator = new();

    private CellGridDebugRenderer? _cellGrid;
    private TerrainRenderer? _terrain;
    private TerrainTextureResolver? _textureResolver;
    private TerrainOpacityTextureCache? _opacityCache;
    private WaterRenderer? _water;
    // v3 Phase 3 placed-object pipeline. Parallel to the terrain pipeline; owns its own
    // NifTextureResolver + GpuTextureCache so terrain texture caching isn't perturbed.
    private NpcMeshArchiveSet? _meshArchives;
    private NifTextureResolver? _referenceTextureResolver;
    private GpuTextureCache? _referenceTextureCache;
    private ReferenceMeshCache? _referenceMeshCache;
    private ReferenceRenderer? _references;
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
    private double _lastControllerUpdateMilliseconds;

    // Layer visibility — toggled by D1/D2/D3 keys, all default-on. D4 toggles textured-vs-VCLR-only.
    // D5 toggles placed-object (REFR) rendering (v3 Phase 3).
    private bool _showWireframe = true;
    private bool _showTerrain = true;
    private bool _showWater = true;
    private bool _vclrOnlyMode;
    private bool _showReferences = true;
    private float _renderDistance = DefaultRenderDistance;

    public WorldView3DControl()
    {
        InitializeComponent();
        _controller = new FlythroughCameraController(_camera);
        _camera.FarPlane = _renderDistance;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (_profileLogging)
        {
            Log.Info(
                "WorldView3DControl: profile logging enabled; interval={0}ms. " +
                "Set FALLOUT_VIEWER_PROFILE_LOG=0 to disable.",
                _profileLogIntervalMilliseconds);
        }
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

        // Tear down any prior pipelines (a second LoadData = switching ESMs) so the texture
        // caches don't leak across data sets. ReferenceRenderer references the mesh+texture
        // caches; dispose those last. TerrainRenderer references the resolver + opacity
        // cache, so dispose it first.
        DisposeReferencePipeline();
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

            TryInitReferencePipeline();
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

    /// <summary>
    ///     Mesh-BSA parallel of <see cref="DiscoverTextureBsaPaths" />. Globs the primary file's
    ///     directory + every Load Order entry's directory for the BSA(s) that <c>BsaDiscovery</c>
    ///     classifies as meshes archives (the primary + each entry's extras). Dedupes by full
    ///     path so identical Load Order entries don't open the same BSA twice.
    /// </summary>
    private static string[] DiscoverMeshBsaPaths(WorldViewData data)
    {
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBsas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        AddFrom(data.SourceFilePath);
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
            var discovery = BsaDiscovery.Discover(candidatePath);
            if (discovery.MeshesBsaPath is { } primary && seenBsas.Add(primary))
            {
                result.Add(primary);
            }
            if (discovery.ExtraMeshesBsaPaths is { } extras)
            {
                foreach (var extra in extras)
                {
                    if (seenBsas.Add(extra)) result.Add(extra);
                }
            }
        }
    }

    /// <summary>
    ///     v3 Phase 3 placed-object pipeline init. Mirrors the terrain pipeline init in
    ///     <see cref="LoadData" /> but lives in its own method since it needs the Meshes BSA
    ///     discovery in addition to the textures BSAs. Soft-fails when no Meshes BSA is found
    ///     (REFRs simply don't render — terrain still does).
    /// </summary>
    private void TryInitReferencePipeline()
    {
        if (_gpu is null || _data is null) return;

        try
        {
            var meshBsas = DiscoverMeshBsaPaths(_data);
            if (meshBsas.Length == 0)
            {
                Log.Warn(
                    "WorldView3DControl: no *Meshes*.bsa from '{0}' or {1} Load Order paths — REFRs will be skipped. Add an ESM whose Data folder contains a Meshes BSA to the Load Order.",
                    Path.GetDirectoryName(_data.SourceFilePath ?? "") ?? "(unknown)",
                    _data.AdditionalDataPaths.Count);
                return;
            }

            var textureBsas = DiscoverTextureBsaPaths(_data);
            _meshArchives = NpcMeshArchiveSet.Open(meshBsas[0], meshBsas.Length > 1 ? meshBsas[1..] : null);
            _referenceTextureResolver = new NifTextureResolver(textureBsas);
            _referenceTextureCache = new GpuTextureCache(_gpu.Device);
            _referenceMeshCache = new ReferenceMeshCache(
                _gpu.Device, _meshArchives, _referenceTextureResolver, _referenceTextureCache, capacity: 2048);
            _references = new ReferenceRenderer(_gpu, _referenceMeshCache);
            Log.Info("WorldView3DControl: reference pipeline initialised ({0} meshes BSA(s), {1} textures BSA(s)).",
                meshBsas.Length, textureBsas.Length);
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: reference pipeline init failed: {0}", ex.Message);
            DisposeReferencePipeline();
        }
    }

    /// <summary>Releases every resource owned by the placed-object pipeline in safe order.</summary>
    private void DisposeReferencePipeline()
    {
        _references?.Dispose(); _references = null;
        _referenceMeshCache?.Dispose(); _referenceMeshCache = null;
        _referenceTextureCache?.Dispose(); _referenceTextureCache = null;
        _referenceTextureResolver?.Dispose(); _referenceTextureResolver = null;
        _meshArchives?.Dispose(); _meshArchives = null;
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
        // Reference pipeline disposes first — it borrows the texture caches / mesh archives
        // but owns its own copy, so this is independent of the terrain stack.
        DisposeReferencePipeline();
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
            or VirtualKey.Number4 or VirtualKey.Number5
            or VirtualKey.F or VirtualKey.PageUp or VirtualKey.PageDown)
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
                else if (e.Key == VirtualKey.Number5) _showReferences = !_showReferences;
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

        var controllerStarted = Stopwatch.GetTimestamp();
        _controller.Update(deltaSeconds);
        _lastControllerUpdateMilliseconds = ElapsedMilliseconds(controllerStarted);

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
        var frameStarted = Stopwatch.GetTimestamp();
        var stageStarted = frameStarted;
        var ctx = _gpu!.Context;
        // Acquire a FRESH back-buffer RTV every frame (see GpuSwapChainSurface.AcquireBackBufferRtv
        // for the rationale — caching across frames produces a Vortice RTV wrapper with a stale
        // native COM pointer after WinUI's first composition pass on the swap chain).
        using var rtv = _surface!.AcquireBackBufferRtv();
        var dsv = _surface.DepthStencilView;
        var acquireMilliseconds = ElapsedMilliseconds(stageStarted);

        stageStarted = Stopwatch.GetTimestamp();
        ctx.ClearRenderTargetView(rtv, new Color4(0x1B / 255f, 0x24 / 255f, 0x36 / 255f, 1f));
        ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1f, 0);
        ctx.OMSetRenderTargets(rtv, dsv);
        ctx.RSSetViewport(0, 0, _surface.Width, _surface.Height, 0, 1);
        var clearSetupMilliseconds = ElapsedMilliseconds(stageStarted);

        stageStarted = Stopwatch.GetTimestamp();
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
        var cameraMilliseconds = ElapsedMilliseconds(stageStarted);

        // Historical note: the projection FarPlane default was 800k world units (~195 cells).
        // That is useful for screenshots, but it makes the real-time path submit most or all
        // terrain in large worldspaces. PageUp/PageDown can widen/narrow the default budget.
        // Layer order: terrain (writes depth) → water (reads depth, alpha-blended) →
        // wireframe (depth-disabled, drawn on top). Terrain processes cells closest-first
        // when the upload budget is tight, so the area under the camera fills first.
        stageStarted = Stopwatch.GetTimestamp();
        var visibleTerrain = (_showTerrain ? _terrain?.Render(viewProj, cylinder) : null) ?? 0;
        var terrainMilliseconds = ElapsedMilliseconds(stageStarted);

        // Phase 3 — placed objects render between terrain (depth write) and water (alpha-blend
        // over depth). Alpha-tested foliage discards in the shader, no sorting needed.
        stageStarted = Stopwatch.GetTimestamp();
        var visibleReferences = (_showReferences ? _references?.Render(viewProj, cylinder) : null) ?? 0;
        var referencesMilliseconds = ElapsedMilliseconds(stageStarted);

        stageStarted = Stopwatch.GetTimestamp();
        var visibleWater = (_showWater ? _water?.Render(viewProj, cylinder) : null) ?? 0;
        var waterMilliseconds = ElapsedMilliseconds(stageStarted);

        stageStarted = Stopwatch.GetTimestamp();
        var visibleWireframe = (_showWireframe ? _cellGrid?.Render(viewProj, cylinder) : null) ?? 0;
        var wireframeMilliseconds = ElapsedMilliseconds(stageStarted);

        stageStarted = Stopwatch.GetTimestamp();
        _surface.Present();
        var presentMilliseconds = ElapsedMilliseconds(stageStarted);

        stageStarted = Stopwatch.GetTimestamp();
        var visible = Math.Max(visibleTerrain, visibleWireframe);
        var totalCells = _terrain?.CellCount ?? _cellGrid?.CellCount ?? 0;
        UpdateHud(visible, totalCells, visibleWater, visibleReferences);
        var hudMilliseconds = ElapsedMilliseconds(stageStarted);

        // Surface the reference-stage timing in the debug log even without extending the
        // FrameProfileAccumulator schema (its shape is owned by the user's instrumentation;
        // I don't want to step on it). Emits at the same cadence as the profile log.
        if (_profileLogging && referencesMilliseconds > 0.1)
        {
            Log.Debug("Phase3 refs draw={0} ms={1:0.00}", visibleReferences, referencesMilliseconds);
        }

        var sample = new FrameProfileSample(
            TotalMilliseconds: ElapsedMilliseconds(frameStarted),
            ControllerMilliseconds: _lastControllerUpdateMilliseconds,
            AcquireMilliseconds: acquireMilliseconds,
            ClearSetupMilliseconds: clearSetupMilliseconds,
            CameraMilliseconds: cameraMilliseconds,
            TerrainMilliseconds: terrainMilliseconds,
            WaterMilliseconds: waterMilliseconds,
            WireframeMilliseconds: wireframeMilliseconds,
            PresentMilliseconds: presentMilliseconds,
            HudMilliseconds: hudMilliseconds,
            VisibleTerrain: visibleTerrain,
            VisibleWater: visibleWater,
            VisibleWireframe: visibleWireframe,
            TotalCells: totalCells,
            RenderDistanceCells: _renderDistance / WorldGridConstants.CellSize);
        MaybeLogProfile(sample);
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
        _references?.LoadData(_data.RenderCache, _cellGridLookup);
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

    private void UpdateHud(int visible, int total, int visibleWater, int visibleReferences)
    {
        var w = _showWireframe ? "on" : "off";
        var t = _showTerrain ? "on" : "off";
        var wa = _showWater ? "on" : "off";
        var v = _vclrOnlyMode ? "on" : "off";
        var r = _showReferences ? "on" : "off";
        var mode = _controller.Mode == CameraMode.Walk ? "walk" : "fly";
        var keyHint = _controller.Mode == CameraMode.Walk ? "WASD" : "WASD + Q/E";
        var text =
            $"Cells: {visible} / {total}   refs: {visibleReferences}   " +
            $"pos: ({_camera.Position.X:0}, {_camera.Position.Y:0}, {_camera.Position.Z:0})   " +
            $"speed: {_controller.MoveSpeed:0}   " +
            $"dist: {_renderDistance / WorldGridConstants.CellSize:0.#}c   " +
            $"[F]mode:{mode} [1]wire:{w} [2]terrain:{t} [3]water:{wa} [4]vclr-only:{v} [5]refs:{r}   " +
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

    private void MaybeLogProfile(FrameProfileSample sample)
    {
        if (!_profileLogging)
        {
            return;
        }

        var terrain = _showTerrain ? _terrain?.LastStats.Snapshot() : null;
        var water = _showWater ? _water?.LastStats.Snapshot() : null;
        var wireframe = _showWireframe ? _cellGrid?.LastStats.Snapshot() : null;
        _profileAccumulator.Add(sample, terrain, water, wireframe);
        if (_profileAccumulator.TryFlush(_profileLogIntervalMilliseconds, out var message))
        {
            Log.Info(message);
        }
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

    private static int ParsePositiveInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               parsed > 0
            ? parsed
            : fallback;
    }

    private static double ElapsedMilliseconds(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private readonly record struct FrameProfileSample(
        double TotalMilliseconds,
        double ControllerMilliseconds,
        double AcquireMilliseconds,
        double ClearSetupMilliseconds,
        double CameraMilliseconds,
        double TerrainMilliseconds,
        double WaterMilliseconds,
        double WireframeMilliseconds,
        double PresentMilliseconds,
        double HudMilliseconds,
        int VisibleTerrain,
        int VisibleWater,
        int VisibleWireframe,
        int TotalCells,
        float RenderDistanceCells);

    private sealed class FrameProfileAccumulator
    {
        private long _intervalStarted = Stopwatch.GetTimestamp();
        private int _frames;
        private double _frameTotal;
        private double _frameMax;
        private double _controller;
        private double _acquire;
        private double _clearSetup;
        private double _camera;
        private double _terrainFrame;
        private double _waterFrame;
        private double _wireframeFrame;
        private double _present;
        private double _hud;
        private double _terrainState;
        private double _terrainGather;
        private double _terrainSort;
        private double _terrainDrawLoop;
        private double _terrainQuadrants;
        private double _terrainMeshUpload;
        private double _terrainPreUpload;
        private double _terrainCpu;
        private double _waterState;
        private double _waterGather;
        private double _waterInstanceBuild;
        private double _waterUpload;
        private double _waterDrawCall;
        private double _waterCpu;
        private double _wireGather;
        private double _wireVertexBuild;
        private double _wireUpload;
        private double _wireDrawCall;
        private double _wireCpu;
        private double _visibleTerrain;
        private double _visibleWater;
        private double _visibleWireframe;
        private double _terrainCandidates;
        private double _terrainDraws;
        private double _terrainQuadrantDraws;
        private double _terrainUploads;
        private double _terrainPreUploads;
        private double _textureMisses;
        private double _opacityMisses;
        private int _lastTotalCells;
        private float _lastRenderDistanceCells;

        internal void Add(
            FrameProfileSample sample,
            WorldRenderStats? terrain,
            WorldRenderStats? water,
            WorldRenderStats? wireframe)
        {
            _frames++;
            _frameTotal += sample.TotalMilliseconds;
            _frameMax = Math.Max(_frameMax, sample.TotalMilliseconds);
            _controller += sample.ControllerMilliseconds;
            _acquire += sample.AcquireMilliseconds;
            _clearSetup += sample.ClearSetupMilliseconds;
            _camera += sample.CameraMilliseconds;
            _terrainFrame += sample.TerrainMilliseconds;
            _waterFrame += sample.WaterMilliseconds;
            _wireframeFrame += sample.WireframeMilliseconds;
            _present += sample.PresentMilliseconds;
            _hud += sample.HudMilliseconds;
            _visibleTerrain += sample.VisibleTerrain;
            _visibleWater += sample.VisibleWater;
            _visibleWireframe += sample.VisibleWireframe;
            _lastTotalCells = sample.TotalCells;
            _lastRenderDistanceCells = sample.RenderDistanceCells;

            if (terrain is not null)
            {
                _terrainState += terrain.StateSetupMilliseconds;
                _terrainGather += terrain.VisibleGatherMilliseconds;
                _terrainSort += terrain.VisibleSortMilliseconds;
                _terrainDrawLoop += terrain.DrawLoopMilliseconds;
                _terrainQuadrants += terrain.QuadrantDrawMilliseconds;
                _terrainMeshUpload += terrain.MeshBuildUploadMilliseconds;
                _terrainPreUpload += terrain.NeighborPreUploadMilliseconds;
                _terrainCpu += terrain.CpuFrameMilliseconds;
                _terrainCandidates += terrain.VisibleCandidates;
                _terrainDraws += terrain.TerrainDraws;
                _terrainQuadrantDraws += terrain.TerrainQuadrantDraws;
                _terrainUploads += terrain.NewUploads;
                _terrainPreUploads += terrain.NewPreUploads;
                _textureMisses += terrain.TextureCacheMisses;
                _opacityMisses += terrain.OpacityCacheMisses;
            }

            if (water is not null)
            {
                _waterState += water.StateSetupMilliseconds;
                _waterGather += water.VisibleGatherMilliseconds;
                _waterInstanceBuild += water.InstanceBuildMilliseconds;
                _waterUpload += water.GpuUploadMilliseconds;
                _waterDrawCall += water.DrawCallMilliseconds;
                _waterCpu += water.CpuFrameMilliseconds;
            }

            if (wireframe is not null)
            {
                _wireGather += wireframe.VisibleGatherMilliseconds;
                _wireVertexBuild += wireframe.InstanceBuildMilliseconds;
                _wireUpload += wireframe.GpuUploadMilliseconds;
                _wireDrawCall += wireframe.DrawCallMilliseconds;
                _wireCpu += wireframe.CpuFrameMilliseconds;
            }
        }

        internal bool TryFlush(int intervalMilliseconds, out string message)
        {
            var elapsed = Stopwatch.GetElapsedTime(_intervalStarted).TotalMilliseconds;
            if (_frames == 0 || elapsed < intervalMilliseconds)
            {
                message = "";
                return false;
            }

            double Avg(double value) => value / _frames;
            // Apply invariant formatting per piece because concatenating interpolated strings
            // first would format each segment with the current UI culture.
            message =
                string.Create(CultureInfo.InvariantCulture, $"3D profile {_frames}f/{elapsed / 1000.0:0.0}s cells={_lastTotalCells} dist={_lastRenderDistanceCells:0.#}c ") +
                string.Create(CultureInfo.InvariantCulture, $"frame avg/max={Avg(_frameTotal):0.00}/{_frameMax:0.00}ms ") +
                string.Create(CultureInfo.InvariantCulture, $"stages ctrl={Avg(_controller):0.00} acquire={Avg(_acquire):0.00} clear={Avg(_clearSetup):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"camera={Avg(_camera):0.00} terrain={Avg(_terrainFrame):0.00} water={Avg(_waterFrame):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"wire={Avg(_wireframeFrame):0.00} present={Avg(_present):0.00} hud={Avg(_hud):0.00} | ") +
                string.Create(CultureInfo.InvariantCulture, $"terrain cpu={Avg(_terrainCpu):0.00} state={Avg(_terrainState):0.00} gather={Avg(_terrainGather):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"sort={Avg(_terrainSort):0.00} loop={Avg(_terrainDrawLoop):0.00} quadrants={Avg(_terrainQuadrants):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"meshUpload={Avg(_terrainMeshUpload):0.00} preUpload={Avg(_terrainPreUpload):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"cand={Avg(_terrainCandidates):0.0} visible={Avg(_visibleTerrain):0.0} cells={Avg(_terrainDraws):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"qdraw={Avg(_terrainQuadrantDraws):0.0} uploads={Avg(_terrainUploads):0.0}+{Avg(_terrainPreUploads):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"texMiss={Avg(_textureMisses):0.0} opMiss={Avg(_opacityMisses):0.0} | ") +
                string.Create(CultureInfo.InvariantCulture, $"water cpu={Avg(_waterCpu):0.00} state={Avg(_waterState):0.00} gather={Avg(_waterGather):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"build={Avg(_waterInstanceBuild):0.00} upload={Avg(_waterUpload):0.00} draw={Avg(_waterDrawCall):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"cells={Avg(_visibleWater):0.0} | ") +
                string.Create(CultureInfo.InvariantCulture, $"wire cpu={Avg(_wireCpu):0.00} gather={Avg(_wireGather):0.00} vertices={Avg(_wireVertexBuild):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"upload={Avg(_wireUpload):0.00} draw={Avg(_wireDrawCall):0.00} cells={Avg(_visibleWireframe):0.0}");

            Reset();
            return true;
        }

        private void Reset()
        {
            _intervalStarted = Stopwatch.GetTimestamp();
            _frames = 0;
            _frameTotal = 0;
            _frameMax = 0;
            _controller = 0;
            _acquire = 0;
            _clearSetup = 0;
            _camera = 0;
            _terrainFrame = 0;
            _waterFrame = 0;
            _wireframeFrame = 0;
            _present = 0;
            _hud = 0;
            _terrainState = 0;
            _terrainGather = 0;
            _terrainSort = 0;
            _terrainDrawLoop = 0;
            _terrainQuadrants = 0;
            _terrainMeshUpload = 0;
            _terrainPreUpload = 0;
            _terrainCpu = 0;
            _waterState = 0;
            _waterGather = 0;
            _waterInstanceBuild = 0;
            _waterUpload = 0;
            _waterDrawCall = 0;
            _waterCpu = 0;
            _wireGather = 0;
            _wireVertexBuild = 0;
            _wireUpload = 0;
            _wireDrawCall = 0;
            _wireCpu = 0;
            _visibleTerrain = 0;
            _visibleWater = 0;
            _visibleWireframe = 0;
            _terrainCandidates = 0;
            _terrainDraws = 0;
            _terrainQuadrantDraws = 0;
            _terrainUploads = 0;
            _terrainPreUploads = 0;
            _textureMisses = 0;
            _opacityMisses = 0;
        }
    }
}
