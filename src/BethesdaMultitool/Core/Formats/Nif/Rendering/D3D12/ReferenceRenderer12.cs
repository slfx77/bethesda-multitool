#if WINDOWS_GUI
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Effects;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Procedural;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Orchestration;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using static BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRendererConstants12;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     D3D12 placed-object renderer. Opaque/cutout references are batched by cached submesh
///     and rendered with instancing; blended/effect submeshes remain sorted back-to-front.
/// </summary>
internal sealed class ReferenceRenderer12 : Abstractions.IReferenceRenderer
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
    private static readonly int MaxNewUploadsPerFrame = EnvironmentVariables.GetClampedInt(
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

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly GpuRingBuffer12 _ringBuffer;
    private readonly GpuDescriptorHeapAllocator12 _cbvSrvUavHeap;
    private readonly GpuDeletionQueue12 _deletionQueue;
    private readonly ReferenceMeshCache12 _meshCache;
    private readonly ReferenceEnabledOverrideStore _enabledOverrides;
    private readonly ReferencePipelineFactory12 _pipelines;
    private readonly ID3D12CommandSignature? _opaqueIndirectSignature;
    // The published set is immutable between batch publications. Refresh-only rebuilds populate the
    // other set over several render-thread slices, then swap the aggregate in one assignment so no
    // draw pass can observe a partial opaque/blended/shadow snapshot.
    private ReferenceBatchSet _publishedBatchSet = new();
    private ReferenceBatchSet _stagingBatchSet = new();
    // Double-buffered ReferenceBatchSet object identity is ABA-prone (A -> B -> A). Packet reuse
    // therefore keys on a publication ordinal that advances on every swap, even when the rebuilt
    // content fingerprint happens to be unchanged.
    private ulong _publishedBatchIdentity;
    private int _publishedBatchGeneration;
    private OpaqueSubmissionPacket12? _opaqueSubmissionPacket;
    private StaticOpaquePacketReuseKey? _opaquePacketCandidateKey;
    private int _opaquePacketCandidateFrames;
    private OpaqueBatchRegistry12 _opaqueBatches => _publishedBatchSet.OpaqueBatches;
    private List<BlendedReferenceDraw> _blendedDraws => _publishedBatchSet.BlendedDraws;
    private readonly List<BlendedReferenceDraw> _waterPartitionedBlendedDraws = new(256);
    // Retained scratch for opaque-submission telemetry. Reference identity is the batch-key
    // contract for PSOs too, and retaining the set avoids a per-frame allocation on this hot path.
    private readonly HashSet<ID3D12PipelineState> _opaqueSubmissionPsos =
        new(ReferenceEqualityComparer.Instance);
    private enum DeferredWaterPartition
    {
        All,
        WhollyBelow,
        NotWhollyBelow,
        // Draws no queued water surface can occlude (bounds bottom AND camera above the highest
        // queued surface). Issued after the whole stream + water drain — the unified-stream
        // complement of WhollyBelow. NotWhollyBelow excludes these when the unified path supplies
        // a finite queued-surface plane.
        WhollyAboveAllWater,
    }
    private readonly HashSet<LiveParticleOwner12> _liveParticleOwners = [];
    private readonly List<global::BethesdaMultitool.Core.WorldData.ParticleRenderTelemetry> _particleTelemetry = [];
    // Depth-writing blend foliage (effects-folder shapes the engine marks ZBuffer_Write, e.g. NVSeaPlant02):
    // kept as alpha blend but drawn INLINE before the water pass with a depth-writing PSO, so water occludes
    // them from above. Separate from _blendedDraws, which defers to after the water pass.
    private List<BlendedReferenceDraw> _depthWritingBlendDraws => _publishedBatchSet.DepthWritingBlendDraws;
    private GrassDistanceEnvelope _grassDistanceEnvelope;
    private ClassicSpecularLodProfile _classicSpecularLodProfile;
    private bool _tallGrassWindSupported;

    // True only for the game whose grass draws through the INSTANCED+BLENDED route (TES4). Scopes the
    // grass shadow-caster exclusion below to that route, because the batch-level grass marker
    // (UsesGrassDistanceEnvelope) is NOT game-specific — Skyrim (3500/1000) and FO3/FNV (7000/1000)
    // set an envelope too, so keying the exclusion on it alone silently stopped THEIR grass casting.
    private bool _instancedBlendGrassSupported;
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

    // Outer band of the same mechanism, admitting LANDMARK-scale refs only (see the FAR RING note in
    // TestCandidate). Derived from the cascade radii at arm time so it tracks the shadow map's real
    // coverage instead of a second magic number.
    private float _shadowCasterFarRingRadius;

    /// <summary>Widest cascade radius whose texel density still resolves a recognisable shadow, and
    /// therefore the outer bound of the landmark caster band. The 4-cascade ladder is
    /// {2048, 8192, 32768, 131072}; at a 2048-px map the 32768 cascade is ~16 world units/texel
    /// (a landmark shadow is still tens of texels) while the 131072 cascade is ~64, where a missing
    /// off-screen caster genuinely is hard to notice — the assumption the ORIGINAL 8192 ring made one
    /// cascade too early.</summary>
    private const float ShadowCasterFarRingCap = 32768f;

    // Cascade geometry for this frame's capture (see ArmShadowCapture). Null ⇒ no per-cascade culling.
    private (Vector3 Anchor, Vector3 SunDirection, float[] Radii, float[] Snaps, float SceneZSpan)? _cascadeFit;
    // Anchor from the frame that established the widened cull survivor set. Incremental rebuilds
    // classify around this fixed horizontal centre so every later camera pose accepted by that cull
    // epoch is within one (not two opposing) CullPositionSlack envelope of the published prefixes.
    // Z stays on the current post-cull shadow fit because its loaded-extent clamp can legitimately
    // change immediately after a mesh-bounds recull.
    private Vector2? _cullCacheCascadeAnchorXY;

    // Scratch for the per-batch counting sort that orders instances by smallest containing cascade.
    private readonly List<int> _cascadeBucketScratch = new(256);
    private readonly List<Matrix4x4> _cascadeSortMatrices = new(256);
    private readonly List<Vector4> _cascadeSortBounds = new(256);
    private readonly List<uint> _cascadeSortSeeds = new(256);
    private readonly List<uint> _cascadeSortHeatmapIds = new(256);

    private readonly List<RenderableReference> _cachedShadowOnlyCasters = new(512);
    private float _cullCacheShadowRing;
    private float _cullCacheShadowFarRing;

    /// <summary>Instance count this draw must submit for each cascade. Because the block is in cascade
    /// order these are prefixes, so a cascade draws <c>[0, Count(i))</c> — exactly what the captured
    /// per-draw CB's fixed instance base allows.</summary>
    private readonly record struct CascadeCounts(int C0, int C1, int C2, int C3)
    {
        public int this[int index] => index switch
        {
            0 => C0,
            1 => C1,
            2 => C2,
            _ => C3
        };

        public static CascadeCounts Uniform(int count) => new(count, count, count, count);

        public static CascadeCounts From(ReadOnlySpan<int> counts, int fallback) => counts.Length < 4
            ? Uniform(fallback)
            : new CascadeCounts(counts[0], counts[1], counts[2], counts[3]);
    }

    private readonly record struct ShadowDraw(
        VertexBufferView VertexBufferView, IndexBufferView IndexBufferView, int IndexCount,
        ulong PerDrawCbAddress, ulong InstanceSrvAddress, int DrawCount, bool AlphaTested,
        CascadeCounts Cascades,
        bool UsesTallGrassWind);

    /// <summary>
    ///     One captured instanced-opaque COLOR draw for the water-reflection mirror replay: the
    ///     original PSO plus the frame-local ring addresses of its per-draw constants and t8
    ///     instance window — valid exactly until the frame's allocations recycle, so the replay
    ///     must run in the SAME frame (unlike sky-only reflection content). Main-frustum survivors
    ///     only (no shadow-only tail); decals and grass are excluded at capture.
    /// </summary>
    private readonly record struct MirrorDraw(
        VertexBufferView VertexBufferView, IndexBufferView IndexBufferView, int IndexCount,
        ulong PerDrawCbAddress, ulong InstanceSrvAddress, int DrawCount,
        ID3D12PipelineState Pso);

    private readonly List<MirrorDraw> _mirrorDraws = new(512);
    private bool _mirrorCaptureArmed;

    /// <summary>Arms per-frame mirror-draw capture (call before the main render; the list is
    /// consumed by <see cref="RenderMirrorColor" /> the same frame).</summary>
    public void ArmMirrorCapture()
    {
        _mirrorDraws.Clear();
        _mirrorCaptureArmed = true;
    }

    /// <summary>Disarms mirror capture and drops any captured draws.</summary>
    public void DisarmMirrorCapture()
    {
        _mirrorCaptureArmed = false;
        _mirrorDraws.Clear();
    }

    /// <summary>Number of mirror draws captured this frame (diagnostics/capture parity).</summary>
    public int MirrorDrawCount => _mirrorDraws.Count;

    /// <summary>Bumped when the loaded reference population, resident drawable content
    /// (GPU upload/eviction), or animation eligibility changes. Camera-driven cull/batch rebuilds
    /// deliberately do not bump it. The shadow
    /// cache combines this with <see cref="VisibilityKey" /> so non-spatial filters invalidate shadows
    /// without treating every camera rebuild as new content.</summary>
    public int BatchContentVersion { get; private set; }

    /// <summary>True when this frame's draw pass captured at least one shadow-caster draw.</summary>
    public bool HasShadowDraws => _shadowDraws.Count > 0;

    /// <summary>Number of shadow-caster draws captured this frame (diagnostics).</summary>
    public int ShadowDrawCount => _shadowDraws.Count;

    /// <summary>Exact draw-call count submitted by the most recent
    /// <see cref="RenderShadowDepth" /> invocation, after per-cascade filtering.</summary>
    public int LastShadowSubmittedDrawCount { get; private set; }

    /// <summary>Exact instance count submitted by the most recent
    /// <see cref="RenderShadowDepth" /> invocation, after per-cascade filtering.</summary>
    public int LastShadowSubmittedInstanceCount { get; private set; }

    /// <summary>
    ///     True when the latest cascade replay reached an authoritative result, including the valid
    ///     zero-draw case where no captured instance belongs to that cascade. False only when replay
    ///     could not run (for example, ring allocation failed).
    /// </summary>
    public bool LastShadowReplayCompleted { get; private set; }

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
    /// <param name="cascadeFit">
    ///     World-space centre the cascades will be fitted around this frame, and the sun direction they
    ///     will be fitted to. Supplied at ARM time so the per-cascade instance classification below uses
    ///     bit-identical inputs to the frustums the pass later builds — classifying against a slightly
    ///     different anchor would let a caster be sorted into a cascade whose box no longer contains it.
    ///     Snaps carries each cascade's pose-key snap quantum: a cascade re-rendered in place replays
    ///     its PUBLISHED frustum, whose fit anchor may lag the classification anchor by up to the snap
    ///     per axis — the classifier widens each cascade's reach by its own quantum so those casters
    ///     survive the prefix. Null keeps the whole caster set in every cascade (no per-cascade culling).
    /// </param>
    public void ArmShadowCapture(
        float casterRingRadius,
        (Vector3 Anchor, Vector3 SunDirection, float[] Radii, float[] Snaps, float SceneZSpan)? cascadeFit = null)
    {
        _shadowDraws.Clear();
        ShadowDrawsIncludeAnimatedLeaves = false;
        ShadowDrawsIncludeAnimatedMeshes = false;
        _shadowCaptureArmed = true;
        _shadowCasterRingRadius = MathF.Max(casterRingRadius, 0f);
        // Track the shadow map's actual coverage, capped where texel density stops resolving a
        // shadow. With no cascade fit (capture paths) fall back to the cap itself — those are
        // one-shot renders where streaming a few extra landmark meshes costs nothing per-frame.
        var farRing = ShadowCasterFarRingCap;
        if (cascadeFit is { Radii: { Length: > 0 } radii })
        {
            farRing = MathF.Min(radii[^1], ShadowCasterFarRingCap);
        }

        _shadowCasterFarRingRadius = MathF.Max(_shadowCasterRingRadius, farRing);
        _cascadeFit = cascadeFit;
    }

    /// <summary>Disarms the capture (shadows off / interior): the next cull keeps no shadow-only
    /// casters and the copy pass uploads none.</summary>
    public void DisarmShadowCapture()
    {
        _shadowCaptureArmed = false;
        _shadowCasterRingRadius = 0f;
        _shadowCasterFarRingRadius = 0f;
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

    private bool _formIdHeatmapEnabled;

    /// <summary>
    ///     Debug "FormID heatmap" overlay: placed REFRs within
    ///     <see cref="FormIdHeatmapRangeCells" /> of the camera render in a smooth blue→red ramp
    ///     keyed on FormID (sequential at authoring time — cold = authored early, hot = authored
    ///     late), normalized against the min/max FormID currently in range. Mirrors
    ///     <see cref="AnimationsEnabled" />'s <see cref="BatchContentVersion" /> bump on each real
    ///     transition, and additionally invalidates the last build: the build loop records
    ///     per-instance FormIDs (<c>OpaqueBatchState.HeatmapFormIds</c>) only while enabled, so a
    ///     frozen batch from the other state can never serve — the next frame's
    ///     <c>ResolveReuseBlocker</c> then re-runs the resolve+batch pass (the cull cache may stand;
    ///     the survivor set itself is unchanged).
    /// </summary>
    public bool FormIdHeatmapEnabled
    {
        get => _formIdHeatmapEnabled;
        set
        {
            if (_formIdHeatmapEnabled == value)
            {
                return;
            }

            _formIdHeatmapEnabled = value;
            _lastBuildValid = false;
            unchecked { _batchInvalidationGeneration++; }
            unchecked
            {
                BatchContentVersion++;
            }
        }
    }

    private bool _grassShadowsEnabled = true;

    /// <summary>
    ///     Video-settings "Shadows on grass" (retail <c>bShadowsOnGrass:Display</c>): whether grass
    ///     RECEIVES the sun shadow map. Grass never CASTS, matching the engine — this only gates
    ///     the receive term. Off ORs <see cref="FnvActiveAdtBasePolicy.RuntimeFnvGrassNoSunShadowFlag" />
    ///     into every grass submesh's packed texture state (the per-game grass shaders honor it),
    ///     so the setter forces a batch rebuild like <see cref="FormIdHeatmapEnabled" />.
    /// </summary>
    public bool GrassShadowsEnabled
    {
        get => _grassShadowsEnabled;
        set
        {
            if (_grassShadowsEnabled == value)
            {
                return;
            }

            _grassShadowsEnabled = value;
            _lastBuildValid = false;
            unchecked { _batchInvalidationGeneration++; }
            unchecked { BatchContentVersion++; }
        }
    }

    private bool _windowReflectionsEnabled = true;

    /// <summary>
    ///     Video-settings "Window reflections" (retail <c>bDynamicWindowReflections:Display</c>):
    ///     gates the classic environment-map term (window/glass materials) at per-draw constants
    ///     fill. Off suppresses the env-map slot so tinted glass renders as plain textured
    ///     geometry. Setter forces a batch rebuild like <see cref="FormIdHeatmapEnabled" />.
    /// </summary>
    public bool WindowReflectionsEnabled
    {
        get => _windowReflectionsEnabled;
        set
        {
            if (_windowReflectionsEnabled == value)
            {
                return;
            }

            _windowReflectionsEnabled = value;
            _lastBuildValid = false;
            unchecked { _batchInvalidationGeneration++; }
            unchecked { BatchContentVersion++; }
        }
    }

    private bool _treeShadowsEnabled = true;

    /// <summary>
    ///     Video-settings "Tree canopy shadows" (retail <c>bDoCanopyShadowPass:Display</c>): whether
    ///     tree canopy geometry casts into the sun shadow map (the viewer's real cascades stand in
    ///     for retail's projected canopy textures). Off excludes SpeedTree leaf cards and branch
    ///     geometry from shadow capture and from the off-frustum caster resolve — the trees remain
    ///     fully VISIBLE (this is a caster-only gate, unlike the category hides). The setter bumps
    ///     <see cref="BatchContentVersion" /> so cached shadow cascades re-render without the stale
    ///     caster set.
    /// </summary>
    public bool TreeShadowsEnabled
    {
        get => _treeShadowsEnabled;
        set
        {
            if (_treeShadowsEnabled == value)
            {
                return;
            }

            _treeShadowsEnabled = value;
            _lastBuildValid = false;
            unchecked { _batchInvalidationGeneration++; }
            unchecked { BatchContentVersion++; }
        }
    }

    /// <summary>
    ///     Heatmap selection range in WHOLE cells: a ref participates when its position's cell is
    ///     within this many cells of the camera's cell on both axes (Chebyshev — a full
    ///     (2N+1)×(2N+1) block of the cell grid, never a circular slice through cells).
    ///     <see cref="float.PositiveInfinity" /> = unlimited (the whole loaded visible set — the
    ///     min/max scan still clamps to the render cylinder, since nothing beyond it can draw).
    ///     Read per frame by the copy pass, so changing it needs no rebuild.
    /// </summary>
    public float FormIdHeatmapRangeCells { get; set; } = float.PositiveInfinity;

    /// <summary>
    ///     The oldest/newest FormID the last frame's scan ranked (over every VISIBLE placed ref in the
    ///     selected cells), or null while the heatmap is off / no eligible ref is in range. Feeds the
    ///     settings panel's live key labels; written and read on the UI thread (the render loop is
    ///     <c>CompositionTarget.Rendering</c>).
    /// </summary>
    public (uint Min, uint Max)? FormIdHeatmapWindow =>
        _frameHeatmapActive ? (_frameHeatmapRanking.Min, _frameHeatmapRanking.Max) : null;

    /// <summary>
    ///     How many distinct FormIDs the last scan ranked — the number of ramp steps in use. Shown
    ///     beside the key so the user can tell "one colour because one step" from "one colour because
    ///     the gradient collapsed".
    /// </summary>
    public int FormIdHeatmapRankedCount => _frameHeatmapActive ? _frameHeatmapRanking.DistinctCount : 0;

    /// <summary>
    ///     Temporary opt-in legacy live-particle owner. Defaults from
    ///     <c>FALLOUT_VIEWER_LIVE_PARTICLES=1</c>; false keeps immutable cached particle clouds.
    /// </summary>
    public bool LiveParticlesEnabled { get; set; } = ParticleLiveSettings.Enabled;
    private readonly List<global::BethesdaMultitool.Core.WorldData.WorldSpatialCell> _candidateCells = new();

    // Candidates returned by the per-cell spatial broadphase before exact sphere/frustum cull.
    private readonly List<RenderableReference> _cullCandidateScratch = new(256);

    // Visible cells materialized once per establishment so the cull can index (and partition) them.
    private readonly List<CellRecord> _cullCellScratch = new(1024);

    /// <summary>Outcome of one candidate's establishment test (see the cull's TestCandidate).</summary>
    private enum CullVerdict
    {
        Culled,
        Survivor,

        /// <summary>Frustum-rejected but inside the sun-shadow caster ring: casts, never drawn.</summary>
        ShadowOnlyCaster,
    }

    /// <summary>Per-partition scratch for the parallel establishment cull. Reused across frames —
    /// an establishment allocating fresh lists would undo the frame's allocation budget.</summary>
    private sealed class CullPartitionState
    {
        public readonly List<RenderableReference> Candidates = new(256);
        public readonly List<RenderableReference> Survivors = new(1024);
        public readonly List<RenderableReference> ShadowOnly = new(128);
        public int CellsVisited;
        public int CandidateCount;
        public int Culled;

        public void Reset()
        {
            Survivors.Clear();
            ShadowOnly.Clear();
            CellsVisited = 0;
            CandidateCount = 0;
            Culled = 0;
        }
    }

    private CullPartitionState[]? _cullPartitions;

    /// <summary>Upper bound on establishment partitions — past this the merge and the shared spatial
    /// index dominate, and the render thread should not monopolise every core.</summary>
    private const int MaxCullPartitions = 8;

    /// <summary>Cells per partition below which threading costs more than it saves.</summary>
    private const int ParallelCullMinCellsPerPartition = 24;

    /// <summary>Parallel establishment cull (default ON). "0" restores the single-threaded loop —
    /// the A/B lever for proving the partitioned merge is order-identical.</summary>
    private static readonly bool ParallelCullEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.ParallelCull) != "0";

    // Opt-in until a settled direct/indirect capture proves both the CPU win and GPU non-regression.
    private static readonly bool OpaqueIndirectRequested =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ReferenceOpaqueIndirect);

    // Opt-in until the settled direct/packet capture proves the CPU saving and GPU non-regression.
    private static readonly bool StaticOpaquePacketRequested =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ReferenceStaticOpaquePacket);

    // Opt-in until strict paired captures prove that coarse batch ordering reduces real PS work.
    private static readonly bool OpaqueFrontToBackRequested =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ReferenceOpaqueFrontToBack);

    private void EnsureCullPartitions(int count)
    {
        if (_cullPartitions is not null && _cullPartitions.Length >= count) return;
        var grown = new CullPartitionState[count];
        for (var i = 0; i < count; i++)
        {
            grown[i] = _cullPartitions is not null && i < _cullPartitions.Length
                ? _cullPartitions[i]
                : new CullPartitionState();
        }

        _cullPartitions = grown;
    }

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
    private int _cullCacheDayNightVersion;
    private int _cullCacheCellsVisited;
    private int _cullCacheCandidates;
    private int _cullCacheCulled;

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

    // === FormID-heatmap frame state ===
    // Computed once per frame by UpdateFormIdHeatmapScan; consumed by the instanced copy passes and
    // the blended per-draw path via ApplyFormIdHeatmapTint. The ranking holds the distinct FormIDs of
    // the VISIBLE non-grass placed refs in the selected whole-cell block around the camera's cell, so
    // each one gets its own evenly-spaced ramp step; Active is false when the heatmap is off or the
    // scan found no eligible ref.
    private bool _frameHeatmapActive;
    private readonly FormIdHeatmapRanking _frameHeatmapRanking = new();
    // Whole-cell selection gate shared by the scan and the per-instance tint: camera cell +
    // Chebyshev radius in cells (int.MaxValue = unlimited) on the active grid's cell size.
    private int _frameHeatmapCamCellX;
    private int _frameHeatmapCamCellY;
    private int _frameHeatmapCellRadius = int.MaxValue;
    private float _frameHeatmapCellSize = WorldGridConstants.CellSize;
    // Memo key — the same shape as GetRenderedSceneWorldZExtentCached's epoch memo, extended by the
    // camera CELL and range because a cell-block scan depends on both: the scan is O(refs in
    // range), so a static frame must not re-walk every placement list. Keying on the cell (not the
    // position) also keeps the window fixed while the camera moves inside one cell.
    private int _heatmapScanEpoch = -1;
    private int _heatmapScanCamCellX;
    private int _heatmapScanCamCellY;
    private int _heatmapScanCellRadius;
    private float _heatmapScanRadius;
    // The visibility gates the scan honored. Hiding a ref (per-instance override, category, Show
    // Disabled, day/night) must RE-RANK the survivors, which is the point of excluding them: the
    // user prunes noise and the remaining refs spread back over the whole ramp.
    private ReferenceVisibilityKey _heatmapScanVisibilityKey;

    /// <summary>
    ///     Recomputes the frame's FormID-heatmap ranking: the distinct FormIDs of every VISIBLE placed
    ///     (non-grass, non-synthetic) reference whose position falls inside the selected block of WHOLE
    ///     cells centred on the camera's cell (Chebyshev distance ≤ N on the cell grid — full cells,
    ///     never a circular slice, so the selection is exactly "these cells").
    ///     <para>
    ///         The ranking is ORDINAL (see <see cref="FormIdHeatmapRanking" />): consecutive refs in
    ///         authoring order are consecutive ramp steps regardless of the numeric gaps between their
    ///         FormIDs, which is what keeps the gradient readable in a worldspace with late additions.
    ///     </para>
    ///     <para>
    ///         Refs the current visibility gates hide are EXCLUDED — a ref the user manually switched to
    ///         Hidden, a hidden category, an initially-disabled ref while Show Disabled is off, or a
    ///         day/night ref that is off at this hour. Nothing draws for them, so letting them consume
    ///         ramp steps would only compress the refs that do draw; excluding them makes manual pruning
    ///         an effective way to spread the survivors back out.
    ///     </para>
    ///     Deliberately no frustum — the selection, and therefore every ref's colour, is stable under
    ///     camera ROTATION and only re-ranks when the camera crosses a cell boundary, the range changes,
    ///     or a visibility gate changes. Enumeration clamps to the loaded visible radius: cells wholly
    ///     beyond it cannot draw. Memoized per (cull epoch, camera cell, range, visible radius,
    ///     visibility key).
    /// </summary>
    private void UpdateFormIdHeatmapScan(float cameraX, float cameraY, float cameraZ, float visibleRadius)
    {
        if (!_formIdHeatmapEnabled || _renderCache is null)
        {
            _frameHeatmapActive = false;
            _heatmapScanEpoch = -1;
            return;
        }

        var cellSize = _spatialIndex?.CellSize ?? WorldGridConstants.CellSize;
        var range = FormIdHeatmapRangeCells;
        var visibilityKey = VisibilityKey;
        _frameHeatmapCellSize = cellSize;
        _frameHeatmapCamCellX = (int)MathF.Floor(cameraX / cellSize);
        _frameHeatmapCamCellY = (int)MathF.Floor(cameraY / cellSize);
        _frameHeatmapCellRadius = float.IsPositiveInfinity(range)
            ? int.MaxValue
            : Math.Max(0, (int)MathF.Round(range));
#pragma warning disable S1244 // memoization identity: exact compare of cached snapshot values detects change, not proximity
        if (_heatmapScanEpoch == _cullEpoch &&
            _heatmapScanCamCellX == _frameHeatmapCamCellX &&
            _heatmapScanCamCellY == _frameHeatmapCamCellY &&
            _heatmapScanCellRadius == _frameHeatmapCellRadius &&
            _heatmapScanRadius == visibleRadius &&
            _heatmapScanVisibilityKey == visibilityKey)
        {
            return;
        }
#pragma warning restore S1244

        _heatmapScanEpoch = _cullEpoch;
        _heatmapScanCamCellX = _frameHeatmapCamCellX;
        _heatmapScanCamCellY = _frameHeatmapCamCellY;
        _heatmapScanCellRadius = _frameHeatmapCellRadius;
        _heatmapScanRadius = visibleRadius;
        _heatmapScanVisibilityKey = visibilityKey;

        // Cylinder radius covering the selected cell block's farthest corner (the camera can sit
        // on its cell's edge, so the block reaches (N+1) cells along an axis — √2× that on the
        // diagonal), clamped to the loaded visible radius.
        var scanRadius = _frameHeatmapCellRadius == int.MaxValue
            ? visibleRadius
            : MathF.Min(visibleRadius, MathF.Sqrt(2f) * (_frameHeatmapCellRadius + 1) * cellSize);
        _frameHeatmapRanking.Reset();
        foreach (var cell in EnumerateVisibleCells(
                     new VisibilityCylinder(new Vector3(cameraX, cameraY, cameraZ), scanRadius)))
        {
            var placements = _renderCache.GetPlacementList(cell);
            for (var i = 0; i < placements.Length; i++)
            {
                var r = placements[i];
                // Synthetic placements (engine-scattered grass, FormId 0) carry no authoring order.
                if (r.IsGrass || r.FormId == 0)
                {
                    continue;
                }

                // Exactly the cull's own gate, so the ranked set is the set that renders.
                if (!visibilityKey.IsVisible(r, _enabledOverrides, DayNightStates))
                {
                    continue;
                }

                var position = r.WorldMatrix.Translation;
                if (!IsInsideHeatmapCellBlock(position.X, position.Y))
                {
                    continue;
                }

                _frameHeatmapRanking.Add(r.FormId);
            }
        }

        _frameHeatmapRanking.Seal();
        _frameHeatmapActive = !_frameHeatmapRanking.IsEmpty;
    }

    /// <summary>
    ///     Whether a world position lies inside the frame's selected block of whole cells. The
    ///     SAME predicate gates the scan and the per-instance tint, so the normalization window and
    ///     the tinted set always describe the same cells (a ref placed outside its authoring cell
    ///     is judged by where it stands — the selection is a clean grid-aligned square in space).
    /// </summary>
    private bool IsInsideHeatmapCellBlock(float worldX, float worldY)
    {
        if (_frameHeatmapCellRadius == int.MaxValue)
        {
            return true;
        }

        return Math.Abs((int)MathF.Floor(worldX / _frameHeatmapCellSize) - _frameHeatmapCamCellX) <= _frameHeatmapCellRadius &&
               Math.Abs((int)MathF.Floor(worldY / _frameHeatmapCellSize) - _frameHeatmapCamCellY) <= _frameHeatmapCellRadius;
    }

    /// <summary>
    ///     Writes the FormID-heatmap tint into <paramref name="world" />'s otherwise-unused w-lanes
    ///     (M14/M24/M34 = ramp rgb, M44 = 2.0 active flag — read as HLSL <c>world[3]</c>): the same
    ///     channel FNV grass packs its lighting payload into, which is why grass instances are
    ///     excluded (recorded as FormID 0). The 2.0 flag is unambiguous — affine matrices carry
    ///     exactly 1.0 there and the grass payload's baked light is clamped ≤ 0.99. Both reference
    ///     VSes force <c>worldPos.w</c> back to 1.0 after transforming, so the payload never reaches
    ///     clip space; the shadow VS/PS never read the new interpolant, so casters are unaffected.
    ///     No-ops for refs outside the selected cell block — those render normally.
    /// </summary>
    private void ApplyFormIdHeatmapTint(ref Matrix4x4 world, uint formId, float worldX, float worldY)
    {
        if (formId == 0)
        {
            return;
        }

        if (!IsInsideHeatmapCellBlock(worldX, worldY))
        {
            return;
        }

        var rgb = FormIdHeatmapPalette.ToVector3(_frameHeatmapRanking.Normalize(formId));
        world.M14 = rgb.X;
        world.M24 = rgb.Y;
        world.M34 = rgb.Z;
        world.M44 = 2f;
    }

    // Placed-NIF water geometry (WaterShaderProperty triangles diverted out of the drawable submesh
    // set at upload), retained together with its owning REFR. Each surface is transformed exactly once;
    // the registry dynamically republishes only owners that pass the renderer's non-spatial gates.
    private readonly PlacedNifWaterRegistry _nifWaterRegistry = new();
    private readonly bool _disableReferenceFrustum =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.DisableReferenceFrustum);

    // A5 distance-LOD is now OFF by default — it was removing things the user wanted to see.
    // Opt back in with FALLOUT_VIEWER_REFERENCE_DISTANCE_LOD=1 once the bounds are trusted.
    private readonly bool _enableDistanceLod =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.ReferenceDistanceLod);

    private Dictionary<(int gx, int gy), CellRecord>? _cells;
    private global::BethesdaMultitool.Core.WorldData.WorldSpatialIndex? _spatialIndex;
    private global::BethesdaMultitool.Core.WorldData.WorldRenderCache? _renderCache;
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
        GpuDeletionQueue12 deletionQueue,
        BethesdaGame game,
        ReferenceEnabledOverrideStore? enabledOverrides = null)
    {
        _gpu = gpu;
        _recorder = recorder;
        _ringBuffer = ringBuffer;
        _cbvSrvUavHeap = cbvSrvUavHeap;
        _deletionQueue = deletionQueue;
        _meshCache = meshCache;
        _enabledOverrides = enabledOverrides ?? new ReferenceEnabledOverrideStore();

        _pipelines = new ReferencePipelineFactory12(gpu, rootSignature, game);

        if (OpaqueIndirectRequested || StaticOpaquePacketRequested)
        {
            try
            {
                var indirectArguments = new IndirectArgumentDescription[4];
                indirectArguments[0].Type = IndirectArgumentType.ConstantBufferView;
                indirectArguments[0].ConstantBufferView.RootParameterIndex =
                    GpuRootSignature12.Slots.PerDrawCbv;
                indirectArguments[1].Type = IndirectArgumentType.VertexBufferView;
                indirectArguments[1].VertexBuffer.Slot = 0;
                indirectArguments[2].Type = IndirectArgumentType.IndexBufferView;
                indirectArguments[3].Type = IndirectArgumentType.DrawIndexed;
                var indirectSignatureDescription = new CommandSignatureDescription
                {
                    ByteStride = OpaqueIndirectCommand12.ByteStride,
                    IndirectArguments = indirectArguments,
                };
                _opaqueIndirectSignature = gpu.Device.CreateCommandSignature<ID3D12CommandSignature>(
                    indirectSignatureDescription,
                    rootSignature.RootSignature);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Core.Diagnostics.Logger.Instance.Warn(
                    "ReferenceRenderer12: opaque ExecuteIndirect disabled because command-signature " +
                    "creation failed: {0}",
                    ex.Message);
            }
        }
    }

    public int ReferencesDrawnLastFrame { get; private set; }
    public global::BethesdaMultitool.Core.WorldData.WorldRenderStats LastStats { get; } = new();

    /// <summary>
    ///     Model paths whose mesh resolved to nothing in the last resolve pass — the names behind
    ///     <c>ReferenceMeshMissing</c>. While streaming is in flight this also contains
    ///     still-decoding paths; sampled AFTER quiescence it is exactly the terminally-unresolvable
    ///     set, which is what capture harnesses report.
    /// </summary>
    public IReadOnlyList<string> LastFrameMissingMeshPaths => _lastFrameMissingMeshPaths;

    private readonly List<string> _lastFrameMissingMeshPaths = [];
    public bool DetailedProfilingEnabled { get; set; }
    public bool ShowInitiallyDisabled { get; set; }
    private int _placedLightCount;
    private double _placedLightTileBuildMilliseconds;
    private int _placedLightTileCount;
    private int _placedLightTileUploadBytes;
    private double _placedLightTileAverageLights;
    private int _placedLightTileMaxLights;
    private double _placedLightTileEmptyPercent;
    private string? _placedLightTileFallbackReason;
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
    ///     Supplies the host-built tiled-forward mask telemetry for the next main reference pass.
    ///     Mirror/capture bindings may replace the pending values after that pass, but cannot mutate
    ///     the already-published <see cref="LastStats" /> row.
    /// </summary>
    public void SetPlacedLightTileTelemetry(
        double buildMilliseconds,
        int tileCount,
        int uploadBytes,
        double averageLights,
        int maxLights,
        double emptyPercent,
        string? fallbackReason)
    {
        _placedLightTileBuildMilliseconds = Math.Max(0, buildMilliseconds);
        _placedLightTileCount = Math.Max(0, tileCount);
        _placedLightTileUploadBytes = Math.Max(0, uploadBytes);
        _placedLightTileAverageLights = Math.Max(0, averageLights);
        _placedLightTileMaxLights = Math.Max(0, maxLights);
        _placedLightTileEmptyPercent = Math.Clamp(emptyPercent, 0, 100);
        _placedLightTileFallbackReason = fallbackReason;
    }

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

    /// <summary>
    ///     Current-hour day/night reference states (scripted street lights, glow FX, fire barrels).
    ///     Owned and re-evaluated by the host as the game hour advances; its Version joins
    ///     <see cref="VisibilityKey" /> so every cull/shadow/water cache refreshes when a scripted
    ///     light network flips at dawn or dusk.
    /// </summary>
    public DayNightRefStateStore? DayNightStates { get; set; }

    /// <summary>
    ///     Exact non-spatial visibility snapshot for renderer-adjacent caches. This intentionally omits
    ///     camera/cylinder state and the host's global Meshes parent.
    /// </summary>
    internal ReferenceVisibilityKey VisibilityKey => ReferenceVisibilityKey.Capture(
        _enabledOverrides,
        _hiddenCategories,
        ShowInitiallyDisabled,
        ShowGrass,
        ShowMarkers,
        ShowImposters,
        DayNightStates);

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
        // Change-DETECTING: the cull cache key does not carry the category set, so this setter is the
        // only thing that invalidates for it — but invalidating on an IDENTICAL set is pure waste.
        // The 2D map's top-down overlay calls this twice per render pass (apply the legend's set, then
        // restore the 3D view's own set in its finally block), which pinned the cull cache invalid on
        // every pass of a static whole-worldspace overlay: measured cullCacheHit=False on 583/583
        // WastelandNV zoom-to-fit passes at 52-130 ms of cull each, re-testing 1.29 M candidates that
        // could not have changed. With both sets equal — the common case, and always so when neither
        // view hides anything — this is now a no-op and the cache survives.
        _categoryScratch.Clear();
        foreach (var category in hidden) _categoryScratch.Add(category);
        if (_categoryScratch.SetEquals(_hiddenCategories)) return;

        _hiddenCategories.Clear();
        foreach (var category in _categoryScratch) _hiddenCategories.Add(category);
        _cullCacheValid = false;
    }

    /// <summary>Reusable buffer for <see cref="SetHiddenCategories" />'s change detection (render
    /// thread only, like the rest of the cull scratch state).</summary>
    private readonly HashSet<PlacedObjectCategory> _categoryScratch = [];

    /// <summary>
    ///     Number of per-draw allocations that didn't fit the shared ring buffer this frame and were
    ///     skipped (instead of throwing + abandoning the whole frame). Non-zero means the scene is
    ///     denser than the ring can hold in one frame — raise <c>FALLOUT_VIEWER_RING_BUFFER_MB</c>.
    ///     Surfaced in the HUD so the soft cap is visible rather than silent.
    /// </summary>
    public int LastFrameDrawsTruncated { get; private set; }

    /// <summary>
    ///     World-space authored water geometry detected on currently eligible placed-reference NIFs
    ///     (cave/pool/reflecting-pool water). The host hands this to
    ///     <see cref="WaterRenderer12.SetNifWaterPlanes" /> so placed water renders with the real water
    ///     shader instead of a flat slab. The published list reference remains stable while its contents
    ///     track per-reference, authored, category, grass, marker, and imposter visibility. It remains
    ///     independent of the host's global Meshes parent because the water pass is a sibling layer.
    /// </summary>
    public IReadOnlyList<NifWaterGeometry> NifWaterPlanes =>
        _nifWaterRegistry.GetPublished(VisibilityKey, _enabledOverrides);

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
        global::BethesdaMultitool.Core.WorldData.WorldRenderCache renderCache,
        Dictionary<(int gx, int gy), CellRecord> cells,
        global::BethesdaMultitool.Core.WorldData.WorldSpatialIndex? spatialIndex)
    {
        // The game is deliberately not part of the frame reuse key: this explicit world-load seam
        // invalidates both the packet and its debounced candidate before the new game is visible.
        RetireOpaqueSubmissionPacket();
        _renderCache = renderCache;
        _cells = cells;
        _spatialIndex = spatialIndex;
        _tallGrassWindSupported = FnvTallGrassWind.IsSupported(renderCache.Game);
        _instancedBlendGrassSupported = GrassShaderProfile.InstancedBlendForGame(renderCache.Game).Enabled;
        _grassDistanceEnvelope = GrassScatterProfile.ForGame(renderCache.Game).DistanceEnvelope;
        _classicSpecularLodProfile = ClassicSpecularLodProfile.ForGame(renderCache.Game);
        // Per-game grass shaders. Resolved here — beside the other ForGame registries — because this
        // is where the loaded game first reaches the renderer; the pipeline factory is constructed
        // earlier, so its grass PSOs are built lazily on first grass draw.
        _pipelines.SetGrassShaderProfile(GrassShaderProfile.ForGame(renderCache.Game));
        _pipelines.SetInstancedGrassShaderProfile(
            GrassShaderProfile.InstancedForGame(renderCache.Game));
        _pipelines.SetInstancedBlendGrassShaderProfile(
            GrassShaderProfile.InstancedBlendForGame(renderCache.Game));
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
        unchecked { _batchInvalidationGeneration++; }
        unchecked { BatchContentVersion++; }
        // Drop placed-NIF water ownership so it cannot leak across worldspace loads. The registry
        // clears its stable publication list in place for consumers that retained the reference.
        _nifWaterRegistry.Clear();

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
        // SpeedTree leaf cards never receive a sun shadow in retail (STLEAF*.vso declare no shadow
        // input; STLEAF2000/2001.pso bind only DiffuseMap). Applied to FO3 as well as FNV because they
        // share the STLEAF shader family and the same .spt pipeline. Kept OUTSIDE the FNV block below
        // deliberately: that block also decides ADT-base eligibility, which is recovered for FNV only,
        // and widening it would drag FO3 onto an unrecovered path for an unrelated reason.
        // Oblivion joined 2026-08-19 (user report: hard shadow edge halfway down each leaf card at
        // noon): TES4 ships NO shadow-map sampling in any shaderpackage shader — its tree darkening
        // is the authored CNAM canopy dimming, which the viewer already applies. Sampling our
        // cascades on a CAMERA-facing card whose caster was re-faced LIGHT-perpendicular
        // (SHADOW_CARD_LIGHT_FACING) self-shadows the card's lower half with a hard boundary at
        // the caster plane. Leaves keep casting (canopy shadows on trunks/ground are the feature);
        // only the leaf RECEIVE is exempt, exactly like FNV/FO3.
        if (_renderCache?.Game is Core.Games.BethesdaGame.FalloutNewVegas
            or Core.Games.BethesdaGame.Fallout3
            or Core.Games.BethesdaGame.Oblivion && submesh.IsLeafBillboard)
        {
            state.Z = (uint)MathF.Round(state.Z)
                | FnvActiveAdtBasePolicy.RuntimeSpeedTreeLeafNoSunShadowFlag;
        }

        // Grass shadow fallback follows the STLEAF pattern above — hoisted OUT of the FNV-only ADT
        // block (FO3 parity 2026-08-10): both games ship byte-identical GRASS2002 shaders, so the
        // shared-shader fallback approximation applies to FO3 grass too now that FO3 plants grass.
        // Shared-shader fallback only: retail shadows grass's SUN term but never its ambient
        // (GRASS2002.pso), which the shared shader cannot express — exempting the draw entirely is
        // the closer approximation there. The per-game grass shader implements the real split and
        // ignores this bit.
        if (_renderCache?.Game is Core.Games.BethesdaGame.FalloutNewVegas
            or Core.Games.BethesdaGame.Fallout3 && submesh.IsTallGrass)
        {
            state.Z = (uint)MathF.Round(state.Z)
                | FnvActiveAdtBasePolicy.RuntimeFnvGrassNoSunShadowFlag;
        }

        // Video-settings "Shadows on grass" OFF: TallGrass carries the no-sun-shadow bit here;
        // scattered LAND grass has no submesh marker, so its OR happens at the instanced-batch
        // call site where the grass route is known. Retail semantics are receive-only — the
        // caster side is already excluded unconditionally (grassNeverCasts).
        if (!_grassShadowsEnabled && submesh.IsTallGrass)
        {
            state.Z = (uint)MathF.Round(state.Z)
                | FnvActiveAdtBasePolicy.RuntimeFnvGrassNoSunShadowFlag;
        }

        if (_renderCache?.Game == Core.Games.BethesdaGame.FalloutNewVegas)
        {
            // ADT-base eligibility stays FNV-only per the documented recovery scope; the grass
            // bit above is already set, and ApplyRuntimeFlags preserves incoming flags.
            var eligibility = ResolveFnvActiveAdtBaseEligibility(submesh);
            state.Z = FnvActiveAdtBasePolicy.ApplyRuntimeFlags(
                eligibility,
                (uint)MathF.Round(state.Z));
        }
        return state;
    }

    /// <summary>
    ///     Applies the "Window reflections" video setting (<see cref="WindowReflectionsEnabled" />,
    ///     retail <c>bDynamicWindowReflections</c>) at per-draw constants fill: when OFF, every
    ///     window-flagged classic env-map draw — FNV BSShaderFlags bit 21 SLS2058 glass AND the
    ///     TES3/TES4-era NiTextureEffect sphere maps, which ride the same window sign convention —
    ///     has its env slot suppressed (x = −1, scale 0) so the glass renders as plain textured
    ///     geometry. Ordinary non-window env maps (FO3/FNV shiny metal, FO4 BGSM cubes) are
    ///     untouched. The .w lane (blend operation) is preserved — the fog term reads it for every
    ///     draw regardless of env state.
    /// </summary>
    private Vector4 ResolveEnvMapState(CachedSubmesh12 submesh)
    {
        var state = submesh.EnvMapState;
        if (!_windowReflectionsEnabled && submesh.ClassicEnvMapUsesWindowReflection)
        {
            state.X = -1f;
            state.Y = 0f;
            state.Z = 0f;
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
        global::BethesdaMultitool.Core.WorldData.WorldRenderCache renderCache,
        Dictionary<(int gx, int gy), CellRecord> cells)
    {
        var altTextureIndex = renderCache.AlternateTextureIndex;
        var swapIndex = renderCache.MaterialSwapIndex;
        var baseSwapIndex = renderCache.BaseMaterialSwapIndex;
        var bendableSplineIndex = renderCache.BendableSplineIndex;
        var seen = new HashSet<(string Path, uint AltBase, uint Swap)>();
        foreach (var cell in cells.Values)
        {
            foreach (var placed in cell.PlacedObjects)
            {
                if (placed.BendableSpline is not null &&
                    bendableSplineIndex is not null &&
                    bendableSplineIndex.ContainsKey(placed.BaseFormId))
                {
                    seen.Add((BendableSplineGeometry.BuildCacheKey(placed.FormId), 0u, 0u));
                    continue;
                }

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
    ///     <para>
    ///         LIVE-LOOP ONLY. Unlike the leaf billboard basis, this is not per-frame state a later frame
    ///         re-supplies: the rig INTEGRATES its oscillator clocks from the (strength, time) history it
    ///         is fed. A one-shot offscreen render that pins a pose through here (map overlay, export,
    ///         thumbnail) therefore corrupts the live sway — passing time 0 leaves foldStrength 0, and the
    ///         next live frame's phase-continuity rescale (<c>_matrixTimes *= foldStrength / S</c>) zeroes
    ///         every oscillator, restarting all four canopy groups in unison at the cos extreme. Use
    ///         <see cref="SetWindForCapture" /> for anything that is not the live per-frame loop.
    ///     </para>
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
    ///     <para>
    ///         This is the seam EVERY non-live renderer must use — profiler captures, the 2D map's
    ///         top-down overlay, one-shot exports. The selection is per-render host state that the live
    ///         loop's <see cref="SetWind" /> clears on its next tick, so an offscreen pose pin can never
    ///         perturb the live rig's integrated phase.
    ///     </para>
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
    ///     Max camera ROTATION (degrees) the tolerant cull cache absorbs, the angular twin of
    ///     <see cref="CullPositionSlack" />. The establishment cull widens its frustum half-angles by
    ///     the same amount, so the cached survivor set stays a provable SUPERSET for any orientation
    ///     within the slack — the draw pass then trims it back with the frame's exact frustum
    ///     (<see cref="PassesExactCull" />), so the drawn set is unchanged.
    ///     <para>
    ///         Without this the pose compare was byte-exact on the camera basis, so a single pixel of
    ///         mouse movement re-culled the whole candidate set: measured at a 0% cache hit rate and
    ///         ~55 ms of cull per frame for the entire duration of a turn, against 70-75% and ~0 ms
    ///         while travelling in a straight line.
    ///     </para>
    ///     <para>
    ///         4 deg is ~2x the slack a steady orbit needs before translation becomes the binding
    ///         constraint again, and costs ~9% more survivors (the widened frustum's extra solid
    ///         angle). Larger values buy longer reuse during fast turns at the cost of a bigger
    ///         batched superset — hence the 20 deg clamp.
    ///     </para>
    /// </summary>
    private static readonly float CullAngleSlackRadians =
        EnvironmentVariables.GetClampedInt(EnvironmentVariables.Viewer.TolerantCullDegrees, 4, 0, 20)
        * (MathF.PI / 180f);

    /// <summary>Keeps the broadphase frustum armed while the shadow caster ring is active (see the
    /// broadphase comment in Render). "0" restores the previous unfiltered broadphase.</summary>
    private static readonly bool BroadphaseShadowFrustumEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.BroadphaseShadowFrustum) != "0";

    /// <summary>Per-cascade culling of the shadow replay (see SortBatchInstancesByCascade).
    /// "0" submits the whole caster set to every cascade — the pre-change behaviour.</summary>
    private static readonly bool ShadowCascadeCullEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.ShadowCascadeCull) != "0";

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

    /// <summary>
    ///     cos(angular slack) the ESTABLISHMENT cull was widened by, captured when the cache was
    ///     written. Storing the establishment-time value rather than reading the constant at compare
    ///     time keeps the cache honest if the slack ever becomes dynamic, and lets a declined widening
    ///     (orthographic) fall back to an exact compare via the 1f sentinel. Never read unless
    ///     <see cref="_cullCacheValid" /> is set, so it needs no explicit reset.
    /// </summary>
    private float _cullCacheForwardCos = 1f;

    private int _framesSinceCull;

    /// <summary>Bumped on every fresh cull — identifies the survivor-set generation (batch reuse keys on it).</summary>
    private int _cullEpoch;

    // === Batch reuse on quiesced frames ===
    // When the cull cache hit, the render origin (snapped by the host) is unchanged, and the LAST
    // build frame streamed nothing (quiescent: every mesh + texture resolved, zero uploads/misses,
    // hence zero evictions mid-build), the frozen batches re-draw. Refresh-only blockers may also
    // spend a bounded slice populating an invisible staging set while those batches draw. Safety
    // rails: any resident-mesh eviction bumps the cache's
    // EvictionGeneration (frozen batches reference CachedSubmesh12 GPU buffers), and the draw pass
    // re-tests every instance against the current exact frustum (PassesExactCull), so a frozen
    // WIDENED batch stays visually exact while the camera drifts inside the cull slack.
    private static readonly bool BatchReuseEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.BatchReuse) != "0";

    /// <summary>Unified transparency stream (default ON): blended reference draws and FNV water
    /// batches merge into ONE back-to-front sequence — the engine's single depth-sorted alpha
    /// stream (decompile-proven; see the fnv_alpha_zwrite_order_re memory note). "0" restores the
    /// split hoisted-blend → below-water → water → deferred-blend pass order.</summary>
    internal static readonly bool UnifiedTransparencyEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.UnifiedTransparency) != "0";

    /// <summary>Engine z-write rule inside the unified stream (default ON): per-draw z-write is
    /// the decompile-proven ambient-ON minus the authored exceptions baked into
    /// <c>CachedSubmesh12.EngineZWriteOff</c> (decal / hair flag / NoLighting flags2-bit0-clear) —
    /// so an ordinary blended LIT shape writes depth, which is what resolves WhiteHorseNettle.
    /// "0" falls back to the legacy blend+test+threshold classification for the per-draw choice.
    /// Effective ONLY while the stream is interleaving (stream-off routes always use the legacy
    /// hoist, decided at routing time — the serialized bits are env-blind).</summary>
    internal static readonly bool EngineZWriteEnabled =
        EnvironmentVariables.Get(EnvironmentVariables.Viewer.EngineZWrite) != "0";

    /// <summary>
    ///     Diagnostic: FALLOUT_VIEWER_BLEND_TRACE=&lt;hex REFR FormID&gt; logs every unified-stream
    ///     draw belonging to that placed reference — sorted position, source shape block, view-depth
    ///     key, and the resolved depth write/test — the exact data needed to diagnose "shape A
    ///     composites over shape B" reports. 0 (unset) in normal runs.
    /// </summary>
    private static readonly uint BlendTraceFormId =
        uint.TryParse(
            EnvironmentVariables.Get("FALLOUT_VIEWER_BLEND_TRACE")?.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber, null, out var blendTraceId)
            ? blendTraceId
            : 0;

    /// <summary>
    ///     Diagnostic companion to <see cref="BlendTraceFormId" />:
    ///     FALLOUT_VIEWER_BLEND_SKIP_BLOCK=&lt;n&gt; suppresses the traced reference's submesh with
    ///     that source block index, isolating which shape paints contested pixels.
    /// </summary>
    private static readonly int BlendSkipBlock =
        int.TryParse(EnvironmentVariables.Get("FALLOUT_VIEWER_BLEND_SKIP_BLOCK"), out var blendSkip)
            ? blendSkip
            : -1;

    /// <summary>Stable ascending sort for the blended index array: primary key from a parallel
    /// float array, ties broken by the ORIGINAL draw index (the array is initialized 0..n-1, so
    /// comparing element values IS comparing registration order) — the managed equivalent of the
    /// engine's stable merge sort over alpha passes. Reused across frames; set
    /// <see cref="Keys" /> before sorting.</summary>
    private sealed class BlendedOrderComparer : IComparer<int>
    {
        public float[] Keys = [];

        public int Compare(int left, int right)
        {
            var byKey = Keys[left].CompareTo(Keys[right]);
            return byKey != 0 ? byKey : left.CompareTo(right);
        }
    }

    private readonly BlendedOrderComparer _blendedOrderComparer = new();

    // View-axis depth column (clip-space w row of the frame's scene viewProj), captured at Render
    // so the blended sort keys use the SAME metric — numerically, not just ordinally — as the
    // water renderer's batch depths. That is what makes the two streams mergeable.
    private Vector4 _frameWColumn;

    /// <summary>
    ///     Consecutive QUIET build frames (no uploads, no cache misses, no pending textures)
    ///     required before reuse engages. A single quiet frame is not proof the scene finished
    ///     filling — decode waves have sub-second lulls, and freezing on one stalled the fill at
    ///     ~80% (drawn froze while 12k meshes were still decoding). A half-second streak is.
    /// </summary>
    private const int QuietBuildStreakFrames = 30;

    /// <summary>
    ///     Reuse ceiling for a build made while content was still streaming (not settled).
    ///     <para>
    ///         Measured: with settling as a hard PRECONDITION, "not quiesced" was the first failing
    ///         reuse clause on 58.7% of frames during motion — streaming never quiesces while the
    ///         camera moves, so the 7-11 ms resolve+batch pass re-ran even on cull-cached frames.
    ///         Reuse itself is safe mid-stream (the draw pass re-tests every instance against the
    ///         frame's exact frustum, and eviction still invalidates separately); the only cost is
    ///         that content resolved since the build appears late. So hold such a build on a SHORT
    ///         leash rather than refusing to hold it at all.
    ///     </para>
    /// </summary>
    private static readonly int BatchReuseStreamingMaxFrames = EnvironmentVariables.GetClampedInt(
        EnvironmentVariables.Viewer.BatchReuseStreamingFrames, 4, 1, 60);

    /// <summary>
    ///     Best-effort render-thread allowance for a refresh-only batch rebuild. Upload pacing is
    ///     independent; this budget covers reference traversal, instance bucketing, inactive registry
    ///     reset and cascade ordering. Work is checked only between bounded quanta so the cursor always
    ///     advances; collection clears and publication refresh can still overshoot and are exposed by
    ///     the split profiler timings.
    /// </summary>
    private static readonly double IncrementalBatchBuildBudgetMilliseconds = ParsePositiveDoubleEnvironment(
        EnvironmentVariables.Viewer.ReferenceBatchBuildMillisecondsPerFrame,
        defaultValue: 2.0,
        min: 0.25,
        max: 10.0);

    /// <summary>Why a frame did not reuse its cached cull survivors. Ordered as the gate tests them,
    /// so the reported value is the FIRST failing clause.</summary>
    internal enum CullCacheVeto
    {
        None = 0,
        /// <summary>Something explicitly invalidated the cache (LoadData / SetHiddenCategories).</summary>
        Invalidated,
        /// <summary>A mesh resolved since the snapshot and no debounce applied.</summary>
        MeshBounds,
        ShowMarkers,
        ShowGrass,
        ShowImposters,
        ShowDisabled,
        EnabledOverrides,
        ShadowRing,
        /// <summary>Tolerant mode: camera pose left the reuse tolerance.</summary>
        Pose,
        /// <summary>Exact mode: the view-projection differs from the snapshot.</summary>
        ViewProj,
        /// <summary>Exact mode: the visibility cylinder differs from the snapshot.</summary>
        Cylinder,
    }

    /// <summary>Why a frame did not reuse its frozen batches. Ordered as the gate tests them, so the
    /// reported value is the FIRST failing clause.</summary>
    internal enum BatchReuseBlocker
    {
        None = 0,
        Disabled = 1,
        CullCacheMiss = 2,
        NoBuild = 3,
        FrameCeiling = CaptureSceneCensus.FrameCeilingBatchBuildTriggerCode,
        CullEpoch = 5,
        RenderOrigin = 6,
        NotQuiesced = 7,
        Eviction = 8,
        StreamRouting = 9,
        /// <summary>The build being considered still had unresolved meshes; freezing it would stop
        /// the resolve pass from ever requesting their decodes.</summary>
        MeshesMissing = 10
    }

    internal enum BatchBuildPhase
    {
        None = 0,
        Reset = 1,
        References = 2,
        ShadowCasters = 3,
        CascadeSort = 4,
        Complete = 5
    }

    private enum BatchBuildAdvanceResult
    {
        Incomplete,
        Complete,
        EvictionInvalidated
    }

    private enum CascadeBatchSortPhase
    {
        Initialize,
        MainClassify,
        MainScatter,
        MainCopy,
        ShadowInitialize,
        ShadowClassify,
        ShadowScatter,
        ShadowCopy,
        Complete
    }

    /// <summary>
    ///     Cursor for one opaque batch's stable cascade counting-sort. A common FO76 batch carries
    ///     roughly 82k placements, so treating the whole sort as one "work item" defeats a 2 ms
    ///     frame budget even when every reference-resolution loop is sliced.
    /// </summary>
    private sealed class CascadeBatchSortState(OpaqueBatchState batch)
    {
        public OpaqueBatchState Batch { get; } = batch;
        public CascadeBatchSortPhase Phase { get; set; } = CascadeBatchSortPhase.Initialize;
        public int Index { get; set; }
        public int Count { get; set; }
        public int Cascades { get; set; }
        public float Slack { get; set; }
        public bool HasSeeds { get; set; }
        public bool HasHeatmapIds { get; set; }
        public int[] BucketCounts { get; } = new int[ShadowMapRenderer12.CascadeCount + 1];
        public int[] WriteCursor { get; } = new int[ShadowMapRenderer12.CascadeCount + 1];
    }

    /// <summary>
    ///     One immutable admission verdict for an entire staged sweep. TexturesReady is mutable on
    ///     CachedNifMesh12, so reading it per placement would publish only the later placements when a
    ///     texture lands halfway through a multi-frame build.
    /// </summary>
    private readonly record struct BatchMeshSnapshot(
        CachedNifMesh12? Mesh,
        bool TexturesReady,
        bool MainAdmitted);

    private sealed class ReferenceBatchSet
    {
        public OpaqueBatchRegistry12 OpaqueBatches { get; } = new();
        public List<BlendedReferenceDraw> BlendedDraws { get; } = new(256);
        public List<BlendedReferenceDraw> DepthWritingBlendDraws { get; } = new(64);
        // MeshId already folds the case-insensitive model path and alternate-texture variant. Keep
        // this uint-keyed like the measured pre-staging memo: hashing strings per placement at 90k+
        // survivors costs materially more than the batch bookkeeping itself.
        public Dictionary<uint, BatchMeshSnapshot> MeshSnapshots { get; } = new(512);
        public List<string> MissingPaths { get; } = new(32);
        public HashSet<string> MissingPathSet { get; } = new(StringComparer.OrdinalIgnoreCase);
        public (Vector3 Anchor, Vector3 SunDirection, float[] Radii, float[] Snaps, float SceneZSpan)? CascadeFit { get; set; }

        public void StartReset()
        {
            BlendedDraws.Clear();
            DepthWritingBlendDraws.Clear();
            MeshSnapshots.Clear();
            MissingPaths.Clear();
            MissingPathSet.Clear();
            CascadeFit = null;
            OpaqueBatches.StartBegin();
        }
    }

    private sealed class BatchBuildState(
        ReferenceBatchSet target,
        BatchReuseBlocker trigger,
        int cullEpoch,
        int invalidationGeneration,
        Vector3 renderOrigin,
        bool streamActive,
        bool widened,
        int evictionGeneration,
        int resetTotal,
        OpaqueFrontToBackBuildView opaqueFrontToBackView)
    {
        public ReferenceBatchSet Target { get; } = target;
        public BatchReuseBlocker Trigger { get; } = trigger;
        public int CullEpoch { get; } = cullEpoch;
        public int InvalidationGeneration { get; } = invalidationGeneration;
        public Vector3 RenderOrigin { get; } = renderOrigin;
        public bool StreamActive { get; } = streamActive;
        public bool Widened { get; } = widened;
        public int StartEvictionGeneration { get; } = evictionGeneration;
        public int ResetTotal { get; } = resetTotal;
        public OpaqueFrontToBackBuildView OpaqueFrontToBackView { get; set; } =
            opaqueFrontToBackView;
        public BatchBuildPhase Phase { get; set; } = BatchBuildPhase.Reset;
        public int ResetProcessed { get; set; }
        public int ReferenceIndex { get; set; }
        public int ShadowIndex { get; set; }
        public int SortIndex { get; set; } = -1;
        public CascadeBatchSortState? CascadeSort { get; set; }
        public int MainMissing { get; set; }
        public int ShadowMissing { get; set; }
        public int TexturePending { get; set; }
        public int Drawn { get; set; }
        public int DrawableSubmeshPlacements { get; set; }
        public bool SawNonQuietActivity { get; set; }
        public bool SawGpuUpload { get; set; }
        public bool EvictionChanged { get; set; }
        public int ObservedFrames { get; set; }
        public ulong ContentFingerprint { get; set; }
        public Dictionary<CachedNifMesh12, float> PendingSkinnerRegistrations { get; } =
            new(ReferenceEqualityComparer.Instance);
        public (Vector3 Anchor, Vector3 SunDirection, float[] Radii, float[] Snaps, float SceneZSpan)? CascadeFit { get; set; }
    }

    private bool _lastBuildValid;
    private int _batchInvalidationGeneration;
    private BatchBuildState? _batchBuildState;
    // Mesh residency can change during a staged build that is later abandoned by a hard cull-key
    // change. Keep the dirty signal outside the disposable cursor so the replacement publication
    // still invalidates cached shadow/reflection content exactly once.
    private bool _batchContentDirtyPending;
    private int _framesSinceBuild;
    // This frame's reuse decision, captured for the geometry draw validator's violation context.
    private bool _frameReusedBatches;
    private int _quietBuildStreak;
    private int _lastBuildCullEpoch;
    private Vector3 _lastBuildRenderOrigin;
    // Depth-writing blends route to the hoisted pre-water list OR the unified stream depending on
    // _transparencyStreamActive AT BUILD TIME. Frozen lists built under the other routing must not
    // be replayed after the flag flips (e.g. the last water cell leaves the gather) — the hoist
    // would draw an empty list while the stream never sees the depth-writing draws, or vice versa.
    private bool _lastBuildStreamActive;
    private bool _lastBuildQuiesced;
    private int _lastBuildEvictionGen;
    private int _lastBuildDrawn;
    private int _lastBuildMissing;
    private int _lastBuildShadowMissing;
    private int _lastBuildDrawableSubmeshPlacements;
    private ulong _lastBuildContentFingerprint;

    /// <summary>
    ///     Consecutive completed builds whose unresolved-mesh count matched the one before it. Reaching
    ///     <see cref="MissingStallStreakBuilds" /> means the remainder is permanently unresolvable
    ///     rather than still streaming in, which lets batch reuse resume (see the MeshesMissing clause).
    /// </summary>
    private int _missingStallStreak;

    /// <summary>
    ///     Builds with an unchanged miss count before the remainder counts as stalled. Two consecutive
    ///     repeats (not one) so a single frame landing between two decode waves cannot freeze a build
    ///     that is still filling in.
    /// </summary>
    private const int MissingStallStreakBuilds = 2;
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
        // Capture the streaming depths BEFORE Reset() clears them. The cull-cache debounce below
        // asks "was streaming in flight?", and the only frame that can answer is the previous one:
        // this frame's counters are not written until the very end of Render (the LastStats.Reference*
        // assignments after the batch pass). Reading them post-Reset returned 0/0/0 unconditionally,
        // which made `streamingDrained` permanently TRUE and silently deleted the debounce — measured
        // 2026-08-25 as a MeshBounds cull-cache veto on 99.6% of 529 FO76 frames, i.e. a full batch
        // rebuild (55.9 ms, 64% of the frame) every single frame. The one-frame lag is inherent and
        // harmless: the debounce is multi-frame hysteresis already.
        var streamingInFlightLastFrame = LastStats.ReferenceQueuedDecodes != 0 ||
                                         LastStats.ReferenceActiveDecodes != 0 ||
                                         LastStats.ReferenceGpuUploads != 0;
        LastStats.Reset();
        // Clip-w row of the scene viewProj — the blended sort key's metric. The water renderer
        // derives its batch depths from the SAME matrix, so the two streams merge numerically.
        _frameWColumn = new Vector4(viewProj.M14, viewProj.M24, viewProj.M34, viewProj.M44);
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
        LastStats.ReferencePlacedLightTileBuildMilliseconds = _placedLightTileBuildMilliseconds;
        LastStats.ReferencePlacedLightTileCount = _placedLightTileCount;
        LastStats.ReferencePlacedLightTileUploadBytes = _placedLightTileUploadBytes;
        LastStats.ReferencePlacedLightTileAverageLights = _placedLightTileAverageLights;
        LastStats.ReferencePlacedLightTileMaxLights = _placedLightTileMaxLights;
        LastStats.ReferencePlacedLightTileEmptyPercent = _placedLightTileEmptyPercent;
        LastStats.ReferencePlacedLightTileFallbackReason = _placedLightTileFallbackReason;
        LastStats.ReferenceBatchBuildBudgetMilliseconds = IncrementalBatchBuildBudgetMilliseconds;
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
            // The offset is shared with the SHADOW variant's cbuffer — see
            // InstancedShadowPerFrameConstants.WindMatricesOffset, which pins these two together.
            var windDst = (Matrix4x4*)((byte*)perFrameAlloc.CpuPtr
                + InstancedShadowPerFrameConstants.WindMatricesOffset);
            for (var w = 0; w < 4; w++)
            {
                windDst[w] = frameWindRig.WindMatrix(w);
            }
        }
        _deferredPerFrameCbvAddress = perFrameAlloc.GpuAddress;
        _deferredFrameIndex = frameIndex;

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        // Bindless: bind the unbounded SRV alias tables once per frame to heap slot 0
        // (the persistent region's start). All bindless `textures[index]` accesses by the
        // PS resolve through these bindings for the rest of the frame.
        GpuRootSignature12.SetGraphicsBindlessTables(cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);
        LastStats.ReferenceStateSetupMilliseconds = ElapsedMilliseconds(segmentStarted);

        var drawn = 0;
        var throttled = _streamingThrottled;
        var uploadBudget = throttled ? MaxNewUploadsPerFrame : int.MaxValue;
        var cylinderRadius = cylinder.Radius;
        var cylinderX = cylinder.Position.X;
        var cylinderY = cylinder.Position.Y;
        // One exact snapshot drives both the main cull and renderer-adjacent retained content.
        var visibilityKey = VisibilityKey;
        var hasFrustum = !_disableReferenceFrustum;
        // Cull in WORLD space: the frustum must come from the absolute viewProj (apex at the camera,
        // not the origin) because every cull test below is against absolute coordinates. The
        // camera-relative `viewProj` is uploaded to the GPU CB above (the VS subtracts CameraOrigin),
        // but it must NOT drive the frustum — see the cullViewProj parameter docs.
        var cullFrustumSource = cullViewProj ?? viewProj;
        var frustum = hasFrustum ? Frustum.FromViewProjection(cullFrustumSource) : default;
        // Shadow caster ring in effect for THIS render pass (0 when the shadow capture isn't
        // armed — top-down/export/capture paths cull without shadow-only augmentation). The
        // broadphase keeps its frustum filter and WIDENS it by the ring instead of dropping it: a
        // bucket outside the frustum-or-ring envelope can hold neither a visible ref nor a ring
        // caster, so off-screen casters still reach the per-REFR loop that collects them. Dropping
        // the filter (the previous behaviour) pushed every bucket in the full square through that
        // loop — measured at ~540k candidates/frame against ~190k with the filter armed, i.e. the
        // ring was silently costing 2.9x the cull time it needed to.
        var shadowRing = _shadowCaptureArmed ? _shadowCasterRingRadius : 0f;
        // Landmark-only band beyond the near ring (see the FAR RING note in TestCandidate).
        var shadowFarRing = _shadowCaptureArmed ? _shadowCasterFarRingRadius : 0f;

        // === Establishment frustum (tolerant cull) ===
        // The cached survivor set must remain a SUPERSET of the exact set for every camera pose the
        // cache still accepts — including ROTATED ones. The draw pass re-tests each instance against
        // the frame's EXACT frustum (PassesExactCull), so widening here can only add candidates, never
        // change what is drawn. `frustum` itself stays exact and is what _frameFrustum is assigned.
        var tolerant = cullCameraPose is not null;
        var exitMargin = WorldGridConstants.CellSize;
        var maxCullReach =
            ((cylinder.Radius + exitMargin) * SquareBroadphaseFactor) + (CullPositionSlack * 1.41422f);
        var angularWidened = false;
        var establishmentFrustum = hasFrustum && tolerant && CullAngleSlackRadians > 0f
            ? frustum.WidenAngular(CullAngleSlackRadians, maxCullReach, out angularWidened)
            : frustum;
        // Declined widening (orthographic capture paths) falls back to an exact-forward compare.
        var effectiveAngleSlack = angularWidened ? CullAngleSlackRadians : 0f;

        Frustum? broadphaseFrustum = hasFrustum && (BroadphaseShadowFrustumEnabled || shadowRing <= 0f)
            ? establishmentFrustum
            : null;
        // Only forward a ring to the broadphase when it is actually filtering by frustum (a null
        // frustum admits everything already) and a ring is genuinely active — otherwise the drift
        // slack added at the call site would turn into a small always-on admit square.
        // The broadphase must admit the WIDER of the two rings: a bucket rejected here never reaches
        // TestCandidate, so a near-ring-sized broadphase would silently defeat the far ring.
        var effectiveRing = MathF.Max(shadowRing, shadowFarRing);
        var broadphaseRing = broadphaseFrustum is not null && effectiveRing > 0f ? effectiveRing : 0f;

        double cullMs = 0;
        double batchBuildMs = 0;
        double meshResolveMs = 0;
        double instanceBucketingMs = 0;
        double batchFinalizeMs = 0;
        double blendedRefreshMs = 0;
        double cbUpdateMs = 0;
        double srvBindMs = 0;
        double drawCallMs = 0;
        int cellsVisited = 0;
        int candidates = 0;
        int culled = 0;
        int missingMeshes = 0;
        int shadowMissingMeshes = 0;
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
        // pose (top-down / capture paths) the compare stays byte-exact on the viewProj. Rotation is
        // absorbed the same way: the establishment frustum was opened by CullAngleSlackRadians, so any
        // orientation within that cone still describes a subset of the cached survivors.
        _framesSinceCull++;
        // A STATIC exact-mode view (the 2D map's top-down overlay, one-shot exports) re-issues the
        // SAME viewProj + cylinder pass after pass while the scene streams. Without the debounce
        // below, every such pass resolved a few meshes, grew _meshLocalRadius, and therefore vetoed
        // the cache — measured on WastelandNV zoom-to-fit: cullCacheHit=False on 150/150 passes at
        // 57-130 ms of cull each, re-testing 1.29 M candidates to add ~25 meshes. Bounds tightening
        // is the same visual nicety here as in tolerant mode (the cached survivor set was culled
        // with the LARGER placeholder radii, so it is a superset of the exact set, and the draw pass
        // re-tests every instance against the frame's exact bounds), so give the static exact view
        // the identical CullStreamingRefreshFrames debounce the live tolerant path already gets.
        var viewIsStatic = !tolerant
                           && _cullCacheViewProj == cullFrustumSource
                           && _cullCacheCylinder == cylinder;
        // The ordinary debounce holds only WHILE streaming is in flight — that is the thrash it
        // exists to kill (every streaming frame resolves something and would re-cull). Once the
        // pipes drain, a pending bounds delta normally vetoes immediately: the placeholder radii
        // are not reliably larger than the real bounds (a re-cull after drain admitted 1,547
        // candidates where the placeholder cull had admitted 1,360). The one exception below is an
        // already-valid staged build, whose live survivor lists must remain stable until publication;
        // capture/quiescence gates count that cursor as pending work, so the temporary defer can
        // never be mistaken for a settled image. The next frame after publication refreshes bounds.
        // Sampled before LastStats.Reset() at the top of this method — see the note there for why
        // reading it here instead is a no-op that disables the debounce entirely.
        var streamingDrained = !streamingInFlightLastFrame;
        // A staged build indexes the live _cachedCullSurvivors / _cachedShadowOnlyCasters lists.
        // A bounds-only re-cull would clear those lists, change their epoch and restart a 100k+
        // sweep before it can ever publish. Hold ONLY the mesh-bounds generation while the cursor
        // still matches every hard batch key; cullCacheStructureValid below remains independent, so
        // a camera/filter/ring change still performs a fresh cull and invalidates the staged state.
        var stagedBuildPinsCullSnapshot = _batchBuildState is not null
                                          && BatchBuildMatchesCurrent(
                                              _batchBuildState, renderOrigin, tolerant);
        var meshBoundsCurrent = _cullCacheMeshRadiusCount == _meshLocalRadius.Count
                                || stagedBuildPinsCullSnapshot
                                || ((tolerant || viewIsStatic) && !streamingDrained &&
                                    _framesSinceCull < CullStreamingRefreshFrames);
#pragma warning disable S1244 // cull-cache identity: exact compare of a cached snapshot detects change, not proximity
#pragma warning disable S3358 // the veto chain below IS the gate order; flattening it loses the priority it encodes
        var cullCacheStructureValid =
            _cullCacheValid
            && _cullCacheShowMarkers == ShowMarkers
            && _cullCacheShowGrass == ShowGrass
            && _cullCacheShowImposters == ShowImposters
            && _cullCacheShowDisabled == ShowInitiallyDisabled
            && _cullCacheEnabledOverrideVersion == _enabledOverrides.Version
            && _cullCacheDayNightVersion == (DayNightStates?.Version ?? 0)
            && _cullCacheShadowRing == shadowRing
            && _cullCacheShadowFarRing == shadowFarRing
            && (tolerant
                ? _cullCachePose is not null
                  && _cullCachePose.Value.FovYRadians == cullCameraPose!.Value.FovYRadians
                  && _cullCachePose.Value.Aspect == cullCameraPose.Value.Aspect
                  && ForwardWithin(
                      _cullCachePose.Value.Forward, cullCameraPose.Value.Forward, _cullCacheForwardCos)
                  && _cullCacheRadius == cylinder.Radius
                  && ChebyshevWithin(cylinder.Position, _cullCachePosition, CullPositionSlack)
                : _cullCacheViewProj == cullFrustumSource && _cullCacheCylinder == cylinder);
        var cullCacheValid = meshBoundsCurrent && cullCacheStructureValid;
        // A bounds-only refresh is safe to publish late: the old frozen set remains drawable while
        // the newly re-culled survivor set is staged. This is the critical MeshesMissing transition
        // where a landed mesh grows _meshLocalRadius; forcing the replacement batch pass to run
        // synchronously here would recreate the exact hitch incremental rebuilding is meant to remove.
        var meshBoundsOnlyCullRefresh = !meshBoundsCurrent && cullCacheStructureValid;

        // Which clause vetoed, in gate order. Reported (not inferred) for the same reason the batch
        // reuse blocker is: a cull miss has eight possible causes and guessing has been expensive.
        LastStats.ReferenceCullCacheVeto = (int)(
            !_cullCacheValid ? CullCacheVeto.Invalidated
            : !meshBoundsCurrent ? CullCacheVeto.MeshBounds
            : _cullCacheShowMarkers != ShowMarkers ? CullCacheVeto.ShowMarkers
            : _cullCacheShowGrass != ShowGrass ? CullCacheVeto.ShowGrass
            : _cullCacheShowImposters != ShowImposters ? CullCacheVeto.ShowImposters
            : _cullCacheShowDisabled != ShowInitiallyDisabled ? CullCacheVeto.ShowDisabled
            : _cullCacheEnabledOverrideVersion != _enabledOverrides.Version ? CullCacheVeto.EnabledOverrides
            : _cullCacheDayNightVersion != (DayNightStates?.Version ?? 0) ? CullCacheVeto.EnabledOverrides
            : _cullCacheShadowRing != shadowRing ? CullCacheVeto.ShadowRing
            : _cullCacheShadowFarRing != shadowFarRing ? CullCacheVeto.ShadowRing
            : cullCacheValid ? CullCacheVeto.None
            : tolerant ? CullCacheVeto.Pose
            : _cullCacheViewProj != cullFrustumSource ? CullCacheVeto.ViewProj
            : CullCacheVeto.Cylinder);
#pragma warning restore S3358
#pragma warning restore S1244

        // === Batch reuse decision ===
        // cullCacheValid ⇒ no re-cull this frame ⇒ _cullEpoch is stable, so an epoch match means
        // the frozen batches were built from exactly the survivor set this frame would iterate.
        // Settled terminal snapshots no longer have a timer-driven rebuild, so keep this diagnostic
        // age monotonic instead of allowing a very long-running viewer to wrap back through zero.
        if (_framesSinceBuild < int.MaxValue)
        {
            _framesSinceBuild++;
        }
        // NOTE (measured 2026-08-02): during motion this evaluates false on ~97-100% of frames, so the
        // 7-11 ms resolve+batch pass re-runs even on the ~40-80% of frames whose cull was cached. That
        // is the largest single CPU cost left in the frame. Relaxing the quiescence requirement was
        // tried and did NOT help (reuse rose only 0% -> ~3%), and pinning a 60,000-entry mesh cache to
        // eliminate EvictionGeneration churn did not help either (~2.8%) — so neither quiescence nor
        // eviction is the binding term. The blocker is still unidentified; instrument WHICH clause
        // fails before changing this again.
        // Which clause vetoed reuse this frame. Reported so the blocker is identified from data
        // instead of inferred: two plausible-sounding candidates (quiescence, eviction churn) were
        // each tried and measured to be irrelevant, which cost a full experiment cycle apiece.
        BatchReuseBlocker ResolveReuseBlocker()
        {
            if (!BatchReuseEnabled)
            {
                return BatchReuseBlocker.Disabled;
            }

            if (!cullCacheValid)
            {
                return BatchReuseBlocker.CullCacheMiss;
            }

            if (!_lastBuildValid)
            {
                return BatchReuseBlocker.NoBuild;
            }

            if (_lastBuildCullEpoch != _cullEpoch)
            {
                return BatchReuseBlocker.CullEpoch;
            }

            // Never stop advancing a build whose missing meshes are still ARRIVING. The resolve pass
            // is the only thing that calls GetOrUpload — i.e. the only thing that requests decodes.
            // On the on-demand top-down path, freezing without a staged slice is a hard
            // deadlock, not just a delay: the very first (cold) pass builds with everything missing, the
            // second pass reuses it, no decodes are ever requested, streaming therefore reports
            // quiesced, the overlay declares itself complete and stops re-rendering — a permanently
            // EMPTY overlay (reproduced the moment the cull cache started hitting: drawn=0 at pass 2,
            // settled=True). The live 60fps path never showed it because its camera moves, which misses
            // the cull cache and forces a rebuild anyway.
            //
            // The test is PROGRESS, not an absolute count. A dense worldspace holds thousands of
            // permanently unresolvable meshes (Appalachia sits at miss ≈ 22,000, FO4 downtown at
            // 10-20k) that never reach zero, so an absolute test vetoes reuse on literally every frame
            // forever — measured 2026-08-18 on Fallout 76: blocker 10 on 100 of 104 frames, re-running
            // the ~250 ms resolve+batch pass (93k survivors, an 82k-instance sort) each time. That is
            // the ~97-100% veto rate the note above asked to have identified. Once the miss count has
            // repeated unchanged across builds with no decode queued or running, nothing is in flight
            // that another resolve pass could advance, so freezing is safe — and any camera move,
            // eviction or cull-epoch change still forces a rebuild through the clauses around this one.
            var hasMissingMeshes = _lastBuildMissing > 0 || _lastBuildShadowMissing > 0;
            var missingMeshesStalled = hasMissingMeshes && MissingMeshesStalled();
            if (_meshCache.PendingMaterializationRetryCount > 0 ||
                (hasMissingMeshes && !missingMeshesStalled))
            {
                return BatchReuseBlocker.MeshesMissing;
            }

            // A fully settled snapshot has no timer-driven work left. In accepted FO76 run
            // 20260828_202603, the unconditional ceiling accounted for 1,865/5,291 scored frames
            // (35.25%) even though all 59 profile samples held miss=476 with zero decode, upload, or
            // texture work, and every rebuild republished the same 97,047 references. Keep the short
            // ceiling only while streaming is not yet quiesced; hard identity changes below still
            // invalidate immediately, while materialization retry debt and a changing missing
            // population are handled above.
            if (!ReferenceBatchBuildPolicy.CanSkipPeriodicRefresh(
                    _lastBuildQuiesced,
                    hasMissingMeshes,
                    missingMeshesStalled,
                    _meshCache.PendingMaterializationRetryCount)
                && _framesSinceBuild >= BatchReuseStreamingMaxFrames)
            {
                return BatchReuseBlocker.FrameCeiling;
            }

            if (_lastBuildRenderOrigin != renderOrigin)
            {
                return BatchReuseBlocker.RenderOrigin;
            }

            if (_lastBuildEvictionGen != _meshCache.EvictionGeneration)
            {
                return BatchReuseBlocker.Eviction;
            }

            if (_lastBuildStreamActive != _transparencyStreamActive)
            {
                return BatchReuseBlocker.StreamRouting;
            }

            return BatchReuseBlocker.None;
        }

        // True once the unresolved-mesh population has repeated unchanged across consecutive builds
        // with nothing queued or decoding — i.e. no resolve pass could change it, so it is the
        // worldspace's permanently unresolvable remainder rather than a stream still landing.
        // PendingDecodeCount is the queue's real depth. FrameQueuedDecodes counts only first-time
        // queue entries this frame, so a steady backlog reports zero — trusting it here would freeze
        // the batches mid-stream. The MeshesMissing blocker must keep scheduling staged resolve work
        // or nothing requests another decode: the world would stop filling in and the miss count would sit
        // frozen, looking exactly like the "permanently unresolvable" population it is not.
        bool MissingMeshesStalled() =>
            _missingStallStreak >= MissingStallStreakBuilds &&
            _meshCache.PendingDecodeCount == 0 &&
            _meshCache.FrameActiveDecodes == 0;

        var reuseBlocker = ResolveReuseBlocker();
        var reuseBatches = reuseBlocker == BatchReuseBlocker.None;
        LastStats.ReferenceBatchReuseBlocker = (int)reuseBlocker;
        _frameReusedBatches = reuseBatches;

        LastStats.ReferenceCullCacheHit = cullCacheValid;

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
            // exitMargin (one cell, hoisted above) matches the terrain/water gathers' draw hysteresis
            // so a boundary cell's references leave the screen in lockstep with its terrain instead
            // of popping a frame earlier at the strict square edge.
            var cullCylinder = new VisibilityCylinder(
                cylinder.Position, cylinder.Radius + exitMargin + driftSlack);
            var smallPropCutoff = (cylinderRadius * SmallPropDistanceFraction) + driftSlack;
            var smallPropCutoffSq = smallPropCutoff * smallPropCutoff;
            var broadphaseReach =
                ((cylinderRadius + exitMargin) * SquareBroadphaseFactor) + (driftSlack * 1.41422f);
            var broadphaseRingReach = broadphaseRing > 0f ? broadphaseRing + driftSlack : 0f;

            // Per-candidate verdict, hoisted out of the loop so the sequential and PARALLEL paths
            // below run byte-identical logic. Reads only frame-constant locals and immutable-for-the
            // -duration state (_meshLocalRadius is written by the resolve pass, which runs after this
            // cull; the override/category tables are mutated only by UI actions on this same thread),
            // so it is safe to call concurrently.
            CullVerdict TestCandidate(in RenderableReference r)
            {
                    // Authored/per-instance, category, grass, marker, and imposter decisions share
                    // the exact policy used by retained placed-water and shadow content. Count any
                    // hidden state as culled so the HUD still reflects the aggregate filter.
                    if (!visibilityKey.IsVisible(r, _enabledOverrides, DayNightStates)) return CullVerdict.Culled;
                    // Cull bounds: once a mesh is resident, use its ACTUAL local bounding sphere (radius
                    // around the NIF origin, scaled into world) instead of the OBND-or-cell-sized fallback
                    // baked at LoadData. The mesh sphere is conservative (contains all geometry) and centered at
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
                    var maxDist = cylinderRadius + exitMargin + cullRadius + FrustumCullMargin + driftSlack;
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
                        !establishmentFrustum.IntersectsSphere(
                            cullCenter, cullRadius + FrustumCullMargin + frustumSlack))
                    {
                        var ringReach = shadowRing + cullRadius + driftSlack;
                        if (shadowRing > 0f && MathF.Abs(dx) <= ringReach && MathF.Abs(dy) <= ringReach)
                        {
                            // Frustum-rejected but inside the caster ring: still counts as culled for
                            // the HUD, and rides the shadow-only list.
                            return CullVerdict.ShadowOnlyCaster;
                        }

                        // FAR RING (2026-08-11). The near ring matches the MID cascade (8192), on the
                        // old assumption that past it "the far cascades' coarse texels make a missing
                        // off-screen caster's shadow hard to notice". Measured false: the cascades
                        // reach 131,072 units, and at a Hoover-dam overlook a 1.7-degree yaw dropped
                        // 126 casters (shadowDraws 1254 -> 1128) and visibly deleted a shadow the user
                        // was looking at. Admitting every ref out to the last cascade would stream the
                        // whole worldspace for the shadow pass, so the far band takes only
                        // LANDMARK-SCALE geometry — the same size split A5 distance-LOD already uses
                        // (small props are clutter whose far shadows really are sub-texel). Cheap:
                        // one extra Chebyshev test on refs that were about to be rejected outright.
                        var farRing = shadowFarRing;
                        if (farRing > shadowRing && cullRadius >= SmallPropLodRadius)
                        {
                            var farReach = farRing + cullRadius + driftSlack;
                            if (MathF.Abs(dx) <= farReach && MathF.Abs(dy) <= farReach)
                            {
                                return CullVerdict.ShadowOnlyCaster;
                            }
                        }

                        rejected = true;
                    }

                    return rejected ? CullVerdict.Culled : CullVerdict.Survivor;
            }

            _cullCellScratch.Clear();
            foreach (var cell in EnumerateVisibleCells(cullCylinder))
            {
                _cullCellScratch.Add(cell);
            }

            // The establishment is the turn-stutter spike: it re-tests every candidate in the visible
            // square (~217k at 16 cells) whenever the pose leaves the cached tolerance, and it is the
            // dominant term on slow frames while orbiting. The per-cell work is independent — the
            // spatial index is a ConcurrentDictionary of immutable per-cell indexes and every test
            // above is a pure read — so partition the CELLS across the pool and merge the per-partition
            // results in cell order afterwards. Merging in order keeps the survivor sequence (and
            // therefore batch/draw order) bit-identical to the sequential path, which matters because
            // blended draw order is visible.
            var cellCount = _cullCellScratch.Count;
            var partitionCount = ParallelCullEnabled
                ? Math.Clamp(Math.Min(Environment.ProcessorCount - 1, cellCount / ParallelCullMinCellsPerPartition), 1, MaxCullPartitions)
                : 1;

            if (partitionCount <= 1)
            {
                for (var ci = 0; ci < cellCount; ci++)
                {
                    _cullCandidateScratch.Clear();
                    var totalPlacements = _renderCache.QueryPlacementCandidates(
                        _cullCellScratch[ci], cylinderX, cylinderY, broadphaseReach, broadphaseFrustum,
                        FrustumCullMargin + frustumSlack, _cullCandidateScratch, broadphaseRingReach);
                    if (totalPlacements == 0 || _cullCandidateScratch.Count == 0) continue;

                    cellsVisited++;
                    candidates += _cullCandidateScratch.Count;
                    foreach (var r in _cullCandidateScratch)
                    {
                        switch (TestCandidate(in r))
                        {
                            case CullVerdict.Survivor:
                                _cachedCullSurvivors.Add(r);
                                break;
                            case CullVerdict.ShadowOnlyCaster:
                                _cachedShadowOnlyCasters.Add(r);
                                culled++;
                                break;
                            default:
                                culled++;
                                break;
                        }
                    }
                }
            }
            else
            {
                EnsureCullPartitions(partitionCount);
                var perPartition = (cellCount + partitionCount - 1) / partitionCount;
                // NOT Parallel.For: this runs inside CompositionTarget.Rendering on the STA UI
                // thread, and Parallel.For blocks the caller through ManualResetEventSlim.Wait —
                // a COM PUMPING wait. The pump dispatches input back into XAML mid-callback and
                // CXcpDispatcher::CheckReentrancy fail-fasts the process (0xc000027b). That is the
                // crash this line caused on 2026-08-07/08-11/08-14/08-23; the dump's faulting stack
                // was Monitor.Wait <- Parallel.For <- Render <- OnRendering. NonPumpingParallel
                // keeps the identical partitioning (and still runs partition 0 on this thread) but
                // joins with a non-alertable sleep that cannot pump.
                Core.Orchestration.NonPumpingParallel.For(0, partitionCount, p =>
                {
                    var state = _cullPartitions![p];
                    state.Reset();
                    var start = p * perPartition;
                    var end = Math.Min(start + perPartition, cellCount);
                    for (var ci = start; ci < end; ci++)
                    {
                        state.Candidates.Clear();
                        var totalPlacements = _renderCache.QueryPlacementCandidates(
                            _cullCellScratch[ci], cylinderX, cylinderY, broadphaseReach, broadphaseFrustum,
                            FrustumCullMargin + frustumSlack, state.Candidates, broadphaseRingReach);
                        if (totalPlacements == 0 || state.Candidates.Count == 0) continue;

                        state.CellsVisited++;
                        state.CandidateCount += state.Candidates.Count;
                        foreach (var r in state.Candidates)
                        {
                            switch (TestCandidate(in r))
                            {
                                case CullVerdict.Survivor:
                                    state.Survivors.Add(r);
                                    break;
                                case CullVerdict.ShadowOnlyCaster:
                                    state.ShadowOnly.Add(r);
                                    state.Culled++;
                                    break;
                                default:
                                    state.Culled++;
                                    break;
                            }
                        }
                    }
                });

                for (var p = 0; p < partitionCount; p++)
                {
                    var state = _cullPartitions![p];
                    _cachedCullSurvivors.AddRange(state.Survivors);
                    _cachedShadowOnlyCasters.AddRange(state.ShadowOnly);
                    cellsVisited += state.CellsVisited;
                    candidates += state.CandidateCount;
                    culled += state.Culled;
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
            // How far the survivor set above tolerates rotation. 1f (no widening applied) keeps the
            // historical exact-forward compare.
            _cullCacheForwardCos = effectiveAngleSlack > 0f ? MathF.Cos(effectiveAngleSlack) : 1f;
            _cullCacheMeshRadiusCount = _meshLocalRadius.Count;
            _cullCacheCascadeAnchorXY = _cascadeFit is { } cullFit
                ? new Vector2(cullFit.Anchor.X, cullFit.Anchor.Y)
                : null;
            _cullCacheShowMarkers = ShowMarkers;
            _cullCacheShowGrass = ShowGrass;
            _cullCacheShowImposters = ShowImposters;
            _cullCacheShowDisabled = ShowInitiallyDisabled;
            _cullCacheEnabledOverrideVersion = _enabledOverrides.Version;
            _cullCacheDayNightVersion = DayNightStates?.Version ?? 0;
            _cullCacheShadowRing = shadowRing;
            _cullCacheShadowFarRing = shadowFarRing;
            _cullCacheCellsVisited = cellsVisited;
            _cullCacheCandidates = candidates;
            _cullCacheCulled = culled;
        }

        // === Resolve + batch pass (time-sliced behind the published set for refresh-only blockers) ===
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
        // FormID-heatmap normalization window for this frame (no-op while the overlay is off).
        // After the cull block so the epoch part of its memo key is settled for this frame.
        UpdateFormIdHeatmapScan(cylinderX, cylinderY, cylinder.Position.Z, cylinderRadius);
        // === Resolve + batch publication ===
        // A rebuild caused only by the periodic refresh ceiling or by still-arriving content can be
        // spread across frames while the complete published set keeps drawing. Every structural key
        // is checked independently: the first reported blocker may be soft even when a later gate
        // (origin, routing or eviction) also changed.
        var refreshOnlyBlocker =
            reuseBlocker is BatchReuseBlocker.FrameCeiling or BatchReuseBlocker.MeshesMissing
            || meshBoundsOnlyCullRefresh;
        var validStagedContinuation = _batchBuildState is not null
                                      && BatchBuildMatchesCurrent(_batchBuildState, renderOrigin, tolerant);
        var canBuildIncrementally = ReferenceBatchBuildPolicy.CanAmortize(
            streamingThrottled: throttled,
            refreshOnlyBlocker: refreshOnlyBlocker || validStagedContinuation,
            cullCacheHit: cullCacheValid || meshBoundsOnlyCullRefresh,
            publishedBuildValid: BatchReuseEnabled && _lastBuildValid,
            // A staged state was born from the current survivor snapshot and
            // BatchBuildMatchesCurrent proved its epoch explicitly. Comparing only the older
            // published epoch here discarded that valid continuation one frame after a
            // mesh-bounds refresh, turning each bounded slice into a full synchronous rebuild.
            cullEpochMatches: validStagedContinuation
                              || _lastBuildCullEpoch == _cullEpoch
                              || meshBoundsOnlyCullRefresh,
            renderOriginMatches: _lastBuildRenderOrigin == renderOrigin,
            evictionGenerationMatches: _lastBuildEvictionGen == _meshCache.EvictionGeneration,
            streamRoutingMatches: _lastBuildStreamActive == _transparencyStreamActive);
        var buildPublished = false;
        var incrementalAdvanced = false;
        var synchronousFallback = false;
        BatchBuildState? frameBuildState = null;

        if (reuseBatches && !validStagedContinuation)
        {
            // A completed publication is the only path that can make the gate reusable again. This is
            // defensive cleanup for a future policy change that might otherwise strand staging state.
            _batchBuildState = null;
        }
        else
        {
            // A staged refresh remains authoritative even if its original missing-mesh blocker
            // becomes "stalled" while the cursor is in flight. Finish it so newly admitted content
            // is not discarded after the refresh trigger clears.
            var buildStarted = StartTiming();
            var blendedRefreshBeforeBuild = blendedRefreshMs;
            if (canBuildIncrementally)
            {
                // Charge fresh-state setup to the same best-effort slice as traversal. StartBatchBuild
                // clears auxiliary staging collections synchronously; starting the clock afterwards
                // allowed that reset cost plus a new full budget in the first incremental frame.
                var batchSliceStarted = Stopwatch.GetTimestamp();
                incrementalAdvanced = true;
                if (_batchBuildState is not null &&
                    !BatchBuildMatchesCurrent(_batchBuildState, renderOrigin, tolerant))
                {
                    _batchBuildState = null;
                }

                _batchBuildState ??= StartBatchBuild(
                    reuseBlocker,
                    renderOrigin,
                    tolerant,
                    frameCameraPosition,
                    frameCameraForward);
                frameBuildState = _batchBuildState;
                var advance = AdvanceBatchBuild(
                    frameBuildState,
                    cmd,
                    budgeted: true,
                    sliceStartedTimestamp: batchSliceStarted,
                    cylinderX,
                    cylinderY,
                    frameCameraPosition,
                    frameCameraForward,
                    ref uploadBudget,
                    ref meshResolveMs,
                    ref instanceBucketingMs,
                    ref batchFinalizeMs);
                ObserveBatchBuildActivity(frameBuildState);

                if (advance == BatchBuildAdvanceResult.EvictionInvalidated)
                {
                    // The published batches hold arena views, not cache leases. Once an insertion
                    // evicts one of those views the old set cannot be drawn again, so finish a fresh
                    // stable pass before issuing this frame.
                    synchronousFallback = true;
                    _batchBuildState = null;
                    if (TryCompleteSynchronousBatchBuild(
                            reuseBlocker,
                            renderOrigin,
                            tolerant,
                            cmd,
                            cylinderX,
                            cylinderY,
                            frameCameraPosition,
                            frameCameraForward,
                            ref uploadBudget,
                            ref meshResolveMs,
                            ref instanceBucketingMs,
                            ref batchFinalizeMs,
                            out var completed))
                    {
                        frameBuildState = completed;
                        PublishBatchBuild(
                            completed!,
                            frameCameraPosition,
                            frameCameraForward,
                            renderOrigin,
                            ref blendedRefreshMs);
                        buildPublished = true;
                        reuseBatches = false;
                    }
                    else
                    {
                        ClearPublishedBatchSnapshotAfterEviction();
                        reuseBatches = false;
                    }
                }
                else if (advance == BatchBuildAdvanceResult.Complete)
                {
                    PublishBatchBuild(
                        frameBuildState,
                        frameCameraPosition,
                        frameCameraForward,
                        renderOrigin,
                        ref blendedRefreshMs);
                    _batchBuildState = null;
                    buildPublished = true;
                    reuseBatches = false;
                }
                else
                {
                    // The inactive aggregate remains invisible until every main/shadow list and every
                    // cascade prefix is complete.
                    reuseBatches = true;
                }
            }
            else
            {
                synchronousFallback = _batchBuildState is not null;
                _batchBuildState = null;
                if (TryCompleteSynchronousBatchBuild(
                        reuseBlocker,
                        renderOrigin,
                        tolerant,
                        cmd,
                        cylinderX,
                        cylinderY,
                        frameCameraPosition,
                        frameCameraForward,
                        ref uploadBudget,
                        ref meshResolveMs,
                        ref instanceBucketingMs,
                        ref batchFinalizeMs,
                        out var completed))
                {
                    frameBuildState = completed;
                    PublishBatchBuild(
                        completed!,
                        frameCameraPosition,
                        frameCameraForward,
                        renderOrigin,
                        ref blendedRefreshMs);
                    buildPublished = true;
                }
                else
                {
                    ClearPublishedBatchSnapshotAfterEviction();
                }

                reuseBatches = false;
            }

            batchBuildMs = Math.Max(
                0.0,
                ElapsedMilliseconds(buildStarted) - (blendedRefreshMs - blendedRefreshBeforeBuild));
        }

        drawn = _lastBuildDrawn;
        referencesWithReadyMesh = _lastBuildDrawn;
        missingMeshes = _lastBuildMissing;
        shadowMissingMeshes = _lastBuildShadowMissing;
        texturePending = _lastBuildTexturePending;

        if (reuseBatches)
        {
            var refreshStarted = StartTiming();
            RefreshBlendedDraws(frameCameraPosition, frameCameraForward, renderOrigin);
            blendedRefreshMs += ElapsedMilliseconds(refreshStarted);
        }

        _frameReusedBatches = reuseBatches;
        LastStats.ReferenceBatchesReused = reuseBatches;
        LastStats.ReferenceBatchBuildInProgress = _batchBuildState is not null;
        // Publication can land meshes whose real radii were unknown when this survivor snapshot was
        // culled. Even though the staged batch itself is complete, the next bounds refresh/build is
        // still pending and capture must not advance its clean-snapshot count in this one-frame gap.
        LastStats.ReferenceCullRefreshPending =
            _cullCacheMeshRadiusCount != _meshLocalRadius.Count;
        LastStats.ReferenceBatchBuildIncrementalAdvanced = incrementalAdvanced;
        LastStats.ReferenceBatchBuildPublished = buildPublished;
        LastStats.ReferenceBatchBuildSynchronousFallback = synchronousFallback;
        LastStats.ReferenceBatchBuildTrigger = (int)(frameBuildState?.Trigger ?? BatchReuseBlocker.None);
        LastStats.ReferenceBatchBuildPhase = (int)(frameBuildState?.Phase ?? BatchBuildPhase.None);
        LastStats.ReferenceBatchBuildProcessed = frameBuildState is null
            ? 0
            : BatchBuildProcessed(frameBuildState);
        LastStats.ReferenceBatchBuildTotal = frameBuildState is null
            ? 0
            : BatchBuildTotal(frameBuildState);

        // Generic cylinder/LOD/frustum refiltering requires a real frustum. Enabled grass-distance
        // batches independently force their own exact per-instance copy in DrawOpaqueBatches, so
        // disabling reference-frustum culling never samples the default frustum and never bypasses
        // the FNV hard end.
        _frameRefilterActive = _batchesWidened && hasFrustum;
        // Legacy live particles: every visible unique particle submesh receives one coherent transient
        // VB/IB update before either blended pass binds geometry. Ring exhaustion/malformed controller data
        // clears the override and draws the existing immutable static cloud for this frame.
        UpdateLiveParticleFrames(frameIndex);

        // Keyframe playback: re-pose + CPU-skin every in-budget animated mesh into fresh ring
        // allocations (or clear all overrides when disabled) BEFORE any pass binds a vertex buffer —
        // both the opaque draws and the shadow capture below read EffectiveVertexBufferView.
        // resolveRan means a COMPLETE resolve/publication pass, not merely that this frame advanced a
        // staged slice. Partial slices buffer new sightings and must not expire/decay published meshes
        // that their cursor has not reached yet.
        _skinner.Tick(
            frameIndex, _ringBuffer, _animationClockSeconds, cylinder.Position, AnimationsEnabled,
            resolveRan: buildPublished);

        ID3D12PipelineState? currentPso = null;
        var opaqueSubmissionStarted = StartTiming();
        DrawOpaqueBatches(cmd, frameIndex, ref currentPso, ref cbUpdateMs, ref srvBindMs, ref drawCallMs, ref srvBinds, ref submeshDraws);
        LastStats.ReferenceOpaqueSubmissionMilliseconds = ElapsedMilliseconds(opaqueSubmissionStarted);
        // Depth-writing blend foliage draws here — INLINE, after the opaque batches but before the water
        // pass — with a depth-writing blend PSO, so the water surface occludes it from above (a no-depth
        // blend drawn after water painted over the surface). Runs every frame regardless of deferBlended.
        var blendedSubmissionStarted = StartTiming();
        DrawDepthWritingBlend(cmd, frameIndex, ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws);
        LastStats.ReferenceBlendedSubmissionMilliseconds += ElapsedMilliseconds(blendedSubmissionStarted);
        // 3D-8: blended submeshes draw now (inline, e.g. top-down overlay) or are deferred to after the
        // water pass via RenderBlendedDeferred() so water never paints over transparent meshes.
        if (!deferBlended)
        {
            blendedSubmissionStarted = StartTiming();
            DrawBlended(
                cmd, frameIndex, sceneDepthSampled: false,
                ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws);
            LastStats.ReferenceBlendedSubmissionMilliseconds += ElapsedMilliseconds(blendedSubmissionStarted);
        }

        ReferencesDrawnLastFrame = drawn;
        LastStats.ReferenceCellsVisited = cellsVisited;
        LastStats.ReferenceCandidates = candidates;
        LastStats.ReferenceCulled = culled;
        LastStats.ReferenceMeshMissing = missingMeshes;
        LastStats.ReferenceShadowMeshMissing = shadowMissingMeshes;
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
        LastStats.ReferenceMeshMaterializationFailures = _meshCache.FrameMaterializationFailures;
        LastStats.ReferenceMeshMaterializationRetriesPending =
            _meshCache.PendingMaterializationRetryCount;
        LastStats.ReferenceUploadByteBudgetDeferrals = _meshCache.FrameByteBudgetDeferrals;
        LastStats.ReferenceCompressedTextureUploads = _meshCache.FrameCompressedTextureUploads;
        LastStats.ReferenceRgbaTextureUploads = _meshCache.FrameRgbaTextureUploads;
        LastStats.ReferenceTextureQueuedResolves = _meshCache.FrameQueuedTextureResolves;
        LastStats.ReferenceTextureActiveResolves = _meshCache.FrameActiveTextureResolves;
        LastStats.ReferenceTexturePendingResolves = _meshCache.PendingTextureResolves;
        LastStats.ReferenceTexturePendingUploads = _meshCache.PendingTextureUploads;
        LastStats.ReferenceCullMilliseconds = cullMs;
        LastStats.ReferenceBatchBuildMilliseconds = batchBuildMs;
        LastStats.ReferenceMeshResolveMilliseconds = meshResolveMs;
        LastStats.ReferenceInstanceBucketingMilliseconds = instanceBucketingMs;
        LastStats.ReferenceBatchFinalizeMilliseconds = batchFinalizeMs;
        LastStats.ReferenceGpuUploadMilliseconds = _meshCache.FrameGpuUploadMilliseconds;
        LastStats.ReferenceBlendedRefreshMilliseconds = blendedRefreshMs;
        LastStats.ReferenceCbUpdateMilliseconds = cbUpdateMs;
        LastStats.ReferenceSrvBindMilliseconds = srvBindMs;
        LastStats.ReferenceDrawCallMilliseconds = drawCallMs;
        LastStats.CpuFrameMilliseconds = ElapsedMilliseconds(started);
        return drawn;
    }

    private BatchBuildState StartBatchBuild(
        BatchReuseBlocker trigger,
        Vector3 renderOrigin,
        bool widened,
        Vector3 cameraPosition,
        Vector3 cameraForward)
    {
        // A new state performs a fresh desired-set traversal. Scope materialization retry debt to
        // that demand set; valid incremental continuations do not call StartBatchBuild and retain
        // the same generation until they finish.
        _meshCache.BeginMaterializationRetryDemandGeneration();
        var resetTotal = _stagingBatchSet.OpaqueBatches.ActiveBatches.Count;
        _stagingBatchSet.StartReset();
        return new BatchBuildState(
            _stagingBatchSet,
            trigger,
            _cullEpoch,
            _batchInvalidationGeneration,
            renderOrigin,
            _transparencyStreamActive,
            widened,
            _meshCache.EvictionGeneration,
            resetTotal,
            OpaqueFrontToBackPolicy.CreateBuildView(
                OpaqueFrontToBackRequested,
                cameraPosition,
                cameraForward));
    }

    private bool BatchBuildMatchesCurrent(
        BatchBuildState state,
        Vector3 renderOrigin,
        bool widened)
    {
        return ReferenceEquals(state.Target, _stagingBatchSet)
               && _lastBuildValid
               && state.CullEpoch == _cullEpoch
               && state.InvalidationGeneration == _batchInvalidationGeneration
               && state.RenderOrigin == renderOrigin
               && state.StreamActive == _transparencyStreamActive
               && state.Widened == widened
               && state.StartEvictionGeneration == _meshCache.EvictionGeneration;
    }

    private BatchBuildAdvanceResult AdvanceBatchBuild(
        BatchBuildState state,
        ID3D12GraphicsCommandList commandList,
        bool budgeted,
        long sliceStartedTimestamp,
        float cylinderX,
        float cylinderY,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        ref int uploadBudget,
        ref double meshResolveMs,
        ref double instanceBucketingMs,
        ref double batchFinalizeMs)
    {
        const int referenceWorkChunk = 64;
        var performedWork = false;

        bool ShouldYield() =>
            budgeted
            && performedWork
            && Stopwatch.GetElapsedTime(sliceStartedTimestamp).TotalMilliseconds >=
            IncrementalBatchBuildBudgetMilliseconds;

        BatchBuildAdvanceResult CheckBoundary()
        {
            if (state.StartEvictionGeneration != _meshCache.EvictionGeneration)
            {
                state.EvictionChanged = true;
                if (budgeted)
                {
                    return BatchBuildAdvanceResult.EvictionInvalidated;
                }
            }

            return ShouldYield()
                ? BatchBuildAdvanceResult.Incomplete
                : BatchBuildAdvanceResult.Complete;
        }

        while (true)
        {
            switch (state.Phase)
            {
                case BatchBuildPhase.Reset:
                {
                    var resetStarted = StartTiming();
                    var resetWork = Math.Max(
                        1,
                        Math.Min(referenceWorkChunk, state.ResetTotal - state.ResetProcessed));
                    var resetComplete = state.Target.OpaqueBatches.ContinueBegin(resetWork);
                    batchFinalizeMs += ElapsedMilliseconds(resetStarted);
                    if (state.ResetProcessed < state.ResetTotal)
                    {
                        state.ResetProcessed = Math.Min(
                            state.ResetProcessed + resetWork, state.ResetTotal);
                    }
                    performedWork = true;
                    if (resetComplete)
                    {
                        state.Phase = BatchBuildPhase.References;
                    }

                    var boundary = CheckBoundary();
                    if (boundary != BatchBuildAdvanceResult.Complete)
                    {
                        return boundary;
                    }
                    break;
                }
                case BatchBuildPhase.References:
                    if (state.ReferenceIndex >= _cachedCullSurvivors.Count)
                    {
                        state.Phase = BatchBuildPhase.ShadowCasters;
                        continue;
                    }

                    var mainChunkStarted = StartTiming();
                    var mainResolveBefore = meshResolveMs;
                    var mainStop = Math.Min(
                        state.ReferenceIndex + referenceWorkChunk, _cachedCullSurvivors.Count);
                    while (state.ReferenceIndex < mainStop)
                    {
                        ProcessMainBatchReference(
                            state,
                            commandList,
                            _cachedCullSurvivors[state.ReferenceIndex++],
                            cylinderX,
                            cylinderY,
                            cameraPosition,
                            cameraForward,
                            ref uploadBudget,
                            ref meshResolveMs);
                    }
                    instanceBucketingMs += Math.Max(
                        0.0, ElapsedMilliseconds(mainChunkStarted) - (meshResolveMs - mainResolveBefore));
                    performedWork = true;
                    {
                        var boundary = CheckBoundary();
                        if (boundary != BatchBuildAdvanceResult.Complete)
                        {
                            return boundary;
                        }
                    }
                    break;
                case BatchBuildPhase.ShadowCasters:
                    if (state.ShadowIndex >= _cachedShadowOnlyCasters.Count)
                    {
                        state.Phase = BatchBuildPhase.CascadeSort;
                        continue;
                    }

                    var shadowChunkStarted = StartTiming();
                    var shadowResolveBefore = meshResolveMs;
                    var shadowStop = Math.Min(
                        state.ShadowIndex + referenceWorkChunk, _cachedShadowOnlyCasters.Count);
                    while (state.ShadowIndex < shadowStop)
                    {
                        ProcessShadowBatchReference(
                            state,
                            commandList,
                            _cachedShadowOnlyCasters[state.ShadowIndex++],
                            cylinderX,
                            cylinderY,
                            ref uploadBudget,
                            ref meshResolveMs);
                    }
                    instanceBucketingMs += Math.Max(
                        0.0, ElapsedMilliseconds(shadowChunkStarted) - (meshResolveMs - shadowResolveBefore));
                    performedWork = true;
                    {
                        var boundary = CheckBoundary();
                        if (boundary != BatchBuildAdvanceResult.Complete)
                        {
                            return boundary;
                        }
                    }
                    break;
                case BatchBuildPhase.CascadeSort:
                    if (state.SortIndex < 0)
                    {
                        var orderStarted = StartTiming();
                        state.Target.OpaqueBatches.OrderGrassBatchesLast();
                        var frontToBackView = state.OpaqueFrontToBackView;
                        state.Target.OpaqueBatches.OrderForSubmission(in frontToBackView);
                        state.CascadeFit = SnapshotCascadeFitForBuild(state.Widened);
                        state.SortIndex = 0;
                        batchFinalizeMs += ElapsedMilliseconds(orderStarted);
                        performedWork = true;
                        var boundary = CheckBoundary();
                        if (boundary != BatchBuildAdvanceResult.Complete)
                        {
                            return boundary;
                        }
                    }

                    var activeBatches = state.Target.OpaqueBatches.ActiveBatches;
                    if (state.SortIndex >= activeBatches.Count)
                    {
                        state.Phase = BatchBuildPhase.Complete;
                        return state.EvictionChanged
                            ? BatchBuildAdvanceResult.EvictionInvalidated
                            : BatchBuildAdvanceResult.Complete;
                    }

                    var sortStarted = StartTiming();
                    var batchSortComplete = SortBatchInstancesByCascade(
                        state, activeBatches[state.SortIndex]);
                    if (batchSortComplete)
                    {
                        state.SortIndex++;
                        state.CascadeSort = null;
                    }
                    batchFinalizeMs += ElapsedMilliseconds(sortStarted);
                    performedWork = true;
                    {
                        var boundary = CheckBoundary();
                        if (boundary != BatchBuildAdvanceResult.Complete)
                        {
                            return boundary;
                        }
                    }
                    break;
                case BatchBuildPhase.Complete:
                    return state.EvictionChanged
                        ? BatchBuildAdvanceResult.EvictionInvalidated
                        : BatchBuildAdvanceResult.Complete;
                default:
                    throw new InvalidOperationException($"Unexpected batch-build phase {state.Phase}.");
            }
        }
    }

    private BatchMeshSnapshot ResolveBatchMesh(
        BatchBuildState state,
        ID3D12GraphicsCommandList commandList,
        in RenderableReference reference,
        float cylinderX,
        float cylinderY,
        bool registerAnimation,
        ref int uploadBudget,
        ref double meshResolveMs)
    {
        var key = reference.MeshId;
        if (state.Target.MeshSnapshots.TryGetValue(key, out var snapshot))
        {
            return snapshot;
        }

        var pdx = reference.BoundsCenter.X - cylinderX;
        var pdy = reference.BoundsCenter.Y - cylinderY;
        var resolveStarted = StartTiming();
        var mesh = reference.BendableSplineMesh is { } generatedSpline
            ? _meshCache.GetOrUploadGenerated(
                commandList,
                generatedSpline,
                ref uploadBudget)
            : _meshCache.GetOrUpload(
                commandList,
                reference.ModelPath,
                ref uploadBudget,
                (pdx * pdx) + (pdy * pdy),
                reference.AlternateTextures);
        meshResolveMs += ElapsedMilliseconds(resolveStarted);

        var texturesReady = mesh?.TexturesReady ?? false;
        snapshot = new BatchMeshSnapshot(
            mesh,
            texturesReady,
            mesh is not null && (texturesReady || SpeedTreeModelPath.IsSpt(reference.ModelPath)));
        state.Target.MeshSnapshots.Add(key, snapshot);
        AddMeshSnapshotToFingerprint(state, key, snapshot);

        if (mesh is not null)
        {
            _meshLocalRadius[reference.MeshId] = mesh.LocalBoundsRadius;
            _meshLocalBounds[reference.MeshId] = (mesh.LocalBoundsMin, mesh.LocalBoundsMax);
            if (registerAnimation && mesh.Animation is not null)
            {
                var distanceSquared = (pdx * pdx) + (pdy * pdy);
                if (!state.PendingSkinnerRegistrations.TryGetValue(mesh, out var priorDistance) ||
                    distanceSquared < priorDistance)
                {
                    state.PendingSkinnerRegistrations[mesh] = distanceSquared;
                }
            }
        }

        return snapshot;
    }

    private static void AddMeshSnapshotToFingerprint(
        BatchBuildState state,
        uint meshId,
        in BatchMeshSnapshot snapshot)
    {
        static ulong Mix(ulong hash, uint value) => (hash ^ value) * 1099511628211UL;

        var hash = 1469598103934665603UL;
        hash = Mix(hash, meshId);
        hash = Mix(hash, snapshot.Mesh is null ? 0u : 1u);
        hash = Mix(hash, snapshot.TexturesReady ? 1u : 0u);
        hash = Mix(hash, snapshot.MainAdmitted ? 1u : 0u);
        hash = Mix(hash, unchecked((uint)(snapshot.Mesh?.Submeshes.Length ?? 0)));
        // One unique key contributes once, so XOR makes the set fingerprint independent of the
        // camera-distance cell order used by the cull enumerator.
        state.ContentFingerprint ^= hash;
    }

    private void ProcessMainBatchReference(
        BatchBuildState state,
        ID3D12GraphicsCommandList commandList,
        in RenderableReference r,
        float cylinderX,
        float cylinderY,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        ref int uploadBudget,
        ref double meshResolveMs)
    {
        var snapshot = ResolveBatchMesh(
            state, commandList, r, cylinderX, cylinderY, registerAnimation: true,
            ref uploadBudget, ref meshResolveMs);
        var mesh = snapshot.Mesh;
        if (mesh is null)
        {
            state.MainMissing++;
            if (state.Target.MissingPathSet.Add(r.ModelPath))
            {
                state.Target.MissingPaths.Add(r.ModelPath);
            }
            return;
        }

        if (mesh.WaterPlanesLocal.Count > 0 && !_nifWaterRegistry.ContainsOwner(r.FormId))
        {
            // Water ownership is intentionally global and visibility-filtered rather than part of
            // the reference batch aggregate. Keep the original discovery semantics inside the
            // budgeted reference work item so publication cannot inherit a water-heavy tail.
            AccumulateNifWaterPlanes(r, mesh.WaterPlanesLocal);
        }

        if (!snapshot.TexturesReady)
        {
            state.TexturePending++;
            if (!snapshot.MainAdmitted)
            {
                return;
            }
        }

        var anySubmeshDrawn = false;
        var relWorldMatrix = r.WorldMatrix;
        relWorldMatrix.Translation -= state.RenderOrigin;
        var boundsScaleBasis = new Vector3(r.WorldMatrix.M11, r.WorldMatrix.M12, r.WorldMatrix.M13);
        var referenceBounds = new Vector4(
            r.WorldMatrix.Translation, mesh.LocalBoundsRadius * boundsScaleBasis.Length());
        var frontToBackView = state.OpaqueFrontToBackView;
        var frontToBackDepthValid = OpaqueFrontToBackPolicy.TryGetNearestViewDepth(
            in frontToBackView,
            in referenceBounds,
            out var nearestViewDepth);
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
                    // Diagnostic only.
                }
            }

            var instancedGrass = r.IsGrass
                                 && sub.AlphaRenderMode == NifAlphaRenderMode.Blend
                                 && !sub.IsBillboard
                                 && !sub.IsDecal
                                 && sub.DepthWritingBlend
                                 && sub.MaterialAlphaController is null
                                 && sub.LiveParticles is null
                                 && _pipelines.InstancedBlendGrassShaderAvailable;

            if ((sub.AlphaRenderMode == NifAlphaRenderMode.Blend && !instancedGrass) || sub.IsBillboard)
            {
                var sampledSourceWorld = ApplyRigidNodeAnimation(
                    sub, ApplyPhysicsLiteSway(sub, r.FormId, r.WorldMatrix));
                var worldCenter = Vector3.Transform(sub.LocalBoundsCenter, sampledSourceWorld);
                var worldRadius = ResolveWorldBoundsRadius(sub, sampledSourceWorld);
                var sampledRelativeWorld = sampledSourceWorld;
                sampledRelativeWorld.Translation -= state.RenderOrigin;
                var world = sub.IsBillboard
                    ? BuildBillboardWorld(
                        sub.LocalBoundsCenter,
                        sub.BillboardMode,
                        sub.BillboardFrontNormal,
                        sampledSourceWorld,
                        worldCenter,
                        cameraPosition,
                        cameraForward,
                        state.RenderOrigin)
                    : sampledRelativeWorld;
                var blendedDraw = new BlendedReferenceDraw(
                    world,
                    sub,
                    Vector3.DistanceSquared(worldCenter, cameraPosition),
                    sub.AlphaState,
                    sub.RenderState,
                    sub.Specular,
                    r.WorldMatrix,
                    referenceBounds,
                    r.FormId,
                    r.IsGrass,
                    r.GrassWaveMultiplier,
                    worldCenter.Z,
                    worldRadius,
                    ResolveWorldBoundsMaxZ(
                        r.MeshId, sub, sampledSourceWorld, worldCenter.Z, worldRadius),
                    ResolveWorldBoundsMinZ(
                        r.MeshId, sub, sampledSourceWorld, worldCenter.Z, worldRadius),
                    r.MeshId,
                    new Vector2(worldCenter.X, worldCenter.Y));
                if (sub.DepthWritingBlend && !sub.IsBillboard && !state.StreamActive)
                {
                    state.Target.DepthWritingBlendDraws.Add(blendedDraw);
                }
                else
                {
                    state.Target.BlendedDraws.Add(blendedDraw);
                }

                state.DrawableSubmeshPlacements++;
                anySubmeshDrawn = true;
                continue;
            }

            var pso = ResolveOpaquePipeline(sub, r.IsGrass, out var usesModernStandardShader);
            if (instancedGrass)
            {
                pso = _pipelines.GetBlendDepthWritePipeline(
                    sub.SrcBlendMode,
                    sub.DstBlendMode,
                    sub.DoubleSided,
                    decal: false,
                    grassRoute: true,
                    depthTestOff: false,
                    instancedGrass: true);
                usesModernStandardShader = false;
            }
            else if (r.IsGrass && sub.AlphaTest && !sub.IsDecal)
            {
                pso = _pipelines.GetGrassCutoutPso(sub.DoubleSided);
                usesModernStandardShader = false;
            }

            var usesGrassDistanceEnvelope =
                GrassDistanceCullPolicy.UsesEnvelope(r.IsGrass, in _grassDistanceEnvelope);
            var usesTallGrassWind = _tallGrassWindSupported && r.IsGrass && sub.IsTallGrass;
            var batch = state.Target.OpaqueBatches.GetOrCreate(
                sub, pso, usesGrassDistanceEnvelope, usesTallGrassWind, r.GrassWaveMultiplier,
                usesModernStandardShader);
            if (state.OpaqueFrontToBackView.Valid &&
                !state.Target.OpaqueBatches.ObserveFrontToBackDepth(
                    batch,
                    frontToBackDepthValid,
                    nearestViewDepth))
            {
                state.OpaqueFrontToBackView = frontToBackView.Fail(
                    OpaqueFrontToBackFallbackReason.InvalidBounds);
            }
            batch.Instances.Add(relWorldMatrix);
            batch.InstanceBounds.Add(referenceBounds);
            if (sub.PhysicsLiteSway is not null)
            {
                batch.PhysicsLiteSeeds.Add(r.FormId);
            }
            if (_formIdHeatmapEnabled)
            {
                batch.HeatmapFormIds.Add(r.IsGrass ? 0u : r.FormId);
            }

            state.DrawableSubmeshPlacements++;
            anySubmeshDrawn = true;
        }

        if (anySubmeshDrawn)
        {
            state.Drawn++;
        }

    }

    private void ProcessShadowBatchReference(
        BatchBuildState state,
        ID3D12GraphicsCommandList commandList,
        in RenderableReference r,
        float cylinderX,
        float cylinderY,
        ref int uploadBudget,
        ref double meshResolveMs)
    {
        var snapshot = ResolveBatchMesh(
            state, commandList, r, cylinderX, cylinderY, registerAnimation: false,
            ref uploadBudget, ref meshResolveMs);
        var mesh = snapshot.Mesh;
        if (mesh is null)
        {
            state.ShadowMissing++;
            if (state.Target.MissingPathSet.Add(r.ModelPath))
            {
                state.Target.MissingPaths.Add(r.ModelPath);
            }
            return;
        }

        var relWorldMatrix = r.WorldMatrix;
        relWorldMatrix.Translation -= state.RenderOrigin;
        foreach (var sub in mesh.Submeshes)
        {
            if (sub.AlphaRenderMode == NifAlphaRenderMode.Blend || sub.IsBillboard || sub.IsDecal
                || (_tallGrassWindSupported && sub.IsTallGrass)
                || (!_treeShadowsEnabled &&
                    (sub.IsSpeedTreeBranch || sub.IsLeafBillboard || sub.SpeedTreeLod is not null)))
            {
                continue;
            }

            var pso = ResolveOpaquePipeline(sub, r.IsGrass, out var usesModernStandardShader);
            if (r.IsGrass && sub.AlphaTest)
            {
                pso = _pipelines.GetGrassCutoutPso(sub.DoubleSided);
                usesModernStandardShader = false;
            }

            var usesGrassDistanceEnvelope =
                GrassDistanceCullPolicy.UsesEnvelope(r.IsGrass, in _grassDistanceEnvelope);
            var usesTallGrassWind = _tallGrassWindSupported && r.IsGrass && sub.IsTallGrass;
            var batch = state.Target.OpaqueBatches.GetOrCreate(
                sub, pso, usesGrassDistanceEnvelope, usesTallGrassWind, r.GrassWaveMultiplier,
                usesModernStandardShader);
            batch.ShadowOnlyInstances.Add(relWorldMatrix);
            if (sub.PhysicsLiteSway is not null)
            {
                batch.ShadowOnlyPhysicsLiteSeeds.Add(r.FormId);
            }
            state.DrawableSubmeshPlacements++;
        }

    }

    private ID3D12PipelineState ResolveOpaquePipeline(
        CachedSubmesh12 sub,
        bool isScatteredGrass,
        out bool usesModernStandardShader)
    {
        var facts = new ModernStandardOpaqueShaderFacts(
            Game: _renderCache?.Game ?? Core.Games.BethesdaGame.Unknown,
            HeatmapEnabled: _formIdHeatmapEnabled,
            IsScatteredGrass: isScatteredGrass,
            AlphaBlend: sub.AlphaBlend,
            IsDecal: sub.IsDecal,
            IsEmissive: sub.IsEmissive,
            IsLighting30: sub.IsLighting30,
            HasLighting30GlowMap: sub.Lighting30GlowMap is not null,
            HasEffectFalloff: sub.HasEffectFalloff,
            IsEffectTintNeutral: sub.EffectTint == Vector3.One,
            HasSoftParticle: sub.SoftParticle.Enabled,
            IsBillboard: sub.IsBillboard,
            IsLeafBillboard: sub.IsLeafBillboard,
            IsTallGrass: sub.IsTallGrass,
            IsParticle: sub.ParticleCenters is not null || sub.LiveParticles is not null,
            HasRuntimeSpeedTreeLod: sub.SpeedTreeLod is not null,
            HasClassicBasicShader: sub.ClassicBasicShaderMode != FnvClassicBasicShaderMode.None,
            HasClassicEnvironmentMap:
                sub.ClassicEnvMap is not null ||
                sub.ClassicEnvMask is not null ||
                sub.ClassicEnvMapUsesWindowReflection ||
                sub.ClassicEnvMapIsSphereMap,
            HasClassicParallax: sub.ClassicParallaxHeightMap is not null,
            HasGradientMap: sub.GradientMap is not null,
            StarfieldMaterialPath: sub.StarfieldMaterialPath,
            HasDerivedStarfieldNormal: sub.HasDerivedStarfieldNormal,
            TextureFeatureMask: (uint)MathF.Round(sub.TextureState.Z),
            HasBump: sub.HasBump,
            HasSpecularMap: sub.SpecularMap is not null,
            SpecularExponent: sub.Specular.W,
            ModernEnvironmentMapDeclared: sub.EnvMap is not null,
            ModernEnvironmentMapScale: sub.EnvMapScale,
            WrapTextureU: !sub.ClampTextureU,
            WrapTextureV: !sub.ClampTextureV,
            AlphaTestEnabled: sub.AlphaState.Y >= 0f,
            AlphaTestFunction: sub.AlphaTestFunction,
            DoubleSided: sub.DoubleSided);
        var variant = ModernStandardOpaqueShaderPolicy.Resolve(in facts);
        if (TryResolveStarfieldDiffuseLitVariant(variant, out var alphaGreater, out var doubleSided) &&
            _pipelines.TryGetStarfieldDiffuseLitPso(alphaGreater, doubleSided, out var starfieldPso))
        {
            // The frozen packet snapshots the same bindless diffuse/normal descriptors and draw
            // constants as the FO76 specialization. Mark this route specialized as well so it can
            // reuse that exact packet resource policy once all resources are terminal.
            usesModernStandardShader = true;
            return starfieldPso!;
        }

        if (_pipelines.TryGetModernStandardOpaquePso(variant, out var specializedPso))
        {
            usesModernStandardShader = true;
            return specializedPso!;
        }

        usesModernStandardShader = false;
        return (sub.DoubleSided, sub.IsDecal) switch
        {
            (true, true) => _pipelines.OpaqueDoubleDecalPso,
            (true, false) => _pipelines.OpaqueDoublePso,
            (false, true) => _pipelines.OpaqueBackDecalPso,
            (false, false) => _pipelines.OpaqueBackPso
        };
    }

    private static bool TryResolveStarfieldDiffuseLitVariant(
        ModernStandardOpaqueShaderVariant variant,
        out bool alphaGreater,
        out bool doubleSided)
    {
        (alphaGreater, doubleSided) = variant switch
        {
            ModernStandardOpaqueShaderVariant.StarfieldSingleSidedOpaque => (false, false),
            ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedOpaque => (false, true),
            ModernStandardOpaqueShaderVariant.StarfieldSingleSidedGreaterCutout => (true, false),
            ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedGreaterCutout => (true, true),
            _ => (false, false)
        };
        return variant is
            ModernStandardOpaqueShaderVariant.StarfieldSingleSidedOpaque or
            ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedOpaque or
            ModernStandardOpaqueShaderVariant.StarfieldSingleSidedGreaterCutout or
            ModernStandardOpaqueShaderVariant.StarfieldDoubleSidedGreaterCutout;
    }

    private void ObserveBatchBuildActivity(BatchBuildState state)
    {
        state.ObservedFrames++;
        state.SawGpuUpload |= _meshCache.FrameGpuUploads != 0;
        _batchContentDirtyPending |= _meshCache.FrameGpuUploads != 0;
        state.SawNonQuietActivity |=
            state.TexturePending != 0
            || _meshCache.FrameGpuUploads != 0
            || _meshCache.FrameCacheMisses != 0
            || _meshCache.FrameDecodeRequests != 0
            || _meshCache.FrameDecodeStarts != 0
            || _meshCache.FrameActiveDecodes != 0;
    }

    private bool TryCompleteSynchronousBatchBuild(
        BatchReuseBlocker trigger,
        Vector3 renderOrigin,
        bool widened,
        ID3D12GraphicsCommandList commandList,
        float cylinderX,
        float cylinderY,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        ref int uploadBudget,
        ref double meshResolveMs,
        ref double instanceBucketingMs,
        ref double batchFinalizeMs,
        out BatchBuildState? completed)
    {
        // The first pass after a staged eviction warms/touches the working set. A second pass is then
        // normally generation-stable; keep one final retry for an unusually dense insertion wave.
        const int maximumAttempts = 3;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            var state = StartBatchBuild(
                trigger,
                renderOrigin,
                widened,
                cameraPosition,
                cameraForward);
            var result = AdvanceBatchBuild(
                state,
                commandList,
                budgeted: false,
                sliceStartedTimestamp: 0,
                cylinderX,
                cylinderY,
                cameraPosition,
                cameraForward,
                ref uploadBudget,
                ref meshResolveMs,
                ref instanceBucketingMs,
                ref batchFinalizeMs);
            ObserveBatchBuildActivity(state);
            if (result == BatchBuildAdvanceResult.Complete)
            {
                completed = state;
                return true;
            }
        }

        completed = null;
        return false;
    }

    private void PublishBatchBuild(
        BatchBuildState state,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 renderOrigin,
        ref double blendedRefreshMs)
    {
        if (state.StartEvictionGeneration != _meshCache.EvictionGeneration ||
            state.CullEpoch != _cullEpoch ||
            state.InvalidationGeneration != _batchInvalidationGeneration ||
            state.RenderOrigin != renderOrigin ||
            state.StreamActive != _transparencyStreamActive)
        {
            throw new InvalidOperationException("Attempted to publish an invalidated reference batch build.");
        }

        var refreshStarted = StartTiming();
        RefreshBlendedDraws(
            state.Target.BlendedDraws,
            state.Target.DepthWritingBlendDraws,
            cameraPosition,
            cameraForward,
            renderOrigin);
        blendedRefreshMs += ElapsedMilliseconds(refreshStarted);

        foreach (var (mesh, distanceSquared) in state.PendingSkinnerRegistrations)
        {
            _skinner.Register(mesh, distanceSquared);
        }

        var sameSurvivorGeneration = _lastBuildValid && state.CullEpoch == _lastBuildCullEpoch;
        var contentChanged = _batchContentDirtyPending
                             || state.SawGpuUpload
                             || _lastBuildEvictionGen != _meshCache.EvictionGeneration
                             || (sameSurvivorGeneration &&
                                 (state.ContentFingerprint != _lastBuildContentFingerprint
                                  || state.DrawableSubmeshPlacements != _lastBuildDrawableSubmeshPlacements));

        state.Target.CascadeFit = SnapshotCascadeFit(state.CascadeFit);

        // Publication identity always advances below, so the current packet can never match again.
        // Retire it at the invalidation seam instead of retaining its upload resource, tail snapshots,
        // and old batch graph indefinitely while a moving camera prevents a replacement debounce.
        RetireOpaqueSubmissionPacket();
        var oldPublished = _publishedBatchSet;
        _publishedBatchSet = state.Target;
        _stagingBatchSet = oldPublished;
        unchecked
        {
            _publishedBatchIdentity++;
            _publishedBatchGeneration++;
        }

        _lastFrameMissingMeshPaths.Clear();
        _lastFrameMissingMeshPaths.AddRange(state.Target.MissingPaths);

        var buildQuiet = !state.SawNonQuietActivity
                         && state.TexturePending == 0
                         && _meshCache.PendingDecodeCount == 0
                         && _meshCache.FrameActiveDecodes == 0;
        _quietBuildStreak = buildQuiet
            ? _quietBuildStreak + Math.Max(state.ObservedFrames, 1)
            : 0;
        if (contentChanged)
        {
            unchecked { BatchContentVersion++; }
        }
        _batchContentDirtyPending = false;

        var missingPopulationStable =
            state.MainMissing == _lastBuildMissing &&
            state.ShadowMissing == _lastBuildShadowMissing;
        _missingStallStreak = missingPopulationStable ? _missingStallStreak + 1 : 0;
        _lastBuildValid = true;
        _framesSinceBuild = 0;
        _lastBuildCullEpoch = state.CullEpoch;
        _lastBuildRenderOrigin = state.RenderOrigin;
        _lastBuildEvictionGen = _meshCache.EvictionGeneration;
        _lastBuildQuiesced = _quietBuildStreak >= QuietBuildStreakFrames;
        _lastBuildStreamActive = state.StreamActive;
        _lastBuildDrawn = state.Drawn;
        _lastBuildMissing = state.MainMissing;
        _lastBuildShadowMissing = state.ShadowMissing;
        _lastBuildTexturePending = state.TexturePending;
        _lastBuildDrawableSubmeshPlacements = state.DrawableSubmeshPlacements;
        _lastBuildContentFingerprint = state.ContentFingerprint;
        _batchesWidened = state.Widened;
        state.Phase = BatchBuildPhase.Complete;
    }

    private void ClearPublishedBatchSnapshotAfterEviction()
    {
        RetireOpaqueSubmissionPacket();
        _publishedBatchSet.OpaqueBatches.Begin();
        _publishedBatchSet.BlendedDraws.Clear();
        _publishedBatchSet.DepthWritingBlendDraws.Clear();
        _publishedBatchSet.CascadeFit = null;
        _lastFrameMissingMeshPaths.Clear();
        _lastBuildValid = false;
        _lastBuildDrawn = 0;
        _lastBuildMissing = 0;
        _lastBuildShadowMissing = 0;
        _lastBuildTexturePending = 0;
        _lastBuildDrawableSubmeshPlacements = 0;
        _batchesWidened = false;
        unchecked
        {
            _publishedBatchIdentity++;
            _publishedBatchGeneration++;
        }
        unchecked { BatchContentVersion++; }
    }

    private static int BatchBuildProcessed(BatchBuildState state)
    {
        return state.ResetProcessed
               + state.ReferenceIndex
               + state.ShadowIndex
               + Math.Max(state.SortIndex, 0)
               + (state.SortIndex >= 0 ? 1 : 0);
    }

    private int BatchBuildTotal(BatchBuildState state)
    {
        return state.ResetTotal
               + _cachedCullSurvivors.Count
               + _cachedShadowOnlyCasters.Count
               + state.Target.OpaqueBatches.ActiveBatches.Count
               + 1;
    }

    private (
        Vector3 Anchor,
        Vector3 SunDirection,
        float[] Radii,
        float[] Snaps,
        float SceneZSpan)? SnapshotCascadeFitForBuild(bool widened)
    {
        var snapshot = SnapshotCascadeFit(_cascadeFit);
        if (!widened || snapshot is not { } fit || _cullCacheCascadeAnchorXY is not { } cullAnchor)
        {
            return snapshot;
        }

        return (
            new Vector3(cullAnchor.X, cullAnchor.Y, fit.Anchor.Z),
            fit.SunDirection,
            fit.Radii,
            fit.Snaps,
            fit.SceneZSpan);
    }

    private static (
        Vector3 Anchor,
        Vector3 SunDirection,
        float[] Radii,
        float[] Snaps,
        float SceneZSpan)? SnapshotCascadeFit(
        (Vector3 Anchor, Vector3 SunDirection, float[] Radii, float[] Snaps, float SceneZSpan)? fit)
    {
        return fit is not { } value
            ? null
            : (value.Anchor, value.SunDirection, value.Radii.ToArray(), value.Snaps.ToArray(), value.SceneZSpan);
    }

    private static bool CascadeFitsCompatible(
        (Vector3 Anchor, Vector3 SunDirection, float[] Radii, float[] Snaps, float SceneZSpan)? left,
        (Vector3 Anchor, Vector3 SunDirection, float[] Radii, float[] Snaps, float SceneZSpan)? right)
    {
        if (left.HasValue != right.HasValue)
        {
            return false;
        }
        if (left is not { } a || right is not { } b)
        {
            return true;
        }

        // ClassifyCascade deliberately expands every fitted volume by this exact anchor-drift
        // allowance. Requiring raw-float anchor equality would make a multi-frame build restart on
        // every walking/orbit frame and could prevent publication forever. Direction and ladder
        // geometry have no corresponding envelope, so those remain exact hard keys.
        var anchorDelta = a.Anchor - b.Anchor;
        var anchorCompatible = anchorDelta.LengthSquared() <=
                               CullPositionSlack * CullPositionSlack * 3.0001f;
        return anchorCompatible
               && a.SunDirection.Equals(b.SunDirection)
               && a.SceneZSpan.Equals(b.SceneZSpan)
               && a.Radii.AsSpan().SequenceEqual(b.Radii)
               && a.Snaps.AsSpan().SequenceEqual(b.Snaps);
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
    public void RenderBlendedDeferred()
    {
        // No split this frame: make that legible instead of leaving the previous frame's numbers
        // standing. A stale non-zero count here is exactly what disguised the below-water split
        // being disabled outdoors for a whole round of testing.
        ResetBelowWaterPartitionTelemetry();
        RenderBlendedDeferredCore(DeferredWaterPartition.All, probe: null, cameraZ: 0f, allowSceneDepth: true);
    }

    /// <summary>
    ///     Blended submeshes classified as wholly below the water surface by the most recent
    ///     below-water partition, alongside the candidate total and a representative surface height.
    ///     Surfaced in the HUD and the capture telemetry so "are submerged decals actually being
    ///     reordered?" is a readable number rather than a pixel-level judgement call. Reset whenever a
    ///     frame does not partition, so zero always means "not splitting".
    /// </summary>
    public int LastBelowWaterBlendedDraws { get; private set; }

    /// <inheritdoc cref="LastBelowWaterBlendedDraws" />
    public int LastPartitionedBlendedCandidates { get; private set; }

    /// <inheritdoc cref="LastBelowWaterBlendedDraws" />
    public float LastBelowWaterPartitionPlane { get; private set; }

    /// <summary>
    ///     Blended draws classified as wholly above every queued water surface by the most recent
    ///     above-all-water partition — the draws issued after the final water drain so no surface
    ///     can composite over them. Zero always means "not splitting", same discipline as the
    ///     below-water pair.
    /// </summary>
    public int LastAboveWaterBlendedDraws { get; private set; }

    private void ResetBelowWaterPartitionTelemetry()
    {
        LastBelowWaterBlendedDraws = 0;
        LastPartitionedBlendedCandidates = 0;
        LastBelowWaterPartitionPlane = float.NaN;
        LastAboveWaterBlendedDraws = 0;
    }

    /// <summary>
    ///     Draws only translucent geometry lying wholly below the water surface local to its own XY.
    ///     The host invokes this before the water pass so underwater decals and effects end up beneath
    ///     the surface — water writes no depth, so anything issued afterwards composites on top of it
    ///     regardless of where it sits in the world. Scene-depth sampling is deliberately disabled
    ///     here because the depth resource is still in its ordinary writable state.
    /// </summary>
    public void RenderBlendedDeferredBelowWater(IWaterHeightProbe probe, float cameraZ) =>
        RenderBlendedDeferredCore(
            DeferredWaterPartition.WhollyBelow,
            probe,
            cameraZ,
            allowSceneDepth: false,
            // Explicit, not defaulted: NaN keeps the above-all-water class EMPTY on the legacy
            // split path, so NotWhollyBelow retains its full legacy meaning (below vs the rest).
            // A finite plane here would silently drop the wholly-above draws from the post-water
            // pass that this path never issues. IsWhollyAboveAllWater pins NaN => false.
            maxQueuedWaterHeight: float.NaN);

    /// <summary>Draws the complementary intersecting/above-water translucent partition.</summary>
    public void RenderBlendedDeferredAtOrAboveWater(IWaterHeightProbe probe, float cameraZ) =>
        RenderBlendedDeferredCore(
            DeferredWaterPartition.NotWhollyBelow,
            probe,
            cameraZ,
            allowSceneDepth: true,
            // Same invariant as the below-water leg: the legacy split has no post-water
            // above-all-water pass, so the class must stay empty or these draws vanish.
            maxQueuedWaterHeight: float.NaN);

    // Set per frame by the host BEFORE Render: this frame's blended routing target. Deliberately
    // per-frame rather than the static env switch — a non-streaming frame (other game, no visible
    // FNV cell water) must keep the legacy hoisted depth-writing path.
    private bool _transparencyStreamActive;

    /// <summary>Tells the next <c>Render</c> whether this frame's transparency draws through the
    /// unified stream (see <see cref="RenderBlendedDeferredUnified" />).</summary>
    public void SetTransparencyStreamActive(bool active) => _transparencyStreamActive = active;

    /// <summary>
    ///     Unified transparency stream: draws the deferred blended submeshes back-to-front while
    ///     interleaving the water renderer's queued batches at their depths (the host drains the
    ///     remaining nearer water afterwards). Scene depth is the post-opaque snapshot, so soft
    ///     particles work across the whole stream while the live DSV stays writable for
    ///     depth-writing blends.
    ///     <para>
    ///         <paramref name="probe" /> restricts the stream to draws NOT wholly below the water
    ///         surface; the host draws the submerged complement through
    ///         <see cref="RenderBlendedDeferredBelowWater" /> beforehand. The global depth sort alone
    ///         cannot order those: water is queued per CELL, so its sort key is a 4096-unit quad's
    ///         CENTROID, and any submerged decal sitting in the near half of its own cell sorts
    ///         nearer than the surface above it and composites on top. That is exactly the residual
    ///         "decals underwater render above it, but not at all angles" — the submerged/above split
    ///         is a classification, not a distance, so it has to stay one.
    ///     </para>
    /// </summary>
    public void RenderBlendedDeferredUnified(
        ITransparencyInterleave interleave, IWaterHeightProbe? probe = null, float cameraZ = 0f,
        float maxQueuedWaterHeight = float.NaN)
    {
        // No probe = no split this frame. Clear the counters rather than leaving the previous
        // frame's standing, for the same reason RenderBlendedDeferred does: a stale non-zero is
        // what once disguised the below-water split being off outdoors for a whole test round.
        if (probe is null) ResetBelowWaterPartitionTelemetry();

        // Address 0 = the opaque pass skipped this frame (ring exhaustion) — nothing valid to bind.
        // The HOST still drains the water queue afterwards, so water survives such a frame.
        if (_blendedDraws.Count == 0 || _deferredPerFrameCbvAddress == 0) return;
        var cmd = _recorder.CommandList;

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, _deferredPerFrameCbvAddress);
        GpuRootSignature12.SetGraphicsBindlessTables(cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);

        ID3D12PipelineState? currentPso = null;
        double cbUpdateMs = 0, drawCallMs = 0;
        var submeshDraws = 0;
        var sceneDepthSampled = _deferredSceneDepthIndex != NoSceneDepth;
        var submissionStarted = StartTiming();
        DrawBlended(
            cmd, _deferredFrameIndex, sceneDepthSampled,
            ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws,
            probe is null ? DeferredWaterPartition.All : DeferredWaterPartition.NotWhollyBelow,
            probe, cameraZ, maxQueuedWaterHeight,
            interleave);
        LastStats.ReferenceBlendedSubmissionMilliseconds += ElapsedMilliseconds(submissionStarted);

        LastStats.ReferenceSubmeshDraws += submeshDraws;
        LastStats.ReferenceCbUpdateMilliseconds += cbUpdateMs;
        LastStats.ReferenceDrawCallMilliseconds += drawCallMs;
    }

    /// <summary>
    ///     Draws the blended partition no queued water surface can occlude (bounds bottom and
    ///     camera both above this frame's highest queued surface). The host issues this AFTER the
    ///     unified stream and the final water drain: those draws are always in front of every
    ///     surface, and water writes no depth, so a surface issued after them composites on top —
    ///     the exact "cell water renders over smoke standing above it" defect. Pass the SAME
    ///     <paramref name="maxQueuedWaterHeight" /> the unified call used so the two partitions
    ///     stay complementary within the frame.
    /// </summary>
    public void RenderBlendedDeferredAboveAllWater(
        IWaterHeightProbe probe, float cameraZ, float maxQueuedWaterHeight) =>
        RenderBlendedDeferredCore(
            DeferredWaterPartition.WhollyAboveAllWater,
            probe,
            cameraZ,
            allowSceneDepth: true,
            maxQueuedWaterHeight);

    private void RenderBlendedDeferredCore(
        DeferredWaterPartition waterPartition,
        IWaterHeightProbe? probe,
        float cameraZ,
        bool allowSceneDepth,
        float maxQueuedWaterHeight = float.NaN)
    {
        // Address 0 = the opaque pass skipped this frame (ring exhaustion) — nothing valid to bind.
        if (_blendedDraws.Count == 0 || _deferredPerFrameCbvAddress == 0) return;
        var cmd = _recorder.CommandList;

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, _deferredPerFrameCbvAddress);
        GpuRootSignature12.SetGraphicsBindlessTables(cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);

        ID3D12PipelineState? currentPso = null;
        double cbUpdateMs = 0, drawCallMs = 0;
        var submeshDraws = 0;
        var sceneDepthSampled = allowSceneDepth && _deferredSceneDepthIndex != NoSceneDepth;
        var submissionStarted = StartTiming();
        DrawBlended(
            cmd, _deferredFrameIndex, sceneDepthSampled,
            ref currentPso, ref cbUpdateMs, ref drawCallMs, ref submeshDraws,
            waterPartition, probe, cameraZ, maxQueuedWaterHeight);
        LastStats.ReferenceBlendedSubmissionMilliseconds += ElapsedMilliseconds(submissionStarted);

        LastStats.ReferenceSubmeshDraws += submeshDraws;
        LastStats.ReferenceCbUpdateMilliseconds += cbUpdateMs;
        LastStats.ReferenceDrawCallMilliseconds += drawCallMs;
    }

    // The shadow VS's PerFrame ABI lives in ReferenceRendererConstants12 as
    // InstancedShadowPerFrameConstants, so platform-neutral tests can reflect its exact layout —
    // the field ORDER is what keeps uWindMatrices at the same offset in both shader variants.

    /// <summary>
    ///     The wind rig this frame's draws were built against. The shadow replay MUST use the same
    ///     one as the main pass or the caster and the visible geometry sway out of step, which is the
    ///     defect the shared cbuffer layout exists to prevent.
    /// </summary>
    private Core.Formats.SpeedTree.SpeedTreeWindRig FrameWindRig =>
        _captureWindActive ? _captureWindRig : _windRig;

    /// <summary>
    ///     Replays this frame's captured opaque draws into the (already bound) shadow-map depth
    ///     target from the light's view. The host binds the shadow DSV/viewport first
    ///     (<c>ShadowMapRenderer12.BeginRender</c>); this method only sets its own per-frame CB
    ///     (the light viewProj + leaf-card basis at b0), the depth-only PSOs, and each draw's
    ///     captured InstanceDraw-CB / instance-SRV addresses — all frame-local ring allocations,
    ///     so it MUST run in the same host frame that captured them (see
    ///     <see cref="ArmShadowCapture" />).
    ///     Returns true only when at least one post-cascade-filter draw was submitted. See
    ///     <see cref="LastShadowReplayCompleted" /> to distinguish an authoritative empty cascade
    ///     from a replay that could not run.
    /// </summary>
    /// <param name="cascadeIndex">
    ///     Which cascade is being filled. Each captured draw carries a per-cascade instance count (a
    ///     prefix into its cascade-ordered block), so a near cascade submits only the casters that can
    ///     actually reach it instead of the whole scene.
    /// </param>
    public bool RenderShadowDepth(in SunShadowMath.LightFrustum frustum, int cascadeIndex = 0)
    {
        _shadowCaptureArmed = false;
        LastShadowSubmittedDrawCount = 0;
        LastShadowSubmittedInstanceCount = 0;
        LastShadowReplayCompleted = false;
        if (_shadowDraws.Count == 0)
        {
            LastShadowReplayCompleted = true;
            return false;
        }

        // Avoid consuming ring space when captured batches exist but this cascade's fitted box
        // contains none of their instances. This is an authoritative empty result, not a failure.
        var hasCascadeInstances = false;
        foreach (var draw in _shadowDraws)
        {
            if (ShadowCascadeSubmissionPolicy.ClampInstanceCount(
                    draw.DrawCount, draw.Cascades[cascadeIndex]) <= 0) continue;
            hasCascadeInstances = true;
            break;
        }

        if (!hasCascadeInstances)
        {
            LastShadowReplayCompleted = true;
            return false;
        }

        var cmd = _recorder.CommandList;
        if (!_ringBuffer.TryAllocate(
                _recorder.FrameIndex, InstancedShadowPerFrameConstants.ByteSize, out var perFrameAlloc,
                GpuRingBuffer12.CbAlignment))
        {
            return false; // the host disables this cleared cascade and falls through to a farther map
        }

        unsafe
        {
            // Same wind rig the main pass used this frame (FrameWindRig), so the caster deforms
            // exactly like the geometry it is casting for. Uploading these is what the corrected
            // cbuffer layout unlocked — the wind terms were previously preprocessed out of the
            // shadow VS because they fell outside a 96-byte allocation.
            var windRig = FrameWindRig;
            var constants = new InstancedShadowPerFrameConstants
            {
                ViewProj = frustum.ViewProj,
                CardRight = new Vector4(frustum.CardRight, 0f),
                CardUp = new Vector4(frustum.CardUp, 0f),
            };
            for (var w = 0; w < 4; w++)
            {
                constants.WindMatrices[w] = windRig.WindMatrix(w);
            }

            *(InstancedShadowPerFrameConstants*)perFrameAlloc.CpuPtr = constants;
        }
        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        GpuRootSignature12.SetGraphicsBindlessTables(cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);

        ID3D12PipelineState? currentPso = null;
        ulong currentInstanceAddress = 0;
        foreach (var draw in _shadowDraws)
        {
            // Tested BEFORE any state binding: a batch that cannot reach this cascade should cost
            // nothing at all, not a PSO/SRV/CB/IA setup followed by a zero-instance draw.
            var cascadeInstances = ShadowCascadeSubmissionPolicy.ClampInstanceCount(
                draw.DrawCount, draw.Cascades[cascadeIndex]);
            if (cascadeInstances <= 0)
            {
                continue;
            }

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
            cmd.DrawIndexedInstanced((uint)draw.IndexCount, (uint)cascadeInstances, 0, 0, 0);
            LastShadowSubmittedDrawCount++;
            LastShadowSubmittedInstanceCount += cascadeInstances;
            if (draw.UsesTallGrassWind)
            {
                LastStats.ReferenceTallGrassShadowDraws++;
                LastStats.ReferenceTallGrassShadowInstances += cascadeInstances;
            }
        }

        LastShadowReplayCompleted = true;
        return LastShadowSubmittedDrawCount > 0;
    }

    /// <summary>
    ///     Water-reflection mirror COLOR replay: re-issues this frame's captured instanced-opaque
    ///     draws with the mirrored view-projection into the already-bound reflection target. One
    ///     fresh b0 (mirror viewProj + the SAME wind rig the main pass used) is the only new GPU
    ///     allocation — each draw reuses its original PSO (winding-flipped twin via
    ///     <see cref="ReferencePipelineFactory12.GetMirrorPso" />), per-draw constants, and t8
    ///     instance window, all frame-local ring addresses. No cull, no batch, no cache touch.
    ///     The caller has bound the mirror b3 (clip plane + mirrored shading camera). Returns
    ///     false on ring exhaustion — the reflection keeps its sky content for the frame.
    /// </summary>
    public bool RenderMirrorColor(Matrix4x4 mirrorViewProj)
    {
        _mirrorCaptureArmed = false;
        if (_mirrorDraws.Count == 0)
        {
            return false;
        }

        var cmd = _recorder.CommandList;
        if (!_ringBuffer.TryAllocate(
                _recorder.FrameIndex, PerFrameByteSize, out var perFrameAlloc, GpuRingBuffer12.CbAlignment))
        {
            return false;
        }

        unsafe
        {
            *(Matrix4x4*)perFrameAlloc.CpuPtr = mirrorViewProj;
            var windRig = FrameWindRig;
            var windDst = (Matrix4x4*)((byte*)perFrameAlloc.CpuPtr
                + InstancedShadowPerFrameConstants.WindMatricesOffset);
            for (var w = 0; w < 4; w++)
            {
                windDst[w] = windRig.WindMatrix(w);
            }
        }

        cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        cmd.SetGraphicsRootConstantBufferView(GpuRootSignature12.Slots.PerFrameCbv, perFrameAlloc.GpuAddress);
        GpuRootSignature12.SetGraphicsBindlessTables(cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);

        ID3D12PipelineState? currentPso = null;
        ulong currentInstanceAddress = 0;
        foreach (var draw in _mirrorDraws)
        {
            var pso = _pipelines.GetMirrorPso(draw.Pso);
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
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RetireOpaqueSubmissionPacket();
        _opaqueIndirectSignature?.Dispose();
        _pipelines.Dispose();
    }

    /// <summary>
    ///     Angular bound for the tolerant cull cache: true when the camera has rotated no further than
    ///     the establishment frustum was widened. <paramref name="cosSlack" /> of 1 means "no widening
    ///     was applied" (orthographic, or the slack is configured off) and degrades to an exact compare
    ///     — a <c>Dot &gt;= 1f</c> test would be flaky at ~0.99999994, so it is spelled out.
    ///     Both vectors are unit (CameraState.Forward, RendererCameraMotion.ForwardFromYawPitch), so
    ///     <c>Dot &gt;= cos θ</c> is exactly "angle &lt;= θ".
    /// </summary>
    private static bool ForwardWithin(Vector3 cached, Vector3 current, float cosSlack) =>
        cosSlack >= 1f ? cached == current : Vector3.Dot(cached, current) >= cosSlack;

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
    private void AccumulateNifWaterPlanes(
        in RenderableReference owner,
        IReadOnlyList<NifWaterGeometry> localPlanes)
    {
        var placedPlanes = new List<NifWaterGeometry>(localPlanes.Count);
        for (var pi = 0; pi < localPlanes.Count; pi++)
        {
            var placed = localPlanes[pi].Transform(owner.WorldMatrix);
            if (placed is null)
            {
                continue;
            }

            // Water renders with the type of WHERE IT IS: stamp the surface with its own cell's
            // XCWT at placement time (0 → the renderer falls back to the worldspace default).
            // Without this, placed surfaces rendered with the CAMERA cell's sticky selection and
            // changed look wholesale when the camera crossed an XCWT boundary mid-flight.
            placedPlanes.Add(placed.WithWaterFormId(ResolveNifWaterPlaneFormId(placed)));
        }

        _nifWaterRegistry.Register(owner, placedPlanes);
    }

    /// <summary>The XCWT of the cell containing the placed surface's centroid, or 0 when the cell
    /// is unknown or authors no override.</summary>
    private uint ResolveNifWaterPlaneFormId(NifWaterGeometry placed)
    {
        if (_cells is null)
        {
            return 0;
        }

        var center = (placed.BoundsMin + placed.BoundsMax) * 0.5f;
        var cellSize = _spatialIndex?.CellSize ?? WorldGridConstants.CellSize;
        return PlacedNifWaterCellResolver.Resolve(_cells, new Vector2(center.X, center.Y), cellSize);
    }

    private InstanceDrawConstants BuildOpaqueInstanceDrawConstants(
        OpaqueBatchState batchState,
        uint instanceBase,
        out Vector4 textureState)
    {
        var sub = batchState.Submesh;
        textureState = ResolveTextureState(sub);
        // Video-settings "Shadows on grass" OFF for SCATTERED grass: the submesh has no grass
        // marker (scatter identity lives on the reference/batch), so the no-sun-shadow bit is
        // ORed here, where the batch's grass route is known. The per-game grass shaders honor
        // the bit; TallGrass got it inside ResolveTextureState.
        if (!_grassShadowsEnabled && batchState.UsesGrassDistanceEnvelope)
        {
            textureState.Z = (uint)MathF.Round(textureState.Z)
                | FnvActiveAdtBasePolicy.RuntimeFnvGrassNoSunShadowFlag;
        }

        return new InstanceDrawConstants(
            sub.AlphaState,
            sub.RenderState,
            textureState,
            new TexIndexQuad(
                sub.Diffuse.BindlessIndex, sub.Normal.BindlessIndex,
                sub.StarfieldOpacity?.BindlessIndex ??
                sub.ClassicParallaxHeightMap?.BindlessIndex ??
                sub.ClassicEnvMask?.BindlessIndex ?? sub.SpecularMap?.BindlessIndex ?? 0,
                sub.GradientMap?.BindlessIndex ?? sub.Lighting30GlowMap?.BindlessIndex ?? 0),
            instanceBase,
            UvOffsetU: WrapUv(sub.UvScrollVelocity.X, UvScrollClock),
            UvOffsetV: WrapUv(sub.UvScrollVelocity.Y, UvScrollClock),
            WindMatrixValid: 1,
            Specular: sub.Specular,
            CameraRight: _leafBillboardRight,
            CameraUp: _leafBillboardUp,
            Wind: sub.SpeedTreeLod?.Component == SpeedTreeLodComponent.Billboard
                ? Vector4.Zero
                : new Vector4(
                    _wind.X,
                    _wind.Y * sub.SpeedTreeWindSpeeds.X,
            _wind.Z,
            _wind.W * sub.SpeedTreeWindSpeeds.Y),
            EffectTint: new Vector4(sub.EffectTint, sub.HasEffectFalloff ? 1f : 0f),
            EffectFalloff: ResolveEffectFalloffConstants(sub),
            EnvMap: ResolveEnvMapState(sub),
            SoftParticle: Vector4.Zero,
            TallGrassWind: BuildTallGrassWindConstants(
                batchState.UsesTallGrassWind,
                batchState.GrassWaveMultiplier),
            SpecularLodBounds: new Vector4(sub.LocalBoundsCenter, sub.LocalBoundsRadius),
            SpecularLodParams: _classicSpecularLodProfile.ShaderParameters(
                specularEligible: sub.Specular.W > 0f));
    }

    private static Vector4 ResolveEffectFalloffConstants(CachedSubmesh12 submesh)
    {
        if (submesh.StarfieldMaterialColor.IsConstantLerp)
        {
            System.Diagnostics.Debug.Assert(
                submesh.StarfieldMaterialPath is not null &&
                !submesh.HasEffectFalloff &&
                !submesh.IsLighting30,
                "Starfield constant Lerp must not alias an active effect/Lighting30 payload.");
            return submesh.StarfieldMaterialColor.LinearTint;
        }

        if (submesh.HasBgsmEmission)
        {
            System.Diagnostics.Debug.Assert(
                submesh.StarfieldMaterialPath is null &&
                !submesh.IsEmissive &&
                !submesh.IsLighting30 &&
                !submesh.HasEffectFalloff &&
                !submesh.SoftParticle.Enabled,
                "Regular BGSM emission must not alias another EffectFalloff union payload.");
            return new Vector4(
                submesh.BgsmEmissionColor,
                submesh.BgsmGlowMap is { } glowMap ? glowMap.BindlessIndex + 1f : 0f);
        }

        return submesh.HasEffectFalloff
            ? submesh.EffectFalloffParams
            : submesh.Lighting30Emission;
    }

    private OpaqueSubmissionPacket12? ResolveOpaqueSubmissionPacket(
        ID3D12GraphicsCommandList cmd,
        IReadOnlyList<OpaqueBatchState> activeBatches,
        bool useCascadePrefixes,
        out StaticOpaquePacketFallbackReason fallbackReason,
        out double buildMilliseconds)
    {
        buildMilliseconds = 0;
        if (!StaticOpaquePacketRequested)
        {
            fallbackReason = StaticOpaquePacketFallbackReason.Disabled;
            return null;
        }

        if (_opaqueIndirectSignature is null)
        {
            fallbackReason = StaticOpaquePacketFallbackReason.SignatureUnavailable;
            return null;
        }
        if (GeometryArenaDiagnostics.Enabled)
        {
            fallbackReason = StaticOpaquePacketFallbackReason.Diagnostics;
            return null;
        }
        if (_frameHeatmapActive)
        {
            fallbackReason = StaticOpaquePacketFallbackReason.Heatmap;
            return null;
        }

        var game = _renderCache?.Game switch
        {
            Core.Games.BethesdaGame.Fallout76 => StaticOpaquePacketGame.Fallout76,
            Core.Games.BethesdaGame.Starfield => StaticOpaquePacketGame.Starfield,
            _ => StaticOpaquePacketGame.Unknown,
        };
        if (game == StaticOpaquePacketGame.Unknown)
        {
            fallbackReason = StaticOpaquePacketFallbackReason.UnsupportedGame;
            return null;
        }

        var frameKey = StaticOpaquePacketReuseKey.Create(
            _publishedBatchIdentity,
            _publishedBatchGeneration,
            _meshCache.EvictionGeneration,
            _frameRenderOrigin,
            _frameRefilterActive,
            in _frameFrustum,
            _frameCylinderX,
            _frameCylinderY,
            _frameCylinderRadius,
            _frameSmallPropCutoffSq,
            _enableDistanceLod,
            _shadowCaptureArmed,
            useCascadePrefixes);
        if (_opaqueSubmissionPacket is { IsDisposed: false } current &&
            StaticOpaquePacketPolicy.CanReuse(current.Key, frameKey))
        {
            fallbackReason = StaticOpaquePacketFallbackReason.None;
            _opaquePacketCandidateKey = null;
            _opaquePacketCandidateFrames = 0;
            return current;
        }

        // Avoid allocating and retiring a committed resource on every moving-camera frame. A
        // candidate must survive one complete frame unchanged; a stationary capture pays one legacy
        // frame, then receives packet hits until an exact dependency changes.
        if (_opaquePacketCandidateKey is not { } candidate || candidate != frameKey)
        {
            _opaquePacketCandidateKey = frameKey;
            _opaquePacketCandidateFrames = 1;
            fallbackReason = StaticOpaquePacketFallbackReason.KeyMismatch;
            return null;
        }

        _opaquePacketCandidateFrames++;
        if (_opaquePacketCandidateFrames < 2)
        {
            fallbackReason = StaticOpaquePacketFallbackReason.KeyMismatch;
            return null;
        }
        var started = Stopwatch.GetTimestamp();
        var inputs = new List<OpaqueSubmissionPacket12.DrawInput>(
            Math.Max(_opaqueBatches.ModernStandardBatchCount, 4));
        for (var submissionIndex = 0; submissionIndex < activeBatches.Count; submissionIndex++)
        {
            var batch = activeBatches[submissionIndex];
            var sub = batch.Submesh;
            var exactInputsCaptured = !_frameRefilterActive ||
                                      batch.InstanceBounds.Count == batch.Instances.Count;
            var environmentTerminal = sub.EnvMap is null ||
                                      sub.EnvMap is { IsReady: true } environment &&
                                      (!environment.IsResident || environment.IsCubemap);
            var facts = new StaticOpaquePacketFacts(
                Requested: StaticOpaquePacketRequested,
                Game: game,
                IsOrdinaryLane: OpaqueIndirectSubmissionPolicy.IsOrdinaryLane(
                    sub.DepthWritingBlend, batch.UsesGrassDistanceEnvelope, sub.IsDecal),
                UsesModernStandardShader: batch.UsesModernStandardShader,
                IsGrass: batch.UsesGrassDistanceEnvelope,
                IsTallGrass: sub.IsTallGrass || batch.UsesTallGrassWind,
                HasSpeedTree: sub.IsSpeedTreeBranch || sub.SpeedTreeLod is not null,
                IsLeaf: sub.IsLeafBillboard,
                HasPhysicsLiteSway: sub.PhysicsLiteSway is not null,
                HasRigidNodeAnimation: sub.RigidNodeAnimation is not null,
                HasSkin: sub.Skin is not null || sub.AnimatedVertexBufferView is not null,
                HasLiveParticles: sub.LiveParticles is not null || sub.ParticleCenters is not null,
                HasUvScroll: sub.UvScrollVelocity != Vector2.Zero,
                DiagnosticsEnabled: GeometryArenaDiagnostics.Enabled,
                HeatmapEnabled: _frameHeatmapActive,
                TexturesTerminal: sub.TexturesReady,
                EnvironmentMapTerminal: environmentTerminal,
                MirrorReplaySupported: true,
                ExactRefilterActive: _frameRefilterActive,
                ExactRefilterInputsCaptured: exactInputsCaptured);
            if (StaticOpaquePacketPolicy.Resolve(in facts) != StaticOpaquePacketFallbackReason.None ||
                !TryBuildOpaquePacketMainMatrices(
                    batch, useCascadePrefixes, out var matrices, out var cascades))
            {
                continue;
            }

            var tailCount = _shadowCaptureArmed
                ? ShadowCascadeSubmissionPolicy.UsefulSourceTailCount(
                    batch.ShadowOnlyInstances.Count,
                    batch.ShadowOnlyCascadePrefix,
                    useCascadePrefixes)
                : 0;
            ReadOnlyMemory<Matrix4x4> tailMatrices = tailCount == 0
                ? default
                : CollectionsMarshal.AsSpan(batch.ShadowOnlyInstances).Slice(0, tailCount).ToArray();
            var tailCascades = useCascadePrefixes
                ? new OpaqueSubmissionPacket12.CascadeCounts(
                    batch.ShadowOnlyCascadePrefix[0],
                    batch.ShadowOnlyCascadePrefix[1],
                    batch.ShadowOnlyCascadePrefix[2],
                    batch.ShadowOnlyCascadePrefix[3])
                : OpaqueSubmissionPacket12.CascadeCounts.Uniform(tailCount);
            var constants = BuildOpaqueInstanceDrawConstants(batch, 0, out _);
            inputs.Add(new OpaqueSubmissionPacket12.DrawInput(
                submissionIndex,
                batch,
                sub,
                batch.Pso,
                matrices,
                constants,
                cascades,
                tailMatrices,
                tailCascades));
        }

        if (inputs.Count == 0)
        {
            buildMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            fallbackReason = StaticOpaquePacketFallbackReason.NoEligibleBatches;
            return null;
        }

        if (!OpaqueSubmissionPacket12.TryCreate(
                _gpu, cmd, _deletionQueue, in frameKey, inputs, out var replacement))
        {
            _opaquePacketCandidateKey = null;
            _opaquePacketCandidateFrames = 0;
            buildMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            fallbackReason = StaticOpaquePacketFallbackReason.BuildFailed;
            return null;
        }

        var prior = _opaqueSubmissionPacket;
        _opaqueSubmissionPacket = replacement;
        if (prior is not null)
        {
            _deletionQueue.EnqueueDispose(prior);
        }
        _opaquePacketCandidateKey = null;
        _opaquePacketCandidateFrames = 0;
        buildMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        fallbackReason = StaticOpaquePacketFallbackReason.None;
        return replacement;
    }

    private bool TryBuildOpaquePacketMainMatrices(
        OpaqueBatchState batch,
        bool useCascadePrefixes,
        out ReadOnlyMemory<Matrix4x4> matrices,
        out OpaqueSubmissionPacket12.CascadeCounts cascades)
    {
        var worlds = CollectionsMarshal.AsSpan(batch.Instances);
        if (worlds.Length == 0 ||
            (_frameRefilterActive && batch.InstanceBounds.Count != worlds.Length))
        {
            matrices = default;
            cascades = default;
            return false;
        }

        if (!_frameRefilterActive)
        {
            matrices = worlds.ToArray();
            cascades = useCascadePrefixes
                ? new OpaqueSubmissionPacket12.CascadeCounts(
                    batch.CascadePrefix[0], batch.CascadePrefix[1],
                    batch.CascadePrefix[2], batch.CascadePrefix[3])
                : OpaqueSubmissionPacket12.CascadeCounts.Uniform(worlds.Length);
            return true;
        }

        var filtered = new Matrix4x4[worlds.Length];
        Span<int> filteredPrefixes = stackalloc int[ShadowMapRenderer12.CascadeCount];
        var bounds = CollectionsMarshal.AsSpan(batch.InstanceBounds);
        var written = 0;
        var cascadeCursor = 0;
        for (var i = 0; i < worlds.Length; i++)
        {
            while (useCascadePrefixes && cascadeCursor < filteredPrefixes.Length &&
                   i >= batch.CascadePrefix[cascadeCursor])
            {
                filteredPrefixes[cascadeCursor++] = written;
            }

            if (!PassesExactCull(bounds[i]))
            {
                continue;
            }
            filtered[written++] = worlds[i];
        }

        while (cascadeCursor < filteredPrefixes.Length)
        {
            filteredPrefixes[cascadeCursor++] = written;
        }
        if (written == 0)
        {
            matrices = default;
            cascades = default;
            return false;
        }

        if (written != filtered.Length)
        {
            Array.Resize(ref filtered, written);
        }
        matrices = filtered;
        cascades = useCascadePrefixes
            ? new OpaqueSubmissionPacket12.CascadeCounts(
                filteredPrefixes[0], filteredPrefixes[1],
                filteredPrefixes[2], filteredPrefixes[3])
            : OpaqueSubmissionPacket12.CascadeCounts.Uniform(written);
        return true;
    }

    private void RetireOpaqueSubmissionPacket()
    {
        if (_opaqueSubmissionPacket is { } packet)
        {
            _opaqueSubmissionPacket = null;
            _deletionQueue.EnqueueDispose(packet);
        }
        _opaquePacketCandidateKey = null;
        _opaquePacketCandidateFrames = 0;
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
        LastStats.ReferenceOpaqueFrontToBackActive = _opaqueBatches.FrontToBackActive;
        LastStats.ReferenceOpaqueFrontToBackBatches = _opaqueBatches.FrontToBackBatchCount;
        LastStats.ReferenceOpaqueFrontToBackInstances = _opaqueBatches.FrontToBackInstanceCount;
        LastStats.ReferenceOpaqueFrontToBackFallbackReason =
            (int)_opaqueBatches.FrontToBackFallbackReason;
        LastStats.ReferenceModernStandardShaderActive = _renderCache?.Game switch
        {
            BethesdaGame.Fallout4 or BethesdaGame.Fallout76 =>
                _pipelines.ModernStandardOpaqueAvailable,
            BethesdaGame.Starfield => _pipelines.StarfieldDiffuseLitOpaqueAvailable,
            _ => false
        };
        LastStats.ReferenceModernStandardBatches = _opaqueBatches.ModernStandardBatchCount;
        LastStats.ReferenceModernStandardInstances = _opaqueBatches.ModernStandardInstanceCount;
        _opaqueSubmissionPsos.Clear();
        // Shadow-only casters are uploaded only when the shadow capture is armed this frame (they
        // ride AFTER each batch's main instances in the same block — see the copy pass below).
        var includeShadow = _shadowCaptureArmed;
        // Prefixes are conservative only inside the fit envelope they were classified for. If the
        // sun/ladder/Z clamp changes before the next batch publication, keep shadows correct by
        // replaying the full copied range for every cascade on those frames.
        var useCascadePrefixes = CascadeFitsCompatible(_publishedBatchSet.CascadeFit, _cascadeFit);
        var packet = ResolveOpaqueSubmissionPacket(
            cmd, activeBatches, useCascadePrefixes, out var packetFallback, out var packetBuildMs);
        GpuRingBuffer12.RingAllocation packetTailAllocation = default;
        ulong packetTailInstanceAddress = 0;
        if (packet is { TailInstanceCount: > 0 })
        {
            var tailBytes = checked((uint)packet.TailInstanceCount * OpaqueSubmissionPacket12.MatrixStride);
            if (!_ringBuffer.TryAllocate(
                    frameIndex,
                    tailBytes + OpaqueSubmissionPacket12.MatrixStride - 1,
                    out packetTailAllocation,
                    alignment: 16))
            {
                // No packet command has been issued and no packet main has been excluded from the
                // census yet, so the complete legacy frame (main + tails) remains available.
                packet = null;
                packetFallback = StaticOpaquePacketFallbackReason.ShadowTailAllocationFailed;
            }
            else
            {
                var tailByteOffset = AlignUp(
                    packetTailAllocation.ByteOffset, OpaqueSubmissionPacket12.MatrixStride);
                var tailCpuPtr = packetTailAllocation.CpuPtr +
                                 (int)(tailByteOffset - packetTailAllocation.ByteOffset);
                unsafe
                {
                    var destination = new Span<Matrix4x4>(
                        (void*)tailCpuPtr, packet.TailInstanceCount);
                    var tailBindingsValid = true;
                    foreach (ref readonly var packetDraw in packet.Draws)
                    {
                        if (packetDraw.TailCount == 0)
                        {
                            continue;
                        }
                        if (packetDraw.TailPerDrawCbAddress == 0 ||
                            (ulong)packetDraw.TailInstanceBase + (uint)packetDraw.TailCount >
                            (uint)packet.TailInstanceCount)
                        {
                            tailBindingsValid = false;
                            break;
                        }
                        packetDraw.ShadowTailMatrices.Span.CopyTo(
                            destination.Slice((int)packetDraw.TailInstanceBase, packetDraw.TailCount));
                    }
                    if (!tailBindingsValid)
                    {
                        packet = null;
                        packetFallback = StaticOpaquePacketFallbackReason.BuildFailed;
                    }
                }
                if (packet is not null)
                {
                    packetTailInstanceAddress = packetTailAllocation.GpuAddress +
                                                (tailByteOffset - packetTailAllocation.ByteOffset);
                }
            }
        }

        var packetBatchCount = packet?.DrawCount ?? 0;
        var packetInstanceCount = packet?.InstanceCount ?? 0;
        var packetRunCount = packet?.RunCount ?? 0;
        LastStats.ReferenceStaticOpaquePacketActive =
            StaticOpaquePacketRequested && _opaqueIndirectSignature is not null;
        LastStats.ReferenceStaticOpaquePacketHit = packet is not null;
        LastStats.ReferenceStaticOpaquePacketBatches = packetBatchCount;
        LastStats.ReferenceStaticOpaquePacketInstances = packetInstanceCount;
        LastStats.ReferenceStaticOpaquePacketRuns = packetRunCount;
        LastStats.ReferenceStaticOpaquePacketBytes = packet is null
            ? 0
            : checked((int)packet.ByteLength);
        LastStats.ReferenceStaticOpaquePacketBuildMilliseconds = packetBuildMs;
        LastStats.ReferenceStaticOpaquePacketSavedMatrixBytes = packet is null
            ? 0
            : checked(LastStats.ReferenceStaticOpaquePacketInstances *
                      (int)OpaqueSubmissionPacket12.MatrixStride);
        LastStats.ReferenceStaticOpaquePacketSavedConstantBytes = packet is null
            ? 0
            : checked((packet.DrawCount + packet.TailDrawCount) * (int)InstanceDrawByteSize);
        LastStats.ReferenceStaticOpaquePacketSavedArgumentBytes = packet is null
            ? 0
            : checked(LastStats.ReferenceStaticOpaquePacketBatches * OpaqueIndirectCommand12.ByteStride);
        LastStats.ReferenceStaticOpaquePacketFallbackReason = (int)packetFallback;
        var totalInstances = 0;
        var activeBatchCount = 0;
        var activeMainDrawCount = 0;
        var ordinaryMainDrawCapacity = 0;
        var packetCensusDrawCursor = 0;
        for (var submissionIndex = 0; submissionIndex < activeBatches.Count; submissionIndex++)
        {
            var batchState = activeBatches[submissionIndex];
            var packetized = packet is not null && packet.TryTakeDraw(
                submissionIndex, batchState, ref packetCensusDrawCursor, out _);
            var sourceInstanceCount = batchState.Instances.Count;
            if (sourceInstanceCount != 0)
            {
                // One potential main-scene draw before this frame's exact instance filters run.
                activeMainDrawCount++;
            }

            // Packet inputs are validated non-empty at construction. Their persistent main and
            // optional tail never enter the transient census, so avoid even inspecting tail state.
            if (packetized)
            {
                activeBatchCount++;
                continue;
            }

            var submesh = batchState.Submesh;
            if (sourceInstanceCount != 0 && OpaqueIndirectSubmissionPolicy.IsOrdinaryLane(
                    submesh.DepthWritingBlend,
                    batchState.UsesGrassDistanceEnvelope,
                    submesh.IsDecal))
            {
                ordinaryMainDrawCapacity++;
            }
            var usefulShadowSourceCount = includeShadow
                ? ShadowCascadeSubmissionPolicy.UsefulSourceTailCount(
                    batchState.ShadowOnlyInstances.Count,
                    batchState.ShadowOnlyCascadePrefix,
                    useCascadePrefixes)
                : 0;
            var count = sourceInstanceCount + usefulShadowSourceCount;
            if (count == 0) continue;
            totalInstances += count;
            activeBatchCount++;
        }
        LastStats.ReferenceOpaqueActiveDraws = activeMainDrawCount;
        if (totalInstances == 0 && packet is null) return;

        var instanceStride = (uint)Marshal.SizeOf<Matrix4x4>();
        var instanceBytes = instanceStride * (uint)totalInstances;
        // Instance-SRV GPU address currently bound at t8 — captured per shadow draw so the shadow
        // replay can rebind exactly what each draw's uInstanceBase indexes into (the shared block,
        // or a per-batch fallback block).
        ulong boundInstanceAddress = 0;
        // Actual t8 state on the command list. Packet runs temporarily replace the legacy shared
        // binding, so the next mixed/dynamic segment must explicitly restore it.
        ulong commandListInstanceAddress = 0;
        // Fast path: one contiguous block holds every batch's world matrices (single bulk memcpy +
        // one SRV bind, per-batch draws index into it via StartInstance). When a dense frame can't
        // fit that single allocation, fall back to a per-batch block inside the loop below — the
        // frame degrades to skipping only the batches that no longer fit instead of dropping the
        // ENTIRE opaque pass (which read as "the whole city un-renders" on dense downtown frames).
        GpuRingBuffer12.RingAllocation instanceAlloc = default;
        var haveSharedBlock = totalInstances > 0 && _ringBuffer.TryAllocate(
            frameIndex, instanceBytes + instanceStride - 1, out instanceAlloc, alignment: 16);
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
                var packetCopyDrawCursor = 0;
                for (var submissionIndex = 0; submissionIndex < activeBatches.Count; submissionIndex++)
                {
                    var batchState = activeBatches[submissionIndex];
                    if (packet is not null && packet.TryTakeDraw(
                            submissionIndex, batchState, ref packetCopyDrawCursor, out _))
                    {
                        continue;
                    }
                    var worlds = batchState.Instances;
                    var shadowWorlds = includeShadow ? batchState.ShadowOnlyInstances : null;
                    var shadowCount = shadowWorlds is null
                        ? 0
                        : ShadowCascadeSubmissionPolicy.UsefulSourceTailCount(
                            shadowWorlds.Count,
                            batchState.ShadowOnlyCascadePrefix,
                            useCascadePrefixes);
                    var filterSpeedTreeLod = batchState.Submesh.SpeedTreeLod is not null;
                    var filterMainGrassDistance = batchState.UsesGrassDistanceEnvelope;
                    var exactMainPerInstance = GrassDistanceCullPolicy.RequiresExactPerInstanceFiltering(
                        refilter,
                        filterMainGrassDistance);
                    var animatePhysicsLite = AnimationsEnabled &&
                                             batchState.Submesh.PhysicsLiteSway is not null &&
                                             batchState.PhysicsLiteSeeds.Count == worlds.Count;
                    // Rigid node-animated statics (saloon sign): one shared delta per frame, no
                    // per-reference seed, applied to every copied instance world.
                    var animateRigidNode = AnimationsEnabled &&
                                           batchState.Submesh.RigidNodeAnimation is not null;
                    // Forces the per-instance branch while the heatmap is on (same trick as
                    // animatePhysicsLite) so every copied matrix can take its tint in the w-lanes.
                    var applyHeatmap = _frameHeatmapActive &&
                                       batchState.HeatmapFormIds.Count == worlds.Count;
                    if (worlds.Count == 0 && shadowCount == 0)
                    {
                        batchState.FrameDrawCount = 0;
                        batchState.FrameShadowOnlyCount = 0;
                        continue;
                    }
                    if (!exactMainPerInstance && !filterSpeedTreeLod && !animatePhysicsLite &&
                        !animateRigidNode && !applyHeatmap)
                    {
                        CollectionsMarshal.AsSpan(worlds).CopyTo(span.Slice(offset, worlds.Count));
                        offset += worlds.Count;
                        batchState.FrameDrawCount = worlds.Count;
                        // Nothing was dropped, so the build-time prefixes still describe the block.
                        if (useCascadePrefixes)
                        {
                            // Exactly four cascades. Array.Copy's general type/rank/bounds machinery
                            // dominated this dense steady-state loop even though every copy is only
                            // sixteen bytes (19k+ calls/frame in the FO76 profile).
                            var source = batchState.CascadePrefix;
                            var destination = batchState.FrameCascadeCount;
                            destination[0] = source[0];
                            destination[1] = source[1];
                            destination[2] = source[2];
                            destination[3] = source[3];
                        }
                        else
                        {
                            batchState.FrameCascadeCount.AsSpan().Fill(worlds.Count);
                        }
                    }
                    else
                    {
                        var worldSpan = CollectionsMarshal.AsSpan(worlds);
                        var boundsSpan = CollectionsMarshal.AsSpan(batchState.InstanceBounds);
                        var physicsSeeds = CollectionsMarshal.AsSpan(batchState.PhysicsLiteSeeds);
                        var heatmapIds = applyHeatmap
                            ? CollectionsMarshal.AsSpan(batchState.HeatmapFormIds)
                            : default;
                        var batchStart = offset;
                        // Instances are in cascade order, so the prefixes only need re-deriving against
                        // what SURVIVES the filters: as each source index crosses a build-time prefix
                        // boundary, that cascade's frame count is the number written so far.
                        var cascadeCursor = 0;
                        for (var i = 0; i < worldSpan.Length; i++)
                        {
                            while (useCascadePrefixes &&
                                   cascadeCursor < batchState.FrameCascadeCount.Length &&
                                   i >= batchState.CascadePrefix[cascadeCursor])
                            {
                                batchState.FrameCascadeCount[cascadeCursor++] = offset - batchStart;
                            }

                            if (filterMainGrassDistance && !PassesExactGrassDistance(
                                    new Vector3(boundsSpan[i].X, boundsSpan[i].Y, boundsSpan[i].Z),
                                    isGrass: true)) continue;
                            if (refilter && !PassesExactCull(boundsSpan[i])) continue;
                            if (filterSpeedTreeLod &&
                                !PassesSpeedTreeLod(batchState.Submesh, worldSpan[i], boundsSpan[i])) continue;
                            var world = animatePhysicsLite
                                ? ApplyPhysicsLiteSway(batchState.Submesh, physicsSeeds[i], worldSpan[i])
                                : worldSpan[i];
                            if (animateRigidNode)
                            {
                                world = ApplyRigidNodeAnimation(batchState.Submesh, world);
                            }
                            if (applyHeatmap)
                            {
                                // InstanceBounds xy = the instance's ABSOLUTE world center — exactly
                                // what the range test wants (the copied matrix is origin-relative).
                                ApplyFormIdHeatmapTint(
                                    ref world, heatmapIds[i], boundsSpan[i].X, boundsSpan[i].Y);
                            }
                            span[offset++] = world;
                        }

                        batchState.FrameDrawCount = offset - batchStart;
                        // Any cascade whose boundary was never crossed spans everything written.
                        while (cascadeCursor < batchState.FrameCascadeCount.Length)
                        {
                            batchState.FrameCascadeCount[cascadeCursor++] = offset - batchStart;
                        }
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
                                                       batchState.ShadowOnlyPhysicsLiteSeeds.Count == shadowWorlds!.Count;
                        if (!filterSpeedTreeLod && !animateShadowPhysicsLite && !animateRigidNode &&
                            !filterGrassDistance)
                        {
                            CollectionsMarshal.AsSpan(shadowWorlds!).Slice(0, shadowCount)
                                .CopyTo(span.Slice(offset, shadowCount));
                            offset += shadowCount;
                            if (useCascadePrefixes)
                            {
                                var source = batchState.ShadowOnlyCascadePrefix;
                                var destination = batchState.FrameShadowOnlyCascadeCount;
                                destination[0] = source[0];
                                destination[1] = source[1];
                                destination[2] = source[2];
                                destination[3] = source[3];
                            }
                            else
                            {
                                batchState.FrameShadowOnlyCascadeCount.AsSpan().Fill(shadowCount);
                            }
                        }
                        else
                        {
                            var shadowStart = offset;
                            var shadowSpan = CollectionsMarshal.AsSpan(shadowWorlds!).Slice(0, shadowCount);
                            var shadowPhysicsSeeds =
                                CollectionsMarshal.AsSpan(batchState.ShadowOnlyPhysicsLiteSeeds);
                            var cascadeCursor = 0;
                            for (var i = 0; i < shadowSpan.Length; i++)
                            {
                                while (useCascadePrefixes &&
                                       cascadeCursor < batchState.FrameShadowOnlyCascadeCount.Length &&
                                       i >= batchState.ShadowOnlyCascadePrefix[cascadeCursor])
                                {
                                    batchState.FrameShadowOnlyCascadeCount[cascadeCursor++] =
                                        offset - shadowStart;
                                }

                                if (filterGrassDistance && !PassesExactGrassDistance(
                                        shadowSpan[i].Translation + _frameRenderOrigin,
                                        isGrass: true)) continue;
                                if (!PassesSpeedTreeLod(batchState.Submesh, shadowSpan[i])) continue;
                                var shadowWorld = animateShadowPhysicsLite
                                    ? ApplyPhysicsLiteSway(
                                        batchState.Submesh, shadowPhysicsSeeds[i], shadowSpan[i])
                                    : shadowSpan[i];
                                if (animateRigidNode)
                                {
                                    shadowWorld = ApplyRigidNodeAnimation(batchState.Submesh, shadowWorld);
                                }
                                span[offset++] = shadowWorld;
                            }

                            shadowCount = offset - shadowStart;
                            while (cascadeCursor < batchState.FrameShadowOnlyCascadeCount.Length)
                            {
                                batchState.FrameShadowOnlyCascadeCount[cascadeCursor++] = shadowCount;
                            }
                        }
                    }
                    batchState.FrameShadowOnlyCount = shadowCount;
                }
            }

            var instanceGpuAddress = instanceAlloc.GpuAddress + (instanceByteOffset - instanceAlloc.ByteOffset);
            BindReferenceInstanceBuffer(cmd, instanceGpuAddress, ref srvBinds, ref srvBindMs);
            boundInstanceAddress = instanceGpuAddress;
            commandListInstanceAddress = instanceGpuAddress;
        }

        GpuRingBuffer12.RingAllocation indirectArgumentAllocation = default;
        var indirectArgumentBytes = checked((uint)(ordinaryMainDrawCapacity * OpaqueIndirectCommand12.ByteStride));
        var remainingRingBytes = (ulong)(_ringBuffer.AllocationLimitBytes - _ringBuffer.CurrentFrameBytes);
        // Preserve the opaque loop's existing soft-failure behavior: only spend argument bytes when
        // every active batch's main CB and worst-case shadow-tail CB still have room afterward.
        var opaqueCbReserve = ((ulong)activeBatchCount * InstanceDrawByteSize * (includeShadow ? 2UL : 1UL))
                              + GpuRingBuffer12.CbAlignment - 1;
        var indirectFallback = OpaqueIndirectSubmissionPolicy.ResolvePreallocationFallback(
            OpaqueIndirectRequested,
            _opaqueIndirectSignature is not null,
            ordinaryMainDrawCapacity,
            haveSharedBlock,
            GeometryArenaDiagnostics.Enabled,
            remainingRingBytes,
            (ulong)indirectArgumentBytes + opaqueCbReserve);
        var useOpaqueIndirect = indirectFallback == OpaqueIndirectFallbackReason.None;
        if (useOpaqueIndirect && !_ringBuffer.TryAllocate(
                frameIndex,
                indirectArgumentBytes,
                out indirectArgumentAllocation,
                alignment: 4))
        {
            useOpaqueIndirect = false;
            indirectFallback = OpaqueIndirectFallbackReason.ArgumentAllocationFailed;
        }

        // A packet-only ordinary lane has no transient argument allocation to attempt. Report the
        // successful persistent ExecuteIndirect route, rather than a misleading Disabled or
        // NoOrdinaryDraws fallback from the transient lane's preallocation policy.
        var reportedIndirectFallback = packet is not null && ordinaryMainDrawCapacity == 0
            ? OpaqueIndirectFallbackReason.None
            : indirectFallback;

        LastStats.ReferenceOpaqueIndirectActive = useOpaqueIndirect || packet is not null;
        LastStats.ReferenceOpaqueIndirectArgumentBytes = useOpaqueIndirect ? (int)indirectArgumentBytes : 0;
        LastStats.ReferenceOpaqueIndirectFallbackReason = (int)reportedIndirectFallback;

        var startInstance = 0u;
        ID3D12PipelineState? previousOpaquePso = null;
        ID3D12PipelineState? pendingIndirectPso = null;
        var pendingIndirectStart = 0;
        var pendingIndirectCount = 0;
        var indirectWriteCount = 0;
        var packetSubmissionDrawCursor = 0;
        var packetSubmissionRunCursor = 0;
        for (var submissionIndex = 0; submissionIndex < activeBatches.Count; submissionIndex++)
        {
            var batchState = activeBatches[submissionIndex];
            if (packet is not null && packet.TryTakeDraw(
                    submissionIndex,
                    batchState,
                    ref packetSubmissionDrawCursor,
                    out var packetDrawIndex))
            {
                ref readonly var packetDraw = ref packet.DrawAt(packetDrawIndex);
                var packetSub = packetDraw.Submesh;
                var packetDrawCount = packetDraw.InstanceCount;

                if (packet.TryTakeRun(
                        submissionIndex,
                        ref packetSubmissionRunCursor,
                        out var packetRunIndex))
                {
                    ref readonly var packetRun = ref packet.RunAt(packetRunIndex);
                    // Every draw in the run is consecutive and uses this PSO. Aggregate counters
                    // and the unique-PSO probe once without changing the surrounding mixed order.
                    LastStats.ReferenceOpaqueSurvivingDraws += packetRun.DrawCount;
                    LastStats.ReferenceOpaqueOrdinaryDraws += packetRun.DrawCount;
                    _opaqueSubmissionPsos.Add(packetRun.Pso);
                    if (previousOpaquePso is not null &&
                        !ReferenceEquals(previousOpaquePso, packetRun.Pso))
                    {
                        LastStats.ReferenceOpaquePsoTransitions++;
                    }
                    previousOpaquePso = packetRun.Pso;
                    submeshDraws += packetRun.DrawCount;
                    LastStats.ReferenceInstancedDraws += packetRun.DrawCount;
                    LastStats.ReferenceInstances += packetRun.InstanceCount;

                    // A transient indirect run owns a different argument resource and may depend on
                    // the legacy shared t8 binding. Drain it before the immutable packet changes t8.
                    FlushOpaqueIndirectRun(
                        cmd,
                        frameIndex,
                        indirectArgumentAllocation,
                        pendingIndirectStart,
                        ref pendingIndirectCount,
                        ref pendingIndirectPso,
                        ref currentPso,
                        ref drawCallMs);
                    if (commandListInstanceAddress != packet.InstanceSrvAddress)
                    {
                        BindReferenceInstanceBuffer(
                            cmd, packet.InstanceSrvAddress, ref srvBinds, ref srvBindMs);
                        commandListInstanceAddress = packet.InstanceSrvAddress;
                    }

                    var packetStarted = StartTiming();
                    if (!ReferenceEquals(currentPso, packetRun.Pso))
                    {
                        cmd.SetPipelineState(packetRun.Pso);
                        currentPso = packetRun.Pso;
                    }
                    cmd.ExecuteIndirect(
                        _opaqueIndirectSignature!,
                        (uint)packetRun.DrawCount,
                        packet.ArgumentBuffer,
                        packetRun.ArgumentBufferOffset,
                        countBuffer: null,
                        countBufferOffset: 0);
                    drawCallMs += ElapsedMilliseconds(packetStarted);
                    LastStats.ReferenceOpaqueIndirectDraws += packetRun.DrawCount;
                    LastStats.ReferenceOpaqueIndirectExecuteCalls++;
                }

                if (_mirrorCaptureArmed)
                {
                    _mirrorDraws.Add(new MirrorDraw(
                        packetDraw.VertexBufferView,
                        packetDraw.IndexBufferView,
                        packetDraw.IndexCount,
                        packetDraw.PerDrawCbAddress,
                        packet.InstanceSrvAddress,
                        packetDrawCount,
                        packetDraw.Pso));
                }

                if (_shadowCaptureArmed)
                {
                    var packetMainCascades = new CascadeCounts(
                        packetDraw.Cascades.C0,
                        packetDraw.Cascades.C1,
                        packetDraw.Cascades.C2,
                        packetDraw.Cascades.C3);
                    if (ShadowCascadeSubmissionPolicy.HasAnyInstances(
                            packetDrawCount,
                            packetMainCascades.C0,
                            packetMainCascades.C1,
                            packetMainCascades.C2,
                            packetMainCascades.C3))
                    {
                        _shadowDraws.Add(new ShadowDraw(
                            packetDraw.VertexBufferView,
                            packetDraw.IndexBufferView,
                            packetDraw.IndexCount,
                            packetDraw.PerDrawCbAddress,
                            packet.InstanceSrvAddress,
                            packetDrawCount,
                            packetSub.AlphaTest,
                            packetMainCascades,
                            UsesTallGrassWind: false));
                    }

                    if (packetDraw.TailCount > 0)
                    {
                        var tailCascades = new CascadeCounts(
                            packetDraw.TailCascades.C0,
                            packetDraw.TailCascades.C1,
                            packetDraw.TailCascades.C2,
                            packetDraw.TailCascades.C3);
                        _shadowDraws.Add(new ShadowDraw(
                            packetDraw.VertexBufferView,
                            packetDraw.IndexBufferView,
                            packetDraw.IndexCount,
                            packetDraw.TailPerDrawCbAddress,
                            packetTailInstanceAddress,
                            packetDraw.TailCount,
                            packetSub.AlphaTest,
                            tailCascades,
                            UsesTallGrassWind: false));
                    }
                }

                continue;
            }

            var batch = batchState.Instances;
            var shadowOnly = includeShadow ? batchState.ShadowOnlyInstances : null;
            var usefulShadowSourceCount = shadowOnly is null
                ? 0
                : ShadowCascadeSubmissionPolicy.UsefulSourceTailCount(
                    shadowOnly.Count,
                    batchState.ShadowOnlyCascadePrefix,
                    useCascadePrefixes);
            if (batch.Count == 0 && usefulShadowSourceCount == 0) continue;

            var drawStartInstance = startInstance;
            var drawInstanceCpuBase = sharedInstanceCpuBase;
            int drawCount;
            int shadowCount;
            if (!haveSharedBlock)
            {
                // Per-batch fallback block: small (count × 64 B), so it fits where the frame-wide
                // block could not. Rebind the instance SRV at this batch's base and draw from 0.
                var fallbackShadowCount = usefulShadowSourceCount;
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
                var animateRigidNode = AnimationsEnabled &&
                                       batchState.Submesh.RigidNodeAnimation is not null;
                // Same per-instance forcing as the shared block: the tint rides the copied matrix.
                var applyHeatmap = _frameHeatmapActive &&
                                   batchState.HeatmapFormIds.Count == batch.Count;
                unsafe
                {
                    var span = new Span<Matrix4x4>((void*)batchCpuPtr, batch.Count + fallbackShadowCount);
                    if (!exactMainPerInstance && !filterSpeedTreeLod && !animatePhysicsLite &&
                        !animateRigidNode && !applyHeatmap)
                    {
                        CollectionsMarshal.AsSpan(batch).CopyTo(span);
                        drawCount = batch.Count;
                    }
                    else
                    {
                        var worldSpan = CollectionsMarshal.AsSpan(batch);
                        var boundsSpan = CollectionsMarshal.AsSpan(batchState.InstanceBounds);
                        var physicsSeeds = CollectionsMarshal.AsSpan(batchState.PhysicsLiteSeeds);
                        var heatmapIds = applyHeatmap
                            ? CollectionsMarshal.AsSpan(batchState.HeatmapFormIds)
                            : default;
                        drawCount = 0;
                        for (var i = 0; i < worldSpan.Length; i++)
                        {
                            if (filterMainGrassDistance && !PassesExactGrassDistance(
                                    new Vector3(boundsSpan[i].X, boundsSpan[i].Y, boundsSpan[i].Z),
                                    isGrass: true)) continue;
                            if (refilter && !PassesExactCull(boundsSpan[i])) continue;
                            if (filterSpeedTreeLod &&
                                !PassesSpeedTreeLod(batchState.Submesh, worldSpan[i], boundsSpan[i])) continue;
                            var world = animatePhysicsLite
                                ? ApplyPhysicsLiteSway(batchState.Submesh, physicsSeeds[i], worldSpan[i])
                                : worldSpan[i];
                            if (animateRigidNode)
                            {
                                world = ApplyRigidNodeAnimation(batchState.Submesh, world);
                            }
                            if (applyHeatmap)
                            {
                                ApplyFormIdHeatmapTint(
                                    ref world, heatmapIds[i], boundsSpan[i].X, boundsSpan[i].Y);
                            }
                            span[drawCount++] = world;
                        }
                    }

                    // Shadow-only casters after the main instances (same contract as the shared block).
                    if (fallbackShadowCount > 0)
                    {
                        var filterGrassDistance = batchState.UsesGrassDistanceEnvelope;
                        var animateShadowPhysicsLite = AnimationsEnabled &&
                                                       batchState.Submesh.PhysicsLiteSway is not null &&
                                                       batchState.ShadowOnlyPhysicsLiteSeeds.Count == shadowOnly!.Count;
                        if (!filterSpeedTreeLod && !animateShadowPhysicsLite && !animateRigidNode &&
                            !filterGrassDistance)
                        {
                            CollectionsMarshal.AsSpan(shadowOnly!).Slice(0, fallbackShadowCount)
                                .CopyTo(span.Slice(drawCount, fallbackShadowCount));
                            shadowCount = fallbackShadowCount;
                        }
                        else
                        {
                            shadowCount = 0;
                            var shadowSpan = CollectionsMarshal.AsSpan(shadowOnly!).Slice(0, fallbackShadowCount);
                            var shadowPhysicsSeeds =
                                CollectionsMarshal.AsSpan(batchState.ShadowOnlyPhysicsLiteSeeds);
                            for (var i = 0; i < shadowSpan.Length; i++)
                            {
                                if (filterGrassDistance && !PassesExactGrassDistance(
                                        shadowSpan[i].Translation + _frameRenderOrigin,
                                        isGrass: true)) continue;
                                if (!PassesSpeedTreeLod(batchState.Submesh, shadowSpan[i])) continue;
                                var shadowWorld = animateShadowPhysicsLite
                                    ? ApplyPhysicsLiteSway(
                                        batchState.Submesh, shadowPhysicsSeeds[i], shadowSpan[i])
                                    : shadowSpan[i];
                                if (animateRigidNode)
                                {
                                    shadowWorld = ApplyRigidNodeAnimation(batchState.Submesh, shadowWorld);
                                }
                                span[drawCount + shadowCount++] = shadowWorld;
                            }
                        }
                    }
                    else
                    {
                        shadowCount = 0;
                    }
                }
                // The fallback compacts each batch into its own ring allocation and historically
                // left these frame-prefix arrays carrying stale values from the prior shared-block
                // frame. Uniform counts are conservative and correct for this already-degraded path.
                batchState.FrameCascadeCount.AsSpan().Fill(drawCount);
                batchState.FrameShadowOnlyCascadeCount.AsSpan().Fill(shadowCount);
                if (drawCount + shadowCount == 0) continue;

                var batchInstanceAddress = batchAlloc.GpuAddress + (batchByteOffset - batchAlloc.ByteOffset);
                BindReferenceInstanceBuffer(cmd, batchInstanceAddress, ref srvBinds, ref srvBindMs);
                boundInstanceAddress = batchInstanceAddress;
                commandListInstanceAddress = batchInstanceAddress;
                drawStartInstance = 0;
                drawInstanceCpuBase = batchCpuPtr;
            }
            else
            {
                drawCount = batchState.FrameDrawCount;
                shadowCount = batchState.FrameShadowOnlyCount;
                if (drawCount + shadowCount == 0) continue;
            }

            var sub = batchState.Submesh;
            // Retail FO3/FNV TallGrass and TES4's instanced blend-grass route have no matching
            // depth/caster permutation. The batch-level grass marker is not game-specific, hence
            // the separate route gates (Skyrim and ordinary FO3/FNV grass must remain untouched).
            var fnvGrassNeverCasts = _tallGrassWindSupported && sub.IsTallGrass;
            var grassNeverCasts = fnvGrassNeverCasts
                                  || (_instancedBlendGrassSupported && batchState.UsesGrassDistanceEnvelope);
            // Video-settings "Tree canopy shadows" is a caster-only gate; color stays visible.
            var treeNeverCasts = !_treeShadowsEnabled &&
                                 (sub.IsSpeedTreeBranch || sub.IsLeafBillboard || sub.SpeedTreeLod is not null);
            var tailHasCascadeInstances =
                ShadowCascadeSubmissionPolicy.HasAnyInstances(
                    shadowCount, batchState.FrameShadowOnlyCascadeCount);
            var shadowCasterEligible = _shadowCaptureArmed &&
                                       !sub.IsDecal && !grassNeverCasts && !treeNeverCasts;
            var canCaptureShadowTail = shadowCasterEligible && tailHasCascadeInstances;
            if (drawCount == 0 && !canCaptureShadowTail)
            {
                // No color command and no addressable shadow instance: do not manufacture main-pass
                // PSO/root/IA work for a batch that cannot submit anything. The copied span still
                // owns this range, so preserve the next batch's instance base.
                startInstance += (uint)shadowCount;
                continue;
            }

            if (drawCount > 0 && commandListInstanceAddress != boundInstanceAddress)
            {
                BindReferenceInstanceBuffer(
                    cmd, boundInstanceAddress, ref srvBinds, ref srvBindMs);
                commandListInstanceAddress = boundInstanceAddress;
            }

            var ordinaryLane = OpaqueIndirectSubmissionPolicy.IsOrdinaryLane(
                sub.DepthWritingBlend,
                batchState.UsesGrassDistanceEnvelope,
                sub.IsDecal);
            if (drawCount > 0)
            {
                // These are post-filter main-scene commands, before the opt-in geometry validator.
                // They are the commands an ExecuteIndirect/grouping path would need to represent.
                LastStats.ReferenceOpaqueSurvivingDraws++;
                _opaqueSubmissionPsos.Add(batchState.Pso);
                if (previousOpaquePso is not null &&
                    !ReferenceEquals(previousOpaquePso, batchState.Pso))
                {
                    LastStats.ReferenceOpaquePsoTransitions++;
                }
                previousOpaquePso = batchState.Pso;

                // Mutually-exclusive order lanes. Ordinary opaque/cutout draws can be PSO-grouped;
                // decals retain their overlay order; depth-writing blended grass retains the
                // grass-last correctness boundary. Cutout grass is separated because the registry
                // deliberately keeps it in that same late lane for early-Z efficiency.
                var submesh = batchState.Submesh;
                if (submesh.DepthWritingBlend)
                {
                    // A blended shape reaches this opaque-instanced path only through the explicit
                    // instanced-grass admission above. Classify it even if its distance envelope is
                    // disabled, since DepthWritingBlend is the correctness-relevant order boundary.
                    LastStats.ReferenceOpaqueGrassDepthWriteDraws++;
                }
                else if (batchState.UsesGrassDistanceEnvelope)
                {
                    LastStats.ReferenceOpaqueGrassCutoutDraws++;
                }
                else if (submesh.IsDecal)
                {
                    LastStats.ReferenceOpaqueDecalDraws++;
                }
                else if (ordinaryLane)
                {
                    LastStats.ReferenceOpaqueOrdinaryDraws++;
                }
            }

            var submitIndirect = useOpaqueIndirect && drawCount > 0 && ordinaryLane;
            if (pendingIndirectCount > 0 && drawCount > 0 &&
                (!submitIndirect || OpaqueIndirectSubmissionPolicy.BeginsNewRun(
                    pendingIndirectCount,
                    pendingIndirectPso,
                    batchState.Pso)))
            {
                FlushOpaqueIndirectRun(
                    cmd,
                    frameIndex,
                    indirectArgumentAllocation,
                    pendingIndirectStart,
                    ref pendingIndirectCount,
                    ref pendingIndirectPso,
                    ref currentPso,
                    ref drawCallMs);
            }

            if (!submitIndirect && drawCount > 0 && !ReferenceEquals(currentPso, batchState.Pso))
            {
                cmd.SetPipelineState(batchState.Pso);
                currentPso = batchState.Pso;
            }

            // Per-batch material/texture state pulled from the submesh (the batch key) — these
            // are identical for every instance in the batch, so they live here, not per instance.
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
            var instanceDraw = BuildOpaqueInstanceDrawConstants(
                batchState, drawStartInstance, out var textureState);
            if (!_ringBuffer.TryAllocate(frameIndex, InstanceDrawByteSize, out var instanceDrawAlloc, GpuRingBuffer12.CbAlignment))
            {
                LastFrameDrawsTruncated += batch.Count;
                break;
            }
            unsafe { *(InstanceDrawConstants*)instanceDrawAlloc.CpuPtr = instanceDraw; }
            if (drawCount > 0 && !submitIndirect)
            {
                cmd.SetGraphicsRootConstantBufferView(
                    GpuRootSignature12.Slots.PerDrawCbv, instanceDrawAlloc.GpuAddress);
            }
            // FALLOUT_VIEWER_GEOMETRY_VALIDATE: prove the geometry views and compacted instance
            // matrices this draw will read are sane. A violation only suppresses the cmd.* calls
            // (under _SKIP_DRAW) — the startInstance accounting below MUST advance regardless,
            // because every batch's instances were already compacted at fixed offsets.
            var drawLiveness = !GeometryArenaDiagnostics.Enabled ||
                               GeometryDrawValidator12.ValidateOpaqueDraw(
                                   batchState, _meshCache, drawInstanceCpuBase, drawStartInstance,
                                   drawCount, shadowCount, _framesSinceBuild, _lastBuildEvictionGen,
                                   _frameReusedBatches);

            if (drawCount > 0)
            {
                if (submitIndirect)
                {
                    if (drawLiveness)
                    {
                        if (pendingIndirectCount == 0)
                        {
                            pendingIndirectStart = indirectWriteCount;
                            pendingIndirectPso = batchState.Pso;
                        }

                        var command = new OpaqueIndirectCommand12
                        {
                            PerDrawCbAddress = instanceDrawAlloc.GpuAddress,
                            VertexBufferView = sub.EffectiveVertexBufferView,
                            IndexBufferView = sub.IndexBufferView,
                            Draw = new OpaqueIndirectDrawIndexedArguments
                            {
                                IndexCountPerInstance = (uint)sub.IndexCount,
                                InstanceCount = (uint)drawCount,
                                StartIndexLocation = 0,
                                BaseVertexLocation = 0,
                                StartInstanceLocation = 0,
                            },
                        };
                        unsafe
                        {
                            var destination = (byte*)indirectArgumentAllocation.CpuPtr
                                              + (indirectWriteCount * OpaqueIndirectCommand12.ByteStride);
                            *(OpaqueIndirectCommand12*)destination = command;
                        }

                        indirectWriteCount++;
                        pendingIndirectCount++;
                        ObserveFnvActiveAdtBaseDraw(sub, textureState, drawCount);
                    }
                }
                else
                {
                    cmd.IASetVertexBuffers(0, batchState.Submesh.EffectiveVertexBufferView);
                    cmd.IASetIndexBuffer(batchState.Submesh.IndexBufferView);
                    if (drawLiveness)
                    {
                        // The main scene draw excludes the shadow-only tail of the instance range.
                        cmd.DrawIndexedInstanced((uint)batchState.Submesh.IndexCount, (uint)drawCount, 0, 0, 0);
                        LastStats.ReferenceOpaqueDirectDraws++;
                        ObserveFnvActiveAdtBaseDraw(sub, textureState, drawCount);
                        if (batchState.UsesTallGrassWind)
                        {
                            LastStats.ReferenceTallGrassInstancedDraws++;
                            LastStats.ReferenceTallGrassInstancedInstances += drawCount;
                            ObserveTallGrassWaveMultiplier(batchState.GrassWaveMultiplier);
                        }
                    }
                }
            }
            // Water-reflection mirror capture: MAIN-frustum draws only (never the shadow-only
            // tail — the mirror wants the visible set), original PSO + the per-draw CB / t8
            // window just bound. Decals are coplanar overlays that need their own biased mirror
            // PSO (excluded v1); grass reuses the caster classification — retail's default
            // reflection is land + sky (bUseWaterReflectionsMisc/Statics/Trees ship 0) and no
            // grass ini exists at all.
            if (_mirrorCaptureArmed && drawCount > 0 && drawLiveness &&
                !sub.IsDecal && !grassNeverCasts)
            {
                _mirrorDraws.Add(new MirrorDraw(
                    sub.EffectiveVertexBufferView, sub.IndexBufferView, sub.IndexCount,
                    instanceDrawAlloc.GpuAddress, boundInstanceAddress, drawCount, batchState.Pso));
            }
            var mainCascades = CascadeCounts.From(batchState.FrameCascadeCount, drawCount);
            var shadowTailCascades =
                CascadeCounts.From(batchState.FrameShadowOnlyCascadeCount, shadowCount);
            var mainHasCascadeInstances = ShadowCascadeSubmissionPolicy.HasAnyInstances(
                drawCount,
                mainCascades.C0,
                mainCascades.C1,
                mainCascades.C2,
                mainCascades.C3);
            if (shadowCasterEligible && drawLiveness &&
                (mainHasCascadeInstances || tailHasCascadeInstances))
            {
                // Record this draw for the frame-end shadow replay: the ring-buffer CB just bound
                // (uInstanceBase et al.) + the t8 instance block it indexes stay valid until the
                // frame's allocations are recycled, i.e. exactly the replay window. The replay's
                // instance count INCLUDES the shadow-only casters appended after the main range.
                // The shadow-only casters sit AFTER the main range, so a cascade's set spans two
                // disjoint sub-ranges — which the replay cannot express, since it may only truncate a
                // draw's instance count from the tail (the start offset lives in the immutable CB just
                // bound). Emit the tail as its own draw with its own instance base so each range stays
                // a clean prefix. Its CB is allocated HERE, inside the main pass, so the shadow pass's
                // protected ring reservation is unaffected.
                var shadowTailCb = 0UL;
                if (tailHasCascadeInstances &&
                    _ringBuffer.TryAllocate(
                        frameIndex, InstanceDrawByteSize, out var shadowTailAlloc,
                        GpuRingBuffer12.CbAlignment))
                {
                    unsafe
                    {
                        *(InstanceDrawConstants*)shadowTailAlloc.CpuPtr =
                            instanceDraw with { InstanceBase = drawStartInstance + (uint)drawCount };
                    }

                    shadowTailCb = shadowTailAlloc.GpuAddress;
                }

                if (tailHasCascadeInstances && shadowTailCb == 0UL)
                {
                    // No room for the tail's own CB: fall back to one unculled draw covering both
                    // ranges. Correct, just not cascade-culled for this batch this frame.
                    _shadowDraws.Add(new ShadowDraw(
                        sub.EffectiveVertexBufferView, sub.IndexBufferView, sub.IndexCount,
                        instanceDrawAlloc.GpuAddress, boundInstanceAddress, drawCount + shadowCount,
                        sub.AlphaTest, CascadeCounts.Uniform(drawCount + shadowCount),
                        batchState.UsesTallGrassWind));
                }
                else
                {
                    if (mainHasCascadeInstances)
                    {
                        _shadowDraws.Add(new ShadowDraw(
                            sub.EffectiveVertexBufferView, sub.IndexBufferView, sub.IndexCount,
                            instanceDrawAlloc.GpuAddress, boundInstanceAddress, drawCount,
                            sub.AlphaTest, mainCascades, batchState.UsesTallGrassWind));
                    }

                    if (tailHasCascadeInstances)
                    {
                        _shadowDraws.Add(new ShadowDraw(
                            sub.EffectiveVertexBufferView, sub.IndexBufferView, sub.IndexCount,
                            shadowTailCb, boundInstanceAddress, shadowCount,
                            sub.AlphaTest,
                            shadowTailCascades,
                            // Tall-grass shadow telemetry is attributed to the main draw only, so the
                            // tail must not double-count it.
                            UsesTallGrassWind: false));
                    }
                }
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
                if (AnimationsEnabled &&
                    (sub.PhysicsLiteSway is not null || sub.RigidNodeAnimation is not null))
                {
                    // Reuse the host's general animated-mesh shadow contract: physics-lite and
                    // rigid node animation change only per-instance matrices, but they have the
                    // same requirement that a cached sun map be refreshed every frame while the
                    // GUI Animations switch is on.
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
            startInstance += (uint)(drawCount + shadowCount);
            if (drawCount > 0)
            {
                // These counters describe color submissions; shadow-only records replay later and
                // have their own per-cascade submitted-draw/instance telemetry.
                submeshDraws++;
                LastStats.ReferenceInstancedDraws++;
                LastStats.ReferenceInstances += drawCount;
            }
        }

        FlushOpaqueIndirectRun(
            cmd,
            frameIndex,
            indirectArgumentAllocation,
            pendingIndirectStart,
            ref pendingIndirectCount,
            ref pendingIndirectPso,
            ref currentPso,
            ref drawCallMs);

        LastStats.ReferenceBatches = activeBatchCount;
        LastStats.ReferenceOpaqueUniquePsos = _opaqueSubmissionPsos.Count;
    }

    private void FlushOpaqueIndirectRun(
        ID3D12GraphicsCommandList cmd,
        int frameIndex,
        GpuRingBuffer12.RingAllocation argumentAllocation,
        int pendingStart,
        ref int pendingCount,
        ref ID3D12PipelineState? pendingPso,
        ref ID3D12PipelineState? currentPso,
        ref double drawCallMs)
    {
        if (pendingCount == 0)
        {
            return;
        }

        var pso = pendingPso ?? throw new InvalidOperationException(
            "An opaque indirect run has commands but no pipeline state.");
        var started = StartTiming();
        if (!ReferenceEquals(currentPso, pso))
        {
            cmd.SetPipelineState(pso);
            currentPso = pso;
        }

        var commandSignature = _opaqueIndirectSignature ?? throw new InvalidOperationException(
            "An opaque indirect run cannot execute without a command signature.");
        cmd.ExecuteIndirect(
            commandSignature: commandSignature,
            maxCommandCount: (uint)pendingCount,
            argumentBuffer: _ringBuffer.GetFrameBuffer(frameIndex),
            argumentBufferOffset: argumentAllocation.ByteOffset
                                  + ((ulong)pendingStart * OpaqueIndirectCommand12.ByteStride),
            countBuffer: null,
            countBufferOffset: 0);
        drawCallMs += ElapsedMilliseconds(started);
        LastStats.ReferenceOpaqueIndirectDraws += pendingCount;
        LastStats.ReferenceOpaqueIndirectExecuteCalls++;
        pendingCount = 0;
        pendingPso = null;
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
        IWaterHeightProbe? probe = null,
        float cameraZ = 0f,
        float maxQueuedWaterHeight = float.NaN,
        ITransparencyInterleave? interleave = null)
    {
        if (_blendedDraws.Count == 0) return;

        var draws = _blendedDraws;
        if (waterPartition != DeferredWaterPartition.All && probe is not null)
        {
            _waterPartitionedBlendedDraws.Clear();
            var belowCount = 0;
            var aboveCount = 0;
            var surfaceSum = 0f;
            foreach (var draw in _blendedDraws)
            {
                // Each draw is tested against the water surface local to its OWN XY — no single
                // global plane works across a view containing several water bodies at different
                // heights (see WaterTransparencyPartition for the measured failure).
                var below = WaterTransparencyPartition.IsWhollyBelow(
                    probe,
                    draw.WorldBoundsCenterXY.X,
                    draw.WorldBoundsCenterXY.Y,
                    draw.WorldBoundsMaxZ,
                    cameraZ);
                // The complement classification is GLOBAL on purpose: "no drawn surface can occlude
                // this" must hold against every queued water body, so it tests the frame's highest
                // queued surface (NaN outside the unified stream = the class is empty and
                // NotWhollyBelow keeps its legacy meaning). Disjoint from below by construction —
                // a draw wholly under its local surface cannot have its bottom above the maximum.
                var above = !below && WaterTransparencyPartition.IsWhollyAboveAllWater(
                    draw.WorldBoundsMinZ, cameraZ, maxQueuedWaterHeight);
                if (below)
                {
                    belowCount++;
                    if (probe.TryGetWaterHeightAt(
                            draw.WorldBoundsCenterXY.X, draw.WorldBoundsCenterXY.Y, out var surface))
                    {
                        surfaceSum += surface;
                    }
                }
                else if (above)
                {
                    aboveCount++;
                }

                if ((waterPartition == DeferredWaterPartition.WhollyBelow && below) ||
                    (waterPartition == DeferredWaterPartition.NotWhollyBelow && !below && !above) ||
                    (waterPartition == DeferredWaterPartition.WhollyAboveAllWater && above))
                {
                    _waterPartitionedBlendedDraws.Add(draw);
                }
            }

            // Recorded on the below-water leg only, so the pair describes one classification rather
            // than the complement overwriting it. Without this the split was invisible to every
            // user-facing surface, which is what let it sit dead outdoors unnoticed. The reported
            // height is the mean surface across the submerged draws — with per-XY lookup there is no
            // longer a single plane to quote.
            if (waterPartition == DeferredWaterPartition.WhollyBelow)
            {
                LastBelowWaterBlendedDraws = belowCount;
                LastPartitionedBlendedCandidates = _blendedDraws.Count;
                LastBelowWaterPartitionPlane = belowCount > 0 ? surfaceSum / belowCount : float.NaN;
                // The below pass opens every partitioned frame (legacy split included), so this is
                // the one place that can guarantee a legacy frame never shows the previous
                // streaming frame's above-water count.
                LastAboveWaterBlendedDraws = 0;
            }

            // The above-water leg draws LAST, so recording here cannot be overwritten by a later
            // partition call this frame — same stale-count discipline as the below-water pair.
            if (waterPartition == DeferredWaterPartition.WhollyAboveAllWater)
            {
                LastAboveWaterBlendedDraws = aboveCount;
            }

            if (_waterPartitionedBlendedDraws.Count == 0) return;
            draws = _waterPartitionedBlendedDraws;
        }

        // Sort an INDEX array, not the draws themselves. BlendedReferenceDraw is ~220 bytes (two
        // Matrix4x4 plus a dozen vectors), and List.Sort moves the elements: at the measured ~7,000
        // blended draws that is ~90k comparisons each shuffling several hundred bytes, tens of MB of
        // pure memory traffic per frame. Permuting 4-byte indices is the same ordering for ~1/50th of
        // the movement, and leaves the draw list itself untouched.
        var order = ArrayPool<int>.Shared.Rent(draws.Count);
        var sortKeys = ArrayPool<float>.Shared.Rent(draws.Count);
        var reservations = ArrayPool<GpuRingBuffer12.RingAllocation>.Shared.Rent(draws.Count);
        try
        {
            for (var i = 0; i < draws.Count; i++)
            {
                order[i] = i;
                // View-axis depth (clip-space w of the draw's world bound center against the frame
                // viewProj) — the engine's own alpha-sort metric, and the SAME metric the water
                // batches carry, which is what lets the two streams merge. Negated so an ASCENDING
                // sort yields farthest-first (back-to-front), which the blend pass and the
                // nearest-first reservation plan both assume. Non-finite keys pin deterministically
                // to the far end (drawn first) instead of NaN-poisoning the comparison.
                var draw = draws[i];
                var depth = (draw.WorldBoundsCenterXY.X * _frameWColumn.X)
                            + (draw.WorldBoundsCenterXY.Y * _frameWColumn.Y)
                            + (draw.WorldBoundsCenterZ * _frameWColumn.Z)
                            + _frameWColumn.W;
                sortKeys[i] = float.IsFinite(depth) ? -depth : float.NegativeInfinity;
            }

            // Sort the index array through a keyed comparer with an original-index tiebreak: the
            // engine sorts alpha passes with a STABLE merge sort, so equal depths keep registration
            // order — Array.Sort's introsort alone would let ties swap frame to frame.
            _blendedOrderComparer.Keys = sortKeys;
            Array.Sort(order, 0, draws.Count, _blendedOrderComparer);
            // Reserve per-draw CBs nearest-first, then issue the surviving suffix back-to-front.
            // Optional particle-sort index allocations happen only after every survivor's CB is safe,
            // so later transient allocations can degrade sorting but can never evict a nearer object.
            var reservationStarted = StartTiming();
            var firstSelected = ReserveNearestBlendedDrawConstants(
                frameIndex, draws, order, reservations, out var truncated);
            cbUpdateMs += ElapsedMilliseconds(reservationStarted);
            LastFrameDrawsTruncated += truncated;
            for (var i = firstSelected; i < draws.Count; i++)
            {
                // i walks the SORTED positions (reservations are keyed to them); order[i] maps back to
                // the draw itself, which was never moved.
                var draw = draws[order[i]];
                if (draw.Submesh.EffectiveIndexCount <= 0 ||
                    (_frameRefilterActive && !PassesExactCull(draw.ReferenceBounds)) ||
                    !PassesExactGrassDistance(draw.SourceWorld.Translation, draw.IsGrass)) continue;
                // Unified stream merge: draw every queued water batch at least as far as this draw
                // first (sortKeys holds the NEGATED depth, indexed by ORIGINAL draw index). Water
                // clobbers the per-frame CBV/bindless tables, so rebind and force a PSO re-set.
                if (interleave is not null && interleave.DrainDownTo(-sortKeys[order[i]]))
                {
                    if (BlendTraceFormId != 0)
                    {
                        Core.Diagnostics.Logger.Instance.Info(
                            $"[blend-trace] water-drain before sorted#{i} " +
                            $"(down to depth {-sortKeys[order[i]]:F1})");
                    }
                    cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    cmd.SetGraphicsRootConstantBufferView(
                        GpuRootSignature12.Slots.PerFrameCbv, _deferredPerFrameCbvAddress);
                    GpuRootSignature12.SetGraphicsBindlessTables(
                        cmd, _cbvSrvUavHeap.BindlessHeapStartGpu);
                    currentPso = null;
                }
                // In the unified stream a depth-writing blend is a per-draw PSO choice (the hoisted
                // pre-water list no longer exists there). With the ENGINE rule active (default),
                // z-write is the decompile-proven ambient-ON minus the authored exceptions baked
                // into EngineZWriteOff; FALLOUT_VIEWER_ENGINE_ZWRITE=0 falls back to the legacy
                // blend+test+threshold classification. Billboards keep z-write off on BOTH arms —
                // a deliberate viewer deviation (the engine's 16 SetZWriteEnable sites include no
                // billboard case): their camera-facing quads are not occluders, and most are
                // NoLighting (authored write-off) anyway.
                //
                // The rule keys on the STREAM ROUTING MODE the draw lists were built under — NOT on
                // whether THIS pass carries the water interleave. The stream frame issues up to
                // three DrawBlended passes (submerged partition, interleaved core, above-all-water
                // tail) and only the core one holds the interleave; gating on it silently dropped
                // depth writes for every draw in the above-all-water tail, so any queued water
                // ANYWHERE in the visible radius made elevated meshes resolve by paint order alone
                // (the Goodsprings saloon sign's south chain compositing over its own nearer post —
                // the sort centers disagree with per-pixel depth, which only z-writes can fix).
                var engineRule = _lastBuildStreamActive && EngineZWriteEnabled;
                var writesDepth = !draw.Submesh.IsBillboard &&
                                  (engineRule
                                      ? !draw.Submesh.EngineZWriteOff
                                      : draw.Submesh.DepthWritingBlend);
                // NoLighting "zbuffer test" bit 31 CLEAR ⇒ hardware depth test OFF. Applied only
                // under the engine rule so stream-off frames stay byte-identical.
                var depthTestOff = engineRule && draw.Submesh.DepthTestOff;
                if (BlendTraceFormId != 0 && draw.PhysicsLiteSeed == BlendTraceFormId)
                {
                    Core.Diagnostics.Logger.Instance.Info(
                        $"[blend-trace] sorted#{i} block={draw.Submesh.SourceBlockIndex} " +
                        $"depthKey={-sortKeys[order[i]]:F1} zwrite={writesDepth} " +
                        $"ztestOff={depthTestOff} mode={draw.Submesh.AlphaRenderMode} " +
                        $"billboard={draw.Submesh.IsBillboard} " +
                        $"center=({draw.WorldBoundsCenterXY.X:F0},{draw.WorldBoundsCenterXY.Y:F0}," +
                        $"{draw.WorldBoundsCenterZ:F0})");
                    if (BlendSkipBlock >= 0 && draw.Submesh.SourceBlockIndex == BlendSkipBlock)
                    {
                        continue;
                    }
                }
                var pso = _lastBuildStreamActive && writesDepth
                    ? _pipelines.GetBlendDepthWritePipeline(
                        draw.Submesh.SrcBlendMode,
                        draw.Submesh.DstBlendMode,
                        draw.Submesh.DoubleSided,
                        draw.Submesh.IsDecal,
                        grassRoute: draw.IsGrass,
                        depthTestOff: depthTestOff)
                    : _pipelines.GetBlendPipeline(
                        draw.Submesh.SrcBlendMode,
                        draw.Submesh.DstBlendMode,
                        draw.Submesh.DoubleSided,
                        draw.Submesh.IsDecal,
                        grassRoute: draw.IsGrass,
                        depthTestOff: depthTestOff);
                DrawBlendedSubmesh(
                    cmd,
                    frameIndex,
                    draw,
                    pso,
                    reservations[i],
                    sceneDepthSampled,
                    ref currentPso);
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
            ArrayPool<float>.Shared.Return(sortKeys);
            ArrayPool<int>.Shared.Return(order);
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

        // Index sort, same reasoning as DrawBlended: permute 4-byte indices rather than ~220-byte draws.
        var count = _depthWritingBlendDraws.Count;
        var order = ArrayPool<int>.Shared.Rent(count);
        var sortKeys = ArrayPool<float>.Shared.Rent(count);
        var reservations = ArrayPool<GpuRingBuffer12.RingAllocation>.Shared.Rent(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                order[i] = i;
                sortKeys[i] = -_depthWritingBlendDraws[i].DistanceSquared; // ascending => farthest first
            }

            Array.Sort(sortKeys, order, 0, count);

            var reservationStarted = StartTiming();
            var firstSelected = ReserveNearestBlendedDrawConstants(
                frameIndex, _depthWritingBlendDraws, order, reservations, out var truncated);
            cbUpdateMs += ElapsedMilliseconds(reservationStarted);
            LastFrameDrawsTruncated += truncated;
            for (var i = firstSelected; i < count; i++)
            {
                var draw = _depthWritingBlendDraws[order[i]];
                if (draw.Submesh.EffectiveIndexCount <= 0 ||
                    (_frameRefilterActive && !PassesExactCull(draw.ReferenceBounds)) ||
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
                    ref currentPso);
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
            ArrayPool<float>.Shared.Return(sortKeys);
            ArrayPool<int>.Shared.Return(order);
        }
    }

    /// <summary>
    ///     Reserves constant-buffer space from nearest to farthest and returns the first draw in the
    ///     back-to-front suffix that survived. Quiet live-particle entries require no reservation and
    ///     are not counted as truncated draws.
    /// </summary>
    /// <param name="order">
    ///     Sorted positions into <paramref name="draws" /> (farthest first). Everything here — the
    ///     drawable mask, the capacity plan and <paramref name="reservations" /> — is keyed to the
    ///     SORTED position, not to the draw's index in the list.
    /// </param>
    private int ReserveNearestBlendedDrawConstants(
        int frameIndex,
        List<BlendedReferenceDraw> draws,
        int[] order,
        Span<GpuRingBuffer12.RingAllocation> reservations,
        out int truncated)
    {
        var drawable = ArrayPool<byte>.Shared.Rent(draws.Count);
        try
        {
            for (var i = 0; i < draws.Count; i++)
            {
                var draw = draws[order[i]];
                drawable[i] = draw.Submesh.EffectiveIndexCount > 0 &&
                              (!_frameRefilterActive || PassesExactCull(draw.ReferenceBounds)) &&
                              PassesExactGrassDistance(
                                  draw.SourceWorld.Translation,
                                  draw.IsGrass)
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
        ref ID3D12PipelineState? currentPso)
    {
        var effectiveIndexCount = draw.Submesh.EffectiveIndexCount;
        Debug.Assert(effectiveIndexCount > 0, "Quiet live-particle frames must be filtered before drawing.");

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
        // FormID heatmap on the blended/billboard per-draw path — same w-lane channel as the
        // instanced copy pass. PhysicsLiteSeed carries the owning REFR's FormID on EVERY blended
        // draw (not just sway meshes — see the BlendedReferenceDraw construction); grass is
        // excluded because its matrix w-lanes are the FNV grass lighting payload.
        var heatmapWorld = draw.World;
        if (_frameHeatmapActive && !draw.IsGrass)
        {
            ApplyFormIdHeatmapTint(
                ref heatmapWorld, draw.PhysicsLiteSeed,
                draw.WorldBoundsCenterXY.X, draw.WorldBoundsCenterXY.Y);
        }
        var perDraw = new PerDrawConstants
        {
            World = heatmapWorld,
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
                draw.Submesh.StarfieldOpacity?.BindlessIndex ??
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
            EffectFalloff = ResolveEffectFalloffConstants(draw.Submesh),
            EnvMap = ResolveEnvMapState(draw.Submesh),
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
                        fallbackReason =
 "transient live-particle GPU budget is exhausted; static baked geometry is rendered";
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
    ///     The world-space TOP of a blended submesh, used to decide whether it lies under a water
    ///     surface. Prefers the exact maximum of the mesh's transformed local AABB; the bounding
    ///     SPHERE's top (<c>centerZ + radius</c>) is far too pessimistic for the flat decal cards this
    ///     mainly exists to classify — measured NiBound radii are 70-125 units (ssLogoDecal 70.7,
    ///     DamageDecal01 125.5), so a wide decal lying a few units under the surface never qualified as
    ///     submerged and composited on top of the water instead of being refracted by it.
    ///     <para>
    ///         Billboards keep the sphere bound: their world matrix is rebuilt to face the camera every
    ///         frame, so a placement-matrix AABB would not describe the geometry actually drawn.
    ///     </para>
    ///     <para>
    ///         The cached AABB spans the whole mesh, not this one submesh, so a multi-submesh mesh
    ///         over-estimates the top. That errs toward "not below", i.e. toward the previous
    ///         behaviour, which is the safe direction: it can only fail to submerge something, never
    ///         wrongly bury geometry that straddles the surface.
    ///     </para>
    /// </summary>
    private float ResolveWorldBoundsMaxZ(
        uint meshId, CachedSubmesh12 submesh, Matrix4x4 world, float worldCenterZ, float worldRadius)
    {
        var sphereTop = worldCenterZ + worldRadius;
        if (submesh.IsBillboard || !_meshLocalBounds.TryGetValue(meshId, out var bounds))
        {
            return sphereTop;
        }

        // A degenerate/empty mesh is recorded as Min > Max (see CachedNifMesh12.LocalBoundsMin);
        // running the corner arithmetic on an inverted box yields a meaningless top.
        if (bounds.Min.X > bounds.Max.X || bounds.Min.Y > bounds.Max.Y || bounds.Min.Z > bounds.Max.Z)
        {
            return sphereTop;
        }

        // Max Z of the transformed box: worldZ = x·M13 + y·M23 + z·M33 + M43, so take the larger
        // endpoint of each axis independently. No need to materialize all eight corners.
        var maxZ = world.M43
                   + MathF.Max(bounds.Min.X * world.M13, bounds.Max.X * world.M13)
                   + MathF.Max(bounds.Min.Y * world.M23, bounds.Max.Y * world.M23)
                   + MathF.Max(bounds.Min.Z * world.M33, bounds.Max.Z * world.M33);
        return float.IsFinite(maxZ) ? MathF.Min(maxZ, sphereTop) : sphereTop;
    }

    /// <summary>
    ///     World-space BOTTOM of the submesh's bounds — the exact mirror of
    ///     <see cref="ResolveWorldBoundsMaxZ" />, with the same fallbacks and the same error
    ///     direction: a sphere-only answer extends LOWER than the true box, which can only demote a
    ///     draw from the above-all-water partition back to the interleaved status quo, never
    ///     wrongly lift water-adjacent geometry above the surface.
    /// </summary>
    private float ResolveWorldBoundsMinZ(
        uint meshId, CachedSubmesh12 submesh, Matrix4x4 world, float worldCenterZ, float worldRadius)
    {
        var sphereBottom = worldCenterZ - worldRadius;
        if (submesh.IsBillboard || !_meshLocalBounds.TryGetValue(meshId, out var bounds))
        {
            return sphereBottom;
        }

        if (bounds.Min.X > bounds.Max.X || bounds.Min.Y > bounds.Max.Y || bounds.Min.Z > bounds.Max.Z)
        {
            return sphereBottom;
        }

        var minZ = world.M43
                   + MathF.Min(bounds.Min.X * world.M13, bounds.Max.X * world.M13)
                   + MathF.Min(bounds.Min.Y * world.M23, bounds.Max.Y * world.M23)
                   + MathF.Min(bounds.Min.Z * world.M33, bounds.Max.Z * world.M33);
        return float.IsFinite(minZ) ? MathF.Max(minZ, sphereBottom) : sphereBottom;
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
        RefreshBlendedDraws(
            _blendedDraws,
            _depthWritingBlendDraws,
            cameraPosition,
            cameraForward,
            renderOrigin);
    }

    private void RefreshBlendedDraws(
        List<BlendedReferenceDraw> blendedDraws,
        List<BlendedReferenceDraw> depthWritingBlendDraws,
        Vector3 cameraPosition,
        Vector3 cameraForward,
        Vector3 renderOrigin)
    {
        RefreshBlendedDrawList(blendedDraws, cameraPosition, cameraForward, renderOrigin);
        RefreshBlendedDrawList(depthWritingBlendDraws, cameraPosition, cameraForward, renderOrigin);
    }

    private void RefreshBlendedDrawList(
        List<BlendedReferenceDraw> draws, Vector3 cameraPosition, Vector3 cameraForward,
        Vector3 renderOrigin)
    {
        var span = CollectionsMarshal.AsSpan(draws);
        for (var i = 0; i < span.Length; i++)
        {
            ref var draw = ref span[i];
            var sampledSourceWorld = ApplyRigidNodeAnimation(
                draw.Submesh,
                ApplyPhysicsLiteSway(draw.Submesh, draw.PhysicsLiteSeed, draw.SourceWorld));
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
            var worldRadius = ResolveWorldBoundsRadius(draw.Submesh, sampledSourceWorld);
            draw = draw with
            {
                World = world,
                DistanceSquared = Vector3.DistanceSquared(worldCenter, cameraPosition),
                WorldBoundsCenterZ = worldCenter.Z,
                WorldBoundsRadius = worldRadius,
                // Recomputed rather than carried over: physics-lite sway rotates the placement, which
                // moves the box top independently of its centre.
                WorldBoundsMaxZ = ResolveWorldBoundsMaxZ(
                    draw.MeshId, draw.Submesh, sampledSourceWorld, worldCenter.Z, worldRadius),
                WorldBoundsMinZ = ResolveWorldBoundsMinZ(
                    draw.MeshId, draw.Submesh, sampledSourceWorld, worldCenter.Z, worldRadius),
                WorldBoundsCenterXY = new Vector2(worldCenter.X, worldCenter.Y),
            };
        }
    }

    /// <summary>
    ///     Applies a baked rigid-node playback delta before the REFR placement (row-vector order:
    ///     delta * placement, mirroring <see cref="ApplyPhysicsLiteSway" />). One evaluation per
    ///     call on the shared global animation clock — all placements of a model stay in sync, the
    ///     engine's controller-manager behavior. Animations off returns the rest matrix unchanged.
    /// </summary>
    private Matrix4x4 ApplyRigidNodeAnimation(CachedSubmesh12 submesh, Matrix4x4 restWorld)
    {
        if (!AnimationsEnabled || submesh.RigidNodeAnimation is not { } track)
        {
            return restWorld;
        }

        return track.Evaluate(_animationClockSeconds) * restWorld;
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
    /// <summary>
    ///     Reorders every active batch's instances so that all casters belonging to cascade 0 come
    ///     first, then those first reached by cascade 1, and so on — leaving
    ///     <see cref="OpaqueBatchState.CascadePrefix" /> as the count each cascade must draw.
    ///     <para>
    ///         Without this the replay submits the ENTIRE caster set to every cascade. Cascade 0 spans
    ///         2048 units of a 65,536-unit render radius, so ~99.7% of what it received was clipped
    ///         after full vertex shading; measured, the two near cascades cost ~11 ms of GPU each,
    ///         every frame.
    ///     </para>
    ///     <para>
    ///         A per-batch UNION bound would be useless here: batches key on submesh, so one common
    ///         rock model's batch spans the whole world and would never cull. Sorting works because
    ///         the cascades are concentric and nested, making "smallest containing cascade" a total
    ///         order whose prefixes are exactly the per-cascade sets.
    ///     </para>
    /// </summary>
    private bool SortBatchInstancesByCascade(BatchBuildState build, OpaqueBatchState batch)
    {
        const int sortWorkChunk = 512;
        if (build.CascadeSort is not { } cursor || !ReferenceEquals(cursor.Batch, batch))
        {
            cursor = new CascadeBatchSortState(batch);
            build.CascadeSort = cursor;
        }

        switch (cursor.Phase)
        {
            case CascadeBatchSortPhase.Initialize:
                Array.Clear(batch.CascadePrefix);
                Array.Clear(batch.ShadowOnlyCascadePrefix);
                // No fit (capture/export paths, or the kill switch): every cascade keeps the whole
                // caster set. Zero prefixes mean "no caster", so fill them explicitly.
                if (!ShadowCascadeCullEnabled || build.CascadeFit is not { } initialFit ||
                    initialFit.Radii.Length == 0)
                {
                    batch.CascadePrefix.AsSpan().Fill(batch.Instances.Count);
                    batch.ShadowOnlyCascadePrefix.AsSpan().Fill(batch.ShadowOnlyInstances.Count);
                    cursor.Phase = CascadeBatchSortPhase.Complete;
                    return true;
                }

                cursor.Cascades = Math.Min(initialFit.Radii.Length, ShadowMapRenderer12.CascadeCount);
                cursor.Slack = CullPositionSlack * 1.7321f;
                cursor.Count = batch.Instances.Count;
                cursor.Index = 0;
                cursor.HasSeeds = batch.PhysicsLiteSeeds.Count == cursor.Count;
                cursor.HasHeatmapIds = batch.HeatmapFormIds.Count == cursor.Count;
                Array.Clear(cursor.BucketCounts);
                _cascadeBucketScratch.Clear();
                _cascadeBucketScratch.EnsureCapacity(cursor.Count);
                if (cursor.Count == 0)
                {
                    cursor.Phase = CascadeBatchSortPhase.ShadowInitialize;
                }
                else if (batch.InstanceBounds.Count != cursor.Count)
                {
                    batch.CascadePrefix.AsSpan().Fill(cursor.Count);
                    cursor.Phase = CascadeBatchSortPhase.ShadowInitialize;
                }
                else
                {
                    cursor.Phase = CascadeBatchSortPhase.MainClassify;
                }
                return false;

            case CascadeBatchSortPhase.MainClassify:
            {
                var fit = build.CascadeFit!.Value;
                var stop = Math.Min(cursor.Index + sortWorkChunk, cursor.Count);
                var bounds = CollectionsMarshal.AsSpan(batch.InstanceBounds);
                while (cursor.Index < stop)
                {
                    var bucket = ClassifyCascade(
                        bounds[cursor.Index], fit, cursor.Cascades, cursor.Slack);
                    _cascadeBucketScratch.Add(bucket);
                    cursor.BucketCounts[bucket]++;
                    cursor.Index++;
                }

                if (cursor.Index == cursor.Count)
                {
                    PrepareCascadeScatter(
                        cursor.BucketCounts, cursor.WriteCursor,
                        batch.CascadePrefix, cursor.Cascades);
                    _cascadeSortMatrices.Clear();
                    _cascadeSortBounds.Clear();
                    _cascadeSortSeeds.Clear();
                    _cascadeSortHeatmapIds.Clear();
                    EnsureCapacity(_cascadeSortMatrices, cursor.Count);
                    EnsureCapacity(_cascadeSortBounds, cursor.Count);
                    if (cursor.HasSeeds) EnsureCapacity(_cascadeSortSeeds, cursor.Count);
                    if (cursor.HasHeatmapIds) EnsureCapacity(_cascadeSortHeatmapIds, cursor.Count);
                    cursor.Index = 0;
                    cursor.Phase = CascadeBatchSortPhase.MainScatter;
                }
                return false;
            }

            case CascadeBatchSortPhase.MainScatter:
            {
                var stop = Math.Min(cursor.Index + sortWorkChunk, cursor.Count);
                var sourceMatrices = CollectionsMarshal.AsSpan(batch.Instances);
                var sourceBounds = CollectionsMarshal.AsSpan(batch.InstanceBounds);
                var sourceSeeds = CollectionsMarshal.AsSpan(batch.PhysicsLiteSeeds);
                var sourceHeatmapIds = CollectionsMarshal.AsSpan(batch.HeatmapFormIds);
                var targetMatrices = CollectionsMarshal.AsSpan(_cascadeSortMatrices);
                var targetBounds = CollectionsMarshal.AsSpan(_cascadeSortBounds);
                var targetSeeds = CollectionsMarshal.AsSpan(_cascadeSortSeeds);
                var targetHeatmapIds = CollectionsMarshal.AsSpan(_cascadeSortHeatmapIds);
                while (cursor.Index < stop)
                {
                    var slot = cursor.WriteCursor[_cascadeBucketScratch[cursor.Index]]++;
                    targetMatrices[slot] = sourceMatrices[cursor.Index];
                    targetBounds[slot] = sourceBounds[cursor.Index];
                    if (cursor.HasSeeds) targetSeeds[slot] = sourceSeeds[cursor.Index];
                    if (cursor.HasHeatmapIds) targetHeatmapIds[slot] = sourceHeatmapIds[cursor.Index];
                    cursor.Index++;
                }

                if (cursor.Index == cursor.Count)
                {
                    cursor.Index = 0;
                    cursor.Phase = CascadeBatchSortPhase.MainCopy;
                }
                return false;
            }

            case CascadeBatchSortPhase.MainCopy:
            {
                var length = Math.Min(sortWorkChunk, cursor.Count - cursor.Index);
                var sourceMatrices = CollectionsMarshal.AsSpan(_cascadeSortMatrices);
                var sourceBounds = CollectionsMarshal.AsSpan(_cascadeSortBounds);
                sourceMatrices.Slice(cursor.Index, length).CopyTo(
                    CollectionsMarshal.AsSpan(batch.Instances).Slice(cursor.Index, length));
                sourceBounds.Slice(cursor.Index, length).CopyTo(
                    CollectionsMarshal.AsSpan(batch.InstanceBounds).Slice(cursor.Index, length));
                if (cursor.HasSeeds)
                {
                    CollectionsMarshal.AsSpan(_cascadeSortSeeds).Slice(cursor.Index, length).CopyTo(
                        CollectionsMarshal.AsSpan(batch.PhysicsLiteSeeds).Slice(cursor.Index, length));
                }
                if (cursor.HasHeatmapIds)
                {
                    CollectionsMarshal.AsSpan(_cascadeSortHeatmapIds).Slice(cursor.Index, length).CopyTo(
                        CollectionsMarshal.AsSpan(batch.HeatmapFormIds).Slice(cursor.Index, length));
                }
                cursor.Index += length;
                if (cursor.Index == cursor.Count)
                {
                    cursor.Phase = CascadeBatchSortPhase.ShadowInitialize;
                }
                return false;
            }

            case CascadeBatchSortPhase.ShadowInitialize:
                cursor.Count = batch.ShadowOnlyInstances.Count;
                cursor.Index = 0;
                cursor.HasSeeds = batch.ShadowOnlyPhysicsLiteSeeds.Count == cursor.Count;
                cursor.HasHeatmapIds = false;
                Array.Clear(cursor.BucketCounts);
                _cascadeBucketScratch.Clear();
                _cascadeBucketScratch.EnsureCapacity(cursor.Count);
                if (cursor.Count == 0)
                {
                    cursor.Phase = CascadeBatchSortPhase.Complete;
                    return true;
                }
                cursor.Phase = CascadeBatchSortPhase.ShadowClassify;
                return false;

            case CascadeBatchSortPhase.ShadowClassify:
            {
                var fit = build.CascadeFit!.Value;
                var stop = Math.Min(cursor.Index + sortWorkChunk, cursor.Count);
                var instances = CollectionsMarshal.AsSpan(batch.ShadowOnlyInstances);
                while (cursor.Index < stop)
                {
                    // Shadow-only matrices are render-origin relative; the fit is absolute.
                    var center = instances[cursor.Index].Translation + build.RenderOrigin;
                    var bucket = ClassifyCascade(
                        new Vector4(center, 0f), fit, cursor.Cascades, cursor.Slack);
                    _cascadeBucketScratch.Add(bucket);
                    cursor.BucketCounts[bucket]++;
                    cursor.Index++;
                }

                if (cursor.Index == cursor.Count)
                {
                    PrepareCascadeScatter(
                        cursor.BucketCounts, cursor.WriteCursor,
                        batch.ShadowOnlyCascadePrefix, cursor.Cascades);
                    _cascadeSortMatrices.Clear();
                    _cascadeSortSeeds.Clear();
                    EnsureCapacity(_cascadeSortMatrices, cursor.Count);
                    if (cursor.HasSeeds) EnsureCapacity(_cascadeSortSeeds, cursor.Count);
                    cursor.Index = 0;
                    cursor.Phase = CascadeBatchSortPhase.ShadowScatter;
                }
                return false;
            }

            case CascadeBatchSortPhase.ShadowScatter:
            {
                var stop = Math.Min(cursor.Index + sortWorkChunk, cursor.Count);
                var sourceMatrices = CollectionsMarshal.AsSpan(batch.ShadowOnlyInstances);
                var sourceSeeds = CollectionsMarshal.AsSpan(batch.ShadowOnlyPhysicsLiteSeeds);
                var targetMatrices = CollectionsMarshal.AsSpan(_cascadeSortMatrices);
                var targetSeeds = CollectionsMarshal.AsSpan(_cascadeSortSeeds);
                while (cursor.Index < stop)
                {
                    var slot = cursor.WriteCursor[_cascadeBucketScratch[cursor.Index]]++;
                    targetMatrices[slot] = sourceMatrices[cursor.Index];
                    if (cursor.HasSeeds) targetSeeds[slot] = sourceSeeds[cursor.Index];
                    cursor.Index++;
                }

                if (cursor.Index == cursor.Count)
                {
                    cursor.Index = 0;
                    cursor.Phase = CascadeBatchSortPhase.ShadowCopy;
                }
                return false;
            }

            case CascadeBatchSortPhase.ShadowCopy:
            {
                var length = Math.Min(sortWorkChunk, cursor.Count - cursor.Index);
                CollectionsMarshal.AsSpan(_cascadeSortMatrices).Slice(cursor.Index, length).CopyTo(
                    CollectionsMarshal.AsSpan(batch.ShadowOnlyInstances).Slice(cursor.Index, length));
                if (cursor.HasSeeds)
                {
                    CollectionsMarshal.AsSpan(_cascadeSortSeeds).Slice(cursor.Index, length).CopyTo(
                        CollectionsMarshal.AsSpan(batch.ShadowOnlyPhysicsLiteSeeds).Slice(cursor.Index, length));
                }
                cursor.Index += length;
                if (cursor.Index == cursor.Count)
                {
                    cursor.Phase = CascadeBatchSortPhase.Complete;
                    return true;
                }
                return false;
            }

            case CascadeBatchSortPhase.Complete:
                return true;
            default:
                throw new InvalidOperationException($"Unexpected cascade-sort phase {cursor.Phase}.");
        }
    }

    private static void PrepareCascadeScatter(
        int[] bucketCounts,
        int[] writeCursor,
        int[] prefix,
        int cascades)
    {
        var running = 0;
        for (var c = 0; c <= cascades; c++)
        {
            writeCursor[c] = running;
            running += bucketCounts[c];
            if (c < cascades) prefix[c] = running;
        }

        // Bucket `cascades` is outside every supplied cascade and deliberately excluded. Any
        // renderer cascade beyond the supplied radii inherits the widest real prefix.
        for (var c = cascades; c < prefix.Length; c++)
        {
            prefix[c] = prefix[cascades - 1];
        }
    }

    /// <summary>Index of the smallest cascade containing this caster, or <paramref name="cascades" />
    /// when it lies outside every one of them.</summary>
    private static int ClassifyCascade(
        Vector4 bounds,
        (Vector3 Anchor, Vector3 SunDirection, float[] Radii, float[] Snaps, float SceneZSpan) fit,
        int cascades,
        float slack)
    {
        var delta = new Vector3(bounds.X, bounds.Y, bounds.Z) - fit.Anchor;
        for (var c = 0; c < cascades; c++)
        {
            // A replayed cascade renders through its PUBLISHED frustum, whose anchor may lag this
            // frame's classification anchor by up to the cascade's own snap quantum per axis — widen
            // this cascade's reach by that quantum so the lag cannot truncate a caster out of the box
            // that actually draws it.
            var snap = c < fit.Snaps.Length ? fit.Snaps[c] : 0f;
            // The up-sun reach MUST come from the same helper the pass's BuildLightFrustum call uses
            // (WorldView3DControl.Frame.RecordSunShadowPass) — this decides what is submitted, that
            // decides what is rasterized, and a mismatch is a silent hole in the near cascade.
            var casterReach = SunShadowMath.CascadeCasterReach(
                fit.SunDirection, fit.Radii[c], fit.SceneZSpan);
            if (SunShadowMath.CascadeContains(
                    delta, fit.SunDirection, fit.Radii[c], bounds.W, slack + snap, casterReach))
            {
                return c;
            }
        }

        return cascades;
    }

    private static void EnsureCapacity<T>(List<T> list, int count)
    {
        if (list.Capacity < count) list.Capacity = count;
        CollectionsMarshal.SetCount(list, count);
    }

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
        Vector4 ReferenceBounds,
        uint PhysicsLiteSeed,
        bool IsGrass,
        float GrassWaveMultiplier,
        float WorldBoundsCenterZ,
        float WorldBoundsRadius,
        float WorldBoundsMaxZ,
        float WorldBoundsMinZ,
        uint MeshId,
        Vector2 WorldBoundsCenterXY);
}
#endif
