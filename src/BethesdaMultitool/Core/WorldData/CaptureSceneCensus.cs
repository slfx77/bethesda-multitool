namespace BethesdaMultitool;

/// <summary>
///     One frame's demand-set census for the capture completion gate. "Everything that should be
///     loaded is loaded" cannot be read off a single counter: quiescence ("nothing is loading right
///     now") passes on the gap between streaming waves, and the demand set itself EXPANDS while
///     streaming (a decoded mesh's real radius admits more refs; an arriving texture un-withholds
///     submeshes that then stream their own textures). Completion is therefore a FIXPOINT: a census
///     whose pending-work terms are all zero AND whose demand-set terms repeat unchanged across
///     consecutive samples. The caller's timeout remains only as a backstop for wedged states —
///     <see cref="DescribeDirt" /> turns a timeout into a diagnosis of which terms never settled.
/// </summary>
internal readonly record struct CaptureSceneCensus(
    // Pending-work terms — all zero on a clean frame.
    int QueuedDecodes,
    int ActiveDecodes,
    int GpuUploads,
    int TexturePendingResolves,
    int TexturePendingUploads,
    int TexturesWithheld,
    int TerrainUploads,
    int TerrainTextureResolves,
    int TerrainTextureUploads,
    int TerrainDrawsTruncated,
    // Demand-set terms — must be STABLE across consecutive clean frames.
    int ReferenceInstances,
    int ReferenceDrawn,
    int ReferenceSubmeshDraws,
    int ReferenceMeshMissing,
    int TerrainDraws,
    int WaterDraws)
{
    /// <summary>No pending work this frame, and no terrain draw was silently truncated.</summary>
    public bool IsClean =>
        QueuedDecodes == 0 && ActiveDecodes == 0 && GpuUploads == 0
        && TexturePendingResolves == 0 && TexturePendingUploads == 0 && TexturesWithheld == 0
        && TerrainUploads == 0 && TerrainTextureResolves == 0 && TerrainTextureUploads == 0
        && TerrainDrawsTruncated == 0;

    /// <summary>
    ///     Builds the census from the layers rendered this frame. A null stats parameter means that
    ///     layer is disabled — it contributes nothing (same contract as
    ///     <see cref="StreamingQuiescence.IsQuiesced" />).
    /// </summary>
    public static CaptureSceneCensus From(WorldRenderStats? references, WorldRenderStats? terrain) => new(
        QueuedDecodes: references?.ReferenceQueuedDecodes ?? 0,
        ActiveDecodes: references?.ReferenceActiveDecodes ?? 0,
        GpuUploads: references?.ReferenceGpuUploads ?? 0,
        TexturePendingResolves: references?.ReferenceTexturePendingResolves ?? 0,
        TexturePendingUploads: references?.ReferenceTexturePendingUploads ?? 0,
        TexturesWithheld: references?.ReferenceTexturePending ?? 0,
        TerrainUploads: terrain?.NewUploads ?? 0,
        TerrainTextureResolves: terrain?.TexturePendingResolves ?? 0,
        TerrainTextureUploads: terrain?.TexturePendingUploads ?? 0,
        TerrainDrawsTruncated: terrain?.TerrainDrawsTruncated ?? 0,
        ReferenceInstances: references?.ReferenceInstances ?? 0,
        ReferenceDrawn: references?.ReferenceDrawn ?? 0,
        ReferenceSubmeshDraws: references?.ReferenceSubmeshDraws ?? 0,
        ReferenceMeshMissing: references?.ReferenceMeshMissing ?? 0,
        TerrainDraws: terrain?.TerrainDraws ?? 0,
        WaterDraws: (references ?? terrain)?.WaterDraws ?? 0);

    /// <summary>
    ///     Names every term keeping this census from counting as complete relative to
    ///     <paramref name="previous" /> — the dirty pending terms and the moving demand terms.
    ///     Empty when this census is clean and unchanged.
    /// </summary>
    public string DescribeDirt(in CaptureSceneCensus previous)
    {
        var parts = new List<string>(4);
        void Pending(string name, int v) { if (v != 0) parts.Add($"{name}={v}"); }
        Pending(nameof(QueuedDecodes), QueuedDecodes);
        Pending(nameof(ActiveDecodes), ActiveDecodes);
        Pending(nameof(GpuUploads), GpuUploads);
        Pending(nameof(TexturePendingResolves), TexturePendingResolves);
        Pending(nameof(TexturePendingUploads), TexturePendingUploads);
        Pending(nameof(TexturesWithheld), TexturesWithheld);
        Pending(nameof(TerrainUploads), TerrainUploads);
        Pending(nameof(TerrainTextureResolves), TerrainTextureResolves);
        Pending(nameof(TerrainTextureUploads), TerrainTextureUploads);
        Pending(nameof(TerrainDrawsTruncated), TerrainDrawsTruncated);
        void Moved(string name, int now, int was) { if (now != was) parts.Add($"{name} {was}->{now}"); }
        Moved(nameof(ReferenceInstances), ReferenceInstances, previous.ReferenceInstances);
        Moved(nameof(ReferenceDrawn), ReferenceDrawn, previous.ReferenceDrawn);
        Moved(nameof(ReferenceSubmeshDraws), ReferenceSubmeshDraws, previous.ReferenceSubmeshDraws);
        Moved(nameof(ReferenceMeshMissing), ReferenceMeshMissing, previous.ReferenceMeshMissing);
        Moved(nameof(TerrainDraws), TerrainDraws, previous.TerrainDraws);
        Moved(nameof(WaterDraws), WaterDraws, previous.WaterDraws);
        return string.Join(" ", parts);
    }
}
