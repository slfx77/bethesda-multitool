namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>A claim that a given runtime string is owned by a specific record/struct, recording the owner identity and how it was attributed.</summary>
internal sealed record RuntimeStringOwnershipClaim(
    long StringFileOffset,
    long? StringVirtualAddress,
    string OwnerKind,
    string OwnerName,
    uint? OwnerFormId,
    long? OwnerFileOffset,
    ClaimSource ClaimSource = ClaimSource.ManagerGlobal,
    string? OwnerRecordType = null,
    string? OwnerFieldOrSubrecord = null);
