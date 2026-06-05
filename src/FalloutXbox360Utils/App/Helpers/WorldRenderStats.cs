namespace FalloutXbox360Utils;

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
    internal int WaterDraws { get; set; }
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

    // v3 Phase 3 — ReferenceRenderer per-stage profiling. Counters answer "what is the
    // visible workload?" Times answer "where is the CPU going?" The split between
    // ReferenceCbUpdate / ReferenceSrvBind / ReferenceDrawCall identifies whether
    // Pass 3 (instancing) is the right next move or whether the bottleneck is elsewhere
    // (mesh cache thrash on motion, texture upload, GC pressure, etc.).
    internal int ReferenceCellsVisited { get; set; }
    internal int ReferenceCandidates { get; set; }       // sum of PlacedObjects across visited cells
    internal int ReferenceCulled { get; set; }           // dropped by per-REFR cylinder test
    internal int ReferenceMeshMissing { get; set; }      // GetOrUpload returned null this frame
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
    internal int ReferenceCompressedTextureUploads { get; set; }
    internal int ReferenceRgbaTextureUploads { get; set; }
    internal int ReferenceBatches { get; set; }
    internal int ReferenceInstances { get; set; }
    internal int ReferenceInstancedDraws { get; set; }
    internal int ReferenceBlendedDraws { get; set; }
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
        WaterDraws = 0;
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
        ReferenceCompressedTextureUploads = 0;
        ReferenceRgbaTextureUploads = 0;
        ReferenceBatches = 0;
        ReferenceInstances = 0;
        ReferenceInstancedDraws = 0;
        ReferenceBlendedDraws = 0;
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
        WaterDraws = WaterDraws,
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
        ReferenceCompressedTextureUploads = ReferenceCompressedTextureUploads,
        ReferenceRgbaTextureUploads = ReferenceRgbaTextureUploads,
        ReferenceBatches = ReferenceBatches,
        ReferenceInstances = ReferenceInstances,
        ReferenceInstancedDraws = ReferenceInstancedDraws,
        ReferenceBlendedDraws = ReferenceBlendedDraws,
        ReferenceStateSetupMilliseconds = ReferenceStateSetupMilliseconds,
        ReferenceCullMilliseconds = ReferenceCullMilliseconds,
        ReferenceMeshUploadMilliseconds = ReferenceMeshUploadMilliseconds,
        ReferenceCbUpdateMilliseconds = ReferenceCbUpdateMilliseconds,
        ReferenceSrvBindMilliseconds = ReferenceSrvBindMilliseconds,
        ReferenceDrawCallMilliseconds = ReferenceDrawCallMilliseconds
    };
}
