using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

public enum RuntimeStringOwnershipStatus
{
    Owned,
    ReferencedOwnerUnknown,
    Unreferenced
}

public sealed class RuntimeStringOwnerResolution
{
    public string? OwnerKind { get; init; }
    public string? OwnerName { get; init; }
    public uint? OwnerFormId { get; init; }
    public long? OwnerFileOffset { get; init; }
    public long? ReferrerVa { get; init; }
    public long? ReferrerFileOffset { get; init; }
    public string? ReferrerContext { get; init; }
}

public sealed class RuntimeStringHit
{
    public required string Text { get; init; }
    public StringCategory Category { get; init; }
    public GapClassification GapClassification { get; init; }
    public long FileOffset { get; init; }
    public long? VirtualAddress { get; init; }
    public int Length { get; init; }

    public RuntimeStringOwnershipStatus OwnershipStatus { get; set; }
    public int InboundPointerCount { get; set; }
    public RuntimeStringOwnerResolution? OwnerResolution { get; set; }

    public bool IsMeaningfulCategory => Category is not StringCategory.Other;
}

public sealed class RuntimeStringOwnershipAnalysis
{
    public List<RuntimeStringHit> AllHits { get; } = [];
    public List<RuntimeStringHit> OwnedHits { get; } = [];
    public List<RuntimeStringHit> ReferencedOwnerUnknownHits { get; } = [];
    public List<RuntimeStringHit> UnreferencedHits { get; } = [];
    public Dictionary<StringCategory, int> CategoryCounts { get; } = [];
    public Dictionary<RuntimeStringOwnershipStatus, int> StatusCounts { get; } = [];
}

internal sealed record RuntimeDecodedString(
    string Text,
    long FileOffset,
    long? VirtualAddress,
    int Length,
    StringCategory Category);

internal sealed record RuntimeStringOwnershipClaim(
    long StringFileOffset,
    long? StringVirtualAddress,
    string OwnerKind,
    string OwnerName,
    uint? OwnerFormId,
    long? OwnerFileOffset);

internal sealed record RuntimeStringReportData(
    StringPoolSummary StringPool,
    RuntimeStringOwnershipAnalysis OwnershipAnalysis);
