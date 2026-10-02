namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>A retained attribution and its actual pointer evidence; no pointer is invented for text matches.</summary>
public sealed record RuntimeStringOwnershipCandidate(
    string OwnerKind, string OwnerName, uint? OwnerFormId, long? OwnerFileOffset,
    string? OwnerRecordType, string? OwnerFieldOrSubrecord, ClaimSource ClaimSource,
    long? ReferrerVa, long? ReferrerFileOffset, string? Validation)
{
    public OwnershipConfidence Confidence => !ReferrerVa.HasValue && ClaimSource == ClaimSource.RuntimeEditorId
        ? OwnershipConfidence.Positional
        : !ReferrerVa.HasValue && ClaimSource == ClaimSource.RuntimeStructField
            ? OwnershipConfidence.OwnerNamed : OwnershipConfidenceMap.For(ClaimSource);

    public bool EstablishesOwnership => ClaimSource switch
    {
        ClaimSource.TextContentMatch or ClaimSource.SecondPassCFormEditorIdPosition => false,
        ClaimSource.RuntimeEditorId or ClaimSource.RuntimeStructField => ReferrerVa.HasValue,
        _ => true
    };

    internal static RuntimeStringOwnershipCandidate FromClaim(RuntimeStringOwnershipClaim claim) =>
        new(claim.OwnerKind, claim.OwnerName, claim.OwnerFormId, claim.OwnerFileOffset,
            claim.OwnerRecordType, claim.OwnerFieldOrSubrecord, claim.ClaimSource,
            claim.ReferrerVa, claim.ReferrerFileOffset, claim.Validation);
}
