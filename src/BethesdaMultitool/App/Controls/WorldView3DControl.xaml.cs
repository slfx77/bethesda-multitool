using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using SharpGen.Runtime;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace BethesdaMultitool;

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

    // Render distance is authored in CELLS and converted to world units via the active worldspace's
    // cell size (4096 Fallout-family, 8192 Morrowind) so "16 cells of view" holds for every game.
    private const float DefaultRenderDistanceCells = 16f;
    private const float MinRenderDistanceCells = 4f;
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
    // The rendering backend is D3D12-only — the former D3D11 stack has been deleted. There is no
    // `_useD3D12` flag, `FALLOUT_VIEWER_D3D11` rollback, or dual-stack field set. With a single
    // backend the renderer fields are the concrete D3D12 types directly; the I*Renderer interfaces
    // remain (the renderers implement them) but add no indirection here.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.CellGridDebugRenderer12? _cellGrid;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.SelectionHighlightRenderer12? _selectionHighlight;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.TerrainRenderer12? _terrain;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.TerrainTextureResolver12? _textureResolver12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.WaterRenderer12? _water;
    // Real climate sky-dome NIF renderer — the exterior sky drawn from the game's own Sky\*.nif geometry
    // (atmosphere gradient + stars + clouds layers) on their authored UVs, replacing the procedural dome.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.SkyGeometryRenderer12? _skyGeometry;
    // Textured sky billboards (sun disc + glare, moon). Textures resolved per-climate via the terrain
    // texture cache; uint.MaxValue = SkyBillboardRenderer12.NoTexture (object skipped).
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.SkyBillboardRenderer12? _skyBillboards;
    private uint _sunDiscTexIndex = uint.MaxValue;
    private uint _sunGlareTexIndex = uint.MaxValue;
    private uint _moonTexIndex = uint.MaxValue;        // Masser full moon (or the single Fallout moon)
    private uint _moonSecundaTexIndex = uint.MaxValue; // Secunda full moon; NoTexture for single-moon games
    // Per-phase moon textures (Morrowind ships 8 per moon: new..full..waning). Index = MoonSky phase 0..7;
    // NoTexture entries (games with no per-phase moon art) fall back to the full-moon index above.
    private readonly uint[] _moonPhaseTexIndices = new uint[BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.MoonSky.PhaseCount];
    private readonly uint[] _moonSecundaPhaseTexIndices = new uint[BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.MoonSky.PhaseCount];
    // (climate FormId, active-weather FormId) the sky textures were resolved for (null = unresolved).
    // Clouds depend on the weather, so a weather change re-resolves even when the climate is unchanged.
    private (uint Climate, uint Weather)? _skyTexKey;
    private IReadOnlyList<SkyNifTexture>? _skyNifTextures; // cached CLMT MODL NIF harvest
    private string? _skyNifModlKey;                        // mesh path the harvest is for

    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.NavMeshRenderer12? _navMesh;
    // Debug overlay: per-ref walk-mode collision mesh (Havok bhk* where present, else visual fallback)
    // drawn as a green wireframe so the user can compare collision against the rendered meshes.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.CollisionDebugRenderer12? _collisionDebug;
    // Placed-object (REFR) rendering pipeline. Parallel to the terrain pipeline; owns separate
    // CPU NIF texture metadata and D3D12 GPU texture payload resolvers.
    private MeshArchiveSet? _meshArchives;
    private NifTextureResolver? _referenceTextureResolver;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.NifGpuTextureResolver? _referenceGpuTextureResolver12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTextureCache12? _referenceTextureCache12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceMeshCache12? _referenceMeshCache12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.ReferenceRenderer12? _references;
    private WorldViewData? _data;
    private DateTime _lastFrameTime;
    // Atmosphere: current game hour (0..24) feeding the shared b3 atmosphere CB. Defaults to noon and
    // driven by the time-of-day slider (TimeSlider_ValueChanged). The lighting/sky/water shaders read
    // the resolved atmosphere from this CB each frame.
    private float _gameHour = 12f;
    // Atmosphere: current day of the lunar cycle (0..24, one full Morrowind cycle), driven by the day
    // slider (LightingPanel_DayChanged). Feeds the moon phase (texture) + orbit position for the two-moon
    // sky; time-of-day alone can't separate Masser/Secunda or pick a phase. Only the 3D moon billboards
    // read it, so the 2D map hides the Day row (ShowDay=False).
    private float _gameDay;
    private bool _mouseDragActive;
    private Vector2 _previousPointerPosition;
    // Click-vs-drag tracking for object picking: a press that releases without moving past the
    // threshold is a click (ray-pick), anything more is a camera-look drag.
    private Vector2 _pointerPressPosition;
    private bool _pointerDragMoved;
    private const float ClickMoveThresholdPixels = 4f;
    // Right-button tracking — a clean (non-drag) right-click steps back through the selection history
    // (SelectPrevious); a right-drag is ignored, keeping that gesture free for future use. Separate
    // from the left-button drag state so the two never interfere.
    private bool _rightButtonActive;
    private Vector2 _rightPressPosition;
    private bool _rightDragMoved;
    private readonly List<global::BethesdaMultitool.WorldSpatialCell> _pickCellScratch = new();
    // Object-selection state. _selectedReference is the current selection (null = none); the pick
    // collects all ray hits into _pickHitScratch (sorted by distance) so a repeat click on the same
    // stack cycles to the next object behind the current one (GECK click-through).
    private PlacedReference? _selectedReference;
    private readonly List<PickHit> _pickHitScratch = new();
    // Broadphase-sphere hits whose tight OBND test missed — used only when no OBND hit lies under the
    // ray, so meshes that overspill a too-tight base-record OBND (SpeedTree canopies) stay clickable.
    private readonly List<PickHit> _pickSphereFallbackScratch = new();
    // Back-navigation history: each forward selection (click pick, E-in-walk pick, programmatic
    // SelectObject) pushes the prior selection here; right-click / Q-in-walk pops it (SelectPrevious).
    // Bounded so a long session can't grow it unboundedly; cleared with the scene in ClearSelection3D.
    private const int SelectionHistoryMax = 32;
    private readonly List<PlacedReference> _selectionHistory = new();
    private bool _renderLoopAttached;
    // D3D12-only backend. The renderer interfaces (`I*Renderer`) plug
    // straight into the D3D12 concrete impls; no D3D11 fallback fields remain.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDevice12? _gpu12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuSwapChainSurface12? _surface12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12? _commandRecorder12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRingBuffer12? _ringBuffer12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12? _cbvSrvUavHeap12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12? _rootSignature12;
    private GpuTimestampProfiler12? _gpuTimestampProfiler12;
    // Deferred-release queue for D3D12 resources evicted from LRU caches. D3D12
    // doesn't auto-track GPU vs CPU lifetime; disposing a vertex/texture while the GPU is
    // still reading it crashes the process. Resources go here instead of being disposed
    // immediately, and are released N frames later.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDeletionQueue12? _deletionQueue12;
    // Scene-depth SRV feeding the water depth-fade. The depth resource is R32_TYPELESS; we create an
    // R32_FLOAT SRV over it in the shared bindless heap so water.frag samples the real water-column
    // depth (FNV WATER000's DepthMap path) instead of a view-angle proxy. Allocated once and the SRV
    // recreated in place on resize (the depth resource changes identity). NoDepthSrv => proxy.
    private const uint NoDepthSrv = 0xFFFFFFFF;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12.PersistentAllocation? _depthSrv;
    private bool _suppressWorldspaceSelectionEvent;
    // Atmosphere weather/time. _selectedWeather null = the placeholder palette (no NAM0 colors);
    // _climateDefaultWeather is the current worldspace climate's default (the "(Climate default)"
    // dropdown item); _currentClimateTiming drives the sun curve + the NAM0 time-band blend.
    private WeatherRecord? _selectedWeather;
    private WeatherRecord? _climateDefaultWeather;
    private AtmosphereState.ClimateTiming _currentClimateTiming = AtmosphereState.ClimateTiming.Default;
    // Interiors are browsed via the shared CellListControl (searchable/filterable grouped list —
    // appropriate for the hundreds of interiors a dropdown can't handle), NOT the worldspace combo.
    // _selectedInterior is the interior currently loaded into the 3D scene (null = exterior mode); the
    // browser source + sort live inside the CellListControl (CellList).
    private CellRecord? _selectedInterior;
    // Set by NavigateToCell when a cross-worldspace jump is requested: the async
    // WorldspaceComboBox_SelectionChanged handler frames on this cell instead of the worldspace
    // centroid once the grid rebuild completes (avoids a yield race that would clobber the camera).
    private CellRecord? _pendingNavigateCell;
    // Warp pose (camera position + yaw) to apply once _pendingNavigateCell's worldspace finishes
    // loading — set by NavigateToCell for an Enter-key door warp into a not-yet-shown exterior.
    private (Vector3 pos, float yaw)? _pendingNavigateWarpPose;
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
    // D5 toggles placed-object (REFR) rendering.
    // The cell-boundary grid is a debug overlay → default OFF (must match CellsCheckBox.IsChecked in XAML).
    // True only during InitializeComponent. The toolbar toggles set IsChecked in XAML, which fires
    // their Checked/Unchecked handlers DURING load (now that they live in flyouts that realize early);
    // the handlers read x:Name fields / touch renderers that aren't ready yet, so they no-op until the
    // constructor clears this. Backing _show* fields already default to the XAML-checked values.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
        "S3604:Member initializer values should not be redundant",
        Justification = "InitializeComponent() fires the toggle handlers during construction; they read " +
                        "_initializing before the constructor sets it false, so the initializer is load-bearing.")]
    private bool _initializing = true;
    private bool _showWireframe;
    private bool _showTerrain = true;
    private bool _showWater = true;
    private bool _showTerrainTextures = true;
    private bool _showVertexColors = true;
    private bool _showReferences = true;
    private bool _showNavMesh;
    private bool _showCollision; // Havok collision-cage debug overlay (Visibility menu; off by default)
    private bool _showDisabled;
    // Engine/editor markers (XMarker, map/travel/teleport markers). Hidden by default to match the
    // game; the env knob seeds the initial value, the toolbar toggle flips it at runtime (persists
    // across ESM reloads like _showDisabled).
    private bool _showMarkers = BethesdaMultitool.Core.EnvironmentVariables.IsEnabled(
        BethesdaMultitool.Core.EnvironmentVariables.Viewer.ShowMarkers);
    // Placed-object categories hidden in the 3D view. Activators and Sky meshes start HIDDEN (user
    // feedback: activator trigger volumes/markers clutter the scene; sky/glow props like DiamondCityGlow
    // are atmosphere-only) — each must match its Visibility-submenu checkbox's initial IsChecked="False".
    // Applied to the reference renderer on pipeline init and on every toggle. Also drives the 2D top-down
    // overlay via _references.SetHiddenCategories.
    private readonly HashSet<PlacedObjectCategory> _hiddenCategories =
        [PlacedObjectCategory.Activator, PlacedObjectCategory.Sky];
    // Atmosphere lighting — directional sun + ambient via the shared b3 CB. On by default; when
    // off, the shaders fall back to the legacy flat shade (pixel-identical to the pre-atmosphere look).
    private bool _showLighting = true;
    // Skybox — horizon→top gradient + sun disc drawn first, depth off. On by default; off ⇒ the
    // flat dark-blue clear shows. Also gates whether water mirrors the atmosphere sky vs its DNAM color.
    private bool _showSky = true;
    // Distance fog (weather-driven) — terrain/reference/water recede toward the WTHR fog color over the
    // FNAM near→far range. On by default; off ⇒ no fog (so distant cells stay crisp for inspection).
    private bool _showFog = true;

    // Active worldspace's cell-edge size in world units (4096 Fallout-family, 8192 Morrowind). Set from
    // WorldViewData.CellWorldSize on every cell-grid build; drives camera framing, picking, render
    // distance, and the cull cylinder so the 3D viewer matches the geometry's absolute coordinates.
    private float _cellSize = WorldGridConstants.CellSize;
    private float _renderDistance = DefaultRenderDistanceCells * WorldGridConstants.CellSize;

    // Top-down overlay rendering for the 2D map (ITopDownSceneRenderer).
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

    public WorldView3DControl()
    {
        InitializeComponent();
        _initializing = false; // XAML load done — toolbar toggle handlers may now run for real.
        // The interior browser is the shared CellListControl; in 3D a cell loads only on a real click.
        CellList.Activation = CellListControl.ActivationMode.ItemClick;
        CellList.CellActivated += CellList_CellActivated;
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

    /// <summary>Raised when the user requests inspection of a placed object (opens its detail panel).</summary>
    public event EventHandler<PlacedReference>? InspectObject;

    /// <summary>Raised when the user requests inspection of a cell.</summary>
    public event EventHandler<CellRecord>? InspectCell;

    internal void LoadData(WorldViewData data)
    {
        _data = data;
        // Make the base-object category available to the placement bake before the renderer pulls
        // its first cell — drives the per-category visibility filter (activators off by default).
        data.RenderCache.CategoryIndex = data.CategoryIndex;
        // Same timing for MODS alternate textures: the placement bake reads this to give each
        // re-skinned base (e.g. billboards) its own textured mesh variant.
        data.RenderCache.AlternateTextureIndex = data.AlternateTexturesByFormId;
        // And FO4/FO76 MSWP material swaps (REFR XMSP): merged into a placement's alternate-texture
        // set at bake time so the swapped placement decodes as its own mesh variant.
        data.RenderCache.MaterialSwapIndex = data.MaterialSwapsByFormId;
        // Plus the base-record default swaps (FO4-family MODS = bare MSWP FormID) for placements
        // that carry no XMSP of their own.
        data.RenderCache.BaseMaterialSwapIndex = data.BaseMaterialSwapsByFormId;
        // And the base-record MODC color remaps (FO4-family gradient-palette row overrides —
        // shipping-crate colorways).
        data.RenderCache.BaseColorRemapIndex = data.BaseColorRemapsByFormId;
        if (data.BaseColorRemapsByFormId.Count > 0)
        {
            Log.Info("WorldView3DControl: {0} base-record MODC color remaps.", data.BaseColorRemapsByFormId.Count);
        }
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
        // The MODL-NIF harvest is keyed by mesh path; drop it too so a new game can't reuse a stale one.
        _skyTexKey = null;
        _skyNifTextures = null;
        _skyNifModlKey = null;
        _sunDiscTexIndex = _sunGlareTexIndex = _moonTexIndex = _moonSecundaTexIndex = uint.MaxValue;
        Array.Fill(_moonPhaseTexIndices, uint.MaxValue);
        Array.Fill(_moonSecundaPhaseTexIndices, uint.MaxValue);

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

                _textureResolver12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.TerrainTextureResolver12(
                    _gpu12, _commandRecorder12!, _cbvSrvUavHeap12!, _deletionQueue12!,
                    _data.LandTexturesByFormId, _data.TextureSetsByFormId, bsas, _data.Game);
                var terrain12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12.TerrainRenderer12(
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

        // Weather dropdown: "(Climate default)" + all weathers. Built once here; the worldspace
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

        _ = InspectObject; // referenced so the event isn't flagged unused on paths that never raise it
        _ = InspectCell;
    }

    /// <summary>Sets (or clears, with null) the current 3D selection and its outline programmatically.</summary>
    internal void SelectObject(PlacedReference? obj)
    {
        PushSelection(obj);
        _pickHitScratch.Clear();
        UpdateHighlightFromSelection();
    }
}
