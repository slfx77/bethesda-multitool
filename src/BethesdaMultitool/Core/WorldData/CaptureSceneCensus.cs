namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     One frame's demand-set census for the capture completion gate. "Everything that should be
///     loaded is loaded" cannot be read off a single counter: quiescence ("nothing is loading right
///     now") passes on the gap between streaming waves, and the demand set itself EXPANDS while
///     streaming (a decoded mesh's real radius admits more refs; an arriving texture un-withholds
///     submeshes that then stream their own textures). Completion is therefore a FIXPOINT: a census
///     whose pending-work terms are all zero AND whose demand-set terms repeat unchanged across
///     consecutive samples. An in-progress staged reference-batch sweep is pending work even when
///     its still-published demand counters are unchanged. The caller's timeout remains only as a
///     backstop for wedged states. Active texture resolves are counted separately from pending
///     resolves because a worker removes an item from the pending queue before finishing it; the
///     pending count can therefore reach zero while reference or terrain texture work is still
///     running —
///     <see cref="DescribeDirt" /> turns a timeout into a diagnosis of which terms never settled.
/// </summary>
internal readonly record struct CaptureSceneCensus(
    // Pending-work terms — all zero on a clean frame.
    bool ReferenceBatchBuildInProgress,
    bool ReferenceCullRefreshPending,
    int QueuedDecodes,
    int ActiveDecodes,
    int GpuUploads,
    int MeshMaterializationFailures,
    int MeshMaterializationRetriesPending,
    int TextureActiveResolves,
    int TexturePendingResolves,
    int TexturePendingUploads,
    int TexturesWithheld,
    int TerrainUploads,
    int TerrainTextureActiveResolves,
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
    // Stable telemetry code shared with ReferenceRenderer12.BatchReuseBlocker.FrameCeiling. Keep
    // this here so the capture gate can recognize the expected maintenance sweep without taking a
    // dependency on the WINDOWS_GUI-only renderer implementation.
    internal const int FrameCeilingBatchBuildTriggerCode = 4;

    /// <summary>No pending work this frame, and no terrain draw was silently truncated.</summary>
    public bool IsClean =>
        !ReferenceBatchBuildInProgress
        && !ReferenceCullRefreshPending
        && QueuedDecodes == 0 && ActiveDecodes == 0 && GpuUploads == 0
        && MeshMaterializationFailures == 0 && MeshMaterializationRetriesPending == 0
        && TextureActiveResolves == 0 && TexturePendingResolves == 0
        && TexturePendingUploads == 0 && TexturesWithheld == 0
        && TerrainUploads == 0 && TerrainTextureActiveResolves == 0
        && TerrainTextureResolves == 0 && TerrainTextureUploads == 0
        && TerrainDrawsTruncated == 0;

    /// <summary>
    ///     True when the frame is clean, or when its only pending-work term is the expected
    ///     FrameCeiling reference-batch maintenance sweep. The latter is deliberately periodic
    ///     after settlement and must remain visible in the raw census counter without invalidating
    ///     an otherwise settled measurement window.
    /// </summary>
    public bool IsCleanOrFrameCeilingMaintenance(int referenceBatchBuildTrigger) =>
        IsClean
        || (ReferenceBatchBuildInProgress
            && referenceBatchBuildTrigger == FrameCeilingBatchBuildTriggerCode
            && (this with { ReferenceBatchBuildInProgress = false }).IsClean);

    /// <summary>
    ///     Builds the census from the layers rendered this frame. A null stats parameter means that
    ///     layer is disabled — it contributes nothing (same contract as
    ///     <see cref="StreamingQuiescence.IsQuiesced" />).
    /// </summary>
    public static CaptureSceneCensus From(WorldRenderStats? references, WorldRenderStats? terrain)
    {
        return new CaptureSceneCensus(
            references?.ReferenceBatchBuildInProgress ?? false,
            references?.ReferenceCullRefreshPending ?? false,
            references?.ReferenceQueuedDecodes ?? 0,
            references?.ReferenceActiveDecodes ?? 0,
            references?.ReferenceGpuUploads ?? 0,
            references?.ReferenceMeshMaterializationFailures ?? 0,
            references?.ReferenceMeshMaterializationRetriesPending ?? 0,
            references?.ReferenceTextureActiveResolves ?? 0,
            references?.ReferenceTexturePendingResolves ?? 0,
            references?.ReferenceTexturePendingUploads ?? 0,
            references?.ReferenceTexturePending ?? 0,
            terrain?.NewUploads ?? 0,
            terrain?.TextureActiveResolves ?? 0,
            terrain?.TexturePendingResolves ?? 0,
            terrain?.TexturePendingUploads ?? 0,
            terrain?.TerrainDrawsTruncated ?? 0,
            references?.ReferenceInstances ?? 0,
            references?.ReferenceDrawn ?? 0,
            references?.ReferenceSubmeshDraws ?? 0,
            references?.ReferenceMeshMissing ?? 0,
            terrain?.TerrainDraws ?? 0,
            (references ?? terrain)?.WaterDraws ?? 0);
    }

    /// <summary>
    ///     Names every term keeping this census from counting as complete relative to
    ///     <paramref name="previous" /> — the dirty pending terms and the moving demand terms.
    ///     Empty when this census is clean and unchanged.
    /// </summary>
    public string DescribeDirt(in CaptureSceneCensus previous)
    {
        var parts = new List<string>(4);

        if (ReferenceBatchBuildInProgress)
        {
            parts.Add($"{nameof(ReferenceBatchBuildInProgress)}=true");
        }
        if (ReferenceCullRefreshPending)
        {
            parts.Add($"{nameof(ReferenceCullRefreshPending)}=true");
        }

        void Pending(string name, int v)
        {
            if (v != 0) parts.Add($"{name}={v}");
        }

        Pending(nameof(QueuedDecodes), QueuedDecodes);
        Pending(nameof(ActiveDecodes), ActiveDecodes);
        Pending(nameof(GpuUploads), GpuUploads);
        Pending(nameof(MeshMaterializationFailures), MeshMaterializationFailures);
        Pending(nameof(MeshMaterializationRetriesPending), MeshMaterializationRetriesPending);
        Pending(nameof(TextureActiveResolves), TextureActiveResolves);
        Pending(nameof(TexturePendingResolves), TexturePendingResolves);
        Pending(nameof(TexturePendingUploads), TexturePendingUploads);
        Pending(nameof(TexturesWithheld), TexturesWithheld);
        Pending(nameof(TerrainUploads), TerrainUploads);
        Pending(nameof(TerrainTextureActiveResolves), TerrainTextureActiveResolves);
        Pending(nameof(TerrainTextureResolves), TerrainTextureResolves);
        Pending(nameof(TerrainTextureUploads), TerrainTextureUploads);
        Pending(nameof(TerrainDrawsTruncated), TerrainDrawsTruncated);

        void Moved(string name, int now, int was)
        {
            if (now != was) parts.Add($"{name} {was}->{now}");
        }

        Moved(nameof(ReferenceInstances), ReferenceInstances, previous.ReferenceInstances);
        Moved(nameof(ReferenceDrawn), ReferenceDrawn, previous.ReferenceDrawn);
        Moved(nameof(ReferenceSubmeshDraws), ReferenceSubmeshDraws, previous.ReferenceSubmeshDraws);
        Moved(nameof(ReferenceMeshMissing), ReferenceMeshMissing, previous.ReferenceMeshMissing);
        Moved(nameof(TerrainDraws), TerrainDraws, previous.TerrainDraws);
        Moved(nameof(WaterDraws), WaterDraws, previous.WaterDraws);
        return string.Join(" ", parts);
    }
}
