namespace FalloutXbox360Utils.Core.Coverage;

public sealed class CoverageGap
{
    public long FileOffset { get; init; }
    public long Size { get; init; }
    public long? VirtualAddress { get; set; }
    public GapClassification Classification { get; set; }
    public string Context { get; set; } = "";
}
