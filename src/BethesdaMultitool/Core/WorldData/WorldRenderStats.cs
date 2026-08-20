namespace BethesdaMultitool.Core.WorldData;

internal sealed record ParticleRenderTelemetry(
    int BlockIndex,
    string SourceTypeName,
    string Support,
    string? DiffuseTexture,
    int Capacity,
    int RenderedCount,
    int UvFrame,
    int AtlasFrameCount,
    uint DeterministicSeed,
    string? EmitterShape,
    string? EmitFrom,
    string? VelocityType,
    IReadOnlyList<string> OrderedModifiers,
    bool LiveFrame,
    string? FallbackReason);

internal sealed class WorldRenderStats
{
    internal int VisibleCandidates { get; set; }
    internal int TerrainDraws { get; set; }
    internal int TerrainQuadrantDraws { get; set; }

    /// <summary>
    ///     Terrain cells whose per-draw ring allocation soft-failed this frame — a silent
    ///     draw skip that would otherwise be un-observable (it reads as a missing cell on screen).
    /// </summary>
    internal int TerrainDrawsTruncated { get; set; }

    internal int NewUploads { get; set; }
    internal int NewPreUploads { get; set; }
    internal int TextureCacheMisses { get; set; }
    internal int TextureCompressedUploads { get; set; }
    internal int TextureRgbaFallbackUploads { get; set; }
    internal int TextureQueuedResolves { get; set; }
    internal int TextureActiveResolves { get; set; }
    internal int TexturePendingResolves { get; set; }
    internal int TexturePendingUploads { get; set; }
    internal int WaterDraws { get; set; }
    internal string? WaterPipeline { get; set; }
    internal string? WaterTechnique { get; set; }
    internal IReadOnlyList<string> WaterMapPaths { get; set; } = [];
    internal IReadOnlyList<string> WaterMapRoles { get; set; } = [];
    internal IReadOnlyList<bool> WaterMapResolved { get; set; } = [];
    internal int WaterAnimationFrame { get; set; } = -1;
    internal float WaterAnimationFps { get; set; }
    internal float WaterAnimationSeconds { get; set; }
    internal bool WaterNoisePrepassUsed { get; set; }
    internal string? WaterTelemetryUnavailableReason { get; set; }

    /// <summary>
    ///     Unified-transparency stream cost + loss, exposed because both regressed invisibly once:
    ///     the drain cuts a run at every blended reference draw, so runs used to scale with visible
    ///     CELLS rather than materials, and a ring exhaustion mid-drain silently dropped every
    ///     remaining (nearer) water entry — which reads to the user as "some pools are missing".
    ///     <see cref="WaterStreamEntries" /> is what was queued, <see cref="WaterStreamRuns" /> how
    ///     many draws that collapsed into, and <see cref="WaterStreamDroppedEntries" /> what never
    ///     drew. Dropped > 0 is always a defect, never a tuning outcome.
    /// </summary>
    internal int WaterStreamEntries { get; set; }

    internal int WaterStreamRuns { get; set; }
    internal int WaterStreamNoisePrepasses { get; set; }
    internal int WaterStreamDroppedEntries { get; set; }

    /// <summary>
    ///     Visible WATR materials that found no free per-material noise tile this frame and fell
    ///     back to their authored normal map. Non-zero means the slot pool is undersized for the
    ///     scene — a quality reduction that would otherwise be invisible.
    /// </summary>
    internal int WaterNoiseSlotOverflows { get; set; }

    internal int WireframeDraws { get; set; }
    internal double CpuFrameMilliseconds { get; set; }
    internal double StateSetupMilliseconds { get; set; }
    internal double VisibleGatherMilliseconds { get; set; }
    internal double VisibleSortMilliseconds { get; set; }
    internal double DrawLoopMilliseconds { get; set; }
    internal double MeshBuildUploadMilliseconds { get; set; }
    internal double NeighborPreUploadMilliseconds { get; set; }
    internal double QuadrantDrawMilliseconds { get; set; }
    internal double InstanceBuildMilliseconds { get; set; }
    internal double GpuUploadMilliseconds { get; set; }
    internal double DrawCallMilliseconds { get; set; }
    internal double ResourceResizeMilliseconds { get; set; }

    // ReferenceRenderer per-stage profiling. Counters answer "what is the
    // visible workload?" Times answer "where is the CPU going?" The split between
    // ReferenceCbUpdate / ReferenceSrvBind / ReferenceDrawCall identifies whether
    // GPU instancing would help or whether the bottleneck is elsewhere
    // (mesh cache thrash on motion, texture upload, GC pressure, etc.).
    internal int ReferenceCellsVisited { get; set; }
    internal int ReferenceCandidates { get; set; } // sum of PlacedObjects across visited cells
    internal int ReferenceCulled { get; set; } // dropped by per-REFR cylinder test

    // Whether this frame REUSED the cached cull survivor set instead of re-testing every candidate.
    // The single most motion-sensitive state in the renderer, and previously unreported: the hit rate
    // could only be inferred from ReferenceCullMilliseconds == 0, which is fragile. A rotating camera
    // drives this to 0% while straight-line travel holds it near 75%.
    internal bool ReferenceCullCacheHit { get; set; }
    internal bool ReferenceBatchesReused { get; set; }

    /// <summary>
    ///     First cull-cache clause that failed this frame (ReferenceRenderer12.CullCacheVeto).
    ///     Reported because "the cull cache missed" alone does not say WHICH invariant moved, and two
    ///     plausible-sounding culprits have already cost an experiment cycle apiece.
    /// </summary>
    internal int ReferenceCullCacheVeto { get; set; }

    /// <summary>
    ///     First reuse-gate clause that failed this frame (ReferenceRenderer12.BatchReuseBlocker).
    ///     0 = reused. The resolve+batch pass is the largest CPU item in the frame, so knowing WHY it
    ///     re-runs is the difference between fixing it and guessing at it.
    /// </summary>
    internal int ReferenceBatchReuseBlocker { get; set; }

    internal int ReferenceMeshMissing { get; set; } // GetOrUpload returned null this frame
    internal int ReferenceTexturePending { get; set; } // mesh ready, but at least one texture still streaming
    internal int ReferenceDrawn { get; set; } // REFRs that issued ≥1 submesh draw
    internal int ReferenceSubmeshDraws { get; set; } // total DrawIndexed calls
    internal int ReferenceSrvBinds { get; set; } // distinct PSSetShaderResource calls (post BindSrvIfChanged dedup)
    internal int ReferenceCullModeFlips { get; set; } // RSSetState calls (post SetCullModeIfChanged dedup)
    internal int ReferenceMeshCacheMisses { get; set; } // NIF parse + GPU upload events this frame
    internal int ReferenceDecodeRequests { get; set; }
    internal int ReferenceQueuedDecodes { get; set; }
    internal int ReferenceDecodeStarts { get; set; }
    internal int ReferenceActiveDecodes { get; set; }
    internal int ReferenceCpuDecodedMeshCacheHits { get; set; }
    internal int ReferenceCpuDecodedMeshCacheMisses { get; set; }
    internal int ReferenceCpuDecodedMeshNegativeHits { get; set; }
    internal int ReferenceGpuUploads { get; set; }
    internal int ReferenceUploadByteBudgetDeferrals { get; set; }
    internal int ReferenceCompressedTextureUploads { get; set; }
    internal int ReferenceRgbaTextureUploads { get; set; }
    internal int ReferenceTextureQueuedResolves { get; set; }
    internal int ReferenceTextureActiveResolves { get; set; }
    internal int ReferenceTexturePendingResolves { get; set; }
    internal int ReferenceTexturePendingUploads { get; set; }
    internal int ReferenceBatches { get; set; }
    internal int ReferenceInstances { get; set; }
    internal int ReferenceInstancedDraws { get; set; }

    internal int ReferenceBlendedDraws { get; set; }

    // Dormant PS1 audit counters. Retail does not submit SLS1009/SLS1013; these must remain zero.
    internal int ReferenceFnvSls1009Draws { get; set; }
    internal int ReferenceFnvSls1009Instances { get; set; }
    internal int ReferenceFnvSls1013Draws { get; set; }
    internal int ReferenceFnvSls1013Instances { get; set; }
    internal int ReferencePlacedLightCount { get; set; }
    internal bool ReferenceFnvClassicBasicLightingEnabled { get; set; }
    internal int ReferenceFnvClassicBasicFallbackDraws { get; set; }
    internal int ReferenceFnvClassicBasicFallbackInstances { get; set; }

    internal string? ReferenceFnvClassicBasicFallbackReason { get; set; }

    // Main-scene color draws that actually selected the bounded active retail
    // ID193/BSSM_ADT/SLS2000 route. Shadow replay is deliberately excluded.
    internal int ReferenceFnvActiveAdtBaseDraws { get; set; }
    internal int ReferenceFnvActiveAdtBaseInstances { get; set; }
    internal int ReferenceFnvActiveAdtBaseVertexColorDraws { get; set; }
    internal int ReferenceFnvActiveAdtBaseVertexColorInstances { get; set; }
    internal bool ReferenceFnvActiveAdtBaseEnabled { get; set; }
    internal int ReferenceFnvActiveAdtBaseFallbackDraws { get; set; }
    internal int ReferenceFnvActiveAdtBaseFallbackInstances { get; set; }

    internal string? ReferenceFnvActiveAdtBaseFallbackReason { get; set; }

    // Deferred blend depth diagnostics. SceneDepth counts transparent draws issued while a sampled
    // read-only depth view is active; SoftParticle is the eligible subset doing shader-side reject/fade.
    internal int ReferenceSceneDepthBlendedDraws { get; set; }
    internal int ReferenceSoftParticleDraws { get; set; }
    internal int ReferenceSoftParticleAuthoredDraws { get; set; }
    internal int ReferenceSoftParticleFallbackDraws { get; set; }

    internal int ReferenceSoftParticleDepthSampleCount { get; set; }

    // NiControllerSequence → NiAlphaController diagnostics for material-opacity animation.
    internal int ReferenceMaterialAlphaControllerDraws { get; set; }
    internal float ReferenceMaterialAlphaControllerMinimum { get; set; }

    internal float ReferenceMaterialAlphaControllerMaximum { get; set; }

    // Opt-in live-particle diagnostics. Owners is the number of unique particle systems visible in
    // the frame (including when the opt-in is disabled); the remaining counters describe geometry
    // actually produced by the live path. Draws counts placed-reference draw calls, so one shared
    // owner can legitimately produce multiple draws while uploading its snapshot only once.
    internal int ReferenceLiveParticleOwners { get; set; }
    internal int ReferenceLiveParticleParticles { get; set; }
    internal int ReferenceLiveParticleDraws { get; set; }
    internal int ReferenceLiveParticleFallbacks { get; set; }
    internal uint ReferenceLiveParticleUploadBytes { get; set; }
    internal int ReferenceLiveParticleUvFrame { get; set; } = -1;
    internal int ReferenceLiveParticleAtlasFrameCount { get; set; }
    internal int ReferenceLiveParticleAuthoredCapacity { get; set; }
    internal int ReferenceParticleRenderedCount { get; set; }
    internal IReadOnlyList<ParticleRenderTelemetry> ReferenceParticleSystems { get; set; } = [];
    internal float ReferenceSpeedTreeWindStrength { get; set; }
    internal float ReferenceSpeedTreeRockAmount { get; set; }
    internal float ReferenceSpeedTreeRockPhase { get; set; }
    internal float ReferenceSpeedTreeRustleAmount { get; set; }
    internal float ReferenceSpeedTreeRustlePhase { get; set; }
    internal float ReferenceSpeedTreeAnimationSeconds { get; set; }
    internal bool ReferenceSpeedTreeRuntimeLodEnabled { get; set; }
    internal int ReferenceSpeedTreeBranchInstances { get; set; }
    internal int ReferenceSpeedTreeLeafInstances { get; set; }
    internal int ReferenceSpeedTreeBillboardInstances { get; set; }
    internal int ReferenceSpeedTreeMinimumLod { get; set; } = -1;

    internal int ReferenceSpeedTreeMaximumLod { get; set; } = -1;

    // FNV TallGrassShaderProperty diagnostics. These are CPU-side observations of draws that used
    // the recovered GRASS2000 wind route; they deliberately do not alter any shader/GPU layout.
    internal bool ReferenceTallGrassWindSupported { get; set; }
    internal bool ReferenceTallGrassAnimationsEnabled { get; set; }
    internal float ReferenceTallGrassNormalizedStrength { get; set; }
    internal float ReferenceTallGrassMagnitudeWorldUnits { get; set; }
    internal float ReferenceTallGrassDirectionX { get; set; }
    internal float ReferenceTallGrassDirectionY { get; set; }
    internal float ReferenceTallGrassAnimationSeconds { get; set; }
    internal int ReferenceTallGrassInstancedDraws { get; set; }
    internal int ReferenceTallGrassInstancedInstances { get; set; }
    internal int ReferenceTallGrassDirectDraws { get; set; }
    internal int ReferenceTallGrassDirectInstances { get; set; }
    internal int ReferenceTallGrassShadowDraws { get; set; }
    internal int ReferenceTallGrassShadowInstances { get; set; }
    internal float ReferenceTallGrassWaveMultiplierMinimum { get; set; }
    internal float ReferenceTallGrassWaveMultiplierMaximum { get; set; }
    internal int ReferenceTallGrassWaveMultiplierDistinctCount { get; set; }
    internal float ReferenceTallGrassTemporalPhaseRadiansMinimum { get; set; }
    internal float ReferenceTallGrassTemporalPhaseRadiansMaximum { get; set; }
    internal double ReferenceStateSetupMilliseconds { get; set; }
    internal double ReferenceCullMilliseconds { get; set; } // per-REFR cylinder cull + placement-list walk
    internal double ReferenceMeshUploadMilliseconds { get; set; } // cache-miss NIF parse + GPU upload
    internal double ReferenceCbUpdateMilliseconds { get; set; } // per-submesh Map(WriteDiscard) on _perDrawCb
    internal double ReferenceSrvBindMilliseconds { get; set; } // BindSrvIfChanged + SetCullModeIfChanged
    internal double ReferenceDrawCallMilliseconds { get; set; } // IASetVB/IB + DrawIndexed

    internal void Reset()
    {
        VisibleCandidates = 0;
        TerrainDraws = 0;
        TerrainQuadrantDraws = 0;
        TerrainDrawsTruncated = 0;
        NewUploads = 0;
        NewPreUploads = 0;
        TextureCacheMisses = 0;
        TextureCompressedUploads = 0;
        TextureRgbaFallbackUploads = 0;
        TextureQueuedResolves = 0;
        TextureActiveResolves = 0;
        TexturePendingResolves = 0;
        TexturePendingUploads = 0;
        WaterDraws = 0;
        WaterPipeline = null;
        WaterTechnique = null;
        WaterMapPaths = [];
        WaterMapRoles = [];
        WaterMapResolved = [];
        WaterAnimationFrame = -1;
        WaterAnimationFps = 0f;
        WaterAnimationSeconds = 0f;
        WaterNoisePrepassUsed = false;
        WaterTelemetryUnavailableReason = null;
        WaterStreamEntries = 0;
        WaterStreamRuns = 0;
        WaterStreamNoisePrepasses = 0;
        WaterNoiseSlotOverflows = 0;
        WaterStreamDroppedEntries = 0;
        WireframeDraws = 0;
        CpuFrameMilliseconds = 0;
        StateSetupMilliseconds = 0;
        VisibleGatherMilliseconds = 0;
        VisibleSortMilliseconds = 0;
        DrawLoopMilliseconds = 0;
        MeshBuildUploadMilliseconds = 0;
        NeighborPreUploadMilliseconds = 0;
        QuadrantDrawMilliseconds = 0;
        InstanceBuildMilliseconds = 0;
        GpuUploadMilliseconds = 0;
        DrawCallMilliseconds = 0;
        ResourceResizeMilliseconds = 0;
        ReferenceCellsVisited = 0;
        ReferenceCandidates = 0;
        ReferenceCulled = 0;
        ReferenceCullCacheHit = false;
        ReferenceCullCacheVeto = 0;
        ReferenceBatchesReused = false;
        ReferenceBatchReuseBlocker = 0;
        ReferenceMeshMissing = 0;
        ReferenceTexturePending = 0;
        ReferenceDrawn = 0;
        ReferenceSubmeshDraws = 0;
        ReferenceSrvBinds = 0;
        ReferenceCullModeFlips = 0;
        ReferenceMeshCacheMisses = 0;
        ReferenceDecodeRequests = 0;
        ReferenceQueuedDecodes = 0;
        ReferenceDecodeStarts = 0;
        ReferenceActiveDecodes = 0;
        ReferenceCpuDecodedMeshCacheHits = 0;
        ReferenceCpuDecodedMeshCacheMisses = 0;
        ReferenceCpuDecodedMeshNegativeHits = 0;
        ReferenceGpuUploads = 0;
        ReferenceUploadByteBudgetDeferrals = 0;
        ReferenceCompressedTextureUploads = 0;
        ReferenceRgbaTextureUploads = 0;
        ReferenceTextureQueuedResolves = 0;
        ReferenceTextureActiveResolves = 0;
        ReferenceTexturePendingResolves = 0;
        ReferenceTexturePendingUploads = 0;
        ReferenceBatches = 0;
        ReferenceInstances = 0;
        ReferenceInstancedDraws = 0;
        ReferenceBlendedDraws = 0;
        ReferenceFnvSls1009Draws = 0;
        ReferenceFnvSls1009Instances = 0;
        ReferenceFnvSls1013Draws = 0;
        ReferenceFnvSls1013Instances = 0;
        ReferencePlacedLightCount = 0;
        ReferenceFnvClassicBasicLightingEnabled = false;
        ReferenceFnvClassicBasicFallbackDraws = 0;
        ReferenceFnvClassicBasicFallbackInstances = 0;
        ReferenceFnvClassicBasicFallbackReason = null;
        ReferenceFnvActiveAdtBaseDraws = 0;
        ReferenceFnvActiveAdtBaseInstances = 0;
        ReferenceFnvActiveAdtBaseVertexColorDraws = 0;
        ReferenceFnvActiveAdtBaseVertexColorInstances = 0;
        ReferenceFnvActiveAdtBaseEnabled = false;
        ReferenceFnvActiveAdtBaseFallbackDraws = 0;
        ReferenceFnvActiveAdtBaseFallbackInstances = 0;
        ReferenceFnvActiveAdtBaseFallbackReason = null;
        ReferenceSceneDepthBlendedDraws = 0;
        ReferenceSoftParticleDraws = 0;
        ReferenceSoftParticleAuthoredDraws = 0;
        ReferenceSoftParticleFallbackDraws = 0;
        ReferenceSoftParticleDepthSampleCount = 0;
        ReferenceMaterialAlphaControllerDraws = 0;
        ReferenceMaterialAlphaControllerMinimum = 0f;
        ReferenceMaterialAlphaControllerMaximum = 0f;
        ReferenceLiveParticleOwners = 0;
        ReferenceLiveParticleParticles = 0;
        ReferenceLiveParticleDraws = 0;
        ReferenceLiveParticleFallbacks = 0;
        ReferenceLiveParticleUploadBytes = 0;
        ReferenceLiveParticleUvFrame = -1;
        ReferenceLiveParticleAtlasFrameCount = 0;
        ReferenceLiveParticleAuthoredCapacity = 0;
        ReferenceParticleRenderedCount = 0;
        ReferenceParticleSystems = [];
        ReferenceSpeedTreeWindStrength = 0f;
        ReferenceSpeedTreeRockAmount = 0f;
        ReferenceSpeedTreeRockPhase = 0f;
        ReferenceSpeedTreeRustleAmount = 0f;
        ReferenceSpeedTreeRustlePhase = 0f;
        ReferenceSpeedTreeAnimationSeconds = 0f;
        ReferenceSpeedTreeRuntimeLodEnabled = false;
        ReferenceSpeedTreeBranchInstances = 0;
        ReferenceSpeedTreeLeafInstances = 0;
        ReferenceSpeedTreeBillboardInstances = 0;
        ReferenceSpeedTreeMinimumLod = -1;
        ReferenceSpeedTreeMaximumLod = -1;
        ReferenceTallGrassWindSupported = false;
        ReferenceTallGrassAnimationsEnabled = false;
        ReferenceTallGrassNormalizedStrength = 0f;
        ReferenceTallGrassMagnitudeWorldUnits = 0f;
        ReferenceTallGrassDirectionX = 0f;
        ReferenceTallGrassDirectionY = 0f;
        ReferenceTallGrassAnimationSeconds = 0f;
        ReferenceTallGrassInstancedDraws = 0;
        ReferenceTallGrassInstancedInstances = 0;
        ReferenceTallGrassDirectDraws = 0;
        ReferenceTallGrassDirectInstances = 0;
        ReferenceTallGrassShadowDraws = 0;
        ReferenceTallGrassShadowInstances = 0;
        ReferenceTallGrassWaveMultiplierMinimum = 0f;
        ReferenceTallGrassWaveMultiplierMaximum = 0f;
        ReferenceTallGrassWaveMultiplierDistinctCount = 0;
        ReferenceTallGrassTemporalPhaseRadiansMinimum = 0f;
        ReferenceTallGrassTemporalPhaseRadiansMaximum = 0f;
        ReferenceStateSetupMilliseconds = 0;
        ReferenceCullMilliseconds = 0;
        ReferenceMeshUploadMilliseconds = 0;
        ReferenceCbUpdateMilliseconds = 0;
        ReferenceSrvBindMilliseconds = 0;
        ReferenceDrawCallMilliseconds = 0;
    }

    internal WorldRenderStats Snapshot()
    {
        return new WorldRenderStats
        {
            VisibleCandidates = VisibleCandidates,
            TerrainDraws = TerrainDraws,
            TerrainQuadrantDraws = TerrainQuadrantDraws,
            TerrainDrawsTruncated = TerrainDrawsTruncated,
            NewUploads = NewUploads,
            NewPreUploads = NewPreUploads,
            TextureCacheMisses = TextureCacheMisses,
            TextureCompressedUploads = TextureCompressedUploads,
            TextureRgbaFallbackUploads = TextureRgbaFallbackUploads,
            TextureQueuedResolves = TextureQueuedResolves,
            TextureActiveResolves = TextureActiveResolves,
            TexturePendingResolves = TexturePendingResolves,
            TexturePendingUploads = TexturePendingUploads,
            WaterDraws = WaterDraws,
            WaterPipeline = WaterPipeline,
            WaterTechnique = WaterTechnique,
            WaterMapPaths = WaterMapPaths.ToArray(),
            WaterMapRoles = WaterMapRoles.ToArray(),
            WaterMapResolved = WaterMapResolved.ToArray(),
            WaterAnimationFrame = WaterAnimationFrame,
            WaterAnimationFps = WaterAnimationFps,
            WaterAnimationSeconds = WaterAnimationSeconds,
            WaterNoisePrepassUsed = WaterNoisePrepassUsed,
            WaterTelemetryUnavailableReason = WaterTelemetryUnavailableReason,
            WaterStreamEntries = WaterStreamEntries,
            WaterStreamRuns = WaterStreamRuns,
            WaterStreamNoisePrepasses = WaterStreamNoisePrepasses,
            WaterNoiseSlotOverflows = WaterNoiseSlotOverflows,
            WaterStreamDroppedEntries = WaterStreamDroppedEntries,
            WireframeDraws = WireframeDraws,
            CpuFrameMilliseconds = CpuFrameMilliseconds,
            StateSetupMilliseconds = StateSetupMilliseconds,
            VisibleGatherMilliseconds = VisibleGatherMilliseconds,
            VisibleSortMilliseconds = VisibleSortMilliseconds,
            DrawLoopMilliseconds = DrawLoopMilliseconds,
            MeshBuildUploadMilliseconds = MeshBuildUploadMilliseconds,
            NeighborPreUploadMilliseconds = NeighborPreUploadMilliseconds,
            QuadrantDrawMilliseconds = QuadrantDrawMilliseconds,
            InstanceBuildMilliseconds = InstanceBuildMilliseconds,
            GpuUploadMilliseconds = GpuUploadMilliseconds,
            DrawCallMilliseconds = DrawCallMilliseconds,
            ResourceResizeMilliseconds = ResourceResizeMilliseconds,
            ReferenceCellsVisited = ReferenceCellsVisited,
            ReferenceCandidates = ReferenceCandidates,
            ReferenceCulled = ReferenceCulled,
            ReferenceCullCacheHit = ReferenceCullCacheHit,
            ReferenceBatchesReused = ReferenceBatchesReused,
            ReferenceBatchReuseBlocker = ReferenceBatchReuseBlocker,
            ReferenceMeshMissing = ReferenceMeshMissing,
            ReferenceTexturePending = ReferenceTexturePending,
            ReferenceDrawn = ReferenceDrawn,
            ReferenceSubmeshDraws = ReferenceSubmeshDraws,
            ReferenceSrvBinds = ReferenceSrvBinds,
            ReferenceCullModeFlips = ReferenceCullModeFlips,
            ReferenceMeshCacheMisses = ReferenceMeshCacheMisses,
            ReferenceDecodeRequests = ReferenceDecodeRequests,
            ReferenceQueuedDecodes = ReferenceQueuedDecodes,
            ReferenceDecodeStarts = ReferenceDecodeStarts,
            ReferenceActiveDecodes = ReferenceActiveDecodes,
            ReferenceCpuDecodedMeshCacheHits = ReferenceCpuDecodedMeshCacheHits,
            ReferenceCpuDecodedMeshCacheMisses = ReferenceCpuDecodedMeshCacheMisses,
            ReferenceCpuDecodedMeshNegativeHits = ReferenceCpuDecodedMeshNegativeHits,
            ReferenceGpuUploads = ReferenceGpuUploads,
            ReferenceUploadByteBudgetDeferrals = ReferenceUploadByteBudgetDeferrals,
            ReferenceCompressedTextureUploads = ReferenceCompressedTextureUploads,
            ReferenceRgbaTextureUploads = ReferenceRgbaTextureUploads,
            ReferenceTextureQueuedResolves = ReferenceTextureQueuedResolves,
            ReferenceTextureActiveResolves = ReferenceTextureActiveResolves,
            ReferenceTexturePendingResolves = ReferenceTexturePendingResolves,
            ReferenceTexturePendingUploads = ReferenceTexturePendingUploads,
            ReferenceBatches = ReferenceBatches,
            ReferenceInstances = ReferenceInstances,
            ReferenceInstancedDraws = ReferenceInstancedDraws,
            ReferenceBlendedDraws = ReferenceBlendedDraws,
            ReferenceFnvSls1009Draws = ReferenceFnvSls1009Draws,
            ReferenceFnvSls1009Instances = ReferenceFnvSls1009Instances,
            ReferenceFnvSls1013Draws = ReferenceFnvSls1013Draws,
            ReferenceFnvSls1013Instances = ReferenceFnvSls1013Instances,
            ReferencePlacedLightCount = ReferencePlacedLightCount,
            ReferenceFnvClassicBasicLightingEnabled = ReferenceFnvClassicBasicLightingEnabled,
            ReferenceFnvClassicBasicFallbackDraws = ReferenceFnvClassicBasicFallbackDraws,
            ReferenceFnvClassicBasicFallbackInstances = ReferenceFnvClassicBasicFallbackInstances,
            ReferenceFnvClassicBasicFallbackReason = ReferenceFnvClassicBasicFallbackReason,
            ReferenceFnvActiveAdtBaseDraws = ReferenceFnvActiveAdtBaseDraws,
            ReferenceFnvActiveAdtBaseInstances = ReferenceFnvActiveAdtBaseInstances,
            ReferenceFnvActiveAdtBaseVertexColorDraws = ReferenceFnvActiveAdtBaseVertexColorDraws,
            ReferenceFnvActiveAdtBaseVertexColorInstances = ReferenceFnvActiveAdtBaseVertexColorInstances,
            ReferenceFnvActiveAdtBaseEnabled = ReferenceFnvActiveAdtBaseEnabled,
            ReferenceFnvActiveAdtBaseFallbackDraws = ReferenceFnvActiveAdtBaseFallbackDraws,
            ReferenceFnvActiveAdtBaseFallbackInstances = ReferenceFnvActiveAdtBaseFallbackInstances,
            ReferenceFnvActiveAdtBaseFallbackReason = ReferenceFnvActiveAdtBaseFallbackReason,
            ReferenceSceneDepthBlendedDraws = ReferenceSceneDepthBlendedDraws,
            ReferenceSoftParticleDraws = ReferenceSoftParticleDraws,
            ReferenceSoftParticleAuthoredDraws = ReferenceSoftParticleAuthoredDraws,
            ReferenceSoftParticleFallbackDraws = ReferenceSoftParticleFallbackDraws,
            ReferenceSoftParticleDepthSampleCount = ReferenceSoftParticleDepthSampleCount,
            ReferenceMaterialAlphaControllerDraws = ReferenceMaterialAlphaControllerDraws,
            ReferenceMaterialAlphaControllerMinimum = ReferenceMaterialAlphaControllerMinimum,
            ReferenceMaterialAlphaControllerMaximum = ReferenceMaterialAlphaControllerMaximum,
            ReferenceLiveParticleOwners = ReferenceLiveParticleOwners,
            ReferenceLiveParticleParticles = ReferenceLiveParticleParticles,
            ReferenceLiveParticleDraws = ReferenceLiveParticleDraws,
            ReferenceLiveParticleFallbacks = ReferenceLiveParticleFallbacks,
            ReferenceLiveParticleUploadBytes = ReferenceLiveParticleUploadBytes,
            ReferenceLiveParticleUvFrame = ReferenceLiveParticleUvFrame,
            ReferenceLiveParticleAtlasFrameCount = ReferenceLiveParticleAtlasFrameCount,
            ReferenceLiveParticleAuthoredCapacity = ReferenceLiveParticleAuthoredCapacity,
            ReferenceParticleRenderedCount = ReferenceParticleRenderedCount,
            ReferenceParticleSystems = ReferenceParticleSystems.ToArray(),
            ReferenceSpeedTreeWindStrength = ReferenceSpeedTreeWindStrength,
            ReferenceSpeedTreeRockAmount = ReferenceSpeedTreeRockAmount,
            ReferenceSpeedTreeRockPhase = ReferenceSpeedTreeRockPhase,
            ReferenceSpeedTreeRustleAmount = ReferenceSpeedTreeRustleAmount,
            ReferenceSpeedTreeRustlePhase = ReferenceSpeedTreeRustlePhase,
            ReferenceSpeedTreeAnimationSeconds = ReferenceSpeedTreeAnimationSeconds,
            ReferenceSpeedTreeRuntimeLodEnabled = ReferenceSpeedTreeRuntimeLodEnabled,
            ReferenceSpeedTreeBranchInstances = ReferenceSpeedTreeBranchInstances,
            ReferenceSpeedTreeLeafInstances = ReferenceSpeedTreeLeafInstances,
            ReferenceSpeedTreeBillboardInstances = ReferenceSpeedTreeBillboardInstances,
            ReferenceSpeedTreeMinimumLod = ReferenceSpeedTreeMinimumLod,
            ReferenceSpeedTreeMaximumLod = ReferenceSpeedTreeMaximumLod,
            ReferenceTallGrassWindSupported = ReferenceTallGrassWindSupported,
            ReferenceTallGrassAnimationsEnabled = ReferenceTallGrassAnimationsEnabled,
            ReferenceTallGrassNormalizedStrength = ReferenceTallGrassNormalizedStrength,
            ReferenceTallGrassMagnitudeWorldUnits = ReferenceTallGrassMagnitudeWorldUnits,
            ReferenceTallGrassDirectionX = ReferenceTallGrassDirectionX,
            ReferenceTallGrassDirectionY = ReferenceTallGrassDirectionY,
            ReferenceTallGrassAnimationSeconds = ReferenceTallGrassAnimationSeconds,
            ReferenceTallGrassInstancedDraws = ReferenceTallGrassInstancedDraws,
            ReferenceTallGrassInstancedInstances = ReferenceTallGrassInstancedInstances,
            ReferenceTallGrassDirectDraws = ReferenceTallGrassDirectDraws,
            ReferenceTallGrassDirectInstances = ReferenceTallGrassDirectInstances,
            ReferenceTallGrassShadowDraws = ReferenceTallGrassShadowDraws,
            ReferenceTallGrassShadowInstances = ReferenceTallGrassShadowInstances,
            ReferenceTallGrassWaveMultiplierMinimum = ReferenceTallGrassWaveMultiplierMinimum,
            ReferenceTallGrassWaveMultiplierMaximum = ReferenceTallGrassWaveMultiplierMaximum,
            ReferenceTallGrassWaveMultiplierDistinctCount = ReferenceTallGrassWaveMultiplierDistinctCount,
            ReferenceTallGrassTemporalPhaseRadiansMinimum = ReferenceTallGrassTemporalPhaseRadiansMinimum,
            ReferenceTallGrassTemporalPhaseRadiansMaximum = ReferenceTallGrassTemporalPhaseRadiansMaximum,
            ReferenceStateSetupMilliseconds = ReferenceStateSetupMilliseconds,
            ReferenceCullMilliseconds = ReferenceCullMilliseconds,
            ReferenceMeshUploadMilliseconds = ReferenceMeshUploadMilliseconds,
            ReferenceCbUpdateMilliseconds = ReferenceCbUpdateMilliseconds,
            ReferenceSrvBindMilliseconds = ReferenceSrvBindMilliseconds,
            ReferenceDrawCallMilliseconds = ReferenceDrawCallMilliseconds
        };
    }
}
