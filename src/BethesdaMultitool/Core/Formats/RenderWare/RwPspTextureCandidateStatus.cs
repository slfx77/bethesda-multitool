namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>Exact-name candidates within one caller-selected dictionary, independent of pixel decoding support.</summary>
internal enum RwPspTextureCandidateStatus
{
    /// <summary>No exact byte-preserving name matches in this dictionary.</summary>
    Missing,
    /// <summary>One occurrence matches; its pixel and material semantics may still be unsupported.</summary>
    Unique,
    /// <summary>Multiple original occurrences match; none is elected by ordering.</summary>
    Ambiguous,
    /// <summary>At least one original name is unreadable; retained known candidates cannot establish complete cardinality.</summary>
    IncompleteNames
}
