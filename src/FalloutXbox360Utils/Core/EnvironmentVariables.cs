namespace FalloutXbox360Utils.Core;

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

        /// <summary>Per-frame upload-heap ring-buffer size in MEGABYTES (default 64). Shared by every D3D12 renderer's per-draw CBs; raise if a very dense top-down render reports "frame slot exhausted".</summary>
        public const string RingBufferMegabytes = "FALLOUT_VIEWER_RING_BUFFER_MB";

        /// <summary>When 1, renders engine marker objects (XMarker/heading, map/travel markers, etc.) that are hidden by default to match the game.</summary>
        public const string ShowMarkers = "FALLOUT_VIEWER_SHOW_MARKERS";

        /// <summary>When 1, renders imposter (distant LOD stand-in) objects that are suppressed by default where a co-located full model exists.</summary>
        public const string ShowImposters = "FALLOUT_VIEWER_SHOW_IMPOSTERS";

        /// <summary>SpeedTree fallback trunk height in native units (default 90) — used only when the TREE record has no OBND. Live-tunable; .spt geometry is not disk-cached.</summary>
        public const string SpeedTreeHeight = "FALLOUT_VIEWER_SPT_HEIGHT";

        /// <summary>SpeedTree final-height tuning multiplier on the data-driven (OBND) tree height (default 1.0).</summary>
        public const string SpeedTreeHeightScale = "FALLOUT_VIEWER_SPT_HEIGHT_SCALE";

        /// <summary>SpeedTree leaf-card size multiplier (default 1.0).</summary>
        public const string SpeedTreeLeafScale = "FALLOUT_VIEWER_SPT_LEAF_SCALE";

        /// <summary>SpeedTree leaf cards per terminal-branch ring (default 2).</summary>
        public const string SpeedTreeLeafCount = "FALLOUT_VIEWER_SPT_LEAF_COUNT";

        /// <summary>SpeedTree child-branch declination in degrees, higher = bushier/wider (default 62).</summary>
        public const string SpeedTreeBranchAngle = "FALLOUT_VIEWER_SPT_BRANCH_ANGLE";

        /// <summary>SpeedTree child-branch count multiplier (default 1.0).</summary>
        public const string SpeedTreeChildDensity = "FALLOUT_VIEWER_SPT_CHILD_DENSITY";

        /// <summary>SpeedTree per-ring curl/bend strength in radians (default 0.13).</summary>
        public const string SpeedTreeCurl = "FALLOUT_VIEWER_SPT_CURL";

        /// <summary>SpeedTree gravity/droop strength — higher = lower, wider, droopier crown (default 0.6).</summary>
        public const string SpeedTreeGravity = "FALLOUT_VIEWER_SPT_GRAVITY";

        public const string TextureResolveConcurrency = "FALLOUT_VIEWER_TEXTURE_RESOLVE_CONCURRENCY";
        public const string RetainTexturePayloads = "FALLOUT_VIEWER_RETAIN_TEXTURE_PAYLOADS";
        public const string PersistentTextureCache = "FALLOUT_VIEWER_PERSISTENT_TEXTURE_CACHE";
        public const string TextureCacheDirectory = "FALLOUT_VIEWER_TEXTURE_CACHE_DIR";
        public const string TextureCacheMaxMegabytes = "FALLOUT_VIEWER_TEXTURE_CACHE_MAX_MB";
        public const string PersistentMeshCache = "FALLOUT_VIEWER_PERSISTENT_MESH_CACHE";
        public const string MeshCacheDirectory = "FALLOUT_VIEWER_MESH_CACHE_DIR";
        public const string MeshCacheMaxMegabytes = "FALLOUT_VIEWER_MESH_CACHE_MAX_MB";
    }

    public static class Map2D
    {
        public const string Trace = "FALLOUT_MAP2D_TRACE";
        public const string TopDownDump = "FALLOUT_MAP2D_TOPDOWN_DUMP";
        public const string TerrainTextureAggregateConcurrency = "FALLOUT_MAP2D_TERRAIN_TEXTURE_AGGREGATE_CONCURRENCY";

        /// <summary>
        ///     Profiling A/B knob: when set, <c>WorldMapOverviewRenderer.DrawTextureCellBitmaps</c>
        ///     reverts to the pre-mip behaviour (draw the highest-res cached tier per cell, always
        ///     HighQualityCubic). Lets the profiler measure the mip-selection + bilinear perf change
        ///     without a code revert. Unset = the current mip-aware path.
        /// </summary>
        public const string LegacyTerrainDraw = "FALLOUT_MAP2D_LEGACY_TERRAIN_DRAW";
    }

    public static class Profiler
    {
        public const string With3D = "FALLOUT_PROFILER_WITH_3D";
    }

    public static class Cli
    {
        public const string EsmOutputPath = "ESM_OUTPUT_PATH";
        public const string GltfValidatorExecutable = "GLTF_VALIDATOR_EXE";
    }

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

    public static class Diagnostics
    {
        /// <summary>When 1, CLI commands print the end-of-run resource statistics table (same as --resource-stats).</summary>
        public const string ResourceStats = "FALLOUT_RESOURCE_STATS";
    }
}
