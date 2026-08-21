using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
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
using BethesdaMultitool.Core.WorldData;

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
    // Render distance is authored in CELLS and converted to world units via the active worldspace's
    // cell size (4096 Fallout-family, 8192 Morrowind) so "16 cells of view" holds for every game.
    private const float DefaultRenderDistanceCells = 16f;
    private const float MinRenderDistanceCells = 4f;
    private const float MaxRenderDistance = 800_000f;
    private const float RenderDistanceStep = 1.25f;
    private const double HudUpdateIntervalMilliseconds = 250d;

    private const float ClickMoveThresholdPixels = 4f;

    // Back-navigation history: each forward selection (click pick, E-in-walk pick, programmatic
    // SelectObject) pushes the prior selection here; right-click / Q-in-walk pops it (SelectPrevious).
    // Bounded so a long session can't grow it unboundedly; cleared with the scene in ClearSelection3D.
    private const int SelectionHistoryMax = 32;

    // Scene-depth SRV feeding single-sample water depth-fade and eligible blended effects. The
    // R32_TYPELESS resource receives an R32_FLOAT Texture2D/Texture2DMS view in the shared bindless
    // heap; the per-draw soft-effect binding selects the matching HLSL alias. Allocated once and
    // recreated in place on resize (the depth resource changes identity). NoDepthSrv => no sampling.
    private const uint NoDepthSrv = 0xFFFFFFFF;

    // Top-down overlay rendering for the 2D map (ITopDownSceneRenderer).
    // Render at 2× the requested size then box-downsample for AA — the live terrain/reference PSOs
    // are 1-sample, so MSAA isn't an option without parallel PSOs. Final long-edge is capped so a
    // large canvas doesn't blow up the per-request readback.
    private const int TopDownSupersample = 2;
    private const int MaxTopDownFinalDimension = 2048;

    /// <summary>
    ///     Smallest projected cell size (screen px per world cell) at which the top-down overlay still
    ///     runs its terrain DEPTH pre-pass. Below this the view is a whole-worldspace overview where a
    ///     cell is a handful of pixels, the pre-pass would admit every cell of the worldspace for a
    ///     full-detail build, and the overlay could never reach streaming quiescence — see the gate in
    ///     <c>WorldView3DControl.TopDown.cs</c> for the measurement. Matches the 2D map's own
    ///     <c>TerrainAggregateScreenPxThreshold</c> so both switch to overview behaviour together.
    /// </summary>
    private const float TopDownTerrainDepthMinPixelsPerCell = 24f;

    private static readonly Logger Log = Logger.Instance;

    private readonly CameraState _camera = new();

    private readonly FlythroughCameraController _controller;

    // Current-hour states for script-driven day/night refs (street lights, glow FX, fire barrels).
    // Re-evaluated from WorldViewData.DayNightSchedule as the game hour advances; its Version keys
    // the renderer's cull/shadow caches so networks flip exactly at their scripted hours.
    private readonly DayNightRefStateStore _dayNightStates = new();

    private readonly bool _forceGpuTimestamps =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.GpuTimestamps);

    // Placed-object categories hidden in the 3D view. Sky/glow props (DiamondCityGlow) start HIDDEN
    // (atmosphere-only meshes); Activators start VISIBLE (model-bearing
    // activators — the Anvil lighthouse fire bowl, flora — are real scenery and must render by
    // default; trigger-volume/marker ACTIs are already dropped by the editor-marker skips). Each
    // category must match its Visibility-submenu checkbox's initial IsChecked. Applied to the
    // reference renderer on pipeline init and on every toggle. Also drives the 2D top-down overlay
    // via _references.SetHiddenCategories.
    private readonly HashSet<PlacedObjectCategory> _hiddenCategories =
        [PlacedObjectCategory.Sky];

    // Per-phase moon textures (Morrowind ships 8 per moon: new..full..waning). Index = MoonSky phase 0..7;
    // NoTexture entries (games with no per-phase moon art) fall back to the full-moon index above.
    private readonly uint[] _moonPhaseTexIndices =
        new uint[BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere.MoonSky.PhaseCount];

    private readonly uint[] _moonSecundaPhaseTexIndices =
        new uint[BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere.MoonSky.PhaseCount];

    private readonly List<global::BethesdaMultitool.Core.WorldData.WorldSpatialCell> _pickCellScratch = new();

    private readonly List<PickHit> _pickHitScratch = new();

    // Broadphase-sphere hits whose tight OBND test missed — used only when no OBND hit lies under the
    // ray, so meshes that overspill a too-tight base-record OBND (SpeedTree canopies) stay clickable.
    private readonly List<PickHit> _pickSphereFallbackScratch = new();
    private readonly Vector2[] _pointerStartPosition = new Vector2[1];
    private readonly FrameProfileAccumulator _profileAccumulator = new();

    private readonly bool _profileLogging =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ProfileLog);

    private readonly int _profileLogIntervalMilliseconds =
        ParsePositiveInt(EnvironmentVariables.Get(EnvironmentVariables.Viewer.ProfileIntervalMilliseconds), 2000);

    // Per-placement ACTI/REFR Enabled preview overrides. This table is scene-local and keyed by the
    // placed FormID, so changing one selected instance never mutates its parsed/base record or siblings.
    private readonly ReferenceEnabledOverrideStore _referenceEnabledOverrides = new();
    private readonly List<PlacedReference> _selectionHistory = new();

    private readonly bool _showFrameStats =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.FrameStats);

    private readonly double _stallThresholdMilliseconds =
        ParseNonNegativeDouble(EnvironmentVariables.Get(EnvironmentVariables.Viewer.StallThresholdMilliseconds), 0);

    private readonly string? _stressScene =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.StressScene);

    private readonly HashSet<VirtualKey> _toggleKeysDown = [];

    // NIF animation playback (banner sway, waterfall/lava UV scroll). On by default; the toolbar
    // toggle flips it live (persists across ESM reloads like _showMarkers). The
    // FALLOUT_VIEWER_NIF_ANIMATION=0 kill-switch gates DECODE upstream; this only pauses playback.
    private bool _animationsEnabled = true;
    private bool _bloomEnabled = true;
    private uint? _boundWaterAppearanceFormId;
    private bool _canopyShadowsEnabled = true;

    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12? _cbvSrvUavHeap12;

    // The rendering backend is single-backend D3D12 by design — renderer fields are the concrete
    // D3D12 types directly; the I*Renderer interfaces remain (the renderers implement them) but
    // add no indirection here, and no backend-selection abstraction should be reintroduced.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.CellGridDebugRenderer12? _cellGrid;
    private Dictionary<(int gx, int gy), CellRecord>? _cellGridLookup;

    // Active worldspace's cell-edge size in world units (4096 Fallout-family, 8192 Morrowind). Set from
    // WorldViewData.CellWorldSize on every cell-grid build; drives camera framing, picking, render
    // distance, and the cull cylinder so the 3D viewer matches the geometry's absolute coordinates.
    private float _cellSize = WorldGridConstants.CellSize;

    private WeatherRecord? _climateDefaultWeather;

    // Debug overlay: per-ref walk-mode collision mesh (Havok bhk* where present, else visual fallback)
    // drawn as a green wireframe so the user can compare collision against the rendered meshes.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.CollisionDebugRenderer12? _collisionDebug;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuCommandRecorder12? _commandRecorder12;
    private AtmosphereState.ClimateTiming _currentClimateTiming = AtmosphereState.ClimateTiming.Default;

    private WorldViewData? _data;

    // Deferred-release queue for D3D12 resources evicted from LRU caches. D3D12
    // doesn't auto-track GPU vs CPU lifetime; disposing a vertex/texture while the GPU is
    // still reading it crashes the process. Resources go here instead of being disposed
    // immediately, and are released N frames later.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDeletionQueue12? _deletionQueue12;

    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12.PersistentAllocation?
        _depthSrv;

    // Export-tab framing preview: the captured world bounds + a view-direction gizmo, drawn as a
    // wireframe overlay when the Export tab is up and its "Show framing" toggle is on.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ExportFramingOverlay? _exportFraming;

    // FormID-heatmap debug overlay (Overlays menu; off by default, must match
    // FormIdHeatmapCheckBox.IsChecked). Tints placed refs blue→red by FormID so authoring order
    // reads at a glance. The range is stored in CELLS (∞ = the slider's unlimited top stop, the
    // default) and persists across ESM reloads like _showMarkers; the renderer receives world
    // units (cells × _cellSize) via the Toolbar funnels + Pipeline seeding.
    private bool _formIdHeatmap;

    private float _formIdHeatmapRangeCells = float.PositiveInfinity;

    // Atmosphere: current day of the lunar cycle (0..24, one full Morrowind cycle), driven by the day
    // slider (LightingPanel_DayChanged). Feeds the moon phase (texture) + orbit position for the two-moon
    // sky; time-of-day alone can't separate Masser/Secunda or pick a phase. Only the 3D moon billboards
    // read it, so the 2D map hides the Day row (ShowDay=False).
    private float _gameDay;

    // Atmosphere: current game hour (0..24) feeding the shared b3 atmosphere CB. Defaults to noon and
    // driven by the time-of-day slider (TimeSlider_ValueChanged). The lighting/sky/water shaders read
    // the resolved atmosphere from this CB each frame.
    private float _gameHour = 12f;

    // D3D12-only backend. The renderer interfaces (`I*Renderer`) plug
    // straight into the D3D12 concrete impls; no D3D11 fallback fields remain.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDevice12? _gpu12;

    /// <summary>
    ///     Set when backend init failed AFTER the device was successfully created (root
    ///     signature, shader compile, PSO build). The failure handler disposes and nulls
    ///     <see cref="_gpu12" />, so this is the only surviving evidence of how far init got —
    ///     without it every failure reads as "no D3D12 device", which is what masked the
    ///     Windows 10 root-signature blocker.
    /// </summary>
    private bool _backendFailedAfterDeviceCreation;

    private GpuTimestampProfiler12? _gpuTimestampProfiler12;
    private bool _gpuTimestampsAutoEnabled;
    private bool _grassShadowsEnabled = true;

    private bool _hasBoundWaterAppearance;

    // User toggled the diagnostics overlay off (HudToggleButton). UpdateHud/HideStatus honor this
    // so a HUD refresh or a status-overlay dismissal doesn't force the panel back on.
    private bool _hudHidden;
    private uint _imagespaceExplicitFormId;
    private ImagespaceSelectionMode _imagespaceMode = ImagespaceSelectionMode.Automatic;

    // Layer visibility — toggled by D1/D2/D3 keys. D4 toggles textured-vs-VCLR-only.
    // D5 toggles placed-object (REFR) rendering.
    // The cell-boundary grid is a debug overlay → default OFF (must match CellsCheckBox.IsChecked in
    // the settings-panel XAML — every _show* initializer must mirror its panel control's default).
    // True during construction: WireSettingsPanel subscribes the settings-panel controls while this
    // is still set, so any event that fires before the renderers exist no-ops.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
        "S3604:Member initializer values should not be redundant",
        Justification = "InitializeComponent() fires the toggle handlers during construction; they read " +
                        "_initializing before the constructor sets it false, so the initializer is load-bearing.")]
    private bool _initializing = true;

    private double _lastControllerUpdateMilliseconds;
    private DateTime _lastFrameTime;
    private int _lastGcGen0Collections = GC.CollectionCount(0);
    private int _lastGcGen1Collections = GC.CollectionCount(1);
    private int _lastGcGen2Collections = GC.CollectionCount(2);
    private string? _lastHudText;

    private long _lastHudUpdateTimestamp;

    // Process-wide monotonic allocation counter. Unlike live-heap size, its per-frame delta exposes
    // transient particle/controller churn even when a collection immediately reclaims the objects.
    private long _lastTotalAllocatedBytes = GC.GetTotalAllocatedBytes(false);

    // Placed-object (REFR) rendering pipeline. Parallel to the terrain pipeline; owns separate
    // CPU NIF texture metadata and D3D12 GPU texture payload resolvers.
    private MeshArchiveSet? _meshArchives;
    private uint _moonSecundaTexIndex = uint.MaxValue; // Secunda full moon; NoTexture for single-moon games
    private uint _moonTexIndex = uint.MaxValue; // Masser full moon (or the single Fallout moon)
    private bool _mouseDragActive;

    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.NavMeshRenderer12? _navMesh;

    /// <summary>
    ///     SRV over the surface's 1-sample post-opaque depth snapshot — the unified
    ///     transparency stream's depth source (live depth stays writable through the stream).
    /// </summary>
    private GpuDescriptorHeapAllocator12.PersistentAllocation? _opaqueDepthSnapshotSrv;

    private Vortice.Direct3D12.ID3D12Resource? _opaqueDepthSnapshotSrvResource;

    // Set by NavigateToCell when a cross-worldspace jump is requested: the async
    // WorldspaceComboBox_SelectionChanged handler frames on this cell instead of the worldspace
    // centroid once the grid rebuild completes (avoids a yield race that would clobber the camera).
    private CellRecord? _pendingNavigateCell;

    // Warp pose (camera position + yaw) to apply once _pendingNavigateCell's worldspace finishes
    // loading — set by NavigateToCell for an Enter-key door warp into a not-yet-shown exterior.
    private (Vector3 pos, float yaw)? _pendingNavigateWarpPose;

    // Placed LIGH point emitters uploaded once per frame. Interiors are on by
    // default; exteriors stay behind FALLOUT_VIEWER_EXTERIOR_LIGHTS until the forward loop is
    // profiled in dense worldspaces. The UI toggle is an additional live AND-gate.
    private bool _placedLightsEnabled = true;

    private bool _pointerDragMoved;

    // Click-vs-drag tracking for object picking: a press that releases without moving past the
    // threshold is a click (ray-pick), anything more is a camera-look drag.
    private Vector2 _pointerPressPosition;
    private Vector2 _previousPointerPosition;
    private long _profileFrameIndex;

    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.NifGpuTextureResolver?
        _referenceGpuTextureResolver12;

    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceMeshCache12? _referenceMeshCache12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRenderer12? _references;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTextureCache12? _referenceTextureCache12;

    private NifTextureResolver? _referenceTextureResolver;

    // Pre-load default only — LoadCellGrid re-seeds this via SetRenderDistance once _cellSize is known,
    // so a non-4096 game (Starfield's cell is 100) never renders against this value.
    private float _renderDistance = DefaultRenderDistanceCells * WorldGridConstants.CellSize;

    private bool _renderLoopAttached;

    // Right-button tracking — a clean (non-drag) right-click steps back through the selection history
    // (SelectPrevious); a right-drag is ignored, keeping that gesture free for future use. Separate
    // from the left-button drag state so the two never interfere.
    private bool _rightButtonActive;
    private bool _rightDragMoved;
    private Vector2 _rightPressPosition;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRingBuffer12? _ringBuffer12;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuRootSignature12? _rootSignature12;

    private int _sceneReadyGeneration = -1;

    // Scene-selection generation used by the headless profiler/capture harness. ComboBox selection is
    // async-void and deliberately yields before rebuilding the world, so a stale handler must not be able
    // to overwrite a newer interior/worldspace selection or advertise the old spatial index as ready.
    private int _sceneSelectionGeneration;

    // Interiors are browsed via the shared CellListControl (searchable/filterable grouped list —
    // appropriate for the hundreds of interiors a dropdown can't handle), NOT the worldspace combo.
    // _selectedInterior is the interior currently loaded into the 3D scene (null = exterior mode); the
    // browser source + sort live inside the CellListControl (CellList).
    private CellRecord? _selectedInterior;

    // Object-selection state. _selectedReference is the current selection (null = none); the pick
    // collects all ray hits into _pickHitScratch (sorted by distance) so a repeat click on the same
    // stack cycles to the next object behind the current one (GECK click-through).
    private PlacedReference? _selectedReference;

    // Atmosphere weather/time. _selectedWeather null = the placeholder palette (no NAM0 colors);
    // _climateDefaultWeather is the current worldspace climate's default (the "(Climate default)"
    // dropdown item); _currentClimateTiming drives the sun curve + the NAM0 time-band blend.
    private WeatherRecord? _selectedWeather;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.SelectionHighlightRenderer12? _selectionHighlight;
    private bool _showCollision; // Havok collision-cage debug overlay (Visibility menu; off by default)

    private bool _showDisabled;

    // Distance fog (weather-driven) — terrain/reference/water recede toward the WTHR fog color over the
    // FNAM near→far range. On by default; off ⇒ no fog (so distant cells stay crisp for inspection).
    private bool _showFog = true;

    private bool _showGrass = true;

    // Atmosphere lighting — directional sun + ambient via the shared b3 CB. On by default; when
    // off, the shaders fall back to the legacy flat shade (pixel-identical to the pre-atmosphere look).
    private bool _showLighting = true;

    // Engine/editor markers (XMarker, map/travel/teleport markers). Hidden by default to match the
    // game; the env knob seeds the initial value, the toolbar toggle flips it at runtime (persists
    // across ESM reloads like _showDisabled).
    private bool _showMarkers = BethesdaMultitool.Core.EnvironmentVariables.IsEnabled(
        BethesdaMultitool.Core.EnvironmentVariables.Viewer.ShowMarkers);

    // Navigation overlay (NAVM navmeshes / TES4 PGRD pathgrids). Off by default; the env knob seeds
    // the initial value (headless captures), the toolbar checkbox / key 6 flip it at runtime.
    private bool _showNavMesh = BethesdaMultitool.Core.EnvironmentVariables.IsEnabled(
        BethesdaMultitool.Core.EnvironmentVariables.Viewer.ShowNavMesh);

    private bool _showReferences = true;

    // Sun shadows — the directional shadow map pass (reference batches cast onto terrain +
    // references). On by default when lighting is on; off ⇒ pixel-identical to the pre-shadow
    // renderer. Env kill-switch FALLOUT_VIEWER_SHADOWS=0 overrides the toggle.
    private bool _showShadows = true;

    // Skybox — horizon→top gradient + sun disc drawn first, depth off. On by default; off ⇒ the
    // flat dark-blue clear shows. Also gates whether water mirrors the atmosphere sky vs its DNAM color.
    private bool _showSky = true;
    private bool _showTerrain = true;
    private bool _showTerrainTextures = true;
    private bool _showVertexColors = true;
    private bool _showWater = true;

    private bool _showWireframe;

    // Textured sky billboards (sun disc + glare, moon). Textures resolved per-climate via the terrain
    // texture cache; uint.MaxValue = SkyBillboardRenderer12.NoTexture (object skipped).
    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.SkyBillboardRenderer12? _skyBillboards;

    // Real climate sky-dome NIF renderer — the exterior sky drawn from the game's own Sky\*.nif geometry
    // (atmosphere gradient + stars + clouds layers) on their authored UVs, replacing the procedural dome.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.SkyGeometryRenderer12? _skyGeometry;
    private string? _skyNifModlKey; // mesh path the harvest is for

    private IReadOnlyList<SkyNifTexture>? _skyNifTextures; // cached CLMT MODL NIF harvest

    // (climate, current-weather, outgoing-weather FormIds) the sky topology was resolved for. The
    // continuously changing weather percentage updates retained layer state without recooking the NIFs.
    private (uint Climate, uint Weather, uint OutgoingWeather)? _skyTexKey;
    private WorldSpatialIndex? _spatialIndex;
    private bool _stressBookmarkApplied;
    private uint _sunDiscTexIndex = uint.MaxValue;
    private uint _sunGlareTexIndex = uint.MaxValue;
    private bool _suppressWorldspaceSelectionEvent;

    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuSwapChainSurface12? _surface12;

    // Guards the draw-distance slider↔SetRenderDistance round trip (funnel reseats the slider).
    private bool _syncingDrawDistance;

    // Same guard for the FormID-heatmap range slider↔SetFormIdHeatmapRange round trip.
    private bool _syncingHeatmapRange;

    // Same guard for the Screen-effects radio↔SetTonemapGuiMode round trip.
    private bool _syncingTonemapRadio;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.TerrainRenderer12? _terrain;

    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.TerrainTextureResolver12? _textureResolver12;

    // Post-processing: the Video expander's retail three-way "Screen effects" radio
    // (None/Bloom/HDR — the classic launcher's option; there is no retail HDR-with-bloom-off
    // state). Read per-frame by ResolveTonemapSettings, so flipping it is free (no pipeline
    // rebuild): HDR = each family's engine operator; Bloom = SDR + classic bloom stand-in
    // (ClassicSdrBloom); None = SDR (FO3/FNV keep their standalone cinematic grade). The float
    // scene target itself stays; only the static FALLOUT_VIEWER_HDR=0 env kill-switch reverts
    // it. _bloomEnabled survives as a NON-UI diagnostic AND-gate (profiler scenarios and
    // FALLOUT_VIEWER_BLOOM A/B bloom independently under HDR). Imagespace is a three-way
    // selection: Automatic follows the cell/worldspace resolution, None renders a neutral
    // cinematic while eye-adapt exposure stays only when an HDR operator is active; Explicit
    // forces one IMGS record by FormID.
    private Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapGuiMode _tonemapGuiMode =
        Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapGuiMode.Hdr;

    // Semantic eye-adaptation reset epoch. Retail FNV preserves history across routine CELL/IMGS/WTHR
    // transitions and clears only on an explicit ClearAdaptedLight request. Loading a new viewer scene
    // is our equivalent explicit lifecycle boundary; target/device resets remain owned by the GPU pass.
    private ulong _tonemapHistoryClearGeneration;

    /// <summary>
    ///     In-flight top-down readback Task.Run body. Tracked so DisposeRenderResources can drain
    ///     it before tearing down the D3D12 device — the body waits on the frame fence and maps a
    ///     readback buffer, both of which would race a concurrent device dispose.
    /// </summary>
    private Task? _topDownReadbackTask;

    /// <summary>
    ///     Reused across top-down overlay requests to avoid per-request allocation of two
    ///     committed textures + RTV/DSV heaps + a readback buffer. Recreated only when the supersampled
    ///     dimensions change; disposed in <see cref="DisposeRenderResources" />.
    /// </summary>
    private GpuOffscreenSceneTarget12? _topDownTarget;

    private int _topDownTargetH;

    private int _topDownTargetW;

    // Active world's HUMAN-scale multiplier: classic camera constants (a 112-unit eye, a 48-unit step)
    // times this give the same physical size in the active game's units. 1 for every classic-unit game,
    // 1/70 for Starfield, whose unit is a metre. Deliberately NOT derived from _cellSize — the cell
    // shrank 40.96× while the unit grew 70×, so using the cell ratio here leaves the camera 1.56× tall.
    private float _unitScale = 1f;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.WaterRenderer12? _water;

    private ResolvedWaterAppearanceSelection _waterAppearanceSelection;

    // Single-sample HDR opaque-scene snapshot sampled only by the bounded FNV WATER001 route.
    // Like _depthSrv, the persistent slot survives surface unload and is rewritten after resize.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12.PersistentAllocation?
        _waterOpaqueSnapshotSrv;

    // Keep the descriptor immutable while prior live frames may still be in flight. The resource is
    // stable until a GPU-idle resize/unload; only an identity change permits repointing the slot.
    private Vortice.Direct3D12.ID3D12Resource? _waterOpaqueSnapshotSrvResource;

    // Video-expander toggles (retail Ultra High defaults — everything on). Persist across ESM
    // reloads like _showMarkers; re-seeded into the renderers on pipeline construction and
    // applied live through the SetWaterReflections/SetWaterRipples/SetWindowReflections/
    // SetGrassShadows/SetCanopyShadows funnels (Toolbar.cs).
    private bool _waterReflectionsEnabled = true;
    private GpuDescriptorHeapAllocator12.PersistentAllocation? _waterReflectionSrv;
    private Vortice.Direct3D12.ID3D12Resource? _waterReflectionSrvResource;

    /// <summary>
    ///     Planar sky-reflection target: the sky drawn once per frame with the view's Z axis
    ///     mirrored, sampled by the water shaders. Small (the reflection is heavily perturbed by ripple
    ///     normals, so resolution buys little) and re-created only when the scene ASPECT changes — the
    ///     water samples it by normalized screen UV, so only the aspect must track the viewport.
    /// </summary>
    private GpuOffscreenSceneTarget12? _waterReflectionTarget;

    private int _waterReflectionTargetH;
    private int _waterReflectionTargetW;

    private bool _waterRipplesEnabled = true;

    // Runtime DMP weather transitions apply only while the UI is on the climate-default sentinel.
    // Concrete dropdown/profiler choices are atomic previews and must not inherit Sky::pLastWeather.
    private bool _weatherSelectionIsClimateDefault = true;
    private bool _windowReflectionsEnabled = true;

    public WorldView3DControl()
    {
        // The settings panel exists for the control's whole lifetime (accessor shims like
        // NavMeshCheckBox resolve through it), so standalone hosts (profiler apps) work with no
        // SingleFileTab attached — the host merely DISPLAYS SettingsPanel in its Settings tab.
        SettingsPanel = new WorldView3DSettingsPanel();
        ExportPanel = new WorldView3DExportPanel(); // shown in the host's right-panel Export tab
        InitializeComponent();
        WireSettingsPanel(); // subscribe while _initializing still gates the handlers
        WireExportPanel();
        _initializing = false; // XAML load done — settings-panel toggle handlers may now run for real.
        // The interior browser is the shared CellListControl; in 3D a cell loads only on a real click.
        CellList.Activation = CellListControl.ActivationMode.ItemClick;
        CellList.CellActivated += CellList_CellActivated;
        WorldspaceList.WorldspaceActivated += WorldspaceList_WorldspaceActivated;
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

    /// <summary>The boolean most call sites actually ask: is the GUI in the HDR state?</summary>
    private bool HdrGuiActive =>
        _tonemapGuiMode == Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapGuiMode.Hdr;

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

    /// <summary>
    ///     Requests the same semantic adapted-light invalidation represented by FNV's
    ///     <c>ClearAdaptedLight</c>. The next tonemap frame observes a new history key.
    /// </summary>
    internal void RequestClearAdaptedLight()
    {
        unchecked
        {
            _tonemapHistoryClearGeneration++;
        }
    }

    internal void LoadData(WorldViewData data)
    {
        var loadSelectionGeneration = BeginSceneSelection();
        // Overrides are inspection previews, not plugin edits. A newly loaded file starts from its
        // authored REFR + XESP state even when it reuses a FormID from the prior scene.
        _referenceEnabledOverrides.Clear();
        RefreshReferenceOverrideRecoveryUi();
        RequestClearAdaptedLight();
        _data = data;
        // Morrowind has no engine HDR/bloom/imagespace stage (LegacyClamp) — hide the toggles
        // rather than offering dead switches. Re-evaluated on every LoadData (ESM switch).
        SettingsPanel.TonemapSection.Visibility = data.Game == Core.Games.BethesdaGame.Morrowind
            ? Visibility.Collapsed
            : Visibility.Visible;
        SettingsPanel.GrassCheckBox.Visibility =
            Core.Formats.Nif.Rendering.Vegetation.GrassScatterProfile.ForGame(data.Game).Supported
                ? Visibility.Visible
                : Visibility.Collapsed;
        // Video expander: per-game capability visibility (profile-driven, like GrassCheckBox).
        // Hidden toggles keep their latent preferences; only an impossible radio state is coerced
        // (the launcher's middle "Bloom" exists solely for the classic-HDR games).
        var videoProfile = Core.Formats.Nif.Rendering.VideoSettingsProfile.ForGame(data.Game);
        SettingsPanel.ApplyVideoProfile(videoProfile);
        if (!videoProfile.HasClassicHdrTriple &&
            _tonemapGuiMode == Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapGuiMode.SdrBloom)
        {
            SetTonemapGuiMode(Core.Formats.Nif.Rendering.Gpu.D3D12.GpuTonemapGuiMode.Hdr);
        }

        // Make the base-object category available to the placement bake before the renderer pulls
        // its first cell — drives the per-category visibility filter (activators default visible).
        data.RenderCache.CategoryIndex = data.CategoryIndex;
        // Resolve placed REFR base FormIDs to LIGH definitions during the same one-time cell bake.
        // This is independent of ModelPath, so meshless lights still become emitter-only entries.
        data.RenderCache.LightIndex = data.LightsByFormId;
        data.RenderCache.ExternalEmittanceIndex = data.ExternalEmittanceColorsByFormId;
        data.RenderCache.LandTextureIndex = data.LandTexturesByFormId;
        data.RenderCache.GrassIndex = data.GrassesByFormId;
        data.RenderCache.Game = data.Game;
        data.RenderCache.DefaultWaterHeight = data.DefaultWaterHeight;
        // XESP enable-parent resolution: parent-gated refs (condition-driven FX like light-ray
        // cones) bake as initially-disabled instead of always drawing.
        data.RenderCache.XespDisabledRefs = data.XespDisabledRefs;
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
        _placedLightClipLoggedCells.Clear();
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
        _terrain?.Dispose();
        _terrain = null;
        _textureResolver12?.Dispose();
        _textureResolver12 = null;
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

                _textureResolver12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.TerrainTextureResolver12(
                    _gpu12, _commandRecorder12!, _cbvSrvUavHeap12!, _deletionQueue12!,
                    _data.LandTexturesByFormId, _data.TextureSetsByFormId, bsas, _data.Game);
                var terrain12 = new BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.TerrainRenderer12(
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
                _textureResolver12?.Dispose();
                _textureResolver12 = null;
            }

            TryInitReferencePipeline();
        }

        // Mirror WorldMapControl's worldspace picker: one entry per worldspace, plus a final
        // "Unlinked Exterior" entry when DMP-only loads surface cells with no parent worldspace.
        _suppressWorldspaceSelectionEvent = true;
        WorldspaceComboBox.Items.Clear();
        // Same entries, same order, feeding both pickers: the ComboBox (fine for a handful) and the
        // searchable browser (needed once a game ships hundreds). The browser keys on this index.
        var worldspaceRows = new List<WorldspaceListControl.WorldspaceListItem>();
        foreach (var ws in data.Worldspaces)
        {
            var name = WorldMapColors.FormatWorldspaceName(ws);
            WorldspaceComboBox.Items.Add($"{name} — {ws.Cells.Count} cells");
            worldspaceRows.Add(new WorldspaceListControl.WorldspaceListItem(
                worldspaceRows.Count,
                string.IsNullOrWhiteSpace(ws.EditorId) ? $"0x{ws.FormId:X8}" : ws.EditorId,
                name,
                ws.Cells.Count));
        }

        if (data.UnlinkedExteriorCells.Count > 0)
        {
            WorldspaceComboBox.Items.Add($"Unlinked Exterior ({data.UnlinkedExteriorCells.Count} cells)");
            worldspaceRows.Add(new WorldspaceListControl.WorldspaceListItem(
                worldspaceRows.Count, "(unlinked)", "Unlinked Exterior",
                data.UnlinkedExteriorCells.Count));
        }

        _suppressWorldspaceSelectionEvent = false;

        WorldspaceList.Populate(worldspaceRows);
        HideWorldspaceBrowser();
        WorldspacesButton.Content = worldspaceRows.Count > 0
            ? $"Worldspaces ({worldspaceRows.Count})"
            : "Worldspaces";
        WorldspacesButton.IsEnabled = worldspaceRows.Count > 0;

        // Interiors live in the shared cell browser (Interiors button), not the combo.
        _selectedInterior = null;
        HideInteriorBrowser();
        InteriorsButton.Content = data.InteriorCells.Count > 0
            ? $"Interiors ({data.InteriorCells.Count})"
            : "Interiors";
        InteriorsButton.IsEnabled = data.InteriorCells.Count > 0;
        AllCellsButton.Content = data.AllCells.Count > 0
            ? $"All Cells ({data.AllCells.Count})"
            : "All Cells";
        AllCellsButton.IsEnabled = data.AllCells.Count > 0;

        // Weather dropdown: "(Climate default)" + all weathers. Built once here; the worldspace
        // selection below refreshes the climate default + timing it resolves against.
        PopulateWeatherDropdown();

        // Imagespace dropdown: "(Automatic)" + "(None)" + every IMGS record; resets to Automatic.
        PopulateImagespaceDropdown();

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
            RefreshAtmosphereForCurrentWorldspace();
            MarkSceneSelectionReady(loadSelectionGeneration);
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
