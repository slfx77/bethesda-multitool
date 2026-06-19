namespace BethesdaMultitool.Core.Coverage;

/// <summary>An unrecognized span of a memory dump that falls between recognized intervals, with its classification.</summary>
public sealed class CoverageGap
{
    public long FileOffset { get; init; }
    public long Size { get; init; }
    public long? VirtualAddress { get; set; }
    public GapClassification Classification { get; set; }
    public string Context { get; set; } = "";
}
