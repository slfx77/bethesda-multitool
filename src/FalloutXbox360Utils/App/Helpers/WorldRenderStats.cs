namespace FalloutXbox360Utils;

internal sealed class WorldRenderStats
{
    internal int VisibleCandidates { get; set; }
    internal int TerrainDraws { get; set; }
    internal int NewUploads { get; set; }
    internal int TextureCacheMisses { get; set; }
    internal int OpacityCacheMisses { get; set; }
    internal int WaterDraws { get; set; }
    internal double CpuFrameMilliseconds { get; set; }

    internal void Reset()
    {
        VisibleCandidates = 0;
        TerrainDraws = 0;
        NewUploads = 0;
        TextureCacheMisses = 0;
        OpacityCacheMisses = 0;
        WaterDraws = 0;
        CpuFrameMilliseconds = 0;
    }

    internal WorldRenderStats Snapshot() => new()
    {
        VisibleCandidates = VisibleCandidates,
        TerrainDraws = TerrainDraws,
        NewUploads = NewUploads,
        TextureCacheMisses = TextureCacheMisses,
        OpacityCacheMisses = OpacityCacheMisses,
        WaterDraws = WaterDraws,
        CpuFrameMilliseconds = CpuFrameMilliseconds
    };
}
