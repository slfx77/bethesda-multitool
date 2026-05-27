namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     How a single asset path was resolved during packing.
/// </summary>
public enum AssetResolutionKind
{
    /// <summary>Path was already available in the baseline FNV Data folder; not packed.</summary>
    AlreadyInBaseline,

    /// <summary>Exact path match found in a secondary folder; packed verbatim.</summary>
    ResolvedExact,

    /// <summary>
    ///     Exact path missed but the basename matched in a secondary folder.
    ///     If multiple basename candidates exist, the one with the longest matching path
    ///     suffix wins.
    /// </summary>
    ResolvedFuzzy,

    /// <summary>Resolved + needed 360→PC byte conversion (DDX/XMA/NIF endian).</summary>
    ResolvedExactConverted,

    /// <summary>Resolved fuzzy + needed 360→PC byte conversion.</summary>
    ResolvedFuzzyConverted,

    /// <summary>Not found in baseline or any secondary folder.</summary>
    Missing,

    /// <summary>360→PC conversion was attempted but failed; original bytes packed as fallback.</summary>
    ConversionFailed
}

/// <summary>
///     Per-asset resolution outcome.
/// </summary>
public sealed record AssetResolution
{
    public required string RequestedPath { get; init; }
    public required AssetResolutionKind Kind { get; init; }
    public string? ResolvedPath { get; init; }
    public string? SourceFolder { get; init; }

    /// <summary>For fuzzy matches, the number of path tokens that matched from the right.</summary>
    public int FuzzySuffixTokens { get; init; }

    /// <summary>Set when <see cref="Kind" /> is <see cref="AssetResolutionKind.ConversionFailed" />.</summary>
    public string? ConversionError { get; init; }
}

/// <summary>
///     Aggregate stats for an asset packing run.
/// </summary>
public sealed record AssetPackingStats
{
    public int TotalPathsScanned { get; init; }
    public int AlreadyInBaseline { get; init; }
    public int ResolvedExact { get; init; }
    public int ResolvedFuzzy { get; init; }
    public int Converted360 { get; init; }
    public int ConversionFailed { get; init; }
    public int Missing { get; init; }
    public int PackedAssetCount { get; init; }
    public long OutputBsaSizeBytes { get; init; }
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
///     Outcome of an asset packing run.
/// </summary>
public sealed record AssetPackingResult
{
    public required bool Success { get; init; }
    public required AssetPackingStats Stats { get; init; }
    public string? OutputPath { get; init; }
    public IReadOnlyList<string> OutputPaths { get; init; } = [];
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<AssetResolution> Resolutions { get; init; } = [];
}
