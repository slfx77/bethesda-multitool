namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>A runtime buffer found during exploration, with its location, inferred format, and estimated size.</summary>
public sealed class DiscoveredBuffer
{
    public long FileOffset { get; init; }
    public long? VirtualAddress { get; init; }
    public string FormatType { get; init; } = "";
    public string Details { get; init; } = "";
    public long EstimatedSize { get; init; }
}
