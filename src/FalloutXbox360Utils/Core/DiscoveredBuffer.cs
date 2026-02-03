namespace FalloutXbox360Utils.Core;

public sealed class DiscoveredBuffer
{
    public long FileOffset { get; init; }
    public long? VirtualAddress { get; init; }
    public string FormatType { get; init; } = "";
    public string Details { get; init; } = "";
    public long EstimatedSize { get; init; }
}