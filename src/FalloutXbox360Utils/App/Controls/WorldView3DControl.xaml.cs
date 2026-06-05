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
using Windows.System;

namespace FalloutXbox360Utils;

/// <summary>
///     v3 worldspace view. Hosts a <see cref="SwapChainPanel" /> backed by a Vortice D3D12
///     swapchain and renders terrain + placed references + water + cell-grid wireframe via
///     the D3D12 renderer interfaces. A <see cref="FlythroughCameraController" /> integrates
///     WASD/Q/E + mouse-look + scroll into a <see cref="CameraState" /> each frame.
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
    private readonly string? _stressScene =
        Environment.GetEnvironmentVariable("FALLOUT_VIEWER_STRESS_SCENE");
    private readonly FrameProfileAccumulator _profileAccumulator = new();
    // v3 Pass 4 Step 3 cleanup — D3D11 stack deleted. The post-cutover backend is D3D12-only;
    // the `_useD3D12` flag, `FALLOUT_VIEWER_D3D11` rollback, and dual-stack field set are all
    // gone. Renderer fields stay interface-typed (`I*Renderer`) so the single concrete D3D12
    // impl plugs in without WorldView caring about types beyond the public surface.
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.Abstractions.ICellGridRenderer? _cellGrid;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.Abstractions.ITerrainRenderer? _terrain;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.TerrainTextureResolver12? _textureResolver12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.Abstractions.IWaterRenderer? _water;
    // v3 Phase 3 placed-object pipeline. Parallel to the terrain pipeline; owns separate
    // CPU NIF texture metadata and D3D12 GPU texture payload resolvers.
    private NpcMeshArchiveSet? _meshArchives;
    private NifTextureResolver? _referenceTextureResolver;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.NifGpuTextureResolver? _referenceGpuTextureResolver12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTextureCache12? _referenceTextureCache12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceMeshCache12? _referenceMeshCache12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.Abstractions.IReferenceRenderer? _references;
    private WorldViewData? _data;
    private DateTime _lastFrameTime;
    private bool _mouseDragActive;
    private Vector2 _previousPointerPosition;
    private bool _renderLoopAttached;
    // v3 Pass 4 Step 3 — D3D12-only backend. The renderer interfaces (`I*Renderer`) plug
    // straight into the D3D12 concrete impls; no D3D11 fallback fields remain.
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDevice12? _gpu12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuSwapChainSurface12? _surface12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12? _commandRecorder12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRingBuffer12? _ringBuffer12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12? _cbvSrvUavHeap12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12? _rootSignature12;
    // Step 2c — deferred-release queue for D3D12 resources evicted from LRU caches. D3D12
    // doesn't auto-track GPU vs CPU lifetime; disposing a vertex/texture while the GPU is
    // still reading it crashes the process. Resources go here instead of being disposed
    // immediately, and are released N frames later.
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDeletionQueue12? _deletionQueue12;
    private bool _suppressWorldspaceSelectionEvent;
    private Dictionary<(int gx, int gy), CellRecord>? _cellGridLookup;
    private WorldSpatialIndex? _spatialIndex;
    private double _lastControllerUpdateMilliseconds;
    private bool _stressBookmarkApplied;

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
        _stressBookmarkApplied = false;

        // Tear down any prior pipelines (a second LoadData = switching ESMs) so the texture
        // caches don't leak across data sets. Drain GPU first so disposal can't race with
        // an in-flight frame. ReferenceRenderer borrows the mesh+texture caches so it's
        // disposed last; TerrainRenderer borrows the resolver so it goes first.
        _commandRecorder12?.WaitForGpuIdle();
        DisposeReferencePipeline();
        _terrain?.Dispose(); _terrain = null;
        _textureResolver12?.Dispose(); _textureResolver12 = null;

        if (_gpu12 is not null)
        {
            try
            {
                var bsas = DiscoverTextureBsaPaths(_data);
                if (bsas.Length == 0)
                {
                    Log.Warn(
                        "WorldView3DControl: no *Textures*.bsa from '{0}' or {1} Load Order paths — terrain will render white-tinted.",
                        Path.GetDirectoryName(_data.SourceFilePath ?? "") ?? "(unknown)",
                        _data.AdditionalDataPaths.Count);
                }
                else
                {
                    Log.Info("WorldView3DControl: discovered {0} texture BSA(s) for terrain.", bsas.Length);
                }

                _textureResolver12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.TerrainTextureResolver12(
                    _gpu12, _commandRecorder12!, _cbvSrvUavHeap12!, _deletionQueue12!,
                    _data.LandTexturesByFormId, _data.TextureSetsByFormId, bsas);
                var terrain12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.TerrainRenderer12(
                    _gpu12, _commandRecorder12!, _ringBuffer12!, _rootSignature12!,
                    _cbvSrvUavHeap12!, _deletionQueue12!,
                    _textureResolver12);
                terrain12.SetVclrOnlyMode(_vclrOnlyMode);
                terrain12.DetailedProfilingEnabled = _profileLogging;
                _terrain = terrain12;
            }
            catch (Exception ex)
            {
                Log.Warn("WorldView3DControl: terrain pipeline init failed: {0}", ex.Message);
                _terrain = null;
                _textureResolver12?.Dispose(); _textureResolver12 = null;
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
            WorldspaceComboBox.SelectedIndex = SelectInitialWorldspaceIndex(data);
        }
        else
        {
            // No worldspaces and no unlinked exteriors — just ensure the renderers are empty.
            TryBuildCellGrid();
            ResetCameraToDataCentroid();
            ApplyStressSceneBookmarkIfRequested();
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
        if (!TryInitD3D12Backend())
        {
            ShowStatus("3D view unavailable: D3D12 init failed (see logs).");
            return;
        }
        Log.Info("WorldView3DControl: D3D12 backend active.");

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
        UpdateHudLayoutBounds();
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
        => WorldDataBsaPathResolver.DiscoverTextureBsaPaths(data);

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
        if (_data is null) return;

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
            _referenceGpuTextureResolver12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.NifGpuTextureResolver(textureBsas);

            if (_gpu12 is null ||
                _commandRecorder12 is null ||
                _ringBuffer12 is null ||
                _rootSignature12 is null ||
                _cbvSrvUavHeap12 is null ||
                _deletionQueue12 is null)
            {
                return;
            }

            _referenceTextureCache12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTextureCache12(
                _gpu12, _commandRecorder12, _cbvSrvUavHeap12!, _referenceGpuTextureResolver12, _deletionQueue12);
            _referenceMeshCache12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceMeshCache12(
                _gpu12, _commandRecorder12, _meshArchives, _referenceTextureResolver, _referenceTextureCache12,
                _deletionQueue12, capacity: 2048);
            _references = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12,
                _cbvSrvUavHeap12, _referenceMeshCache12)
            {
                DetailedProfilingEnabled = _profileLogging,
            };
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
        if (_referenceMeshCache12 is not null || _referenceTextureCache12 is not null)
        {
            _commandRecorder12?.WaitForGpuIdle();
        }

        _references?.Dispose(); _references = null;
        _referenceMeshCache12?.Dispose(); _referenceMeshCache12 = null;
        _referenceTextureCache12?.Dispose(); _referenceTextureCache12 = null;
        _referenceGpuTextureResolver12?.Dispose(); _referenceGpuTextureResolver12 = null;
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
        _ = _pointerStartPosition;
    }

    private void OnRenderPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateHudLayoutBounds();
        TryEnsureSurface();
    }

    private void OnRenderPanelCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        UpdateHudLayoutBounds();
        TryEnsureSurface();
    }

    private void UpdateHudLayoutBounds()
    {
        var width = RenderPanel.ActualWidth;
        if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width)) return;

        var maxPanelWidth = Math.Max(0d, width - 16d);
        HudPanel.MaxWidth = maxPanelWidth;
        HudText.MaxWidth = Math.Max(0d, maxPanelWidth - 16d);
    }

    private void TryEnsureSurface()
    {
        if (_gpu12 is null) return;

        var widthLayout = RenderPanel.ActualWidth;
        var heightLayout = RenderPanel.ActualHeight;
        if (widthLayout <= 0 || heightLayout <= 0) return;
        UpdateHudLayoutBounds();

        var width = (uint)Math.Max(1, Math.Round(widthLayout * RenderPanel.CompositionScaleX));
        var height = (uint)Math.Max(1, Math.Round(heightLayout * RenderPanel.CompositionScaleY));

        if (_surface12 is null)
        {
            _surface12 = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuSwapChainSurface12.Create(
                _gpu12!, RenderPanel, width, height);
            if (_surface12 is null)
            {
                ShowStatus("3D view: D3D12 swapchain bind failed (see logs).");
                return;
            }
            HideStatus();
            _lastFrameTime = DateTime.UtcNow;
            AttachRenderLoop();
        }
        else
        {
            // Resize requires GPU idle so the back buffers aren't referenced by in-flight
            // command lists. The recorder's WaitForGpuIdle drains the queue.
            _commandRecorder12!.WaitForGpuIdle();
            _surface12.Resize(width, height);
            HideStatus();
        }
    }

    private void DisposeRenderResources()
    {
        _commandRecorder12?.WaitForGpuIdle();

        // Reference pipeline disposes first — it borrows the texture caches / mesh archives
        // but owns its own copy, so this is independent of the terrain stack.
        DisposeReferencePipeline();
        _water?.Dispose();
        _water = null;
        _terrain?.Dispose();
        _terrain = null;
        _textureResolver12?.Dispose();
        _textureResolver12 = null;
        _cellGrid?.Dispose();
        _cellGrid = null;
        DisposeD3D12Backend();
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
        if (Visibility == Visibility.Collapsed) return;
        if (_surface12 is null || _gpu12 is null || _commandRecorder12 is null) return;

        var now = DateTime.UtcNow;
        var deltaSeconds = (float)(now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;
        // Clamp pathological deltas (long pause, debugger break) to keep camera step bounded.
        if (deltaSeconds > 0.1f) deltaSeconds = 0.1f;

        var controllerStarted = StartProfileTimestamp();
        _controller.Update(deltaSeconds);
        _lastControllerUpdateMilliseconds = ElapsedMilliseconds(controllerStarted);

        try
        {
            RenderFrameD3D12();
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: render frame failed, detaching loop:\n{0}", ex);
            // Drain any pending D3D12 validation messages BEFORE detaching — the next-frame
            // pump won't get a chance. No-op unless FALLOUT_VIEWER_D3D12_DEBUG=1.
            try { _gpu12?.PumpDebugMessages(); }
            catch (Exception pumpEx) { Log.Warn("PumpDebugMessages on render-frame failure threw: {0}", pumpEx.Message); }
            DetachRenderLoop();
        }
    }

    /// <summary>
    ///     D3D12 live-render path. The shared backend binds the swap chain, root signature,
    ///     descriptor heaps, and per-frame allocators once, then each renderer records its
    ///     own terrain/reference/wireframe draws into the same command list.
    /// </summary>
    private void RenderFrameD3D12()
    {
        var frameStarted = StartProfileTimestamp();
        var recorder = _commandRecorder12!;
        var surface = _surface12!;

        var segmentStarted = StartProfileTimestamp();
        recorder.BeginFrame();
        // Deletion queue advances frame counter — releases resources whose hold has elapsed.
        // Must run AFTER BeginFrame's fence wait so the prior GPU work is known complete.
        _deletionQueue12!.Tick();
        _ringBuffer12!.ResetFrame();
        _cbvSrvUavHeap12!.BeginFrame(recorder.FrameIndex);
        // Drain D3D12 debug layer messages from the prior frame (no-op unless
        // FALLOUT_VIEWER_D3D12_DEBUG=1 enabled the layer).
        _gpu12!.PumpDebugMessages();
        var beginFrameMs = ElapsedMilliseconds(segmentStarted);

        var cmd = recorder.CommandList;
        segmentStarted = StartProfileTimestamp();
        var (backBuffer, rtvHandle) = surface.AcquireBackBufferRtv();
        var acquireMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        // PRESENT → RENDER_TARGET so we can write to the back buffer.
        cmd.ResourceBarrierTransition(backBuffer,
            Vortice.Direct3D12.ResourceStates.Present,
            Vortice.Direct3D12.ResourceStates.RenderTarget);

        cmd.ClearRenderTargetView(rtvHandle,
            new Vortice.Mathematics.Color4(0x1B / 255f, 0x24 / 255f, 0x36 / 255f, 1f));
        cmd.ClearDepthStencilView(surface.DepthStencilView,
            Vortice.Direct3D12.ClearFlags.Depth, 1f, 0);
        cmd.OMSetRenderTargets(rtvHandle, surface.DepthStencilView);
        cmd.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, surface.Width, surface.Height, 0f, 1f));
        cmd.RSSetScissorRect((int)surface.Width, (int)surface.Height);

        // Bind the shared root signature + shader-visible descriptor heaps once per frame.
        // Each renderer sets only its own PSO + per-slot bindings inside Render().
        cmd.SetGraphicsRootSignature(_rootSignature12!.RootSignature);
        cmd.SetDescriptorHeaps(1, new[] { _cbvSrvUavHeap12.Heap });
        var clearSetupMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        // Camera + cylinder match the D3D11 path so visible-cell sets are identical.
        var aspect = surface.Width / (float)surface.Height;
        _camera.FarPlane = _renderDistance;
        var view = _camera.GetViewMatrix();
        var proj = _camera.GetProjectionMatrix(aspect);
        var viewProj = view * proj;
        var cylinder = new VisibilityCylinder(_camera.Position, _renderDistance);
        var cameraMs = ElapsedMilliseconds(segmentStarted);

        // Layer order matches D3D11: terrain → references → water → wireframe. Water is
        // alpha-blended depth-read, so it must come after terrain + references (which write
        // the depth that water samples). Wireframe last so it stays on top (depth-disabled).
        segmentStarted = StartProfileTimestamp();
        var visibleTerrain = _showTerrain ? _terrain?.Render(viewProj, cylinder) ?? 0 : 0;
        var terrainMs = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartProfileTimestamp();
        var visibleReferences = _showReferences ? _references?.Render(viewProj, cylinder) ?? 0 : 0;
        var referencesMs = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartProfileTimestamp();
        var visibleWater = _showWater ? _water?.Render(viewProj, cylinder) ?? 0 : 0;
        var waterMs = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartProfileTimestamp();
        var visibleWireframe = _showWireframe ? _cellGrid?.Render(viewProj, cylinder) ?? 0 : 0;
        var wireframeMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        var visible = Math.Max(visibleTerrain, visibleWireframe);
        var totalCells = _terrain?.CellCount ?? _cellGrid?.CellCount ?? 0;
        UpdateHud(visible, totalCells, visibleWater, visibleReferences);
        var hudMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        // RENDER_TARGET → PRESENT so DXGI can flip.
        cmd.ResourceBarrierTransition(backBuffer,
            Vortice.Direct3D12.ResourceStates.RenderTarget,
            Vortice.Direct3D12.ResourceStates.Present);

        recorder.EndFrame();
        var endFrameMs = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartProfileTimestamp();
        surface.Present();
        var presentMs = ElapsedMilliseconds(segmentStarted);

        if (_profileLogging)
        {
            MaybeLogProfile(new FrameProfileSample(
                TotalMilliseconds: ElapsedMilliseconds(frameStarted),
                ControllerMilliseconds: _lastControllerUpdateMilliseconds,
                BeginFrameMilliseconds: beginFrameMs,
                AcquireMilliseconds: acquireMs,
                ClearSetupMilliseconds: clearSetupMs,
                CameraMilliseconds: cameraMs,
                TerrainMilliseconds: terrainMs,
                ReferencesMilliseconds: referencesMs,
                WaterMilliseconds: waterMs,
                WireframeMilliseconds: wireframeMs,
                EndFrameMilliseconds: endFrameMs,
                PresentMilliseconds: presentMs,
                HudMilliseconds: hudMs,
                VisibleTerrain: visibleTerrain,
                VisibleReferences: visibleReferences,
                VisibleWater: visibleWater,
                VisibleWireframe: visibleWireframe,
                TotalCells: totalCells,
                RenderDistanceCells: _renderDistance / WorldGridConstants.CellSize,
                ViewportWidth: surface.Width,
                ViewportHeight: surface.Height,
                DescriptorCount: _cbvSrvUavHeap12.CurrentFramePeak,
                RingBytes: _ringBuffer12.CurrentFrameBytes));
        }
    }

    /// <summary>
    ///     Initialises the D3D12 backend stack: device + direct queue + per-frame command
    ///     recorder + upload-heap ring + shader-visible descriptor heaps + shared root
    ///     signature. Called from <see cref="OnLoaded" /> when <c>FALLOUT_VIEWER_D3D12=1</c>.
    /// </summary>
    private bool TryInitD3D12Backend()
    {
        try
        {
            // FALLOUT_VIEWER_D3D12_DEBUG=1 enables the D3D12 debug + validation layer. Catches
            // resource-state mismatches, descriptor mismatches, lifetime-after-free, etc. that
            // the runtime would otherwise silently corrupt or hard-crash on. ~30% CPU overhead
            // per draw on validation alone — leave off in normal use.
            var debugLayer = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_D3D12_DEBUG") == "1";
            _gpu12 = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDevice12.Create(debugLayer);
            if (_gpu12 is null) return false;

            _commandRecorder12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12(_gpu12);
            // 16 MB per frame slot — generous, easy to walk back if needed. Sized so the worst
            // realistic WastelandNV frame (~1500 draws × 80 B per-draw CB + padding) fits with
            // 100× headroom for future per-frame instance buffers (Step 4 instancing).
            _ringBuffer12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRingBuffer12(
                _gpu12,
                FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12.FramesInFlight,
                bytesPerFrame: 16 * 1024 * 1024);
            // CBV/SRV/UAV heap layout (4a — bindless): persistent region 0..16383 holds
            // every texture's bindless SRV (stable index over a worldspace session); the
            // remaining 114688 slots ring across N frames for per-frame transient SRVs
            // (the reference renderer's per-frame instance structured-buffer SRV, water's
            // per-frame instance-SRV copy, etc.). 16K persistent comfortably accommodates
            // FNV's ~5K unique texture set with 3× headroom for DLC + cross-worldspace
            // browsing. Per-frame ring is sized for the legacy per-batch SRV pattern; once
            // 4a's bindless lookups land for reference + terrain, per-frame usage shrinks
            // by an order of magnitude.
            _cbvSrvUavHeap12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12(
                _gpu12,
                Vortice.Direct3D12.DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                capacity: 131072,
                framesInFlight: FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12.FramesInFlight,
                persistentCapacity: 16384);
            _rootSignature12 = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12.Create(_gpu12);
            _deletionQueue12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDeletionQueue12(
                framesToHold: FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12.FramesInFlight);

            // Step 2b — instantiate the simplest D3D12 renderer end-to-end. This proves
            // PSO creation + ring-buffer-backed CB/VB + root signature + command list draws
            // all hang together before the heavier renderers (terrain/reference/water) port.
            _cellGrid = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.CellGridDebugRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12)
            {
                DetailedProfilingEnabled = _profileLogging,
            };

            // Step 2e — WaterRenderer12 doesn't depend on per-ESM data (water height is
            // discovered at LoadData), so we can instantiate up front alongside CellGrid.
            _water = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.WaterRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _cbvSrvUavHeap12, _deletionQueue12)
            {
                DetailedProfilingEnabled = _profileLogging,
            };

            // Step 2c — TerrainRenderer12 is instantiated lazily in LoadData (needs the per-ESM
            // LTEX/TXST dictionaries + BSA paths), mirroring the D3D11 deferred construction.

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: D3D12 backend init failed: {0}", ex.Message);
            DisposeD3D12Backend();
            return false;
        }
    }

    /// <summary>Tears down the D3D12 backend in reverse construction order, draining the
    /// GPU first so resources aren't referenced by in-flight command lists.</summary>
    private void DisposeD3D12Backend()
    {
        _commandRecorder12?.WaitForGpuIdle();
        // Drain pending deletions now that the GPU is idle — anything queued is safe to release.
        _deletionQueue12?.Dispose(); _deletionQueue12 = null;
        _rootSignature12?.Dispose(); _rootSignature12 = null;
        _cbvSrvUavHeap12?.Dispose(); _cbvSrvUavHeap12 = null;
        _ringBuffer12?.Dispose(); _ringBuffer12 = null;
        _surface12?.Dispose(); _surface12 = null;
        _commandRecorder12?.Dispose(); _commandRecorder12 = null;
        _gpu12?.Dispose(); _gpu12 = null;
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

    private int SelectInitialWorldspaceIndex(WorldViewData data)
    {
        if (!IsWastelandNvHeavyStressScene())
        {
            return 0;
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
            DefaultRenderDistance);
        _stressBookmarkApplied = true;
        if (bookmark is not { } heavy)
        {
            Log.Warn("WorldView3DControl: WastelandNV Heavy stress bookmark requested, but no renderable reference cluster was found.");
            return;
        }

        SetRenderDistance(DefaultRenderDistance);
        var gameY = -heavy.CanvasCenter.Y;
        _camera.Position = new Vector3(
            heavy.CanvasCenter.X,
            gameY - WorldGridConstants.CellSize * 2f,
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
        string.Equals(_stressScene, "WastelandNVHeavy", StringComparison.OrdinalIgnoreCase);

    private static bool WorldspaceLooksLikeWastelandNv(WorldspaceRecord worldspace) =>
        ContainsWorldspaceToken(worldspace.EditorId, "WastelandNV") ||
        ContainsWorldspaceToken(worldspace.FullName, "WastelandNV") ||
        ContainsWorldspaceToken(worldspace.FullName, "Mojave");

    private static bool ContainsWorldspaceToken(string? value, string token) =>
        value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    private void WorldspaceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorldspaceSelectionEvent || _data is null) return;
        if (WorldspaceComboBox.SelectedIndex < 0) return;

        TryBuildCellGrid();
        ResetCameraToDataCentroid();
        ApplyStressSceneBookmarkIfRequested();
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
        _references?.LoadData(_data.RenderCache, _cellGridLookup, _spatialIndex);
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
        // Backend chip — D3D12 shows the feature level for at-a-glance diagnostics.
        var backend = _gpu12 is not null
            ? $"D3D12 {_gpu12.FeatureLevel}"
            : "D3D11";
        var text =
            $"[{backend}]   " +
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
                $"\nstats cand:{stats.VisibleCandidates} draw:{stats.TerrainDraws} " +
                $"up:{stats.NewUploads} texMiss:{stats.TextureCacheMisses} " +
                $"water:{visibleWater} cpu:{stats.CpuFrameMilliseconds:0.0}ms";

            if (_references is not null && _showReferences)
            {
                var rstats = _references.LastStats;
                text +=
                    $"\nrefs cand:{rstats.ReferenceCandidates} drawn:{rstats.ReferenceDrawn} " +
                    $"sub:{rstats.ReferenceSubmeshDraws} batch:{rstats.ReferenceBatches} inst:{rstats.ReferenceInstances} " +
                    $"instDraw:{rstats.ReferenceInstancedDraws} blendDraw:{rstats.ReferenceBlendedDraws} " +
                    $"srvBinds:{rstats.ReferenceSrvBinds} meshMiss:{rstats.ReferenceMeshCacheMisses} " +
                    $"qDec:{rstats.ReferenceQueuedDecodes} actDec:{rstats.ReferenceActiveDecodes} " +
                    $"cpuHit:{rstats.ReferenceCpuDecodedMeshCacheHits} bcTex:{rstats.ReferenceCompressedTextureUploads} rgbaTex:{rstats.ReferenceRgbaTextureUploads} " +
                    $"cull:{rstats.ReferenceCullMilliseconds:0.0} mesh:{rstats.ReferenceMeshUploadMilliseconds:0.0} " +
                    $"cb:{rstats.ReferenceCbUpdateMilliseconds:0.0} srv:{rstats.ReferenceSrvBindMilliseconds:0.0} " +
                    $"draw:{rstats.ReferenceDrawCallMilliseconds:0.0}ms";
            }
        }

        UpdateHudLayoutBounds();
        HudText.Text = text;
        if (StatusOverlay.Visibility != Visibility.Visible)
        {
            HudPanel.Visibility = Visibility.Visible;
        }
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
        var references = _showReferences ? _references?.LastStats.Snapshot() : null;
        _profileAccumulator.Add(sample, terrain, water, wireframe, references);
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

    private long StartProfileTimestamp() => _profileLogging ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private readonly record struct FrameProfileSample(
        double TotalMilliseconds,
        double ControllerMilliseconds,
        double BeginFrameMilliseconds,
        double AcquireMilliseconds,
        double ClearSetupMilliseconds,
        double CameraMilliseconds,
        double TerrainMilliseconds,
        double ReferencesMilliseconds,
        double WaterMilliseconds,
        double WireframeMilliseconds,
        double EndFrameMilliseconds,
        double PresentMilliseconds,
        double HudMilliseconds,
        int VisibleTerrain,
        int VisibleReferences,
        int VisibleWater,
        int VisibleWireframe,
        int TotalCells,
        float RenderDistanceCells,
        uint ViewportWidth,
        uint ViewportHeight,
        uint DescriptorCount,
        uint RingBytes);

    private sealed class FrameProfileAccumulator
    {
        private long _intervalStarted = Stopwatch.GetTimestamp();
        private int _frames;
        private double _frameTotal;
        private double _frameMax;
        private double _controller;
        private double _beginFrame;
        private double _acquire;
        private double _clearSetup;
        private double _camera;
        private double _terrainFrame;
        private double _referencesFrame;
        private double _waterFrame;
        private double _wireframeFrame;
        private double _endFrame;
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
        private double _visibleReferences;
        private double _visibleWater;
        private double _visibleWireframe;
        private double _descriptors;
        private double _ringBytes;
        private double _terrainCandidates;
        private double _terrainDraws;
        private double _terrainQuadrantDraws;
        private double _terrainUploads;
        private double _terrainPreUploads;
        private double _textureMisses;
        private double _textureCompressedUploads;
        private double _textureRgbaUploads;
        private double _refCellsVisited;
        private double _refCandidates;
        private double _refCulled;
        private double _refMeshMissing;
        private double _refDrawn;
        private double _refSubmeshDraws;
        private double _refSrvBinds;
        private double _refCullFlips;
        private double _refMeshMisses;
        private double _refDecodeRequests;
        private double _refQueuedDecodes;
        private double _refDecodeStarts;
        private double _refActiveDecodes;
        private double _refCpuDecodedHits;
        private double _refCpuDecodedMisses;
        private double _refCpuDecodedNegativeHits;
        private double _refCompressedTextureUploads;
        private double _refRgbaTextureUploads;
        private double _refBatches;
        private double _refInstances;
        private double _refInstancedDraws;
        private double _refBlendedDraws;
        private double _refState;
        private double _refCull;
        private double _refMeshUpload;
        private double _refCb;
        private double _refSrvBind;
        private double _refDrawCall;
        private int _lastTotalCells;
        private float _lastRenderDistanceCells;
        private uint _lastViewportWidth;
        private uint _lastViewportHeight;

        internal void Add(
            FrameProfileSample sample,
            WorldRenderStats? terrain,
            WorldRenderStats? water,
            WorldRenderStats? wireframe,
            WorldRenderStats? references)
        {
            _frames++;
            _frameTotal += sample.TotalMilliseconds;
            _frameMax = Math.Max(_frameMax, sample.TotalMilliseconds);
            _controller += sample.ControllerMilliseconds;
            _beginFrame += sample.BeginFrameMilliseconds;
            _acquire += sample.AcquireMilliseconds;
            _clearSetup += sample.ClearSetupMilliseconds;
            _camera += sample.CameraMilliseconds;
            _terrainFrame += sample.TerrainMilliseconds;
            _referencesFrame += sample.ReferencesMilliseconds;
            _waterFrame += sample.WaterMilliseconds;
            _wireframeFrame += sample.WireframeMilliseconds;
            _endFrame += sample.EndFrameMilliseconds;
            _present += sample.PresentMilliseconds;
            _hud += sample.HudMilliseconds;
            _visibleTerrain += sample.VisibleTerrain;
            _visibleReferences += sample.VisibleReferences;
            _visibleWater += sample.VisibleWater;
            _visibleWireframe += sample.VisibleWireframe;
            _descriptors += sample.DescriptorCount;
            _ringBytes += sample.RingBytes;
            _lastTotalCells = sample.TotalCells;
            _lastRenderDistanceCells = sample.RenderDistanceCells;
            _lastViewportWidth = sample.ViewportWidth;
            _lastViewportHeight = sample.ViewportHeight;

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
                _textureCompressedUploads += terrain.TextureCompressedUploads;
                _textureRgbaUploads += terrain.TextureRgbaFallbackUploads;
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

            if (references is not null)
            {
                _refCellsVisited += references.ReferenceCellsVisited;
                _refCandidates += references.ReferenceCandidates;
                _refCulled += references.ReferenceCulled;
                _refMeshMissing += references.ReferenceMeshMissing;
                _refDrawn += references.ReferenceDrawn;
                _refSubmeshDraws += references.ReferenceSubmeshDraws;
                _refSrvBinds += references.ReferenceSrvBinds;
                _refCullFlips += references.ReferenceCullModeFlips;
                _refMeshMisses += references.ReferenceMeshCacheMisses;
                _refDecodeRequests += references.ReferenceDecodeRequests;
                _refQueuedDecodes += references.ReferenceQueuedDecodes;
                _refDecodeStarts += references.ReferenceDecodeStarts;
                _refActiveDecodes += references.ReferenceActiveDecodes;
                _refCpuDecodedHits += references.ReferenceCpuDecodedMeshCacheHits;
                _refCpuDecodedMisses += references.ReferenceCpuDecodedMeshCacheMisses;
                _refCpuDecodedNegativeHits += references.ReferenceCpuDecodedMeshNegativeHits;
                _refCompressedTextureUploads += references.ReferenceCompressedTextureUploads;
                _refRgbaTextureUploads += references.ReferenceRgbaTextureUploads;
                _refBatches += references.ReferenceBatches;
                _refInstances += references.ReferenceInstances;
                _refInstancedDraws += references.ReferenceInstancedDraws;
                _refBlendedDraws += references.ReferenceBlendedDraws;
                _refState += references.ReferenceStateSetupMilliseconds;
                _refCull += references.ReferenceCullMilliseconds;
                _refMeshUpload += references.ReferenceMeshUploadMilliseconds;
                _refCb += references.ReferenceCbUpdateMilliseconds;
                _refSrvBind += references.ReferenceSrvBindMilliseconds;
                _refDrawCall += references.ReferenceDrawCallMilliseconds;
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
                string.Create(CultureInfo.InvariantCulture, $"3D profile {_frames}f/{elapsed / 1000.0:0.0}s {_lastViewportWidth}x{_lastViewportHeight} cells={_lastTotalCells} dist={_lastRenderDistanceCells:0.#}c ") +
                string.Create(CultureInfo.InvariantCulture, $"frame avg/max={Avg(_frameTotal):0.00}/{_frameMax:0.00}ms ") +
                string.Create(CultureInfo.InvariantCulture, $"stages ctrl={Avg(_controller):0.00} begin={Avg(_beginFrame):0.00} acquire={Avg(_acquire):0.00} clear={Avg(_clearSetup):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"camera={Avg(_camera):0.00} terrain={Avg(_terrainFrame):0.00} refs={Avg(_referencesFrame):0.00} water={Avg(_waterFrame):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"wire={Avg(_wireframeFrame):0.00} end={Avg(_endFrame):0.00} present={Avg(_present):0.00} hud={Avg(_hud):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"desc={Avg(_descriptors):0.0} ringKB={Avg(_ringBytes) / 1024.0:0.0} | ") +
                string.Create(CultureInfo.InvariantCulture, $"visible terrain={Avg(_visibleTerrain):0.0} refs={Avg(_visibleReferences):0.0} water={Avg(_visibleWater):0.0} wire={Avg(_visibleWireframe):0.0} | ") +
                string.Create(CultureInfo.InvariantCulture, $"terrain cpu={Avg(_terrainCpu):0.00} state={Avg(_terrainState):0.00} gather={Avg(_terrainGather):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"sort={Avg(_terrainSort):0.00} loop={Avg(_terrainDrawLoop):0.00} quadrants={Avg(_terrainQuadrants):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"meshUpload={Avg(_terrainMeshUpload):0.00} preUpload={Avg(_terrainPreUpload):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"cand={Avg(_terrainCandidates):0.0} visible={Avg(_visibleTerrain):0.0} cells={Avg(_terrainDraws):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"qdraw={Avg(_terrainQuadrantDraws):0.0} uploads={Avg(_terrainUploads):0.0}+{Avg(_terrainPreUploads):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"texMiss={Avg(_textureMisses):0.0} texBC={Avg(_textureCompressedUploads):0.0} texRGBA={Avg(_textureRgbaUploads):0.0} | ") +
                string.Create(CultureInfo.InvariantCulture, $"water cpu={Avg(_waterCpu):0.00} state={Avg(_waterState):0.00} gather={Avg(_waterGather):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"build={Avg(_waterInstanceBuild):0.00} upload={Avg(_waterUpload):0.00} draw={Avg(_waterDrawCall):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"cells={Avg(_visibleWater):0.0} | ") +
                string.Create(CultureInfo.InvariantCulture, $"wire cpu={Avg(_wireCpu):0.00} gather={Avg(_wireGather):0.00} vertices={Avg(_wireVertexBuild):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"upload={Avg(_wireUpload):0.00} draw={Avg(_wireDrawCall):0.00} cells={Avg(_visibleWireframe):0.0} | ") +
                string.Create(CultureInfo.InvariantCulture, $"refs state={Avg(_refState):0.00} cull={Avg(_refCull):0.00} meshUp={Avg(_refMeshUpload):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"cb={Avg(_refCb):0.00} srvBind={Avg(_refSrvBind):0.00} draw={Avg(_refDrawCall):0.00} ") +
                string.Create(CultureInfo.InvariantCulture, $"cells={Avg(_refCellsVisited):0.0} cand={Avg(_refCandidates):0.0} culled={Avg(_refCulled):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"miss={Avg(_refMeshMissing):0.0} drawn={Avg(_refDrawn):0.0} submesh={Avg(_refSubmeshDraws):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"srvBinds={Avg(_refSrvBinds):0.0} cullFlips={Avg(_refCullFlips):0.0} meshMisses={Avg(_refMeshMisses):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"decodeReq={Avg(_refDecodeRequests):0.0} qDec={Avg(_refQueuedDecodes):0.0} startDec={Avg(_refDecodeStarts):0.0} activeDec={Avg(_refActiveDecodes):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"cpuMeshHit={Avg(_refCpuDecodedHits):0.0} cpuMeshMiss={Avg(_refCpuDecodedMisses):0.0} cpuMeshNeg={Avg(_refCpuDecodedNegativeHits):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"refTexBC={Avg(_refCompressedTextureUploads):0.0} refTexRGBA={Avg(_refRgbaTextureUploads):0.0} batches={Avg(_refBatches):0.0} inst={Avg(_refInstances):0.0} ") +
                string.Create(CultureInfo.InvariantCulture, $"instDraw={Avg(_refInstancedDraws):0.0} blendDraw={Avg(_refBlendedDraws):0.0}");

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
            _beginFrame = 0;
            _acquire = 0;
            _clearSetup = 0;
            _camera = 0;
            _terrainFrame = 0;
            _referencesFrame = 0;
            _waterFrame = 0;
            _wireframeFrame = 0;
            _endFrame = 0;
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
            _visibleReferences = 0;
            _visibleWater = 0;
            _visibleWireframe = 0;
            _descriptors = 0;
            _ringBytes = 0;
            _terrainCandidates = 0;
            _terrainDraws = 0;
            _terrainQuadrantDraws = 0;
            _terrainUploads = 0;
            _terrainPreUploads = 0;
            _textureMisses = 0;
            _textureCompressedUploads = 0;
            _textureRgbaUploads = 0;
            _refCellsVisited = 0;
            _refCandidates = 0;
            _refCulled = 0;
            _refMeshMissing = 0;
            _refDrawn = 0;
            _refSubmeshDraws = 0;
            _refSrvBinds = 0;
            _refCullFlips = 0;
            _refMeshMisses = 0;
            _refDecodeRequests = 0;
            _refQueuedDecodes = 0;
            _refDecodeStarts = 0;
            _refActiveDecodes = 0;
            _refCpuDecodedHits = 0;
            _refCpuDecodedMisses = 0;
            _refCpuDecodedNegativeHits = 0;
            _refCompressedTextureUploads = 0;
            _refRgbaTextureUploads = 0;
            _refBatches = 0;
            _refInstances = 0;
            _refInstancedDraws = 0;
            _refBlendedDraws = 0;
            _refState = 0;
            _refCull = 0;
            _refMeshUpload = 0;
            _refCb = 0;
            _refSrvBind = 0;
            _refDrawCall = 0;
        }
    }
}
