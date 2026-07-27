#if WINDOWS_GUI
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Effects;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Core.Orchestration;
using Vortice.Direct3D;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     D3D12 placed-object renderer. Opaque/cutout references are batched by cached submesh
///     and rendered with instancing; blended/effect submeshes remain sorted back-to-front.
/// </summary>
internal sealed partial class ReferenceRenderer12 : Abstractions.IReferenceRenderer
{
    // viewProj (64) + the 4 SpeedTree wind-v2 sway matrices (4 × 64) — see reference_instanced.vert.hlsl
    // PerFrame. The shadow pass keeps its own shorter PerFrame; its shader variant never reads the matrices.
    private const uint PerFrameByteSize = 64 + 256;
    private const uint PerDrawByteSize = 256;
    // 3 float4 (material) + uint4 (tex indices) + uint base + uint3 pad + float4 specular
    // + float4 camRight + float4 camUp (leaf billboard basis) + float4 wind
    // + float4 effect tint + float4 effect falloff + float4 env map + float4 soft-particle
    // + float4 TallGrass wind + float4 specular-LOD bounds + float4 specular-LOD params = 256 bytes.
    private const uint InstanceDrawByteSize = 256;
    private const uint NoSceneDepth = 0xFFFFFFFFu;
    private const float FrustumCullMargin = 512f;

    // Cold mesh realization is render-thread work. By DEFAULT there is no per-frame COUNT cap: the
    // wall-clock time budget (MaxUploadMillisecondsPerFrame) + the mesh cache's byte budget pace
    // uploads, so the per-frame *cost* is bounded by frame-TIME rather than an arbitrary mesh count.
    // A count cap couples loading rate to frame rate (N meshes × FPS); time-budgeting keeps the
    // per-frame cost constant regardless of FPS, which is the correct "spend a fixed slice of each
    // frame" pacing. The env var still imposes an explicit count for profiling.
    private static readonly int MaxNewUploadsPerFrame = ParsePositiveIntEnvironment(
        EnvironmentVariables.Viewer.ReferenceUploadsPerFrame,
        defaultValue: int.MaxValue,
        min: 1,
        max: int.MaxValue);

    // Wall-clock ceiling on render-thread mesh-upload work per frame: the primary pacer now that the
    // count cap is lifted by default. Caps *how long* uploading may take so a frame entering a dense
    // area can't spike. A single very large mesh can still consume a frame, but this stops clusters
    // of completed decoded meshes from being realized together.
    private static readonly double MaxUploadMillisecondsPerFrame = ParsePositiveDoubleEnvironment(
        EnvironmentVariables.Viewer.ReferenceUploadMillisecondsPerFrame,
        defaultValue: 2.0,
        min: 0.25,
        max: 10.0);

    // A5 — distance LOD for tiny clutter only. References whose bounding radius is below this are
    // culled past SmallPropDistanceFraction of the view distance. Kept deliberately small (96) so
    // it only drops debris/rocks/small props — solar reflectors (~150-200), cars, shacks and
    // buildings stay visible to the far plane (a prior 256 threshold dropped reflector rows).
    private const float SmallPropLodRadius = 96f;
    private const float SmallPropDistanceFraction = 0.6f;
    // "Dist" loads a SQUARE of half-extent cylinderRadius (Chebyshev). The per-cell sub-cell broadphase
    // is a circular radius query, so widen its radius by √2 to reach the square's corners; the exact
    // per-REFR Chebyshev test below then trims back to the square (the broadphase only over-includes).
    private const float SquareBroadphaseFactor = 1.41422f;

    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly ReferenceMeshCache12 _meshCache;
    private readonly ReferenceEnabledOverrideStore _enabledOverrides;
    private readonly ReferencePipelineFactory12 _pipelines;
    private readonly OpaqueBatchRegistry12 _opaqueBatches = new();
    private readonly List<BlendedReferenceDraw> _blendedDraws = new(256);
    private readonly List<BlendedReferenceDraw> _waterPartitionedBlendedDraws = new(256);
    private enum DeferredWaterPartition
    {
        All,
        WhollyBelow,
        NotWhollyBelow,
    }
    private readonly HashSet<LiveParticleOwner12> _liveParticleOwners = [];
    private readonly List<global::BethesdaMultitool.ParticleRenderTelemetry> _particleTelemetry = [];
    // Depth-writing blend foliage (effects-folder shapes the engine marks ZBuffer_Write, e.g. NVSeaPlant02):
    // kept as alpha blend but drawn INLINE before the water pass with a depth-writing PSO, so water occludes
    // them from above. Separate from _blendedDraws, which defers to after the water pass.
    private readonly List<BlendedReferenceDraw> _depthWritingBlendDraws = new(64);
    private GrassDistanceEnvelope _grassDistanceEnvelope;
    private ClassicSpecularLodProfile _classicSpecularLodProfile;
    private bool _tallGrassWindSupported;
    private readonly HashSet<float> _tallGrassWaveMultipliers = [];

    // Sun-shadow pass replay list: one entry per instanced opaque draw recorded this frame (VB/IB
    // views + the frame's ring-buffer InstanceDraw-CB / instance-SRV GPU addresses), captured at
    // main-draw time and replayed by RenderShadowDepth with the depth-only shadow PSOs. Armed by
    // the host once per frame (ArmShadowCapture) and consumed within that SAME host frame — the
    // captured ring addresses are only valid inside the frame that allocated them. Decals are
    // excluded (coplanar overlays would double-cast onto their backing surface); blended draws
    // don't cast (matching the engine's shadow-caster set).
    private readonly List<ShadowDraw> _shadowDraws = new(512);
    private bool _shadowCaptureArmed;
    // Shadow-only caster ring (world units; 0 = off): frustum-rejected refs within it are kept as
    // casters for the shadow replay. Cached with the cull survivors (see _cachedShadowOnlyCasters).
    private float _shadowCasterRingRadius;
    private readonly List<RenderableReference> _cachedShadowOnlyCasters = new(512);
    private float _cullCacheShadowRing;

    private readonly record struct ShadowDraw(
        VertexBufferView VertexBufferView, IndexBufferView IndexBufferView, int IndexCount,
        ulong PerDrawCbAddress, ulong InstanceSrvAddress, int DrawCount, bool AlphaTested,
        bool UsesTallGrassWind);

    /// <summary>Bumped whenever the opaque batches are REBUILT (a new cull/build pass ran, i.e.
    /// the drawable content may have changed). Part of the shadow map's cache key, so streamed-in
    /// geometry re-renders the map while a settled scene never does.</summary>
    public int BatchContentVersion { get; private set; }

    /// <summary>True when this frame's draw pass captured at least one shadow-caster draw.</summary>
    public bool HasShadowDraws => _shadowDraws.Count > 0;

    /// <summary>Number of shadow-caster draws captured this frame (diagnostics).</summary>
    public int ShadowDrawCount => _shadowDraws.Count;

    /// <summary>True when this frame's captured shadow casters include WIND-ANIMATED leaf cards —
    /// the host then re-renders the map every frame (a cached map freezes the swaying canopy's
    /// shadow into a slideshow that only advances on camera/sun changes).</summary>
    public bool ShadowDrawsIncludeAnimatedLeaves { get; private set; }

    /// <summary>True when this frame's captured shadow casters include a KEYFRAME-ANIMATED mesh
    /// (a CPU-skinned banner) or FNV physics-lite instance matrices — same host contract as
    /// <see cref="ShadowDrawsIncludeAnimatedLeaves" />: the map re-renders while one is casting.</summary>
    public bool ShadowDrawsIncludeAnimatedMeshes { get; private set; }

    /// <summary>Arms the shadow-draw capture for the frame about to be rendered (clears the prior
    /// list). Must be followed by <see cref="RenderShadowDepth" /> within the same host frame when
    /// the map is re-rendered; the addresses go stale with the frame's ring allocations.
    /// <paramref name="casterRingRadius" /> (world units) bounds the SHADOW-ONLY caster set: refs
    /// within it that fail the camera frustum still cast (their shadows can land on-screen), at the
    /// cost of streaming their meshes. 0 disables the augmentation.</summary>
    public void ArmShadowCapture(float casterRingRadius)
    {
        _shadowDraws.Clear();
        ShadowDrawsIncludeAnimatedLeaves = false;
        ShadowDrawsIncludeAnimatedMeshes = false;
        _shadowCaptureArmed = true;
        _shadowCasterRingRadius = MathF.Max(casterRingRadius, 0f);
    }

    /// <summary>Disarms the capture (shadows off / interior): the next cull keeps no shadow-only
    /// casters and the copy pass uploads none.</summary>
    public void DisarmShadowCapture()
    {
        _shadowCaptureArmed = false;
        _shadowCasterRingRadius = 0f;
    }

    // TEMP transparency diagnostic — set FALLOUT_VIEWER_ALPHA_DEBUG=<path-substring> (e.g. "maniasm25")
    // to append each matching mesh's per-submesh alpha classification + actual render pass to
    // %TEMP%\alpha_debug.txt (once per mesh). Tells us whether a "transparent" mesh is genuinely in the
    // OPAQUE pass (→ deeper GPU-state cause) or routed to BLEND at runtime. Remove after diagnosis.
    private static readonly string? AlphaDebugFilter = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_ALPHA_DEBUG");
    private readonly HashSet<string> _alphaDebugLogged = new(StringComparer.OrdinalIgnoreCase);

    // Camera world-space right/up for per-card SpeedTree leaf billboards, set each frame by the host
    // (WorldView3DControl) from the inverse view matrix — same source as the sky billboards. Defaults to
    // the world basis so leaves still render sanely if the host never sets it.
    // NOTE: _leafBillboardRight.W is NOT padding — it carries the STLEAF LeafLighting.y per-corner
    // normal-puff strength to both VSes as uCameraRight.w. Zeroing it flattens leaf shading.
    private Vector4 _leafBillboardRight = new(1f, 0f, 0f, LeafLightingAdjust);
    private Vector4 _leafBillboardUp = new(0f, 0f, 1f, 0f);

    // STLEAF's LeafLighting.y (PC STLEAF000.vso c17.y): scales the per-corner outward lean of each
    // leaf-card normal so the card shades as a rounded cluster instead of a flat plate. Engine value
    // recovered from the FNV 360 MemDebug: SpeedTreeLeafShader::UpdatePipelineForInstance (Fn_82AA14D8)
    // fills a float4 shaped (0, y, 0, 0) — matching the VS's .yyyy-only consumption at every STLEAF
    // site — with y = 0.4 (0x3ECCCCCD) on the standard shader path (mode global == 9) and 0 on the
    // reduced path. The c17 binding is via the serialized shader-package constant map (not a name
    // string), so the register pairing is one inference step; FALLOUT_VIEWER_SPT_LEAF_LIGHTING
    // overrides it for tuning (clamped 0..2).
    private static readonly float LeafLightingAdjust = ReadLeafLightingAdjust();

    private static float ReadLeafLightingAdjust()
    {
        var raw = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_SPT_LEAF_LIGHTING");
        return float.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 0f, 2f)
            : 0.4f;
    }
    // SpeedTree leaf wind uniform: (rockAmount, rockPhase, rustleAmount, rustlePhase) — see SetWind.
    // Default all-zero so non-viewer paths (captures, exports, headless) render trees static.
    private Vector4 _wind;
    private float _windStrength;
    private readonly Core.Formats.SpeedTree.SpeedTreeWindRig _windRig = new();
    private readonly Core.Formats.SpeedTree.SpeedTreeWindRig _captureWindRig = new();
    private bool _captureWindActive;

    // NIF animation clock (seconds), captured from the SetWind timeSeconds the host already sends
    // every frame. Drives UV-scroll offsets (NiUVController) and the CPU skinner's keyframe pose.
    // Stays 0 on paths that never call SetWind → those render the authored base frame / rest pose.
    private double _animationClockSeconds;
    private readonly Animation.CpuMeshSkinner12 _skinner = new();

    private bool _animationsEnabled = true;

    /// <summary>Master switch for NIF keyframe/UV/physics-lite playback (GUI "Animations" toggle).
    /// Off ⇒ UV offsets stay (0,0), the skinner clears its overrides, and sway matrices remain at
    /// the byte-identical static rest pose. Each actual transition invalidates the batch-content
    /// version once so a cached sun-shadow map refreshes into or out of animated rest.</summary>
    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set
        {
            if (!ReferenceAnimationToggle.TryApply(ref _animationsEnabled, value))
            {
                return;
            }

            unchecked
            {
                BatchContentVersion++;
            }
        }
    }

    /// <summary>
    ///     Temporary opt-in legacy live-particle owner. Defaults from
    ///     <c>FALLOUT_VIEWER_LIVE_PARTICLES=1</c>; false keeps immutable cached particle clouds.
    /// </summary>
    public bool LiveParticlesEnabled { get; set; } = ParticleLiveSettings.Enabled;
    private readonly List<global::BethesdaMultitool.WorldSpatialCell> _candidateCells = new();

    // Candidates returned by the per-cell spatial broadphase before exact sphere/frustum cull.
    private readonly List<RenderableReference> _cullCandidateScratch = new(256);

    // Profiler fix #4 — cached cull survivors. The render loop runs the cull (broadphase + per-REFR
    // sphere/frustum tests) and the mesh-resolve/batch build as two SEPARATE passes. When the camera
    // pose, the mesh-bounds generation (grows as meshes resolve and tighten their cull spheres), and
    // the visibility filters are all unchanged frame-to-frame, the cull is SKIPPED and this survivor
    // list is reused — static / settled frames otherwise re-test ~15k candidates every frame (the
    // dominant late-frame cost). The resolve+batch pass still runs each frame so streamed-in meshes
    // appear. Persists across frames (it's the cache); cleared only on a fresh cull or LoadData.
    private readonly List<RenderableReference> _cachedCullSurvivors = new(2048);
    private bool _cullCacheValid;
    private Matrix4x4 _cullCacheViewProj;
    // The camera cylinder (position + radius) is part of the cache key because the camera-relative scene
    // viewProj (1G) is translation-INVARIANT — it changes only with rotation — so viewProj alone would not
    // detect the camera moving. The cylinder captures translation; together they cover the full pose.
    private VisibilityCylinder _cullCacheCylinder;
    private int _cullCacheMeshRadiusCount;
    private bool _cullCacheShowMarkers;
    private bool _cullCacheShowGrass;
    private bool _cullCacheShowImposters;
    private bool _cullCacheShowDisabled;
    private int _cullCacheEnabledOverrideVersion;
    private int _cullCacheCellsVisited;
    private int _cullCacheCandidates;
    private int _cullCacheCulled;

    // 4-pre Item B — per-frame memoization: MeshId → resolved CachedNifMesh12 (or null
    // when the cache hasn't uploaded it yet this frame). Cleared at the top of each
    // Render() so per-frame inserts (which may evict older entries) only affect entries
    // we haven't yet read. Cuts the cull loop's per-REFR string-key dict lookup
    // (~80 ns × 5000 REFRs ≈ 0.4 ms) down to a uint-keyed lookup (~25 ns) after the
    // first sighting of each MeshId.
    private readonly Dictionary<uint, CachedNifMesh12?> _resolvedMeshesThisFrame = new(256);

    // MeshId → the mesh's local bounding-sphere radius (around the NIF origin), recorded the first
    // time a mesh resolves. Lets the per-REFR cull use the ACTUAL geometry extent (conservative)
    // instead of the OBND/256 fallback, so large meshes stop popping at screen edges / near camera.
    // Persists across frames + worldspaces (radius is static per NIF); bounded by unique-mesh count.
    private readonly Dictionary<uint, float> _meshLocalRadius = new(512);

    // MeshId → the mesh's local AABB (min/max around the NIF origin), recorded alongside the radius on
    // the first resident sighting. Feeds the selection highlight a tight box for refs with no OBND
    // (OBLIV-1). Grows in lockstep with _meshLocalRadius, so the cull cache's radius-count gate covers it.
    private readonly Dictionary<uint, (Vector3 Min, Vector3 Max)> _meshLocalBounds = new(512);

    /// <summary>The resolved local bounding-sphere radius for a mesh (around its NIF origin), or false
    /// when that mesh hasn't streamed yet. Lets object picking use the real geometry extent for refs that
    /// have no OBND, instead of the oversized fallback sphere (the "massive click zone" symptom).</summary>
    internal bool TryGetMeshLocalRadius(uint meshId, out float radius) =>
        _meshLocalRadius.TryGetValue(meshId, out radius);

    // The resolved mesh's local AABB (min/max), once it has been seen resident at least once. Used by the
    // selection highlight to box no-OBND refs to their real geometry (OBLIV-1). False until first sighting.
    internal bool TryGetMeshLocalBounds(uint meshId, out Vector3 min, out Vector3 max)
    {
        if (_meshLocalBounds.TryGetValue(meshId, out var bounds))
        {
            min = bounds.Min;
            max = bounds.Max;
            return true;
        }

        min = default;
        max = default;
        return false;
    }

    /// <summary>
    ///     World-space Z extent (min/max) over every reference rendered in the last cull whose mesh AABB
    ///     has streamed in — i.e. the actual geometric vertical span of the loaded scene. The 2D map's
    ///     interior top-down overlay clips just below <c>Max</c> to remove the ceiling: interiors are
    ///     room-meshes with floor-level origins, so placed-object Z can't reveal the roof height, but the
    ///     transformed mesh bounds can. Returns null until at least one survivor's mesh bounds are
    ///     resident (the overlay re-renders while streaming, so the clip converges to the real ceiling).
    /// </summary>
    /// <summary>
    ///     <see cref="GetRenderedSceneWorldZExtent" /> memoized per cull epoch — the full scan is
    ///     O(survivors) with a dictionary lookup each, too hot for the per-frame shadow pass that
    ///     anchors the cascade center vertically. The survivor set only changes when the cull
    ///     re-runs, so the epoch is the exact invalidation signal.
    /// </summary>
    internal (float Min, float Max)? GetRenderedSceneWorldZExtentCached()
    {
        if (_zExtentEpoch != _cullEpoch)
        {
            _zExtentCache = GetRenderedSceneWorldZExtent();
            _zExtentEpoch = _cullEpoch;
        }

        return _zExtentCache;
    }

    private int _zExtentEpoch = -1;
    private (float Min, float Max)? _zExtentCache;

    internal (float Min, float Max)? GetRenderedSceneWorldZExtent()
    {
        var minZ = float.MaxValue;
        var maxZ = float.MinValue;
        var any = false;
        foreach (var r in _cachedCullSurvivors)
        {
            if (!_meshLocalBounds.TryGetValue(r.MeshId, out var b))
            {
                continue;
            }

            // Transform the 8 local-AABB corners by the instance world matrix; keep the Z extent. (A
            // rotated box's world-Z span needs all 8 corners, not just min/max.)
            for (var c = 0; c < 8; c++)
            {
                var corner = new Vector3(
                    (c & 1) == 0 ? b.Min.X : b.Max.X,
                    (c & 2) == 0 ? b.Min.Y : b.Max.Y,
                    (c & 4) == 0 ? b.Min.Z : b.Max.Z);
                var wz = Vector3.Transform(corner, r.WorldMatrix).Z;
                if (wz < minZ) minZ = wz;
                if (wz > maxZ) maxZ = wz;
            }

            any = true;
        }

        return any ? (minZ, maxZ) : null;
    }

    // Placed-NIF water geometry (WaterShaderProperty triangles diverted out of the drawable submesh
    // set at upload) accumulated as meshes resolve, deduped per REFR FormId, and handed to
    // WaterRenderer12 each frame. Each surface is static (authored vertices × placement), so it is
    // transformed exactly once; both collections are reset on LoadData.
    private readonly List<NifWaterGeometry> _nifWaterPlanes = new();
    private readonly HashSet<uint> _nifWaterPlaneSeen = new();
    private readonly bool _disableReferenceFrustum =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.DisableReferenceFrustum);

    // A5 distance-LOD is now OFF by default — it was removing things the user wanted to see.
    // Opt back in with FALLOUT_VIEWER_REFERENCE_DISTANCE_LOD=1 once the bounds are trusted.
    private readonly bool _enableDistanceLod =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ReferenceDistanceLod);

    private Dictionary<(int gx, int gy), CellRecord>? _cells;
    private global::BethesdaMultitool.WorldSpatialIndex? _spatialIndex;
    private global::BethesdaMultitool.WorldRenderCache? _renderCache;
    private bool _disposed;

    // 3D-8: when Render(deferBlended: true) is used, the blended (transparent) reference submeshes are
    // NOT drawn inside Render — the caller invokes RenderBlendedDeferred() after the water pass so water
    // never paints over them. We stash the per-frame CBV address + frame index because the water pass
    // rebinds PerFrameCbv (its own uniforms) and the DSV, which RenderBlendedDeferred must re-establish.
    private ulong _deferredPerFrameCbvAddress;
    private int _deferredFrameIndex;
    private uint _deferredSceneDepthIndex = NoSceneDepth;
    private float _deferredSceneDepthNear;
    private float _deferredSceneDepthFar;
    private int _deferredSceneDepthSampleCount;

    public ReferenceRenderer12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuRingBuffer12 ringBuffer,
        GpuRootSignature12 rootSignature,
        GpuDescriptorHeapAllocator12 cbvSrvUavHeap,
        ReferenceMeshCache12 meshCache,
        ReferenceEnabledOverrideStore? enabledOverrides = null)
    {
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _cbvSrvUavHeap = cbvSrvUavHeap;
        _meshCache = meshCache;
        _enabledOverrides = enabledOverrides ?? new ReferenceEnabledOverrideStore();

        _pipelines = new ReferencePipelineFactory12(gpu, rootSignature);
    }

    public int ReferencesDrawnLastFrame { get; private set; }
    public global::BethesdaMultitool.WorldRenderStats LastStats { get; } = new();
    public bool DetailedProfilingEnabled { get; set; }
    public bool ShowInitiallyDisabled { get; set; }
    private int _placedLightCount;
    private bool _fnvActiveAdtLightingEnabled;
    private bool _fnvProjectedSunShadowActive;
    private bool _fnvActiveAdtFogEnabled;

    /// <summary>
    ///     Sets the number of point lights uploaded to t9 for the current scene pass. Active retail
    ///     ID193/BSSM_ADT is exact only at zero: any positive world list fails closed because retail
    ///     associates and orders non-shadow lights per geometry rather than camera-globally.
    /// </summary>
    public void SetPlacedLightCount(int count) => _placedLightCount = Math.Max(count, 0);

    /// <summary>
    ///     Supplies the global gates for the bounded active ID193/BSSM_ADT path. SLS2000 has no
    ///     projected-shadow sampler, and the viewer has not yet reproduced its per-vertex fog
    ///     interpolator, so either active feature selects the combined fallback.
    /// </summary>
    public void SetFnvActiveAdtBaseState(
        bool lightingEnabled,
        bool projectedSunShadowActive,
        bool fogEnabled)
    {
        _fnvActiveAdtLightingEnabled = lightingEnabled;
        _fnvProjectedSunShadowActive = projectedSunShadowActive;
        _fnvActiveAdtFogEnabled = fogEnabled;
    }

    /// <summary>
    ///     Selects the R32 scene-depth SRV used by eligible effects in the next deferred blended
    ///     pass. The host must transition the resource to DepthRead|PixelShaderResource and bind
    ///     its read-only DSV before <see cref="RenderBlendedDeferred" />. <see cref="NoSceneDepth" />
    ///     retains the established hardware-only path.
    /// </summary>
    public void SetSceneDepth(uint depthBindlessIndex, float nearPlane, float farPlane, int sampleCount = 1)
    {
        if (depthBindlessIndex == NoSceneDepth || !float.IsFinite(nearPlane) ||
            !float.IsFinite(farPlane) || nearPlane <= 0f || farPlane <= nearPlane || sampleCount < 1)
        {
            _deferredSceneDepthIndex = NoSceneDepth;
            _deferredSceneDepthNear = 0f;
            _deferredSceneDepthFar = 0f;
            _deferredSceneDepthSampleCount = 0;
            return;
        }

        _deferredSceneDepthIndex = depthBindlessIndex;
        _deferredSceneDepthNear = nearPlane;
        _deferredSceneDepthFar = farPlane;
        _deferredSceneDepthSampleCount = sampleCount;
    }

    /// <summary>
    ///     When <c>false</c> (default), engine "marker" objects (XMarker/heading, map/travel/door
    ///     markers, encounter/idle markers) are culled — matching the game, which never renders them
    ///     in play. Driven by <c>RenderableReference.IsMarker</c> (model-path classification).
    /// </summary>
    public bool ShowMarkers { get; set; }

    /// <summary>
    ///     LAND/GRAS vegetation visibility. On by default; filtered before mesh resolution so an
    ///     off toggle avoids decoding and uploading grass NIFs.
    /// </summary>
    public bool ShowGrass { get; set; } = true;

    /// <summary>
    ///     When <c>false</c> (default), imposter references (low-detail distant LOD stand-ins) are
    ///     culled. In a full-detail render the whole worldspace is loaded, so the real STAT/SCOL
    ///     geometry each imposter stands in for is already present — matching the engine, which hides
    ///     the imposter once that geometry's region loads. Driven by <c>RenderableReference.IsImposter</c>.
    /// </summary>
    public bool ShowImposters { get; set; }

    // Per-category visibility filter. References whose RenderableReference.Category is in this set are
    // culled (e.g. activators hidden by the user in the 3D view; the 2D legend's hidden set for the
    // top-down overlay). Mutated only via SetHiddenCategories, which invalidates the cull cache.
    private readonly HashSet<PlacedObjectCategory> _hiddenCategories = [];

    /// <summary>
    ///     Replaces the set of <see cref="PlacedObjectCategory" /> values whose references are hidden.
    ///     Invalidates the cull cache so the next frame re-filters. Pass an empty set to show all.
    /// </summary>
    public void SetHiddenCategories(IEnumerable<PlacedObjectCategory> hidden)
    {
        _hiddenCategories.Clear();
        foreach (var category in hidden) _hiddenCategories.Add(category);
        _cullCacheValid = false;
    }

    /// <summary>
    ///     Number of per-draw allocations that didn't fit the shared ring buffer this frame and were
    ///     skipped (instead of throwing + abandoning the whole frame). Non-zero means the scene is
    ///     denser than the ring can hold in one frame — raise <c>FALLOUT_VIEWER_RING_BUFFER_MB</c>.
    ///     Surfaced in the HUD so the soft cap is visible rather than silent.
    /// </summary>
    public int LastFrameDrawsTruncated { get; private set; }

    /// <summary>
    ///     World-space authored water geometry detected on placed-reference NIFs (cave/pool/
    ///     reflecting-pool water). Accumulates as those references' meshes stream in; the host hands
    ///     this to <see cref="WaterRenderer12.SetNifWaterPlanes" /> so placed water renders with the real
    ///     water shader instead of a flat slab. The list reference is stable across a worldspace load
    ///     (cleared, not reallocated, in <see cref="LoadData" />).
    /// </summary>
    public IReadOnlyList<NifWaterGeometry> NifWaterPlanes => _nifWaterPlanes;

    private bool _streamingThrottled = true;

    /// <summary>
    ///     When <c>false</c>, the per-frame mesh-streaming budget (upload count + time + bytes, decode
    ///     starts + concurrency) is lifted so a single <see cref="Render(Matrix4x4, VisibilityCylinder)"/> loads everything visible
    ///     instead of the live loop's smoothness-preserving trickle. Used by the on-demand top-down
    ///     overlay, which has no framerate target; the live 60fps loop leaves it <c>true</c>.
    ///     Forwards to the shared <see cref="ReferenceMeshCache12.StreamingThrottled"/>.
    /// </summary>
    public bool StreamingThrottled
    {
        get => _streamingThrottled;
        set
        {
            _streamingThrottled = value;
            _meshCache.StreamingThrottled = value;
        }
    }

    public void LoadData(
        global::BethesdaMultitool.WorldRenderCache renderCache,
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::BethesdaMultitool.WorldSpatialIndex? spatialIndex)
    {
        _renderCache = renderCache;
        _cells = cells;
        _spatialIndex = spatialIndex;
        _tallGrassWindSupported = FnvTallGrassWind.IsSupported(renderCache.Game);
        _grassDistanceEnvelope = GrassScatterProfile.ForGame(renderCache.Game).DistanceEnvelope;
        _classicSpecularLodProfile = ClassicSpecularLodProfile.ForGame(renderCache.Game);
        // Per-game grass shaders. Resolved here — beside the other ForGame registries — because this
        // is where the loaded game first reaches the renderer; the pipeline factory is constructed
        // earlier, so its grass PSOs are built lazily on first grass draw.
        _pipelines.SetGrassShaderProfile(GrassShaderProfile.ForGame(renderCache.Game));
        if (_grassDistanceEnvelope.Enabled)
        {
            Core.Diagnostics.Logger.Instance.Info(
                "ReferenceRenderer12: grass distance envelope start={0} range={1} hardEnd={2}; " +
                "the active horizontal render radius may cap the hard end.",
                _grassDistanceEnvelope.FadeStart,
                _grassDistanceEnvelope.FadeRange,
                _grassDistanceEnvelope.HardEnd);
        }
        // The reference set + spatial index changed — the cached cull survivors are stale, and so
        // are any frozen batches built from them.
        _cullCacheValid = false;
        _lastBuildValid = false;
        // Drop the accumulated NIF water geometry so it doesn't leak across worldspace loads. Cleared
        // (not reallocated) so the reference the host handed WaterRenderer12 stays valid.
        _nifWaterPlanes.Clear();
        _nifWaterPlaneSeen.Clear();

        // Size the resident-mesh LRU to this worldspace's distinct-mesh working set so it holds the
        // whole set rather than thrashing (evict → re-decode → re-upload) when the count exceeds the
        // default cap. No-op when the capacity env knob pins a fixed cap (auto-size off).
        var uniqueMeshKeys = CountUniqueMeshKeys(renderCache, cells);
        var plannedCapacity = Core.Resources.ReferenceMeshCapacityPlanner.Plan(uniqueMeshKeys);
        Core.Diagnostics.Logger.Instance.Info(
            "ReferenceRenderer12: {0} distinct mesh keys (paths × re-skin variants) → mesh LRU capacity {1}.",
            uniqueMeshKeys, plannedCapacity);
        _meshCache.SetMeshCapacity(plannedCapacity);
    }

    /// <summary>
    ///     Applies the bounded active-retail ID193/BSSM_ADT identity at submission time. The decoded
    ///     PS1-family mode is reused only as a conservative ordinary-static material classifier;
    ///     no dormant SLS1009/SLS1013 pass is activated or counted.
    /// </summary>
    private Vector4 ResolveTextureState(CachedSubmesh12 submesh)
    {
        var state = submesh.TextureState;
        if (_renderCache?.Game == Core.Games.BethesdaGame.FalloutNewVegas)
        {
            var eligibility = ResolveFnvActiveAdtBaseEligibility(submesh);
            state.Z = FnvActiveAdtBasePolicy.ApplyRuntimeFlags(
                eligibility,
                (uint)MathF.Round(state.Z));
        }
        return state;
    }

    private FnvActiveAdtBaseEligibility ResolveFnvActiveAdtBaseEligibility(CachedSubmesh12 submesh) =>
        new(
            _renderCache?.Game ?? Core.Games.BethesdaGame.Unknown,
            _fnvActiveAdtLightingEnabled,
            _placedLightCount,
            _fnvProjectedSunShadowActive,
            _fnvActiveAdtFogEnabled,
            submesh.AlphaBlend,
            submesh.AlphaTest,
            submesh.MaterialAlpha,
            submesh.MaterialAlphaController is not null,
            submesh.ClassicBasicShaderMode);

    private string? ResolveFnvActiveAdtBaseFallbackReason()
    {
        if (_renderCache?.Game != Core.Games.BethesdaGame.FalloutNewVegas)
        {
            return null;
        }

        if (!_fnvActiveAdtLightingEnabled)
        {
            return "lighting-disabled";
        }

        if (_placedLightCount > 0)
        {
            return "per-geometry-local-light-selection-unrecovered";
        }

        if (_fnvProjectedSunShadowActive)
        {
            return "projected-shadow-permutation-unrecovered";
        }

        return _fnvActiveAdtFogEnabled
            ? "per-vertex-fog-interpolator-unrecovered"
            : null;
    }

    /// <summary>
    ///     Records color-pass submissions that selected a recovered route or hit its explicit
    ///     fail-closed fallback. The decoded mode alone is not sufficient: BS34 assets are shared
    ///     with Fallout 3, where <see cref="ResolveTextureState" /> intentionally leaves both route
    ///     and FNV fallback telemetry disabled.
    /// </summary>
    private void ObserveFnvActiveAdtBaseDraw(
        CachedSubmesh12 submesh,
        Vector4 textureState,
        int instanceCount)
    {
        if (instanceCount <= 0 ||
            _renderCache?.Game != Core.Games.BethesdaGame.FalloutNewVegas)
        {
            return;
        }

        var flags = (uint)MathF.Round(textureState.Z);
        if (submesh.ClassicBasicShaderMode == FnvClassicBasicShaderMode.None)
        {
            return;
        }

        if ((flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtFlag) == 0)
        {
            LastStats.ReferenceFnvActiveAdtBaseFallbackDraws++;
            LastStats.ReferenceFnvActiveAdtBaseFallbackInstances += instanceCount;
            LastStats.ReferenceFnvClassicBasicFallbackDraws++;
            LastStats.ReferenceFnvClassicBasicFallbackInstances += instanceCount;
            var reason = ResolveFnvActiveAdtBaseFallbackReason()
                ?? "outside-active-adt-base-subset";
            LastStats.ReferenceFnvActiveAdtBaseFallbackReason ??= reason;
            LastStats.ReferenceFnvClassicBasicFallbackReason ??= reason;
            return;
        }

        LastStats.ReferenceFnvActiveAdtBaseDraws++;
        LastStats.ReferenceFnvActiveAdtBaseInstances += instanceCount;
        if ((flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtVertexColorFlag) != 0)
        {
            LastStats.ReferenceFnvActiveAdtBaseVertexColorDraws++;
            LastStats.ReferenceFnvActiveAdtBaseVertexColorInstances += instanceCount;
        }
    }

    // O(total placements), one-time per worldspace load — negligible next to the rest of load.
    // OrdinalIgnoreCase matches the mesh LRU's key comparer so the count tracks real cache keys.
    // Counts distinct KEYS, not paths: a re-skinned placement (REFR XMSP / base-record default swap
    // / FNV alternate textures) decodes as its own `path#variant` cache entry, and on FO4 — where
    // base-record swaps are ubiquitous — variants can multiply the key space well past the plain
    // path count. Sizing on paths alone under-provisioned the LRU there, and the resulting evict →
    // re-decode → re-upload thrash presented as meshes disappearing in dense areas. The (alt-texture
    // base, swap FormID) pair is a cheap stand-in for the exact VariantKey: it can only OVER-count
    // (two swaps yielding identical tables), which errs on the safe side for capacity.
    private static int CountUniqueMeshKeys(
        global::BethesdaMultitool.WorldRenderCache renderCache,
        Dictionary<(int gx, int gy), CellRecord> cells)
    {
        var altTextureIndex = renderCache.AlternateTextureIndex;
        var swapIndex = renderCache.MaterialSwapIndex;
        var baseSwapIndex = renderCache.BaseMaterialSwapIndex;
        var seen = new HashSet<(string Path, uint AltBase, uint Swap)>();
        foreach (var cell in cells.Values)
        {
            foreach (var placed in cell.PlacedObjects)
            {
                if (string.IsNullOrEmpty(placed.ModelPath))
                {
                    continue;
                }

                var altBase = altTextureIndex is not null && altTextureIndex.ContainsKey(placed.BaseFormId)
                    ? placed.BaseFormId
                    : 0u;
                var swapFormId = placed.MaterialSwapFormId ?? 0u;
                if (swapFormId == 0 && baseSwapIndex is not null &&
                    baseSwapIndex.TryGetValue(placed.BaseFormId, out var baseSwap))
                {
                    swapFormId = baseSwap;
                }

                // A swap FormID only creates a variant when it resolves to a real MSWP table.
                if (swapFormId != 0 && (swapIndex is null || !swapIndex.ContainsKey(swapFormId)))
                {
                    swapFormId = 0;
                }

                seen.Add((placed.ModelPath.ToLowerInvariant(), altBase, swapFormId));
            }
        }

        return seen.Count;
    }

    /// <summary>
    ///     Sets the camera world-space right/up basis used to re-face SpeedTree leaf cards to the camera
    ///     in the leaf-billboard vertex shader. The host computes it from the inverse view matrix (the
    ///     same source as <c>SkyBillboardRenderer12</c>) and calls this each frame before <see cref="Render(Matrix4x4, VisibilityCylinder, bool, Matrix4x4?, Vector3, CullCameraPose?, Vector3?, Vector3?)" />.
    /// </summary>
    public void SetLeafBillboardBasis(Vector3 cameraRight, Vector3 cameraUp)
    {
        // .W = LeafLighting.y normal-puff strength (see the field comment) — must survive every update.
        _leafBillboardRight = new Vector4(cameraRight, LeafLightingAdjust);
        _leafBillboardUp = new Vector4(cameraUp, 0f);
    }

    /// <summary>
    ///     Sets the SpeedTree wind state used by the leaf-billboard vertex shader to sway leaf cards
    ///     Engine leaf rock/rustle (BSTreeManager::UpdateWindMatrices + SpeedTreeLeafShader
    ///     wind timers, ported in <see cref="Core.Formats.SpeedTree.SpeedTreeWindRig" />). The
    ///     shader-facing uWind packs (rockAmount, rockPhase, rustleAmount, rustlePhase) — both
    ///     engine <c>RockParams.z</c>/<c>RustleParams.z</c> scalars are constructor-1.0, so they
    ///     need no uniform. <paramref name="direction" /> is unused by both recovered models
    ///     (TallGrass uses fixed world +Y), but remains for signature stability and future consumers;
    ///     <paramref name="strength" /> = the weather wind-speed byte / 255 (0 is SpeedTree rest;
    ///     TallGrass still applies its recovered minimum magnitude);
    ///     <paramref name="timeSeconds" /> the animation clock. Call each frame before
    ///     <see cref="Render(Matrix4x4, VisibilityCylinder, bool, Matrix4x4?, Vector3, CullCameraPose?, Vector3?, Vector3?)" />.
    /// </summary>
    public void SetWind(Vector2 direction, float strength, float timeSeconds)
    {
        _ = direction;
        _captureWindActive = false;
        _windStrength = float.IsFinite(strength) ? Math.Clamp(strength, 0f, 1f) : 0f;
        _animationClockSeconds = float.IsFinite(timeSeconds) ? timeSeconds : 0f;
        _windRig.Tick(_windStrength, (float)_animationClockSeconds);
        _wind = new Vector4(_windRig.RockAmount, _windRig.RockPhase, _windRig.RustleAmount, _windRig.RustlePhase);
    }

    /// <summary>
    ///     Selects a deterministic profiler-capture pose without mutating the live history-driven wind
    ///     rig. The capture rig replays constant current wind from rest at its canonical fixed cadence.
    /// </summary>
    public void SetWindForCapture(Vector2 direction, float strength, float timeSeconds)
    {
        _ = direction;
        _windStrength = float.IsFinite(strength) ? Math.Clamp(strength, 0f, 1f) : 0f;
        _animationClockSeconds = float.IsFinite(timeSeconds) ? timeSeconds : 0f;
        _captureWindRig.ResetAndReplayConstantWind(_windStrength, (float)_animationClockSeconds);
        _captureWindActive = true;
        _wind = new Vector4(
            _captureWindRig.RockAmount,
            _captureWindRig.RockPhase,
            _captureWindRig.RustleAmount,
            _captureWindRig.RustlePhase);
    }

    /// <summary>
    ///     Per-game <c>fLeaf*</c> wind settings for the rig (Oblivion ships them as esm GMSTs with
    ///     values that change behavior — steady amounts, slower rock; FNV/FO3 run compiled
    ///     defaults). Idempotent; call whenever the loaded game is known.
    /// </summary>
    public void SetWindProfile(Core.Formats.SpeedTree.SpeedTreeWindProfile profile)
    {
        _windRig.Profile = profile;
        _captureWindRig.Profile = profile;
    }

    /// <summary>
    ///     Camera pose key for the translation-TOLERANT cull cache — see the
    ///     <c>cullCameraPose</c> parameter docs on the main <c>Render</c> overload.
    /// </summary>
    public readonly record struct CullCameraPose(Vector3 Forward, float FovYRadians, float Aspect);

    /// <summary>Max camera drift (world units, Chebyshev) the tolerant cull cache absorbs.</summary>
    private const float CullPositionSlack = 512f;

    /// <summary>
    ///     Tolerant-mode debounce for the mesh-bounds generation: a NEW mesh resolve tightens cull
    ///     spheres, which the exact path treats as an immediate invalidation — but during active
    ///     streaming that means a re-cull EVERY frame (each frame resolves something), which is
    ///     precisely the 6-7ms/frame cost this cache exists to kill. Bounds tightening is a visual
    ///     nicety (edge refs pop in with the corrected sphere), not a safety property — the widened
    ///     establishment cull keeps everything already visible — so the tolerant path refreshes for
    ///     it at most once per this many frames (~0.5s at 60fps).
    /// </summary>
    private const int CullStreamingRefreshFrames = 30;

    private CullCameraPose? _cullCachePose;
    private Vector3 _cullCachePosition;
    private float _cullCacheRadius;
    private int _framesSinceCull;

    /// <summary>Bumped on every fresh cull — identifies the survivor-set generation (batch reuse keys on it).</summary>
    private int _cullEpoch;

    // === Batch reuse on quiesced frames ===
    // When the cull cache hit, the render origin (snapped by the host) is unchanged, and the LAST
    // build frame streamed nothing (quiescent: every mesh + texture resolved, zero uploads/misses,
    // hence zero evictions mid-build), the resolve+batch pass is skipped wholesale and the frozen
    // batches re-draw. Safety rails: any resident-mesh eviction bumps the cache's
    // EvictionGeneration (frozen batches reference CachedSubmesh12 GPU buffers), and the draw pass
    // re-tests every instance against the current exact frustum (PassesExactCull), so a frozen
    // WIDENED batch stays visually exact while the camera drifts inside the cull slack.
    private static readonly bool BatchReuseEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.BatchReuse) != "0";

    /// <summary>
    ///     Ceiling on consecutive reuse frames (~1s at 60fps). Reuse frames never call GetOrUpload,
    ///     so a background decode that completes AFTER the build (e.g. one of the perpetually
    ///     retrying unresolvable meshes finally succeeding) has no path into the frozen batches;
    ///     the periodic rebuild reissues every survivor's resolve, picks up anything that landed,
    ///     and re-freezes. Also throttles the steady-state decode-retry churn of permanently
    ///     missing meshes from per-frame to once per rebuild.
    /// </summary>
    private const int BatchReuseMaxFrames = 60;

    /// <summary>
    ///     Consecutive QUIET build frames (no uploads, no cache misses, no pending textures)
    ///     required before reuse engages. A single quiet frame is not proof the scene finished
    ///     filling — decode waves have sub-second lulls, and freezing on one stalled the fill at
    ///     ~80% (drawn froze while 12k meshes were still decoding). A half-second streak is.
    /// </summary>
    private const int QuietBuildStreakFrames = 30;

    private bool _lastBuildValid;
    private int _framesSinceBuild;
    // This frame's reuse decision, captured for the geometry draw validator's violation context.
    private bool _frameReusedBatches;
    private int _quietBuildStreak;
    private int _lastBuildCullEpoch;
    private Vector3 _lastBuildRenderOrigin;
    private bool _lastBuildQuiesced;
    private int _lastBuildEvictionGen;
    private int _lastBuildDrawn;
    private int _lastBuildMissing;
    private int _lastBuildTexturePending;

    // True while the CURRENT batch content came from a widened (tolerant) cull — the draw pass
    // must then apply the per-instance exact refilter. Set on build frames, carried across reuse.
    private bool _batchesWidened;

    // Current frame's exact cull context for PassesExactCull (set every Render before the draws).
    private Frustum _frameFrustum;
    private bool _frameRefilterActive;
    private float _frameCylinderX;
    private float _frameCylinderY;
    private float _frameCylinderRadius;
    private Vector3 _frameCameraPosition;
    private Vector3 _frameRenderOrigin;
    private float _frameSmallPropCutoffSq;

    // IWorldRenderer entry point — draws opaque + blended inline (no deferral). Used by the 2D
    // top-down overlay, which has no water pass.
    public int Render(Matrix4x4 viewProj, VisibilityCylinder cylinder)
        => Render(viewProj, cylinder, deferBlended: false, cameraForward: -Vector3.UnitZ);

    /// <summary>
    ///     Draws the visible reference submeshes for this frame — opaque always, blended either inline or
    ///     deferred until after the water pass — and returns the number of references drawn.
    /// </summary>
    /// <param name="deferBlended">
    ///     When true, the blended (transparent) reference submeshes are NOT drawn in this call — the
    ///     caller must invoke <see cref="RenderBlendedDeferred" /> after the water pass so water does
    ///     not paint over them (3D-8). False draws opaque + blended inline.
    /// </param>
    /// <param name="cullViewProj">
    ///     The <b>world-space (absolute)</b> view·projection used to derive the CPU culling frustum.
    ///     When camera-relative rendering is on, <paramref name="viewProj" /> is translation-free (its
    ///     frustum apex sits at the world origin), but the cull geometry — <c>WorldMatrix.Translation</c>,
    ///     bounds centers, bucket AABBs — is in absolute world space. Building the frustum from the
    ///     translation-free matrix would only keep objects roughly along the camera's forward ray from
    ///     the origin, culling everything else (the "only visible facing one direction" bug). Pass the
    ///     absolute viewProj so the frustum matches the cull geometry's space. Null ⇒ use
    ///     <paramref name="viewProj" /> (correct when it is already absolute, e.g. the top-down overlay).
    /// </param>
    /// <param name="renderOrigin">
    ///     The world-space point <paramref name="viewProj" /> treats as its origin — i.e. the same value
    ///     the caller binds as <c>uCameraOrigin</c> in the shared atmosphere CB (b3). It is folded into
    ///     each uploaded world matrix's TRANSLATION on the CPU so the reference VS projects small
    ///     near-camera coordinates (the reference VS no longer subtracts <c>uCameraOrigin</c> itself);
    ///     this keeps float32 depth precision far from the world origin, killing the coplanar-surface
    ///     Z-fighting on distant architecture. Camera-relative live path passes the camera position;
    ///     the absolute paths (top-down / scene-capture / export / headless, all with
    ///     <c>uCameraOrigin == 0</c>) pass <see cref="Vector3.Zero" /> (the default), making the fold a
    ///     no-op. The CPU cull below stays in ABSOLUTE space (uses <c>r.WorldMatrix</c>, not the folded
    ///     copy) — only the GPU upload is shifted.
    /// </param>
    /// <param name="cullCameraPose">
    ///     Camera pose key for the translation-TOLERANT cull cache. When supplied, the cached cull
    ///     survivors stay valid while the camera drifts up to <see cref="CullPositionSlack" /> from
    ///     the establishment position with an unchanged view basis/FOV/aspect — instead of requiring
    ///     a byte-identical viewProj, which NEVER matches while walking (the far plane is recomputed
    ///     from |camera.Z| every frame, so the old exact compare re-culled ~100k candidates per walk
    ///     frame). The establishment cull widens every bound by the slack so the survivor set is a
    ///     SUPERSET of the exact set for any camera inside the slack box; the draw passes re-test
    ///     each instance against the current exact bounds, so the drawn set stays exact.
    /// </param>
    /// <param name="cameraPosition">
    ///     The actual world-space rendering eye used for blended ordering, billboard facing, particle
    ///     quad sorting, and runtime SpeedTree LOD. Null falls back to <paramref name="cylinder" />'s
    ///     position, which is exact for the perspective and legacy top-down paths. Tilted orthographic
    ///     views must pass their look-at eye because their covering cylinder is centered on the visible
    ///     footprint rather than on that eye.
    /// </param>
    /// <param name="cameraForward">
    ///     The actual normalized view direction used by face-camera billboard modes. This is independent
    ///     of <paramref name="cullCameraPose" />: exact-cull, capture, projection, and export paths still
    ///     need the correct facing even when they deliberately do not use the tolerant cull cache.
    /// </param>
    public int Render(
        Matrix4x4 viewProj, VisibilityCylinder cylinder, bool deferBlended, Matrix4x4? cullViewProj = null,
        Vector3 renderOrigin = default, CullCameraPose? cullCameraPose = null,
        Vector3? cameraPosition = null, Vector3? cameraForward = null)
    {
        ReferencesDrawnLastFrame = 0;
        LastFrameDrawsTruncated = 0;
        LastStats.Reset();
        _tallGrassWaveMultipliers.Clear();
        var frameWindRig = _captureWindActive ? _captureWindRig : _windRig;
        LastStats.ReferenceSpeedTreeWindStrength = _windStrength;
        LastStats.ReferenceSpeedTreeRockAmount = frameWindRig.RockAmount;
        LastStats.ReferenceSpeedTreeRockPhase = frameWindRig.RockPhase;
        LastStats.ReferenceSpeedTreeRustleAmount = frameWindRig.RustleAmount;
        LastStats.ReferenceSpeedTreeRustlePhase = frameWindRig.RustlePhase;
        LastStats.ReferenceSpeedTreeAnimationSeconds = (float)_animationClockSeconds;
        LastStats.ReferenceSpeedTreeRuntimeLodEnabled = SpeedTreeRuntimeLod.Enabled;
        LastStats.ReferenceTallGrassWindSupported = _tallGrassWindSupported;
        LastStats.ReferencePlacedLightCount = _placedLightCount;
        var isFalloutNewVegas =
            _renderCache?.Game == Core.Games.BethesdaGame.FalloutNewVegas;
        // The dormant PS1 route remains off even when the active SLS2000 subset is enabled.
        LastStats.ReferenceFnvClassicBasicLightingEnabled = false;
        LastStats.ReferenceFnvActiveAdtBaseEnabled =
            isFalloutNewVegas &&
            _fnvActiveAdtLightingEnabled &&
            _placedLightCount == 0 &&
            !_fnvProjectedSunShadowActive &&
            !_fnvActiveAdtFogEnabled;
        var activeAdtFallbackReason = ResolveFnvActiveAdtBaseFallbackReason();
        LastStats.ReferenceFnvActiveAdtBaseFallbackReason = activeAdtFallbackReason;
        // Preserve the historical candidate/fallback diagnostic keys for existing traces while
        // naming the actually submitted route with the active-ADT counters above.
        LastStats.ReferenceFnvClassicBasicFallbackReason = activeAdtFallbackReason;
        if (_tallGrassWindSupported)
        {
            LastStats.ReferenceTallGrassAnimationsEnabled = AnimationsEnabled;
            LastStats.ReferenceTallGrassNormalizedStrength = _windStrength;
            LastStats.ReferenceTallGrassMagnitudeWorldUnits = AnimationsEnabled
                ? FnvTallGrassWind.ComputeWindMagnitude(_windStrength)
                : 0f;
            LastStats.ReferenceTallGrassDirectionX = Vector2.UnitY.X;
            LastStats.ReferenceTallGrassDirectionY = Vector2.UnitY.Y;
            LastStats.ReferenceTallGrassAnimationSeconds = AnimationsEnabled
                ? (float)_animationClockSeconds
                : 0f;
        }
        // Hand the cache its per-frame upload-time allowance BEFORE ResetFrameStats snapshots it.
        _meshCache.UploadMillisecondsPerFrame = MaxUploadMillisecondsPerFrame;
        _meshCache.ResetFrameStats();
        if (_cells is null || _cells.Count == 0 || _renderCache is null) return 0;

        var started = StartTiming();
        var frameCameraPosition = cameraPosition ?? cylinder.Position;
        var frameCameraForward = cameraForward ?? Vector3.Zero;
        var cmd = _recorder.CommandList;
        var frameIndex = _recorder.FrameIndex;

        var segmentStarted = StartTiming();
        // Soft-fail on ring exhaustion (dense whole-map frames): skip the reference pass this
        // frame instead of throwing and killing the render loop. Zeroing the deferred CBV makes
        // RenderBlendedDeferred a no-op too (its state would otherwise be stale this frame).
        if (!_ringBuffer.TryAllocate(frameIndex, PerFrameByteSize, out var perFrameAlloc, GpuRingBuffer12.CbAlignment))
        {
            _deferredPerFrameCbvAddress = 0;
            return 0;
        }
        unsafe
        {
            *(Matrix4x4*)perFrameAlloc.CpuPtr = viewProj;
            // Wind v2: the 4 slow sway matrices (identity at S = 0, so a calm frame is byte-static).
            var windDst = (Matrix4x4*)((byte*)perFrameAlloc.CpuPtr + 64);
            for (var w = 0; w < 4; w++)
            {
                windDst[w] = frameWindRig.WindMatrix(w);
            }
        }
        _deferredPerFrameCbvAddress = perFrameAlloc.GpuAddress;
        _deferredFrameIndex = frameIndex;

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        // Bindless: bind the unbounded SRV table once per frame to heap slot 0
        // (the persistent region's start). All bindless `textures[index]` accesses by the
        // PS resolve through this single binding for the rest of the frame.
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);
        LastStats.ReferenceStateSetupMilliseconds = ElapsedMilliseconds(segmentStarted);

        var drawn = 0;
        var throttled = _streamingThrottled;
        var uploadBudget = throttled ? MaxNewUploadsPerFrame : int.MaxValue;
        var cylinderRadius = cylinder.Radius;
        var cylinderX = cylinder.Position.X;
        var cylinderY = cylinder.Position.Y;
        var hasFrustum = !_disableReferenceFrustum;
        // Cull in WORLD space: the frustum must come from the absolute viewProj (apex at the camera,
        // not the origin) because every cull test below is against absolute coordinates. The
        // camera-relative `viewProj` is uploaded to the GPU CB above (the VS subtracts CameraOrigin),
        // but it must NOT drive the frustum — see the cullViewProj parameter docs.
        var cullFrustumSource = cullViewProj ?? viewProj;
        var frustum = hasFrustum ? Frustum.FromViewProjection(cullFrustumSource) : default;
        // Shadow caster ring in effect for THIS render pass (0 when the shadow capture isn't
        // armed — top-down/export/capture paths cull without shadow-only augmentation). While a
        // ring is active the BROADPHASE must not frustum-filter, or off-screen casters never reach
        // the per-REFR loop that collects them; the per-REFR frustum test still trims the main set.
        var shadowRing = _shadowCaptureArmed ? _shadowCasterRingRadius : 0f;
        Frustum? broadphaseFrustum = hasFrustum && shadowRing <= 0f ? frustum : null;

        double cullMs = 0;
        double meshUploadMs = 0;
        double cbUpdateMs = 0;
        double srvBindMs = 0;
        double drawCallMs = 0;
        int cellsVisited = 0;
        int candidates = 0;
        int culled = 0;
        int missingMeshes = 0;
        int texturePending = 0;
        int referencesWithReadyMesh = 0;
        int submeshDraws = 0;
        int srvBinds = 0;

        // === Cull pass (or cache reuse) ===
        // The cull (per-cell broadphase + per-REFR filter/sphere/frustum tests) is reused from the
        // previous frame when the camera pose, the mesh-bounds generation (the count of resolved
        // meshes — each new resolve can tighten a cull sphere), and the visibility filters are ALL
        // unchanged. With a CullCameraPose the compare is translation-TOLERANT (see the type docs):
        // same basis/FOV/aspect + drift under CullPositionSlack reuses the widened establishment
        // cull, so walking a straight line stops re-testing ~100k candidates per frame. Without a
        // pose (top-down / capture paths) the compare stays byte-exact on the viewProj.
        var tolerant = cullCameraPose is not null;
        _framesSinceCull++;
        var meshBoundsCurrent = _cullCacheMeshRadiusCount == _meshLocalRadius.Count
                                || (tolerant && _framesSinceCull < CullStreamingRefreshFrames);
#pragma warning disable S1244 // cull-cache identity: exact compare of a cached snapshot detects change, not proximity
        var cullCacheValid =
            _cullCacheValid
            && meshBoundsCurrent
            && _cullCacheShowMarkers == ShowMarkers
            && _cullCacheShowGrass == ShowGrass
            && _cullCacheShowImposters == ShowImposters
            && _cullCacheShowDisabled == ShowInitiallyDisabled
            && _cullCacheEnabledOverrideVersion == _enabledOverrides.Version
            && _cullCacheShadowRing == shadowRing
            && (tolerant
                ? _cullCachePose is not null
                  && _cullCachePose.Value == cullCameraPose!.Value
                  && _cullCacheRadius == cylinder.Radius
                  && ChebyshevWithin(cylinder.Position, _cullCachePosition, CullPositionSlack)
                : _cullCacheViewProj == cullFrustumSource && _cullCacheCylinder == cylinder);
#pragma warning restore S1244

        // === Batch reuse decision ===
        // cullCacheValid ⇒ no re-cull this frame ⇒ _cullEpoch is stable, so an epoch match means
        // the frozen batches were built from exactly the survivor set this frame would iterate.
        _framesSinceBuild++;
        var reuseBatches = BatchReuseEnabled && cullCacheValid
                           && _lastBuildValid
                           && _framesSinceBuild < BatchReuseMaxFrames
                           && _lastBuildCullEpoch == _cullEpoch
                           && _lastBuildRenderOrigin == renderOrigin
                           && _lastBuildQuiesced
                           && _lastBuildEvictionGen == _meshCache.EvictionGeneration;
        _frameReusedBatches = reuseBatches;
        if (!reuseBatches)
        {
            _opaqueBatches.Begin();
            _blendedDraws.Clear();
            _depthWritingBlendDraws.Clear();
            _resolvedMeshesThisFrame.Clear();
            unchecked { BatchContentVersion++; } // content may change → shadow map re-renders
        }

        if (cullCacheValid)
        {
            // Restore the cull HUD counters so they don't read zero while the survivor set is reused.
            cellsVisited = _cullCacheCellsVisited;
            candidates = _cullCacheCandidates;
            culled = _cullCacheCulled;
        }
        else
        {
            var cullStarted = StartTiming();
            _cachedCullSurvivors.Clear();
            _cachedShadowOnlyCasters.Clear();
            // Tolerant mode widens every cull bound by the drift slack so the survivor set is a
            // SUPERSET of the exact set for any camera position within CullPositionSlack of the
            // establishment position: √2× for the square footprint's corner reach, √3× for the 3D
            // frustum drift, 1× for the per-axis Chebyshev test and the small-prop LOD ring.
            var driftSlack = tolerant ? CullPositionSlack : 0f;
            var frustumSlack = tolerant ? CullPositionSlack * 1.7321f : 0f;
            var cullCylinder = tolerant
                ? new VisibilityCylinder(cylinder.Position, cylinder.Radius + CullPositionSlack)
                : cylinder;
            var smallPropCutoff = (cylinderRadius * SmallPropDistanceFraction) + driftSlack;
            var smallPropCutoffSq = smallPropCutoff * smallPropCutoff;
            foreach (var cell in EnumerateVisibleCells(cullCylinder))
            {
                _cullCandidateScratch.Clear();
                var totalPlacements = _renderCache.QueryPlacementCandidates(
                    cell,
                    cylinderX,
                    cylinderY,
                    (cylinderRadius * SquareBroadphaseFactor) + (driftSlack * 1.41422f), // corner reach + drift
                    broadphaseFrustum,
                    FrustumCullMargin + frustumSlack,
                    _cullCandidateScratch);
                if (totalPlacements == 0 || _cullCandidateScratch.Count == 0) continue;

                cellsVisited++;
                candidates += _cullCandidateScratch.Count;
                foreach (var r in _cullCandidateScratch)
                {
                    // Authored state combines the REFR's own Initially Disabled flag with its resolved
                    // XESP parent chain. A scene-local per-instance On/Off preview wins over both that
                    // state and the global "show disabled" diagnostic; category/layer gates below stay
                    // independent. Count a hidden state as culled so the HUD reflects the filter.
                    if (!_enabledOverrides.IsVisible(r.FormId, r.IsInitiallyDisabled, ShowInitiallyDisabled))
                    {
                        culled++;
                        continue;
                    }
                    if (!ShowGrass && r.IsGrass)
                    {
                        culled++;
                        continue;
                    }
                    // Hide engine marker objects (XMarker, map/travel markers, etc.) unless toggled on.
                    // The game strips these in play; we match that for a clean render.
                    if (!ShowMarkers && r.IsMarker)
                    {
                        culled++;
                        continue;
                    }
                    // Hide imposter LOD stand-ins. Their real geometry (STAT/SCOL) lives elsewhere in
                    // the same worldspace (placed at a different origin, not exactly co-located), so in
                    // this full-detail render — where the whole worldspace is loaded — every imposter is
                    // redundant and the engine would have hidden it. Toggle back via ShowImposters.
                    if (!ShowImposters && r.IsImposter)
                    {
                        culled++;
                        continue;
                    }
                    // Per-category visibility (user-selected category toggles; 2D legend toggles for the
                    // top-down overlay). The set is empty in the common case so this is one HashSet
                    // count check per candidate.
                    if (_hiddenCategories.Count > 0 && _hiddenCategories.Contains(r.Category))
                    {
                        culled++;
                        continue;
                    }
                    // Cull bounds: once a mesh is resident, use its ACTUAL local bounding sphere (radius
                    // around the NIF origin, scaled into world) instead of the OBND-or-256-fallback baked
                    // at LoadData. The mesh sphere is conservative (contains all geometry) and centered at
                    // the placement point, so large meshes (highways/buildings) with a missing/tiny OBND no
                    // longer get culled at the screen edge or when the camera is close. Falls back to the
                    // OBND sphere until the mesh first resolves (it then self-corrects on later frames).
                    Vector3 cullCenter;
                    float cullRadius;
                    if (_meshLocalRadius.TryGetValue(r.MeshId, out var localRadius))
                    {
                        cullCenter = r.WorldMatrix.Translation;
                        var basisX = new Vector3(r.WorldMatrix.M11, r.WorldMatrix.M12, r.WorldMatrix.M13);
                        cullRadius = localRadius * basisX.Length(); // uniform-scale assumption (FNV refs)
                    }
                    else
                    {
                        cullCenter = r.BoundsCenter;
                        cullRadius = r.BoundsRadius;
                    }
                    var dx = cullCenter.X - cylinderX;
                    var dy = cullCenter.Y - cylinderY;
                    var distSq = dx * dx + dy * dy; // retained for the small-prop distance LOD below
                    // Include the same FrustumCullMargin the frustum test uses, so a reference whose OBND
                    // bounds underestimate its real extent (or whose center sits just outside the square)
                    // is not dropped at the cylinder edge a frame before its true mesh radius resolves.
                    var maxDist = cylinderRadius + cullRadius + FrustumCullMargin + driftSlack;
                    // Square ("Dist") footprint: reject iff the object falls outside the half-extent box
                    // along either axis (Chebyshev), so references load as a square matching the terrain/
                    // grid footprint instead of a circle.
                    var rejected = MathF.Abs(dx) > maxDist || MathF.Abs(dy) > maxDist;
                    // FNV's shipped fGrassStartFadeDistance/fGrassFadeRange describe a hard end at
                    // 8000 world units. Only authored LAND/GRAS placements participate. The
                    // establishment pass widens that end by the same camera-drift slack as the
                    // other tolerant tests; DrawOpaqueBatches removes the slack every frame.
                    if (!rejected && !GrassDistanceCullPolicy.Passes(
                            r.IsGrass,
                            in _grassDistanceEnvelope,
                            HorizontalDistanceSquared(r.WorldMatrix.Translation, cylinderX, cylinderY),
                            cylinderRadius,
                            driftSlack))
                    {
                        rejected = true;
                    }
                    // A5 — distance LOD: drop small props (clutter — barrels, debris, rocks) well
                    // beyond the near field. Shrinks the high-view-distance working set (fewer
                    // decodes/uploads/draws). Large props (cullRadius >= threshold: cars, shacks,
                    // buildings) are never distance-culled so landmark-scale geometry stays visible to
                    // the far plane. Trade-off, opted in: small objects fade out in the distance.
                    if (_enableDistanceLod && !rejected && cullRadius < SmallPropLodRadius && distSq > smallPropCutoffSq)
                    {
                        rejected = true;
                    }
                    // Track the FRUSTUM-only rejection separately: an off-screen ref inside the
                    // shadow caster ring still CASTS (its shadow can land on-screen — without this,
                    // shadows pop when their caster leaves the frustum). Kept in a shadow-only list
                    // the resolve pass batches for the shadow replay; the main draw never sees it.
                    if (!rejected && hasFrustum &&
                        !frustum.IntersectsSphere(cullCenter, cullRadius + FrustumCullMargin + frustumSlack))
                    {
                        rejected = true;
                        var ringReach = shadowRing + cullRadius + driftSlack;
                        if (shadowRing > 0f && MathF.Abs(dx) <= ringReach && MathF.Abs(dy) <= ringReach)
                        {
                            _cachedShadowOnlyCasters.Add(r);
                        }
                    }
                    if (rejected) culled++;
                    else _cachedCullSurvivors.Add(r);
                }
            }
            cullMs = ElapsedMilliseconds(cullStarted);

            // Snapshot the cache key. MeshRadiusCount is captured BEFORE the resolve pass (below) adds
            // this frame's newly-resolved radii, so the next frame re-culls iff a new mesh resolved.
            _cullEpoch++;
            _framesSinceCull = 0;
            _cullCacheValid = true;
            _cullCacheViewProj = cullFrustumSource;
            _cullCacheCylinder = cylinder;
            _cullCachePose = cullCameraPose;
            _cullCachePosition = cylinder.Position;
            _cullCacheRadius = cylinder.Radius;
            _cullCacheMeshRadiusCount = _meshLocalRadius.Count;
            _cullCacheShowMarkers = ShowMarkers;
            _cullCacheShowGrass = ShowGrass;
            _cullCacheShowImposters = ShowImposters;
            _cullCacheShowDisabled = ShowInitiallyDisabled;
            _cullCacheEnabledOverrideVersion = _enabledOverrides.Version;
            _cullCacheShadowRing = shadowRing;
            _cullCacheCellsVisited = cellsVisited;
            _cullCacheCandidates = candidates;
            _cullCacheCulled = culled;
        }

        // === Resolve + batch pass (skipped wholesale on batch-reuse frames) ===
        // Exact (unwidened) small-prop LOD cutoff for the draw passes' per-instance refilter — the
        // tolerant establishment cull's cutoff carries the drift slack, so the batches hold a ring
        // of distance-LOD'd clutter that must not survive the draw-time exact cull.
        var exactSmallPropCutoff = cylinderRadius * SmallPropDistanceFraction;
        var exactSmallPropCutoffSq = exactSmallPropCutoff * exactSmallPropCutoff;
        _frameFrustum = frustum;
        _frameCylinderX = cylinderX;
        _frameCylinderY = cylinderY;
        _frameCylinderRadius = cylinderRadius;
        _frameCameraPosition = frameCameraPosition;
        _frameRenderOrigin = renderOrigin;
        _frameSmallPropCutoffSq = exactSmallPropCutoffSq;
        var meshStarted = StartTiming();
        if (reuseBatches)
        {
            // Frozen batches re-draw as-is; only what the camera moves needs refreshing — blended
            // draw distances (their back-to-front sort) and billboard facing matrices. Streaming
            // counters carry over from the build frame (quiescent by definition of reuse).
            drawn = _lastBuildDrawn;
            referencesWithReadyMesh = _lastBuildDrawn;
            missingMeshes = _lastBuildMissing;
            texturePending = _lastBuildTexturePending;
            RefreshBlendedDraws(frameCameraPosition, frameCameraForward, renderOrigin);
        }
        var survivorsToResolve = reuseBatches ? 0 : _cachedCullSurvivors.Count;
        for (var si = 0; si < survivorsToResolve; si++)
        {
            var r = _cachedCullSurvivors[si];
            // 4-pre Item B — per-frame memoized resolve. First sighting of a MeshId
            // pays one string-keyed _meshCache.GetOrUpload (+ potential mid-frame Insert
            // + LRU touch); every later REFR with the same MeshId hits this map
            // directly (uint-keyed). At 5K REFRs / ~300 unique meshes that drops the
            // cull-loop's mesh-lookup cost ~3× steady-state.
            if (!_resolvedMeshesThisFrame.TryGetValue(r.MeshId, out var mesh))
            {
                // Upload pacing lives in the mesh cache: an accumulated-upload-TIME budget
                // (UploadMillisecondsPerFrame × FrameBudgetScale) plus the byte budget. Both defer
                // rather than fail — the remainder simply appears over the next frame(s). NOT a
                // wall-clock deadline here: measured from frame start it expired before this loop
                // reached far-cell survivors, so distant ready payloads could never start.
                // Nearest-first decode priority: squared XY distance from the view point, so a
                // dense area's foreground meshes decode before its far edge (the queue persists
                // across frames). Cheap — only computed on the first sighting of each MeshId.
                var pdx = r.BoundsCenter.X - cylinderX;
                var pdy = r.BoundsCenter.Y - cylinderY;
                mesh = _meshCache.GetOrUpload(
                    r.ModelPath, ref uploadBudget, pdx * pdx + pdy * pdy, r.AlternateTextures);
                _resolvedMeshesThisFrame[r.MeshId] = mesh;
                if (mesh is not null)
                {
                    // Record the resident mesh's true local radius so the NEXT frame's cull uses
                    // real geometry extent for this MeshId. A new key here grows
                    // _meshLocalRadius.Count, which refreshes the cull cache (immediately in exact
                    // mode, debounced in tolerant mode) so the tighter bounds are applied. Once per
                    // unique MeshId per frame — per-REFR it was ~2ms of redundant dictionary writes
                    // at 50k survivors.
                    _meshLocalRadius[r.MeshId] = mesh.LocalBoundsRadius;
                    _meshLocalBounds[r.MeshId] = (mesh.LocalBoundsMin, mesh.LocalBoundsMax);
                    if (mesh.Animation is not null)
                    {
                        // Keyframe-animated mesh sighted this frame: (re-)register with the skinner
                        // using the nearest-instance distance already computed for decode priority.
                        _skinner.Register(mesh, pdx * pdx + pdy * pdy);
                    }
                }
            }
            if (mesh is null)
            {
                missingMeshes++;
                continue;
            }
            // Placed water geometry needs no textures — emit it as soon as the mesh resolves, once per
            // REFR (the seen-set dedups the per-frame resolve). Transform every mesh-local authored
            // vertex by this placement's world matrix for WaterRenderer12.
            if (mesh.WaterPlanesLocal.Count > 0 && _nifWaterPlaneSeen.Add(r.FormId))
            {
                AccumulateNifWaterPlanes(r.WorldMatrix, mesh.WaterPlanesLocal);
            }

            var texturesReady = mesh.TexturesReady;
            if (!texturesReady)
            {
                texturePending++;
                // SpeedTree .spt geometry has a valid fallback SRV immediately. Do not hide the
                // entire tree while a bark/leaf atlas is still resolving or unavailable; otherwise a
                // single pending foliage texture suppresses both bark and leaves in the live viewer.
                if (!SpeedTreeModelPath.IsSpt(r.ModelPath))
                {
                    continue;
                }
            }

            var anySubmeshDrawn = false;
            // Camera-relative UPLOAD: fold the render origin into this reference's world-matrix translation
            // once, and upload the folded copy for every submesh. mul(uWorld_rel, pos) in the VS then
            // operates on small near-camera coordinates instead of ~52,000 absolute ones — float32 keeps
            // ~30× more precision, so coplanar surfaces on far-from-origin architecture stop Z-fighting. The
            // CULL above still uses the ABSOLUTE r.WorldMatrix; only this GPU copy is shifted. renderOrigin
            // is 0 on the absolute paths, so relWorldMatrix == r.WorldMatrix there (fold is a no-op).
            var relWorldMatrix = r.WorldMatrix;
            relWorldMatrix.Translation -= renderOrigin;
            // Absolute per-instance cull sphere, stored alongside each opaque instance so the draw
            // pass can re-test it against the current frame's EXACT bounds (the tolerant cull
            // batches a widened superset; see PassesExactCull). Mirrors the establishment cull's
            // resident-mesh branch: sphere at the placement point, mesh radius × uniform scale.
            var boundsScaleBasis = new Vector3(r.WorldMatrix.M11, r.WorldMatrix.M12, r.WorldMatrix.M13);
            var instanceBounds = new Vector4(
                r.WorldMatrix.Translation, mesh.LocalBoundsRadius * boundsScaleBasis.Length());
            var alphaDebug = AlphaDebugFilter != null
                && !string.IsNullOrEmpty(r.ModelPath)
                && r.ModelPath.Contains(AlphaDebugFilter, StringComparison.OrdinalIgnoreCase)
                && _alphaDebugLogged.Add(r.ModelPath);
            var alphaDebugIndex = 0;
            foreach (var sub in mesh.Submeshes)
            {
                if (alphaDebug)
                {
                    var opaquePass = sub.DoubleSided ? "OPAQUE/DoublePso" : "OPAQUE/BackPso";
                    var pass = sub.AlphaRenderMode == NifAlphaRenderMode.Blend || sub.IsBillboard
                        ? "BLEND"
                        : opaquePass;
                    try
                    {
                        File.AppendAllText(
                            Path.Combine(Path.GetTempPath(), "alpha_debug.txt"),
                            $"{r.ModelPath} sub#{alphaDebugIndex++} mode={sub.AlphaRenderMode} " +
                            $"billboard={sub.IsBillboard} double={sub.DoubleSided} pass={pass} " +
                            $"alphaStateW={sub.AlphaState.W:0.##}{Environment.NewLine}");
                    }
                    catch
                    {
                        // diagnostic only — never let logging break the frame
                    }
                }

                // Billboards must take the per-draw blended path even when opaque: the instanced
                // opaque path has no per-draw matrix, but a billboard needs a unique camera-facing
                // world matrix per placement.
                if (sub.AlphaRenderMode == NifAlphaRenderMode.Blend || sub.IsBillboard)
                {
                    var sampledSourceWorld = ApplyPhysicsLiteSway(sub, r.FormId, r.WorldMatrix);
                    var worldCenter = Vector3.Transform(sub.LocalBoundsCenter, sampledSourceWorld);
                    var worldRadius = ResolveWorldBoundsRadius(sub, sampledSourceWorld);
                    // worldCenter stays ABSOLUTE (sorted against the actual rendering eye; billboard
                    // facing needs the absolute camera vector). The uploaded matrix is camera-relative:
                    // the non-billboard case folds the possibly-swayed source matrix;
                    // BuildBillboardWorld folds renderOrigin into its own composed translation.
                    var sampledRelativeWorld = sampledSourceWorld;
                    sampledRelativeWorld.Translation -= renderOrigin;
                    var world = sub.IsBillboard
                        ? BuildBillboardWorld(
                            sub.LocalBoundsCenter, sub.BillboardMode, sub.BillboardFrontNormal,
                            sampledSourceWorld, worldCenter, frameCameraPosition, frameCameraForward,
                            renderOrigin)
                        : sampledRelativeWorld;
                    var blendedDraw = new BlendedReferenceDraw(
                        world,
                        sub,
                        Vector3.DistanceSquared(worldCenter, frameCameraPosition),
                        sub.AlphaState,
                        sub.RenderState,
                        sub.Specular,
                        r.WorldMatrix,
                        r.FormId,
                        r.IsGrass,
                        r.GrassWaveMultiplier,
                        worldCenter.Z,
                        worldRadius);
                    // Depth-writing blend foliage (e.g. NVSeaPlant02) draws inline BEFORE the water pass so
                    // water occludes it from above; everything else defers to after water. Billboards keep
                    // the deferred path — they need per-frame camera-facing matrices and aren't occluders.
                    if (sub.DepthWritingBlend && !sub.IsBillboard)
                    {
                        _depthWritingBlendDraws.Add(blendedDraw);
                    }
                    else
                    {
                        _blendedDraws.Add(blendedDraw);
                    }
                    anySubmeshDrawn = true;
                    continue;
                }

                var pso = (sub.DoubleSided, sub.IsDecal) switch
                {
                    (true, true) => _pipelines.OpaqueDoubleDecalPso,
                    (true, false) => _pipelines.OpaqueDoublePso,
                    (false, true) => _pipelines.OpaqueBackDecalPso,
                    (false, false) => _pipelines.OpaqueBackPso
                };
                if (r.IsGrass && sub.AlphaTest && !sub.IsDecal)
                {
                    // Grass cutouts take the alpha-to-coverage variants so MSAA antialiases the
                    // blade silhouettes (identical to the plain opaque PSOs when the scene is
                    // single-sampled — the factory aliases them, so no fallback branch here).
                    pso = sub.DoubleSided ? _pipelines.OpaqueDoubleA2CPso : _pipelines.OpaqueBackA2CPso;
                }
                var usesGrassDistanceEnvelope =
                    GrassDistanceCullPolicy.UsesEnvelope(r.IsGrass, in _grassDistanceEnvelope);
                var usesTallGrassWind = _tallGrassWindSupported && r.IsGrass && sub.IsTallGrass;
                var batch = _opaqueBatches.GetOrCreate(
                    sub,
                    pso,
                    usesGrassDistanceEnvelope,
                    usesTallGrassWind,
                    r.GrassWaveMultiplier);
                // Only the world matrix (+ its cull sphere) is per-instance. Material/texture state
                // (AlphaState/RenderState/TextureState + bindless TexIndices) is identical
                // across the whole batch — it comes from the submesh, which IS the batch key
                // — so it is uploaded once per batch via the InstanceDraw CBV at draw time
                // instead of being copied into every instance record.
                batch.Instances.Add(relWorldMatrix);
                batch.InstanceBounds.Add(instanceBounds);
                if (sub.PhysicsLiteSway is not null)
                {
                    batch.PhysicsLiteSeeds.Add(r.FormId);
                }
                anySubmeshDrawn = true;
            }

            if (anySubmeshDrawn)
            {
                referencesWithReadyMesh++;
                drawn++;
            }
        }

        // === Shadow-only caster resolve (skipped on batch-reuse frames, like the main pass) ===
        // Off-screen refs inside the caster ring: resolved through the same memoized mesh path
        // (their meshes must be resident to cast — bounded streaming, that's the ring's price) and
        // batched into ShadowOnlyInstances, which only the shadow replay draws. Opaque non-decal
        // submeshes only — blended/billboard/decal submeshes don't cast (matches the capture
        // filter), and there is no textures-ready gate: a cutout casting with a placeholder alpha
        // for a few frames beats the whole caster popping.
        var shadowCastersToResolve = reuseBatches ? 0 : _cachedShadowOnlyCasters.Count;
        for (var si = 0; si < shadowCastersToResolve; si++)
        {
            var r = _cachedShadowOnlyCasters[si];
            if (!_resolvedMeshesThisFrame.TryGetValue(r.MeshId, out var mesh))
            {
                var pdx = r.BoundsCenter.X - cylinderX;
                var pdy = r.BoundsCenter.Y - cylinderY;
                mesh = _meshCache.GetOrUpload(
                    r.ModelPath, ref uploadBudget, pdx * pdx + pdy * pdy, r.AlternateTextures);
                _resolvedMeshesThisFrame[r.MeshId] = mesh;
                if (mesh is not null)
                {
                    _meshLocalRadius[r.MeshId] = mesh.LocalBoundsRadius;
                    _meshLocalBounds[r.MeshId] = (mesh.LocalBoundsMin, mesh.LocalBoundsMax);
                }
            }
            if (mesh is null)
            {
                continue;
            }

            var relWorldMatrix = r.WorldMatrix;
            relWorldMatrix.Translation -= renderOrigin;
            foreach (var sub in mesh.Submeshes)
            {
                if (sub.AlphaRenderMode == NifAlphaRenderMode.Blend || sub.IsBillboard || sub.IsDecal)
                {
                    continue;
                }

                var pso = sub.DoubleSided ? _pipelines.OpaqueDoublePso : _pipelines.OpaqueBackPso;
                if (r.IsGrass && sub.AlphaTest)
                {
                    // Mirror the main pass's A2C routing so the shadow-only instances join the
                    // SAME batch (the PSO is part of the batch key) instead of splitting a
                    // second plain-PSO batch for the identical grass submesh.
                    pso = sub.DoubleSided ? _pipelines.OpaqueDoubleA2CPso : _pipelines.OpaqueBackA2CPso;
                }
                var usesGrassDistanceEnvelope =
                    GrassDistanceCullPolicy.UsesEnvelope(r.IsGrass, in _grassDistanceEnvelope);
                var usesTallGrassWind = _tallGrassWindSupported && r.IsGrass && sub.IsTallGrass;
                var batch = _opaqueBatches.GetOrCreate(
                    sub,
                    pso,
                    usesGrassDistanceEnvelope,
                    usesTallGrassWind,
                    r.GrassWaveMultiplier);
                batch.ShadowOnlyInstances.Add(relWorldMatrix);
                if (sub.PhysicsLiteSway is not null)
                {
                    batch.ShadowOnlyPhysicsLiteSeeds.Add(r.FormId);
                }
            }
        }

        if (!reuseBatches)
        {
            // Build snapshot for the next frame's reuse decision. Quiescence == a sustained streak
            // of builds that streamed NOTHING: every RESOLVABLE survivor's mesh + textures are in,
            // zero GPU uploads, zero cache misses — which also means zero LRU Sets, hence zero
            // evictions mid-build, so the frozen batches cannot reference freed buffers (belt: the
            // EvictionGeneration compare). missingMeshes is deliberately NOT in the gate: dense
            // spots hold thousands of permanently unresolvable meshes (miss ≈ 10-20k at FO4
            // downtown) that never reach zero and contribute nothing to the batches; a late
            // resolve lands via the periodic BatchReuseMaxFrames rebuild instead.
            // "Quiet" additionally requires the decode PIPELINE to be empty — not just zero
            // uploads this frame. A freeze that begins while decodes are still in flight strands
            // their payloads outside the frozen batches (a settle-gated capture caught a whole
            // storefront missing that way). With upload pacing no longer reach-starved, the
            // pipeline genuinely drains at steady state (decodeReq/active/queued all 0), so this
            // does not dead-lock reuse the way it would have before that fix.
            var buildQuiet = texturePending == 0
                             && _meshCache.FrameGpuUploads == 0 && _meshCache.FrameCacheMisses == 0
                             && _meshCache.FrameDecodeRequests == 0 && _meshCache.FrameDecodeStarts == 0
                             && _meshCache.FrameActiveDecodes == 0;
            _quietBuildStreak = buildQuiet ? _quietBuildStreak + 1 : 0;
            _lastBuildValid = true;
            _framesSinceBuild = 0;
            _lastBuildCullEpoch = _cullEpoch;
            _lastBuildRenderOrigin = renderOrigin;
            _lastBuildEvictionGen = _meshCache.EvictionGeneration;
            _lastBuildQuiesced = _quietBuildStreak >= QuietBuildStreakFrames;
            _lastBuildDrawn = referencesWithReadyMesh;
            _lastBuildMissing = missingMeshes;
            _lastBuildTexturePending = texturePending;
            _batchesWidened = tolerant;
        }
        // Generic cylinder/LOD/frustum refiltering requires a real frustum. Enabled grass-distance
        // batches independently force their own exact per-instance copy in DrawOpaqueBatches, so
        // disabling reference-frustum culling never samples the default frustum and never bypasses
        // the FNV hard end.
        _frameRefilterActive = _batchesWidened && hasFrustum;
        meshUploadMs = ElapsedMilliseconds(meshStarted);

        // Legacy live particles: every visible unique particle submesh receives one coherent transient
        // VB/IB update before either blended pass binds geometry. Ring exhaustion/malformed controller data
        // clears the override and draws the existing immutable static cloud for this frame.
        UpdateLiveParticleFrames(frameIndex);

        // Keyframe playback: re-pose + CPU-skin every in-budget animated mesh into fresh ring
        // allocations (or clear all overrides when disabled) BEFORE any pass binds a vertex buffer —
        // both the opaque draws and the shadow capture below read EffectiveVertexBufferView.
        // resolveRan: on batch-REUSE frames the resolve loop was skipped, so Register never had a
        // chance to re-sight live meshes — the skinner must not expire/decay entries on those frames
        // (a settled scene froze playback otherwise: meshes animated only while the camera moved).
        _skinner.Tick(
            frameIndex, _ringBuffer, _animationClockSeconds, cylinder.Position, AnimationsEnabled,
            resolveRan: !reuseBatches);

        ID3D12PipelineState? currentPso = null;
        DrawOpaqueBatches(cmd, frameIndex, ref currentPso, ref cbUpdateMs, ref srvBindMs, ref drawCallMs, ref srvBinds, ref submeshDraws);
        // Depth-writing blend foliage draws here — INLINE, after the opaque batches but before the water
        // pass — with a depth-writing blend PSO, so the water surface occludes it from above (a no-depth
        // blend drawn after water painted over the surface). Runs every frame regardless of deferBlended.
        DrawDepthWritingBlend(cmd, frameIndex, ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws);
        // 3D-8: blended submeshes draw now (inline, e.g. top-down overlay) or are deferred to after the
        // water pass via RenderBlendedDeferred() so water never paints over transparent meshes.
        if (!deferBlended)
        {
            DrawBlended(
                cmd, frameIndex, sceneDepthSampled: false,
                ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws);
        }

        ReferencesDrawnLastFrame = drawn;
        LastStats.ReferenceCellsVisited = cellsVisited;
        LastStats.ReferenceCandidates = candidates;
        LastStats.ReferenceCulled = culled;
        LastStats.ReferenceMeshMissing = missingMeshes;
        LastStats.ReferenceTexturePending = texturePending;
        LastStats.ReferenceDrawn = referencesWithReadyMesh;
        LastStats.ReferenceSubmeshDraws = submeshDraws;
        LastStats.ReferenceSrvBinds = srvBinds;
        LastStats.ReferenceMeshCacheMisses = _meshCache.FrameCacheMisses;
        LastStats.ReferenceDecodeRequests = _meshCache.FrameDecodeRequests;
        LastStats.ReferenceQueuedDecodes = _meshCache.FrameQueuedDecodes;
        LastStats.ReferenceDecodeStarts = _meshCache.FrameDecodeStarts;
        LastStats.ReferenceActiveDecodes = _meshCache.FrameActiveDecodes;
        LastStats.ReferenceCpuDecodedMeshCacheHits = _meshCache.FrameCpuDecodedCacheHits;
        LastStats.ReferenceCpuDecodedMeshCacheMisses = _meshCache.FrameCpuDecodedCacheMisses;
        LastStats.ReferenceCpuDecodedMeshNegativeHits = _meshCache.FrameCpuDecodedNegativeHits;
        LastStats.ReferenceGpuUploads = _meshCache.FrameGpuUploads;
        LastStats.ReferenceUploadByteBudgetDeferrals = _meshCache.FrameByteBudgetDeferrals;
        LastStats.ReferenceCompressedTextureUploads = _meshCache.FrameCompressedTextureUploads;
        LastStats.ReferenceRgbaTextureUploads = _meshCache.FrameRgbaTextureUploads;
        LastStats.ReferenceTextureQueuedResolves = _meshCache.FrameQueuedTextureResolves;
        LastStats.ReferenceTextureActiveResolves = _meshCache.FrameActiveTextureResolves;
        LastStats.ReferenceTexturePendingResolves = _meshCache.PendingTextureResolves;
        LastStats.ReferenceTexturePendingUploads = _meshCache.PendingTextureUploads;
        LastStats.ReferenceCullMilliseconds = cullMs;
        LastStats.ReferenceMeshUploadMilliseconds = meshUploadMs;
        LastStats.ReferenceCbUpdateMilliseconds = cbUpdateMs;
        LastStats.ReferenceSrvBindMilliseconds = srvBindMs;
        LastStats.ReferenceDrawCallMilliseconds = drawCallMs;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return drawn;
    }

    /// <summary>
    ///     3D-8: draws the blended (transparent) reference submeshes accumulated by the most recent
    ///     <see cref="Render(Matrix4x4, VisibilityCylinder, bool, Matrix4x4?, Vector3, CullCameraPose?, Vector3?, Vector3?)" /> with <c>deferBlended: true</c>. Called AFTER the water pass so water
    ///     never paints over transparent meshes. The water pass rebinds <c>PerFrameCbv</c> to its own
    ///     uniforms (and may change topology), so this re-establishes the reference per-frame state
    ///     before issuing the blended draws. With a scene-depth SRV the host leaves the DSV unbound;
    ///     the shader reproduces reversed-Z opaque occlusion and feathers only eligible effects.
    ///     Otherwise the established hardware-depth PSO remains active.
    /// </summary>
    public void RenderBlendedDeferred() =>
        RenderBlendedDeferredCore(DeferredWaterPartition.All, 0f, allowSceneDepth: true);

    /// <summary>
    ///     Draws only translucent geometry whose conservative world bound is wholly below the
    ///     generated-water plane.  The host invokes this before capturing WATER001's opaque-scene
    ///     approximation so underwater decals/effects are refracted by the surface instead of being
    ///     composited over it.  Scene-depth sampling is deliberately disabled here because the depth
    ///     resource is still in its ordinary writable state.
    /// </summary>
    public void RenderBlendedDeferredBelowWater(float planeHeight) =>
        RenderBlendedDeferredCore(
            DeferredWaterPartition.WhollyBelow,
            planeHeight,
            allowSceneDepth: false);

    /// <summary>Draws the complementary intersecting/above-water translucent partition.</summary>
    public void RenderBlendedDeferredAtOrAboveWater(float planeHeight) =>
        RenderBlendedDeferredCore(
            DeferredWaterPartition.NotWhollyBelow,
            planeHeight,
            allowSceneDepth: true);

    private void RenderBlendedDeferredCore(
        DeferredWaterPartition waterPartition,
        float waterPlaneHeight,
        bool allowSceneDepth)
    {
        // Address 0 = the opaque pass skipped this frame (ring exhaustion) — nothing valid to bind.
        if (_blendedDraws.Count == 0 || _deferredPerFrameCbvAddress == 0) return;
        var cmd = _recorder.CommandList;

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, _deferredPerFrameCbvAddress);
        cmd.SetGraphicsRootDescriptorTable(GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);

        ID3D12PipelineState? currentPso = null;
        double cbUpdateMs = 0, drawCallMs = 0;
        var submeshDraws = 0;
        var sceneDepthSampled = allowSceneDepth && _deferredSceneDepthIndex != NoSceneDepth;
        DrawBlended(
            cmd, _deferredFrameIndex, sceneDepthSampled,
            ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws,
            waterPartition, waterPlaneHeight);

        LastStats.ReferenceSubmeshDraws += submeshDraws;
        LastStats.ReferenceCbUpdateMilliseconds += cbUpdateMs;
        LastStats.ReferenceDrawCallMilliseconds += drawCallMs;
    }

    // CPU mirror of the shadow VS variant's extended PerFrame cbuffer (SHADOW_CARD_LIGHT_FACING):
    // the light viewProj plus the light-perpendicular leaf-card billboard basis.
    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowPerFrameConstants
    {
        public Matrix4x4 ViewProj;
        public Vector4 CardRight;
        public Vector4 CardUp;
        public const uint ByteSize = 64 + 32;
    }

    /// <summary>
    ///     Replays this frame's captured opaque draws into the (already bound) shadow-map depth
    ///     target from the light's view. The host binds the shadow DSV/viewport first
    ///     (<c>ShadowMapRenderer12.BeginRender</c>); this method only sets its own per-frame CB
    ///     (the light viewProj + leaf-card basis at b0), the depth-only PSOs, and each draw's
    ///     captured InstanceDraw-CB / instance-SRV addresses — all frame-local ring allocations,
    ///     so it MUST run in the same host frame that captured them (see
    ///     <see cref="ArmShadowCapture" />). Returns false (drawing nothing) when there is nothing
    ///     to replay.
    /// </summary>
    public bool RenderShadowDepth(in SunShadowMath.LightFrustum frustum)
    {
        _shadowCaptureArmed = false;
        if (_shadowDraws.Count == 0) return false;

        var cmd = _recorder.CommandList;
        if (!_ringBuffer.TryAllocate(
                _recorder.FrameIndex, ShadowPerFrameConstants.ByteSize, out var perFrameAlloc,
                GpuRingBuffer12.CbAlignment))
        {
            return false; // the host disables this cleared cascade and falls through to a farther map
        }

        unsafe
        {
            *(ShadowPerFrameConstants*)perFrameAlloc.CpuPtr = new ShadowPerFrameConstants
            {
                ViewProj = frustum.ViewProj,
                CardRight = new Vector4(frustum.CardRight, 0f),
                CardUp = new Vector4(frustum.CardUp, 0f),
            };
        }
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        cmd.SetGraphicsRootDescriptorTable(
            GpuRootSignature12.Slots.BindlessSrvTable, _cbvSrvUavHeap.BindlessHeapStartGpu);

        ID3D12PipelineState? currentPso = null;
        ulong currentInstanceAddress = 0;
        foreach (var draw in _shadowDraws)
        {
            var pso = draw.AlphaTested ? _pipelines.ShadowAlphaTestPso : _pipelines.ShadowOpaquePso;
            if (!ReferenceEquals(currentPso, pso))
            {
                cmd.SetPipelineState(pso);
                currentPso = pso;
            }

            if (draw.InstanceSrvAddress != currentInstanceAddress)
            {
                cmd.SetGraphicsRootShaderResourceView(
                    (uint)GpuRootSignature12.Slots.ReferenceInstanceSrv, draw.InstanceSrvAddress);
                currentInstanceAddress = draw.InstanceSrvAddress;
            }

            cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, draw.PerDrawCbAddress);
            cmd.IASetVertexBuffers(0, draw.VertexBufferView);
            cmd.IASetIndexBuffer(draw.IndexBufferView);
            cmd.DrawIndexedInstanced((uint)draw.IndexCount, (uint)draw.DrawCount, 0, 0, 0);
            if (draw.UsesTallGrassWind)
            {
                LastStats.ReferenceTallGrassShadowDraws++;
                LastStats.ReferenceTallGrassShadowInstances += draw.DrawCount;
            }
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pipelines.Dispose();
    }

    // Per-axis drift bound for the tolerant cull cache (see CullCameraPose).
    private static bool ChebyshevWithin(Vector3 a, Vector3 b, float slack)
        => MathF.Abs(a.X - b.X) < slack
           && MathF.Abs(a.Y - b.Y) < slack
           && MathF.Abs(a.Z - b.Z) < slack;

    private IEnumerable<CellRecord> EnumerateVisibleCells(VisibilityCylinder cylinder)
    {
        // Nearest cell first. The resolve pass consumes the per-frame upload + disk-load budget in
        // enumeration order, and the spatial query returns row-major scan order — so a cold dense
        // area streamed in from the top-left corner of the visible square while the meshes right in
        // front of the camera waited their turn. Distance-sorting the cells makes streaming radiate
        // out from the camera, and gives a shared mesh its NEAREST instance's decode-queue priority
        // (the per-MeshId priority is captured on first sighting).
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryCellsInRadius(
                cylinder.Position.X,
                -cylinder.Position.Y,
                cylinder.Radius,
                _candidateCells);
            var camCanvas = new Vector2(cylinder.Position.X, -cylinder.Position.Y);
            _candidateCells.Sort((a, b) =>
                Vector2.DistanceSquared(a.CenterCanvas, camCanvas)
                    .CompareTo(Vector2.DistanceSquared(b.CenterCanvas, camCanvas)));
            foreach (var candidate in _candidateCells)
            {
                yield return candidate.Cell;
            }
            yield break;
        }

        var matches = new List<(float DistSq, CellRecord Cell)>();
        foreach (var (key, cell) in _cells!)
        {
            if (cylinder.ContainsCell(key.gx, key.gy))
            {
                var dx = (key.gx + 0.5f) * WorldGridConstants.CellSize - cylinder.Position.X;
                var dy = (key.gy + 0.5f) * WorldGridConstants.CellSize - cylinder.Position.Y;
                matches.Add((dx * dx + dy * dy, cell));
            }
        }

        matches.Sort(static (a, b) => a.DistSq.CompareTo(b.DistSq));
        foreach (var (_, cell) in matches)
        {
            yield return cell;
        }
    }

    // Applies the placement matrix to each authored water vertex. Bounds are recomputed only for
    // culling; WaterRenderer12 consumes the transformed positions + original indices, matching the
    // retail FNV WATER000 vertex path instead of expanding a flat AABB-derived quad.
    private void AccumulateNifWaterPlanes(Matrix4x4 world, IReadOnlyList<NifWaterGeometry> localPlanes)
    {
        for (var pi = 0; pi < localPlanes.Count; pi++)
        {
            var placed = localPlanes[pi].Transform(world);
            if (placed is null)
            {
                continue;
            }

            _nifWaterPlanes.Add(placed);
        }
    }

    private void DrawOpaqueBatches(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        ref ID3D12PipelineState? currentPso,
        ref double cbUpdateMs,
        ref double srvBindMs,
        ref double drawCallMs,
        ref int srvBinds,
        ref int submeshDraws)
    {
        var activeBatches = _opaqueBatches.ActiveBatches;
        // Shadow-only casters are uploaded only when the shadow capture is armed this frame (they
        // ride AFTER each batch's main instances in the same block — see the copy pass below).
        var includeShadow = _shadowCaptureArmed;
        var totalInstances = 0;
        var activeBatchCount = 0;
        foreach (var batchState in activeBatches)
        {
            var count = batchState.Instances.Count +
                        (includeShadow ? batchState.ShadowOnlyInstances.Count : 0);
            if (count == 0) continue;
            totalInstances += count;
            activeBatchCount++;
        }
        if (totalInstances == 0) return;

        var instanceStride = (uint)Marshal.SizeOf<Matrix4x4>();
        var instanceBytes = instanceStride * (uint)totalInstances;
        // Instance-SRV GPU address currently bound at t8 — captured per shadow draw so the shadow
        // replay can rebind exactly what each draw's uInstanceBase indexes into (the shared block,
        // or a per-batch fallback block).
        ulong boundInstanceAddress = 0;
        // Fast path: one contiguous block holds every batch's world matrices (single bulk memcpy +
        // one SRV bind, per-batch draws index into it via StartInstance). When a dense frame can't
        // fit that single allocation, fall back to a per-batch block inside the loop below — the
        // frame degrades to skipping only the batches that no longer fit instead of dropping the
        // ENTIRE opaque pass (which read as "the whole city un-renders" on dense downtown frames).
        var haveSharedBlock = _ringBuffer.TryAllocate(
            frameIndex, instanceBytes + instanceStride - 1, out var instanceAlloc, alignment: 16);
        var refilter = _frameRefilterActive;
        // CPU-mapped base of the shared instance block, kept for the geometry draw validator: it
        // reads back the exact matrices each batch's draw will fetch (0 when there is no shared block).
        nint sharedInstanceCpuBase = 0;
        if (haveSharedBlock)
        {
            var instanceByteOffset = AlignUp(instanceAlloc.ByteOffset, instanceStride);
            var instanceCpuPtr = instanceAlloc.CpuPtr + (int)(instanceByteOffset - instanceAlloc.ByteOffset);
            sharedInstanceCpuBase = instanceCpuPtr;

            var offset = 0;
            unsafe
            {
                // Copy each batch's world matrices into the shared block. Ordinary exact-cull mode
                // uses one bulk memcpy per batch. A widened generic cull OR an enabled grass-distance
                // envelope switches to per-instance copy: generic bounds call PassesExactCull only
                // while a real frustum is armed, while grass independently restores its zero-slack
                // hard end even under the reference-frustum kill switch.
                var span = new Span<Matrix4x4>((void*)instanceCpuPtr, totalInstances);
                foreach (var batchState in activeBatches)
                {
                    var worlds = batchState.Instances;
                    var shadowWorlds = includeShadow ? batchState.ShadowOnlyInstances : null;
                    var shadowCount = shadowWorlds?.Count ?? 0;
                    var filterSpeedTreeLod = batchState.Submesh.SpeedTreeLod is not null;
                    var filterMainGrassDistance = batchState.UsesGrassDistanceEnvelope;
                    var exactMainPerInstance = GrassDistanceCullPolicy.RequiresExactPerInstanceFiltering(
                        refilter,
                        filterMainGrassDistance);
                    var animatePhysicsLite = AnimationsEnabled &&
                                             batchState.Submesh.PhysicsLiteSway is not null &&
                                             batchState.PhysicsLiteSeeds.Count == worlds.Count;
                    if (worlds.Count == 0 && shadowCount == 0)
                    {
                        batchState.FrameDrawCount = 0;
                        batchState.FrameShadowOnlyCount = 0;
                        continue;
                    }
                    if (!exactMainPerInstance && !filterSpeedTreeLod && !animatePhysicsLite)
                    {
                        CollectionsMarshal.AsSpan(worlds).CopyTo(span.Slice(offset, worlds.Count));
                        offset += worlds.Count;
                        batchState.FrameDrawCount = worlds.Count;
                    }
                    else
                    {
                        var worldSpan = CollectionsMarshal.AsSpan(worlds);
                        var boundsSpan = CollectionsMarshal.AsSpan(batchState.InstanceBounds);
                        var physicsSeeds = CollectionsMarshal.AsSpan(batchState.PhysicsLiteSeeds);
                        var batchStart = offset;
                        for (var i = 0; i < worldSpan.Length; i++)
                        {
                            if (filterMainGrassDistance && !PassesExactGrassDistance(
                                    new Vector3(boundsSpan[i].X, boundsSpan[i].Y, boundsSpan[i].Z),
                                    isGrass: true)) continue;
                            if (refilter && !PassesExactCull(boundsSpan[i])) continue;
                            if (filterSpeedTreeLod &&
                                !PassesSpeedTreeLod(batchState.Submesh, worldSpan[i], boundsSpan[i])) continue;
                            span[offset++] = animatePhysicsLite
                                ? ApplyPhysicsLiteSway(batchState.Submesh, physicsSeeds[i], worldSpan[i])
                                : worldSpan[i];
                        }
                        batchState.FrameDrawCount = offset - batchStart;
                    }

                    // Shadow-only casters go AFTER the main instances so the main draw's count
                    // excludes them while the shadow replay's count includes them. They do not need
                    // the camera exact-cull, but runtime SpeedTrees must still select one LOD or every
                    // generated level would overlap in the shadow map.
                    if (shadowCount > 0)
                    {
                        var filterGrassDistance = batchState.UsesGrassDistanceEnvelope;
                        var animateShadowPhysicsLite = AnimationsEnabled &&
                                                       batchState.Submesh.PhysicsLiteSway is not null &&
                                                       batchState.ShadowOnlyPhysicsLiteSeeds.Count == shadowCount;
                        if (!filterSpeedTreeLod && !animateShadowPhysicsLite && !filterGrassDistance)
                        {
                            CollectionsMarshal.AsSpan(shadowWorlds!).CopyTo(span.Slice(offset, shadowCount));
                            offset += shadowCount;
                        }
                        else
                        {
                            var shadowStart = offset;
                            var shadowSpan = CollectionsMarshal.AsSpan(shadowWorlds!);
                            var shadowPhysicsSeeds =
                                CollectionsMarshal.AsSpan(batchState.ShadowOnlyPhysicsLiteSeeds);
                            for (var i = 0; i < shadowSpan.Length; i++)
                            {
                                if (filterGrassDistance && !PassesExactGrassDistance(
                                        shadowSpan[i].Translation + _frameRenderOrigin,
                                        isGrass: true)) continue;
                                if (!PassesSpeedTreeLod(batchState.Submesh, shadowSpan[i])) continue;
                                span[offset++] = animateShadowPhysicsLite
                                    ? ApplyPhysicsLiteSway(
                                        batchState.Submesh, shadowPhysicsSeeds[i], shadowSpan[i])
                                    : shadowSpan[i];
                            }

                            shadowCount = offset - shadowStart;
                        }
                    }
                    batchState.FrameShadowOnlyCount = shadowCount;
                }
            }

            var instanceGpuAddress = instanceAlloc.GpuAddress + (instanceByteOffset - instanceAlloc.ByteOffset);
            BindReferenceInstanceBuffer(cmd, instanceGpuAddress, ref srvBinds, ref srvBindMs);
            boundInstanceAddress = instanceGpuAddress;
        }

        var startInstance = 0u;
        foreach (var batchState in activeBatches)
        {
            var batch = batchState.Instances;
            var shadowOnly = includeShadow ? batchState.ShadowOnlyInstances : null;
            if (batch.Count == 0 && (shadowOnly is null || shadowOnly.Count == 0)) continue;

            var drawStartInstance = startInstance;
            var drawInstanceCpuBase = sharedInstanceCpuBase;
            int drawCount;
            int shadowCount;
            if (!haveSharedBlock)
            {
                // Per-batch fallback block: small (count × 64 B), so it fits where the frame-wide
                // block could not. Rebind the instance SRV at this batch's base and draw from 0.
                var fallbackShadowCount = shadowOnly?.Count ?? 0;
                var batchBytes = instanceStride * (uint)(batch.Count + fallbackShadowCount);
                if (!_ringBuffer.TryAllocate(
                        frameIndex, batchBytes + instanceStride - 1, out var batchAlloc, alignment: 16))
                {
                    LastFrameDrawsTruncated += batch.Count;
                    continue;
                }

                var batchByteOffset = AlignUp(batchAlloc.ByteOffset, instanceStride);
                var batchCpuPtr = batchAlloc.CpuPtr + (int)(batchByteOffset - batchAlloc.ByteOffset);
                var filterSpeedTreeLod = batchState.Submesh.SpeedTreeLod is not null;
                var filterMainGrassDistance = batchState.UsesGrassDistanceEnvelope;
                var exactMainPerInstance = GrassDistanceCullPolicy.RequiresExactPerInstanceFiltering(
                    refilter,
                    filterMainGrassDistance);
                var animatePhysicsLite = AnimationsEnabled &&
                                         batchState.Submesh.PhysicsLiteSway is not null &&
                                         batchState.PhysicsLiteSeeds.Count == batch.Count;
                unsafe
                {
                    var span = new Span<Matrix4x4>((void*)batchCpuPtr, batch.Count + fallbackShadowCount);
                    if (!exactMainPerInstance && !filterSpeedTreeLod && !animatePhysicsLite)
                    {
                        CollectionsMarshal.AsSpan(batch).CopyTo(span);
                        drawCount = batch.Count;
                    }
                    else
                    {
                        var worldSpan = CollectionsMarshal.AsSpan(batch);
                        var boundsSpan = CollectionsMarshal.AsSpan(batchState.InstanceBounds);
                        var physicsSeeds = CollectionsMarshal.AsSpan(batchState.PhysicsLiteSeeds);
                        drawCount = 0;
                        for (var i = 0; i < worldSpan.Length; i++)
                        {
                            if (filterMainGrassDistance && !PassesExactGrassDistance(
                                    new Vector3(boundsSpan[i].X, boundsSpan[i].Y, boundsSpan[i].Z),
                                    isGrass: true)) continue;
                            if (refilter && !PassesExactCull(boundsSpan[i])) continue;
                            if (filterSpeedTreeLod &&
                                !PassesSpeedTreeLod(batchState.Submesh, worldSpan[i], boundsSpan[i])) continue;
                            span[drawCount++] = animatePhysicsLite
                                ? ApplyPhysicsLiteSway(batchState.Submesh, physicsSeeds[i], worldSpan[i])
                                : worldSpan[i];
                        }
                    }

                    // Shadow-only casters after the main instances (same contract as the shared block).
                    if (fallbackShadowCount > 0)
                    {
                        var filterGrassDistance = batchState.UsesGrassDistanceEnvelope;
                        var animateShadowPhysicsLite = AnimationsEnabled &&
                                                       batchState.Submesh.PhysicsLiteSway is not null &&
                                                       batchState.ShadowOnlyPhysicsLiteSeeds.Count == fallbackShadowCount;
                        if (!filterSpeedTreeLod && !animateShadowPhysicsLite && !filterGrassDistance)
                        {
                            CollectionsMarshal.AsSpan(shadowOnly!).CopyTo(span.Slice(drawCount, fallbackShadowCount));
                            shadowCount = fallbackShadowCount;
                        }
                        else
                        {
                            shadowCount = 0;
                            var shadowSpan = CollectionsMarshal.AsSpan(shadowOnly!);
                            var shadowPhysicsSeeds =
                                CollectionsMarshal.AsSpan(batchState.ShadowOnlyPhysicsLiteSeeds);
                            for (var i = 0; i < shadowSpan.Length; i++)
                            {
                                if (filterGrassDistance && !PassesExactGrassDistance(
                                        shadowSpan[i].Translation + _frameRenderOrigin,
                                        isGrass: true)) continue;
                                if (!PassesSpeedTreeLod(batchState.Submesh, shadowSpan[i])) continue;
                                span[drawCount + shadowCount++] = animateShadowPhysicsLite
                                    ? ApplyPhysicsLiteSway(
                                        batchState.Submesh, shadowPhysicsSeeds[i], shadowSpan[i])
                                    : shadowSpan[i];
                            }
                        }
                    }
                    else
                    {
                        shadowCount = 0;
                    }
                }
                if (drawCount + shadowCount == 0) continue;

                var batchInstanceAddress = batchAlloc.GpuAddress + (batchByteOffset - batchAlloc.ByteOffset);
                BindReferenceInstanceBuffer(cmd, batchInstanceAddress, ref srvBinds, ref srvBindMs);
                boundInstanceAddress = batchInstanceAddress;
                drawStartInstance = 0;
                drawInstanceCpuBase = batchCpuPtr;
            }
            else
            {
                drawCount = batchState.FrameDrawCount;
                shadowCount = batchState.FrameShadowOnlyCount;
                if (drawCount + shadowCount == 0) continue;
            }

            if (!ReferenceEquals(currentPso, batchState.Pso))
            {
                cmd.SetPipelineState(batchState.Pso);
                currentPso = batchState.Pso;
            }

            var cbStarted = StartTiming();
            // Per-batch material/texture state pulled from the submesh (the batch key) — these
            // are identical for every instance in the batch, so they live here, not per instance.
            var sub = batchState.Submesh;
            if (drawCount > 0 &&
                (sub.IsSpeedTreeBranch || sub.IsLeafBillboard || sub.SpeedTreeLod is not null))
            {
                var lod = sub.SpeedTreeLod;
                var component = lod?.Component
                                ?? (sub.IsLeafBillboard
                                    ? SpeedTreeLodComponent.Leaf
                                    : SpeedTreeLodComponent.Branch);
                switch (component)
                {
                    case SpeedTreeLodComponent.Branch:
                        LastStats.ReferenceSpeedTreeBranchInstances += drawCount;
                        break;
                    case SpeedTreeLodComponent.Leaf:
                        LastStats.ReferenceSpeedTreeLeafInstances += drawCount;
                        break;
                    case SpeedTreeLodComponent.Billboard:
                        LastStats.ReferenceSpeedTreeBillboardInstances += drawCount;
                        break;
                }

                if (lod is { } metadata)
                {
                    LastStats.ReferenceSpeedTreeMinimumLod =
                        LastStats.ReferenceSpeedTreeMinimumLod < 0
                            ? metadata.Level
                            : Math.Min(LastStats.ReferenceSpeedTreeMinimumLod, metadata.Level);
                    LastStats.ReferenceSpeedTreeMaximumLod =
                        Math.Max(LastStats.ReferenceSpeedTreeMaximumLod, metadata.Level);
                }
            }
            var textureState = ResolveTextureState(sub);
            var instanceDraw = new InstanceDrawConstants(
                sub.AlphaState,
                sub.RenderState,
                textureState,
                new TexIndexQuad(
                    sub.Diffuse.BindlessIndex, sub.Normal.BindlessIndex,
                    sub.ClassicParallaxHeightMap?.BindlessIndex ??
                    sub.ClassicEnvMask?.BindlessIndex ?? sub.SpecularMap?.BindlessIndex ?? 0,
                    sub.GradientMap?.BindlessIndex ?? sub.Lighting30GlowMap?.BindlessIndex ?? 0),
                drawStartInstance,
                UvOffsetU: WrapUv(sub.UvScrollVelocity.X, UvScrollClock),
                UvOffsetV: WrapUv(sub.UvScrollVelocity.Y, UvScrollClock),
                WindMatrixValid: 1,
                Specular: sub.Specular,
                CameraRight: _leafBillboardRight,
                CameraUp: _leafBillboardUp,
                // TREE CNAM RockSpeed/RustleSpeed are per-species phase multipliers. They must be
                // applied per batch (the shared wind rig advances the game-wide base phases).
                // The far imposter is one whole-tree card, not a live leaf. Keep its corners rigid;
                // applying STLEAF rock/rustle would curl the complete tree silhouette independently.
                Wind: sub.SpeedTreeLod?.Component == SpeedTreeLodComponent.Billboard
                    ? Vector4.Zero
                    : new Vector4(
                        _wind.X,
                        _wind.Y * sub.SpeedTreeWindSpeeds.X,
                        _wind.Z,
                        _wind.W * sub.SpeedTreeWindSpeeds.Y),
                EffectTint: new Vector4(sub.EffectTint, sub.HasEffectFalloff ? 1f : 0f),
                // Classic PP lighting and BGEM/NoLighting falloff are mutually exclusive;
                // TextureState bit 4 tells the PS when this existing slot carries
                // (raw emission rgb, material multiplier) instead.
                EffectFalloff: sub.HasEffectFalloff ? sub.EffectFalloffParams : sub.Lighting30Emission,
                // Re-read every frame (like the bindless indices) so a cube promoted after the
                // batch froze still lands — EnvMapState flips from −1 to the slot on residency.
                EnvMap: sub.EnvMapState,
                SoftParticle: Vector4.Zero,
                TallGrassWind: BuildTallGrassWindConstants(
                    batchState.UsesTallGrassWind,
                    batchState.GrassWaveMultiplier),
                SpecularLodBounds: new Vector4(sub.LocalBoundsCenter, sub.LocalBoundsRadius),
                SpecularLodParams: _classicSpecularLodProfile.ShaderParameters(
                    specularEligible: sub.Specular.W > 0f));
            if (!_ringBuffer.TryAllocate(frameIndex, InstanceDrawByteSize, out var instanceDrawAlloc, GpuRingBuffer12.CbAlignment))
            {
                LastFrameDrawsTruncated += batch.Count;
                break;
            }
            unsafe { *(InstanceDrawConstants*)instanceDrawAlloc.CpuPtr = instanceDraw; }
            cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, instanceDrawAlloc.GpuAddress);
            cbUpdateMs += ElapsedMilliseconds(cbStarted);

            // FALLOUT_VIEWER_GEOMETRY_VALIDATE: prove the geometry views and compacted instance
            // matrices this draw will read are sane. A violation only suppresses the cmd.* calls
            // (under _SKIP_DRAW) — the startInstance accounting below MUST advance regardless,
            // because every batch's instances were already compacted at fixed offsets.
            var drawLiveness = !GeometryArenaDiagnostics.Enabled ||
                               GeometryDrawValidator12.ValidateOpaqueDraw(
                                   batchState, _meshCache, drawInstanceCpuBase, drawStartInstance,
                                   drawCount, shadowCount, _framesSinceBuild, _lastBuildEvictionGen,
                                   _frameReusedBatches);

            var drawStarted = StartTiming();
            cmd.IASetVertexBuffers(0, batchState.Submesh.EffectiveVertexBufferView);
            cmd.IASetIndexBuffer(batchState.Submesh.IndexBufferView);
            if (drawCount > 0 && drawLiveness)
            {
                // The main scene draw excludes the shadow-only tail of the instance range.
                cmd.DrawIndexedInstanced((uint)batchState.Submesh.IndexCount, (uint)drawCount, 0, 0, 0);
                ObserveFnvActiveAdtBaseDraw(sub, textureState, drawCount);
                if (batchState.UsesTallGrassWind)
                {
                    LastStats.ReferenceTallGrassInstancedDraws++;
                    LastStats.ReferenceTallGrassInstancedInstances += drawCount;
                    ObserveTallGrassWaveMultiplier(batchState.GrassWaveMultiplier);
                }
            }
            if (_shadowCaptureArmed && !sub.IsDecal && drawCount + shadowCount > 0 && drawLiveness)
            {
                // Record this draw for the frame-end shadow replay: the ring-buffer CB just bound
                // (uInstanceBase et al.) + the t8 instance block it indexes stay valid until the
                // frame's allocations are recycled, i.e. exactly the replay window. The replay's
                // instance count INCLUDES the shadow-only casters appended after the main range.
                _shadowDraws.Add(new ShadowDraw(
                    sub.EffectiveVertexBufferView, sub.IndexBufferView, sub.IndexCount,
                    instanceDrawAlloc.GpuAddress, boundInstanceAddress, drawCount + shadowCount,
                    sub.AlphaTest, batchState.UsesTallGrassWind));
                if (batchState.UsesTallGrassWind)
                {
                    ObserveTallGrassWaveMultiplier(batchState.GrassWaveMultiplier);
                }
#pragma warning disable S1244 // exactly-zero rock/rustle amounts mean wind authored off by design
                if (sub.IsLeafBillboard &&
                    sub.SpeedTreeLod?.Component != SpeedTreeLodComponent.Billboard &&
                    (_wind.X != 0f || _wind.Z != 0f))
#pragma warning restore S1244
                {
                    // Swaying canopy in the map → the host re-renders it every frame (rock/rustle
                    // AMOUNTS gate motion; the phases advance regardless).
                    ShadowDrawsIncludeAnimatedLeaves = true;
                }
                if (sub.AnimatedVertexBufferView is not null)
                {
                    // A CPU-skinned pose is casting: its ring VB is only valid this frame, so the
                    // host must re-render the map every frame it casts (same as animated leaves).
                    ShadowDrawsIncludeAnimatedMeshes = true;
                }
                if (AnimationsEnabled && sub.PhysicsLiteSway is not null)
                {
                    // Reuse the host's general animated-mesh shadow contract: physics-lite changes
                    // only per-instance matrices, but it has the same requirement that a cached sun
                    // map be refreshed every frame while the GUI Animations switch is on.
                    ShadowDrawsIncludeAnimatedMeshes = true;
                }
                if (AnimationsEnabled && batchState.UsesTallGrassWind)
                {
                    // TallGrass changes only in the VS, just like physics-lite changes only its
                    // instance matrix. Its recovered minimum amplitude remains nonzero even at
                    // weather strength zero, so the cached sun map must always advance while enabled.
                    ShadowDrawsIncludeAnimatedMeshes = true;
                }
            }
            drawCallMs += ElapsedMilliseconds(drawStarted);
            startInstance += (uint)(drawCount + shadowCount);
            submeshDraws++;
            LastStats.ReferenceInstancedDraws++;
            LastStats.ReferenceInstances += drawCount;
        }

        LastStats.ReferenceBatches = activeBatchCount;
    }

    private void DrawBlended(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        bool sceneDepthSampled,
        ref ID3D12PipelineState? currentPso,
        ref double cbUpdateMs,
        ref double drawCallMs,
        ref int submeshDraws,
        DeferredWaterPartition waterPartition = DeferredWaterPartition.All,
        float waterPlaneHeight = 0f)
    {
        if (_blendedDraws.Count == 0) return;

        var draws = _blendedDraws;
        if (waterPartition != DeferredWaterPartition.All)
        {
            _waterPartitionedBlendedDraws.Clear();
            foreach (var draw in _blendedDraws)
            {
                var below = WaterTransparencyPartition.IsWhollyBelow(
                    draw.WorldBoundsCenterZ,
                    draw.WorldBoundsRadius,
                    waterPlaneHeight);
                if ((waterPartition == DeferredWaterPartition.WhollyBelow && below) ||
                    (waterPartition == DeferredWaterPartition.NotWhollyBelow && !below))
                {
                    _waterPartitionedBlendedDraws.Add(draw);
                }
            }
            if (_waterPartitionedBlendedDraws.Count == 0) return;
            draws = _waterPartitionedBlendedDraws;
        }

        draws.Sort(static (a, b) => b.DistanceSquared.CompareTo(a.DistanceSquared));
        var reservations = ArrayPool<GpuRingBuffer12.RingAllocation>.Shared.Rent(draws.Count);
        try
        {
            // Reserve per-draw CBs nearest-first, then issue the surviving suffix back-to-front.
            // Optional particle-sort index allocations happen only after every survivor's CB is safe,
            // so later transient allocations can degrade sorting but can never evict a nearer object.
            var reservationStarted = StartTiming();
            var firstSelected = ReserveNearestBlendedDrawConstants(
                frameIndex, draws, reservations, out var truncated);
            cbUpdateMs += ElapsedMilliseconds(reservationStarted);
            LastFrameDrawsTruncated += truncated;
            for (var i = firstSelected; i < draws.Count; i++)
            {
                var draw = draws[i];
                if (draw.Submesh.EffectiveIndexCount <= 0 ||
                    !PassesExactGrassDistance(draw.SourceWorld.Translation, draw.IsGrass)) continue;
                var pso = _pipelines.GetBlendPipeline(
                    draw.Submesh.SrcBlendMode,
                    draw.Submesh.DstBlendMode,
                    draw.Submesh.DoubleSided,
                    draw.Submesh.IsDecal,
                    grassRoute: draw.IsGrass);
                DrawBlendedSubmesh(
                    cmd,
                    frameIndex,
                    draw,
                    pso,
                    reservations[i],
                    sceneDepthSampled,
                    ref currentPso,
                    ref cbUpdateMs,
                    ref drawCallMs);
                submeshDraws++;
                LastStats.ReferenceBlendedDraws++;
                if (sceneDepthSampled)
                {
                    LastStats.ReferenceSceneDepthBlendedDraws++;
                    if (draw.Submesh.SoftParticle.Enabled)
                    {
                        LastStats.ReferenceSoftParticleDraws++;
                        LastStats.ReferenceSoftParticleDepthSampleCount =
                            Math.Max(LastStats.ReferenceSoftParticleDepthSampleCount, _deferredSceneDepthSampleCount);
                        if (draw.Submesh.SoftParticle.Source == NifSoftParticleSource.AuthoredSoftEffect)
                        {
                            LastStats.ReferenceSoftParticleAuthoredDraws++;
                        }
                        else
                        {
                            LastStats.ReferenceSoftParticleFallbackDraws++;
                        }
                    }
                }
                if (draw.Submesh.LiveParticles is { HasLiveFrame: true })
                {
                    LastStats.ReferenceLiveParticleDraws++;
                }
            }
        }
        finally
        {
            ArrayPool<GpuRingBuffer12.RingAllocation>.Shared.Return(reservations);
        }
    }

    /// <summary>
    ///     Draws the depth-writing blend submeshes (effects-folder foliage the engine marks ZBuffer_Write,
    ///     e.g. NVSeaPlant02) accumulated by the most recent <see cref="Render(Matrix4x4, VisibilityCylinder, bool, Matrix4x4?, Vector3, CullCameraPose?, Vector3?, Vector3?)" />.
    ///     Unlike <see cref="DrawBlended" /> these run INLINE — after the opaque batches but before the
    ///     water pass — with a depth-WRITING blend PSO, so the water surface occludes them from above
    ///     instead of them painting over it. Sorted back-to-front so overlapping cards blend correctly.
    /// </summary>
    private void DrawDepthWritingBlend(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        ref ID3D12PipelineState? currentPso,
        ref double cbUpdateMs,
        ref double drawCallMs,
        ref int submeshDraws)
    {
        if (_depthWritingBlendDraws.Count == 0) return;

        _depthWritingBlendDraws.Sort(static (a, b) => b.DistanceSquared.CompareTo(a.DistanceSquared));
        var reservations = ArrayPool<GpuRingBuffer12.RingAllocation>.Shared.Rent(
            _depthWritingBlendDraws.Count);
        try
        {
            var reservationStarted = StartTiming();
            var firstSelected = ReserveNearestBlendedDrawConstants(
                frameIndex, _depthWritingBlendDraws, reservations, out var truncated);
            cbUpdateMs += ElapsedMilliseconds(reservationStarted);
            LastFrameDrawsTruncated += truncated;
            for (var i = firstSelected; i < _depthWritingBlendDraws.Count; i++)
            {
                var draw = _depthWritingBlendDraws[i];
                if (draw.Submesh.EffectiveIndexCount <= 0 ||
                    !PassesExactGrassDistance(draw.SourceWorld.Translation, draw.IsGrass)) continue;
                // BOTH blend sites must pass grassRoute. Oblivion grass authors 0x12ED (blend AND
                // test) and BuildAlphaState keeps the test live for a depth-writing blend, so grass
                // reaches this list as well as the plain one — routing only the other site would
                // light half the carpet with the shared shader and half with the per-game one.
                var pso = _pipelines.GetBlendDepthWritePipeline(
                    draw.Submesh.SrcBlendMode,
                    draw.Submesh.DstBlendMode,
                    draw.Submesh.DoubleSided,
                    draw.Submesh.IsDecal,
                    grassRoute: draw.IsGrass);
                DrawBlendedSubmesh(
                    cmd,
                    frameIndex,
                    draw,
                    pso,
                    reservations[i],
                    sceneDepthSampled: false,
                    ref currentPso,
                    ref cbUpdateMs,
                    ref drawCallMs);
                submeshDraws++;
                LastStats.ReferenceBlendedDraws++;
                if (draw.Submesh.LiveParticles is { HasLiveFrame: true })
                {
                    LastStats.ReferenceLiveParticleDraws++;
                }
            }
        }
        finally
        {
            ArrayPool<GpuRingBuffer12.RingAllocation>.Shared.Return(reservations);
        }
    }

    /// <summary>
    ///     Reserves constant-buffer space from nearest to farthest and returns the first draw in the
    ///     back-to-front suffix that survived. Quiet live-particle entries require no reservation and
    ///     are not counted as truncated draws.
    /// </summary>
    private int ReserveNearestBlendedDrawConstants(
        int frameIndex,
        List<BlendedReferenceDraw> draws,
        Span<GpuRingBuffer12.RingAllocation> reservations,
        out int truncated)
    {
        var drawable = ArrayPool<byte>.Shared.Rent(draws.Count);
        try
        {
            for (var i = 0; i < draws.Count; i++)
            {
                drawable[i] = draws[i].Submesh.EffectiveIndexCount > 0 &&
                              PassesExactGrassDistance(
                                  draws[i].SourceWorld.Translation,
                                  draws[i].IsGrass)
                    ? (byte)1
                    : (byte)0;
            }

            var capacity = BlendedDrawPriority.CountAlignedAllocations(
                _ringBuffer.CurrentFrameBytes,
                _ringBuffer.AllocationLimitBytes,
                PerDrawByteSize,
                GpuRingBuffer12.CbAlignment);
            var plan = BlendedDrawPriority.PlanNearestBackToFront(
                drawable.AsSpan(0, draws.Count), capacity);

            var firstReserved = draws.Count;
            for (var i = draws.Count - 1; i >= plan.FirstSelected; i--)
            {
                if (drawable[i] == 0) continue;
                if (_ringBuffer.TryAllocate(
                        frameIndex, PerDrawByteSize, out reservations[i], GpuRingBuffer12.CbAlignment))
                {
                    firstReserved = i;
                    continue;
                }

                // The pure capacity calculation and the bump allocator share the same alignment
                // contract, so this is only a defensive fallback if that contract changes later.
                Debug.Fail("Blended draw reservation diverged from the aligned-capacity plan.");
                truncated = 0;
                for (var j = 0; j <= i; j++)
                {
                    if (drawable[j] != 0) truncated++;
                }
                return firstReserved;
            }

            truncated = plan.TruncatedCount;
            return plan.FirstSelected;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(drawable);
        }
    }

    private void DrawBlendedSubmesh(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        BlendedReferenceDraw draw,
        ID3D12PipelineState pso,
        GpuRingBuffer12.RingAllocation perDrawAlloc,
        bool sceneDepthSampled,
        ref ID3D12PipelineState? currentPso,
        ref double cbUpdateMs,
        ref double drawCallMs)
    {
        var effectiveIndexCount = draw.Submesh.EffectiveIndexCount;
        Debug.Assert(effectiveIndexCount > 0, "Quiet live-particle frames must be filtered before drawing.");

        var cbStarted = StartTiming();
        var alphaState = draw.AlphaState;
        if (draw.Submesh.MaterialAlphaController is { } alphaController)
        {
            var sampledAlpha = alphaController.ResolveTargetAlpha(
                (float)_animationClockSeconds, AnimationsEnabled);
            // NiAlphaController::Update writes the interpolator result directly into the target
            // NiMaterialProperty alpha field. Do not multiply by the NIF's initial field value:
            // SandDust02's sStorm03/sStorm04 materials intentionally initialize it to zero.
            alphaState.Z = sampledAlpha;
            if (LastStats.ReferenceMaterialAlphaControllerDraws == 0)
            {
                LastStats.ReferenceMaterialAlphaControllerMinimum = sampledAlpha;
                LastStats.ReferenceMaterialAlphaControllerMaximum = sampledAlpha;
            }
            else
            {
                LastStats.ReferenceMaterialAlphaControllerMinimum = Math.Min(
                    LastStats.ReferenceMaterialAlphaControllerMinimum, sampledAlpha);
                LastStats.ReferenceMaterialAlphaControllerMaximum = Math.Max(
                    LastStats.ReferenceMaterialAlphaControllerMaximum, sampledAlpha);
            }

            LastStats.ReferenceMaterialAlphaControllerDraws++;
        }

        var usesTallGrassWind = _tallGrassWindSupported &&
                                draw.IsGrass && draw.Submesh.IsTallGrass;
        var tallGrassWind = BuildTallGrassWindConstants(
            usesTallGrassWind,
            draw.GrassWaveMultiplier);
        var specularLodFade = ClassicSpecularLodFade.Compute(
            in _classicSpecularLodProfile,
            draw.Submesh.LocalBoundsCenter,
            draw.Submesh.LocalBoundsRadius,
            draw.World,
            _frameCameraPosition - _frameRenderOrigin,
            specularEligible: draw.Specular.W > 0f);
        // Blended batches can be reused across frames. Resolve the runtime route here so a
        // lighting or placed-light transition cannot retain stale classic-basic shader flags.
        var textureState = ResolveTextureState(draw.Submesh);
        var perDraw = new PerDrawConstants
        {
            World = draw.World,
            AlphaState = alphaState,
            RenderState = draw.RenderState,
            TextureState = textureState,
            // Bindless TexIndices on the blended draw path. Same convention as the instanced
            // path: .x = diffuse, .y = normal, .z = FO4 specular mask OR classic environment mask
            // OR classic simple-parallax height map,
            // .w = gradient palette OR the classified Lighting30 glow map. TextureState bits
            // disambiguate both unions.
            TexIndices = new TexIndexQuad(
                draw.Submesh.Diffuse.BindlessIndex,
                draw.Submesh.Normal.BindlessIndex,
                draw.Submesh.ClassicParallaxHeightMap?.BindlessIndex ??
                draw.Submesh.ClassicEnvMask?.BindlessIndex ??
                draw.Submesh.SpecularMap?.BindlessIndex ?? 0,
                draw.Submesh.GradientMap?.BindlessIndex ??
                draw.Submesh.Lighting30GlowMap?.BindlessIndex ?? 0),
            Specular = draw.Specular,
            // Leaf-billboard camera basis (consumed only when TextureState.y marks a leaf submesh, e.g. a
            // baked particle cloud) so the blended-path VS can re-face the quads to the camera.
            CameraRight = _leafBillboardRight,
            CameraUp = _leafBillboardUp,
            EffectTint = new Vector4(draw.Submesh.EffectTint, draw.Submesh.HasEffectFalloff ? 1f : 0f),
            EffectFalloff = draw.Submesh.HasEffectFalloff
                ? draw.Submesh.EffectFalloffParams
                : draw.Submesh.Lighting30Emission,
            EnvMap = draw.Submesh.EnvMapState,
            UvScroll = new Vector4(
                WrapUv(draw.Submesh.UvScrollVelocity.X, UvScrollClock),
                WrapUv(draw.Submesh.UvScrollVelocity.Y, UvScrollClock), specularLodFade, 0f),
            // TallGrass and soft-particle material routes are mutually exclusive. The VS consumes
            // this union as WindData and zeroes vSoftParticle before the reference PS sees it.
            SoftParticle = usesTallGrassWind
                ? tallGrassWind
                : BuildSoftParticleConstants(draw.Submesh, sceneDepthSampled),
        };
        if (!ReferenceEquals(currentPso, pso))
        {
            cmd.SetPipelineState(pso);
            currentPso = pso;
        }
        unsafe { *(PerDrawConstants*)perDrawAlloc.CpuPtr = perDraw; }
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerDrawCbv, perDrawAlloc.GpuAddress);
        cbUpdateMs += ElapsedMilliseconds(cbStarted);

        var drawStarted = StartTiming();
        cmd.IASetVertexBuffers(0, draw.Submesh.EffectiveVertexBufferView);
        var indexBufferView = draw.Submesh.EffectiveIndexBufferView;
        if (draw.Submesh.EffectiveParticleCenters is { Length: > 1 } centers)
        {
            var sortedIndexCount = checked(centers.Length * ParticleDepthSort.IndicesPerQuad);
            var indexBytes = checked((uint)(sortedIndexCount * sizeof(ushort)));
            if (_ringBuffer.TryAllocate(frameIndex, indexBytes, out var sortedIndexAlloc, sizeof(ushort)))
            {
                var scratch = ArrayPool<ParticleDepthSort.Entry>.Shared.Rent(centers.Length);
                try
                {
                    unsafe
                    {
                        var destination = new Span<ushort>((void*)sortedIndexAlloc.CpuPtr, sortedIndexCount);
                        ParticleDepthSort.WriteBackToFrontQuadIndices(
                            centers,
                            draw.SourceWorld,
                            _frameCameraPosition,
                            scratch.AsSpan(0, centers.Length),
                            destination);
                    }
                }
                finally
                {
                    ArrayPool<ParticleDepthSort.Entry>.Shared.Return(scratch);
                }

                indexBufferView = new IndexBufferView
                {
                    BufferLocation = sortedIndexAlloc.GpuAddress,
                    SizeInBytes = indexBytes,
                    Format = Vortice.DXGI.Format.R16_UInt
                };
            }
        }
        cmd.IASetIndexBuffer(indexBufferView);
        cmd.DrawIndexedInstanced((uint)effectiveIndexCount, 1, 0, 0, 0);
        ObserveFnvActiveAdtBaseDraw(draw.Submesh, textureState, 1);
        if (usesTallGrassWind)
        {
            LastStats.ReferenceTallGrassDirectDraws++;
            LastStats.ReferenceTallGrassDirectInstances++;
            ObserveTallGrassWaveMultiplier(draw.GrassWaveMultiplier);
        }
        drawCallMs += ElapsedMilliseconds(drawStarted);
    }

    /// <summary>
    ///     Advances each visible shared particle owner once. A model can have many placed references, but its
    ///     local particle simulation is identical for the global animation clock and uploads only one geometry
    ///     snapshot which every placement draws. Batch-reuse frames retain the same submesh objects, so this
    ///     still runs while the resolve/build pass is frozen.
    /// </summary>
    private void UpdateLiveParticleFrames(int frameIndex)
    {
        _liveParticleOwners.Clear();
        _particleTelemetry.Clear();
        LastStats.ReferenceParticleSystems = _particleTelemetry;
        var liveStart = _ringBuffer.CurrentFrameBytes;
        // The shared ring still needs room for per-draw constants and sorted indices. Live particles may
        // consume at most 1/16 of a slot (capped at 8 MiB) and always leave its final quarter untouched.
        var liveBudget = Math.Min(_ringBuffer.BytesPerFrame / 16, 8u * 1024 * 1024);
        var ringReserve = _ringBuffer.BytesPerFrame / 4;
        UpdateList(_blendedDraws);
        UpdateList(_depthWritingBlendDraws);
        LastStats.ReferenceLiveParticleOwners = _liveParticleOwners.Count;
        LastStats.ReferenceLiveParticleUploadBytes = _ringBuffer.CurrentFrameBytes - liveStart;
        return;

        void UpdateList(List<BlendedReferenceDraw> draws)
        {
            foreach (var draw in draws)
            {
                if (draw.Submesh.LiveParticles is not { } owner || !_liveParticleOwners.Add(owner))
                {
                    continue;
                }

                LastStats.ReferenceLiveParticleAtlasFrameCount = Math.Max(
                    LastStats.ReferenceLiveParticleAtlasFrameCount, owner.AtlasFrameCount);
                LastStats.ReferenceLiveParticleAuthoredCapacity = Math.Max(
                    LastStats.ReferenceLiveParticleAuthoredCapacity, owner.AuthoredCapacity);

                string? fallbackReason = null;
                if (!LiveParticlesEnabled || !AnimationsEnabled)
                {
                    owner.UseStaticFallback();
                    if (LiveParticlesEnabled)
                    {
                        LastStats.ReferenceLiveParticleFallbacks++;
                    }
                    fallbackReason = !LiveParticlesEnabled
                        ? "live-particle path is disabled; static baked geometry is rendered"
                        : "animation playback is disabled; static baked geometry is rendered";
                }
                else
                {
                    var liveUsed = _ringBuffer.CurrentFrameBytes - liveStart;
                    var liveRemaining = liveUsed < liveBudget ? liveBudget - liveUsed : 0;
                    var ringRemaining = _ringBuffer.CurrentFrameBytes < _ringBuffer.AllocationLimitBytes
                        ? _ringBuffer.AllocationLimitBytes - _ringBuffer.CurrentFrameBytes
                        : 0;
                    var safeRingRemaining = ringRemaining > ringReserve ? ringRemaining - ringReserve : 0;
                    var ownerBudget = Math.Min(liveRemaining, safeRingRemaining);
                    if (ownerBudget == 0)
                    {
                        owner.UseStaticFallback();
                        LastStats.ReferenceLiveParticleFallbacks++;
                        fallbackReason = "transient live-particle GPU budget is exhausted; static baked geometry is rendered";
                    }
                    else if (!owner.TryAdvance(
                                 frameIndex, _ringBuffer, _animationClockSeconds, ownerBudget))
                    {
                        LastStats.ReferenceLiveParticleFallbacks++;
                        fallbackReason = owner.LastFailure ??
                                         "live-particle update failed; static baked geometry is rendered";
                    }
                }

                var renderedCount = draw.Submesh.EffectiveParticleCenters?.Length ?? 0;
                LastStats.ReferenceParticleRenderedCount += renderedCount;
                _particleTelemetry.Add(owner.CaptureTelemetry(renderedCount, fallbackReason));
                if (!owner.HasLiveFrame)
                {
                    continue;
                }

                LastStats.ReferenceLiveParticleParticles += owner.ParticleCenters?.Length ?? 0;
                if (LastStats.ReferenceLiveParticleUvFrame < 0 && owner.CurrentUvFrame >= 0)
                {
                    LastStats.ReferenceLiveParticleUvFrame = owner.CurrentUvFrame;
                }
            }
        }
    }

    private void BindReferenceInstanceBuffer(
        ID3D12GraphicsCommandList cmd,
        ulong instanceGpuAddress,
        ref int srvBinds,
        ref double srvBindMs)
    {
        var srvStarted = StartTiming();
        cmd.SetGraphicsRootShaderResourceView((uint)GpuRootSignature12.Slots.ReferenceInstanceSrv, instanceGpuAddress);
        srvBinds++;
        srvBindMs += ElapsedMilliseconds(srvStarted);
    }

    private long StartTiming() => DetailedProfilingEnabled ? Stopwatch.GetTimestamp() : 0;

    private static double ElapsedMilliseconds(long started) =>
        started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static int ParsePositiveIntEnvironment(string name, int defaultValue, int min, int max)
    {
        var raw = EnvironmentVariables.Get(name);
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static double ParsePositiveDoubleEnvironment(string name, double defaultValue, double min, double max)
    {
        var raw = EnvironmentVariables.Get(name);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }

    private static uint AlignUp(uint value, uint alignment) =>
        alignment == 0 ? value : ((value + alignment - 1) / alignment) * alignment;

    // NiUVController scroll clock, gated by the Animations toggle (off ⇒ every offset is exactly 0,
    // so a disabled frame is byte-identical to a pre-animation build).
    private double UvScrollClock => AnimationsEnabled ? _animationClockSeconds : 0.0;

    /// <summary>Fractional UV phase for a scrolling material: frac(velocity × clock), computed in
    /// double so a long-running clock keeps sub-texel precision before the wrap. Zero velocity
    /// returns exactly 0 — non-scrolling submeshes stay byte-static.</summary>
    private static float WrapUv(float velocity, double clockSeconds)
    {
#pragma warning disable S1244 // exactly-zero velocity keeps non-scrolling submeshes byte-static by design
        if (velocity == 0f)
#pragma warning restore S1244
        {
            return 0f;
        }

        var phase = velocity * clockSeconds;
        return (float)(phase - Math.Floor(phase));
    }

    /// <summary>
    ///     GRASS2000 WindData: xy = fixed world +Y; z = lerp of the recovered 5/125 setting defaults
    ///     by the weather wind fraction; w = wrapped time phase in radians. Retail uses
    ///     BSShaderManager's default timer, whose units remain unproven; this renderer adapts its
    ///     existing animation-clock value directly. The raw GRAS field is a direct multiplier,
    ///     never a reciprocal. Disabled animations rest.
    /// </summary>
    private Vector4 BuildTallGrassWindConstants(bool enabled, float grassWaveMultiplier)
    {
        if (!enabled)
        {
            return Vector4.Zero;
        }

        return new Vector4(
            Vector2.UnitY,
            AnimationsEnabled ? FnvTallGrassWind.ComputeWindMagnitude(_windStrength) : 0f,
            FnvTallGrassWind.ComputeTimePhaseRadians(
                AnimationsEnabled ? _animationClockSeconds : 0.0,
                grassWaveMultiplier));
    }

    private void ObserveTallGrassWaveMultiplier(float grassWaveMultiplier)
    {
        var multiplier = FnvTallGrassWind.SanitizeWaveMultiplier(grassWaveMultiplier);
#pragma warning disable S1244 // SanitizeWaveMultiplier returns the exact 0f sentinel for no-wave grass
        if (multiplier == 0f)
#pragma warning restore S1244
        {
            return;
        }

        var phase = FnvTallGrassWind.ComputeTimePhaseRadians(
            AnimationsEnabled ? _animationClockSeconds : 0.0,
            multiplier);
        if (_tallGrassWaveMultipliers.Count == 0)
        {
            LastStats.ReferenceTallGrassWaveMultiplierMinimum = multiplier;
            LastStats.ReferenceTallGrassWaveMultiplierMaximum = multiplier;
            LastStats.ReferenceTallGrassTemporalPhaseRadiansMinimum = phase;
            LastStats.ReferenceTallGrassTemporalPhaseRadiansMaximum = phase;
        }
        else
        {
            LastStats.ReferenceTallGrassWaveMultiplierMinimum = Math.Min(
                LastStats.ReferenceTallGrassWaveMultiplierMinimum, multiplier);
            LastStats.ReferenceTallGrassWaveMultiplierMaximum = Math.Max(
                LastStats.ReferenceTallGrassWaveMultiplierMaximum, multiplier);
            LastStats.ReferenceTallGrassTemporalPhaseRadiansMinimum = Math.Min(
                LastStats.ReferenceTallGrassTemporalPhaseRadiansMinimum, phase);
            LastStats.ReferenceTallGrassTemporalPhaseRadiansMaximum = Math.Max(
                LastStats.ReferenceTallGrassTemporalPhaseRadiansMaximum, phase);
        }

        _tallGrassWaveMultipliers.Add(multiplier);
        LastStats.ReferenceTallGrassWaveMultiplierDistinctCount = _tallGrassWaveMultipliers.Count;
    }

    /// <summary>
    ///     Per-draw scene-depth contract. Only policy-approved effects get a depth slot; ordinary
    ///     transparency keeps the read-only DSV's exact hardware test. Positive falloff fades
    ///     alpha, while negative falloff fades RGB (multiplicative neutrality is selected from
    ///     EnvMap.w in the shader).
    /// </summary>
    private Vector4 BuildSoftParticleConstants(CachedSubmesh12 submesh, bool sceneDepthSampled)
    {
        if (!sceneDepthSampled || _deferredSceneDepthIndex == NoSceneDepth ||
            !submesh.SoftParticle.Enabled)
        {
            return Vector4.Zero;
        }

        var signedFalloff = submesh.SoftParticle.FadeTarget == NifSoftParticleFadeTarget.Alpha
            ? submesh.SoftParticle.FalloffDepth
            : -submesh.SoftParticle.FalloffDepth;
        return new Vector4(
            NifSoftParticleDepthBinding.Encode(
                _deferredSceneDepthIndex, _deferredSceneDepthSampleCount),
            _deferredSceneDepthNear,
            _deferredSceneDepthFar,
            signedFalloff);
    }

    /// <summary>
    ///     Builds the authored camera-facing world matrix for a submesh that sat under a
    ///     <c>NiBillboardNode</c>. Rotate-about-up modes spin only around world Z; face-camera and
    ///     face-center modes can pitch as well as yaw. The REFR placement rotation is ignored — only its world
    ///     position and uniform scale are kept. The bake already dropped the billboard node's own
    ///     rotation (NifSceneGraphWalker.WalkNode), so the quad sits at <paramref name="worldCenter" />
    ///     in its authored local orientation, re-aimable here.
    ///     <para>
    ///         The local front axis comes from the submesh's indexed winding. This preserves the
    ///         historical +Y route while correctly handling effects such as FNV FireBall09, whose
    ///         single-sided visible front is authored along -Y.
    ///     </para>
    /// </summary>
    private static Matrix4x4 BuildBillboardWorld(
        Vector3 localBoundsCenter, NifBillboardMode mode, Vector3 authoredFrontNormal,
        Matrix4x4 world, Vector3 worldCenter, Vector3 cameraPosition, Vector3 cameraForward,
        Vector3 renderOrigin)
    {
        var toCam = cameraPosition - worldCenter;
        // Uniform scale = length of the placement matrix's first row (X axis).
        var scale = new Vector3(world.M11, world.M12, world.M13).Length();
        var rs = Matrix4x4.CreateScale(scale) * NifBillboardFacing.ResolveRotation(
            mode, authoredFrontNormal, toCam, cameraForward);
        // Anchor the quad so its local bounds center maps to the placement's world position, then fold in
        // the camera-relative render origin (0 on absolute paths) so the uploaded matrix matches the
        // camera-relative space the reference VS now projects in — see the Render(renderOrigin) docs.
        rs.Translation = worldCenter - Vector3.TransformNormal(localBoundsCenter, rs) - renderOrigin;
        return rs;
    }

    private static float ResolveWorldBoundsRadius(CachedSubmesh12 submesh, Matrix4x4 world)
    {
        var scaleX = new Vector3(world.M11, world.M12, world.M13).Length();
        var scaleY = new Vector3(world.M21, world.M22, world.M23).Length();
        var scaleZ = new Vector3(world.M31, world.M32, world.M33).Length();
        return submesh.LocalBoundsRadius * MathF.Max(scaleX, MathF.Max(scaleY, scaleZ));
    }

    /// <summary>
    ///     Batch-reuse frames keep the frozen blended draw lists but must refresh what the camera
    ///     moves: every entry's sort distance (back-to-front order stays correct across in-cell
    ///     drift) and, for billboards, the camera-facing world matrix. Everything else on the entry
    ///     is placement/material state that reuse guarantees unchanged.
    /// </summary>
    private void RefreshBlendedDraws(
        Vector3 cameraPosition, Vector3 cameraForward, Vector3 renderOrigin)
    {
        RefreshBlendedDrawList(_blendedDraws, cameraPosition, cameraForward, renderOrigin);
        RefreshBlendedDrawList(_depthWritingBlendDraws, cameraPosition, cameraForward, renderOrigin);
    }

    private void RefreshBlendedDrawList(
        List<BlendedReferenceDraw> draws, Vector3 cameraPosition, Vector3 cameraForward,
        Vector3 renderOrigin)
    {
        var span = CollectionsMarshal.AsSpan(draws);
        for (var i = 0; i < span.Length; i++)
        {
            ref var draw = ref span[i];
            var sampledSourceWorld = ApplyPhysicsLiteSway(
                draw.Submesh, draw.PhysicsLiteSeed, draw.SourceWorld);
            var worldCenter = Vector3.Transform(draw.Submesh.LocalBoundsCenter, sampledSourceWorld);
            var sampledRelativeWorld = sampledSourceWorld;
            sampledRelativeWorld.Translation -= renderOrigin;
            var world = draw.Submesh.IsBillboard
                ? BuildBillboardWorld(
                    draw.Submesh.LocalBoundsCenter, draw.Submesh.BillboardMode,
                    draw.Submesh.BillboardFrontNormal, sampledSourceWorld, worldCenter,
                    cameraPosition, cameraForward,
                    renderOrigin)
                : sampledRelativeWorld;
            draw = draw with
            {
                World = world,
                DistanceSquared = Vector3.DistanceSquared(worldCenter, cameraPosition),
                WorldBoundsCenterZ = worldCenter.Z,
                WorldBoundsRadius = ResolveWorldBoundsRadius(draw.Submesh, sampledSourceWorld),
            };
        }
    }

    /// <summary>
    ///     Applies the strict FNV physics-lite descriptor before the REFR placement. The descriptor
    ///     lives in baked mesh-root-local space, so row-vector order is sway * placement. Turning the
    ///     GUI Animations switch off returns the byte-identical rest matrix; no batch rebuild or disk
    ///     cache invalidation is required.
    /// </summary>
    private Matrix4x4 ApplyPhysicsLiteSway(
        CachedSubmesh12 submesh,
        uint placedReferenceSeed,
        Matrix4x4 restWorld)
    {
        if (!AnimationsEnabled || submesh.PhysicsLiteSway is not { } descriptor)
        {
            return restWorld;
        }

        var sample = descriptor.Evaluate(_animationClockSeconds, placedReferenceSeed);
        return sample.Applied ? sample.Transform * restWorld : restWorld;
    }

    /// <summary>
    ///     Per-instance generic exact cull for draw passes with an armed frustum, mirroring the
    ///     establishment cull's
    ///     resident-mesh tests WITHOUT the tolerant drift slack: square footprint reach, small-prop
    ///     distance LOD, frustum sphere. The batches hold the widened survivor superset; this keeps
    ///     the actually-drawn set exact for the CURRENT camera every frame — including on frozen
    ///     (reused) batches, where it is what makes camera drift inside the cull slack artifact-free.
    ///     The grass hard end is intentionally separate in the copy loops so disabling reference
    ///     frustum culling cannot call <c>IntersectsSphere</c> on an uninitialized frustum.
    /// </summary>
    private bool PassesExactCull(Vector4 bounds)
    {
        var rdx = MathF.Abs(bounds.X - _frameCylinderX);
        var rdy = MathF.Abs(bounds.Y - _frameCylinderY);
        var reach = _frameCylinderRadius + bounds.W + FrustumCullMargin;
        if (rdx > reach || rdy > reach)
        {
            return false;
        }

        var horizontalDistanceSquared = (rdx * rdx) + (rdy * rdy);
        if (_enableDistanceLod && bounds.W < SmallPropLodRadius
                               && horizontalDistanceSquared > _frameSmallPropCutoffSq)
        {
            return false;
        }

        return _frameFrustum.IntersectsSphere(
            new Vector3(bounds.X, bounds.Y, bounds.Z), bounds.W + FrustumCullMargin);
    }

    private bool PassesExactGrassDistance(Vector3 absolutePlacement, bool isGrass)
        => PassesExactGrassDistance(
            HorizontalDistanceSquared(absolutePlacement, _frameCylinderX, _frameCylinderY),
            isGrass);

    private bool PassesExactGrassDistance(float horizontalDistanceSquared, bool isGrass)
        => GrassDistanceCullPolicy.Passes(
            isGrass,
            in _grassDistanceEnvelope,
            horizontalDistanceSquared,
            _frameCylinderRadius);

    private static float HorizontalDistanceSquared(Vector3 placement, float cameraX, float cameraY)
    {
        var dx = placement.X - cameraX;
        var dy = placement.Y - cameraY;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    ///     Per-instance runtime SpeedTree LOD selection. Every component batch carries all placements;
    ///     filtering while copying worlds into the frame ring keeps exactly one logical LOD active even
    ///     when batch reuse freezes the CPU-side lists while the camera continues moving.
    /// </summary>
    private bool PassesSpeedTreeLod(
        CachedSubmesh12 submesh,
        Matrix4x4 world,
        Vector4 absoluteBounds)
    {
        if (submesh.SpeedTreeLod is not { IsValid: true } metadata)
        {
            return true;
        }

        var placement = new Vector3(absoluteBounds.X, absoluteBounds.Y, absoluteBounds.Z);
        return SelectsSpeedTreeLod(world, placement, metadata);
    }

    /// <summary>Shadow-only placements do not carry a parallel bounds array; recover their absolute
    /// origin from the render-origin-folded world matrix.</summary>
    private bool PassesSpeedTreeLod(CachedSubmesh12 submesh, Matrix4x4 world)
    {
        if (submesh.SpeedTreeLod is not { IsValid: true } metadata)
        {
            return true;
        }

        var placement = world.Translation + _frameRenderOrigin;
        return SelectsSpeedTreeLod(world, placement, metadata);
    }

    private bool SelectsSpeedTreeLod(
        Matrix4x4 world,
        Vector3 absolutePlacement,
        in SpeedTreeLodMetadata metadata)
    {
        var scaleBasis = new Vector3(world.M11, world.M12, world.M13);
        var uniformScale = scaleBasis.Length();
        var distance = Vector3.Distance(_frameCameraPosition, absolutePlacement);
        return SpeedTreeRuntimeLod.SelectLevelForPlacement(distance, uniformScale, metadata) == metadata.Level;
    }

    /// <summary>
    ///     One deferred blended (transparent) reference submesh draw, frozen with everything the
    ///     back-to-front pass needs to re-sort and re-issue it.
    /// </summary>
    /// <param name="SourceWorld">
    ///     The ABSOLUTE placement world matrix, kept so batch-reuse frames can refresh what the
    ///     camera moves without re-resolving the reference: the back-to-front sort distance and,
    ///     for billboards, the camera-facing <paramref name="World" /> matrix.
    /// </param>
    private readonly record struct BlendedReferenceDraw(
        Matrix4x4 World,
        CachedSubmesh12 Submesh,
        float DistanceSquared,
        Vector4 AlphaState,
        Vector4 RenderState,
        Vector4 Specular,
        Matrix4x4 SourceWorld,
        uint PhysicsLiteSeed,
        bool IsGrass,
        float GrassWaveMultiplier,
        float WorldBoundsCenterZ,
        float WorldBoundsRadius);
}
#endif

