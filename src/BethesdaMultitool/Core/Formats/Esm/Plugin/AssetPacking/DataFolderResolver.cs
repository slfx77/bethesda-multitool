namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Outcome of <see cref="DataFolderResolver.Resolve" />.
/// </summary>
internal sealed record DataFolderResolution
{
    public required AssetResolutionKind Kind { get; init; }

    /// <summary>
    ///     Source bytes location (null when <see cref="Kind" /> is <see cref="AssetResolutionKind.Missing" /> or
    ///     <see cref="AssetResolutionKind.AlreadyInBaseline" />).
    /// </summary>
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
///     list of secondary data folders. Default strategy:
///     <list type="number">
///         <item>
///             <description>
///                 Check the baseline for exact match. If present, return <c>AlreadyInBaseline</c> — the FNV
///                 runtime can find this asset on its own; don't pack it.
///             </description>
///         </item>
///         <item>
///             <description>Walk secondaries in priority order. First exact match wins (<c>ResolvedExact</c>).</description>
///         </item>
///         <item>
///             <description>
///                 If no exact match anywhere, gather every basename-matching candidate across all secondaries.
///                 If 1 candidate, return it (<c>ResolvedFuzzy</c>). If multiple, score each by the longest path-token
///                 suffix shared with the requested path; pick the highest-scoring candidate; ties broken by folder
///                 priority.
///             </description>
///         </item>
///         <item>
///             <description>If still nothing, return <c>Missing</c>.</description>
///         </item>
///     </list>
///     When <c>overrideBaseline</c> is true (constructor arg), the order flips: secondaries
///     are consulted first (exact, then extension-swap) and the baseline becomes a fallback.
///     A secondary win in override mode returns <c>ResolvedExact</c>/<c>ResolvedFuzzy</c>
///     (so the bytes are packed and override the baseline at runtime), not
///     <c>AlreadyInBaseline</c>.
/// </summary>
internal sealed class DataFolderResolver
{
    private readonly DataFolderIndex _baseline;
    private readonly bool _overrideBaseline;
    private readonly IReadOnlyList<DataFolderIndex> _secondaries;
    private readonly bool _enableFuzzy;

    /// <summary>
    ///     Creates a resolver over a baseline data folder and ordered secondary folders;
    ///     <paramref name="overrideBaseline" /> lets secondaries replace baseline matches.
    ///     <paramref name="enableFuzzy" /> gates the renamed-asset fuzzy fallback cascade: it is
    ///     ON by default (the conversion pipeline relies on it to match prototype→final renames),
    ///     but callers that want exact-only resolution (e.g. the viewer browsing an ESM, or CLI
    ///     NPC asset loading) pass <c>false</c> so a missing path simply misses instead of
    ///     resolving to a similarly-named sibling. Exact + extension-swap resolution is unaffected.
    /// </summary>
    public DataFolderResolver(
        DataFolderIndex baseline,
        IReadOnlyList<DataFolderIndex> secondaries,
        bool overrideBaseline = false,
        bool enableFuzzy = true)
    {
        _baseline = baseline;
        _secondaries = secondaries;
        _overrideBaseline = overrideBaseline;
        _enableFuzzy = enableFuzzy;
    }

    /// <summary>Resolve one requested path. The path must already be normalized.</summary>
    public DataFolderResolution Resolve(string normalizedPath)
    {
        var head = TryResolveOrderedExactStrategies(normalizedPath);
        if (head is not null)
        {
            return head;
        }

        // Exact + extension-swap resolution above is always applied; the renamed-asset fuzzy
        // cascade below is opt-in (see the constructor's enableFuzzy parameter).
        if (!_enableFuzzy)
        {
            return Miss(normalizedPath);
        }

        // Fuzzy fallback: collect all basename-equal candidates across every secondary.
        var basename = Path.GetFileName(normalizedPath);
        if (string.IsNullOrEmpty(basename))
        {
            return Miss(normalizedPath);
        }

        var requestedTokens = TokenizePath(normalizedPath);

        var best = FindBestFuzzyCandidate(_secondaries, basename, requestedTokens, false);
        // When exact-basename match fails, try loose basename (case-folded with
        // spaces, underscores, dashes, apostrophes stripped). Catches renames between
        // prototype and final where only separator style or capitalization changed —
        // e.g. `monorailplatform.nif` ↔ `monorail_platform.nif`. Same suffix-token
        // scoring so directory context still breaks ties.
        if (best is null)
        {
            best = FindBestFuzzyCandidate(_secondaries, basename, requestedTokens, true);
        }

        // Also extend baseline against loose matches — many renames live in the user's
        // FNV PC Data and we'd rather take those for free than pack a duplicate. The
        // baseline resolution path is reported as <c>AlreadyInBaseline</c> so the BSA
        // skips packing it, but the rename rewriter still updates the record field.
        if (best is null)
        {
            var baselineLoose = FindBestFuzzyCandidate([_baseline], basename, requestedTokens, true);
            if (baselineLoose is not null)
            {
                return new DataFolderResolution
                {
                    Kind = AssetResolutionKind.ResolvedFuzzy,
                    Source = baselineLoose.Source,
                    ResolvedPath = baselineLoose.Source.NormalizedPath,
                    SourceFolderIndex = -1,
                    FuzzySuffixTokens = baselineLoose.SuffixScore
                };
            }
        }

        // NV-stripped loose match. The final FNV build occasionally removed the
        // <c>nv</c> namespace token from prototype filenames (e.g. `nv_slotmachine` ↔
        // `slotmachine`, `rockcanyonrubblepile05nv` ↔ `rockcanyonrubblepile05`). The
        // index has already stored candidates under both their original loose and
        // nv-stripped forms; here we also strip the request's `nv` affix when present
        // and look up the same loose-basename map. Guarded by min-length and tied to
        // the same suffix-token scoring so directory context still matters.
        if (best is null)
        {
            var requestNoNv = AssetPathRules.ComputeLooseBasenameWithoutNvAffix(basename);
            if (!string.IsNullOrEmpty(requestNoNv))
            {
                best = FindCandidateByLooseKey(_secondaries, requestNoNv, basename, requestedTokens);
                if (best is null)
                {
                    var baselineHit = FindCandidateByLooseKey(
                        [_baseline], requestNoNv, basename, requestedTokens);
                    if (baselineHit is not null)
                    {
                        return new DataFolderResolution
                        {
                            Kind = AssetResolutionKind.ResolvedFuzzy,
                            Source = baselineHit.Source,
                            ResolvedPath = baselineHit.Source.NormalizedPath,
                            SourceFolderIndex = -1,
                            FuzzySuffixTokens = baselineHit.SuffixScore
                        };
                    }
                }
            }
        }

        // Final fallback — directory-anchored substring-suffix match. Catches cases
        // where a final-build mesh kept the prototype's directory but acquired (or lost)
        // a short prefix/suffix on the filename, e.g. `rubble\ibeam02.nif` ↔ `rubble\c_ibeam02.nif`.
        // Bounded by same-last-directory + minimum-stem-length + max-length-delta guards
        // so we don't match e.g. requested `cap.nif` to every "*cap*.nif" candidate.
        if (best is null)
        {
            var substringHit = FindSubstringSuffixMatch(normalizedPath, requestedTokens);
            if (substringHit is not null)
            {
                return substringHit;
            }
        }

        // Final containment pass — same parent directory, one loose stem strictly contains
        // the other as a substring (both ≥ MinContainmentStem). Catches mid-stem renames
        // that substring-suffix misses because they're not anchored at either end, e.g.
        // `enclavehelmet01.nif` ↔ same-folder `helmet.nif`. Gated on uniqueness: when more
        // than one candidate in the folder qualifies, we'd be guessing — return missing
        // instead. This keeps the false-positive rate at zero on directories with multiple
        // similarly-named siblings (e.g. mccarran `_section1..4` ↔ multiple letter-suffixed
        // candidates).
        if (best is null)
        {
            var containmentHit = FindDirectoryContainmentMatch(normalizedPath, requestedTokens);
            if (containmentHit is not null)
            {
                return containmentHit;
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

    /// <summary>
    ///     Resolve an asset whose bytes must be emitted even when its source already exists
    ///     in the baseline. This is used for engine-derived plugin namespaces such as NPC
    ///     FaceGen sidecars: the vanilla bytes exist under <c>falloutnv.esm</c>, while the
    ///     runtime asks for the overriding plugin's filename and cannot use the baseline
    ///     entry in place.
    /// </summary>
    public DataFolderResolution ResolveForForcedPack(string normalizedPath)
    {
        var resolution = Resolve(normalizedPath);
        if (resolution.Kind != AssetResolutionKind.AlreadyInBaseline
            || string.IsNullOrEmpty(resolution.ResolvedPath)
            || !_baseline.TryResolveExact(resolution.ResolvedPath, out var source))
        {
            return resolution;
        }

        return resolution with
        {
            Kind = string.Equals(normalizedPath, resolution.ResolvedPath, StringComparison.OrdinalIgnoreCase)
                ? AssetResolutionKind.ResolvedExact
                : AssetResolutionKind.ResolvedFuzzy,
            Source = source,
            SourceFolderIndex = -1
        };
    }

    /// <summary>
    ///     Resolve a path using only the exact + extension-swap strategies — no fuzzy
    ///     fallback. Used for specular-companion (<c>_s</c>) lookups, where a fuzzy match to a
    ///     different sibling (e.g. the diffuse or normal map) is never correct: it yields a
    ///     wrong-resolution specular that can't be merged into the normal map's alpha. When no
    ///     exact <c>_s</c> exists, returns <see cref="AssetResolutionKind.Missing" /> so the
    ///     caller falls back to a neutral specular instead of an unrelated texture.
    /// </summary>
    public DataFolderResolution ResolveExactOnly(string normalizedPath)
    {
        return TryResolveOrderedExactStrategies(normalizedPath) ?? Miss(normalizedPath);
    }

    private DataFolderResolution? TryResolveOrderedExactStrategies(string normalizedPath)
    {
        foreach (var strategy in BuildOrderedExactStrategies())
        {
            var result = strategy.Resolve(normalizedPath);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private IEnumerable<IAssetResolutionStrategy> BuildOrderedExactStrategies()
    {
        if (_overrideBaseline)
        {
            yield return new SecondaryExactResolutionStrategy(_secondaries);
            yield return new SecondaryExtensionSwapResolutionStrategy(_secondaries);
            yield return new BaselineExactResolutionStrategy(_baseline);
            yield return new BaselineExtensionSwapResolutionStrategy(_baseline);
            yield break;
        }

        yield return new BaselineExactResolutionStrategy(_baseline);
        yield return new BaselineExtensionSwapResolutionStrategy(_baseline);
        yield return new SecondaryExactResolutionStrategy(_secondaries);
        yield return new SecondaryExtensionSwapResolutionStrategy(_secondaries);
    }

    /// <summary>
    ///     Walk every secondary folder gathering candidate sources keyed by either exact
    ///     basename (<paramref name="useLoose" /> = false) or loose basename (separator-
    ///     stripped, case-folded). Returns the candidate with the highest path-suffix
    ///     token overlap, breaking ties by folder priority (first-index wins).
    ///     For the loose variant, candidates whose extension is incompatible with the
    ///     request (e.g., a <c>.dds</c> texture when a <c>.nif</c> mesh was requested)
    ///     are filtered out — same-stem cross-format matches were producing nonsense
    ///     hits like <c>enclavehelmet01.nif → enclavehelmet01.dds</c>.
    /// </summary>
    private static FuzzyCandidate? FindBestFuzzyCandidate(
        IReadOnlyList<DataFolderIndex> folders,
        string basename,
        string[] requestedTokens,
        bool useLoose)
    {
        var key = useLoose ? AssetPathRules.ComputeLooseBasename(basename) : basename;
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        return FindBestIndexedCandidate(
            folders,
            key,
            Path.GetExtension(basename),
            requestedTokens,
            useLoose
                ? static (folder, lookupKey) => folder.EnumerateByLooseBasename(lookupKey)
                : static (folder, lookupKey) => folder.EnumerateByBasename(lookupKey),
            requireCompatibleExtension: useLoose);
    }

    /// <summary>
    ///     Look up candidates by an arbitrary pre-computed loose-basename key and pick
    ///     the one whose path suffix tokens overlap the request the most. Used for the
    ///     nv-stripped fallback where the key is computed off the request basename's
    ///     nv-affix-stripped form. Cross-class extensions are filtered out per the
    ///     existing <see cref="AssetPathRules.ExtensionsAreCompatible" /> rules.
    /// </summary>
    private static FuzzyCandidate? FindCandidateByLooseKey(
        IReadOnlyList<DataFolderIndex> folders,
        string looseKey,
        string requestedBasename,
        string[] requestedTokens)
    {
        if (string.IsNullOrEmpty(looseKey))
        {
            return null;
        }

        return FindBestIndexedCandidate(
            folders,
            looseKey,
            Path.GetExtension(requestedBasename),
            requestedTokens,
            static (folder, lookupKey) => folder.EnumerateByLooseBasename(lookupKey),
            requireCompatibleExtension: true);
    }

    private static FuzzyCandidate? FindBestIndexedCandidate(
        IReadOnlyList<DataFolderIndex> folders,
        string lookupKey,
        string requestedExtension,
        string[] requestedTokens,
        Func<DataFolderIndex, string, IEnumerable<AssetSource>> enumerateCandidates,
        bool requireCompatibleExtension)
    {
        FuzzyCandidate? best = null;
        for (var i = 0; i < folders.Count; i++)
        {
            foreach (var candidate in enumerateCandidates(folders[i], lookupKey))
            {
                if (requireCompatibleExtension &&
                    !AssetPathRules.ExtensionsAreCompatible(requestedExtension, candidate.NormalizedPath))
                {
                    continue;
                }

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

        return best;
    }

    /// <summary>
    ///     Directory-anchored substring match: look up the immediate parent folder of the
    ///     requested path across baseline + secondaries, then return any candidate whose
    ///     loose-stem ends-with or starts-with the requested loose-stem (within a tight
    ///     length budget). Designed for cases where a final-build asset stayed in the
    ///     same folder but acquired a short prefix/suffix on the basename — e.g.
    ///     <c>rubble\ibeam02.nif</c> ↔ <c>rubble\c_ibeam02.nif</c>.
    /// </summary>
    private DataFolderResolution? FindSubstringSuffixMatch(string normalizedPath, string[] requestedTokens)
    {
        const int MinStemLength = 6;
        const int MaxLengthDelta = 6;

        var basename = Path.GetFileName(normalizedPath);
        var requestedStem = AssetPathRules.ComputeLooseBasename(basename);
        if (requestedStem.Length < MinStemLength)
        {
            return null;
        }

        var requestedExt = Path.GetExtension(basename);

        // Pull the immediate parent directory (last token).
        var lastDir = requestedTokens.Length >= 2 ? requestedTokens[^2] : null;
        if (string.IsNullOrEmpty(lastDir))
        {
            return null;
        }

        var folders = new List<(DataFolderIndex Index, int FolderIndex)>(_secondaries.Count + 1);
        folders.Add((_baseline, -1));
        for (var i = 0; i < _secondaries.Count; i++)
        {
            folders.Add((_secondaries[i], i));
        }

        AssetSource? bestSource = null;
        var bestFolderIndex = int.MaxValue;
        var bestDelta = MaxLengthDelta + 1;

        foreach (var (index, folderIndex) in folders)
        {
            foreach (var candidate in index.EnumerateByLastDirectory(lastDir))
            {
                if (!AssetPathRules.ExtensionsAreCompatible(requestedExt, candidate.NormalizedPath))
                {
                    continue;
                }

                var candidateStem = AssetPathRules.ComputeLooseBasename(
                    Path.GetFileName(candidate.NormalizedPath));
                if (candidateStem.Length < MinStemLength)
                {
                    continue;
                }

                int delta;
                if (candidateStem.EndsWith(requestedStem, StringComparison.Ordinal))
                {
                    delta = candidateStem.Length - requestedStem.Length;
                }
                else if (candidateStem.StartsWith(requestedStem, StringComparison.Ordinal))
                {
                    delta = candidateStem.Length - requestedStem.Length;
                }
                else if (requestedStem.EndsWith(candidateStem, StringComparison.Ordinal))
                {
                    delta = requestedStem.Length - candidateStem.Length;
                }
                else if (requestedStem.StartsWith(candidateStem, StringComparison.Ordinal))
                {
                    delta = requestedStem.Length - candidateStem.Length;
                }
                else
                {
                    continue;
                }

                if (delta <= 0 || delta > MaxLengthDelta)
                {
                    continue;
                }

                // Prefer smaller deltas (more specific match), then earlier folder priority.
                if (delta < bestDelta ||
                    (delta == bestDelta && folderIndex < bestFolderIndex && folderIndex >= 0))
                {
                    bestSource = candidate;
                    bestFolderIndex = folderIndex;
                    bestDelta = delta;
                }
            }
        }

        if (bestSource is null)
        {
            return null;
        }

        return new DataFolderResolution
        {
            Kind = AssetResolutionKind.ResolvedFuzzy,
            Source = bestSource,
            ResolvedPath = bestSource.NormalizedPath,
            SourceFolderIndex = bestFolderIndex == int.MaxValue ? -1 : bestFolderIndex,
            FuzzySuffixTokens = TokenizePath(bestSource.NormalizedPath).Length
        };
    }

    /// <summary>
    ///     Directory-anchored containment match: when both the request and a candidate
    ///     share the immediate parent directory, and one loose stem strictly contains
    ///     the other as a substring, return the candidate. Only fires when exactly ONE
    ///     candidate in the directory qualifies, across all baseline + secondary folders —
    ///     ambiguity returns missing.
    ///     <para>
    ///         The substring-suffix pass only catches starts-with / ends-with anchored
    ///         renames within a tight length delta. This pass relaxes the anchoring (allows
    ///         mid-stem matches) but tightens the safety with strict uniqueness, so it
    ///         catches cases like <c>enclavehelmet01.nif</c> ↔ <c>helmet.nif</c> in the same
    ///         <c>enclavepowerarmor</c> folder, while declining to guess when a directory
    ///         carries many similar siblings.
    ///     </para>
    /// </summary>
    private DataFolderResolution? FindDirectoryContainmentMatch(string normalizedPath, string[] requestedTokens)
    {
        const int MinContainmentStem = 6;

        var basename = Path.GetFileName(normalizedPath);
        var requestedStem = AssetPathRules.ComputeLooseBasename(basename);
        if (requestedStem.Length < MinContainmentStem)
        {
            return null;
        }

        var requestedExt = Path.GetExtension(basename);

        var lastDir = requestedTokens.Length >= 2 ? requestedTokens[^2] : null;
        if (string.IsNullOrEmpty(lastDir))
        {
            return null;
        }

        var folders = new List<(DataFolderIndex Index, int FolderIndex)>(_secondaries.Count + 1);
        folders.Add((_baseline, -1));
        for (var i = 0; i < _secondaries.Count; i++)
        {
            folders.Add((_secondaries[i], i));
        }

        AssetSource? uniqueSource = null;
        var uniqueFolderIndex = int.MaxValue;
        var matchCount = 0;

        foreach (var (index, folderIndex) in folders)
        {
            foreach (var candidate in index.EnumerateByLastDirectory(lastDir))
            {
                if (!AssetPathRules.ExtensionsAreCompatible(requestedExt, candidate.NormalizedPath))
                {
                    continue;
                }

                var candidateStem = AssetPathRules.ComputeLooseBasename(
                    Path.GetFileName(candidate.NormalizedPath));
                if (candidateStem.Length < MinContainmentStem)
                {
                    continue;
                }

                // Skip stems we've already covered via the prior passes — pure equality
                // would have been caught by the loose-basename match, and the
                // starts-with/ends-with shapes are already in FindSubstringSuffixMatch.
                if (string.Equals(candidateStem, requestedStem, StringComparison.Ordinal))
                {
                    continue;
                }

                var isContained = requestedStem.Contains(candidateStem, StringComparison.Ordinal)
                                  || candidateStem.Contains(requestedStem, StringComparison.Ordinal);
                if (!isContained)
                {
                    continue;
                }

                matchCount++;
                if (matchCount > 1)
                {
                    return null; // ambiguous — bail rather than guess
                }

                uniqueSource = candidate;
                uniqueFolderIndex = folderIndex;
            }
        }

        if (uniqueSource is null)
        {
            return null;
        }

        return new DataFolderResolution
        {
            Kind = AssetResolutionKind.ResolvedFuzzy,
            Source = uniqueSource,
            ResolvedPath = uniqueSource.NormalizedPath,
            SourceFolderIndex = uniqueFolderIndex == int.MaxValue ? -1 : uniqueFolderIndex,
            FuzzySuffixTokens = TokenizePath(uniqueSource.NormalizedPath).Length
        };
    }

    private static DataFolderResolution Miss(string normalizedPath)
    {
        return new DataFolderResolution
        {
            Kind = AssetResolutionKind.Missing,
            ResolvedPath = normalizedPath
        };
    }

    /// <summary>
    ///     Split a normalized path on backslashes. Returns the path components from left to
    ///     right; the last token is the filename.
    /// </summary>
    private static string[] TokenizePath(string normalized)
    {
        return normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    ///     Count how many trailing tokens of <paramref name="a" /> and <paramref name="b" />
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

    private interface IAssetResolutionStrategy
    {
        /// <summary>Tries to resolve the normalized path, returning null if this strategy doesn't match it.</summary>
        DataFolderResolution? Resolve(string normalizedPath);
    }

    private sealed class BaselineExactResolutionStrategy(DataFolderIndex baseline) : IAssetResolutionStrategy
    {
        /// <summary>Resolves by exact-path match in the baseline folder (asset already present).</summary>
        public DataFolderResolution? Resolve(string normalizedPath)
        {
            return baseline.TryResolveExact(normalizedPath, out _)
                ? new DataFolderResolution
                {
                    Kind = AssetResolutionKind.AlreadyInBaseline,
                    ResolvedPath = normalizedPath
                }
                : null;
        }
    }

    private sealed class BaselineExtensionSwapResolutionStrategy(DataFolderIndex baseline) : IAssetResolutionStrategy
    {
        /// <summary>Resolves by matching an interchangeable-extension variant of the path in the baseline folder.</summary>
        public DataFolderResolution? Resolve(string normalizedPath)
        {
            foreach (var swapped in AssetPathRules.EnumerateExtensionSwaps(normalizedPath))
            {
                if (baseline.TryResolveExact(swapped, out _))
                {
                    return new DataFolderResolution
                    {
                        Kind = AssetResolutionKind.AlreadyInBaseline,
                        ResolvedPath = swapped
                    };
                }
            }

            return null;
        }
    }

    private sealed class SecondaryExactResolutionStrategy(IReadOnlyList<DataFolderIndex> secondaries)
        : IAssetResolutionStrategy
    {
        /// <summary>Resolves by exact-path match in the ordered secondary (fallback) folders.</summary>
        public DataFolderResolution? Resolve(string normalizedPath)
        {
            for (var i = 0; i < secondaries.Count; i++)
            {
                if (secondaries[i].TryResolveExact(normalizedPath, out var source))
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

            return null;
        }
    }

    private sealed class SecondaryExtensionSwapResolutionStrategy(IReadOnlyList<DataFolderIndex> secondaries)
        : IAssetResolutionStrategy
    {
        /// <summary>Resolves by matching an interchangeable-extension variant of the path in the secondary folders.</summary>
        public DataFolderResolution? Resolve(string normalizedPath)
        {
            for (var i = 0; i < secondaries.Count; i++)
            {
                foreach (var swapped in AssetPathRules.EnumerateExtensionSwaps(normalizedPath))
                {
                    if (secondaries[i].TryResolveExact(swapped, out var source))
                    {
                        return new DataFolderResolution
                        {
                            Kind = AssetResolutionKind.ResolvedFuzzy,
                            Source = source,
                            ResolvedPath = swapped,
                            SourceFolderIndex = i,
                            FuzzySuffixTokens = TokenizePath(swapped).Length
                        };
                    }
                }
            }

            return null;
        }
    }

    private sealed record FuzzyCandidate
    {
        public required AssetSource Source { get; init; }
        public required int FolderIndex { get; init; }
        public required int SuffixScore { get; init; }
    }
}
