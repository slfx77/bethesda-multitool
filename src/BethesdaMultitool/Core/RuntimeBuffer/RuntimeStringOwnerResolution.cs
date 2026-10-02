namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     The resolved owner of a runtime string: owning record kind/name/FormID, the referrer that points to it, and
///     how the claim was made.
/// </summary>
public sealed class RuntimeStringOwnerResolution
{
    public string? OwnerKind { get; init; }
    public string? OwnerName { get; init; }
    public uint? OwnerFormId { get; init; }
    public long? OwnerFileOffset { get; init; }
    public long? ReferrerVa { get; init; }
    public long? ReferrerFileOffset { get; init; }
    public string? ReferrerContext { get; init; }
    public ClaimSource? ClaimSource { get; init; }
    public string? OwnerRecordType { get; init; }
    public string? OwnerFieldOrSubrecord { get; init; }
    public IReadOnlyList<(long FileOffset, long Va, string? Context)>? AllReferrers { get; init; }
    public IReadOnlyList<RuntimeStringOwnershipCandidate> Candidates { get; init; } = [];
    public bool HasAmbiguousOwners { get; init; }
    public bool HasValidatedOwner { get; init; }

    internal static RuntimeStringOwnerResolution FromClaims(IEnumerable<RuntimeStringOwnershipClaim> claims,
        IReadOnlyList<(long FileOffset, long Va, string? Context)>? referrers)
    {
        var candidates = claims.Select(RuntimeStringOwnershipCandidate.FromClaim).Distinct().ToArray();
        var validated = candidates.Where(c => c.EstablishesOwnership).ToArray();
        var selection = validated.Length > 0 ? validated : candidates;
        var ambiguous = selection.Select(c => (FormId: c.OwnerFileOffset.HasValue ? null : c.OwnerFormId, c.OwnerFileOffset,
                Name: c.OwnerFormId.HasValue || c.OwnerFileOffset.HasValue ? null : c.OwnerName))
            .Distinct().Skip(1).Any();
        var best = ambiguous ? null : validated.OrderBy(c => c.Confidence).FirstOrDefault();
        return new RuntimeStringOwnerResolution
        {
            OwnerKind = best?.OwnerKind, OwnerName = best?.OwnerName, OwnerFormId = best?.OwnerFormId,
            OwnerFileOffset = best?.OwnerFileOffset, OwnerRecordType = best?.OwnerRecordType,
            OwnerFieldOrSubrecord = best?.OwnerFieldOrSubrecord, ClaimSource = best?.ClaimSource,
            ReferrerVa = best?.ReferrerVa, ReferrerFileOffset = best?.ReferrerFileOffset,
            ReferrerContext = best?.ReferrerFileOffset is { } pointerOffset
                ? referrers?.FirstOrDefault(r => r.FileOffset == pointerOffset).Context : null,
            AllReferrers = referrers, Candidates = candidates, HasAmbiguousOwners = ambiguous,
            HasValidatedOwner = best != null
        };
    }

    /// <summary>
    ///     How much this claim can be trusted, from <see cref="ClaimSource" />. Derived rather than
    ///     stored so it can never disagree with the source it describes, and so every construction
    ///     site gets it without having to remember to set it.
    /// </summary>
    public OwnershipConfidence? Confidence =>
        ClaimSource.HasValue ? OwnershipConfidenceMap.For(ClaimSource.Value) : null;
}
