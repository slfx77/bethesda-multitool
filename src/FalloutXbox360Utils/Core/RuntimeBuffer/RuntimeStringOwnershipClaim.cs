namespace FalloutXbox360Utils.Core.RuntimeBuffer;

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
