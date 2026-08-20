using System.Globalization;
using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool.Core;

/// <summary>
///     Central names for application-owned environment variables. Keeping them here prevents
///     renderer/profiler/debug knobs from drifting as hardcoded strings across the codebase.
/// </summary>
internal static class EnvironmentVariables
{
    public const string Enabled = "1";

    public static string? Get(string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }

    public static void Set(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
    }

    public static bool IsEnabled(string name)
    {
        return string.Equals(Get(name), Enabled, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Parses <paramref name="name" /> as an invariant-culture integer clamped to
    ///     [<paramref name="min" />, <paramref name="max" />]; unset/unparseable values yield
    ///     <paramref name="defaultValue" />. The single canonical way concurrency/budget knobs read
    ///     their overrides.
    /// </summary>
    public static int GetClampedInt(string name, int defaultValue, int min, int max)
    {
        var raw = Get(name);
        if (!int.TryParse(raw, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    /// <inheritdoc cref="GetClampedInt" />
    public static long GetClampedLong(string name, long defaultValue, long min, long max)
    {
        var raw = Get(name);
        if (!long.TryParse(raw, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    /// <summary>Environment-variable names for the 3D viewer's profiling, debug, concurrency, and cache knobs.</summary>
    public static class Viewer
    {
        public const string FrameStats = "FALLOUT_VIEWER_FRAME_STATS";
        public const string ProfileLog = "FALLOUT_VIEWER_PROFILE_LOG";
        public const string ProfileIntervalMilliseconds = "FALLOUT_VIEWER_PROFILE_INTERVAL_MS";
        public const string ProfileJsonl = "FALLOUT_VIEWER_PROFILE_JSONL";
        public const string StallThresholdMilliseconds = "FALLOUT_VIEWER_STALL_THRESHOLD_MS";
        public const string GpuTimestamps = "FALLOUT_VIEWER_GPU_TIMESTAMPS";
        public const string StressScene = "FALLOUT_VIEWER_STRESS_SCENE";
        public const string D3D12Debug = "FALLOUT_VIEWER_D3D12_DEBUG";
        public const string D3D12GpuBasedValidation = "FALLOUT_VIEWER_D3D12_GBV";
        public const string Dred = "FALLOUT_VIEWER_DRED";
        public const string Worldspace = "FALLOUT_VIEWER_WORLDSPACE";
        public const string DumpReference = "FALLOUT_VIEWER_DUMP_REFR";

        /// <summary>
        ///     Geometry/instance validation for the D3D12 reference renderer. "1": draw-time
        ///     liveness + view-window checks on every opaque batch, upload-time packing self-check,
        ///     strict arena free validation, and per-instance world-matrix sanity checks. "2":
        ///     additionally byte-hashes the mapped arena ranges each drawn batch reads and compares
        ///     against the upload-time hash. Unset/0 = off (zero hot-path cost).
        /// </summary>
        public const string GeometryValidate = "FALLOUT_VIEWER_GEOMETRY_VALIDATE";

        /// <summary>
        ///     When "1" (and <see cref="GeometryValidate" /> is on), a draw that fails
        ///     validation is skipped — instance accounting still advances. Visual confirmation lever
        ///     for diagnosis only, never a fix.
        /// </summary>
        public const string GeometryValidateSkipDraw = "FALLOUT_VIEWER_GEOMETRY_VALIDATE_SKIP_DRAW";

        /// <summary>
        ///     When "1", emits geometry-arena alloc/free/evict JSONL audit events to the
        ///     renderer profiler trace (needs <see cref="ProfileJsonl" /> or --profile-jsonl).
        /// </summary>
        public const string GeometryAudit = "FALLOUT_VIEWER_GEOMETRY_AUDIT";

        /// <summary>
        ///     Explicit opt-in for the lazy FO4/FO76 dynamic-water architecture. Unset keeps the
        ///     established stand-in pipeline byte-for-byte; initialization or per-frame prepass
        ///     failure also falls back to it.
        /// </summary>
        public const string ModernWater = "FALLOUT_VIEWER_MODERN_WATER";

        /// <summary>
        ///     Explicit opt-in for the evidence-backed Skyrim/FO4-family imagespace increment. Unset
        ///     retains GammaACES while modern record blending and shader stages complete capture gates.
        /// </summary>
        public const string ModernImageSpace = "FALLOUT_VIEWER_MODERN_IMAGESPACE";

        /// <summary>
        ///     Explicit opt-in for authored-sky architectural replacements: loading the default
        ///     Atmosphere.nif layer and uploading the full WTHR DALC directional-ambient cube.
        ///     Unset preserves the procedural atmosphere and flat ambient compatibility paths.
        /// </summary>
        public const string AuthoredSky = "FALLOUT_VIEWER_AUTHORED_SKY";

        /// <summary>
        ///     Set to <c>0</c> to force grass back onto the shared reference shaders even for a game
        ///     that has a recovered per-game grass pair. Exists so the per-game lighting can be A/B'd
        ///     against the old path in one session without a rebuild — the shipped default is on.
        /// </summary>
        public const string PerGameGrassShader = "FALLOUT_VIEWER_PER_GAME_GRASS_SHADER";

        public const string TerrainBuildConcurrency = "FALLOUT_VIEWER_TERRAIN_BUILD_CONCURRENCY";
        public const string TerrainBuildStartsPerFrame = "FALLOUT_VIEWER_TERRAIN_BUILD_STARTS_PER_FRAME";

        public const string ReferenceDecodeConcurrency = "FALLOUT_VIEWER_REFERENCE_DECODE_CONCURRENCY";
        public const string ReferenceDecodeStartsPerFrame = "FALLOUT_VIEWER_REFERENCE_DECODE_STARTS_PER_FRAME";
        public const string ReferenceUploadBytesPerFrame = "FALLOUT_VIEWER_REFERENCE_UPLOAD_BYTES_PER_FRAME";
        public const string ReferenceUploadsPerFrame = "FALLOUT_VIEWER_REFERENCE_UPLOADS_PER_FRAME";
        public const string ReferenceUploadMillisecondsPerFrame = "FALLOUT_VIEWER_REFERENCE_UPLOAD_MS_PER_FRAME";

        /// <summary>
        ///     Resident-mesh LRU entry cap for the 3D viewer's reference cache (default 2048). Diagnostic lever: tiny values
        ///     force constant eviction-cascade churn for stress gates.
        /// </summary>
        public const string ReferenceMeshCapacity = "FALLOUT_VIEWER_REFERENCE_MESH_CAPACITY";

        /// <summary>
        ///     Decoded CPU mesh cache byte budget in MEGABYTES (default SCALES WITH SYSTEM RAM —
        ///     see <c>AdaptiveMemoryDefaults</c>: 128/256/512/1024 at &lt;12/12+/24+/48+ GB). Diagnostic
        ///     lever, same purpose as <see cref="ReferenceMeshCapacity" />.
        /// </summary>
        public const string ReferenceDecodedCacheMegabytes = "FALLOUT_VIEWER_REFERENCE_DECODED_CACHE_MB";

        public const string DisableReferenceFrustum = "FALLOUT_VIEWER_DISABLE_REFERENCE_FRUSTUM";
        public const string ReferenceDistanceLod = "FALLOUT_VIEWER_REFERENCE_DISTANCE_LOD";

        /// <summary>
        ///     Translation-tolerant reference cull cache (default ON for the perspective camera).
        ///     Set to "0" to revert to the exact viewProj compare — perf A/B lever + escape hatch.
        /// </summary>
        public const string TolerantCull = "FALLOUT_VIEWER_TOLERANT_CULL";

        /// <summary>
        ///     Angular slack, in whole DEGREES, that the tolerant cull cache absorbs before
        ///     re-culling (default 4, clamped 0-20). The establishment cull widens its frustum by the same
        ///     angle, so the cached survivor set stays a superset across small rotations. Set to "0" to
        ///     restore the exact-forward compare, under which ANY mouse movement re-culls every frame.
        /// </summary>
        public const string TolerantCullDegrees = "FALLOUT_VIEWER_TOLERANT_CULL_DEG";

        /// <summary>
        ///     Frames a batch set built DURING streaming may be reused before it is rebuilt
        ///     (default 4, clamped 1-60). Bounds how late newly-streamed geometry can appear; 1 restores
        ///     rebuild-every-frame for a settling scene.
        /// </summary>
        public const string BatchReuseStreamingFrames = "FALLOUT_VIEWER_BATCH_REUSE_STREAM_FRAMES";

        /// <summary>
        ///     Per-cascade culling of the sun-shadow replay (default ON): each captured draw
        ///     submits only the casters that can reach the cascade being filled. Set to "0" to submit the
        ///     whole caster set to every cascade — the previous behaviour, and the A/B lever for proving
        ///     the culling is not dropping shadows.
        /// </summary>
        public const string ShadowCascadeCull = "FALLOUT_VIEWER_SHADOW_CASCADE_CULL";

        /// <summary>
        ///     Unified transparency stream (default ON): blended reference draws and FNV water
        ///     batches merge into one back-to-front sequence, matching the engine's single depth-sorted
        ///     alpha stream. "0" restores the split pass order — the A/B lever for ordering changes.
        /// </summary>
        public const string UnifiedTransparency = "FALLOUT_VIEWER_UNIFIED_TRANSPARENCY";

        /// <summary>
        ///     Engine z-write rule inside the unified stream (default ON): per-draw z-write =
        ///     decompiled ambient-ON minus the authored exceptions (decal / hair flag /
        ///     NoLighting flags2-bit0-clear), so blended LIT geometry writes depth. "0" keeps the
        ///     legacy blend+test+threshold per-draw choice. No effect when the stream is off.
        /// </summary>
        public const string EngineZWrite = "FALLOUT_VIEWER_ENGINE_ZWRITE";

        /// <summary>
        ///     "1" renders TRIGGERED FX as if their activation sequences were playing (the
        ///     pre-2026-08-05 behavior). Default OFF = the load-time rest-state resolve: a particle
        ///     emitter whose NiControllerManager sequences are all activation-triggered (no idle-named
        ///     autoplay) stays DORMANT, matching the world at game start before any script or door
        ///     fires it.
        /// </summary>
        public const string TriggeredFx = "FALLOUT_VIEWER_TRIGGERED_FX";

        /// <summary>
        ///     Planar sky reflection for water (default ON): a second, mirrored sky draw into a
        ///     small offscreen target that the water shaders sample. "0" restores the 2-row sky-gradient
        ///     stand-in — the A/B lever for the reflection's cost and look.
        /// </summary>
        public const string WaterReflection = "FALLOUT_VIEWER_WATER_REFLECTION";

        /// <summary>
        ///     Parallel establishment cull (default ON): the visible cells are partitioned
        ///     across the thread pool and the per-partition survivors merged in cell order. Set to "0"
        ///     for the single-threaded loop — the A/B lever for the cull's cost and for proving the
        ///     merge preserves survivor order.
        /// </summary>
        public const string ParallelCull = "FALLOUT_VIEWER_PARALLEL_CULL";

        /// <summary>
        ///     Mip chain on the FNV water noise-normal prepass target (default ON). The
        ///     authored NNAM DDS the retail shader samples ships with mips; the recovered prepass
        ///     regenerates mip 0 every frame, and sampling it mipless at the macro tile turns grazing
        ///     views into per-frame uncorrelated glint shimmer. Set to "0" to restore the single-mip
        ///     texture — the A/B lever for the fixed-pose water flicker.
        /// </summary>
        public const string WaterNoiseMips = "FALLOUT_VIEWER_WATER_NOISE_MIPS";

        /// <summary>
        ///     Keep the broadphase frustum filter armed while the sun-shadow caster ring is active
        ///     (default ON), widening it by the ring instead of dropping it. Set to "0" for the previous
        ///     behaviour — an unfiltered broadphase — as a perf A/B lever and escape hatch.
        /// </summary>
        public const string BroadphaseShadowFrustum = "FALLOUT_VIEWER_BROADPHASE_SHADOW_FRUSTUM";

        /// <summary>
        ///     Reference batch reuse on quiesced frames (default ON): when the cull cache hit,
        ///     the render origin is unchanged, and the last build streamed nothing, the per-frame
        ///     resolve+batch pass is skipped and the frozen batches are re-drawn (with a per-instance
        ///     exact cull at draw time). Set to "0" to rebuild every frame — perf A/B + escape hatch.
        /// </summary>
        public const string BatchReuse = "FALLOUT_VIEWER_BATCH_REUSE";

        /// <summary>
        ///     Camera-relative rendering for the scene (terrain/references/water). DEFAULT ON;
        ///     set to "0" to disable and render in absolute world space (the pre-fix behavior, for A/B
        ///     comparison). Eliminates the float-precision "wobble" at worldspace edges by shifting scene
        ///     vertices by -cameraPos before projection. Sky + debug overlays render in absolute space and
        ///     stay clip-aligned either way.
        /// </summary>
        public const string CameraRelative = "FALLOUT_VIEWER_CAMERA_RELATIVE";

        /// <summary>
        ///     Sun shadow kill-switch: set to "0" to disable the cascaded directional shadow
        ///     pass entirely (default ON; also toggleable per-session in the lighting flyout). OFF
        ///     renders pixel-identical to the pre-shadow viewer. Companion knob:
        ///     <c>FALLOUT_VIEWER_SHADOW_RES</c> (per-cascade map dimension; the default is VRAM-scaled
        ///     from the adapter's OS-assigned budget — 4096 at ≥ 8 GB, 2048 at ≥ 3 GB, else 1024).
        /// </summary>
        public const string Shadows = "FALLOUT_VIEWER_SHADOWS";

        /// <summary>
        ///     Placed LIGH emitter kill-switch. Set to "0" to skip the point-light
        ///     buffer and shader loop (default ON for interiors AND exteriors; also gated by the live UI
        ///     toggle). The former exterior-only opt-in, FALLOUT_VIEWER_EXTERIOR_LIGHTS, is gone: nothing
        ///     ever set it, so it silently forced every exterior to zero lights in every game while the UI
        ///     toggle claimed otherwise. Its real purpose — bounding the upload in dense worldspaces — is
        ///     now served by the frame-wide nearest-N cap in WorldView3DControl.PointLights.
        /// </summary>
        public const string PlacedLights = "FALLOUT_VIEWER_PLACED_LIGHTS";

        /// <summary>
        ///     DIAGNOSTIC cap for the LAST shadow cascade's half-extent, in WORLD UNITS
        ///     (unset = the fixed ladder's 131072; the earlier cascades are fixed at 2048/8192/32768).
        ///     Exists only for headless texel-density experiments.
        /// </summary>
        public const string ShadowRadius = "FALLOUT_VIEWER_SHADOW_RADIUS";

        /// <summary>
        ///     When 1, logs each sun-shadow cascade render decision ([Shadow] lines). OFF by
        ///     default — the per-re-render console write at walking cadence was a measurable frame cost.
        /// </summary>
        public const string ShadowDiag = "FALLOUT_VIEWER_SHADOW_DIAG";

        /// <summary>
        ///     Per-frame upload-heap ring-buffer size in MEGABYTES (default SCALES WITH SYSTEM
        ///     RAM — see <c>AdaptiveMemoryDefaults</c>: 64/128/256 at &lt;12/12+/24+ GB). Shared by every
        ///     D3D12 renderer's per-draw CBs; raise if a very dense whole-map view drops draws (every
        ///     consumer soft-fails instead of throwing).
        /// </summary>
        public const string RingBufferMegabytes = "FALLOUT_VIEWER_RING_BUFFER_MB";

        /// <summary>
        ///     When 1, renders engine marker objects (XMarker/heading, map/travel markers, etc.) that are hidden by default
        ///     to match the game.
        /// </summary>
        public const string ShowMarkers = "FALLOUT_VIEWER_SHOW_MARKERS";

        /// <summary>
        ///     When 1, the 3D navigation overlay (NAVM navmeshes / TES4 PGRD pathgrids) starts ON — headless captures can
        ///     prove the overlay without the toolbar toggle.
        /// </summary>
        public const string ShowNavMesh = "FALLOUT_VIEWER_SHOW_NAVMESH";

        /// <summary>
        ///     When 1, renders imposter (distant LOD stand-in) objects that are suppressed by default where a co-located full
        ///     model exists.
        /// </summary>
        public const string ShowImposters = "FALLOUT_VIEWER_SHOW_IMPOSTERS";

        /// <summary>
        ///     SpeedTree fallback trunk height in native units (default 90) — used only when the TREE record has no OBND.
        ///     Live-tunable; .spt geometry is not disk-cached.
        /// </summary>
        public const string SpeedTreeHeight = "FALLOUT_VIEWER_SPT_HEIGHT";

        /// <summary>
        ///     SpeedTree final-height tuning multiplier on the data-driven (OBND) tree height (default 1.0).
        ///     The only host-side tree knob — branch/leaf geometry is fully derived from the .spt + decompile.
        /// </summary>
        public const string SpeedTreeHeightScale = "FALLOUT_VIEWER_SPT_HEIGHT_SCALE";

        /// <summary>
        ///     SpeedTree leaf-wind strength MANUAL OVERRIDE on the engine scale (weather wind-speed
        ///     byte / 255; 0 = static). UNSET (the default) = engine-faithful auto: wind follows the viewer's
        ///     active weather record, forced 0 in interiors (Sky::UpdateWind). Drives the leaf rock/rustle
        ///     model recovered from the STLEAF shaders + BSTreeManager::UpdateWindMatrices.
        /// </summary>
        public const string SpeedTreeWind = "FALLOUT_VIEWER_SPT_WIND";

        public const string TextureResolveConcurrency = "FALLOUT_VIEWER_TEXTURE_RESOLVE_CONCURRENCY";
        public const string RetainTexturePayloads = "FALLOUT_VIEWER_RETAIN_TEXTURE_PAYLOADS";

        /// <summary>
        ///     Persistent decoded texture cache toggle. Default cache root is under <see cref="System.IO.Path.GetTempPath()" />;
        ///     set to "0" to disable disk caching.
        /// </summary>
        public const string PersistentTextureCache = "FALLOUT_VIEWER_PERSISTENT_TEXTURE_CACHE";

        /// <summary>Optional override for decoded texture cache directory. When unset, the OS temp directory is used.</summary>
        public const string TextureCacheDirectory = "FALLOUT_VIEWER_TEXTURE_CACHE_DIR";

        public const string TextureCacheMaxMegabytes = "FALLOUT_VIEWER_TEXTURE_CACHE_MAX_MB";

        /// <summary>
        ///     Persistent decoded mesh cache toggle. Default cache root is under <see cref="System.IO.Path.GetTempPath()" />;
        ///     set to "0" to disable disk caching.
        /// </summary>
        public const string PersistentMeshCache = "FALLOUT_VIEWER_PERSISTENT_MESH_CACHE";

        /// <summary>Optional override for decoded mesh cache directory. When unset, the OS temp directory is used.</summary>
        public const string MeshCacheDirectory = "FALLOUT_VIEWER_MESH_CACHE_DIR";

        public const string MeshCacheMaxMegabytes = "FALLOUT_VIEWER_MESH_CACHE_MAX_MB";

        /// <summary>
        ///     NIF animation kill-switch: set to "0" to decode animated/skinned placed NIFs as
        ///     inert static geometry (pre-v32 behavior — bind-pose banners, no UV scroll velocities).
        ///     The disk-cache version stays 32 either way, so flipping it doesn't corrupt warm entries.
        /// </summary>
        public const string NifAnimation = "FALLOUT_VIEWER_NIF_ANIMATION";
    }

    /// <summary>Environment-variable names for the 2D world-map renderer's tracing and terrain-draw knobs.</summary>
    public static class Map2D
    {
        public const string Trace = "FALLOUT_MAP2D_TRACE";
        public const string TopDownDump = "FALLOUT_MAP2D_TOPDOWN_DUMP";
        public const string TerrainTextureAggregateConcurrency = "FALLOUT_MAP2D_TERRAIN_TEXTURE_AGGREGATE_CONCURRENCY";

        /// <summary>
        ///     Profiling A/B knob: when set, <c>WorldMapOverviewRenderer.DrawTextureCellBitmaps</c>
        ///     reverts to the pre-mip behavior (draw the highest-res cached tier per cell, always
        ///     HighQualityCubic). Lets the profiler measure the mip-selection + bilinear perf change
        ///     without a code revert. Unset = the current mip-aware path.
        /// </summary>
        public const string LegacyTerrainDraw = "FALLOUT_MAP2D_LEGACY_TERRAIN_DRAW";
    }

    /// <summary>Environment-variable names for the standalone profiler harnesses.</summary>
    public static class Profiler
    {
        public const string With3D = "FALLOUT_PROFILER_WITH_3D";
    }

    /// <summary>Environment-variable names for ESM / memory-dump record parsing.</summary>
    public static class Esm
    {
        /// <summary>
        ///     When 1, DISABLES lenient partial recovery of truncated compressed records read from memory
        ///     dumps. Default (unset) = ON for DMPs: a compressed record whose zlib payload is cut short in
        ///     the dump still yields its complete leading subrecords (the parser stops cleanly at the cut)
        ///     instead of being dropped — data preservation for the pre-July-2010 dumps. Clean on-disk ESMs
        ///     and the Xbox→PC conversion path stay strictly all-or-nothing regardless of this knob.
        /// </summary>
        public const string DisableDmpPartialRecovery = "FALLOUT_ESM_DISABLE_DMP_PARTIAL_RECOVERY";
    }

    /// <summary>Environment-variable names that override CLI output paths and external tool locations.</summary>
    public static class Cli
    {
        public const string EsmOutputPath = "ESM_OUTPUT_PATH";
        public const string GltfValidatorExecutable = "GLTF_VALIDATOR_EXE";
    }

    /// <summary>Environment-variable names for the memory budget coordinator's cache-budget knobs.</summary>
    public static class Memory
    {
        /// <summary>
        ///     Opt-in CPU-cache byte budget in MB for the MemoryBudgetCoordinator. UNSET = no cap (default): caches are
        ///     tracked but never trimmed. Set a positive value to enable trimming when CpuCache bytes exceed it.
        /// </summary>
        public const string BudgetMegabytes = "FALLOUT_MEMORY_BUDGET_MB";

        /// <summary>Coordinator check interval in seconds (default 5).</summary>
        public const string CheckIntervalSeconds = "FALLOUT_MEMORY_CHECK_INTERVAL_S";

        /// <summary>When 1, the coordinator logs a full resource snapshot on every timer tick.</summary>
        public const string Log = "FALLOUT_MEMORY_LOG";

        /// <summary>When 1, disables the memory budget coordinator entirely.</summary>
        public const string Disable = "FALLOUT_MEMORY_DISABLE";
    }

    /// <summary>Environment-variable names for resource-stats reporting and the WinUI GUI log sink.</summary>
    public static class Diagnostics
    {
        /// <summary>When 1, CLI commands print the end-of-run resource statistics table (same as --resource-stats).</summary>
        public const string ResourceStats = "FALLOUT_RESOURCE_STATS";

        /// <summary>
        ///     Path to a file the WinUI GUI appends ALL <see cref="Logger" /> output to. A windowed app
        ///     has no visible console, so without this every log line (and every env-gated trace such as
        ///     <see cref="Map2D.Trace" />) is lost. When set, <c>GuiEntryPoint</c> also auto-enables
        ///     <see cref="Map2D.Trace" /> (unless the caller set it explicitly) so a single env var
        ///     captures the 2D-map cache/viewport/stream trace from the real app — the same
        ///     instrumentation the BethesdaMap2DProfiler gets. Unset = no file sink.
        /// </summary>
        public const string GuiLogFile = "FALLOUT_GUI_LOG";

        /// <summary>When <c>1</c>, the GUI log captures Debug-level output too (default: Info+).</summary>
        public const string GuiLogVerbose = "FALLOUT_GUI_LOG_VERBOSE";
    }
}
