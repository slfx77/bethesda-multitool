namespace FalloutXbox360Utils;

internal sealed class WorldRenderStats
{
    internal int VisibleCandidates { get; set; }
    internal int TerrainDraws { get; set; }
    internal int TerrainQuadrantDraws { get; set; }
    internal int NewUploads { get; set; }
    internal int NewPreUploads { get; set; }
    internal int TextureCacheMisses { get; set; }
    internal int OpacityCacheMisses { get; set; }
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

    internal void Reset()
    {
        VisibleCandidates = 0;
        TerrainDraws = 0;
        TerrainQuadrantDraws = 0;
        NewUploads = 0;
        NewPreUploads = 0;
        TextureCacheMisses = 0;
        OpacityCacheMisses = 0;
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
    }

    internal WorldRenderStats Snapshot() => new()
    {
        VisibleCandidates = VisibleCandidates,
        TerrainDraws = TerrainDraws,
        TerrainQuadrantDraws = TerrainQuadrantDraws,
        NewUploads = NewUploads,
        NewPreUploads = NewPreUploads,
        TextureCacheMisses = TextureCacheMisses,
        OpacityCacheMisses = OpacityCacheMisses,
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
        ResourceResizeMilliseconds = ResourceResizeMilliseconds
    };
}
