namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>All exact-name occurrences in the selected dictionary; no candidate is silently chosen.</summary>
/// <param name="Name">The caller's unnormalized texture name.</param>
/// <param name="Occurrences">Original occurrences in authored raster order.</param>
/// <param name="HasUnresolvedNames">Whether original names remain uninterpreted in this selected dictionary.</param>
internal sealed record RwPspTextureCandidates(string Name, IReadOnlyList<RwPspTextureDictionaryRaster> Occurrences, bool HasUnresolvedNames)
{
    /// <summary>Conclusive cardinality requires complete names; no result establishes decode, material or rendering admission.</summary>
    public RwPspTextureCandidateStatus Status => HasUnresolvedNames
        ? RwPspTextureCandidateStatus.IncompleteNames
        : Occurrences.Count switch
    {
        0 => RwPspTextureCandidateStatus.Missing,
        1 => RwPspTextureCandidateStatus.Unique,
        _ => RwPspTextureCandidateStatus.Ambiguous
    };
}
