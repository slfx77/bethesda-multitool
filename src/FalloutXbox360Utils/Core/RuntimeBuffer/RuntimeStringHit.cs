using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

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
