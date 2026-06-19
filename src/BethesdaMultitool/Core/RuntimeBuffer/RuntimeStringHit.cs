using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>A string found in a memory dump along with its ownership status and the record/struct resolved as its owner.</summary>
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
