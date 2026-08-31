namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     Pure decision boundary for keeping the published reference batches visible while a refresh
///     is rebuilt incrementally. Every structural invariant is explicit so a soft first blocker
///     cannot mask a later origin, eviction, cull-epoch, or transparency-routing change.
/// </summary>
internal static class ReferenceBatchBuildPolicy
{
    /// <summary>
    ///     Returns true when a published snapshot has no unresolved work that a timer-driven
    ///     rebuild could advance. Hard identity changes (cull epoch, origin, eviction, routing)
    ///     remain independent invalidators; this only suppresses the periodic refresh of an
    ///     otherwise identical snapshot.
    /// </summary>
    public static bool CanSkipPeriodicRefresh(
        bool buildQuiesced,
        bool hasMissingMeshes,
        bool missingMeshesStalled,
        int pendingMaterializationRetries)
    {
        return buildQuiesced
               && pendingMaterializationRetries == 0
               && (!hasMissingMeshes || missingMeshesStalled);
    }

    public static bool CanAmortize(
        bool streamingThrottled,
        bool refreshOnlyBlocker,
        bool cullCacheHit,
        bool publishedBuildValid,
        bool cullEpochMatches,
        bool renderOriginMatches,
        bool evictionGenerationMatches,
        bool streamRoutingMatches)
    {
        return streamingThrottled
               && refreshOnlyBlocker
               && cullCacheHit
               && publishedBuildValid
               && cullEpochMatches
               && renderOriginMatches
               && evictionGenerationMatches
               && streamRoutingMatches;
    }
}
