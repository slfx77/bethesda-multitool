using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using SharpGen.Runtime;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
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
public sealed partial class WorldView3DControl : UserControl, IDisposable, ITopDownSceneRenderer
{
    private static readonly Logger Log = Logger.Instance;

    private const float DefaultRenderDistance = WorldGridConstants.CellSize * 16f;
    private const float MinRenderDistance = WorldGridConstants.CellSize * 4f;
    private const float MaxRenderDistance = 800_000f;
    private const float RenderDistanceStep = 1.25f;
    private const double HudUpdateIntervalMilliseconds = 250d;

    private readonly CameraState _camera = new();
    private readonly FlythroughCameraController _controller;
    private readonly Vector2[] _pointerStartPosition = new Vector2[1];
    private readonly HashSet<VirtualKey> _toggleKeysDown = [];
    private readonly bool _showFrameStats =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.FrameStats);
    private readonly bool _profileLogging =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ProfileLog);
    private readonly int _profileLogIntervalMilliseconds =
        ParsePositiveInt(EnvironmentVariables.Get(EnvironmentVariables.Viewer.ProfileIntervalMilliseconds), 2000);
    private readonly double _stallThresholdMilliseconds =
        ParseNonNegativeDouble(EnvironmentVariables.Get(EnvironmentVariables.Viewer.StallThresholdMilliseconds), 0);
    private readonly bool _forceGpuTimestamps =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.GpuTimestamps);
    private readonly string? _stressScene =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.StressScene);
    private readonly FrameProfileAccumulator _profileAccumulator = new();
    // v3 Pass 4 Step 3 cleanup — D3D11 stack deleted. The post-cutover backend is D3D12-only;
    // the `_useD3D12` flag, `FALLOUT_VIEWER_D3D11` rollback, and dual-stack field set are all
    // gone. With a single backend the renderer fields are the concrete D3D12 types directly; the
    // I*Renderer interfaces remain (the renderers implement them) but add no indirection here.
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.CellGridDebugRenderer12? _cellGrid;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.SelectionHighlightRenderer12? _selectionHighlight;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.TerrainRenderer12? _terrain;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.TerrainTextureResolver12? _textureResolver12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.WaterRenderer12? _water;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.SkyboxRenderer12? _sky;
    // Textured sky billboards (sun disc + glare, moon). Textures resolved per-climate via the terrain
    // texture cache; uint.MaxValue = SkyBillboardRenderer12.NoTexture (object skipped).
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.SkyBillboardRenderer12? _skyBillboards;
    private uint _sunDiscTexIndex = uint.MaxValue;
    private uint _sunGlareTexIndex = uint.MaxValue;
    private uint _moonTexIndex = uint.MaxValue;
    private uint _cloudTexIndex = uint.MaxValue;
    private uint _starTexIndex = uint.MaxValue;
    private uint? _skyTexClimateKey; // climate FormId the sky textures were resolved for (null = unresolved)
    private const string DefaultSunTexturePath = @"textures\sky\sun.dds";
    private const string DefaultSunGlareTexturePath = @"textures\sky\sunglare.dds";
    private const string DefaultMoonTexturePath = @"textures\sky\masser_full.dds";
    private const string DefaultCloudTexturePath = @"textures\sky\nvskyclouds.dds";
    private const string DefaultStarTexturePath = @"textures\sky\skystars.dds";
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.NavMeshRenderer12? _navMesh;
    // v3 Phase 3 placed-object pipeline. Parallel to the terrain pipeline; owns separate
    // CPU NIF texture metadata and D3D12 GPU texture payload resolvers.
    private NpcMeshArchiveSet? _meshArchives;
    private NifTextureResolver? _referenceTextureResolver;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.NifGpuTextureResolver? _referenceGpuTextureResolver12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTextureCache12? _referenceTextureCache12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceMeshCache12? _referenceMeshCache12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceRenderer12? _references;
    private WorldViewData? _data;
    private DateTime _lastFrameTime;
    // Atmosphere: current game hour (0..24) feeding the shared b3 atmosphere CB. Defaults to noon; the
    // P4 time-of-day slider will drive it. The lighting/sky/water shaders read the resolved atmosphere
    // (wired in P3+); until then the CB is uploaded but unread (an invisible no-op).
    private float _gameHour = 12f;
    private bool _mouseDragActive;
    private Vector2 _previousPointerPosition;
    // Click-vs-drag tracking for object picking: a press that releases without moving past the
    // threshold is a click (ray-pick), anything more is a camera-look drag.
    private Vector2 _pointerPressPosition;
    private bool _pointerDragMoved;
    private const float ClickMoveThresholdPixels = 4f;
    private readonly List<global::FalloutXbox360Utils.WorldSpatialCell> _pickCellScratch = new();
    // 3D-5/3D-9 selection state. _selectedReference is the current selection (null = none); the pick
    // collects all ray hits into _pickHitScratch (sorted by distance) so a repeat click on the same
    // stack cycles to the next object behind the current one (GECK click-through).
    private PlacedReference? _selectedReference;
    private readonly List<PickHit> _pickHitScratch = new();
    // Broadphase-sphere hits whose tight OBND test missed — used only when no OBND hit lies under the
    // ray, so meshes that overspill a too-tight base-record OBND (SpeedTree canopies) stay clickable.
    private readonly List<PickHit> _pickSphereFallbackScratch = new();
    private bool _renderLoopAttached;
    // v3 Pass 4 Step 3 — D3D12-only backend. The renderer interfaces (`I*Renderer`) plug
    // straight into the D3D12 concrete impls; no D3D11 fallback fields remain.
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDevice12? _gpu12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuSwapChainSurface12? _surface12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12? _commandRecorder12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRingBuffer12? _ringBuffer12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12? _cbvSrvUavHeap12;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12? _rootSignature12;
    private GpuTimestampProfiler12? _gpuTimestampProfiler12;
    // Step 2c — deferred-release queue for D3D12 resources evicted from LRU caches. D3D12
    // doesn't auto-track GPU vs CPU lifetime; disposing a vertex/texture while the GPU is
    // still reading it crashes the process. Resources go here instead of being disposed
    // immediately, and are released N frames later.
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDeletionQueue12? _deletionQueue12;
    // Scene-depth SRV feeding the water depth-fade. The depth resource is R32_TYPELESS; we create an
    // R32_FLOAT SRV over it in the shared bindless heap so water.frag samples the real water-column
    // depth (FNV WATER000's DepthMap path) instead of a view-angle proxy. Allocated once and the SRV
    // recreated in place on resize (the depth resource changes identity). NoDepthSrv => proxy.
    private const uint NoDepthSrv = 0xFFFFFFFF;
    private FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12.PersistentAllocation? _depthSrv;
    private bool _suppressWorldspaceSelectionEvent;
    // Atmosphere weather/time (P4). _selectedWeather null = the placeholder palette (no NAM0 colors);
    // _climateDefaultWeather is the current worldspace climate's default (the "(Climate default)"
    // dropdown item); _currentClimateTiming drives the sun curve + the NAM0 time-band blend.
    private WeatherRecord? _selectedWeather;
    private WeatherRecord? _climateDefaultWeather;
    private AtmosphereState.ClimateTiming _currentClimateTiming = AtmosphereState.ClimateTiming.Default;
    private bool _suppressWeatherSelectionEvent;
    // Interiors are browsed via the shared 2D-viewer cell browser (searchable/filterable grouped
    // list — appropriate for the hundreds of interiors a dropdown can't handle), NOT the worldspace
    // combo. _selectedInterior is the interior currently loaded into the 3D scene (null = exterior
    // mode); _allInteriorItems is the unfiltered browser source the search/filter UI narrows.
    private CellRecord? _selectedInterior;
    // Set by NavigateToCell when a cross-worldspace jump is requested: the async
    // WorldspaceComboBox_SelectionChanged handler frames on this cell instead of the worldspace
    // centroid once the grid rebuild completes (avoids a yield race that would clobber the camera).
    private CellRecord? _pendingNavigateCell;
    private List<WorldMapControl.CellListItem> _allInteriorItems = new();
    private Dictionary<(int gx, int gy), CellRecord>? _cellGridLookup;
    private WorldSpatialIndex? _spatialIndex;
    private double _lastControllerUpdateMilliseconds;
    private bool _stressBookmarkApplied;
    private bool _gpuTimestampsAutoEnabled;
    private long _profileFrameIndex;
    private long _lastHudUpdateTimestamp;
    private string? _lastHudText;
    // User toggled the diagnostics overlay off (HudToggleButton). UpdateHud/HideStatus honor this
    // so a HUD refresh or a status-overlay dismissal doesn't force the panel back on.
    private bool _hudHidden;
    private int _lastGcGen0Collections = GC.CollectionCount(0);
    private int _lastGcGen1Collections = GC.CollectionCount(1);
    private int _lastGcGen2Collections = GC.CollectionCount(2);

    // Layer visibility — toggled by D1/D2/D3 keys. D4 toggles textured-vs-VCLR-only.
    // D5 toggles placed-object (REFR) rendering (v3 Phase 3).
    // The cell-boundary grid is a debug overlay → default OFF (must match CellsCheckBox.IsChecked in XAML).
    // True only during InitializeComponent. The toolbar toggles set IsChecked in XAML, which fires
    // their Checked/Unchecked handlers DURING load (now that they live in flyouts that realize early);
    // the handlers read x:Name fields / touch renderers that aren't ready yet, so they no-op until the
    // constructor clears this. Backing _show* fields already default to the XAML-checked values.
    private bool _initializing = true;
    private bool _showWireframe;
    private bool _showTerrain = true;
    private bool _showWater = true;
    private bool _showTerrainTextures = true;
    private bool _showVertexColors = true;
    private bool _showReferences = true;
    private bool _showNavMesh;
    private bool _showDisabled;
    // Engine/editor markers (XMarker, map/travel/teleport markers). Hidden by default to match the
    // game; the env knob seeds the initial value, the toolbar toggle flips it at runtime (persists
    // across ESM reloads like _showDisabled).
    private bool _showMarkers = FalloutXbox360Utils.Core.EnvironmentVariables.IsEnabled(
        FalloutXbox360Utils.Core.EnvironmentVariables.Viewer.ShowMarkers);
    // Placed-object categories hidden in the 3D view. Activators are off by default (the engine shows
    // them as invisible interaction volumes, not meshes); toggled via the Visibility submenu. Applied
    // to the reference renderer on pipeline init and on every toggle.
    private readonly HashSet<PlacedObjectCategory> _hiddenCategories = [PlacedObjectCategory.Activator];
    // Atmosphere lighting (P3) — directional sun + ambient via the shared b3 CB. On by default; when
    // off, the shaders fall back to the legacy flat shade (pixel-identical to the pre-atmosphere look).
    private bool _showLighting = true;
    // Skybox (P5) — horizon→top gradient + sun disc drawn first, depth off. On by default; off ⇒ the
    // flat dark-blue clear shows. Also gates whether water mirrors the atmosphere sky vs its DNAM color.
    private bool _showSky = true;
    // Distance fog (weather-driven) — terrain/reference/water recede toward the WTHR fog color over the
    // FNAM near→far range. On by default; off ⇒ no fog (so distant cells stay crisp for inspection).
    private bool _showFog = true;
    private float _renderDistance = DefaultRenderDistance;

    public WorldView3DControl()
    {
        InitializeComponent();
        _initializing = false; // XAML load done — toolbar toggle handlers may now run for real.
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
        // Make the base-object category available to the placement bake before the renderer pulls
        // its first cell — drives the per-category visibility filter (activators off by default).
        data.RenderCache.CategoryIndex = data.CategoryIndex;
        _stressBookmarkApplied = false;

        // Tear down any prior pipelines (a second LoadData = switching ESMs) so the texture
        // caches don't leak across data sets. Drain GPU first so disposal can't race with
        // an in-flight frame. ReferenceRenderer borrows the mesh+texture caches so it's
        // disposed last; TerrainRenderer borrows the resolver so it goes first.
        _commandRecorder12?.WaitForGpuIdle();
        DisposeReferencePipeline();
        _terrain?.Dispose(); _terrain = null;
        _textureResolver12?.Dispose(); _textureResolver12 = null;
        // The sky textures (sun/moon/cloud/star) are bindless indices INTO this resolver's heap region;
        // recreating the resolver invalidates them, so force a re-resolve even if the climate key is
        // unchanged, and blank the indices so nothing samples a stale slot until the re-resolve lands.
        _skyTexClimateKey = null;
        _sunDiscTexIndex = _sunGlareTexIndex = _moonTexIndex = _cloudTexIndex = _starTexIndex = uint.MaxValue;

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
                terrain12.SetDebugModes(_showTerrainTextures, _showVertexColors);
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

        // Interiors live in the shared cell browser (Interiors button), not the combo.
        _selectedInterior = null;
        HideInteriorBrowser();
        InteriorsButton.Content = data.InteriorCells.Count > 0
            ? $"Interiors ({data.InteriorCells.Count})"
            : "Interiors";
        InteriorsButton.IsEnabled = data.InteriorCells.Count > 0;

        // Weather dropdown: "(Climate default)" + all weathers (P4). Built once here; the worldspace
        // selection below refreshes the climate default + timing it resolves against.
        PopulateWeatherDropdown();

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

    /// <summary>Sets (or clears, with null) the current 3D selection and its outline programmatically.</summary>
    internal void SelectObject(PlacedReference? obj)
    {
        _selectedReference = obj;
        _pickHitScratch.Clear();
        UpdateHighlightFromSelection();
    }

    /// <summary>
    ///     Navigates the 3D scene to a specific cell — the 3D counterpart of
    ///     <see cref="WorldMapControl.NavigateToCell" />, used when a door-destination / linked-cell
    ///     link is clicked while the 3D view is the active one. Interiors load as a single-cell scene;
    ///     exteriors select the parent worldspace (when it isn't already shown) and frame the camera on
    ///     the cell's grid position.
    /// </summary>
    internal void NavigateToCell(CellRecord cell)
    {
        if (_data is null) return;

        // Interiors have no grid coords — same single-cell load path as a cell-browser pick.
        if (cell.GridX is not int || cell.GridY is not int)
        {
            _pendingNavigateCell = null;
            _selectedInterior = cell;
            _suppressWorldspaceSelectionEvent = true;
            WorldspaceComboBox.SelectedIndex = -1;
            _suppressWorldspaceSelectionEvent = false;
            HideInteriorBrowser();
            TryBuildCellGrid();
            ResetCameraToInteriorBounds(cell);
            RefreshAtmosphereForCurrentWorldspace();
            return;
        }

        // Exterior: switch to the parent worldspace if we're not already showing it (or we're in
        // interior mode). The async SelectionChanged handler rebuilds the grid and would reset the
        // camera to the worldspace centroid, so stash the target for it to re-frame on instead.
        var wsIndex = cell.WorldspaceFormId is uint wsFormId
            ? _data.Worldspaces.FindIndex(ws => ws.FormId == wsFormId)
            : -1;
        if (wsIndex >= 0 && (wsIndex != WorldspaceComboBox.SelectedIndex || _selectedInterior is not null))
        {
            _pendingNavigateCell = cell;
            WorldspaceComboBox.SelectedIndex = wsIndex;
            return;
        }

        // Already showing the right exterior worldspace — just move the camera.
        CenterCameraOnCell(cell);
    }

    /// <summary>Frames the camera on a single exterior cell's grid position, reusing the same
    /// pitched-down posture as <see cref="ResetCameraToDataCentroid" />.</summary>
    private void CenterCameraOnCell(CellRecord cell)
    {
        if (cell.GridX is not int gx || cell.GridY is not int gy) return;
        var worldX = (gx + 0.5f) * WorldGridConstants.CellSize;
        var worldY = (gy + 0.5f) * WorldGridConstants.CellSize;
        _camera.Position = new Vector3(worldX, worldY - 8192f, 32768f);
        _camera.Yaw = 0f;
        _camera.Pitch = -MathF.PI / 6f;
    }

    // Lifecycle -----------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The control instance lives for the whole app session; a SubTabView tab switch unloads +
        // reloads its content. First load creates the device-level backend; on return the device +
        // scene pipelines persist (OnUnloaded only released the swapchain surface), so we just
        // re-hook input + recreate the surface. This is what keeps 3D + the 2D top-down overlay
        // working after switching to another tab and back, without re-streaming the scene.
        var firstInit = _gpu12 is null;
        if (firstInit)
        {
            if (!TryInitD3D12Backend())
            {
                ShowStatus("3D view unavailable: D3D12 init failed (see logs).");
                return;
            }
            Log.Info("WorldView3DControl: D3D12 backend active.");
        }

        // (Re)subscribe input handlers — OnUnloaded unconditionally unsubscribes them, so this never
        // double-subscribes. The XAML markup compiler types RenderPanel as SwapChainPanel.
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
        // interpolate. _cellGridLookup is set in TryBuildCellGrid. On return the grid persists.
        _controller.GroundHeightSampler = SampleGroundHeight;

        if (firstInit)
        {
            TryBuildCellGrid();
        }
        UpdateHudLayoutBounds();
        // _surface12 was released on unload, so on return this recreates the swapchain on the
        // re-attached panel + EnsureDepthSrv + AttachRenderLoop (its create branch).
        TryEnsureSurface();
    }

    // How far above the eye the down-ray starts, so a surface the camera is already resting on still
    // registers against float error. Also the slack on the "surface must be at/below the eye" rule.
    private const float GroundRaycastEpsUp = 8f;

    private float? SampleGroundHeight(float worldX, float worldY)
    {
        if (_cellGridLookup is null) return null;
        var terrain = TerrainHeightSampler.Sample(_cellGridLookup, worldX, worldY, _data?.RenderCache);

        // Real downward triangle raycast so walk mode rides ON the actual surface of placed meshes
        // (floors, walkways, rocks, roofs) instead of their axis-aligned bounding box — rotation is
        // respected, and a roof ABOVE the eye is never grabbed because the ray starts at the eye and
        // casts down. Ground = max(terrain, highest object surface at/below the eye).
        var objectHit = RaycastObjectGround(worldX, worldY, _camera.Position.Z);
        if (terrain is { } t) return objectHit is { } m && m > t ? m : t;
        return objectHit; // null only when neither terrain nor an object sits under the camera
    }

    /// <summary>
    ///     Casts a ray straight down from just above the eye and returns the world Z of the highest
    ///     placed-object surface at/below the eye under (<paramref name="worldX" />,
    ///     <paramref name="worldY" />), or <c>null</c> when nothing is hit. Scans the camera's cell and
    ///     its 8 neighbours (a ref whose origin sits in an adjacent cell can still overlap the camera
    ///     footprint). Warm meshes raycast against real triangles; cold meshes fall back to the OBND box
    ///     for that frame. Only called once per frame in walk mode (<c>SnapToGround</c>).
    /// </summary>
    private float? RaycastObjectGround(float worldX, float worldY, float eyeZ)
    {
        if (_cellGridLookup is null) return null;
        var gx = (int)MathF.Floor(worldX / WorldGridConstants.CellSize);
        var gy = (int)MathF.Floor(worldY / WorldGridConstants.CellSize);

        var origin = new Vector3(worldX, worldY, eyeZ + GroundRaycastEpsUp);
        var down = new Vector3(0f, 0f, -1f);

        float? best = null;
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            if (!_cellGridLookup.TryGetValue((gx + dx, gy + dy), out var cell)) continue;
            foreach (var p in cell.PlacedObjects)
            {
                if (string.IsNullOrEmpty(p.ModelPath)) continue;
                if (!_showDisabled && p.IsInitiallyDisabled) continue;
                if (p.RecordType is "ACHR" or "ACRE") continue; // skinned actors carry no static collision
                if (RenderableReference.IsMarkerModelPath(p.ModelPath) ||
                    RenderableReference.IsImposterModelPath(p.ModelPath)) continue;

                var hit = TryRaycastReferenceGround(p, origin, down, eyeZ);
                if (hit is { } h && (best is null || h > best)) best = h;
            }
        }

        return best;
    }

    /// <summary>
    ///     Intersects the world-space down-ray with one placed reference. Transforms the ray into the
    ///     mesh's local space (inverting the placement world matrix — rotation/scale exact), raycasts
    ///     the cached collision triangles, then maps the local hit point back to world to read its Z.
    ///     Falls back to the rotation-ignoring OBND box top when the collision mesh isn't cached yet.
    /// </summary>
    private float? TryRaycastReferenceGround(PlacedReference p, Vector3 worldOrigin, Vector3 worldDir, float eyeZ)
    {
        if (_referenceMeshCache12 is not null &&
            _referenceMeshCache12.TryGetCollisionMesh(p.ModelPath!, out var collision) &&
            collision is not null)
        {
            var world = PlacedReferenceTransform.ComposeWorldMatrix(p.X, p.Y, p.Z, p.RotX, p.RotY, p.RotZ, p.Scale);
            if (!Matrix4x4.Invert(world, out var inv)) return null;

            var localOrigin = Vector3.Transform(worldOrigin, inv);
            var localDir = Vector3.TransformNormal(worldDir, inv);
            if (!collision.RaycastNearest(localOrigin, localDir, out var tLocal)) return null;

            var worldHit = Vector3.Transform(localOrigin + localDir * tLocal, world);
            return worldHit.Z; // already constrained to the down-ray, so it is at/below the eye
        }

        // Cold-mesh fallback: axis-aligned OBND box top placed at the ref origin (rotation ignored),
        // gated by the same "at/below the eye" rule so it never yanks the camera onto a roof.
        if (p.Bounds is not { } b) return null;
        var scale = p.Scale > 0f ? p.Scale : 1f;
        if (worldOrigin.X < p.X + b.X1 * scale || worldOrigin.X > p.X + b.X2 * scale) return null;
        if (worldOrigin.Y < p.Y + b.Y1 * scale || worldOrigin.Y > p.Y + b.Y2 * scale) return null;
        var top = p.Z + b.Z2 * scale;
        return top <= eyeZ + GroundRaycastEpsUp ? top : null;
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
            foreach (var bsa in discovery.MeshesBsaPaths)
            {
                if (seenBsas.Add(bsa)) result.Add(bsa);
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
                    _gpu12, _commandRecorder12, _cbvSrvUavHeap12!, _referenceGpuTextureResolver12, _deletionQueue12)
                .RegisterWith(FalloutXbox360Utils.Core.Diagnostics.ResourceRegistry.Instance, "reference");
            // Capacity/budget env knobs are diagnostic levers for eviction-pressure stress gates
            // (e.g. capacity 64 + 16 MB makes the LRU eviction cascade fire constantly); defaults
            // preserve the shipped behavior. Read here, not in the cache — same as `capacity` always was.
            _referenceMeshCache12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceMeshCache12(
                _gpu12, _meshArchives, _referenceTextureResolver, _referenceTextureCache12,
                _deletionQueue12,
                capacity: FalloutXbox360Utils.Core.EnvironmentVariables.GetClampedInt(
                    FalloutXbox360Utils.Core.EnvironmentVariables.Viewer.ReferenceMeshCapacity,
                    defaultValue: 2048, min: 8, max: 65_536),
                decodedCacheByteBudget: FalloutXbox360Utils.Core.EnvironmentVariables.GetClampedLong(
                    FalloutXbox360Utils.Core.EnvironmentVariables.Viewer.ReferenceDecodedCacheMegabytes,
                    defaultValue: 256, min: 4, max: 8_192) * 1024L * 1024L,
                // Auto-size the resident-mesh cap to each worldspace's working set UNLESS the capacity
                // knob is explicitly set (then honor the pinned value — used by eviction stress gates).
                autoSizeMeshCapacity: FalloutXbox360Utils.Core.EnvironmentVariables.Get(
                    FalloutXbox360Utils.Core.EnvironmentVariables.Viewer.ReferenceMeshCapacity) is null,
                // Data-driven SpeedTree sizing: each .spt tree is scaled to its TREE-record OBND height.
                speedTreeHeights: _data?.SpeedTreeHeights);
            _references = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12,
                _cbvSrvUavHeap12, _referenceMeshCache12)
            {
                DetailedProfilingEnabled = _profileLogging,
                ShowInitiallyDisabled = _showDisabled, // persist the toggle across ESM reloads
                // Markers/imposters are hidden by default to match the game; markers are also
                // toggleable from the toolbar (_showMarkers persists across reloads).
                ShowMarkers = _showMarkers,
                ShowImposters = FalloutXbox360Utils.Core.EnvironmentVariables.IsEnabled(
                    FalloutXbox360Utils.Core.EnvironmentVariables.Viewer.ShowImposters),
            };
            _references.SetHiddenCategories(_hiddenCategories);
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

    // Top-down overlay rendering for the 2D map (ITopDownSceneRenderer) ----------------------

    // Render at 2× the requested size then box-downsample for AA — the live terrain/reference PSOs
    // are 1-sample, so MSAA isn't an option without parallel PSOs. Final long-edge is capped so a
    // large canvas doesn't blow up the per-request readback.
    private const int TopDownSupersample = 2;
    private const int MaxTopDownFinalDimension = 2048;

    /// <summary>
    ///     In-flight top-down readback Task.Run body. Tracked so DisposeRenderResources can drain
    ///     it before tearing down the D3D12 device — the body waits on the frame fence and maps a
    ///     readback buffer, both of which would race a concurrent device dispose.
    /// </summary>
    private Task? _topDownReadbackTask;

    /// <summary>Reused across top-down overlay requests to avoid per-request allocation of two
    /// committed textures + RTV/DSV heaps + a readback buffer. Recreated only when the supersampled
    /// dimensions change; disposed in <see cref="DisposeRenderResources" />.</summary>
    private GpuOffscreenSceneTarget12? _topDownTarget;
    private int _topDownTargetW;
    private int _topDownTargetH;

    // Explicit interface implementation: the interface + TopDownRender are internal, so these can't
    // be public members on this public control (CS0050). The 2D map only ever calls them via the
    // ITopDownSceneRenderer reference it's handed, so explicit implementation is exactly right.

    private bool CanRenderTopDownCore =>
        _gpu12 is not null && _commandRecorder12 is not null &&
        _ringBuffer12 is not null && _cbvSrvUavHeap12 is not null &&
        _rootSignature12 is not null && _deletionQueue12 is not null &&
        _terrain is not null && _references is not null;

    /// <inheritdoc />
    bool ITopDownSceneRenderer.CanRenderTopDown => CanRenderTopDownCore;

    /// <inheritdoc />
    async Task<TopDownRender?> ITopDownSceneRenderer.RenderTopDownAsync(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        int pixelWidth, int pixelHeight, bool showDisabled, bool showWater, uint? worldspaceFormId,
        IReadOnlyCollection<PlacedObjectCategory> hiddenCategories, CancellationToken ct)
    {
        if (!CanRenderTopDownCore) return null;
        if (worldMaxX <= worldMinX || worldMaxY <= worldMinY) return null;
        if (pixelWidth <= 0 || pixelHeight <= 0) return null;
        ct.ThrowIfCancellationRequested();

        // Sync the 3D control's active worldspace to the one the 2D map is showing (the 3D view picks
        // its own initial worldspace independently). Switching reloads the renderers' cells, so the
        // overlay matches the map instead of showing a stale worldspace. No-op when already current.
        if (!EnsureActiveExteriorWorldspace(worldspaceFormId)) return null;

        var finalW = Math.Clamp(pixelWidth, 1, MaxTopDownFinalDimension);
        var finalH = Math.Clamp(pixelHeight, 1, MaxTopDownFinalDimension);
        // When scene MSAA is active the offscreen target is multisampled (GpuOffscreenSceneTarget12
        // reads GpuDevice12.SceneSampleCount), so MSAA provides the edge AA and we skip the
        // supersample multiply — keeping the target memory-neutral (4x MSAA at NxN == 1 sample at
        // 2Nx2N) and the terrain/reference PSOs (SampleDesc = SceneSampleCount) matching the target.
        var supersample = _gpu12!.SceneSampleCount > 1 ? 1 : TopDownSupersample;
        var ssWidth = finalW * supersample;
        var ssHeight = finalH * supersample;

        // Orthographic top-down viewProj + covering cylinder (see TopDownViewProjBuilder for the
        // orientation contract — east→right, north→top, no readback flip). The renderers treat
        // viewProj as an opaque matrix, so this bypasses the perspective CameraState.
        var viewProj = TopDownViewProjBuilder.BuildViewProj(worldMinX, worldMaxX, worldMinY, worldMaxY);
        var cylinder = TopDownViewProjBuilder.BuildCoverCylinder(
            worldMinX, worldMaxX, worldMinY, worldMaxY, WorldGridConstants.CellSize);

        // Reuse the offscreen target across requests; recreate only when the supersampled size
        // changes. Avoids per-request allocation of two committed textures + RTV/DSV heaps + a
        // readback buffer (steady allocator/GPU churn that compounded the overlay-on slowdown).
        GpuOffscreenSceneTarget12 target;
        try
        {
            if (_topDownTarget is null || _topDownTargetW != ssWidth || _topDownTargetH != ssHeight)
            {
                _topDownTarget?.Dispose();
                _topDownTarget = new GpuOffscreenSceneTarget12(_gpu12!, ssWidth, ssHeight);
                _topDownTargetW = ssWidth;
                _topDownTargetH = ssHeight;
            }
            target = _topDownTarget;
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: top-down target alloc failed: {0}", ex.Message);
            _topDownTarget = null;
            _topDownTargetW = _topDownTargetH = 0;
            return null;
        }

        ulong fenceValue;
        bool isComplete;
        var prevShowDisabled = _references!.ShowInitiallyDisabled;
        var prevRefThrottled = _references.StreamingThrottled;
        var prevTerrainThrottled = _terrain!.StreamingThrottled;
        try
        {
            _references.ShowInitiallyDisabled = showDisabled;
            // Apply the 2D map's category filter for this overlay render so the rendered meshes match
            // the map's legend toggles (restored to the 3D view's own set in the finally block).
            _references.SetHiddenCategories(hiddenCategories);
            // The overlay is on-demand, not a 60fps loop, so lift the live renderers' per-frame
            // streaming budget: a single render starts every visible decode (CPU-saturating
            // concurrency) and uploads everything that has decoded. Combined with the caller's
            // re-render-while-incomplete loop, the overlay loads meshes as fast as the hardware
            // allows instead of trickling 8/frame. Restored below so the live 3D loop stays smooth.
            _references.StreamingThrottled = false;
            _terrain.StreamingThrottled = false;
            var recorder = _commandRecorder12!;
            recorder.BeginFrame();
            try
            {
                var cmd = recorder.CommandList;
                _deletionQueue12!.Tick();
                _ringBuffer12!.ResetFrame();
                _cbvSrvUavHeap12!.BeginFrame(recorder.FrameIndex);
                _gpu12!.PumpDebugMessages();

                // SetDescriptorHeaps before SetGraphicsRootSignature (bindless ordering), mirroring
                // RenderFrameD3D12.
                cmd.SetDescriptorHeaps(1, new[] { _cbvSrvUavHeap12.Heap });
                cmd.SetGraphicsRootSignature(_rootSignature12!.RootSignature);
                // Bind the atmosphere CB here too so the references the overlay draws are lit
                // consistently with the live view (reference.frag reads b3 — P3). Fog OFF: the overlay
                // is an orthographic top-down capture, where distance-from-camera fog is meaningless.
                BindAtmosphereConstants(cmd, recorder.FrameIndex, enableFog: false);

                target.Bind(cmd);
                _terrain!.RenderDepthOnly(viewProj, cylinder); // depth pre-pass: ground occludes refs
                _references.Render(viewProj, cylinder);        // textured objects, depth-tested
                if (showWater && _water is not null)
                {
                    // Height-correct water for the overlay: the offscreen DSV already holds the terrain +
                    // reference depth, so a depth-tested water pass lets the ortho-topmost surface win —
                    // submerged geometry is covered by water, geometry ABOVE the water plane (docks,
                    // bridges) shows through. A flat 2D water layer can't do this; that's why the map
                    // suppresses its own water wherever this overlay draws. No depth SRV → the hardware
                    // depth-test water PSO (same path the live frame uses when no SRV is bound); near/far
                    // are unused without the SRV soft-fade.
                    _water.SetNifWaterPlanes(_references.NifWaterPlanes);
                    _water.SetSceneDepth(NoDepthSrv, _camera.NearPlane, _camera.FarPlane);
                    _water.Render(viewProj, cylinder);
                }
                target.RecordReadback(cmd);
            }
            finally
            {
                recorder.EndFrame();
            }

            fenceValue = recorder.LastSubmittedFenceValue;
            isComplete = TopDownStreamingComplete();
            _gpu12.PumpDebugMessages();
        }
        catch (Exception ex)
        {
            // The render failed mid-record, so the shared target's resource state may be
            // inconsistent. EndFrame already submitted whatever was recorded; drain the GPU before
            // dropping the shared target so the next request rebuilds it cleanly (and we never
            // release a resource the GPU still references).
            try { _commandRecorder12?.WaitForGpuIdle(); }
            catch (Exception waitEx) { Log.Warn("WaitForGpuIdle after top-down failure threw: {0}", waitEx.Message); }
            _topDownTarget?.Dispose();
            _topDownTarget = null;
            _topDownTargetW = _topDownTargetH = 0;
            Log.Warn("WorldView3DControl: top-down render failed: {0}", ex.Message);
            try { _gpu12?.PumpDebugMessages(); }
            catch (Exception pumpEx) { Log.Warn("PumpDebugMessages threw: {0}", pumpEx.Message); }
            return null;
        }
        finally
        {
            _references.ShowInitiallyDisabled = prevShowDisabled;
            // Restore the 3D view's own category filter (the authoritative live-view set).
            _references.SetHiddenCategories(_hiddenCategories);
            _references.StreamingThrottled = prevRefThrottled;
            _terrain.StreamingThrottled = prevTerrainThrottled;
        }

        // Off the UI thread: wait for the submission fence, then map + downsample. The target is
        // SHARED/reused across requests, so the body must NOT dispose it (RecordReadback left the
        // color target back in RenderTarget state for the next Bind). Teardown disposes it in
        // DisposeRenderResources, which first drains _topDownReadbackTask so this map completes
        // before the resource is released. The fence is captured HERE: the body must not dereference
        // _gpu12, which the UI thread nulls during teardown while this task is still in flight.
        var frameFence = _gpu12!.FrameFence;
        var readbackTask = Task.Run(() =>
        {
            WaitForFrameFence(frameFence, fenceValue);
            ct.ThrowIfCancellationRequested();
            var ssBytes = target.ReadbackToBytes();
            var pixels = supersample > 1
                ? NifSpriteRenderer.Downsample(ssBytes, ssWidth, ssHeight, supersample)
                : ssBytes;
            return new TopDownRender(pixels, finalW, finalH,
                worldMinX, worldMaxX, worldMinY, worldMaxY, isComplete);
        });
        _topDownReadbackTask = readbackTask;
        try
        {
            return await readbackTask;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            if (ReferenceEquals(_topDownReadbackTask, readbackTask))
            {
                _topDownReadbackTask = null;
            }
        }
    }

    /// <summary>
    ///     Heuristic completeness: is any terrain/mesh/texture streaming work still ACTIVE this frame?
    ///     While false the 2D map keeps re-requesting so the overlay fills in (the 3D control's live
    ///     loop is idle when the map is showing, so nothing else drives the caches forward).
    ///     <para>
    ///         Keyed on in-flight work (uploads this frame + queued/active decodes + texture queue
    ///         depth) — deliberately NOT on <c>ReferenceMeshMissing</c>/<c>ReferenceTexturePending</c>,
    ///         which count assets that returned null/pending THIS frame and never reach zero when a
    ///         region has permanently-missing/cut meshes or textures (those are negative-cached, not
    ///         re-queued). Settling on "nothing actively loading" lets the overlay converge instead of
    ///         re-requesting forever.
    ///     </para>
    /// </summary>
    private bool TopDownStreamingComplete()
    {
        var t = _terrain!.LastStats;
        var r = _references!.LastStats;
        return t.NewUploads == 0
            && r.ReferenceGpuUploads == 0
            && r.ReferenceQueuedDecodes == 0
            && r.ReferenceActiveDecodes == 0
            && r.ReferenceTexturePendingResolves == 0
            && r.ReferenceTexturePendingUploads == 0;
    }

    /// <summary>Blocks until <paramref name="fence" /> reaches <paramref name="value" />. Safe to
    /// call off the UI thread (fence read + event are thread-safe). The fence is a parameter, not
    /// read from <c>_gpu12</c>, because callers run on worker threads after teardown may have
    /// nulled the field. Always completes (the offscreen frame is a single submission), so callers
    /// can dispose GPU resources afterwards safely.</summary>
    private static void WaitForFrameFence(Vortice.Direct3D12.ID3D12Fence fence, ulong value)
    {
        if (fence.CompletedValue >= value) return;
        using var ev = new AutoResetEvent(false);
        FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.D3D12FenceWaiter.WaitForFence(
            fence, value, ev);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        RenderPanel.SizeChanged -= OnRenderPanelSizeChanged;
        RenderPanel.CompositionScaleChanged -= OnRenderPanelCompositionScaleChanged;
        RenderPanel.KeyDown -= OnRenderPanelKeyDown;
        RenderPanel.KeyUp -= OnRenderPanelKeyUp;
        RenderPanel.LostFocus -= OnRenderPanelLostFocus;
        RenderPanel.PointerPressed -= OnRenderPanelPointerPressed;
        RenderPanel.PointerMoved -= OnRenderPanelPointerMoved;
        RenderPanel.PointerReleased -= OnRenderPanelPointerReleased;
        RenderPanel.PointerWheelChanged -= OnRenderPanelPointerWheelChanged;

        // Light teardown: a tab switch unloads this content but the control instance persists.
        // Keep the device + scene alive (see DetachForUnload / OnLoaded); full teardown is Dispose().
        DetachForUnload();
        _ = _pointerStartPosition;
    }

    /// <summary>
    ///     Light teardown for a tab-switch unload (the SubTabView swaps WorldMapTab content out of
    ///     the visual tree). Detaches the render loop and releases ONLY the swapchain surface — its
    ///     back buffers are bound to the SwapChainPanel being unloaded. The D3D12 device, descriptor
    ///     heaps, root signature, and the per-data scene pipelines (_terrain/_references/_water/
    ///     _navMesh/_cellGrid) + caches stay resident so 3D rendering and the 2D top-down overlay
    ///     resume instantly on return without re-streaming or resetting the camera. The persistent
    ///     <see cref="_depthSrv" /> slot is intentionally NOT nulled (its heap persists; EnsureDepthSrv
    ///     re-points it at the new depth resource on return — nulling would leak a persistent slot per
    ///     switch). Full teardown (device + all pipelines) lives in <see cref="DisposeRenderResources" />.
    /// </summary>
    private void DetachForUnload()
    {
        // Drain any in-flight top-down readback first — its Task.Run body maps the readback buffer,
        // which must not race the surface release below. Non-pumping: Task.Wait() on the STA UI
        // thread pumps messages and can re-enter XAML → CheckReentrancy fail-fast (see NonPumpingWait).
        var readback = _topDownReadbackTask;
        if (readback is { IsCompleted: false })
        {
            Core.Orchestration.NonPumpingWait.Wait(readback);
            _ = readback.Exception; // observe so it never resurfaces as an UnobservedTaskException
        }

        DetachRenderLoop();
        // The surface's back buffers must not be referenced by in-flight command lists when disposed.
        _commandRecorder12?.WaitForGpuIdle();
        _surface12?.Dispose();
        _surface12 = null;
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
        if (ShouldUpdateLayoutValue(HudPanel.MaxWidth, maxPanelWidth))
        {
            HudPanel.MaxWidth = maxPanelWidth;
        }

        var maxTextWidth = Math.Max(0d, maxPanelWidth - 16d);
        if (ShouldUpdateLayoutValue(HudText.MaxWidth, maxTextWidth))
        {
            HudText.MaxWidth = maxTextWidth;
        }
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
            EnsureDepthSrv();
            _lastFrameTime = DateTime.UtcNow;
            AttachRenderLoop();
        }
        else
        {
            // Resize requires GPU idle so the back buffers aren't referenced by in-flight
            // command lists. The recorder's WaitForGpuIdle drains the queue.
            _commandRecorder12!.WaitForGpuIdle();
            _surface12.Resize(width, height);
            EnsureDepthSrv(); // depth resource was recreated → repoint the SRV at the new one
            HideStatus();
        }
    }

    /// <summary>
    ///     Allocates (once) and (re)creates an R32_FLOAT SRV over the swap-chain's R32_TYPELESS
    ///     depth resource in the shared bindless heap, so <c>water.frag</c> can sample the scene
    ///     depth for its real water-column depth-fade. The persistent slot is allocated once and
    ///     the SRV rewritten in place after each surface resize (the depth resource changes
    ///     identity). Leaves <see cref="_depthSrv" /> null (→ proxy fade) if anything is missing.
    /// </summary>
    private void EnsureDepthSrv()
    {
        // Under scene MSAA the depth is multisampled (D32_Float MS) and can't be bound as a plain
        // Texture2D SRV; the water depth-fade is gated off in that mode and uses the view-angle proxy
        // fade instead (RenderFrameD3D12's waterUsesDepth stays false while _depthSrv is null).
        if (_surface12?.IsMsaa == true) return;

        var depth = _surface12?.DepthResource;
        if (_gpu12 is null || _cbvSrvUavHeap12 is null || depth is null)
        {
            return;
        }

        _depthSrv ??= _cbvSrvUavHeap12.AllocatePersistent();
        var srvDesc = new Vortice.Direct3D12.ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.R32_Float,
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
            Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        };
        _gpu12.Device.CreateShaderResourceView(depth, srvDesc, _depthSrv.Value.Cpu);
    }

    private void DisposeRenderResources()
    {
        // Drain any in-flight top-down readback first — its Task.Run body waits on the frame
        // fence and maps a readback buffer, which must not race the device teardown below.
        // Non-pumping: Task.Wait() on the STA UI thread pumps messages and can re-enter XAML
        // mid-teardown → CheckReentrancy fail-fast (see NonPumpingWait docs).
        var readback = _topDownReadbackTask;
        if (readback is { IsCompleted: false })
        {
            Core.Orchestration.NonPumpingWait.Wait(readback);
            // The render path already logged the failure; observe it so it never resurfaces as
            // an UnobservedTaskException, then proceed with teardown regardless.
            _ = readback.Exception;
        }

        _commandRecorder12?.WaitForGpuIdle();

        // Reused top-down overlay target — safe to release now (the readback above is drained and
        // the GPU is idle).
        _topDownTarget?.Dispose();
        _topDownTarget = null;
        _topDownTargetW = _topDownTargetH = 0;

        // Reference pipeline disposes first — it borrows the texture caches / mesh archives
        // but owns its own copy, so this is independent of the terrain stack.
        DisposeReferencePipeline();
        _navMesh?.Dispose();
        _navMesh = null;
        _water?.Dispose();
        _water = null;
        _sky?.Dispose();
        _sky = null;
        _skyBillboards?.Dispose();
        _skyBillboards = null;
        _terrain?.Dispose();
        _terrain = null;
        _textureResolver12?.Dispose();
        _textureResolver12 = null;
        _cellGrid?.Dispose();
        _cellGrid = null;
        _selectionHighlight?.Dispose();
        _selectionHighlight = null;
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
            or VirtualKey.Number4 or VirtualKey.Number5 or VirtualKey.Number6 or VirtualKey.Number7
            or VirtualKey.Number8 or VirtualKey.Number9 or VirtualKey.Number0
            or VirtualKey.F or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            if (_toggleKeysDown.Add(e.Key))
            {
                // Each toggle key flips its toolbar ToggleButton, whose Changed handler updates the
                // backing field — so keyboard and toolbar stay in sync (3D-6).
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

        // 3D-5 — Esc clears the current selection (no InspectObject fired; nothing to inspect).
        if (e.Key == VirtualKey.Escape)
        {
            ClearSelection3D();
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

        // 3D-9 click-through: collect ALL hits along the ray (same filters as the renderer so the
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
            // If the GPU device was removed (TDR / page fault — the usual "crash with no cause"),
            // attribute it. No-op when the device is fine. Needs FALLOUT_VIEWER_DRED=1 for breadcrumbs.
            try { _gpu12?.LogDeviceRemovedDiagnostics("render-frame"); }
            catch (Exception dredEx) { Log.Warn("LogDeviceRemovedDiagnostics on render-frame failure threw: {0}", dredEx.Message); }
            DetachRenderLoop();
        }
    }

    /// <summary>
    ///     D3D12 live-render path. The shared backend binds the swap chain, root signature,
    ///     descriptor heaps, and per-frame allocators once, then each renderer records its
    ///     own terrain/reference/wireframe draws into the same command list.
    /// </summary>
    // Builds the shared atmosphere from the current game hour and uploads it to the b3 root CBV,
    // bound for the whole scene. A 112-byte once-per-frame upload off the per-frame ring (always the
    // first allocation after ResetFrame, so it can't overflow). Flags: lighting on by default (P3),
    // sky/fog off until their phases enable the shader paths.
    // enableFog is forced off for the orthographic top-down overlay capture: distance-from-camera fog
    // is meaningless there (it would tint the 2D map by the overhead camera's height). The live
    // perspective path leaves it true so the weather fog shows.
    private void BindAtmosphereConstants(Vortice.Direct3D12.ID3D12GraphicsCommandList cmd, int frameIndex, bool enableFog = true)
    {
        var resolved = AtmosphereState.Resolve(
            _gameHour, _selectedWeather, _currentClimateTiming, lightingEnabled: _showLighting);
        var constants = AtmosphereConstants.From(
            resolved, _gameHour, _camera.Position, lightingEnabled: _showLighting ? 1f : 0f,
            skyEnabled: _showSky ? 1f : 0f, fogEnabled: enableFog && _showFog ? 1f : 0f, time: 0f);
        var alloc = _ringBuffer12!.Allocate(frameIndex, AtmosphereConstants.ByteSize, GpuRingBuffer12.CbAlignment);
        unsafe { *(AtmosphereConstants*)alloc.CpuPtr = constants; }
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.AtmosphereCbv, alloc.GpuAddress);
    }

    // CPU mirror of the b3 `cbuffer Atmosphere` the lighting/sky/water shaders declare in P3+. 8 float4
    // = 128 bytes (CB-aligned by the ring buffer). When a flag is 0 the shader falls back to its
    // pre-atmosphere behavior, so the toggles default the scene to today's look until turned on.
    // (CameraPosFogPower is the 8th float4; shaders that don't apply fog — e.g. skybox — declare only
    // the first 7, which is layout-safe since the new field is appended at the end.)
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct AtmosphereConstants
    {
        public Vector4 SunDirIntensity;    // xyz = sun world dir (toward sun), w = intensity
        public Vector4 SunColorLighting;   // rgb = sun color, w = lightingEnabled (0/1)
        public Vector4 AmbientColor;       // rgb = ambient, w = spare
        public Vector4 SkyTopSkyEnabled;   // rgb = sky-top color, w = skyEnabled (0/1)
        public Vector4 SkyHorizon;         // rgb = sky-horizon color, w = spare
        public Vector4 FogColorFogEnabled; // rgb = fog color, w = fogEnabled (0/1)
        public Vector4 Params;             // x = gameHour, y = fogNear, z = fogFar, w = time
        public Vector4 CameraPosFogPower;  // xyz = camera world pos, w = fog power (1 = linear)

        public const uint ByteSize = 8 * 16;

        public static AtmosphereConstants From(
            AtmosphereState.Resolved a,
            float gameHour,
            Vector3 cameraPos,
            float lightingEnabled,
            float skyEnabled,
            float fogEnabled,
            float time) => new()
            {
                SunDirIntensity = new Vector4(a.SunWorldDirection, a.SunIntensity),
                SunColorLighting = new Vector4(a.SunColor, lightingEnabled),
                AmbientColor = new Vector4(a.AmbientColor, 0f),
                SkyTopSkyEnabled = new Vector4(a.SkyTopColor, skyEnabled),
                SkyHorizon = new Vector4(a.SkyHorizonColor, 0f),
                FogColorFogEnabled = new Vector4(a.FogColor, fogEnabled),
                Params = new Vector4(gameHour, a.FogNear, a.FogFar, time),
                CameraPosFogPower = new Vector4(cameraPos, a.FogPower),
            };
    }

    private void RenderFrameD3D12()
    {
        var frameNumber = ++_profileFrameIndex;
        var frameStarted = StartProfileTimestamp();
        var recorder = _commandRecorder12!;
        var surface = _surface12!;

        var segmentStarted = StartProfileTimestamp();
        recorder.BeginFrame();
        EmitCompletedGpuFrames();
        _gpuTimestampProfiler12?.BeginFrame(recorder.FrameIndex);
        var cmd = recorder.CommandList;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.FrameStart);
        // Deletion queue advances frame counter — releases resources whose hold has elapsed.
        // Must run AFTER BeginFrame's fence wait so the prior GPU work is known complete.
        _deletionQueue12!.Tick();
        _ringBuffer12!.ResetFrame();
        _cbvSrvUavHeap12!.BeginFrame(recorder.FrameIndex);
        // Drain D3D12 debug layer messages from the prior frame (no-op unless
        // FALLOUT_VIEWER_D3D12_DEBUG=1 enabled the layer).
        _gpu12!.PumpDebugMessages();
        var beginFrameMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        var (backBuffer, backRtv) = surface.AcquireBackBufferRtv();
        // Scene renders into the MSAA color target (resolved into the back buffer before Present)
        // when scene-wide MSAA is active; otherwise straight into the back buffer.
        var sceneRtv = surface.IsMsaa ? surface.MsaaColorRtv : backRtv;
        var sceneDsv = surface.DepthStencilView;
        var acquireMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        // The MSAA color target stays in RENDER_TARGET across frames; only the non-MSAA back buffer
        // needs PRESENT → RENDER_TARGET here (the MSAA back buffer is handled by ResolveTo at frame end).
        if (!surface.IsMsaa)
        {
            cmd.ResourceBarrierTransition(backBuffer,
                Vortice.Direct3D12.ResourceStates.Present,
                Vortice.Direct3D12.ResourceStates.RenderTarget);
        }

        cmd.ClearRenderTargetView(sceneRtv,
            new Vortice.Mathematics.Color4(0x1B / 255f, 0x24 / 255f, 0x36 / 255f, 1f));
        // Reversed-Z: clear depth to 0 (the far value). Pairs with CameraState.ReverseZ + every
        // scene PSO's GreaterEqual depth test.
        cmd.ClearDepthStencilView(sceneDsv,
            Vortice.Direct3D12.ClearFlags.Depth, 0f, 0);
        cmd.OMSetRenderTargets(sceneRtv, sceneDsv);
        cmd.RSSetViewport(new Vortice.Mathematics.Viewport(0, 0, surface.Width, surface.Height, 0f, 1f));
        cmd.RSSetScissorRect((int)surface.Width, (int)surface.Height);

        // Bind the shared shader-visible descriptor heap + root signature once per frame.
        // SetDescriptorHeaps MUST precede SetGraphicsRootSignature: the root signature carries
        // CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED (bindless), and D3D12 requires the CBV/SRV/UAV heap
        // to be bound before a directly-indexed root signature is set (else the debug layer
        // errors every frame and bindless lookups read from an unbound heap).
        // Each renderer sets only its own PSO + per-slot bindings inside Render().
        cmd.SetDescriptorHeaps(1, new[] { _cbvSrvUavHeap12.Heap });
        cmd.SetGraphicsRootSignature(_rootSignature12!.RootSignature);
        var clearSetupMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        // Camera + cylinder match the D3D11 path so visible-cell sets are identical.
        var aspect = surface.Width / (float)surface.Height;
        // Decouple the far plane from the streaming radius: the cylinder (= _renderDistance) controls
        // what LOADS; the far plane only needs to be large enough never to clip the loaded set from
        // any altitude/tilt (this was the horizon-cutoff bug — far plane == render distance). The
        // farthest loaded point is within _renderDistance horizontally and |Z| vertically of the
        // camera, so cover that plus a couple cells of slack. Reversed-Z keeps precision at range.
        _camera.FarPlane = _renderDistance * 2f + MathF.Abs(_camera.Position.Z)
                           + 2f * WorldGridConstants.CellSize;
        var view = _camera.GetViewMatrix();
        var proj = _camera.GetProjectionMatrix(aspect);
        var viewProj = view * proj;
        var cylinder = new VisibilityCylinder(_camera.Position, _renderDistance);
        var cameraMs = ElapsedMilliseconds(segmentStarted);

        // Resolve + upload the shared atmosphere CB (b3) once per frame, bound for the whole scene.
        // terrain/reference/water read it for directional + ambient lighting (P3); sky/fog flags stay
        // off until their phases enable those shader paths. Bound once — the renderers only set their
        // own PSO + slots, never the root signature, so this CBV survives every scene pass.
        BindAtmosphereConstants(cmd, recorder.FrameIndex);

        // Sky FIRST — gradient + clouds + stars into the cleared color target (depth OFF, so terrain
        // overwrites it via the normal depth pass; OFF ⇒ the flat dark-blue clear shows), then the
        // sun/moon billboards over it. Reads the b3 atmosphere CB bound just above.
        if (_showSky)
        {
            RenderSky(viewProj);
        }

        // Layer order matches D3D11: terrain → references → water → wireframe. Water is
        // alpha-blended depth-read, so it must come after terrain + references (which write
        // the depth that water samples). Wireframe last so it stays on top (depth-disabled).
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.TerrainStart);
        var visibleTerrain = _showTerrain ? _terrain?.Render(viewProj, cylinder) ?? 0 : 0;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.TerrainEnd);
        var terrainMs = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ReferencesStart);
        // 3D-8: opaque references draw now (write depth) but blended/transparent submeshes are deferred
        // until AFTER the water pass via RenderBlendedDeferred(), so water never paints over them.
        var visibleReferences = _showReferences ? _references?.Render(viewProj, cylinder, deferBlended: true) ?? 0 : 0;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.ReferencesEnd);
        var referencesMs = ElapsedMilliseconds(segmentStarted);
        // Hand the water renderer the placed-NIF water planes the reference pass accumulated (cave/
        // pool water embedded in REFR meshes) so they draw with the real Fresnel/ripple/depth-fade
        // water shader instead of the flat slab. Cheap reference assignment; the list grows as those
        // references' meshes stream in.
        if (_water is not null && _references is not null)
        {
            _water.SetNifWaterPlanes(_references.NifWaterPlanes);
        }
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WaterStart);
        // Feed water the real scene depth (terrain + references already wrote it) so it computes the
        // WATER000-style water-column depth fade. The depth-sample PSO binds no DSV, so for this pass
        // only: unbind the depth target and transition the R32_TYPELESS depth DEPTH_WRITE →
        // PIXEL_SHADER_RESOURCE, then restore both for the depth-reading navmesh/wireframe overlays.
        // Without a depth SRV, water keeps the hardware-depth-test PSO + view-angle proxy (DSV stays).
        var depthRes = surface.DepthResource;
        var waterUsesDepth = _showWater && _water is not null && _depthSrv is not null && depthRes is not null;
        _water?.SetSceneDepth(
            waterUsesDepth ? _depthSrv!.Value.BindlessIndex : NoDepthSrv,
            _camera.NearPlane,
            _camera.FarPlane);
        if (waterUsesDepth)
        {
            cmd.OMSetRenderTargets(sceneRtv); // drop the DSV; keep the scene color RTV
            cmd.ResourceBarrierTransition(depthRes!,
                Vortice.Direct3D12.ResourceStates.DepthWrite,
                Vortice.Direct3D12.ResourceStates.PixelShaderResource);
        }
        var visibleWater = _showWater ? _water?.Render(viewProj, cylinder) ?? 0 : 0;
        if (waterUsesDepth)
        {
            cmd.ResourceBarrierTransition(depthRes!,
                Vortice.Direct3D12.ResourceStates.PixelShaderResource,
                Vortice.Direct3D12.ResourceStates.DepthWrite);
            cmd.OMSetRenderTargets(sceneRtv, sceneDsv);
        }
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WaterEnd);
        var waterMs = ElapsedMilliseconds(segmentStarted);
        // 3D-8: blended (transparent) reference submeshes draw AFTER water so water never paints over
        // them. Water wrote no depth, so they depth-test against the opaque scene depth (terrain +
        // opaque refs); the DSV was restored above, so this stays depth-correct.
        if (_showReferences) _references?.RenderBlendedDeferred();
        // Navmesh overlay — translucent, drawn after water (depth-read) and before the
        // depth-disabled wireframe so the grid still stays on top.
        var visibleNavMesh = _showNavMesh ? _navMesh?.Render(viewProj, cylinder) ?? 0 : 0;
        segmentStarted = StartProfileTimestamp();
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WireframeStart);
        var visibleWireframe = _showWireframe ? _cellGrid?.Render(viewProj, cylinder) ?? 0 : 0;
        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.WireframeEnd);
        var wireframeMs = ElapsedMilliseconds(segmentStarted);

        // 3D-5 — selection outline, drawn last + depth-disabled so it stays visible on top of everything
        // (no-op when nothing is selected).
        _selectionHighlight?.Render(viewProj);

        segmentStarted = StartProfileTimestamp();
        var visible = Math.Max(visibleTerrain, visibleWireframe);
        var totalCells = _terrain?.CellCount ?? _cellGrid?.CellCount ?? 0;
        UpdateHud(visible, totalCells, visibleWater, visibleReferences, visibleNavMesh);
        var hudMs = ElapsedMilliseconds(segmentStarted);

        segmentStarted = StartProfileTimestamp();
        if (surface.IsMsaa)
        {
            // Resolve the multisampled scene color into the back buffer; leaves the back buffer in
            // PRESENT (and the MSAA color back in RENDER_TARGET for the next frame).
            surface.ResolveTo(cmd, backBuffer);
        }
        else
        {
            // RENDER_TARGET → PRESENT so DXGI can flip.
            cmd.ResourceBarrierTransition(backBuffer,
                Vortice.Direct3D12.ResourceStates.RenderTarget,
                Vortice.Direct3D12.ResourceStates.Present);
        }

        _gpuTimestampProfiler12?.Write(cmd, GpuTimestampRegion.FrameEnd);
        _gpuTimestampProfiler12?.ResolveActiveFrame(cmd);
        recorder.EndFrame();
        _gpuTimestampProfiler12?.MarkActiveFrameSubmitted(frameNumber, recorder.LastSubmittedFenceValue);
        var endFrameMs = ElapsedMilliseconds(segmentStarted);
        segmentStarted = StartProfileTimestamp();
        surface.Present();
        var presentMs = ElapsedMilliseconds(segmentStarted);

        var totalFrameMs = ElapsedMilliseconds(frameStarted);
        var gcGen0Collections = GC.CollectionCount(0);
        var gcGen1Collections = GC.CollectionCount(1);
        var gcGen2Collections = GC.CollectionCount(2);
        var gcGen0Delta = gcGen0Collections - _lastGcGen0Collections;
        var gcGen1Delta = gcGen1Collections - _lastGcGen1Collections;
        var gcGen2Delta = gcGen2Collections - _lastGcGen2Collections;
        _lastGcGen0Collections = gcGen0Collections;
        _lastGcGen1Collections = gcGen1Collections;
        _lastGcGen2Collections = gcGen2Collections;

        var terrainStats = _showTerrain ? _terrain?.LastStats.Snapshot() : null;
        var waterStats = _showWater ? _water?.LastStats.Snapshot() : null;
        var wireframeStats = _showWireframe ? _cellGrid?.LastStats.Snapshot() : null;
        var referenceStats = _showReferences ? _references?.LastStats.Snapshot() : null;
        var sample = new FrameProfileSample(
            FrameNumber: frameNumber,
            TotalMilliseconds: totalFrameMs,
            ControllerMilliseconds: _lastControllerUpdateMilliseconds,
            BeginFrameMilliseconds: beginFrameMs,
            FenceWaitMilliseconds: recorder.LastFrameFenceWaitMilliseconds,
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
            RingBytes: _ringBuffer12.CurrentFrameBytes,
            GcGen0Collections: gcGen0Delta,
            GcGen1Collections: gcGen1Delta,
            GcGen2Collections: gcGen2Delta,
            ManagedMemoryBytes: GC.GetTotalMemory(false));

        if (_profileLogging)
        {
            MaybeLogProfile(sample, terrainStats, waterStats, wireframeStats, referenceStats);
        }

        if (_stallThresholdMilliseconds > 0 && sample.TotalMilliseconds >= _stallThresholdMilliseconds)
        {
            EmitFrameStall(sample, terrainStats, waterStats, wireframeStats, referenceStats);
            if (_gpuTimestampProfiler12 is null && !_gpuTimestampsAutoEnabled)
            {
                _gpuTimestampsAutoEnabled = true;
                EnableGpuTimestamps("auto-stall");
            }
        }
    }

    private void EnableGpuTimestamps(string reason)
    {
        if (_gpuTimestampProfiler12 is not null || _gpu12 is null)
        {
            return;
        }

        try
        {
            _gpuTimestampProfiler12 = new GpuTimestampProfiler12(_gpu12);
            Log.Info("WorldView3DControl: D3D12 GPU timestamp profiling enabled ({0}).", reason);
            RendererProfilerTrace.Event("gpu-timestamps-enabled", new Dictionary<string, object?>
            {
                ["frame"] = _profileFrameIndex,
                ["reason"] = reason
            });
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: failed to enable GPU timestamp profiling: {0}", ex.Message);
        }
    }

    private void EmitCompletedGpuFrames()
    {
        if (_gpuTimestampProfiler12 is null || !RendererProfilerTrace.IsEnabled)
        {
            return;
        }

        while (_gpuTimestampProfiler12.TryCollectCompleted(out var timings))
        {
            RendererProfilerTrace.Event("gpu-frame", new Dictionary<string, object?>
            {
                ["frame"] = timings.FrameNumber,
                ["gpuFrameMs"] = timings.FrameMilliseconds,
                ["gpuTerrainMs"] = timings.TerrainMilliseconds,
                ["gpuReferencesMs"] = timings.ReferencesMilliseconds,
                ["gpuWaterMs"] = timings.WaterMilliseconds,
                ["gpuWireframeMs"] = timings.WireframeMilliseconds
            });
        }
    }

    private void EmitFrameStall(
        FrameProfileSample sample,
        WorldRenderStats? terrain,
        WorldRenderStats? water,
        WorldRenderStats? wireframe,
        WorldRenderStats? references)
    {
        Log.Warn(
            "3D stall frame={0} total={1:0.00}ms threshold={2:0.00}ms terrain={3:0.00}ms refs={4:0.00}ms present={5:0.00}ms fence={6:0.00}ms",
            sample.FrameNumber,
            sample.TotalMilliseconds,
            _stallThresholdMilliseconds,
            sample.TerrainMilliseconds,
            sample.ReferencesMilliseconds,
            sample.PresentMilliseconds,
            sample.FenceWaitMilliseconds);

        var fields = BuildFrameFields(sample);
        fields["thresholdMs"] = _stallThresholdMilliseconds;
        AddStats(fields, "terrain.", terrain);
        AddStats(fields, "water.", water);
        AddStats(fields, "wire.", wireframe);
        AddStats(fields, "refs.", references);
        RendererProfilerTrace.Event("frame-stall", fields);
    }

    private Dictionary<string, object?> BuildFrameFields(FrameProfileSample sample)
    {
        var fields = RendererProfilerTrace.CameraPoseFields(Profiler_CameraPose);
        fields["frame"] = sample.FrameNumber;
        fields["frameMs"] = sample.TotalMilliseconds;
        fields["controllerMs"] = sample.ControllerMilliseconds;
        fields["beginFrameMs"] = sample.BeginFrameMilliseconds;
        fields["fenceWaitMs"] = sample.FenceWaitMilliseconds;
        fields["acquireMs"] = sample.AcquireMilliseconds;
        fields["clearMs"] = sample.ClearSetupMilliseconds;
        fields["cameraMs"] = sample.CameraMilliseconds;
        fields["terrainMs"] = sample.TerrainMilliseconds;
        fields["referencesMs"] = sample.ReferencesMilliseconds;
        fields["waterMs"] = sample.WaterMilliseconds;
        fields["wireframeMs"] = sample.WireframeMilliseconds;
        fields["endFrameMs"] = sample.EndFrameMilliseconds;
        fields["presentMs"] = sample.PresentMilliseconds;
        fields["hudMs"] = sample.HudMilliseconds;
        fields["visibleTerrain"] = sample.VisibleTerrain;
        fields["visibleReferences"] = sample.VisibleReferences;
        fields["visibleWater"] = sample.VisibleWater;
        fields["visibleWireframe"] = sample.VisibleWireframe;
        fields["totalCells"] = sample.TotalCells;
        fields["renderDistanceCells"] = sample.RenderDistanceCells;
        fields["viewportWidth"] = sample.ViewportWidth;
        fields["viewportHeight"] = sample.ViewportHeight;
        fields["descriptorCount"] = sample.DescriptorCount;
        fields["ringBytes"] = sample.RingBytes;
        fields["gcGen0"] = sample.GcGen0Collections;
        fields["gcGen1"] = sample.GcGen1Collections;
        fields["gcGen2"] = sample.GcGen2Collections;
        fields["managedMemoryBytes"] = sample.ManagedMemoryBytes;
        return fields;
    }

    private static void AddStats(
        Dictionary<string, object?> fields,
        string prefix,
        WorldRenderStats? stats)
    {
        foreach (var item in RendererProfilerTrace.StatsFields(prefix, stats))
        {
            fields[item.Key] = item.Value;
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
            var debugLayer = EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.D3D12Debug);
            _gpu12 = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDevice12.Create(debugLayer);
            if (_gpu12 is null) return false;

            _commandRecorder12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12(_gpu12);
            // 64 MB per frame slot (env-overridable). Shared by every renderer's per-draw CBs. The
            // live view is frustum+cylinder culled so it stays well under this, but the unthrottled
            // top-down overlay can render a very dense window in one pass; 64 MB gives ~262 k
            // 256-byte allocations of headroom (4× the prior 16 MB) before TryAllocate degrades
            // gracefully (stops adding draws, presents what fit) rather than abandoning the frame.
            var ringMegabytes = EnvironmentVariables.GetClampedInt(
                EnvironmentVariables.Viewer.RingBufferMegabytes, defaultValue: 64, min: 16, max: 512);
            _ringBuffer12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRingBuffer12(
                _gpu12,
                FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12.FramesInFlight,
                bytesPerFrame: (uint)ringMegabytes * 1024 * 1024);
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
                    persistentCapacity: 16384)
                .RegisterWith(FalloutXbox360Utils.Core.Diagnostics.ResourceRegistry.Instance, "viewer");
            _rootSignature12 = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12.Create(_gpu12);
            _deletionQueue12 = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDeletionQueue12(
                    framesToHold: FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12.FramesInFlight)
                .RegisterWith(FalloutXbox360Utils.Core.Diagnostics.ResourceRegistry.Instance, "viewer");
            if (_forceGpuTimestamps)
            {
                EnableGpuTimestamps("forced");
            }

            // Step 2b — instantiate the simplest D3D12 renderer end-to-end. This proves
            // PSO creation + ring-buffer-backed CB/VB + root signature + command list draws
            // all hang together before the heavier renderers (terrain/reference/water) port.
            _cellGrid = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.CellGridDebugRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12)
            {
                DetailedProfilingEnabled = _profileLogging,
            };

            // 3D-5 — selection outline overlay. Same lightweight backend handles as the cell grid;
            // created once and reused across worldspace/cell switches (selection state lives on the
            // control and is cleared on those switches).
            _selectionHighlight = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.SelectionHighlightRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12);

            // Step 2e — WaterRenderer12 doesn't depend on per-ESM data (water height is
            // discovered at LoadData), so we can instantiate up front alongside CellGrid.
            _water = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.WaterRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _cbvSrvUavHeap12, _deletionQueue12)
            {
                DetailedProfilingEnabled = _profileLogging,
            };

            // Atmosphere skybox (P5+) — gradient + cloud/star textures; reads the shared b3 CB + its own
            // b0 (invViewProj + cloud/star params) and the shared bindless table for its textures.
            _sky = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.SkyboxRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _cbvSrvUavHeap12);

            // Textured sky billboards — sun (disc + glare) + moon, drawn after the gradient. Uses the
            // shared bindless table (textures resolved per-climate from the terrain texture cache).
            _skyBillboards = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.SkyBillboardRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _cbvSrvUavHeap12);

            // Navmesh overlay — like water/cellgrid, no per-ESM dependency at construction;
            // the navmesh-by-cell data + spatial index arrive in LoadData via TryBuildCellGrid.
            _navMesh = new FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.NavMeshRenderer12(
                _gpu12, _commandRecorder12, _ringBuffer12, _rootSignature12, _deletionQueue12)
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
        _gpuTimestampProfiler12?.Dispose(); _gpuTimestampProfiler12 = null;
        // Drain pending deletions now that the GPU is idle — anything queued is safe to release.
        _deletionQueue12?.Dispose(); _deletionQueue12 = null;
        _rootSignature12?.Dispose(); _rootSignature12 = null;
        _cbvSrvUavHeap12?.Dispose(); _cbvSrvUavHeap12 = null;
        _depthSrv = null; // its slot lived in the heap just disposed; reallocate on the next surface

        _ringBuffer12?.Dispose(); _ringBuffer12 = null;
        _surface12?.Dispose(); _surface12 = null;
        _commandRecorder12?.Dispose(); _commandRecorder12 = null;
        _gpu12?.Dispose(); _gpu12 = null;
    }

    // Camera framing ------------------------------------------------------------------------

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
        var dist = MathF.Max(extent, WorldGridConstants.CellSize) * 0.75f;

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
        string.Equals(_stressScene, "WastelandNVHeavy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(_stressScene, "WastelandNV", StringComparison.OrdinalIgnoreCase);

    private static bool WorldspaceLooksLikeWastelandNv(WorldspaceRecord worldspace) =>
        ContainsWorldspaceToken(worldspace.EditorId, "WastelandNV") ||
        ContainsWorldspaceToken(worldspace.FullName, "WastelandNV") ||
        ContainsWorldspaceToken(worldspace.FullName, "Mojave");

    private static bool ContainsWorldspaceToken(string? value, string token) =>
        value?.Contains(token, StringComparison.OrdinalIgnoreCase) == true;

    // Toolbar checkboxes mirror the 2D viewer. Handlers fire once during XAML load with
    // IsChecked="True" (Water only) before sibling fields are assigned, so each must be safe to
    // call before LoadData — they touch only their own control (assigned) and null-guarded state.
    private void CellsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showWireframe = CellsCheckBox.IsChecked == true;
    }

    private void TerrainToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showTerrain = TerrainToggle.IsChecked == true;
    }

    private void WaterCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showWater = WaterCheckBox.IsChecked == true;
    }

    private void TerrainTexturesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showTerrainTextures = TerrainTexturesToggle.IsChecked == true;
        _terrain?.SetDebugModes(_showTerrainTextures, _showVertexColors);
    }

    private void VertexColorsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showVertexColors = VertexColorsToggle.IsChecked == true;
        _terrain?.SetDebugModes(_showTerrainTextures, _showVertexColors);
    }

    private void RefsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showReferences = RefsToggle.IsChecked == true;
    }

    private void LightingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showLighting = LightingToggle.IsOn;
    }

    private void SkyboxToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showSky = SkyboxToggle.IsChecked == true;
    }

    private void FogToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showFog = FogToggle.IsOn;
    }

    private void NavMeshCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowNavMesh(NavMeshCheckBox.IsChecked == true);
    }

    private void DisabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowDisabled(DisabledCheckBox.IsChecked == true);
    }

    private void EditorMarkersCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowMarkers(EditorMarkersCheckBox.IsChecked == true);
    }

    private void ActivatorsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        SetShowActivators(ActivatorsCheckBox.IsChecked == true);
    }

    private async void WorldspaceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWorldspaceSelectionEvent || _data is null) return;
        if (WorldspaceComboBox.SelectedIndex < 0) return;

        // Picking a worldspace leaves interior mode and shows that exterior. Building the spatial
        // index + loading the renderers for a large worldspace briefly blocks the UI thread, which
        // used to read as a frozen blank with no feedback. Show a loading overlay first and yield so
        // it actually paints before the blocking work, then hide it once the grid is built (mesh /
        // terrain streaming continues asynchronously afterwards while the controls stay responsive).
        _selectedInterior = null;
        HideInteriorBrowser();
        ShowStatus("Loading worldspace…");
        try
        {
            await System.Threading.Tasks.Task.Yield();
            TryBuildCellGrid();
            if (_pendingNavigateCell is { } target)
            {
                _pendingNavigateCell = null;
                CenterCameraOnCell(target);
            }
            else
            {
                ResetCameraToDataCentroid();
            }
            ApplyStressSceneBookmarkIfRequested();
        }
        finally
        {
            HideStatus();
        }

        RefreshAtmosphereForCurrentWorldspace();
    }

    private void TryBuildCellGrid()
    {
        if (_data is null) return;

        // 3D-5 — the prior selection belongs to the set we're replacing (different worldspace/cell, and
        // FormIDs can be reused), so drop it on every rebuild.
        ClearSelection3D();

        if (_selectedInterior is { } interior)
        {
            BuildInteriorCellGrid(interior);
            return;
        }

        var (cells, defaultWaterHeight) = GetSelectedWorldspaceCells(_data);
        var cellList = cells.ToList();

        var activeWorldspaceFormId = GetSelectedWorldspaceFormId(_data);
        var markers = GetSelectedWorldspaceMarkers(_data, activeWorldspaceFormId);
        _spatialIndex = WorldSpatialIndex.Build(
            _data, cellList, markers, activeWorldspaceFormId, defaultWaterHeight);

        _cellGridLookup = _spatialIndex.CellsByGrid.ToDictionary(kv => kv.Key, kv => kv.Value);
        var appearance = GetSelectedWaterAppearance(_data);
        var noiseIndex = _textureResolver12?.ResolveNormalMapBindlessIndex(appearance?.NoiseTexture);
        _cellGrid?.LoadData(cellList, _spatialIndex);
        if (ComputeGridZExtent(cellList) is { } zext) _cellGrid?.SetWorldZExtent(zext.zMin, zext.zMax);
        _terrain?.LoadData(_cellGridLookup, _spatialIndex, _data.RenderCache);
        _water?.LoadData(_cellGridLookup, defaultWaterHeight, _spatialIndex, appearance, noiseIndex);
        _references?.LoadData(_data.RenderCache, _cellGridLookup, _spatialIndex);
        _navMesh?.LoadData(_data.NavMeshesByCell, _cellGridLookup, _spatialIndex);
    }

    /// <summary>
    ///     Resolves the WATR appearance (DNAM Shallow/Deep/Reflection colors) for the selected
    ///     worldspace's default water — mirrors the 2D viewer, which colors the whole worldspace
    ///     from its single <c>WaterFormId</c>. Null (unlinked-exterior / no WATR) lets the
    ///     renderer fall back to a default tint.
    /// </summary>
    private FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World.WaterAppearance? GetSelectedWaterAppearance(WorldViewData data)
    {
        var index = WorldspaceComboBox.SelectedIndex;
        if (index < 0 || index >= data.Worldspaces.Count) return null;
        if (data.Worldspaces[index].WaterFormId is not uint waterFormId) return null;
        return data.WatersByFormId.TryGetValue(waterFormId, out var water)
            ? FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World.WaterAppearance.FromWaterRecord(water)
            : null;
    }

    // ── Interior cell browser (shared 2D WorldMapCellBrowser logic) ──────────────────────────

    private void InteriorsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null || _data.InteriorCells.Count == 0) return;
        PopulateInteriorBrowser();
        CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void CellBrowserCloseButton_Click(object sender, RoutedEventArgs e) => HideInteriorBrowser();

    private void HideInteriorBrowser() => CellBrowserPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;

    private void PopulateInteriorBrowser()
    {
        if (_data is null) return;
        // groupInteriors:false → group by first letter (A..Z), like the 2D viewer's Interiors browser.
        _allInteriorItems = WorldMapCellBrowser.BuildCellListItems(_data.InteriorCells, groupInteriors: false, _data);
        CellSearchBox.Text = "";
        FilterHasObjects.IsChecked = false;
        FilterNamedOnly.IsChecked = false;
        RebuildInteriorList(_allInteriorItems);
    }

    private void CellSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyInteriorFilters();

    private void CellFilter_Changed(object sender, RoutedEventArgs e) => ApplyInteriorFilters();

    private void ApplyInteriorFilters()
    {
        var query = CellSearchBox.Text?.Trim() ?? "";
        var hasObjects = FilterHasObjects.IsChecked == true;
        var namedOnly = FilterNamedOnly.IsChecked == true;
        RebuildInteriorList(WorldMapCellBrowser.ApplyFilters(_allInteriorItems, query, hasObjects, namedOnly));
    }

    private void RebuildInteriorList(List<WorldMapControl.CellListItem> items)
    {
        var source = WorldMapCellBrowser.BuildGroupedSource(items);
        var cvs = new Microsoft.UI.Xaml.Data.CollectionViewSource { IsSourceGrouped = true, Source = source };
        CellListView.ItemsSource = cvs.View;
        CellBrowserCountText.Text = $"{items.Count} cells";
    }

    private void CellListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        // ItemClick (not SelectionChanged) so ONLY a real user click loads a cell. Reassigning
        // ItemsSource on open/filter auto-selects item 0 and fired SelectionChanged, which used to
        // load the first interior on open and hijack every search keystroke — ItemClick never fires
        // for programmatic selection or type-ahead, so both symptoms are gone.
        if (e.ClickedItem is not WorldMapControl.CellListItem item) return;

        _selectedInterior = item.Cell;
        // Drop the combo selection so re-picking the same worldspace later still returns to exterior.
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.SelectedIndex = -1;
        _suppressWorldspaceSelectionEvent = false;

        HideInteriorBrowser();
        TryBuildCellGrid();
        ResetCameraToInteriorBounds(item.Cell);
        RefreshAtmosphereForCurrentWorldspace();
    }

    /// <summary>
    ///     Single-cell load path for an interior: builds a synthetic-grid spatial index
    ///     (<see cref="WorldSpatialIndex.BuildInterior" />) and loads the renderers against it.
    ///     Terrain has no LAND so it renders nothing; references/water/navmesh resolve via the
    ///     synthetic key. Water uses the interior's own XCLW (no worldspace default), default tint.
    /// </summary>
    private void BuildInteriorCellGrid(CellRecord interior)
    {
        if (_data is null) return;

        _spatialIndex = WorldSpatialIndex.BuildInterior(_data, interior);
        _cellGridLookup = _spatialIndex.CellsByGrid.ToDictionary(kv => kv.Key, kv => kv.Value);
        var cellList = new List<CellRecord> { interior };

        _cellGrid?.LoadData(cellList, _spatialIndex);
        if (ComputeGridZExtent(cellList) is { } zext) _cellGrid?.SetWorldZExtent(zext.zMin, zext.zMax);
        _terrain?.LoadData(_cellGridLookup, _spatialIndex, _data.RenderCache);
        _water?.LoadData(_cellGridLookup, worldspaceDefaultWaterHeight: null, _spatialIndex);
        _references?.LoadData(_data.RenderCache, _cellGridLookup, _spatialIndex);
        _navMesh?.LoadData(_data.NavMeshesByCell, _cellGridLookup, _spatialIndex);
    }

    /// <summary>
    ///     Computes the vertical (Z) extent the cell-grid walls should span for the loaded cells,
    ///     from the placed-object Z range (objects rest on the terrain, so this brackets the relief
    ///     and tall structures) plus a one-cell margin, with a minimum span so flat worldspaces still
    ///     show tall-enough walls. Returns null when no finite placed-object Z exists (grid keeps its
    ///     default extent).
    /// </summary>
    private static (float zMin, float zMax)? ComputeGridZExtent(IReadOnlyCollection<CellRecord> cells)
    {
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var cell in cells)
        {
            foreach (var o in cell.PlacedObjects)
            {
                if (!float.IsFinite(o.Z)) continue;
                if (o.Z < minZ) minZ = o.Z;
                if (o.Z > maxZ) maxZ = o.Z;
            }
        }
        if (minZ > maxZ) return null;

        var margin = WorldGridConstants.CellSize;
        var zMin = minZ - margin;
        var zMax = maxZ + margin;
        var minSpan = WorldGridConstants.CellSize * 2f;
        if (zMax - zMin < minSpan)
        {
            var mid = (zMin + zMax) * 0.5f;
            zMin = mid - minSpan * 0.5f;
            zMax = mid + minSpan * 0.5f;
        }
        return (zMin, zMax);
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

    // --- Atmosphere weather/time (P4) ----------------------------------------------------------------

    /// <summary>The WorldspaceRecord currently shown in the 3D scene, or null for an interior /
    /// unlinked-exterior / nothing selected (those have no climate → placeholder atmosphere).</summary>
    private WorldspaceRecord? CurrentExteriorWorldspace()
    {
        if (_data is null || _selectedInterior is not null) return null;
        var index = WorldspaceComboBox.SelectedIndex;
        return index >= 0 && index < _data.Worldspaces.Count ? _data.Worldspaces[index] : null;
    }

    private ClimateRecord? ResolveClimate(WorldspaceRecord? ws)
    {
        if (_data is null || ws?.ClimateFormId is not uint climateFormId) return null;
        return _data.ClimatesByFormId.TryGetValue(climateFormId, out var climate) ? climate : null;
    }

    /// <summary>The climate's default weather = its first WLST entry (highest-priority candidate),
    /// resolved to a record. Null when the worldspace has no climate / weather list.</summary>
    private WeatherRecord? ResolveClimateDefaultWeather(WorldspaceRecord? ws)
    {
        var climate = ResolveClimate(ws);
        if (_data is null || climate is null || climate.WeatherTypes.Count == 0) return null;
        return _data.WeathersByFormId.TryGetValue(climate.WeatherTypes[0].WeatherFormId, out var w) ? w : null;
    }

    /// <summary>Refreshes the climate timing + default weather for whatever worldspace is now showing,
    /// then re-applies the dropdown selection (so "(Climate default)" picks up the new default).</summary>
    private void RefreshAtmosphereForCurrentWorldspace()
    {
        var ws = CurrentExteriorWorldspace();
        _currentClimateTiming = AtmosphereState.ClimateTiming.FromClimateData(ResolveClimate(ws)?.Timing);
        _climateDefaultWeather = ResolveClimateDefaultWeather(ws);
        _skyTexClimateKey = null; // force the sky billboards to re-resolve sun/moon textures next frame
        ApplyWeatherSelection();
    }

    // Resolves the sun / sun-glare / moon bindless texture indices for the current climate, once per
    // climate change. Runs inside the render frame (a command list is open for the texture cache's first
    // upload). Sun + glare come from the CLMT (FNAM/GNAM), falling back to the fixed FNV sky paths; the
    // moon uses the fixed full-Masser texture (the viewer has no days-passed counter to phase it).
    private void EnsureSkyTexturesResolved()
    {
        if (_textureResolver12 is null) return; // retry once a worldspace (and its resolver) has loaded
        var climate = ResolveClimate(CurrentExteriorWorldspace());
        var key = climate?.FormId ?? 0u;
        if (_skyTexClimateKey == key) return;
        _skyTexClimateKey = key;

        const uint none = FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12.SkyBillboardRenderer12.NoTexture;
        _sunDiscTexIndex = _textureResolver12.ResolveDiffuseBindlessIndex(climate?.SunTexture ?? DefaultSunTexturePath) ?? none;
        _sunGlareTexIndex = _textureResolver12.ResolveDiffuseBindlessIndex(climate?.SunGlareTexture ?? DefaultSunGlareTexturePath) ?? none;
        _moonTexIndex = _textureResolver12.ResolveDiffuseBindlessIndex(DefaultMoonTexturePath) ?? none;
        // Cloud + star textures aren't in the ESM (the engine reads them from the sky-dome NIF), so the
        // viewer uses fixed FNV sky textures as the stand-in for the dome layers.
        _cloudTexIndex = _textureResolver12.ResolveDiffuseBindlessIndex(DefaultCloudTexturePath) ?? none;
        _starTexIndex = _textureResolver12.ResolveDiffuseBindlessIndex(DefaultStarTexturePath) ?? none;
    }

    // Draws the whole sky for the frame: the gradient + cloud/star textures (skybox), then the sun/moon
    // billboards over them. The atmosphere is resolved ONCE here with lighting forced on (the sky shows
    // regardless of the lighting toggle) and shared by both. Clouds/stars are exterior-only (suppressed,
    // not the gradient, in interiors); the sun/moon billboards are exterior-only too.
    private void RenderSky(Matrix4x4 viewProj)
    {
        EnsureSkyTexturesResolved();
        var atmo = AtmosphereState.Resolve(_gameHour, _selectedWeather, _currentClimateTiming, lightingEnabled: true);
        var daylight = atmo.SunIntensity; // 0 night → 1 day
        var exterior = _selectedInterior is null;

        // Clouds: grey/blue at night → near-white by day. Stars: cool white, fading in as the sun sets.
        var cloudTint = Vector3.Lerp(new Vector3(0.12f, 0.13f, 0.18f), new Vector3(1.0f, 0.98f, 0.95f), daylight);
        var starTint = new Vector3(0.85f, 0.90f, 1.0f);
        var starFade = Math.Clamp(1f - (daylight * 1.5f), 0f, 1f);
        _sky?.Render(viewProj,
            cloudTint, exterior ? 0.55f : 0f, _cloudTexIndex,
            starTint, exterior ? starFade : 0f, _starTexIndex);

        if (exterior)
        {
            RenderSkyBillboards(viewProj, atmo);
        }
    }

    // Draws the textured sun (disc + glare, day) + moon (night) billboards after the sky gradient. Sun
    // direction/intensity come from the decompile-grounded AtmosphereState (already resolved by the
    // caller with lighting forced on); the moon uses a plausible night arc (the engine's independent
    // orbit isn't tracked here — documented simplification).
    private void RenderSkyBillboards(Matrix4x4 viewProj, AtmosphereState.Resolved atmo)
    {
        if (_skyBillboards is null)
        {
            return;
        }

        // Camera world basis from the inverse view matrix (System.Numerics row-vector: invView's rows are
        // the camera's world-space right/up).
        if (!Matrix4x4.Invert(_camera.GetViewMatrix(), out var invView))
        {
            return;
        }

        var camRight = Vector3.Normalize(new Vector3(invView.M11, invView.M12, invView.M13));
        var camUp = Vector3.Normalize(new Vector3(invView.M21, invView.M22, invView.M23));
        var camPos = _camera.Position;

        var sunDir = atmo.SunWorldDirection;
        var sunFade = Math.Clamp(sunDir.Z * 6f, 0f, 1f);                    // soft fade through the horizon
        var sunTint = Vector3.Lerp(new Vector3(1.0f, 0.55f, 0.30f), new Vector3(1.0f, 0.97f, 0.92f),
            Math.Clamp(sunDir.Z * 2f, 0f, 1f));                            // warm at the horizon → near-white high

        // Moon arc: peaks at midnight, below the horizon by day; fades in as the sun sets.
        var nightAng = (_gameHour / 24f) * MathF.Tau;                       // 0 at midnight
        var moonElev = MathF.Cos(nightAng) * (MathF.PI / 2f * 0.7f);        // peak ≈ 63° up at midnight
        var moonAz = (MathF.PI * 0.5f) + (nightAng * 0.5f);
        var cosE = MathF.Cos(moonElev);
        var moonDir = Vector3.Normalize(new Vector3(MathF.Cos(moonAz) * cosE, MathF.Sin(moonAz) * cosE, MathF.Sin(moonElev)));
        var moonFade = Math.Clamp(1f - (atmo.SunIntensity * 1.4f), 0f, 1f);

        _skyBillboards.Render(viewProj, camPos, camRight, camUp,
            sunDir, sunFade, sunTint, _sunDiscTexIndex, _sunGlareTexIndex,
            moonDir, moonFade, _moonTexIndex);
    }

    /// <summary>Maps the weather dropdown's current selection to <see cref="_selectedWeather" /> (the
    /// "(Climate default)" item resolves to the current worldspace default, or null = placeholder).</summary>
    private void ApplyWeatherSelection()
    {
        _selectedWeather = WeatherComboBox?.SelectedItem is WeatherDropdownItem item
            ? item.IsClimateDefault ? _climateDefaultWeather : item.Weather
            : null;
    }

    /// <summary>Builds the weather dropdown once per load: a "(Climate default)" entry plus every
    /// weather in the file (by EditorId). Defaults the selection to "(Climate default)".</summary>
    private void PopulateWeatherDropdown()
    {
        if (WeatherComboBox is null || _data is null) return;

        var items = new List<WeatherDropdownItem> { new("(Climate default)", null, true) };
        foreach (var w in _data.AllWeathers)
        {
            items.Add(new WeatherDropdownItem(WeatherLabel(w), w, false));
        }

        _suppressWeatherSelectionEvent = true;
        WeatherComboBox.ItemsSource = items;
        WeatherComboBox.SelectedIndex = 0;
        _suppressWeatherSelectionEvent = false;
    }

    private void WeatherComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Null-guard _data — the handler can early-fire during XAML load before LoadData runs.
        if (_suppressWeatherSelectionEvent || _data is null) return;
        ApplyWeatherSelection();
    }

    private void TimeSlider_ValueChanged(
        object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // Continuous input — no _show flag; the next frame's BindAtmosphereConstants reads _gameHour.
        _gameHour = (float)e.NewValue;
    }

    private static string WeatherLabel(WeatherRecord w) =>
        string.IsNullOrEmpty(w.EditorId) ? $"0x{w.FormId:X8}" : $"{w.EditorId} (0x{w.FormId:X8})";

    /// <summary>One weather dropdown entry. The first item is the climate-default sentinel
    /// (<see cref="IsClimateDefault" /> true, <see cref="Weather" /> resolved per worldspace at
    /// selection time); the rest carry a concrete weather. <see cref="ToString" /> drives the display.</summary>
    private sealed record WeatherDropdownItem(string Label, WeatherRecord? Weather, bool IsClimateDefault)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    ///     Ensures the active worldspace matches <paramref name="worldspaceFormId" /> (null = the
    ///     unlinked-exterior set) so the top-down overlay renders the worldspace the 2D map is showing.
    ///     Switches + rebuilds the cell grid only when it differs (worldspace switches are infrequent).
    ///     Suppresses the selection event so the camera isn't reset. Returns false when no matching
    ///     exterior worldspace exists (the caller then skips the overlay).
    /// </summary>
    private bool EnsureActiveExteriorWorldspace(uint? worldspaceFormId)
    {
        if (_data is null) return false;

        // Already on the requested exterior worldspace (and not in interior mode)?
        if (_selectedInterior is null && GetSelectedWorldspaceFormId(_data) == worldspaceFormId)
        {
            return true;
        }

        var targetIndex = -1;
        if (worldspaceFormId is uint ws)
        {
            for (var i = 0; i < _data.Worldspaces.Count; i++)
            {
                if (_data.Worldspaces[i].FormId == ws) { targetIndex = i; break; }
            }
        }
        else if (_data.UnlinkedExteriorCells.Count > 0)
        {
            targetIndex = _data.Worldspaces.Count; // the unlinked-exterior tail entry
        }

        if (targetIndex < 0) return false;

        _selectedInterior = null; // switching to an exterior worldspace leaves interior mode
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.SelectedIndex = targetIndex;
        _suppressWorldspaceSelectionEvent = false;
        TryBuildCellGrid();
        RefreshAtmosphereForCurrentWorldspace();
        return true;
    }

    /// <summary>Profiler hook: the FormId of the currently-selected exterior worldspace (null when an
    /// unlinked-exterior / interior entry is selected). Lets the capture harness round-trip the
    /// worldspace-sync parameter without switching.</summary>
    internal uint? Profiler_SelectedWorldspaceFormId =>
        _data is null ? null : GetSelectedWorldspaceFormId(_data);

    /// <summary>Profiler hook: number of exterior worldspaces (for the capture harness to iterate).</summary>
    internal int Profiler_ExteriorWorldspaceCount => _data?.Worldspaces.Count ?? 0;

    /// <summary>Profiler hook: FormId + world-space centroid (north-Y) + name of exterior worldspace
    /// <paramref name="index" />, so the capture harness can target a specific worldspace and verify
    /// the top-down sync switches to it. Null when the index is out of range or has no placed cells.</summary>
    internal (uint FormId, float CenterX, float CenterY, string Name)? Profiler_GetWorldspaceCenter(int index)
    {
        if (_data is null || index < 0 || index >= _data.Worldspaces.Count) return null;
        var ws = _data.Worldspaces[index];
        long sumX = 0, sumY = 0, n = 0;
        foreach (var c in ws.Cells)
        {
            if (c.GridX is int gx && c.GridY is int gy) { sumX += gx; sumY += gy; n++; }
        }
        if (n == 0) return null;
        var cx = (float)((sumX / (double)n + 0.5) * WorldGridConstants.CellSize);
        var cy = (float)((sumY / (double)n + 0.5) * WorldGridConstants.CellSize);
        return (ws.FormId, cx, cy, ws.EditorId ?? $"0x{ws.FormId:X8}");
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

    private void UpdateHud(int visible, int total, int visibleWater, int visibleReferences, int visibleNavMesh)
    {
        if (!_hudHidden && StatusOverlay.Visibility != Visibility.Visible && HudPanel.Visibility != Visibility.Visible)
        {
            HudPanel.Visibility = Visibility.Visible;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastHudUpdateTimestamp != 0 &&
            Stopwatch.GetElapsedTime(_lastHudUpdateTimestamp, now).TotalMilliseconds < HudUpdateIntervalMilliseconds)
        {
            return;
        }
        _lastHudUpdateTimestamp = now;

        var mode = _controller.Mode == CameraMode.Walk ? "walk" : "fly";
        // Time-of-day readout (P4) — the slider drives _gameHour; show it as HH:MM.
        var hour = (int)_gameHour;
        var minute = (int)((_gameHour - hour) * 60f);
        // Backend chip — D3D12 shows the feature level for at-a-glance diagnostics.
        var backend = _gpu12 is not null
            ? $"D3D12 {_gpu12.FeatureLevel}"
            : "D3D11";
        // Layer on/off state now lives in the toolbar toggle buttons (3D-6), so the HUD spends the
        // freed space spelling out the movement controls (3D-10).
        var text =
            $"[{backend}]   " +
            $"Cells: {visible} / {total}   refs: {visibleReferences}   nav: {visibleNavMesh}   " +
            $"pos: ({_camera.Position.X:0}, {_camera.Position.Y:0}, {_camera.Position.Z:0})   " +
            $"speed: {_controller.MoveSpeed:0}   " +
            $"dist: {_renderDistance / WorldGridConstants.CellSize:0.#}c   " +
            $"mode: {mode}   time {hour:00}:{minute:00}\n" +
            "WASD move   Q/E up/down   mouse-wheel speed   drag to look   " +
            "PgUp/PgDn view distance   F fly/walk   click select (click again = cycle)   Esc deselect";

        // Draw-cap signal: when a dense frame can't fit every per-draw CB in the shared ring slot,
        // the renderer skips the overflow (instead of throwing + blanking the scene). Surface it so
        // the soft cap is visible — raise FALLOUT_VIEWER_RING_BUFFER_MB if this is persistently > 0.
        var truncatedDraws = _references?.LastFrameDrawsTruncated ?? 0;
        if (truncatedDraws > 0)
        {
            var ringTotalMib = _ringBuffer12 is not null ? _ringBuffer12.BytesPerFrame / (1024.0 * 1024.0) : 0;
            text += $"\n⚠ DRAW-CAP: {truncatedDraws} draws skipped this frame " +
                    $"(ring {ringTotalMib:0}MiB full — raise FALLOUT_VIEWER_RING_BUFFER_MB)";
        }
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
                    $"texPend:{rstats.ReferenceTexturePending} " +
                    $"cpuHit:{rstats.ReferenceCpuDecodedMeshCacheHits} bcTex:{rstats.ReferenceCompressedTextureUploads} rgbaTex:{rstats.ReferenceRgbaTextureUploads} " +
                    $"cull:{rstats.ReferenceCullMilliseconds:0.0} mesh:{rstats.ReferenceMeshUploadMilliseconds:0.0} " +
                    $"cb:{rstats.ReferenceCbUpdateMilliseconds:0.0} srv:{rstats.ReferenceSrvBindMilliseconds:0.0} " +
                    $"draw:{rstats.ReferenceDrawCallMilliseconds:0.0}ms";
            }
        }

        if (!string.Equals(_lastHudText, text, StringComparison.Ordinal))
        {
            _lastHudText = text;
            HudText.Text = text;
        }
        if (!_hudHidden && StatusOverlay.Visibility != Visibility.Visible)
        {
            HudPanel.Visibility = Visibility.Visible;
        }
    }

    private void HudToggleButton_Changed(object sender, RoutedEventArgs e)
    {
        _hudHidden = HudToggleButton.IsChecked != true;
        HudPanel.Visibility = !_hudHidden && StatusOverlay.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void MaybeLogProfile(
        FrameProfileSample sample,
        WorldRenderStats? terrain,
        WorldRenderStats? water,
        WorldRenderStats? wireframe,
        WorldRenderStats? references)
    {
        if (!_profileLogging)
        {
            return;
        }

        _profileAccumulator.Add(sample, terrain, water, wireframe, references);
        if (_profileAccumulator.TryFlush(_profileLogIntervalMilliseconds, out var message, out var aggregate))
        {
            var fields = new Dictionary<string, object?>(aggregate, StringComparer.Ordinal);
            foreach (var item in RendererProfilerTrace.CameraPoseFields(Profiler_CameraPose))
            {
                fields[item.Key] = item.Value;
            }

            // Resource gauges — the trajectory of these in the last log line(s) before a crash tells
            // which resource exhausted (the usual "crash with no cause" under sustained streaming):
            //   desc → bindless descriptor heap (texture-slot leak; capacity is the hard ceiling),
            //   vramMb used/budget → GPU memory (device removal once usage exceeds budget),
            //   managedMb → managed heap OOM (retained CPU buffers).
            // Appended to BOTH the JSONL event and the plain text log line so the GUI log alone suffices.
            var resourceGauge = "";
            if (_cbvSrvUavHeap12 is not null)
            {
                fields["persistentDescriptors"] = _cbvSrvUavHeap12.PersistentCount;
                fields["persistentDescriptorCapacity"] = _cbvSrvUavHeap12.PersistentCapacity;
                resourceGauge += string.Create(CultureInfo.InvariantCulture,
                    $" desc={_cbvSrvUavHeap12.PersistentCount}/{_cbvSrvUavHeap12.PersistentCapacity}");
            }

            var managedHeapMb = GC.GetTotalMemory(false) / (1024 * 1024);
            fields["managedHeapMb"] = managedHeapMb;
            resourceGauge += string.Create(CultureInfo.InvariantCulture, $" managedMb={managedHeapMb}");

            if (_gpu12 is not null && _gpu12.TryQueryLocalVideoMemoryMb(out var vramUsedMb, out var vramBudgetMb))
            {
                fields["vramUsedMb"] = vramUsedMb;
                fields["vramBudgetMb"] = vramBudgetMb;
                resourceGauge += string.Create(CultureInfo.InvariantCulture, $" vramMb={vramUsedMb}/{vramBudgetMb}");
            }

            Log.Info(resourceGauge.Length > 0 ? message + " | res:" + resourceGauge : message);
            RendererProfilerTrace.Event("frame-aggregate", fields);
        }
    }

    private void SetRenderDistance(float distance)
    {
        _renderDistance = Math.Clamp(distance, MinRenderDistance, MaxRenderDistance);
        _camera.FarPlane = _renderDistance;
    }

    /// <summary>Single point for the navmesh-layer toggle (keyboard key 6 + the toolbar
    /// checkbox both route here so field, checkbox, and render state stay in sync).</summary>
    private void SetShowNavMesh(bool on)
    {
        _showNavMesh = on;
        if (NavMeshCheckBox is not null && NavMeshCheckBox.IsChecked != on)
        {
            NavMeshCheckBox.IsChecked = on;
        }
    }

    /// <summary>Single point for the initially-disabled-objects toggle. Default off = disabled
    /// REFRs hidden, matching the 2D viewer. Applies straight to the live reference renderer
    /// (render-time filter — no cache rebuild).</summary>
    private void SetShowDisabled(bool on)
    {
        _showDisabled = on;
        if (_references is not null)
        {
            _references.ShowInitiallyDisabled = on;
        }
        if (DisabledCheckBox is not null && DisabledCheckBox.IsChecked != on)
        {
            DisabledCheckBox.IsChecked = on;
        }
    }

    /// <summary>Editor/engine-marker visibility toggle. Default off = markers hidden (matches the
    /// game). Applies straight to the live reference renderer (render-time filter, no rebuild).</summary>
    private void SetShowMarkers(bool on)
    {
        _showMarkers = on;
        if (_references is not null)
        {
            _references.ShowMarkers = on;
        }
        if (EditorMarkersCheckBox is not null && EditorMarkersCheckBox.IsChecked != on)
        {
            EditorMarkersCheckBox.IsChecked = on;
        }
    }

    /// <summary>Activator-category visibility toggle. Default off = activators hidden. Updates the
    /// per-category filter on the reference renderer (render-time, no cache rebuild).</summary>
    private void SetShowActivators(bool on)
    {
        if (on) _hiddenCategories.Remove(PlacedObjectCategory.Activator);
        else _hiddenCategories.Add(PlacedObjectCategory.Activator);
        _references?.SetHiddenCategories(_hiddenCategories);
        if (ActivatorsCheckBox is not null && ActivatorsCheckBox.IsChecked != on)
        {
            ActivatorsCheckBox.IsChecked = on;
        }
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
        HudPanel.Visibility = _hudHidden ? Visibility.Collapsed : Visibility.Visible;
        _lastHudUpdateTimestamp = 0;
    }

    // Profiler-driving surface --------------------------------------------------------------

    internal long Profiler_FrameIndex => _profileFrameIndex;

    internal RendererProfilerCameraPose Profiler_CameraPose =>
        new(_camera.Position, _camera.Yaw, _camera.Pitch, _renderDistance);

    internal WorldRenderStats? Profiler_TerrainStats => _terrain?.LastStats.Snapshot();
    internal WorldRenderStats? Profiler_ReferenceStats => _references?.LastStats.Snapshot();
    internal WorldRenderStats? Profiler_WaterStats => _water?.LastStats.Snapshot();
    internal WorldRenderStats? Profiler_WireframeStats => _cellGrid?.LastStats.Snapshot();

    internal void Profiler_SetCameraPose(RendererProfilerCameraPose pose)
    {
        _camera.Position = pose.Position;
        _camera.Yaw = pose.Yaw;
        _camera.Pitch = Math.Clamp(
            pose.Pitch,
            -MathF.PI * 0.5f + 0.01f,
            MathF.PI * 0.5f - 0.01f);
        SetRenderDistance(pose.RenderDistance);
    }

    internal void Profiler_ClearInputState()
    {
        _controller.ClearKeys();
        _toggleKeysDown.Clear();
        _mouseDragActive = false;
    }

    private static int ParsePositiveInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
               parsed > 0
            ? parsed
            : fallback;
    }

    private static double ParseNonNegativeDouble(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
               parsed >= 0
            ? parsed
            : fallback;
    }

    private static bool ShouldUpdateLayoutValue(double current, double next)
    {
        if (double.IsNaN(current) || double.IsInfinity(current))
        {
            return true;
        }

        return Math.Abs(current - next) >= 0.5d;
    }

    private long StartProfileTimestamp() => _profileLogging ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private readonly record struct FrameProfileSample(
        long FrameNumber,
        double TotalMilliseconds,
        double ControllerMilliseconds,
        double BeginFrameMilliseconds,
        double FenceWaitMilliseconds,
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
        uint RingBytes,
        int GcGen0Collections,
        int GcGen1Collections,
        int GcGen2Collections,
        long ManagedMemoryBytes);

    private sealed class FrameProfileAccumulator
    {
        private long _intervalStarted = Stopwatch.GetTimestamp();
        private int _frames;
        private long _lastFrameNumber;
        private double _frameTotal;
        private double _frameMax;
        private double _controller;
        private double _beginFrame;
        private double _fenceWait;
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
        private double _refTexturePending;
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
            _lastFrameNumber = sample.FrameNumber;
            _frameTotal += sample.TotalMilliseconds;
            _frameMax = Math.Max(_frameMax, sample.TotalMilliseconds);
            _controller += sample.ControllerMilliseconds;
            _beginFrame += sample.BeginFrameMilliseconds;
            _fenceWait += sample.FenceWaitMilliseconds;
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
                _refTexturePending += references.ReferenceTexturePending;
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

        internal bool TryFlush(
            int intervalMilliseconds,
            out string message,
            out IReadOnlyDictionary<string, object?> aggregate)
        {
            var elapsed = Stopwatch.GetElapsedTime(_intervalStarted).TotalMilliseconds;
            if (_frames == 0 || elapsed < intervalMilliseconds)
            {
                message = "";
                aggregate = new Dictionary<string, object?>();
                return false;
            }

            double Avg(double value) => value / _frames;
            aggregate = new Dictionary<string, object?>
            {
                ["lastFrame"] = _lastFrameNumber,
                ["frames"] = _frames,
                ["elapsedSeconds"] = elapsed / 1000.0,
                ["viewportWidth"] = _lastViewportWidth,
                ["viewportHeight"] = _lastViewportHeight,
                ["totalCells"] = _lastTotalCells,
                ["renderDistanceCells"] = _lastRenderDistanceCells,
                ["frameAvgMs"] = Avg(_frameTotal),
                ["frameMaxMs"] = _frameMax,
                ["controllerAvgMs"] = Avg(_controller),
                ["beginFrameAvgMs"] = Avg(_beginFrame),
                ["fenceWaitAvgMs"] = Avg(_fenceWait),
                ["acquireAvgMs"] = Avg(_acquire),
                ["clearAvgMs"] = Avg(_clearSetup),
                ["cameraAvgMs"] = Avg(_camera),
                ["terrainAvgMs"] = Avg(_terrainFrame),
                ["referencesAvgMs"] = Avg(_referencesFrame),
                ["waterAvgMs"] = Avg(_waterFrame),
                ["wireframeAvgMs"] = Avg(_wireframeFrame),
                ["endFrameAvgMs"] = Avg(_endFrame),
                ["presentAvgMs"] = Avg(_present),
                ["hudAvgMs"] = Avg(_hud),
                ["descriptorAvg"] = Avg(_descriptors),
                ["ringBytesAvg"] = Avg(_ringBytes),
                ["visibleTerrainAvg"] = Avg(_visibleTerrain),
                ["visibleReferencesAvg"] = Avg(_visibleReferences),
                ["visibleWaterAvg"] = Avg(_visibleWater),
                ["visibleWireframeAvg"] = Avg(_visibleWireframe),
                ["terrainCpuAvgMs"] = Avg(_terrainCpu),
                ["terrainLoopAvgMs"] = Avg(_terrainDrawLoop),
                ["terrainMeshUploadAvgMs"] = Avg(_terrainMeshUpload),
                ["refsCpuAvgMs"] = Avg(_refState + _refCull + _refMeshUpload + _refCb + _refSrvBind + _refDrawCall),
                ["refsCullAvgMs"] = Avg(_refCull),
                ["refsMeshUploadAvgMs"] = Avg(_refMeshUpload),
                ["refsCbAvgMs"] = Avg(_refCb),
                ["refsDrawAvgMs"] = Avg(_refDrawCall),
                ["refsTexturePendingAvg"] = Avg(_refTexturePending),
                ["refsDrawnAvg"] = Avg(_refDrawn),
                ["refsSubmeshAvg"] = Avg(_refSubmeshDraws),
                ["refsBatchesAvg"] = Avg(_refBatches),
                ["refsInstancesAvg"] = Avg(_refInstances)
            };
            // Apply invariant formatting per piece because concatenating interpolated strings
            // first would format each segment with the current UI culture.
                message =
                string.Create(CultureInfo.InvariantCulture, $"3D profile {_frames}f/{elapsed / 1000.0:0.0}s {_lastViewportWidth}x{_lastViewportHeight} cells={_lastTotalCells} dist={_lastRenderDistanceCells:0.#}c ") +
                string.Create(CultureInfo.InvariantCulture, $"frame avg/max={Avg(_frameTotal):0.00}/{_frameMax:0.00}ms ") +
                string.Create(CultureInfo.InvariantCulture, $"stages ctrl={Avg(_controller):0.00} begin={Avg(_beginFrame):0.00} fence={Avg(_fenceWait):0.00} acquire={Avg(_acquire):0.00} clear={Avg(_clearSetup):0.00} ") +
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
                string.Create(CultureInfo.InvariantCulture, $"miss={Avg(_refMeshMissing):0.0} texPend={Avg(_refTexturePending):0.0} drawn={Avg(_refDrawn):0.0} submesh={Avg(_refSubmeshDraws):0.0} ") +
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
            _lastFrameNumber = 0;
            _frameTotal = 0;
            _frameMax = 0;
            _controller = 0;
            _beginFrame = 0;
            _fenceWait = 0;
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
            _refTexturePending = 0;
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
