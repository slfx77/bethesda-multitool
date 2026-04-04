namespace FalloutXbox360Utils.Core.RuntimeBuffer;

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
}
