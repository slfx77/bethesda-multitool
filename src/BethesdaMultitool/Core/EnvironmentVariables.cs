namespace BethesdaMultitool.Core;

/// <summary>
///     Central names for application-owned environment variables. Keeping them here prevents
///     renderer/profiler/debug knobs from drifting as hardcoded strings across the codebase.
/// </summary>
internal static class EnvironmentVariables
{
    public const string Enabled = "1";

    public static string? Get(string name) => Environment.GetEnvironmentVariable(name);

    public static void Set(string name, string? value) => Environment.SetEnvironmentVariable(name, value);

    public static bool IsEnabled(string name) =>
        string.Equals(Get(name), Enabled, StringComparison.Ordinal);

    /// <summary>
    ///     Parses <paramref name="name" /> as an invariant-culture integer clamped to
    ///     [<paramref name="min" />, <paramref name="max" />]; unset/unparseable values yield
    ///     <paramref name="defaultValue" />. The single canonical way concurrency/budget knobs read
    ///     their overrides.
    /// </summary>
    public static int GetClampedInt(string name, int defaultValue, int min, int max)
    {
        var raw = Get(name);
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    /// <inheritdoc cref="GetClampedInt" />
    public static long GetClampedLong(string name, long defaultValue, long min, long max)
    {
        var raw = Get(name);
        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
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

        public const string TerrainBuildConcurrency = "FALLOUT_VIEWER_TERRAIN_BUILD_CONCURRENCY";
        public const string TerrainBuildStartsPerFrame = "FALLOUT_VIEWER_TERRAIN_BUILD_STARTS_PER_FRAME";

        public const string ReferenceDecodeConcurrency = "FALLOUT_VIEWER_REFERENCE_DECODE_CONCURRENCY";
        public const string ReferenceDecodeStartsPerFrame = "FALLOUT_VIEWER_REFERENCE_DECODE_STARTS_PER_FRAME";
        public const string ReferenceUploadBytesPerFrame = "FALLOUT_VIEWER_REFERENCE_UPLOAD_BYTES_PER_FRAME";
        public const string ReferenceUploadsPerFrame = "FALLOUT_VIEWER_REFERENCE_UPLOADS_PER_FRAME";
        public const string ReferenceUploadMillisecondsPerFrame = "FALLOUT_VIEWER_REFERENCE_UPLOAD_MS_PER_FRAME";

        /// <summary>Resident-mesh LRU entry cap for the 3D viewer's reference cache (default 2048). Diagnostic lever: tiny values force constant eviction-cascade churn for stress gates.</summary>
        public const string ReferenceMeshCapacity = "FALLOUT_VIEWER_REFERENCE_MESH_CAPACITY";

        /// <summary>Decoded CPU mesh cache byte budget in MEGABYTES (default 256). Diagnostic lever, same purpose as <see cref="ReferenceMeshCapacity" />.</summary>
        public const string ReferenceDecodedCacheMegabytes = "FALLOUT_VIEWER_REFERENCE_DECODED_CACHE_MB";

        public const string DisableReferenceFrustum = "FALLOUT_VIEWER_DISABLE_REFERENCE_FRUSTUM";
        public const string ReferenceDistanceLod = "FALLOUT_VIEWER_REFERENCE_DISTANCE_LOD";

        /// <summary>Camera-relative rendering for the scene (terrain/references/water). DEFAULT ON;
        /// set to "0" to disable and render in absolute world space (the pre-fix behavior, for A/B
        /// comparison). Eliminates the float-precision "wobble" at worldspace edges by shifting scene
        /// vertices by -cameraPos before projection. Sky + debug overlays render in absolute space and
        /// stay clip-aligned either way.</summary>
        public const string CameraRelative = "FALLOUT_VIEWER_CAMERA_RELATIVE";

        /// <summary>Per-frame upload-heap ring-buffer size in MEGABYTES (default 64). Shared by every D3D12 renderer's per-draw CBs; raise if a very dense top-down render reports "frame slot exhausted".</summary>
        public const string RingBufferMegabytes = "FALLOUT_VIEWER_RING_BUFFER_MB";

        /// <summary>When 1, renders engine marker objects (XMarker/heading, map/travel markers, etc.) that are hidden by default to match the game.</summary>
        public const string ShowMarkers = "FALLOUT_VIEWER_SHOW_MARKERS";

        /// <summary>When 1, renders imposter (distant LOD stand-in) objects that are suppressed by default where a co-located full model exists.</summary>
        public const string ShowImposters = "FALLOUT_VIEWER_SHOW_IMPOSTERS";

        /// <summary>SpeedTree fallback trunk height in native units (default 90) — used only when the TREE record has no OBND. Live-tunable; .spt geometry is not disk-cached.</summary>
        public const string SpeedTreeHeight = "FALLOUT_VIEWER_SPT_HEIGHT";

        /// <summary>SpeedTree final-height tuning multiplier on the data-driven (OBND) tree height (default 1.0).
        /// The only host-side tree knob — branch/leaf geometry is fully derived from the .spt + decompile.</summary>
        public const string SpeedTreeHeightScale = "FALLOUT_VIEWER_SPT_HEIGHT_SCALE";

        /// <summary>SpeedTree leaf-wind sway strength (default 0.12; 0 disables, trees render static).
        /// The leaf-billboard VS sways each card by this × per-leaf weight × card size (model recovered
        /// from STLEAF/STB shaders + BSTreeManager::UpdateWindMatrices). Live-tunable; .spt isn't cached.</summary>
        public const string SpeedTreeWind = "FALLOUT_VIEWER_SPT_WIND";

        public const string TextureResolveConcurrency = "FALLOUT_VIEWER_TEXTURE_RESOLVE_CONCURRENCY";
        public const string RetainTexturePayloads = "FALLOUT_VIEWER_RETAIN_TEXTURE_PAYLOADS";

        /// <summary>Persistent decoded texture cache toggle. Default cache root is under <see cref="System.IO.Path.GetTempPath()" />;
        /// set to "0" to disable disk caching.</summary>
        public const string PersistentTextureCache = "FALLOUT_VIEWER_PERSISTENT_TEXTURE_CACHE";

        /// <summary>Optional override for decoded texture cache directory. When unset, the OS temp directory is used.</summary>
        public const string TextureCacheDirectory = "FALLOUT_VIEWER_TEXTURE_CACHE_DIR";
        public const string TextureCacheMaxMegabytes = "FALLOUT_VIEWER_TEXTURE_CACHE_MAX_MB";

        /// <summary>Persistent decoded mesh cache toggle. Default cache root is under <see cref="System.IO.Path.GetTempPath()" />;
        /// set to "0" to disable disk caching.</summary>
        public const string PersistentMeshCache = "FALLOUT_VIEWER_PERSISTENT_MESH_CACHE";

        /// <summary>Optional override for decoded mesh cache directory. When unset, the OS temp directory is used.</summary>
        public const string MeshCacheDirectory = "FALLOUT_VIEWER_MESH_CACHE_DIR";
        public const string MeshCacheMaxMegabytes = "FALLOUT_VIEWER_MESH_CACHE_MAX_MB";
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
        /// <summary>Opt-in CPU-cache byte budget in MB for the MemoryBudgetCoordinator. UNSET = no cap (default): caches are tracked but never trimmed. Set a positive value to enable trimming when CpuCache bytes exceed it.</summary>
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
