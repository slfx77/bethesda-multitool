namespace BethesdaMultitool;

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
    internal int ReferenceCandidates { get; set; }       // sum of PlacedObjects across visited cells
    internal int ReferenceCulled { get; set; }           // dropped by per-REFR cylinder test
    internal int ReferenceMeshMissing { get; set; }      // GetOrUpload returned null this frame
    internal int ReferenceTexturePending { get; set; }   // mesh ready, but at least one texture still streaming
    internal int ReferenceDrawn { get; set; }            // REFRs that issued ≥1 submesh draw
    internal int ReferenceSubmeshDraws { get; set; }     // total DrawIndexed calls
    internal int ReferenceSrvBinds { get; set; }         // distinct PSSetShaderResource calls (post BindSrvIfChanged dedup)
    internal int ReferenceCullModeFlips { get; set; }    // RSSetState calls (post SetCullModeIfChanged dedup)
    internal int ReferenceMeshCacheMisses { get; set; }  // NIF parse + GPU upload events this frame
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
    internal double ReferenceStateSetupMilliseconds { get; set; }
    internal double ReferenceCullMilliseconds { get; set; }   // per-REFR cylinder cull + placement-list walk
    internal double ReferenceMeshUploadMilliseconds { get; set; } // cache-miss NIF parse + GPU upload
    internal double ReferenceCbUpdateMilliseconds { get; set; }   // per-submesh Map(WriteDiscard) on _perDrawCb
    internal double ReferenceSrvBindMilliseconds { get; set; }    // BindSrvIfChanged + SetCullModeIfChanged
    internal double ReferenceDrawCallMilliseconds { get; set; }   // IASetVB/IB + DrawIndexed

    internal void Reset()
    {
        VisibleCandidates = 0;
        TerrainDraws = 0;
        TerrainQuadrantDraws = 0;
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
        ReferenceStateSetupMilliseconds = 0;
        ReferenceCullMilliseconds = 0;
        ReferenceMeshUploadMilliseconds = 0;
        ReferenceCbUpdateMilliseconds = 0;
        ReferenceSrvBindMilliseconds = 0;
        ReferenceDrawCallMilliseconds = 0;
    }

    internal WorldRenderStats Snapshot() => new()
    {
        VisibleCandidates = VisibleCandidates,
        TerrainDraws = TerrainDraws,
        TerrainQuadrantDraws = TerrainQuadrantDraws,
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
        ReferenceStateSetupMilliseconds = ReferenceStateSetupMilliseconds,
        ReferenceCullMilliseconds = ReferenceCullMilliseconds,
        ReferenceMeshUploadMilliseconds = ReferenceMeshUploadMilliseconds,
        ReferenceCbUpdateMilliseconds = ReferenceCbUpdateMilliseconds,
        ReferenceSrvBindMilliseconds = ReferenceSrvBindMilliseconds,
        ReferenceDrawCallMilliseconds = ReferenceDrawCallMilliseconds
    };
}
