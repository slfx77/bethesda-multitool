namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Outcome of <see cref="DataFolderResolver.Resolve"/>.
/// </summary>
internal sealed record DataFolderResolution
{
    public required AssetResolutionKind Kind { get; init; }

    /// <summary>Source bytes location (null when <see cref="Kind"/> is <see cref="AssetResolutionKind.Missing"/> or <see cref="AssetResolutionKind.AlreadyInBaseline"/>).</summary>
    public AssetSource? Source { get; init; }

    /// <summary>The resolved (possibly different from requested) normalized path. Null when missing.</summary>
    public string? ResolvedPath { get; init; }

    /// <summary>Index of the secondary folder that provided the bytes. -1 if N/A.</summary>
    public int SourceFolderIndex { get; init; } = -1;

    /// <summary>For fuzzy matches, how many path tokens matched from the right.</summary>
    public int FuzzySuffixTokens { get; init; }
}

/// <summary>
///     Resolves requested asset paths against a baseline (FNV PC Data) plus an ordered
///     list of secondary data folders. Resolution strategy:
///
///     <list type="number">
///         <item><description>Check the baseline for exact match. If present, return <c>AlreadyInBaseline</c> — the FNV runtime can find this asset on its own; don't pack it.</description></item>
///         <item><description>Walk secondaries in priority order. First exact match wins (<c>ResolvedExact</c>).</description></item>
///         <item><description>If no exact match anywhere, gather every basename-matching candidate across all secondaries. If 1 candidate, return it (<c>ResolvedFuzzy</c>). If multiple, score each by the longest path-token suffix shared with the requested path; pick the highest-scoring candidate; ties broken by folder priority.</description></item>
///         <item><description>If still nothing, return <c>Missing</c>.</description></item>
///     </list>
/// </summary>
internal sealed class DataFolderResolver
{
    private readonly DataFolderIndex _baseline;
    private readonly IReadOnlyList<DataFolderIndex> _secondaries;

    public DataFolderResolver(DataFolderIndex baseline, IReadOnlyList<DataFolderIndex> secondaries)
    {
        _baseline = baseline;
        _secondaries = secondaries;
    }

    /// <summary>Resolve one requested path. The path must already be normalized.</summary>
    public DataFolderResolution Resolve(string normalizedPath)
    {
        if (_baseline.TryResolveExact(normalizedPath, out _))
        {
            return new DataFolderResolution
            {
                Kind = AssetResolutionKind.AlreadyInBaseline,
                ResolvedPath = normalizedPath
            };
        }

        // Exact-match walk through secondaries in priority order.
        for (var i = 0; i < _secondaries.Count; i++)
        {
            if (_secondaries[i].TryResolveExact(normalizedPath, out var source))
            {
                return new DataFolderResolution
                {
                    Kind = AssetResolutionKind.ResolvedExact,
                    Source = source,
                    ResolvedPath = normalizedPath,
                    SourceFolderIndex = i
                };
            }
        }

        // Fuzzy fallback: collect all basename-equal candidates across every secondary.
        var basename = Path.GetFileName(normalizedPath);
        if (string.IsNullOrEmpty(basename))
        {
            return Miss(normalizedPath);
        }

        var requestedTokens = TokenizePath(normalizedPath);

        FuzzyCandidate? best = null;
        for (var i = 0; i < _secondaries.Count; i++)
        {
            var candidates = _secondaries[i].EnumerateByBasename(basename);
            foreach (var candidate in candidates)
            {
                var candidateTokens = TokenizePath(candidate.NormalizedPath);
                var suffixScore = CountMatchingSuffixTokens(requestedTokens, candidateTokens);

                if (best is null ||
                    suffixScore > best.SuffixScore ||
                    (suffixScore == best.SuffixScore && i < best.FolderIndex))
                {
                    best = new FuzzyCandidate
                    {
                        Source = candidate,
                        FolderIndex = i,
                        SuffixScore = suffixScore
                    };
                }
            }
        }

        if (best is null)
        {
            return Miss(normalizedPath);
        }

        return new DataFolderResolution
        {
            Kind = AssetResolutionKind.ResolvedFuzzy,
            Source = best.Source,
            ResolvedPath = best.Source.NormalizedPath,
            SourceFolderIndex = best.FolderIndex,
            FuzzySuffixTokens = best.SuffixScore
        };
    }

    private static DataFolderResolution Miss(string normalizedPath) => new()
    {
        Kind = AssetResolutionKind.Missing,
        ResolvedPath = normalizedPath
    };

    /// <summary>
    ///     Split a normalized path on backslashes. Returns the path components from left to
    ///     right; the last token is the filename.
    /// </summary>
    private static string[] TokenizePath(string normalized)
    {
        return normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    ///     Count how many trailing tokens of <paramref name="a"/> and <paramref name="b"/>
    ///     match (case-insensitive). The filename itself is always part of the suffix when
    ///     this resolver is called, so the minimum return is 1.
    /// </summary>
    private static int CountMatchingSuffixTokens(string[] a, string[] b)
    {
        var matches = 0;
        var i = a.Length - 1;
        var j = b.Length - 1;
        while (i >= 0 && j >= 0 &&
               string.Equals(a[i], b[j], StringComparison.OrdinalIgnoreCase))
        {
            matches++;
            i--;
            j--;
        }

        return matches;
    }

    private sealed record FuzzyCandidate
    {
        public required AssetSource Source { get; init; }
        public required int FolderIndex { get; init; }
        public required int SuffixScore { get; init; }
    }
}
